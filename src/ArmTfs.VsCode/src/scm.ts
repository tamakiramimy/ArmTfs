import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { ArmTfsCliClient, ArmTfsCliError } from './armTfsCliClient';
import type { DiffResponse, StatusItem } from './contracts';
import { t } from './i18n';
import { findTfvcWorkspaceRoot, findTfvcWorkspaceRootSync, getCommandCwd } from './tfvcContext';

export class ArmTfsResourceState implements vscode.SourceControlResourceState {
  readonly resourceUri: vscode.Uri;
  readonly command: vscode.Command;
  readonly decorations: vscode.SourceControlResourceDecorations;
  readonly contextValue: string;

  constructor(public readonly item: StatusItem) {
    this.resourceUri = vscode.Uri.file(item.localPath);
    this.command = {
      command: 'armTfs.openResourceDiff',
      title: t('command.openTfsDiff'),
      arguments: [this],
    };
    this.contextValue = getContextValue(item);
    this.decorations = {
      tooltip: buildTooltip(item),
      strikeThrough: isDeleteLike(item),
      faded: item.state === 'modifiedNotCheckedOut',
    };
  }
}

export class ArmTfsScmController implements vscode.Disposable, vscode.FileDecorationProvider, vscode.QuickDiffProvider {
  private readonly sourceControl: vscode.SourceControl;
  private readonly changesGroup: vscode.SourceControlResourceGroup;
  private readonly localChangesGroup: vscode.SourceControlResourceGroup;
  private readonly conflictsGroup: vscode.SourceControlResourceGroup;
  private readonly disposables: vscode.Disposable[] = [];
  private readonly onDidChangeDecorationsEmitter = new vscode.EventEmitter<vscode.Uri[] | undefined>();
  private resourcesByPath = new Map<string, ArmTfsResourceState>();
  private refreshInFlight: Promise<void> | undefined;
  private refreshQueued = false;

  readonly onDidChangeFileDecorations = this.onDidChangeDecorationsEmitter.event;

  constructor(
    private readonly client: ArmTfsCliClient,
    private readonly output: vscode.OutputChannel,
    private readonly rootPath: string | undefined,
  ) {
    this.sourceControl = vscode.scm.createSourceControl(
      'armTfs',
      'arm-tfs',
      this.rootPath ? vscode.Uri.file(this.rootPath) : undefined,
    );
    this.sourceControl.inputBox.placeholder = t('scm.input.placeholder');
    this.sourceControl.acceptInputCommand = {
      command: 'armTfs.checkinFromScm',
      title: t('command.checkIn'),
    };
    this.sourceControl.quickDiffProvider = this;

    this.changesGroup = this.sourceControl.createResourceGroup('changes', t('scm.group.changes'));
    this.localChangesGroup = this.sourceControl.createResourceGroup('localChanges', t('scm.group.localChanges'));
    this.conflictsGroup = this.sourceControl.createResourceGroup('conflicts', t('scm.group.conflicts'));
  }

  dispose(): void {
    this.onDidChangeDecorationsEmitter.dispose();
    vscode.Disposable.from(this.sourceControl, ...this.disposables).dispose();
  }

  async initialize(): Promise<void> {
    this.refreshLabels();
    await this.refresh();
  }

  refreshLabels(): void {
    this.sourceControl.inputBox.placeholder = t('scm.input.placeholder');
    this.sourceControl.acceptInputCommand = {
      command: 'armTfs.checkinFromScm',
      title: t('command.checkIn'),
    };
    this.changesGroup.label = t('scm.group.changes');
    this.localChangesGroup.label = t('scm.group.localChanges');
    this.conflictsGroup.label = t('scm.group.conflicts');
  }

  async refresh(): Promise<void> {
    if (this.refreshInFlight) {
      this.refreshQueued = true;
      return this.refreshInFlight;
    }

    this.refreshInFlight = this.doRefresh();
    try {
      await this.refreshInFlight;
    } finally {
      this.refreshInFlight = undefined;
      if (this.refreshQueued) {
        this.refreshQueued = false;
        void this.refresh();
      }
    }
  }

