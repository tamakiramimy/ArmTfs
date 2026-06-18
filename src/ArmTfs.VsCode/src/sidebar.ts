import { existsSync } from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { ArmTfsCliClient, ArmTfsCliError } from './armTfsCliClient';
import { reportCommandOutput } from './commandOutput';
import type { BranchRef, ChangesetShowResponse, HistoryItem, MergeBaseResponse, MergeCandidateResponse, MergeExecuteResponse, ServerItemEntry, StatusResponse } from './contracts';
import type { ArmTfsHistoryBrowser } from './historyBrowser';
import { t, translateCliMessage } from './i18n';
import { ArmTfsMergeWorkbench } from './mergeWorkbench';
import { checkoutServerPathToLocalFolder } from './serverPathCheckout';
import { computeLocalPathForServerPath, discoverTfvcMappingForPath, findTfvcWorkspaceRoot, findTfvcWorkspaceRootSync, getCommandCwd, isPathWithin } from './tfvcContext';
import { openServerVersionDiff } from './versionedFiles';

const MERGE_SOURCE_KEY = 'armTfs.merge.sourcePath';
const MERGE_TARGET_KEY = 'armTfs.merge.targetPath';

abstract class ArmTfsTreeNode extends vscode.TreeItem {
  children?: ArmTfsTreeNode[];
}

class InfoNode extends ArmTfsTreeNode {
  constructor(label: string, description?: string, command?: vscode.Command) {
    super(label, vscode.TreeItemCollapsibleState.None);
    this.description = description;
    this.command = command;
    this.contextValue = 'armTfsInfo';
  }
}

class SectionNode extends ArmTfsTreeNode {
  constructor(label: string, description: string | undefined, children: ArmTfsTreeNode[]) {
    super(label, children.length ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.None);
    this.description = description;
    this.children = children;
    this.contextValue = 'armTfsSection';
    this.iconPath = new vscode.ThemeIcon('list-tree');
  }
}

class BranchNode extends ArmTfsTreeNode {
  constructor(public readonly branch: BranchRef) {
    super(path.posix.basename(branch.path), vscode.TreeItemCollapsibleState.Collapsed);
    this.id = `branch:${branch.path}`;
    this.description = branch.path;
    this.tooltip = [branch.path, branch.description, branch.owner?.displayName].filter(Boolean).join('\n');
    this.iconPath = new vscode.ThemeIcon('git-branch');
    this.contextValue = 'armTfsSidebarBranch';
    this.command = {
      command: 'armTfs.sidebar.selectBranch',
      title: t('sidebar.selectBranch'),
      arguments: [this],
    };
  }
}

class HistoryNode extends ArmTfsTreeNode {
  constructor(public readonly item: HistoryItem) {
    super(`cs${item.changesetId}`, vscode.TreeItemCollapsibleState.None);
    this.id = `history:${item.changesetId}`;
    this.description = buildHistoryDescription(item);
    this.tooltip = [
      t('sidebar.history.tooltip.changeset', { changeset: item.changesetId }),
      t('sidebar.history.tooltip.created', { createdAt: item.createdAt }),
      item.author?.displayName ? t('sidebar.history.tooltip.author', { author: item.author.displayName }) : undefined,
      item.comment,
    ].filter(Boolean).join('\n');
    this.iconPath = new vscode.ThemeIcon('history');
    this.contextValue = 'armTfsSidebarChangeset';
    this.command = {
      command: 'armTfs.history.openChangeset',
      title: t('sidebar.showChangesetJson'),
      arguments: [this],
    };
  }
}

class MergeConfigNode extends ArmTfsTreeNode {
  constructor(id: string, label: string, description: string | undefined, command: vscode.Command) {
    super(label, vscode.TreeItemCollapsibleState.None);
    this.id = id;
    this.description = description;
    this.command = command;
    this.contextValue = 'armTfsMergeConfig';
    this.iconPath = new vscode.ThemeIcon('gear');
  }
}

class MergeTargetOptionNode extends ArmTfsTreeNode {
  constructor(public readonly targetPath: string, active: boolean) {
    super(path.posix.basename(targetPath), vscode.TreeItemCollapsibleState.None);
    this.id = `merge:target:${targetPath}`;
    this.description = active ? t('sidebar.currentTarget') : targetPath;
    this.tooltip = targetPath;
    this.contextValue = 'armTfsMergeTargetOption';
    this.iconPath = new vscode.ThemeIcon(active ? 'check' : 'git-branch');
    this.command = {
      command: 'armTfs.sidebar.useMergeTarget',
      title: t('sidebar.setMergeTarget'),
      arguments: [targetPath],
    };
  }
}

class MergeCandidateNode extends ArmTfsTreeNode {
  constructor(
    public readonly sourcePath: string,
    public readonly targetPath: string,
    public readonly changesetId: number,
    description: string,
    tooltip: string,
  ) {
    super(`cs${changesetId}`, vscode.TreeItemCollapsibleState.None);
    this.id = `merge:candidate:${sourcePath}:${targetPath}:${changesetId}`;
    this.description = description;
    this.tooltip = tooltip;
    this.iconPath = new vscode.ThemeIcon('combine');
    this.contextValue = 'armTfsSidebarMergeCandidate';
    this.command = {
      command: 'armTfs.showChangeset',
      title: t('sidebar.showChangesetJson'),
      arguments: [{ changesetId }],
    };
  }
}

class StaticTreeProvider implements vscode.TreeDataProvider<ArmTfsTreeNode> {
  private readonly onDidChangeTreeDataEmitter = new vscode.EventEmitter<ArmTfsTreeNode | undefined | null | void>();
  private rootItems: ArmTfsTreeNode[] = [];

  readonly onDidChangeTreeData = this.onDidChangeTreeDataEmitter.event;

  setItems(items: ArmTfsTreeNode[]): void {
    this.rootItems = items;
    this.onDidChangeTreeDataEmitter.fire();
  }

  getTreeItem(element: ArmTfsTreeNode): vscode.TreeItem {
    return element;
  }

  getChildren(element?: ArmTfsTreeNode): vscode.ProviderResult<ArmTfsTreeNode[]> {
    return element?.children ?? this.rootItems;
  }
}

export class ArmTfsSidebarController implements vscode.Disposable {
  private readonly branchProvider = new StaticTreeProvider();
  private readonly historyProvider = new StaticTreeProvider();
  private readonly mergeProvider = new StaticTreeProvider();
  private readonly disposables: vscode.Disposable[] = [];
  private readonly branchView: vscode.TreeView<ArmTfsTreeNode>;
  private readonly historyView: vscode.TreeView<ArmTfsTreeNode>;
  private readonly mergeView: vscode.TreeView<ArmTfsTreeNode>;
  private readonly autoCheckoutInFlight = new Set<string>();
  private readonly autoCheckoutSkipped = new Set<string>();
  private resolvedWorkspaceRoot: string | undefined;
  private workspaceStatus: StatusResponse | undefined;
  private activeServerPath: string | undefined;
  private activeBranchPath: string | undefined;
  private selectedCompareChangesetId: number | undefined;

