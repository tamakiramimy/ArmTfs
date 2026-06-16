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

export class ArmTfsScmController implements vscode.Disposable, vscode.FileDecorationProvider {
  private readonly disposables: vscode.Disposable[] = [];
  private readonly onDidChangeDecorationsEmitter = new vscode.EventEmitter<vscode.Uri[] | undefined>();
  private readonly onDidChangeChangesEmitter = new vscode.EventEmitter<void>();
  private resourcesByPath = new Map<string, ArmTfsResourceState>();
  private refreshInFlight: Promise<void> | undefined;
  private refreshQueued = false;

  // Public, read-only snapshot of the latest scan. Subscribed by the changes view.
  pendingChanges: ArmTfsResourceState[] = [];
  localChanges: ArmTfsResourceState[] = [];
  conflicts: ArmTfsResourceState[] = [];
  untrackedFiles: ArmTfsUntrackedResourceState[] = [];
  lastWorkspaceRoot: string | undefined;
  /** Cached set of TFS-tracked local paths. Invalidated on workspace switch and after add/checkin/undo. */
  private knownPathsCache: Set<string> | undefined;

  readonly onDidChangeFileDecorations = this.onDidChangeDecorationsEmitter.event;
  /** Fires whenever pendingChanges/localChanges/conflicts/untrackedFiles is replaced. */
  readonly onDidChangeChanges = this.onDidChangeChangesEmitter.event;

  constructor(
    private readonly client: ArmTfsCliClient,
    private readonly output: vscode.OutputChannel,
    private readonly rootPath: string | undefined,
  ) {
    // arm-tfs intentionally does NOT register a vscode.SourceControl. All TFS changes are
    // surfaced inside the TFS activity-bar (armTfsHub) tree view, not in VS Code's built-in
    // Source Control panel — keeping arm-tfs separate from git so the two never collide.
  }

  dispose(): void {
    this.onDidChangeDecorationsEmitter.dispose();
    this.onDidChangeChangesEmitter.dispose();
    vscode.Disposable.from(...this.disposables).dispose();
  }

  async initialize(): Promise<void> {
    await this.refresh();
  }

