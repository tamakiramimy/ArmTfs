import * as path from 'node:path';
import * as vscode from 'vscode';
import { ArmTfsCliClient, ArmTfsCliError } from './armTfsCliClient';
import type { BranchRef, ChangesetShowResponse, DiffResponse, HistoryItem, MergeBaseResponse, MergeCandidateResponse, ServerItemEntry, StatusResponse } from './contracts';
import type { ArmTfsHistoryBrowser } from './historyBrowser';
import { t, translateCliMessage, translateCliText } from './i18n';
import { checkoutServerPathToLocalFolder } from './serverPathCheckout';
import { discoverTfvcMappingForPath, findTfvcWorkspaceRoot, findTfvcWorkspaceRootSync, getCommandCwd, isPathWithin } from './tfvcContext';

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
      `Changeset: ${item.changesetId}`,
      `Created: ${item.createdAt}`,
      item.author?.displayName ? `Author: ${item.author.displayName}` : undefined,
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
    const branchView = vscode.window.createTreeView('armTfs.branches', {
      treeDataProvider: this.branchProvider,
      canSelectMany: true,
    });

    this.disposables.push(
      branchView,
      branchView.onDidChangeSelection((event) => {
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
      vscode.window.registerTreeDataProvider('armTfs.historyGraph', this.historyProvider),
      vscode.window.registerTreeDataProvider('armTfs.merge', this.mergeProvider),
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
      vscode.commands.registerCommand('armTfs.sidebar.setBranchScope', async (node?: BranchNode) => this.setBranchScope(node?.branch.path)),
      vscode.commands.registerCommand('armTfs.sidebar.setMergeSource', async (node?: BranchNode) => this.setMergePath(MERGE_SOURCE_KEY, t('sidebar.prompt.mergeSourcePath'), node?.branch.path)),
      vscode.commands.registerCommand('armTfs.sidebar.setMergeTarget', async (node?: BranchNode) => this.setMergePath(MERGE_TARGET_KEY, t('sidebar.prompt.mergeTargetPath'), node?.branch.path)),
      vscode.commands.registerCommand('armTfs.sidebar.copyTfvcPath', async (node?: BranchNode | MergeTargetOptionNode | string) => this.copyTfvcPath(node)),
      vscode.commands.registerCommand('armTfs.sidebar.pullBranch', async (node?: BranchNode) => this.pullBranch(node)),
      vscode.commands.registerCommand('armTfs.sidebar.checkoutBranch', async (node?: BranchNode) => this.checkoutBranch(node)),
      vscode.commands.registerCommand('armTfs.sidebar.showBranchHistory', async (node?: BranchNode) => this.showBranchHistory(node)),
      vscode.commands.registerCommand('armTfs.sidebar.mergeFromBranch', async (node?: BranchNode) => this.mergeFromBranch(node)),
      vscode.commands.registerCommand('armTfs.sidebar.addBranch', async (node?: BranchNode) => this.addBranch(node)),
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
    const siblingTargets = sourcePath ? await this.getSiblingBranchPaths(sourcePath) : [];
    const items: ArmTfsTreeNode[] = [
      new MergeConfigNode('merge-source', t('sidebar.mergeSource'), sourcePath ?? t('sidebar.selectBranch'), {
        command: 'armTfs.sidebar.setMergeSource',
        title: t('sidebar.setMergeSource'),
      }),
      new MergeConfigNode('merge-target', t('sidebar.mergeTarget'), targetPath ?? t('sidebar.selectBranch'), {
        command: 'armTfs.sidebar.setMergeTarget',
        title: t('sidebar.setMergeTarget'),
      }),
      new MergeConfigNode('merge-swap', t('sidebar.swapMergePaths'), t('sidebar.swapMergePaths.desc'), {
        command: 'armTfs.sidebar.swapMergePaths',
        title: t('sidebar.swapMergePaths.title'),
      }),
    ];

    if (sourcePath && siblingTargets.length) {
      const parentPath = getServerParentPath(sourcePath);
      items.push(new SectionNode(
        t('sidebar.targetBranches'),
        `${siblingTargets.length} under ${parentPath}`,
        siblingTargets.map((candidate) => new MergeTargetOptionNode(
          candidate,
          targetPath !== undefined && normalizeServerPath(candidate) === normalizeServerPath(targetPath),
        )),
      ));
    }

    if (!sourcePath || !targetPath) {
      items.push(new InfoNode(
        t('sidebar.mergeCandidatesUnavailable'),
        siblingTargets.length ? t('sidebar.pickTargetBranch') : t('sidebar.pickSourceAndTarget'),
      ));
      this.mergeProvider.setItems(items);
      return;
    }

    const top = vscode.workspace.getConfiguration('armTfs').get<number>('merge.candidateTop', 20);
    const scan = vscode.workspace.getConfiguration('armTfs').get<number>('merge.candidateScan', 80);

    try {
      const [mergeBase, candidates] = await Promise.all([
        this.client.mergeBase(sourcePath, targetPath),
        this.client.mergeCandidates(sourcePath, targetPath, top, scan),
      ]);

      items.push(...buildMergeSummaryNodes(mergeBase, candidates));
      this.mergeProvider.setItems(items);
    } catch (error) {
      items.push(new InfoNode(t('sidebar.mergeQueryUnavailable'), getErrorMessage(error), {
        command: 'armTfs.sidebar.refresh',
        title: t('sidebar.refreshViews'),
      }));
      this.mergeProvider.setItems(items);
      this.showError('arm-tfs merge query', error);
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
    const anchorPath = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? this.rootPath;
    const discovered = discoverTfvcMappingForPath(anchorPath);
    if (discovered) {
      return cleanServerPath(discovered.serverPath);
    }
    if (!this.workspaceStatus) {
      return undefined;
    }
    const normalizedAnchor = anchorPath ? path.resolve(anchorPath).toLowerCase() : undefined;
    const mapping = this.workspaceStatus.workspace.mappings
      .filter((item) => !normalizedAnchor || normalizedAnchor === path.resolve(item.localPath).toLowerCase()
        || normalizedAnchor.startsWith(`${path.resolve(item.localPath).toLowerCase()}${path.sep}`))
      .sort((left, right) => right.localPath.length - left.localPath.length)[0];
    return mapping?.serverPath ? cleanServerPath(mapping.serverPath) : undefined;
  }

  private async getMergeTargetPath(sourcePath: string | undefined): Promise<string | undefined> {
    const storedTarget = this.getStoredPath(MERGE_TARGET_KEY);
    if (!sourcePath) {
      return storedTarget;
    }

    const sourceParent = getServerParentPath(sourcePath);
    if (!sourceParent || sourceParent === '$/') {
      return undefined;
    }

    if (
      storedTarget
      && normalizeServerPath(storedTarget) !== normalizeServerPath(sourcePath)
      && normalizeServerPath(getServerParentPath(storedTarget) ?? '') === normalizeServerPath(sourceParent)
    ) {
      return storedTarget;
    }

    return undefined;
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

  private async copyTfvcPath(node?: BranchNode | MergeTargetOptionNode | string): Promise<void> {
    const tfvcPath = typeof node === 'string'
      ? node
      : node instanceof BranchNode
        ? node.branch.path
        : node instanceof MergeTargetOptionNode
          ? node.targetPath
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

  private async pullBranch(node?: BranchNode): Promise<void> {
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
        () => this.client.get(mapped.localPath, { recursive: true }, {
          cwdOverride: getCommandCwd(mapped.workspaceRoot, mapped.localPath),
        }),
      );

      const document = await vscode.workspace.openTextDocument({ language: 'text', content: translateCliText(result) });
      await vscode.window.showTextDocument(document, { preview: false });
      await this.refreshScm();
      await this.refreshAll();
    } catch (error) {
      this.showError('arm-tfs pull branch', error);
    }
  }

  private async checkoutBranch(node?: BranchNode): Promise<void> {
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
        () => this.client.checkout([mapped.localPath], true, {
          cwdOverride: getCommandCwd(mapped.workspaceRoot, mapped.localPath),
        }),
      );

      const document = await vscode.workspace.openTextDocument({ language: 'text', content: translateCliText(result) });
      await vscode.window.showTextDocument(document, { preview: false });
      await this.refreshScm();
      await this.refreshAll();
    } catch (error) {
      this.showError('arm-tfs checkout branch', error);
    }
  }

  private async showBranchHistory(node?: BranchNode): Promise<void> {
    if (!node) {
      void vscode.window.showWarningMessage(t('sidebar.warning.selectBranchFromView'));
      return;
    }

    await this.historyBrowser.open(node.branch.path);
  }

  private async mergeFromBranch(node?: BranchNode): Promise<void> {
    if (!node) {
      void vscode.window.showWarningMessage(t('sidebar.warning.selectSourceBranch'));
      return;
    }

    const sourcePath = node.branch.path;
    const siblingTargets = await this.getSiblingBranchPaths(sourcePath);
    const target = await vscode.window.showQuickPick(
      siblingTargets.map((targetPath) => ({
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
      const candidate = await vscode.window.showQuickPick(
        response.items.map((item) => ({
          label: `cs${item.changesetId}`,
          description: buildMergeCandidateDescription(item),
          item,
        })),
        { placeHolder: t('sidebar.merge.pickCandidate') },
      );
      if (!candidate) {
        return;
      }
      await this.executeMergeCandidate(new MergeCandidateNode(
        sourcePath,
        target.targetPath,
        candidate.item.changesetId,
        buildMergeCandidateDescription(candidate.item),
        `${candidate.item.comment ?? ''}\n${candidate.item.createdAt}\n${candidate.item.author?.displayName ?? ''}`.trim(),
      ));
    } catch (error) {
      this.showError('arm-tfs merge', error);
    }
  }

  private async addBranch(node?: BranchNode): Promise<void> {
    if (!node) {
      void vscode.window.showWarningMessage(t('sidebar.warning.selectBranchFromView'));
      return;
    }
    const sourcePath = node.branch.path;
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
      const result = await this.client.diffVersions(serverPath, from, to);
      await showDiffDocument(serverPath, result);
    } catch (error) {
      this.showError('arm-tfs diff versions', error);
    }
  }

  private resolveMappedBranchPath(node: BranchNode | undefined, action: 'pull' | 'checkout'): MappedBranchPath | undefined {
    if (!node) {
      void vscode.window.showWarningMessage(t('sidebar.warning.beforeAction', { action }));
      return undefined;
    }

    if (!this.workspaceStatus || !this.resolvedWorkspaceRoot) {
      void vscode.window.showWarningMessage(t('sidebar.warning.openWorkspaceFirst'));
      return undefined;
    }

    const mapped = resolveMappedLocalPathForServerPath(node.branch.path, this.workspaceStatus, this.resolvedWorkspaceRoot);
    if (!mapped) {
      void vscode.window.showWarningMessage(t('sidebar.warning.branchNotMapped', { path: node.branch.path }));
      return undefined;
    }

    return mapped;
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
      const result = await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: `arm-tfs merge execute cs${node.changesetId}`,
        },
        () => this.client.mergeExecute(node.sourcePath, node.targetPath, node.changesetId, {
          comment: comment.trim() || undefined,
          dryRun: mode.value === 'dryRun',
        }),
      );

      const document = await vscode.workspace.openTextDocument({ language: 'text', content: translateCliText(result) });
      await vscode.window.showTextDocument(document, { preview: false });
      if (mode.value !== 'dryRun') {
        await this.refreshScm();
        await this.refreshAll();
      }
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
          `Changeset: ${item.changesetId}`,
          `Created: ${item.createdAt}`,
          item.author?.displayName ? `Author: ${item.author.displayName}` : undefined,
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
  const parts = [author, item.createdAt.slice(0, 10)].filter(Boolean);
  return parts.join(' | ');
}

function buildMergeCandidateDescription(item: MergeCandidateResponse['items'][number]): string {
  const author = item.author?.displayName ?? 'unknown';
  return `${author} | ${item.createdAt.slice(0, 10)}`;
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
  return path.resolve(targetPath).toLowerCase();
}

function getConfigTarget(): vscode.ConfigurationTarget {
  return vscode.workspace.workspaceFolders?.length ? vscode.ConfigurationTarget.Workspace : vscode.ConfigurationTarget.Global;
}

function getErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : `${error}`;
}