  constructor(
    private readonly client: ArmTfsCliClient,
    private readonly output: vscode.OutputChannel,
    private readonly rootPath: string | undefined,
    private readonly refreshScm: () => Promise<void>,
    private readonly historyBrowser: ArmTfsHistoryBrowser,
  ) {
    this.branchView = vscode.window.createTreeView('armTfs.branches', {
      treeDataProvider: this.branchProvider,
      canSelectMany: true,
    });
    this.historyView = vscode.window.createTreeView('armTfs.historyGraph', {
      treeDataProvider: this.historyProvider,
    });
    this.mergeView = vscode.window.createTreeView('armTfs.merge', {
      treeDataProvider: this.mergeProvider,
    });
    this.refreshLabels();

    this.disposables.push(
      this.branchView,
      this.historyView,
      this.mergeView,
      this.branchView.onDidChangeSelection((event) => {
        const branches = event.selection.filter((item): item is BranchNode => item instanceof BranchNode);
        if (branches.length === 2) {
          void this.historyBrowser.openTargets(branches.map((branch) => ({
            label: path.posix.basename(branch.branch.path),
            serverPath: branch.branch.path,
          })));
          return;
        }
        const selected = event.selection[0];
        if (selected instanceof BranchNode) {
          void this.setActiveServerPath(selected.branch.path, {
            syncBranchScope: false,
            syncMergeSource: true,
          });
        }
      }),
      vscode.commands.registerCommand('armTfs.sidebar.refresh', async () => this.refreshAll()),
      vscode.commands.registerCommand('armTfs.sidebar.selectBranch', async (node?: BranchNode) => {
        if (node) {
          await this.setActiveServerPath(node.branch.path, {
            syncBranchScope: false,
            syncMergeSource: true,
          });
        }
      }),
      vscode.commands.registerCommand('armTfs.sidebar.useMergeTarget', async (targetPath: string) => this.useMergeTarget(targetPath)),
      vscode.commands.registerCommand('armTfs.sidebar.setBranchScope', async (node?: BranchNode | ServerExplorerNode) => this.setBranchScope(getNodeServerPath(node))),
      vscode.commands.registerCommand('armTfs.sidebar.setMergeSource', async (node?: BranchNode | ServerExplorerNode) => this.setMergePath(MERGE_SOURCE_KEY, t('sidebar.prompt.mergeSourcePath'), getNodeServerPath(node))),
      vscode.commands.registerCommand('armTfs.sidebar.setMergeTarget', async (node?: BranchNode | ServerExplorerNode) => this.setMergePath(MERGE_TARGET_KEY, t('sidebar.prompt.mergeTargetPath'), getNodeServerPath(node))),
      vscode.commands.registerCommand('armTfs.sidebar.copyTfvcPath', async (node?: BranchNode | MergeTargetOptionNode | string) => this.copyTfvcPath(node)),
      vscode.commands.registerCommand('armTfs.sidebar.pullBranch', async (node?: BranchNode | ServerExplorerNode) => this.pullBranch(node)),
      vscode.commands.registerCommand('armTfs.sidebar.checkoutBranch', async (node?: BranchNode | ServerExplorerNode) => this.checkoutBranch(node)),
      vscode.commands.registerCommand('armTfs.sidebar.showBranchHistory', async (node?: BranchNode | ServerExplorerNode) => this.showBranchHistory(node)),
      vscode.commands.registerCommand('armTfs.sidebar.mergeFromBranch', async (node?: BranchNode | ServerExplorerNode) => this.mergeFromBranch(node)),
      vscode.commands.registerCommand('armTfs.sidebar.addBranch', async (node?: BranchNode | ServerExplorerNode) => this.addBranch(node)),
      vscode.commands.registerCommand('armTfs.history.diffPrevious', async (node?: HistoryNode) => this.diffHistoryNodeWithPrevious(node)),
      vscode.commands.registerCommand('armTfs.history.selectForCompare', async (node?: HistoryNode) => this.selectChangesetForCompare(node)),
      vscode.commands.registerCommand('armTfs.history.compareSelected', async (node?: HistoryNode) => this.compareWithSelectedChangeset(node)),
      vscode.commands.registerCommand('armTfs.history.openChangeset', async (node?: HistoryNode) => {
        if (node) {
          await this.historyBrowser.open(await this.getPreferredHistoryPath(), node.item.changesetId);
        }
      }),
      vscode.commands.registerCommand('armTfs.history.openBrowser', async () => {
        await this.historyBrowser.open(await this.getPreferredHistoryPath());
      }),
      vscode.commands.registerCommand('armTfs.sidebar.swapMergePaths', async () => this.swapMergePaths()),
      vscode.commands.registerCommand('armTfs.sidebar.executeMergeCandidate', async (node?: MergeCandidateNode) => this.executeMergeCandidate(node)),
      vscode.workspace.onDidCloseTextDocument((document) => {
        if (document.uri.scheme === 'file') {
          this.autoCheckoutSkipped.delete(document.uri.fsPath);
        }
      }),
    );
  }

  dispose(): void {
    vscode.Disposable.from(...this.disposables).dispose();
  }

  async initialize(): Promise<void> {
    await this.refreshWorkspaceStatus();
    const mappedServerPath = this.getCurrentWorkspaceMappingPath();
    this.activeServerPath = mappedServerPath ?? this.getConfiguredServerExplorerRoot();
    this.activeBranchPath = this.activeServerPath;
    if (mappedServerPath) {
      await this.persistActiveServerPath(mappedServerPath, {
        syncBranchScope: true,
        syncMergeSource: false,
      });
    }
    await this.refreshAll();
  }

  refreshLabels(): void {
    this.branchView.title = t('view.branches');
    this.historyView.title = t('view.historyGraph');
    this.mergeView.title = t('view.merge');
  }

  async setActiveServerPath(
    serverPath: string,
    options: { branchContext?: boolean; syncBranchScope?: boolean; refresh?: boolean; syncMergeSource?: boolean } = {},
  ): Promise<void> {
    const normalized = cleanServerPath(serverPath);
    if (!normalized.startsWith('$/')) {
      return;
    }

    const changed = !this.activeServerPath || normalizeServerPath(this.activeServerPath) !== normalizeServerPath(normalized);
    this.activeServerPath = normalized;
    const shouldUpdateBranchContext = options.branchContext !== false;

    if (shouldUpdateBranchContext) {
      this.activeBranchPath = normalized;
    }

    if (shouldUpdateBranchContext && (options.syncBranchScope !== false || options.syncMergeSource !== false)) {
      await this.persistActiveServerPath(normalized, {
        syncBranchScope: options.syncBranchScope !== false,
        syncMergeSource: options.syncMergeSource !== false,
      });
    }

    if (changed || options.refresh !== false) {
      if (shouldUpdateBranchContext) {
        await Promise.all([this.refreshBranches(), this.refreshHistory(), this.refreshMerge()]);
      } else {
        await this.refreshHistory();
      }
    }
  }

  async refreshAll(): Promise<void> {
    await this.refreshWorkspaceStatus();
    await Promise.all([this.refreshBranches(), this.refreshHistory(), this.refreshMerge()]);
  }

  async refreshHistory(): Promise<void> {
    const activeServerPath = this.getActiveServerPath();
    if (activeServerPath) {
      await this.refreshServerHistory(activeServerPath);
      return;
    }

    const workspaceRoot = this.resolvedWorkspaceRoot ?? await findTfvcWorkspaceRoot(this.rootPath);
    if (!workspaceRoot) {
      this.historyProvider.setItems([new InfoNode(t('sidebar.noWorkspaceFolder'), t('sidebar.noWorkspaceFolder.desc'))]);
      return;
    }

    const targetPath = this.getHistoryTargetPath(workspaceRoot);
    const top = vscode.workspace.getConfiguration('armTfs').get<number>('history.top', 30);

    try {
      const response = await this.client.history(targetPath, top, undefined, { cwdOverride: getCommandCwd(workspaceRoot, targetPath) });
      const nodes: ArmTfsTreeNode[] = [
        new InfoNode(t('sidebar.target'), targetPath),
        new InfoNode(t('sidebar.loadedChangesets'), `${response.items.length}`),
        ...response.items.map((item) => new HistoryNode(item)),
      ];
      this.historyProvider.setItems(nodes);
    } catch (error) {
      const msg = getErrorMessage(error);
      const isNotFound = msg.includes('ENOENT');
      this.historyProvider.setItems([new InfoNode(
        isNotFound ? t('sidebar.cliNotFound') : t('sidebar.historyUnavailable'),
        isNotFound ? t('sidebar.cliNotFound.desc') : msg,
        {
          command: isNotFound ? 'armTfs.configureCliCommand' : 'armTfs.sidebar.refresh',
          title: isNotFound ? t('sidebar.configureCli') : t('sidebar.refreshViews'),
        },
      )]);
      if (!isNotFound) {
        this.showError('arm-tfs history', error);
      }
    }
  }

  private async refreshServerHistory(targetPath: string): Promise<void> {
    const top = vscode.workspace.getConfiguration('armTfs').get<number>('history.top', 30);

    try {
      const response = await this.client.history(targetPath, top);
      const nodes: ArmTfsTreeNode[] = [
        new InfoNode(t('sidebar.target'), targetPath),
        new InfoNode(t('sidebar.loadedChangesets'), `${response.items.length}`),
        ...response.items.map((item) => new HistoryNode(item)),
      ];
      this.historyProvider.setItems(nodes);
    } catch (error) {
      const msg = getErrorMessage(error);
      const isNotFound = msg.includes('ENOENT');
      this.historyProvider.setItems([new InfoNode(
        isNotFound ? t('sidebar.cliNotFound') : t('sidebar.historyUnavailable'),
        isNotFound ? t('sidebar.cliNotFound.desc') : msg,
        {
          command: isNotFound ? 'armTfs.configureCliCommand' : 'armTfs.sidebar.refresh',
          title: isNotFound ? t('sidebar.configureCli') : t('sidebar.refreshViews'),
        },
      )]);
      if (!isNotFound) {
        this.showError('arm-tfs history', error);
      }
    }
  }

  async refreshBranches(): Promise<void> {
    const scope = this.getBranchScope();
    if (!scope) {
      this.branchProvider.setItems([new InfoNode(t('sidebar.branchScopeUnavailable'), t('sidebar.branchScopeUnavailable.desc'), {
        command: 'armTfs.sidebar.setBranchScope',
        title: t('sidebar.setBranchScope'),
      })]);
      return;
    }

    try {
      const response = await this.client.branchList(scope);
      if (!response.items.length) {
        this.branchProvider.setItems([new InfoNode(t('sidebar.noBranchesFound'), scope)]);
        return;
      }

      const roots = buildBranchTree(response.items);
      const scopeNode = new InfoNode(t('sidebar.scope'), scope);
      scopeNode.iconPath = new vscode.ThemeIcon('repo');
      this.branchProvider.setItems([scopeNode, ...roots]);
    } catch (error) {
      const msg = getErrorMessage(error);
      const isNotFound = msg.includes('ENOENT');
      this.branchProvider.setItems([new InfoNode(
        isNotFound ? t('sidebar.cliNotFound') : t('sidebar.branchesUnavailable'),
        isNotFound ? t('sidebar.cliNotFound.desc') : msg,
        {
          command: isNotFound ? 'armTfs.configureCliCommand' : 'armTfs.sidebar.refresh',
          title: isNotFound ? t('sidebar.configureCli') : t('sidebar.refreshViews'),
        },
      )]);
      if (!isNotFound) {
        this.showError('arm-tfs branch list', error);
      }
    }
  }