  async checkout(resource?: ArmTfsResourceState | vscode.Uri): Promise<void> {
    const targetPath = resolveResourcePath(resource) ?? getActivePath();
    if (!targetPath) {
      void vscode.window.showWarningMessage(t('warning.noFile.checkout'));
      return;
    }

    const workspaceRoot = await findTfvcWorkspaceRoot(targetPath ?? this.rootPath);
    if (!workspaceRoot) {
      void vscode.window.showWarningMessage(t('warning.noWorkspace.file'));
      return;
    }

    await this.runTextCommand('arm-tfs checkout', () => this.client.checkout([targetPath], false, { cwdOverride: getCommandCwd(workspaceRoot, targetPath) }), true);
  }

  async add(resource?: ArmTfsResourceState | vscode.Uri): Promise<void> {
    const targetPath = resolveResourcePath(resource) ?? getActivePath();
    if (!targetPath) {
      void vscode.window.showWarningMessage(t('warning.noFile.add'));
      return;
    }

    const workspaceRoot = await findTfvcWorkspaceRoot(targetPath ?? this.rootPath);
    if (!workspaceRoot) {
      void vscode.window.showWarningMessage(t('warning.noWorkspace.file'));
      return;
    }

    await this.runTextCommand('arm-tfs add', () => this.client.add([targetPath], false, { cwdOverride: getCommandCwd(workspaceRoot, targetPath) }), true);
  }

  async undo(resource?: ArmTfsResourceState | vscode.Uri): Promise<void> {
    const targetPath = resolveResourcePath(resource) ?? getActivePath();
    if (!targetPath) {
      void vscode.window.showWarningMessage(t('warning.noFile.undo'));
      return;
    }

    const workspaceRoot = await findTfvcWorkspaceRoot(targetPath ?? this.rootPath);
    if (!workspaceRoot) {
      void vscode.window.showWarningMessage(t('warning.noWorkspace.file'));
      return;
    }

    await this.runTextCommand('arm-tfs undo', () => this.client.undo([targetPath], false, { cwdOverride: getCommandCwd(workspaceRoot, targetPath) }), true);
  }

  async checkin(): Promise<void> {
    const workspaceRoot = await findTfvcWorkspaceRoot(this.rootPath);
    if (!workspaceRoot) {
      void vscode.window.showWarningMessage(t('warning.noWorkspace.general'));
      return;
    }

    const comment = this.sourceControl.inputBox.value.trim();
    if (!comment) {
      void vscode.window.showWarningMessage(t('warning.checkin.comment'));
      return;
    }

    const targetPath = workspaceRoot;
    const result = await this.runTextCommand('arm-tfs checkin', () => this.client.checkin(comment, [targetPath], false, false, { cwdOverride: workspaceRoot }), true);
    if (result) {
      this.sourceControl.inputBox.value = '';
    }
  }

  async openDiff(resource?: ArmTfsResourceState | vscode.Uri): Promise<void> {
    const targetPath = resolveResourcePath(resource) ?? getActivePath();
    if (!targetPath) {
      void vscode.window.showWarningMessage(t('warning.noFile.diff'));
      return;
    }

    const item = targetPath ? this.resourcesByPath.get(normalizeLocalPath(targetPath))?.item : undefined;
    if (item?.changeType === 'add') {
      await vscode.commands.executeCommand('vscode.open', vscode.Uri.file(targetPath));
      void vscode.window.showInformationMessage(t('info.diff.added'));
      return;
    }

    if (item && isDeleteLike(item)) {
      void vscode.window.showInformationMessage(t('info.diff.deleted'));
      return;
    }

    try {
      const workspaceRoot = await findTfvcWorkspaceRoot(targetPath ?? this.rootPath);
      if (!workspaceRoot) {
        void vscode.window.showWarningMessage(t('warning.noWorkspace.file'));
        return;
      }

      const diff = await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.SourceControl,
          title: `arm-tfs diff ${path.basename(targetPath)}`,
        },
        () => this.client.diff(targetPath, { useBase: shouldUseBase(item) }, { cwdOverride: getCommandCwd(workspaceRoot, targetPath) }),
      );