async function showDiffDocument(serverPath: string, diff: DiffResponse): Promise<void> {
  let content: string;
  if (diff.result.kind === 'text') {
    content = diff.result.patch ?? '';
  } else if (diff.result.kind === 'binary') {
    content = `${serverPath}\n\n${t('sidebar.diff.binary')}`;
  } else {
    content = `${serverPath}\n\n${t('sidebar.diff.none')}`;
  }
  const document = await vscode.workspace.openTextDocument({
    language: diff.result.kind === 'text' ? 'diff' : 'text',
    content,
  });
  await vscode.window.showTextDocument(document, { preview: false });
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
      const localPath = suffix ? path.join(mapping.localPath, ...suffix.split('/')) : mapping.localPath;
      const score = mapping.serverPath.length + 1000;
      if (!bestMatch || score > bestMatch.score) {
        bestMatch = { localPath, score };
      }
      continue;
    }

    if (isSameOrChildServerPath(mapping.serverPath, serverPath)) {
      const score = mapping.serverPath.length;
      if (!bestMatch || score > bestMatch.score) {
        bestMatch = { localPath: mapping.localPath, score };
      }
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
      entry.checkinDate ? `Last modified: ${entry.checkinDate.slice(0, 10)}` : undefined,
      entry.contentLength != null ? `Size: ${entry.contentLength} bytes` : undefined,
    ].filter(Boolean).join('\n');
    this.iconPath = entry.isFolder
      ? new vscode.ThemeIcon('folder')
      : new vscode.ThemeIcon('file');
    this.contextValue = entry.isFolder ? 'armTfsServerFolder' : 'armTfsServerFile';
    if (!entry.isFolder) {
      this.command = {
        command: 'armTfs.serverExplorer.showHistory',
        title: 'Show History',
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

  constructor(private readonly client: ArmTfsCliClient, private readonly output: vscode.OutputChannel) {
    this.rootPath = vscode.workspace.getConfiguration('armTfs').get<string>('serverExplorer.rootPath')?.trim() || undefined;
  }

  setRootPath(rootPath: string): void {
    this.rootPath = rootPath;
    this.childCache.clear();
    this.onDidChangeTreeDataEmitter.fire();
  }

  refresh(): void {
    this.childCache.clear();
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

      const cached = this.childCache.get(this.rootPath);
      if (cached) {
        return cached;
      }

      return this.loadChildren(this.rootPath);
    }

    if (element instanceof ServerExplorerNode && element.entry.isFolder) {
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
        label = `Error: ${msg.split('\n')[0].slice(0, 120)}`;
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
    const treeView = vscode.window.createTreeView('armTfs.serverExplorer', { treeDataProvider: this.provider });

    this.disposables.push(
      treeView,
      treeView.onDidChangeSelection((event) => {
        const selected = event.selection[0];
        if (selected instanceof ServerExplorerNode) {
          void this.onActiveServerPathChanged?.(selected.entry.serverPath, {
            branchContext: selected.entry.isFolder,
            syncBranchScope: selected.entry.isFolder,
            syncMergeSource: false,
          });
        }
      }),

      vscode.commands.registerCommand('armTfs.serverExplorer.refresh', () => this.provider.refresh()),

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
        await this.onActiveServerPathChanged?.(trimmed, {
          branchContext: true,
          syncBranchScope: true,
          syncMergeSource: false,
        });
      }),

      vscode.commands.registerCommand('armTfs.serverExplorer.getLatest', async (node?: ServerExplorerNode) => {
        if (!node) {
          return;
        }

        const serverPath = node.entry.serverPath;
        const localPath = await vscode.window.showInputBox({
          prompt: t('serverExplorer.prompt.downloadPath', { path: serverPath }),
          value: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? '.',
          ignoreFocusOut: true,
        });
        if (!localPath) {
          return;
        }

        try {
          const result = await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: `arm-tfs get ${serverPath}` },
            () => checkoutServerPathToLocalFolder(this.client, serverPath, localPath),
          );
          const doc = await vscode.workspace.openTextDocument({ language: 'text', content: translateCliText(result) });
          await vscode.window.showTextDocument(doc, { preview: false });
          await this.refreshScm();
          await this.provider.refresh();
        } catch (error) {
          void vscode.window.showErrorMessage(t('error.failed', {
            title: 'arm-tfs get',
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
            { location: vscode.ProgressLocation.Notification, title: `arm-tfs checkout ${serverPath}` },
            () => this.client.checkout([serverPath]),
          );
          const doc = await vscode.workspace.openTextDocument({ language: 'text', content: translateCliText(result) });
          await vscode.window.showTextDocument(doc, { preview: false });
          await this.refreshScm();
        } catch (error) {
          void vscode.window.showErrorMessage(t('error.failed', {
            title: 'arm-tfs checkout',
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