  async refreshMerge(): Promise<void> {
    const sourcePath = this.getMergeSourcePath();
    const targetPath = await this.getMergeTargetPath(sourcePath);
    const configNodes: ArmTfsTreeNode[] = [
      new MergeConfigNode(
        'merge:source',
        t('sidebar.mergeSource'),
        sourcePath ?? t('sidebar.pickSourceAndTarget'),
        { command: 'armTfs.sidebar.setMergeSource', title: t('sidebar.setMergeSource') },
      ),
      new MergeConfigNode(
        'merge:target',
        t('sidebar.mergeTarget'),
        targetPath ?? t('sidebar.pickTargetBranch'),
        { command: 'armTfs.sidebar.setMergeTarget', title: t('sidebar.setMergeTarget') },
      ),
      new MergeConfigNode(
        'merge:swap',
        t('sidebar.swapMergePaths'),
        t('sidebar.swapMergePaths.desc'),
        { command: 'armTfs.sidebar.swapMergePaths', title: t('sidebar.swapMergePaths.title') },
      ),
    ];

    const sections: ArmTfsTreeNode[] = [
      new SectionNode(t('sidebar.mergeQuery'), undefined, configNodes),
    ];

    if (!sourcePath) {
      sections.push(new InfoNode(t('sidebar.pickSourceAndTarget'), undefined, {
        command: 'armTfs.sidebar.setMergeSource',
        title: t('sidebar.setMergeSource'),
      }));
      this.mergeProvider.setItems(sections);
      return;
    }

    const targetOptions = await this.getMergeTargetBranchPaths(sourcePath);
    if (targetOptions.length) {
      sections.push(new SectionNode(
        t('sidebar.targetBranches'),
        undefined,
        targetOptions.map((candidate) => new MergeTargetOptionNode(candidate, targetPath ? normalizeServerPath(candidate) === normalizeServerPath(targetPath) : false)),
      ));
    }

    if (!targetPath) {
      sections.push(new InfoNode(t('sidebar.pickTargetBranch'), undefined, {
        command: 'armTfs.sidebar.setMergeTarget',
        title: t('sidebar.setMergeTarget'),
      }));
      this.mergeProvider.setItems(sections);
      return;
    }

    try {
      const top = vscode.workspace.getConfiguration('armTfs').get<number>('merge.candidateTop', 20);
      const scan = vscode.workspace.getConfiguration('armTfs').get<number>('merge.candidateScan', 80);
      const [mergeBase, candidates] = await Promise.all([
        this.client.mergeBase(sourcePath, targetPath),
        this.client.mergeCandidates(sourcePath, targetPath, top, scan),
      ]);
      this.mergeProvider.setItems([
        ...sections,
        new SectionNode(t('sidebar.mergeSummary'), undefined, buildMergeSummaryNodes(mergeBase, candidates)),
      ]);
    } catch (error) {
      const msg = getErrorMessage(error);
      const isNotFound = msg.includes('ENOENT');
      this.mergeProvider.setItems([
        ...sections,
        new InfoNode(
          isNotFound ? t('sidebar.cliNotFound') : t('sidebar.mergeQueryUnavailable'),
          isNotFound ? t('sidebar.cliNotFound.desc') : msg,
          {
            command: isNotFound ? 'armTfs.configureCliCommand' : 'armTfs.sidebar.refresh',
            title: isNotFound ? t('sidebar.configureCli') : t('sidebar.refreshViews'),
          },
        ),
      ]);
      if (!isNotFound) {
        this.showError('arm-tfs merge', error);
      }
    }
  }

  async handleActiveEditorChanged(): Promise<void> {
    await this.refreshHistory();
  }

  async handleTextChanged(document: vscode.TextDocument, hadContentChanges: boolean): Promise<void> {
    if (!hadContentChanges) {
      return;
    }

    const mode = vscode.workspace.getConfiguration('armTfs').get<string>('autoCheckout.mode', 'firstEdit');
    if (mode !== 'firstEdit') {
      return;
    }

    await this.maybeAutoCheckout(document, 'firstEdit');
  }

  async handleWillSave(document: vscode.TextDocument): Promise<readonly vscode.TextEdit[]> {
    const mode = vscode.workspace.getConfiguration('armTfs').get<string>('autoCheckout.mode', 'firstEdit');
    if (mode !== 'onSave') {
      return [];
    }

    await this.maybeAutoCheckout(document, 'onSave');
    return [];
  }

  private async maybeAutoCheckout(document: vscode.TextDocument, reason: 'firstEdit' | 'onSave'): Promise<void> {
    if (document.uri.scheme !== 'file' || document.isUntitled) {
      return;
    }

    const filePath = document.uri.fsPath;
    const workspaceRoot = findTfvcWorkspaceRootSync(filePath) ?? this.resolvedWorkspaceRoot;
    if (!workspaceRoot || !isPathWithin(workspaceRoot, filePath)) {
      return;
    }

    if (this.autoCheckoutInFlight.has(filePath) || this.autoCheckoutSkipped.has(filePath)) {
      return;
    }

    this.autoCheckoutInFlight.add(filePath);
    try {
      const status = await this.client.status(filePath, true, { cwdOverride: getCommandCwd(workspaceRoot, filePath) });
      const item = status.items.find((entry) => samePath(entry.localPath, filePath));
      if (item?.state !== 'modifiedNotCheckedOut') {
        return;
      }

      const requireConfirm = vscode.workspace.getConfiguration('armTfs').get<boolean>('autoCheckout.confirm', false);
      if (requireConfirm) {
        const action = await vscode.window.showInformationMessage(
          t('sidebar.autoCheckoutPrompt', { file: path.basename(filePath) }),
          { modal: false },
          t('sidebar.action.checkout'),
          t('sidebar.action.skipOnce'),
        );

        if (action !== t('sidebar.action.checkout')) {
          this.autoCheckoutSkipped.add(filePath);
          return;
        }
      }

      await this.client.checkout([filePath], false, { cwdOverride: getCommandCwd(workspaceRoot, filePath) });
      this.output.appendLine(`Auto checkout (${reason}): ${filePath}`);
      await this.refreshScm();
      await this.refreshAll();
      vscode.window.setStatusBarMessage(t('sidebar.autoCheckoutStatus', { file: path.basename(filePath) }), 2500);
    } catch (error) {
      this.showError('arm-tfs auto checkout', error);
    } finally {
      this.autoCheckoutInFlight.delete(filePath);
    }
  }

  private async refreshWorkspaceStatus(): Promise<void> {
    const workspaceRoot = await findTfvcWorkspaceRoot(this.rootPath);
    this.resolvedWorkspaceRoot = workspaceRoot;
    if (!workspaceRoot) {
      this.workspaceStatus = undefined;
      return;
    }

    try {
      this.workspaceStatus = await this.client.status(workspaceRoot, false, { cwdOverride: workspaceRoot });
    } catch (error) {
      this.workspaceStatus = undefined;
      this.showError('arm-tfs status', error);
    }
  }

  private getHistoryTargetPath(workspaceRoot: string): string {
    const activeUri = vscode.window.activeTextEditor?.document.uri;
    if (activeUri?.scheme === 'file' && isPathWithin(workspaceRoot, activeUri.fsPath)) {
      return activeUri.fsPath;
    }

    return workspaceRoot;
  }

  private getBranchScope(): string | undefined {
    const activeScope = this.getActiveBranchScope();
    if (activeScope) {
      return activeScope;
    }

    const discovered = this.getCurrentWorkspaceMappingPath();
    if (discovered) {
      return discovered;
    }

    const configuredScope = vscode.workspace.getConfiguration('armTfs').get<string>('branch.scope')?.trim();
    if (configuredScope) {
      return cleanServerPath(configuredScope);
    }

    const mergeSource = this.getStoredPath(MERGE_SOURCE_KEY);
    if (mergeSource) {
      return mergeSource;
    }

    const mergeTarget = this.getStoredPath(MERGE_TARGET_KEY);
    if (mergeTarget) {
      return mergeTarget;
    }

    return undefined;
  }

  private getActiveServerPath(): string | undefined {
    return this.activeServerPath ?? this.getConfiguredServerExplorerRoot();
  }

  private async getPreferredHistoryPath(): Promise<string> {
    const active = this.getActiveServerPath();
    if (active && normalizeServerPath(active) !== normalizeServerPath('$/')) {
      return active;
    }
    const mapped = this.getCurrentWorkspaceMappingPath();
    if (mapped) {
      return cleanServerPath(mapped);
    }
    const workspaceRoot = this.resolvedWorkspaceRoot ?? await findTfvcWorkspaceRoot(this.rootPath);
    if (workspaceRoot) {
      try {
        const status = await this.client.status(workspaceRoot, false, { cwdOverride: workspaceRoot });
        this.workspaceStatus = status;
        const workspaceMapping = this.getCurrentWorkspaceMappingPath();
        if (workspaceMapping) {
          return cleanServerPath(workspaceMapping);
        }
      } catch {
        // The history request below will surface the actionable CLI error.
      }
    }
    return active ?? this.getBranchScope() ?? '$/';
  }

  private getActiveBranchScope(): string | undefined {
    const activePath = this.getActiveBranchPath();
    if (!activePath) {
      return undefined;
    }

    return cleanServerPath(activePath);
  }

  private getMergeSourcePath(): string | undefined {
    return this.getStoredPath(MERGE_SOURCE_KEY)
      ?? this.getActiveBranchPath()
      ?? this.getCurrentWorkspaceMappingPath();
  }

  private getActiveBranchPath(): string | undefined {
    return this.activeBranchPath ?? this.getConfiguredServerExplorerRoot();
  }