      await this.showDiffResult(targetPath, diff);
    } catch (error) {
      this.showError('arm-tfs diff', error);
    }
  }

  provideFileDecoration(uri: vscode.Uri): vscode.ProviderResult<vscode.FileDecoration> {
    const item = this.resourcesByPath.get(normalizeLocalPath(uri.fsPath))?.item;
    if (!item) {
      return undefined;
    }

    return {
      badge: getBadge(item),
      tooltip: buildTooltip(item),
      color: getDecorationColor(item),
      propagate: false,
    };
  }

  provideOriginalResource(uri: vscode.Uri): vscode.ProviderResult<vscode.Uri> {
    const workspaceRoot = findTfvcWorkspaceRootSync(uri.fsPath) ?? (this.rootPath ? findTfvcWorkspaceRootSync(this.rootPath) : undefined);
    if (!workspaceRoot) {
      return undefined;
    }

    const baseFilePath = getBaseFilePath(workspaceRoot, uri.fsPath);
    return existsSync(baseFilePath) ? vscode.Uri.file(baseFilePath) : undefined;
  }

  private async doRefresh(): Promise<void> {
    const workspaceRoot = await findTfvcWorkspaceRoot(this.rootPath);
    if (!workspaceRoot) {
      this.clearResources();
      return;
    }

    try {
      const status = await this.client.status(workspaceRoot, true, { cwdOverride: workspaceRoot });
      const pending = status.items.filter((item) => item.state === 'pending').map((item) => new ArmTfsResourceState(item));
      const localChanges = status.items
        .filter((item) => item.state === 'modifiedNotCheckedOut')
        .map((item) => new ArmTfsResourceState(item));
      const conflicts = status.items
        .filter((item) => item.state.toLowerCase().includes('conflict'))
        .map((item) => new ArmTfsResourceState(item));

      this.changesGroup.resourceStates = pending;
      this.localChangesGroup.resourceStates = localChanges;
      this.conflictsGroup.resourceStates = conflicts;
      this.sourceControl.count = pending.length + localChanges.length + conflicts.length;

      const nextResources = [...pending, ...localChanges, ...conflicts];
      const affectedUris = collectAffectedUris(this.resourcesByPath, nextResources);
      this.resourcesByPath = new Map(nextResources.map((resource) => [normalizeLocalPath(resource.resourceUri.fsPath), resource]));
      this.onDidChangeDecorationsEmitter.fire(affectedUris);
    } catch (error) {
      this.showError('arm-tfs status', error);
    }
  }

  private clearResources(): void {
    const affectedUris = [...this.resourcesByPath.values()].map((resource) => resource.resourceUri);
    this.resourcesByPath.clear();
    this.changesGroup.resourceStates = [];
    this.localChangesGroup.resourceStates = [];
    this.conflictsGroup.resourceStates = [];
    this.sourceControl.count = 0;
    this.onDidChangeDecorationsEmitter.fire(affectedUris);
  }

  private async runTextCommand(title: string, runner: () => Promise<string>, refreshAfter: boolean): Promise<string | undefined> {
    try {
      const result = await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.SourceControl,
          title,
        },
        () => runner(),
      );

      if (result.trim()) {
        this.output.appendLine(result.trim());
      }

      vscode.window.setStatusBarMessage(t('status.completed', { title }), 2500);
      if (refreshAfter) {
        await this.refresh();
      }

      return result;
    } catch (error) {
      this.showError(title, error);
      return undefined;
    }
  }

  private async showDiffResult(targetPath: string, diff: DiffResponse): Promise<void> {
    const content = buildDiffDocument(targetPath, diff);
    const document = await vscode.workspace.openTextDocument({
      language: diff.result.kind === 'text' ? 'diff' : 'text',
      content,
    });
    await vscode.window.showTextDocument(document, { preview: false });
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
      void vscode.window.showErrorMessage(t('error.failed', { title, message: error.message }));
      return;
    }

    const message = error instanceof Error ? error.message : `${error}`;
    this.output.appendLine(message);
    void vscode.window.showErrorMessage(t('error.failed', { title, message }));
  }
}

function collectAffectedUris(
  previous: Map<string, ArmTfsResourceState>,
  next: ArmTfsResourceState[],
): vscode.Uri[] {
  const affected = new Map<string, vscode.Uri>();
  for (const resource of previous.values()) {
    affected.set(normalizeLocalPath(resource.resourceUri.fsPath), resource.resourceUri);
  }
  for (const resource of next) {
    affected.set(normalizeLocalPath(resource.resourceUri.fsPath), resource.resourceUri);
  }

  return [...affected.values()];
}

function resolveResourcePath(resource: ArmTfsResourceState | vscode.Uri | undefined): string | undefined {
  if (!resource) {
    return undefined;
  }

  if (resource instanceof vscode.Uri) {
    return resource.fsPath;
  }

  return resource.resourceUri.fsPath;
}