  /**
   * Used to be the SCM resource-group label refresher. Kept for callers but is now a no-op
   * because all label rendering moved to the TreeView under armTfsHub.
   */
  refreshLabels(): void {
    // intentionally empty
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

  async checkout(resource?: ArmTfsResourceState | ArmTfsUntrackedResourceState | vscode.Uri): Promise<void> {
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

  async add(resource?: ArmTfsResourceState | ArmTfsUntrackedResourceState | vscode.Uri): Promise<void> {
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
    let recursive = false;
    try {
      recursive = statSync(targetPath).isDirectory();
    } catch {
      recursive = false;
    }

    // Try once; if the CLI reports [NO MAPPING], auto-register using a configured mapping and retry.
    const result = await this.runTextCommand('arm-tfs add', async () => {
      try {
        return await this.client.add([targetPath], recursive, { cwdOverride });
      } catch (error) {
        if (isNoMappingError(error)) {
          const mapped = await tryAutoRegisterMapping(this.client, targetPath);
          if (mapped) {
            void vscode.window.showInformationMessage(
              t('connections.workspaceMappings.autoRegistered', { serverPath: mapped.serverPath, localPath: mapped.localPath }),
            );
            return this.client.add([targetPath], recursive, { cwdOverride });
          }
        }
        throw error;
      }
    }, true);
    return result as unknown as void;
  }

  async undo(resource?: ArmTfsResourceState | ArmTfsUntrackedResourceState | vscode.Uri): Promise<void> {
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

  async checkin(comment?: string): Promise<void> {
    const workspaceRoot = await findTfvcWorkspaceRoot(this.rootPath);
    if (!workspaceRoot) {
      void vscode.window.showWarningMessage(t('warning.noWorkspace.general'));
      return;
    }

    const finalComment = (comment ?? await vscode.window.showInputBox({
      prompt: t('extension.prompt.checkinComment'),
      ignoreFocusOut: true,
      validateInput: (value) => value.trim() ? undefined : t('warning.checkin.comment'),
    }))?.trim();
    if (!finalComment) {
      return;
    }

    const targetPath = workspaceRoot;
    await this.runTextCommand('arm-tfs checkin', () => this.client.checkin(finalComment, [targetPath], false, false, { cwdOverride: workspaceRoot }), true);
  }

  async openDiff(resource?: ArmTfsResourceState | ArmTfsUntrackedResourceState | vscode.Uri): Promise<void> {
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
          location: vscode.ProgressLocation.Window,
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
    // No longer wired to a SCM QuickDiffProvider. Kept as a utility for the diff command
    // so editor diff lookups can still locate the cached base file when needed.
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
    // Invalidate the items-list cache when the workspace root changes.
    if (this.lastWorkspaceRoot && this.lastWorkspaceRoot !== workspaceRoot) {
      this.knownPathsCache = undefined;
    }
    this.lastWorkspaceRoot = workspaceRoot;

    try {
      const status = await this.client.status(workspaceRoot, true, { cwdOverride: workspaceRoot });
      const pending = status.items.filter((item) => item.state === 'pending').map((item) => new ArmTfsResourceState(item));
      const localChanges = status.items
        .filter((item) => item.state === 'modifiedNotCheckedOut')
        .map((item) => new ArmTfsResourceState(item));
      const conflicts = status.items
        .filter((item) => item.state.toLowerCase().includes('conflict'))
        .map((item) => new ArmTfsResourceState(item));

      // Build the set of paths the CLI knows about. Start with everything `status` returned
      // (those have changes) plus every file the workspace's items list returns (those are
      // already tracked but unchanged — without this we'd misclassify them as untracked).
      const knownPaths = new Set<string>();
      for (const item of status.items) {
        if (item.localPath) {
          knownPaths.add(normalizeLocalPath(item.localPath));
        }
      }
      const trackedPaths = await this.getTrackedPaths(workspaceRoot, status.workspace.mappings);
      for (const p of trackedPaths) {
        knownPaths.add(p);
      }

      const untracked = await scanUntrackedFiles(workspaceRoot, knownPaths);
      const untrackedStates = untracked.map((filePath) => new ArmTfsUntrackedResourceState(filePath));

      this.pendingChanges = pending;
      this.localChanges = localChanges;
      this.conflicts = conflicts;
      this.untrackedFiles = untrackedStates;

      const nextResources = [...pending, ...localChanges, ...conflicts];
      const affectedUris = collectAffectedUris(this.resourcesByPath, nextResources);
      for (const u of untrackedStates) {
        affectedUris.push(u.resourceUri);
      }
      this.resourcesByPath = new Map(nextResources.map((resource) => [normalizeLocalPath(resource.resourceUri.fsPath), resource]));
      this.onDidChangeDecorationsEmitter.fire(affectedUris);
      this.onDidChangeChangesEmitter.fire();
    } catch (error) {
      this.showError('arm-tfs status', error);
    }
  }

  /**
   * Returns every local path the TFS workspace tracks. Pulled via `arm-tfs items list --recursive`
   * for each mapping and translated server-path → local-path. Cached per workspace root to avoid
   * re-fetching the entire tree on every status refresh; invalidate via {@link invalidateTrackedCache}
   * when add/checkin succeeds.
   */
  private async getTrackedPaths(
    workspaceRoot: string,
    mappings: Array<{ serverPath: string; localPath: string }>,
  ): Promise<Set<string>> {
    if (this.knownPathsCache) {
      return this.knownPathsCache;
    }

    const result = new Set<string>();
    for (const mapping of mappings) {
      if (!mapping.serverPath || !mapping.localPath) {
        continue;
      }
      try {
        const listing = await this.client.itemsList(mapping.serverPath, true, { cwdOverride: workspaceRoot });
        const serverRoot = mapping.serverPath.replace(/\/+$/, '');
        for (const entry of listing.items) {
          if (entry.isFolder) {
            continue;
          }
          // Translate server path to local path: serverPath - serverRoot → relative → join localPath
          const relative = entry.serverPath.startsWith(serverRoot + '/')
            ? entry.serverPath.slice(serverRoot.length + 1)
            : entry.serverPath.replace(/^\$\//, '');
          const localFile = path.join(mapping.localPath, ...relative.split('/'));
          result.add(normalizeLocalPath(localFile));
        }
      } catch (error) {
        // Network/CLI errors should not break status; we'll just see more "untracked" files.
        this.output.appendLine(`arm-tfs items list ${mapping.serverPath}: ${error instanceof Error ? error.message : String(error)}`);
      }
    }
    this.knownPathsCache = result;
    return result;
  }

  /**
   * Drop the cached items list. Call after operations that change the set of tracked files
   * (add, checkin, undo of a pending add).
   */
  invalidateTrackedCache(): void {
    this.knownPathsCache = undefined;
  }

  private clearResources(): void {
    const affectedUris = [...this.resourcesByPath.values()].map((resource) => resource.resourceUri);
    this.resourcesByPath.clear();
    this.pendingChanges = [];
    this.localChanges = [];
    this.conflicts = [];
    this.untrackedFiles = [];
    this.onDidChangeDecorationsEmitter.fire(affectedUris);
    this.onDidChangeChangesEmitter.fire();
  }

  private async runTextCommand(title: string, runner: () => Promise<string>, refreshAfter: boolean): Promise<string | undefined> {
    try {
      const result = await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Window,
          title,
        },
        () => runner(),
      );

      if (result.trim()) {
        this.output.appendLine(result.trim());
      }

      vscode.window.setStatusBarMessage(t('status.completed', { title }), 2500);
      if (refreshAfter) {
        // Operations like add/checkin/undo can change the set of tracked files; drop the cache
        // so the next status pass re-fetches the items list.
        this.invalidateTrackedCache();
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

function resolveResourcePath(resource: ArmTfsResourceState | ArmTfsUntrackedResourceState | vscode.Uri | undefined): string | undefined {
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

  // Win UNC or drive-stripped form: //Mac/Home/<user>/... or /Mac/Home/<user>/...
  for (const prefix of ['//Mac/Home/', '/Mac/Home/']) {
    if (normalized.toLowerCase().startsWith(prefix.toLowerCase())) {
      const rest = normalized.slice(prefix.length);
      return process.platform === 'darwin'
        ? path.join('/Users', rest)
        : path.join('C:\\Mac\\Home', ...rest.split('/'));
    }
  }

  // Win drive-letter form: C:/Mac/Home/<user>/... (any drive letter)
  const driveMatch = normalized.match(/^[A-Za-z]:\/(.*)$/);
  if (driveMatch) {
    const withoutDrive = driveMatch[1];
    if (withoutDrive.toLowerCase().startsWith('mac/home/')) {
      const rest = withoutDrive.slice('mac/home/'.length);
      return process.platform === 'darwin'
        ? path.join('/Users', rest)
        : path.join('C:\\Mac\\Home', ...rest.split('/'));
    }
  }

  // macOS path written from the macOS side, opened on Windows
  if (process.platform === 'win32' && normalized.toLowerCase().startsWith('/users/')) {
    const rest = normalized.slice('/Users/'.length);
    return path.join('C:\\Mac\\Home', ...rest.split('/'));
  }

  return localPath;
}

function getPlatformSharedHomeDirectory(): string | undefined {
  // Kept for backward compatibility. Internally translatePlatformSharedPath no longer relies on it.
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