  private getCurrentWorkspaceMappingPath(): string | undefined {
    const anchorPath = vscode.window.activeTextEditor?.document.uri.scheme === 'file'
      ? vscode.window.activeTextEditor.document.uri.fsPath
      : vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? this.rootPath;
    const discovered = discoverTfvcMappingForPath(anchorPath);
    if (discovered) {
      return cleanServerPath(discovered.serverPath);
    }
    if (!this.workspaceStatus) {
      return undefined;
    }
    const normalizedAnchor = anchorPath ? normalizeForCompare(anchorPath) : undefined;
    const mapping = this.workspaceStatus.workspace.mappings
      .filter((item) => {
        const normalizedMapping = normalizeForCompare(item.localPath);
        return !normalizedAnchor || normalizedAnchor === normalizedMapping
          || normalizedAnchor.startsWith(`${normalizedMapping}${path.sep}`);
      })
      .sort((left, right) => right.localPath.length - left.localPath.length)[0];
    return mapping?.serverPath ? cleanServerPath(mapping.serverPath) : undefined;
  }

  private async getMergeTargetPath(sourcePath: string | undefined): Promise<string | undefined> {
    const storedTarget = this.getStoredPath(MERGE_TARGET_KEY);
    if (!sourcePath) {
      return storedTarget;
    }

    if (storedTarget && normalizeServerPath(storedTarget) !== normalizeServerPath(sourcePath)) {
      return storedTarget;
    }

    return undefined;
  }

  private async getMergeTargetBranchPaths(sourcePath: string): Promise<string[]> {
    try {
      const response = await this.client.branchShow(sourcePath);
      const children = response.branch.children
        ?.map((item) => cleanServerPath(item))
        .filter((item) => normalizeServerPath(item) !== normalizeServerPath(sourcePath))
        .sort((left, right) => left.localeCompare(right, undefined, { sensitivity: 'base' })) ?? [];

      if (children.length) {
        return children;
      }

      if (response.branch.parentPath) {
        return [cleanServerPath(response.branch.parentPath)];
      }
    } catch (error) {
      this.output.appendLine(`arm-tfs merge target branch show: ${getErrorMessage(error)}`);
    }

    return this.getSiblingBranchPaths(sourcePath);
  }

  private async getSiblingBranchPaths(sourcePath: string): Promise<string[]> {
    const parent = getServerParentPath(sourcePath);
    if (!parent || parent === '$/') {
      return [];
    }

    try {
      const response = await this.client.branchList(parent);
      const sourceName = normalizeServerPath(sourcePath);
      const parentName = normalizeServerPath(parent);
      const siblings = response.items
        .map((item) => item.path)
        .filter((candidate) => {
          const normalized = normalizeServerPath(candidate);
          return normalized !== sourceName
            && normalizeServerPath(getServerParentPath(candidate) ?? '') === parentName;
        })
        .sort((left, right) => left.localeCompare(right, undefined, { sensitivity: 'base' }));

      return siblings;
    } catch (error) {
      this.output.appendLine(`arm-tfs merge target discovery: ${getErrorMessage(error)}`);
      return [];
    }
  }

  private async useMergeTarget(targetPath: string): Promise<void> {
    await vscode.workspace.getConfiguration('armTfs').update('merge.targetPath', cleanServerPath(targetPath), getConfigTarget());
    await this.refreshMerge();
  }

  private async copyTfvcPath(node?: BranchNode | MergeTargetOptionNode | MergeCandidateNode | ServerExplorerNode | string): Promise<void> {
    const tfvcPath = typeof node === 'string'
      ? node
      : node instanceof BranchNode
        ? node.branch.path
        : node instanceof MergeTargetOptionNode
          ? node.targetPath
          : node instanceof MergeCandidateNode
            ? node.targetPath
            : node instanceof ServerExplorerNode
              ? node.entry.serverPath
              : undefined;

    if (!tfvcPath) {
      void vscode.window.showWarningMessage(t('sidebar.warning.selectBranch'));
      return;
    }

    await vscode.env.clipboard.writeText(tfvcPath);
    vscode.window.setStatusBarMessage(t('sidebar.status.copiedTfvcPath', { path: tfvcPath }), 2500);
  }

  private async persistActiveServerPath(
    serverPath: string,
    options: { syncBranchScope: boolean; syncMergeSource: boolean },
  ): Promise<void> {
    const target = getConfigTarget();
    if (options.syncBranchScope) {
      const branchScope = cleanServerPath(serverPath);
      await vscode.workspace.getConfiguration('armTfs').update('branch.scope', branchScope, target);
    }
    if (options.syncMergeSource) {
      await vscode.workspace.getConfiguration('armTfs').update('merge.sourcePath', serverPath, target);
    }
  }

  private getConfiguredServerExplorerRoot(): string | undefined {
    const value = vscode.workspace.getConfiguration('armTfs').get<string>('serverExplorer.rootPath')?.trim();
    return value?.startsWith('$/') ? cleanServerPath(value) : undefined;
  }

  private getStoredPath(key: string): string | undefined {
    const configKey = key === MERGE_SOURCE_KEY ? 'merge.sourcePath' : 'merge.targetPath';
    const value = vscode.workspace.getConfiguration('armTfs').get<string>(configKey)?.trim();
    return value || undefined;
  }

  private async setBranchScope(suggestedValue?: string): Promise<void> {
    const existingValue = suggestedValue ?? this.getBranchScope() ?? '$/';
    const value = await vscode.window.showInputBox({
      prompt: t('sidebar.prompt.branchScope'),
      value: existingValue,
      ignoreFocusOut: true,
      validateInput(input) {
        return input.trim().startsWith('$/') ? undefined : t('sidebar.validate.serverPath');
      },
    });

    if (value === undefined) {
      return;
    }

    const trimmed = cleanServerPath(value);
    this.activeBranchPath = trimmed;
    await vscode.workspace.getConfiguration('armTfs').update('branch.scope', trimmed, getConfigTarget());
    await this.refreshBranches();
  }

  private async setMergePath(key: string, prompt: string, suggestedValue?: string): Promise<void> {
    const configKey = key === MERGE_SOURCE_KEY ? 'merge.sourcePath' : 'merge.targetPath';
    const existingValue = this.getStoredPath(key) ?? suggestedValue ?? this.getCurrentWorkspaceMappingPath() ?? '$/';
    const value = await vscode.window.showInputBox({
      prompt,
      value: existingValue,
      ignoreFocusOut: true,
      validateInput(input) {
        return input.trim().startsWith('$/') ? undefined : t('sidebar.validate.serverPath');
      },
    });

    if (value === undefined) {
      return;
    }

    await vscode.workspace.getConfiguration('armTfs').update(configKey, value.trim(), getConfigTarget());
    await this.refreshMerge();
  }

  private async swapMergePaths(): Promise<void> {
    const sourcePath = this.getStoredPath(MERGE_SOURCE_KEY);
    const targetPath = this.getStoredPath(MERGE_TARGET_KEY);
    const target = getConfigTarget();

    await vscode.workspace.getConfiguration('armTfs').update('merge.sourcePath', targetPath ?? '', target);
    await vscode.workspace.getConfiguration('armTfs').update('merge.targetPath', sourcePath ?? '', target);
    await this.refreshMerge();
  }

  private async pullBranch(node?: BranchNode | ServerExplorerNode): Promise<void> {
    const mapped = this.resolveMappedBranchPath(node, 'pull');
    if (!mapped) {
      return;
    }

    try {
      const result = await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: `arm-tfs pull ${mapped.serverPath}`,
        },
        // checkoutServerPathToLocalFolder ensures .tf/workspace.json exists (via workspaceNew
        // when nothing is there yet, or workspaceMap if a parent workspace exists). Without
        // this, `arm-tfs get` would fetch files but never write the workspace metadata —
        // re-opening the folder later would have no .tf/ to discover the mapping from.
        () => checkoutServerPathToLocalFolder(this.client, mapped.serverPath, mapped.localPath),
      );

