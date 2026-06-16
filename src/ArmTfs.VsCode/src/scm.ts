import { createHash } from 'node:crypto';
import { existsSync, readdirSync, statSync } from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { ArmTfsCliClient, ArmTfsCliError } from './armTfsCliClient';
import type { StatusItem } from './contracts';
import { t, translateChangeType, translateStatusLabel } from './i18n';
import { findConfiguredMappingForLocalPath, findTfvcWorkspaceRoot, findTfvcWorkspaceRootSync, getCommandCwd } from './tfvcContext';
import { openLocalWorkingDiff } from './versionedFiles';

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

/**
 * Resource state for a local file that exists on disk but is unknown to TFS.
 * Surfaced in the "Untracked" group so the user can add it to TFS with one click,
 * preventing the file from being picked up by git's source control instead.
 */
export class ArmTfsUntrackedResourceState implements vscode.SourceControlResourceState {
  readonly resourceUri: vscode.Uri;
  readonly command: vscode.Command;
  readonly decorations: vscode.SourceControlResourceDecorations;
  readonly contextValue: string = 'armTfsUntracked';

  constructor(public readonly localPath: string) {
    this.resourceUri = vscode.Uri.file(localPath);
    this.command = {
      command: 'vscode.open',
      title: 'Open',
      arguments: [this.resourceUri],
    };
    this.decorations = {
      tooltip: t('scm.tooltip.untracked'),
      strikeThrough: false,
      faded: true,
    };
  }
}

export class ArmTfsScmController implements vscode.Disposable, vscode.FileDecorationProvider, vscode.QuickDiffProvider {
  private readonly sourceControl: vscode.SourceControl;
  private readonly changesGroup: vscode.SourceControlResourceGroup;
  private readonly localChangesGroup: vscode.SourceControlResourceGroup;
  private readonly conflictsGroup: vscode.SourceControlResourceGroup;
  private readonly untrackedGroup: vscode.SourceControlResourceGroup;
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
    this.untrackedGroup = this.sourceControl.createResourceGroup('untracked', t('scm.group.untracked'));
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
    this.untrackedGroup.label = t('scm.group.untracked');
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

    const cwdOverride = getCommandCwd(workspaceRoot, targetPath);

    // Try once; if the CLI reports [NO MAPPING], auto-register using a configured mapping and retry.
    const result = await this.runTextCommand('arm-tfs add', async () => {
      try {
        return await this.client.add([targetPath], false, { cwdOverride });
      } catch (error) {
        if (isNoMappingError(error)) {
          const mapped = await tryAutoRegisterMapping(this.client, targetPath);
          if (mapped) {
            void vscode.window.showInformationMessage(
              t('connections.workspaceMappings.autoRegistered', { serverPath: mapped.serverPath, localPath: mapped.localPath }),
            );
            return this.client.add([targetPath], false, { cwdOverride });
          }
        }
        throw error;
      }
    }, true);
    return result as unknown as void;
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

      const version = shouldUseBase(item)
        ? item?.baselineChangesetId ?? item?.trackedChangesetId
        : undefined;

      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.SourceControl,
          title: `arm-tfs diff ${path.basename(targetPath)}`,
        },
        () => openLocalWorkingDiff(
          this.client,
          targetPath,
          item?.serverPath ?? targetPath,
          {
            version,
            title: `${path.basename(targetPath)} (${version ? `cs${version}` : t('version.server')}) ↔ ${path.basename(targetPath)} (${t('version.workingTree')})`,
          },
        ),
      );
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

      // Build a set of all paths the CLI already knows about so we don't double-count them
      // when scanning for untracked files.
      const knownPaths = new Set<string>();
      for (const item of status.items) {
        if (item.localPath) {
          knownPaths.add(normalizeLocalPath(item.localPath));
        }
      }

      const untracked = await scanUntrackedFiles(workspaceRoot, knownPaths);
      const untrackedStates = untracked.map((filePath) => new ArmTfsUntrackedResourceState(filePath));

      this.changesGroup.resourceStates = pending;
      this.localChangesGroup.resourceStates = localChanges;
      this.conflictsGroup.resourceStates = conflicts;
      this.untrackedGroup.resourceStates = untrackedStates;
      this.sourceControl.count = pending.length + localChanges.length + conflicts.length + untrackedStates.length;

      const nextResources = [...pending, ...localChanges, ...conflicts];
      const affectedUris = collectAffectedUris(this.resourcesByPath, nextResources);
      // Also notify decorations for untracked files
      for (const u of untrackedStates) {
        affectedUris.push(u.resourceUri);
      }
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
    this.untrackedGroup.resourceStates = [];
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