function shouldUseBase(item: StatusItem | undefined): boolean {
  return item?.baselineChangesetId !== undefined || item?.trackedChangesetId !== undefined || item?.state === 'modifiedNotCheckedOut';
}

function buildDiffDocument(targetPath: string, diff: DiffResponse): string {
  if (diff.result.kind === 'none') {
    return `${targetPath}\n\nNo differences found against the selected TFVC base.`;
  }

  if (diff.result.kind === 'binary') {
    return [
      targetPath,
      '',
      'Binary files differ.',
      `Local size: ${diff.result.localSize} byte(s)`,
      `Server size: ${diff.result.serverSize} byte(s)`,
    ].join('\n');
  }

  return diff.result.patch?.trim() || `${targetPath}\n\nNo textual differences were returned.`;
}

function buildTooltip(item: StatusItem): string {
  const parts = [`State: ${item.state}`];
  if (item.changeType) {
    parts.push(`Change: ${item.changeType}`);
  }
  parts.push(`Server: ${item.serverPath}`);
  if (item.trackedChangesetId !== undefined) {
    parts.push(`Tracked changeset: ${item.trackedChangesetId}`);
  }
  if (item.baselineChangesetId !== undefined) {
    parts.push(`Baseline changeset: ${item.baselineChangesetId}`);
  }
  return parts.join('\n');
}

function getContextValue(item: StatusItem): string {
  if (item.state.toLowerCase().includes('conflict')) {
    return 'armTfsConflict';
  }

  if (item.state === 'modifiedNotCheckedOut') {
    return 'armTfsModifiedNotCheckedOut';
  }

  if (item.state !== 'pending') {
    return 'armTfsResource';
  }

  switch ((item.changeType ?? '').toLowerCase()) {
    case 'add':
      return 'armTfsPendingAdd';
    case 'delete':
      return 'armTfsPendingDelete';
    case 'rename':
      return 'armTfsPendingRename';
    case 'undelete':
      return 'armTfsPendingUndelete';
    case 'edit':
      return 'armTfsPendingEdit';
    default:
      return 'armTfsPending';
  }
}

function getBadge(item: StatusItem): string {
  if (item.state.toLowerCase().includes('conflict')) {
    return '!';
  }

  if (item.state === 'modifiedNotCheckedOut') {
    return 'M';
  }

  switch ((item.changeType ?? '').toLowerCase()) {
    case 'add':
      return 'A';
    case 'delete':
      return 'D';
    case 'rename':
      return 'R';
    case 'undelete':
      return 'U';
    case 'edit':
      return 'M';
    default:
      return 'T';
  }
}

function getDecorationColor(item: StatusItem): vscode.ThemeColor {
  if (item.state.toLowerCase().includes('conflict')) {
    return new vscode.ThemeColor('gitDecoration.conflictingResourceForeground');
  }

  if (item.state === 'modifiedNotCheckedOut') {
    return new vscode.ThemeColor('gitDecoration.modifiedResourceForeground');
  }

  switch ((item.changeType ?? '').toLowerCase()) {
    case 'add':
      return new vscode.ThemeColor('gitDecoration.addedResourceForeground');
    case 'delete':
      return new vscode.ThemeColor('gitDecoration.deletedResourceForeground');
    case 'rename':
      return new vscode.ThemeColor('gitDecoration.modifiedResourceForeground');
    default:
      return new vscode.ThemeColor('gitDecoration.modifiedResourceForeground');
  }
}

function isDeleteLike(item: StatusItem): boolean {
  return item.changeType?.toLowerCase() === 'delete';
}

function getBaseFilePath(rootPath: string, localPath: string): string {
  const normalizedPath = normalizeLocalPath(localPath).toLowerCase();
  const hash = createHash('sha256').update(normalizedPath).digest('hex').toUpperCase().slice(0, 16);
  return path.join(rootPath, '.tf', 'base', `${hash}${path.extname(normalizedPath)}`);
}

function normalizeLocalPath(localPath: string): string {
  const fullPath = path.resolve(localPath);
  if (process.platform === 'darwin' && fullPath.startsWith('/private/tmp')) {
    return `/tmp${fullPath.slice('/private/tmp'.length)}`;
  }
  return fullPath;
}

function getActivePath(): string | undefined {
  const activeUri = vscode.window.activeTextEditor?.document.uri;
  return activeUri?.scheme === 'file' ? activeUri.fsPath : undefined;
}