      reportCommandOutput(this.output, `arm-tfs pull ${mapped.serverPath}`, result);
      await this.refreshScm();
      await this.refreshAll();
    } catch (error) {
      this.showError('arm-tfs pull branch', error);
    }
  }

  private async checkoutBranch(node?: BranchNode | ServerExplorerNode): Promise<void> {
    const mapped = this.resolveMappedBranchPath(node, 'checkout');
    if (!mapped) {
      return;
    }

    try {
      const result = await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: `arm-tfs checkout ${mapped.serverPath}`,
        },
        // Same reasoning as pullBranch: ensure .tf/workspace.json before any get/checkout, so
        // the user can re-open the folder later and have the extension auto-detect the branch.
        () => checkoutServerPathToLocalFolder(this.client, mapped.serverPath, mapped.localPath),
      );

      reportCommandOutput(this.output, `arm-tfs checkout ${mapped.serverPath}`, result);
      await this.refreshScm();
      await this.refreshAll();
    } catch (error) {
      this.showError('arm-tfs checkout branch', error);
    }
  }

  private async showBranchHistory(node?: BranchNode | ServerExplorerNode): Promise<void> {
    const branchPath = getNodeServerPath(node);
    if (!branchPath) {
      void vscode.window.showWarningMessage(t('sidebar.warning.selectBranchFromView'));
      return;
    }

    await this.historyBrowser.open(branchPath);
  }

  private async mergeFromBranch(node?: BranchNode | ServerExplorerNode): Promise<void> {
    const sourcePath = getNodeServerPath(node);
    if (!sourcePath) {
      void vscode.window.showWarningMessage(t('sidebar.warning.selectSourceBranch'));
      return;
    }

    const targetBranches = await this.getMergeTargetBranchPaths(sourcePath);
    if (!targetBranches.length) {
      void vscode.window.showWarningMessage(t('sidebar.merge.noTargets'));
      return;
    }

    const target = await vscode.window.showQuickPick(
      targetBranches.map((targetPath) => ({
        label: path.posix.basename(targetPath),
        description: targetPath,
        targetPath,
      })),
      { placeHolder: t('sidebar.merge.pickTarget') },
    );
    if (!target) {
      return;
    }

    const top = vscode.workspace.getConfiguration('armTfs').get<number>('merge.candidateTop', 20);
    const scan = vscode.workspace.getConfiguration('armTfs').get<number>('merge.candidateScan', 80);
    try {
      const response = await this.client.mergeCandidates(sourcePath, target.targetPath, top, scan);
      if (!response.items.length) {
        void vscode.window.showInformationMessage(t('sidebar.noMergeCandidates'));
        return;
      }
      await ArmTfsMergeWorkbench.open(
        this.client,
        this.output,
        sourcePath,
        target.targetPath,
        response,
        async () => {
          await this.refreshScm();
          await this.refreshAll();
        },
      );
    } catch (error) {
      this.showError('arm-tfs merge', error);
    }
  }

  private async addBranch(node?: BranchNode | ServerExplorerNode): Promise<void> {
    const sourcePath = getNodeServerPath(node);
    if (!sourcePath) {
      void vscode.window.showWarningMessage(t('sidebar.warning.selectBranchFromView'));
      return;
    }
    const parent = getServerParentPath(sourcePath) ?? '$/';
    const targetPath = await vscode.window.showInputBox({
      prompt: t('sidebar.branch.createTarget'),
      value: `${parent}/${path.posix.basename(sourcePath)}-branch`,
      ignoreFocusOut: true,
      validateInput: (value) => {
        const trimmed = value.trim();
        if (!trimmed.startsWith('$/')) {
          return t('sidebar.validate.serverPath');
        }
        return normalizeServerPath(trimmed) === normalizeServerPath(sourcePath)
          ? t('sidebar.validate.mergeTargetDifferent')
          : undefined;
      },
    });
    if (!targetPath) {
      return;
    }
    const comment = await vscode.window.showInputBox({
      prompt: t('sidebar.branch.createComment'),
      value: `Branch ${targetPath.trim()} from ${sourcePath}`,
      ignoreFocusOut: true,
    });
    if (comment === undefined) {
      return;
    }
    try {
      const created = await this.client.branchCreate(sourcePath, targetPath.trim(), {
        comment: comment.trim() || undefined,
      });
      void vscode.window.showInformationMessage(t('sidebar.branch.created', {
        path: targetPath.trim(),
        changeset: created.createdChangesetId,
      }));
      await this.refreshBranches();
    } catch (error) {
      this.showError('arm-tfs branch create', error);
    }
  }

  private async diffHistoryNodeWithPrevious(node?: HistoryNode): Promise<void> {
    if (!node) {
      return;
    }
    const detail = await this.client.changesetShow(node.item.changesetId);
    const serverPath = await this.pickChangesetFile([detail]);
    if (!serverPath) {
      return;
    }
    const history = await this.client.history(serverPath, 100);
    const previous = history.items
      .map((item) => item.changesetId)
      .filter((changesetId) => changesetId < node.item.changesetId)
      .sort((left, right) => right - left)[0];
    if (previous === undefined) {
      void vscode.window.showInformationMessage(t('sidebar.diff.noPreviousVersion'));
      return;
    }
    await this.showServerVersionDiff(serverPath, previous, node.item.changesetId);
  }

  private selectChangesetForCompare(node?: HistoryNode): void {
    if (!node) {
      return;
    }
    this.selectedCompareChangesetId = node.item.changesetId;
    vscode.window.setStatusBarMessage(t('sidebar.diff.selectedChangeset', { changeset: node.item.changesetId }), 3000);
  }

  private async compareWithSelectedChangeset(node?: HistoryNode): Promise<void> {
    if (!node || this.selectedCompareChangesetId === undefined) {
      void vscode.window.showWarningMessage(t('sidebar.diff.selectFirst'));
      return;
    }
    const from = Math.min(this.selectedCompareChangesetId, node.item.changesetId);
    const to = Math.max(this.selectedCompareChangesetId, node.item.changesetId);
    if (from === to) {
      void vscode.window.showWarningMessage(t('sidebar.diff.sameChangeset'));
      return;
    }
    const details = await Promise.all([this.client.changesetShow(from), this.client.changesetShow(to)]);
    const serverPath = await this.pickChangesetFile(details);
    if (serverPath) {
      await this.showServerVersionDiff(serverPath, from, to);
    }
  }

  private async pickChangesetFile(details: ChangesetShowResponse[]): Promise<string | undefined> {
    const paths = new Set<string>();
    for (const detail of details) {
      for (const change of detail.changeset.changes ?? []) {
        if (change.item?.path && !change.item.isBranch) {
          paths.add(change.item.path);
        }
      }
    }
    const picked = await vscode.window.showQuickPick(
      [...paths].sort((left, right) => left.localeCompare(right)).map((serverPath) => ({
        label: path.posix.basename(serverPath),
        description: serverPath,
        serverPath,
      })),
      { placeHolder: t('sidebar.diff.pickFile') },
    );
    return picked?.serverPath;
  }

  private async showServerVersionDiff(serverPath: string, from: number, to: number): Promise<void> {
    try {
      await openServerVersionDiff(
        this.client,
        {
          serverPath,
          version: from,
          label: `${path.posix.basename(serverPath)} (cs${from})`,
        },
        {
          serverPath,
          version: to,
          label: `${path.posix.basename(serverPath)} (cs${to})`,
        },
        `${path.posix.basename(serverPath)}: cs${from} ↔ cs${to}`,
      );
    } catch (error) {
      this.showError('arm-tfs diff versions', error);
    }
  }

  private resolveMappedBranchPath(node: BranchNode | ServerExplorerNode | undefined, action: 'pull' | 'checkout'): MappedBranchPath | undefined {
    const branchPath = getNodeServerPath(node);
    if (!branchPath) {
      void vscode.window.showWarningMessage(t('sidebar.warning.beforeAction', { action }));
      return undefined;
    }

    // If we have workspace status, use the full mapping resolution (preferred path).
    if (this.workspaceStatus && this.resolvedWorkspaceRoot) {
      const mapped = resolveMappedLocalPathForServerPath(branchPath, this.workspaceStatus, this.resolvedWorkspaceRoot);
      if (!mapped) {
        // Tell the user exactly what's missing: a root mapping under their active TFS connection.
        // Offer a button that takes them straight to the place where they can add it.
        void vscode.window.showWarningMessage(
          t('sidebar.warning.noRootMapping', { path: branchPath }),
          t('sidebar.warning.openMappingsView'),
        ).then((choice) => {
          if (choice === t('sidebar.warning.openMappingsView')) {
            void vscode.commands.executeCommand('armTfs.workspaceMappings.add');
          }
        });
        return undefined;
      }
      return mapped;
    }

    // No workspace yet — try to auto-derive a local path from configured mappings or
    // tfsRootDirectory so the user can pull a brand-new branch without creating a workspace first.
    const computed = computeLocalPathForServerPath(branchPath);
    if (computed) {
      // Use the computed local path as both the workspace root and the target.
      // checkoutServerPathToLocalFolder will create .tf/workspace.json if needed.
      return { workspaceRoot: computed, serverPath: branchPath, localPath: computed };
    }

    // Nothing we can do — ask the user to configure a mapping root first.
    void vscode.window.showWarningMessage(t('sidebar.warning.openWorkspaceFirst'));
    return undefined;
  }

  private async executeMergeCandidate(node?: MergeCandidateNode): Promise<void> {
    if (!node) {
      void vscode.window.showWarningMessage(t('sidebar.warning.selectMergeCandidate'));
      return;
    }

    const mode = await vscode.window.showQuickPick(
      [
        { label: t('sidebar.mergeMode.dryRun'), description: t('sidebar.mergeMode.dryRun.desc'), value: 'dryRun' },
        { label: t('sidebar.mergeMode.execute'), description: t('sidebar.mergeMode.execute.desc'), value: 'execute' },
      ],
      { placeHolder: t('sidebar.mergeMode.placeholder') },
    );
    if (!mode) {
      return;
    }

    const defaultComment = `Merge cs${node.changesetId} from ${path.posix.basename(node.sourcePath)} to ${path.posix.basename(node.targetPath)}`;
    const comment = await vscode.window.showInputBox({
      prompt: t('sidebar.prompt.mergeComment'),
      value: defaultComment,
      ignoreFocusOut: true,
    });
    if (comment === undefined) {
      return;
    }

    try {
      const isDryRun = mode.value === 'dryRun';
      const response = await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: `arm-tfs merge execute cs${node.changesetId}`,
        },
        () => this.client.mergeExecuteJson(node.sourcePath, node.targetPath, node.changesetId, {
          comment: comment.trim() || undefined,
          dryRun: isDryRun,
        }),
      );

      const title = `arm-tfs merge execute cs${node.changesetId}`;
      const createdId = response.result.createdChangesetId;
      const warnings = response.result.warnings ?? [];

      if (isDryRun) {
        reportCommandOutput(this.output, title, formatMergeOutcome(response), {
          summary: t('merge.execute.dryRun', { count: 1 }),
        });
        return;
      }

      if (createdId === null || createdId === undefined) {
        // Exit code was 0 but no changeset was actually created: surface this as a problem
        // instead of a misleading success message.
        this.output.appendLine(`> ${title}`);
        this.output.appendLine(formatMergeOutcome(response));
        this.output.appendLine('');
        void vscode.window.showWarningMessage(
          warnings[0]
            ? translateCliMessage(warnings[0])
            : t('merge.execute.noChange', { changesets: `cs${node.changesetId}` }),
        );
        return;
      }

      reportCommandOutput(this.output, title, formatMergeOutcome(response), {
        summary: t('merge.execute.success', { count: 1, created: `cs${createdId}` }),
      });
      await this.refreshScm();
      await this.refreshAll();
    } catch (error) {
      this.showError('arm-tfs merge execute', error);
    }
  }

  private showError(title: string, error: unknown): void {
    this.output.show(true);
    if (error instanceof ArmTfsCliError) {
      this.output.appendLine(error.message);
      if (error.stdout.trim()) {
        this.output.appendLine(error.stdout.trim());
      }
      if (error.stderr.trim()) {
        this.output.appendLine(error.stderr.trim());
      }
      void vscode.window.showErrorMessage(t('error.failed', { title, message: translateCliMessage(error.message) }));
      return;
    }

    const message = translateCliMessage(getErrorMessage(error));
    this.output.appendLine(message);
    void vscode.window.showErrorMessage(t('error.failed', { title, message }));
  }
}