function buildTooltip(item: StatusItem): string {
  const parts = [t('scm.tooltip.state', { state: translateStatusLabel(item.state) })];
  if (item.changeType) {
    parts.push(t('scm.tooltip.change', { change: translateChangeType(item.changeType) }));
  }
  parts.push(t('scm.tooltip.server', { path: item.serverPath }));
  if (item.trackedChangesetId !== undefined) {
    parts.push(t('scm.tooltip.trackedChangeset', { changeset: item.trackedChangesetId }));
  }
  if (item.baselineChangesetId !== undefined) {
    parts.push(t('scm.tooltip.baselineChangeset', { changeset: item.baselineChangesetId }));
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
  const fullPath = path.resolve(translatePlatformSharedPath(localPath));
  if (process.platform === 'darwin' && fullPath.startsWith('/private/tmp')) {
    return `/tmp${fullPath.slice('/private/tmp'.length)}`;
  }
  return fullPath;
}

function translatePlatformSharedPath(localPath: string): string {
  if (process.platform !== 'darwin' && process.platform !== 'win32') {
    return localPath;
  }

  const normalized = localPath.replace(/\\/g, '/');
  const home = getPlatformSharedHomeDirectory();
  if (!home) {
    return localPath;
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

  return localPath;
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

/**
 * Walk the workspace root looking for files that exist locally but TFS does not know
 * about (i.e. not in `arm-tfs status` results). Excludes well-known noise directories
 * (.tf/.git/node_modules/bin/obj/out/dist), and caps the result to keep the scan cheap.
 *
 * This is what makes new files show up in the arm-tfs SCM panel before the user runs
 * `add`. Without it, the file falls through to git's source control panel instead.
 */
export async function scanUntrackedFiles(
  workspaceRoot: string,
  knownPaths: Set<string>,
  options?: { maxResults?: number; maxDepth?: number },
): Promise<string[]> {
  const maxResults = options?.maxResults ?? 500;
  const maxDepth = options?.maxDepth ?? 12;
  const results: string[] = [];
  const exclude = new Set(['.tf', '.git', 'node_modules', 'bin', 'obj', 'out', 'dist', '.vs', '.idea', '.next', 'target', '.gradle']);

  const walk = (directory: string, depth: number): boolean => {
    if (depth > maxDepth || results.length >= maxResults) {
      return results.length >= maxResults;
    }
    let entries;
    try {
      entries = readdirSync(directory, { withFileTypes: true });
    } catch {
      return false;
    }
    for (const entry of entries) {
      if (results.length >= maxResults) {
        return true;
      }
      if (entry.name.startsWith('.') && entry.name !== '.editorconfig' && entry.name !== '.gitignore') {
        // Skip hidden files/dirs (incl. .git, .tf, .vscode, .DS_Store, etc.)
        // .editorconfig and .gitignore are useful committed config — keep them visible.
        if (entry.name !== '.vscode') {
          continue;
        }
      }
      if (entry.isDirectory()) {
        if (exclude.has(entry.name)) {
          continue;
        }
        if (walk(path.join(directory, entry.name), depth + 1)) {
          return true;
        }
      } else if (entry.isFile()) {
        const fullPath = path.join(directory, entry.name);
        if (!knownPaths.has(normalizeLocalPath(fullPath))) {
          results.push(fullPath);
        }
      }
    }
    return false;
  };

  walk(workspaceRoot, 0);
  return results;
}

/**
 * Returns true when the CLI error output contains the [NO MAPPING] marker, which
 * means the target file's local path is not covered by any registered workspace mapping.
 */
export function isNoMappingError(error: unknown): boolean {
  if (error instanceof ArmTfsCliError) {
    return error.stdout.includes('[NO MAPPING]') || error.stderr.includes('[NO MAPPING]') || error.message.includes('[NO MAPPING]');
  }
  if (error instanceof Error) {
    return error.message.includes('[NO MAPPING]');
  }
  return false;
}

/**
 * When arm-tfs reports [NO MAPPING] for a local file, look up a matching entry in
 * `armTfs.workspaceMappings` and call `workspace map` to register it with the TFS
 * workspace. Returns the mapping that was registered, or undefined if none applies.
 */
export async function tryAutoRegisterMapping(
  client: ArmTfsCliClient,
  targetPath: string,
): Promise<{ serverPath: string; localPath: string } | undefined> {
  const mapping = findConfiguredMappingForLocalPath(targetPath);
  if (!mapping) {
    return undefined;
  }
  try {
    await client.workspaceMap(mapping.serverPath, mapping.localPath);
    return mapping;
  } catch {
    return undefined;
  }
}

function getActivePath(): string | undefined {
  const activeUri = vscode.window.activeTextEditor?.document.uri;
  return activeUri?.scheme === 'file' ? activeUri.fsPath : undefined;
}