function formatMergeOutcome(response: MergeExecuteResponse): string {
  const result = response.result;
  const lines: string[] = [];
  lines.push(`Source    : ${result.sourcePath}`);
  lines.push(`Target    : ${result.targetPath}`);
  lines.push(`Changeset : cs${result.sourceChangesetId}`);
  lines.push(`Mode      : ${result.dryRun ? 'dry-run' : 'execute'}`);
  if (result.createdChangesetId !== null && result.createdChangesetId !== undefined) {
    lines.push(`Created   : cs${result.createdChangesetId}`);
  }
  for (const change of result.changes) {
    lines.push(`  [${change.status}] ${change.targetServerPath} (${change.sourceChangeType} -> ${change.targetChangeType})`);
    if (change.note) {
      lines.push(`      note: ${change.note}`);
    }
  }
  if (result.warnings.length) {
    lines.push('Warnings:');
    for (const warning of result.warnings) {
      lines.push(`  - ${warning}`);
    }
  }
  return lines.join('\n');
}

function buildBranchTree(branches: BranchRef[]): BranchNode[] {
  const nodes = branches
    .slice()
    .sort((left, right) => left.path.localeCompare(right.path, undefined, { sensitivity: 'base' }))
    .map((branch) => new BranchNode(branch));
  const nodeByPath = new Map(nodes.map((node) => [node.branch.path, node]));
  const roots: BranchNode[] = [];

  for (const node of nodes) {
    const parent = findParentBranch(node.branch.path, nodeByPath);
    if (parent) {
      parent.children ??= [];
      parent.children.push(node);
      parent.collapsibleState = vscode.TreeItemCollapsibleState.Expanded;
    } else {
      roots.push(node);
    }
  }

  for (const node of nodes) {
    if (!node.children?.length) {
      node.collapsibleState = vscode.TreeItemCollapsibleState.None;
    }
  }

  return roots;
}

function findParentBranch(branchPath: string, nodeByPath: Map<string, BranchNode>): BranchNode | undefined {
  let bestMatch: BranchNode | undefined;
  for (const [candidatePath, candidateNode] of nodeByPath.entries()) {
    if (candidatePath === branchPath) {
      continue;
    }

    if (!branchPath.startsWith(`${candidatePath}/`)) {
      continue;
    }

    if (!bestMatch || candidatePath.length > bestMatch.branch.path.length) {
      bestMatch = candidateNode;
    }
  }

  return bestMatch;
}

function buildMergeSummaryNodes(mergeBase: MergeBaseResponse, candidates: MergeCandidateResponse): ArmTfsTreeNode[] {
  const summary = new InfoNode(t('sidebar.mergeBase'), mergeBase.mergeBase.commonAncestorPath ?? mergeBase.mergeBase.relationship);
  summary.iconPath = new vscode.ThemeIcon('combine');
  summary.tooltip = [
    `Relationship: ${mergeBase.mergeBase.relationship}`,
    `Confidence: ${mergeBase.mergeBase.confidence}`,
    ...mergeBase.mergeBase.notes,
  ].join('\n');

  const candidateSection = new InfoNode(t('sidebar.candidates'), t('sidebar.pendingCount', { count: candidates.items.length }));
  candidateSection.iconPath = new vscode.ThemeIcon('list-tree');
  candidateSection.children = candidates.items.length
    ? candidates.items.map((item) => new MergeCandidateNode(
        candidates.query.sourcePath,
        candidates.query.targetPath,
        item.changesetId,
        buildMergeCandidateDescription(item),
        [
          t('sidebar.history.tooltip.changeset', { changeset: item.changesetId }),
          t('sidebar.history.tooltip.created', { createdAt: item.createdAt }),
          item.author?.displayName ? t('sidebar.history.tooltip.author', { author: item.author.displayName }) : undefined,
          item.comment,
        ].filter(Boolean).join('\n'),
      ))
    : [new InfoNode(t('sidebar.noMergeCandidates'), t('sidebar.noMergeCandidates.desc'))];

  return [
    summary,
    new InfoNode(t('sidebar.sourceAncestry'), mergeBase.mergeBase.sourceAncestry.join(' -> ')),
    new InfoNode(t('sidebar.targetAncestry'), mergeBase.mergeBase.targetAncestry.join(' -> ')),
    new InfoNode(t('sidebar.historyScan'), `${candidates.summary.sourceHistoryScanned}/${candidates.summary.targetHistoryScanned}`),
    candidateSection,
  ];
}

function buildHistoryDescription(item: HistoryItem): string {
  const author = item.author?.displayName ?? item.checkedInBy?.displayName;
  const comment = item.comment?.replace(/\s+/g, ' ').trim();
  const shortComment = comment && comment.length > 40 ? `${comment.slice(0, 39)}…` : comment;
  const parts = [author, item.createdAt.slice(0, 10), shortComment].filter(Boolean);
  return parts.join(' | ');
}

function buildMergeCandidateDescription(item: MergeCandidateResponse['items'][number]): string {
  const author = item.author?.displayName ?? 'unknown';
  const comment = item.comment?.replace(/\s+/g, ' ').trim();
  const shortComment = comment && comment.length > 40 ? `${comment.slice(0, 39)}…` : comment;
  return [author, item.createdAt.slice(0, 10), shortComment].filter(Boolean).join(' | ');
}

function getNodeServerPath(node: BranchNode | ServerExplorerNode | undefined): string | undefined {
  if (node instanceof BranchNode) {
    return node.branch.path;
  }
  if (node instanceof ServerExplorerNode) {
    return node.entry.serverPath;
  }
  return undefined;
}

function samePath(left: string, right: string): boolean {
  return normalizeForCompare(left) === normalizeForCompare(right);
}

function isSameOrChildPath(candidate: string, parent: string): boolean {
  const normalizedCandidate = normalizeForCompare(candidate);
  const normalizedParent = normalizeForCompare(parent);
  return normalizedCandidate === normalizedParent || normalizedCandidate.startsWith(`${normalizedParent}${path.sep}`);
}

function normalizeForCompare(targetPath: string): string {
  return path.resolve(translatePlatformSharedPath(targetPath)).toLowerCase();
}

function getConfigTarget(): vscode.ConfigurationTarget {
  return vscode.workspace.workspaceFolders?.length ? vscode.ConfigurationTarget.Workspace : vscode.ConfigurationTarget.Global;
}

function getErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : `${error}`;
}

interface MappedBranchPath {
  workspaceRoot: string;
  serverPath: string;
  localPath: string;
}

function resolveMappedLocalPathForServerPath(
  serverPath: string,
  status: StatusResponse,
  workspaceRoot: string,
): MappedBranchPath | undefined {
  let bestMatch: { localPath: string; score: number } | undefined;

  for (const mapping of status.workspace.mappings) {
    if (isSameOrChildServerPath(serverPath, mapping.serverPath)) {
      const suffix = getServerPathSuffix(serverPath, mapping.serverPath);
      const mappingLocalPath = translatePlatformSharedPath(mapping.localPath);
      const localPath = suffix ? path.join(mappingLocalPath, ...suffix.split('/')) : mappingLocalPath;
      const score = mapping.serverPath.length + 1000;
      if (!bestMatch || score > bestMatch.score) {
        bestMatch = { localPath, score };
      }
      continue;
    }

    if (isSameOrChildServerPath(mapping.serverPath, serverPath)) {
      const score = mapping.serverPath.length;
      if (!bestMatch || score > bestMatch.score) {
        bestMatch = { localPath: translatePlatformSharedPath(mapping.localPath), score };
      }
    }
  }

  // Fallback to the user's configured root mapping (profile workspaceMappings or
  // armTfs.tfsRootDirectory). Lets `pull branch` / `checkout branch` auto-derive a sensible
  // path instead of erroring 'Branch not mapped, run Checkout Server Path To Folder first'.
  if (!bestMatch) {
    const computed = computeLocalPathForServerPath(serverPath);
    if (computed) {
      return { workspaceRoot, serverPath, localPath: computed };
    }
  }

  return bestMatch
    ? {
        workspaceRoot,
        serverPath,
        localPath: bestMatch.localPath,
      }
    : undefined;
}

function isSameOrChildServerPath(candidate: string, parent: string): boolean {
  const normalizedCandidate = normalizeServerPath(candidate);
  const normalizedParent = normalizeServerPath(parent);
  return normalizedCandidate === normalizedParent || normalizedCandidate.startsWith(`${normalizedParent}/`);
}

function getServerPathSuffix(candidate: string, parent: string): string {
  const normalizedCandidate = normalizeServerPath(candidate);
  const normalizedParent = normalizeServerPath(parent);
  return normalizedCandidate === normalizedParent
    ? ''
    : normalizedCandidate.slice(normalizedParent.length + 1);
}

function cleanServerPath(serverPath: string): string {
  const trimmed = serverPath.trim();
  if (trimmed === '$' || /^\$\/+$/.test(trimmed)) {
    return '$/';
  }
  return trimmed.replace(/\/+$/, '') || '$/';
}

function normalizeServerPath(serverPath: string): string {
  return cleanServerPath(serverPath).toLowerCase();
}

function translatePlatformSharedPath(targetPath: string): string {
  if (process.platform !== 'darwin' && process.platform !== 'win32') {
    return targetPath;
  }

  const normalized = targetPath.replace(/\\/g, '/');
  const home = getPlatformSharedHomeDirectory();
  if (!home) {
    return targetPath;
  }

  for (const prefix of ['//Mac/Home/', '/Mac/Home/']) {
    if (normalized.toLowerCase().startsWith(prefix.toLowerCase())) {
      return path.join(home, normalized.slice(prefix.length));
    }
  }

  const withoutDrive = /^[A-Za-z]:\//.test(normalized) ? normalized.slice(3) : normalized;
  const drivePrefix = 'Mac/Home/';
  if (withoutDrive.toLowerCase().startsWith(drivePrefix.toLowerCase())) {
    return path.join(home, withoutDrive.slice(drivePrefix.length));
  }

  return targetPath;
}

function getPlatformSharedHomeDirectory(): string | undefined {
  if (process.platform === 'darwin') {
    return process.env.HOME;
  }
  if (process.platform === 'win32' && existsSync('C:\\Mac\\Home')) {
    return 'C:\\Mac\\Home';
  }
  return undefined;
}

function getServerParentPath(serverPath: string): string | undefined {
  const normalized = cleanServerPath(serverPath);
  if (normalized === '$/' || normalized === '$') {
    return undefined;
  }

  const lastSlash = normalized.lastIndexOf('/');
  if (lastSlash <= 1) {
    return '$/';
  }

  return normalized.slice(0, lastSlash);
}

function getBranchScopeForActivePath(serverPath: string): string {
  const parentPath = getServerParentPath(serverPath);
  return parentPath && parentPath !== '$/' ? parentPath : cleanServerPath(serverPath);
}

function compareServerExplorerEntries(left: ServerItemEntry, right: ServerItemEntry): number {
  if (left.isFolder !== right.isFolder) {
    return left.isFolder ? -1 : 1;
  }

  const leftLabel = path.posix.basename(left.serverPath) || left.serverPath;
  const rightLabel = path.posix.basename(right.serverPath) || right.serverPath;
  const byLabel = leftLabel.localeCompare(rightLabel, undefined, { sensitivity: 'base', numeric: false });
  if (byLabel !== 0) {
    return byLabel;
  }

  return left.serverPath.localeCompare(right.serverPath, undefined, { sensitivity: 'base', numeric: false });
}

// ─── Server Explorer (Source Control Explorer) ───────────────────────────────

/** A node in the TFS server explorer tree. */
class ServerExplorerNode extends vscode.TreeItem {
  constructor(public readonly entry: ServerItemEntry) {
    super(
      path.posix.basename(entry.serverPath) || entry.serverPath,
      entry.isFolder
        ? vscode.TreeItemCollapsibleState.Collapsed
        : vscode.TreeItemCollapsibleState.None,
    );
    this.id = `serverExplorer:${entry.serverPath}`;
    this.description = entry.isFolder ? undefined : `cs${entry.changesetId}`;
    this.tooltip = [
      entry.serverPath,
      entry.checkinDate ? t('serverExplorer.tooltip.lastModified', { date: entry.checkinDate.slice(0, 10) }) : undefined,
      entry.contentLength != null ? t('serverExplorer.tooltip.size', { size: entry.contentLength }) : undefined,
    ].filter(Boolean).join('\n');
    this.iconPath = entry.isFolder
      ? new vscode.ThemeIcon('folder')
      : new vscode.ThemeIcon('file');
    this.contextValue = entry.isFolder ? 'armTfsServerFolder' : 'armTfsServerFile';
    if (!entry.isFolder) {
      this.command = {
        command: 'armTfs.serverExplorer.showHistory',
        title: t('serverExplorer.showHistory'),
        arguments: [this],
      };
    }
  }
}

/** Root placeholder node shown when the root path is not configured. */
class ServerExplorerUnconfiguredNode extends vscode.TreeItem {
  constructor() {
    super(t('serverExplorer.unconfigured'), vscode.TreeItemCollapsibleState.None);
    this.iconPath = new vscode.ThemeIcon('info');
    this.contextValue = 'armTfsServerUnconfigured';
    this.command = {
      command: 'armTfs.serverExplorer.setRoot',
      title: t('serverExplorer.setRootPath'),
    };
  }
}

/**
 * Lazy-loading tree data provider for the TFS Server Explorer.
 * Each folder node fetches its direct children on first expand.
 */
class ServerExplorerProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
  private readonly onDidChangeTreeDataEmitter = new vscode.EventEmitter<vscode.TreeItem | undefined | null | void>();
  readonly onDidChangeTreeData = this.onDidChangeTreeDataEmitter.event;

  /**
   * Cache of loaded children per server path.
   * Error nodes are also cached to prevent VS Code from retrying infinitely.
   * Call refresh() to clear the cache and allow retry.
   */
  private readonly childCache = new Map<string, vscode.TreeItem[]>();
  private rootPath: string | undefined;

  /** Active filter pattern (case-insensitive). Empty/undefined means no filter. */
  private filterPattern: string | undefined;
  /**
   * When filtering, we recursively load the entire subtree once and store the surviving nodes
   * grouped by their parent server path. Tree expansion then reads from this map instead of
   * issuing a fresh itemsList per directory.
   */
  private filteredChildrenByParent: Map<string, ServerItemEntry[]> | undefined;

  constructor(private readonly client: ArmTfsCliClient, private readonly output: vscode.OutputChannel) {
    this.rootPath = vscode.workspace.getConfiguration('armTfs').get<string>('serverExplorer.rootPath')?.trim() || undefined;
  }

  setRootPath(rootPath: string): void {
    this.rootPath = rootPath;
    this.childCache.clear();
    // Filter is anchored to a root — invalidate it whenever the root changes.
    this.filteredChildrenByParent = undefined;
    this.onDidChangeTreeDataEmitter.fire();
  }

  refresh(): void {
    this.childCache.clear();
    this.filteredChildrenByParent = undefined;
    this.onDidChangeTreeDataEmitter.fire();
  }

  getActiveFilter(): string | undefined {
    return this.filterPattern;
  }

  /**
   * Apply a name filter. Pass empty/undefined to clear. The filter only matches folders /
   * branches — files inside branches are never searched. Matching folders show with their
   * full ancestor chain so the user can see where each match lives.
   */
  async setFilter(pattern: string | undefined): Promise<void> {
    const trimmed = pattern?.trim();
    if (!trimmed) {
      this.filterPattern = undefined;
      this.filteredChildrenByParent = undefined;
      this.onDidChangeTreeDataEmitter.fire();
      return;
    }
    if (!this.rootPath) {
      return;
    }

    this.filterPattern = trimmed;
    try {
      const response = await this.client.itemsList(this.rootPath, true);
      const lowered = trimmed.toLowerCase();
      const folders = response.items.filter((item) => item.isFolder
        && item.serverPath.toLowerCase() !== this.rootPath!.toLowerCase());
      const surviving = new Set<string>();
      for (const folder of folders) {
        const name = folder.serverPath.split('/').filter(Boolean).pop() ?? folder.serverPath;
        if (name.toLowerCase().includes(lowered)) {
          // Keep this folder and every ancestor up to the root so the path stays visible.
          let current = folder.serverPath;
          while (current && current.toLowerCase() !== this.rootPath!.toLowerCase()) {
            surviving.add(current);
            const parent = current.split('/').slice(0, -1).join('/');
            if (!parent || parent === current) {
              break;
            }
            current = parent;
          }
        }
      }
      const grouped = new Map<string, ServerItemEntry[]>();
      for (const folder of folders) {
        if (!surviving.has(folder.serverPath)) {
          continue;
        }
        const parent = folder.serverPath.split('/').slice(0, -1).join('/');
        const list = grouped.get(parent) ?? [];
        list.push(folder);
        grouped.set(parent, list);
      }
      // Sort each parent's children for consistent presentation.
      for (const list of grouped.values()) {
        list.sort(compareServerExplorerEntries);
      }
      this.filteredChildrenByParent = grouped;
    } catch (error) {
      this.output.appendLine(`arm-tfs server explorer filter: ${error instanceof Error ? error.message : String(error)}`);
      this.filteredChildrenByParent = new Map();
    }
    this.onDidChangeTreeDataEmitter.fire();
  }

  getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
    return element;
  }

  async getChildren(element?: vscode.TreeItem): Promise<vscode.TreeItem[]> {
    if (!element) {
      // Root level
      if (!this.rootPath) {
        return [new ServerExplorerUnconfiguredNode()];
      }

      if (this.filteredChildrenByParent) {
        const entries = this.filteredChildrenByParent.get(this.rootPath) ?? [];
        return entries.map((item) => new ServerExplorerNode(item));
      }

      const cached = this.childCache.get(this.rootPath);
      if (cached) {
        return cached;
      }

      return this.loadChildren(this.rootPath);
    }

    if (element instanceof ServerExplorerNode && element.entry.isFolder) {
      if (this.filteredChildrenByParent) {
        const entries = this.filteredChildrenByParent.get(element.entry.serverPath) ?? [];
        return entries.map((item) => new ServerExplorerNode(item));
      }

      const cached = this.childCache.get(element.entry.serverPath);
      if (cached) {
        return cached;
      }

      return this.loadChildren(element.entry.serverPath);
    }

    return [];
  }

  private async loadChildren(serverPath: string): Promise<vscode.TreeItem[]> {
    try {
      const response = await this.client.itemsList(serverPath, false);
      // Filter out the parent itself (TFS REST API always returns the scope path as the first entry)
      const children = response.items
        .filter((item) => item.serverPath.toLowerCase() !== serverPath.toLowerCase())
        .sort(compareServerExplorerEntries)
        .map((item) => new ServerExplorerNode(item));

      this.childCache.set(serverPath, children);
      return children;
    } catch (error) {
      const msg = error instanceof ArmTfsCliError ? error.message : `${error}`;
      this.output.appendLine(`arm-tfs server explorer: ${msg}`);

      const isNotFound = msg.includes('ENOENT') || msg.includes('not found');
      const isUnrecognized = msg.includes('Unrecognized command') || msg.includes('Required command was not provided');

      let label: string;
      if (isNotFound) {
        label = t('serverExplorer.cliNotFoundNode');
      } else if (isUnrecognized) {
        label = t('serverExplorer.commandMissingNode');
      } else {
        // Only show the first line to keep the tree node label short
        label = `${t('serverExplorer.errorPrefix')}: ${msg.split('\n')[0].slice(0, 120)}`;
      }

      const errNode = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
      errNode.iconPath = new vscode.ThemeIcon(isNotFound || isUnrecognized ? 'warning' : 'error');
      errNode.tooltip = msg;
      if (isNotFound) {
        errNode.command = {
          command: 'armTfs.configureCliCommand',
          title: t('serverExplorer.configureCliPath'),
        };
      }

      // CRITICAL: cache the error result so VS Code does NOT call getChildren again
      // on the next expand. The user must click Refresh to retry.
      // Without this cache, the tree view enters an infinite retry loop.
      this.childCache.set(serverPath, [errNode]);
      return [errNode];
    }
    // NOTE: No finally { fire() } here — that would trigger an infinite loop:
    // error → fire() → getChildren → no cache → loadChildren → error → ...
  }
}

/** Controller wiring the server explorer tree view and its commands. */
export class ArmTfsServerExplorerController implements vscode.Disposable {
  private readonly provider: ServerExplorerProvider;
  private readonly disposables: vscode.Disposable[] = [];
  private readonly treeView: vscode.TreeView<ServerExplorerNode | vscode.TreeItem>;

  constructor(
    private readonly client: ArmTfsCliClient,
    private readonly output: vscode.OutputChannel,
    private readonly refreshScm: () => Promise<void>,
    private readonly onActiveServerPathChanged?: (
      serverPath: string,
      options?: { branchContext?: boolean; syncBranchScope?: boolean; refresh?: boolean; syncMergeSource?: boolean },
    ) => Promise<void> | void,
    private readonly onRootPathChanged?: (serverPath: string) => Promise<void> | void,
    private readonly historyBrowser?: ArmTfsHistoryBrowser,
  ) {
    this.provider = new ServerExplorerProvider(client, output);
    this.treeView = vscode.window.createTreeView('armTfs.serverExplorer', { treeDataProvider: this.provider });
    this.refreshLabels();

    this.disposables.push(
      this.treeView,
      // Selection in server explorer used to push the path into the branches view, but that
      // coupled two unrelated concerns: explorer is a pure TFVC tree browser, branches comes
      // from the workspace folder's .tf/workspace.json. Keep them independent.

      vscode.commands.registerCommand('armTfs.serverExplorer.refresh', () => this.provider.refresh()),

      vscode.commands.registerCommand('armTfs.serverExplorer.setFilter', async () => {
        const current = this.provider.getActiveFilter() ?? '';
        const value = await vscode.window.showInputBox({
          prompt: t('serverExplorer.prompt.filter'),
          placeHolder: t('serverExplorer.prompt.filter.placeholder'),
          value: current,
          ignoreFocusOut: true,
        });
        if (value === undefined) {
          return;
        }
        await vscode.window.withProgress(
          { location: { viewId: 'armTfs.serverExplorer' } },
          () => this.provider.setFilter(value),
        );
        this.refreshLabels();
      }),

      vscode.commands.registerCommand('armTfs.serverExplorer.clearFilter', async () => {
        await this.provider.setFilter(undefined);
        this.refreshLabels();
      }),

      vscode.commands.registerCommand('armTfs.serverExplorer.setRoot', async (node?: ServerExplorerNode) => {
        const current = node?.entry.serverPath
          ?? vscode.workspace.getConfiguration('armTfs').get<string>('serverExplorer.rootPath')?.trim()
          ?? '$/';
        const value = await vscode.window.showInputBox({
          prompt: t('serverExplorer.prompt.rootPath'),
          value: current,
          ignoreFocusOut: true,
          validateInput: (input) => input.trim().startsWith('$/') ? undefined : t('sidebar.validate.serverPath'),
        });
        if (value === undefined) {
          return;
        }

        const trimmed = value.trim();
        await vscode.workspace.getConfiguration('armTfs').update('serverExplorer.rootPath', trimmed, getConfigTarget());
        await this.onRootPathChanged?.(trimmed);
        this.provider.setRootPath(trimmed);
        // Note: setting the explorer root no longer affects the branches view.
      }),

      vscode.commands.registerCommand('armTfs.serverExplorer.getLatest', async (node?: ServerExplorerNode) => {
        if (!node) {
          return;
        }

        const serverPath = node.entry.serverPath;
        const computedPath = computeLocalPathForServerPath(serverPath);
        const localPath = await vscode.window.showInputBox({
          prompt: t('serverExplorer.prompt.downloadPath', { path: serverPath }),
          value: computedPath ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? '.',
          ignoreFocusOut: true,
        });
        if (!localPath) {
          return;
        }

        try {
          const result = await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: t('serverExplorer.progress.get', { path: serverPath }) },
            () => checkoutServerPathToLocalFolder(this.client, serverPath, localPath),
          );
          reportCommandOutput(this.output, t('serverExplorer.progress.get', { path: serverPath }), result);
          await this.refreshScm();
          await this.provider.refresh();
        } catch (error) {
          void vscode.window.showErrorMessage(t('error.failed', {
            title: t('extension.operation.get'),
            message: translateCliMessage(getErrorMessage(error)),
          }));
        }
      }),

      vscode.commands.registerCommand('armTfs.serverExplorer.checkout', async (node?: ServerExplorerNode) => {
        if (!node || node.entry.isFolder) {
          return;
        }

        const serverPath = node.entry.serverPath;
        try {
          const result = await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: t('serverExplorer.progress.checkout', { path: serverPath }) },
            () => this.client.checkout([serverPath]),
          );
          reportCommandOutput(this.output, t('serverExplorer.progress.checkout', { path: serverPath }), result);
          await this.refreshScm();
        } catch (error) {
          void vscode.window.showErrorMessage(t('error.failed', {
            title: t('extension.operation.checkout'),
            message: translateCliMessage(getErrorMessage(error)),
          }));
        }
      }),

      vscode.commands.registerCommand('armTfs.serverExplorer.showHistory', async (node?: ServerExplorerNode) => {
        if (!node) {
          return;
        }

        const serverPath = node.entry.serverPath;
        await this.historyBrowser?.open(serverPath);
      }),

      vscode.commands.registerCommand('armTfs.serverExplorer.copyPath', async (node?: ServerExplorerNode) => {
        if (!node) {
          return;
        }

        await vscode.env.clipboard.writeText(node.entry.serverPath);
        vscode.window.setStatusBarMessage(t('serverExplorer.status.copiedPath', { path: node.entry.serverPath }), 2000);
      }),
    );
  }

  refreshLabels(): void {
    const filter = this.provider.getActiveFilter();
    this.treeView.title = filter
      ? t('view.serverExplorer.filtered', { pattern: filter })
      : t('view.serverExplorer');
  }

  dispose(): void {
    vscode.Disposable.from(...this.disposables).dispose();
  }

  setRootPath(serverPath: string): void {
    this.provider.setRootPath(serverPath);
  }

  refresh(): void {
    this.provider.refresh();
  }
}
