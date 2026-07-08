import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { ArmTfsCliClient, ArmTfsCliError } from './armTfsCliClient';
import type { StatusItem } from './contracts';
import { t, translateChangeType, translateStatusLabel } from './i18n';
import { addIgnorePatternForPath, type IgnoreMatcher, loadIgnoreMatcher } from './ignore';
import { findConfiguredMappingForLocalPath, findTfvcWorkspaceRoot, findTfvcWorkspaceRootSync, getCommandCwd } from './tfvcContext';
import { openLocalWorkingDiff, openLocalWorkingDiffFromEmpty } from './versionedFiles';

interface TrackedPathSnapshot {
  paths: Set<string>;
  reliable: boolean;
}

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
      command: 'armTfs.openResourceDiff',
      title: t('command.openTfsDiff'),
      arguments: [this],
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
  private excludedCheckinPaths = new Set<string>();
  private excludedCheckinWorkspaceRoot: string | undefined;

  // Public, read-only snapshot of the latest scan. Subscribed by the changes view.
  pendingChanges: ArmTfsResourceState[] = [];
  localChanges: ArmTfsResourceState[] = [];
  conflicts: ArmTfsResourceState[] = [];
  untrackedFiles: ArmTfsUntrackedResourceState[] = [];
  lastWorkspaceRoot: string | undefined;
  /** Cached set of TFS-tracked relative paths. Invalidated on workspace switch and after add/checkin/undo. */
  private knownPathsCache: TrackedPathSnapshot | undefined;

  readonly onDidChangeFileDecorations = this.onDidChangeDecorationsEmitter.event;
  /** Fires whenever pendingChanges/localChanges/conflicts/untrackedFiles is replaced. */
  readonly onDidChangeChanges = this.onDidChangeChangesEmitter.event;

  constructor(
    private readonly client: ArmTfsCliClient,
    private readonly output: vscode.OutputChannel,
    private readonly rootPath: string | undefined,
    private readonly workspaceState?: vscode.Memento,
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

  isExcludedFromCheckin(resource: ArmTfsResourceState | ArmTfsUntrackedResourceState | vscode.Uri | string): boolean {
    const targetPath = typeof resource === 'string' ? resource : resolveResourcePath(resource);
    if (!targetPath) {
      return false;
    }

    const workspaceRoot = this.lastWorkspaceRoot
      ?? findTfvcWorkspaceRootSync(targetPath)
      ?? (this.rootPath ? findTfvcWorkspaceRootSync(this.rootPath) : undefined);
    if (!workspaceRoot || this.excludedCheckinWorkspaceRoot !== workspaceRoot) {
      return false;
    }

    return this.excludedCheckinPaths.has(getCheckinSelectionKey(workspaceRoot, targetPath));
  }

  getIncludedPendingChangeCount(): number {
    return this.pendingChanges.filter((resource) => !this.isExcludedFromCheckin(resource)).length;
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

  async addAllUntracked(): Promise<void> {
    const paths = this.untrackedFiles.map((resource) => resource.localPath);
    if (!paths.length) {
      void vscode.window.showInformationMessage(t('changesView.bulkAdd.none'));
      return;
    }

    const workspaceRoot = await findTfvcWorkspaceRoot(paths[0] ?? this.rootPath);
    if (!workspaceRoot) {
      void vscode.window.showWarningMessage(t('warning.noWorkspace.general'));
      return;
    }

    await this.runTextCommand(
      t('changesView.bulkAdd.title'),
      () => runPathBatches(paths, (batch) => this.client.add(batch, false, { cwdOverride: workspaceRoot })),
      true,
    );
  }

  async ignore(resource?: ArmTfsResourceState | ArmTfsUntrackedResourceState | vscode.Uri): Promise<void> {
    const targetPath = resolveResourcePath(resource) ?? getActivePath();
    if (!targetPath) {
      void vscode.window.showWarningMessage(t('warning.noFile.ignore'));
      return;
    }

    const workspaceRoot = await findTfvcWorkspaceRoot(targetPath ?? this.rootPath);
    if (!workspaceRoot) {
      void vscode.window.showWarningMessage(t('warning.noWorkspace.file'));
      return;
    }

    const item = resource instanceof ArmTfsResourceState
      ? resource.item
      : this.resourcesByPath.get(normalizeLocalPath(targetPath))?.item;
    const isPending = item?.state === 'pending';

    await this.runTextCommand(
      t('changesView.ignore.title'),
      async () => {
        const outputs: string[] = [];
        if (isPending) {
          outputs.push(await this.client.undo([targetPath], true, { cwdOverride: getCommandCwd(workspaceRoot, targetPath) }));
        }

        const result = addIgnorePatternForPath(workspaceRoot, targetPath);
        outputs.push(result.added
          ? t('changesView.ignore.added', { pattern: result.pattern, file: result.filePath })
          : t('changesView.ignore.already', { pattern: result.pattern, file: result.filePath }));
        return outputs.filter((item) => item.trim()).join('\n');
      },
      true,
    );
  }

  async stage(resource?: ArmTfsResourceState | ArmTfsUntrackedResourceState | vscode.Uri): Promise<void> {
    if (resource instanceof ArmTfsResourceState && resource.item.state === 'modifiedNotCheckedOut') {
      return this.checkout(resource);
    }
    return this.add(resource);
  }

  async stageAllWorkingChanges(): Promise<void> {
    const modifiedPaths = this.localChanges.map((resource) => resource.resourceUri.fsPath);
    const untrackedPaths = this.untrackedFiles.map((resource) => resource.localPath);
    if (!modifiedPaths.length && !untrackedPaths.length) {
      void vscode.window.showInformationMessage(t('changesView.stageAll.none'));
      return;
    }

    const firstPath = modifiedPaths[0] ?? untrackedPaths[0] ?? this.rootPath;
    const workspaceRoot = await findTfvcWorkspaceRoot(firstPath);
    if (!workspaceRoot) {
      void vscode.window.showWarningMessage(t('warning.noWorkspace.general'));
      return;
    }

    await this.runTextCommand(
      t('changesView.stageAll.title'),
      async () => {
        const outputs: string[] = [];
        if (modifiedPaths.length) {
          outputs.push(await runPathBatches(modifiedPaths, (batch) => this.client.checkout(batch, false, { cwdOverride: workspaceRoot })));
        }
        if (untrackedPaths.length) {
          outputs.push(await runPathBatches(untrackedPaths, (batch) => this.client.add(batch, false, { cwdOverride: workspaceRoot })));
        }
        return outputs.filter((item) => item.trim()).join('\n');
      },
      true,
    );
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

    const item = resource instanceof ArmTfsResourceState
      ? resource.item
      : await this.resolveStatusItemForPath(targetPath, workspaceRoot);
    if (!item || (item.state !== 'pending' && !item.state.toLowerCase().includes('conflict'))) {
      void vscode.window.showInformationMessage(t('changesView.undo.none'));
      return;
    }

    await this.runTextCommand(
      t('changesView.undo.title'),
      () => this.client.undo([targetPath], true, { cwdOverride: getCommandCwd(workspaceRoot, targetPath) }),
      true,
    );
  }

  async unstage(resource?: ArmTfsResourceState | ArmTfsUntrackedResourceState | vscode.Uri): Promise<void> {
    return this.excludeFromCheckin(resource);
  }

  async excludeFromCheckin(resource?: ArmTfsResourceState | ArmTfsUntrackedResourceState | vscode.Uri): Promise<void> {
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

    await this.ensureExcludedCheckinStateLoaded(workspaceRoot);
    const item = resource instanceof ArmTfsResourceState
      ? resource.item
      : await this.resolveStatusItemForPath(targetPath, workspaceRoot);
    if (!item || (item.state !== 'pending' && !item.state.toLowerCase().includes('conflict'))) {
      void vscode.window.showInformationMessage(t('changesView.exclude.none'));
      return;
    }

    const key = getCheckinSelectionKey(workspaceRoot, targetPath);
    if (this.excludedCheckinPaths.has(key)) {
      return;
    }

    this.excludedCheckinPaths.add(key);
    await this.persistExcludedCheckinState(workspaceRoot);
    this.onDidChangeChangesEmitter.fire();
    vscode.window.setStatusBarMessage(t('status.completed', { title: t('changesView.exclude.title') }), 2500);
  }

  async stageCheckin(resource?: ArmTfsResourceState | ArmTfsUntrackedResourceState | vscode.Uri): Promise<void> {
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

    await this.ensureExcludedCheckinStateLoaded(workspaceRoot);
    const key = getCheckinSelectionKey(workspaceRoot, targetPath);
    if (!this.excludedCheckinPaths.has(key)) {
      return;
    }

    this.excludedCheckinPaths.delete(key);
    await this.persistExcludedCheckinState(workspaceRoot);
    this.onDidChangeChangesEmitter.fire();
    vscode.window.setStatusBarMessage(t('status.completed', { title: t('changesView.include.title') }), 2500);
  }

  async undoPendingAdds(): Promise<void> {
    return this.unstageAllPendingChanges();
  }

  async unstageAllPendingChanges(): Promise<void> {
    const paths = this.pendingChanges.map((resource) => resource.resourceUri.fsPath);

    if (!paths.length) {
      void vscode.window.showInformationMessage(t('changesView.unstageAll.none'));
      return;
    }

    const workspaceRoot = await findTfvcWorkspaceRoot(paths[0] ?? this.rootPath);
    if (!workspaceRoot) {
      void vscode.window.showWarningMessage(t('warning.noWorkspace.general'));
      return;
    }

    await this.runTextCommand(
      t('changesView.unstageAll.title'),
      () => runPathBatches(paths, (batch) => this.client.undo(batch, true, { cwdOverride: workspaceRoot })),
      true,
    );
  }

  async discardPendingChange(resource?: ArmTfsResourceState | ArmTfsUntrackedResourceState | vscode.Uri): Promise<void> {
    return this.revertLocalChange(resource);
  }

  async revertLocalChange(resource?: ArmTfsResourceState | ArmTfsUntrackedResourceState | vscode.Uri): Promise<void> {
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

    const item = resource instanceof ArmTfsResourceState
      ? resource.item
      : await this.resolveStatusItemForPath(targetPath, workspaceRoot);
    if (!item) {
      const serverPath = await this.resolveServerPathForLocalFile(targetPath, workspaceRoot);
      if (!serverPath) {
        void vscode.window.showInformationMessage(t('changesView.discard.untracked'));
        return;
      }
    }

    const choice = await vscode.window.showWarningMessage(
      t('changesView.discard.confirm', { file: path.basename(targetPath) }),
      { modal: true, detail: t('changesView.discard.detail', { path: targetPath }) },
      t('changesView.discard.action'),
    );
    if (choice !== t('changesView.discard.action')) {
      return;
    }

    const cwdOverride = getCommandCwd(workspaceRoot, targetPath);

    await this.runTextCommand(t('changesView.discard.title'), async () => {
      if (item?.state === 'pending' || item?.state.toLowerCase().includes('conflict')) {
        return this.client.undo([targetPath], false, { cwdOverride });
      }

      return this.client.get(targetPath, { force: true, recursive: false }, { cwdOverride });
    }, true);
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

    await this.ensureExcludedCheckinStateLoaded(workspaceRoot);

    const pendingPaths = this.pendingChanges
      .map((resource) => resource.resourceUri.fsPath)
      .filter((resourcePath) => !this.excludedCheckinPaths.has(getCheckinSelectionKey(workspaceRoot, resourcePath)));
    if (!this.pendingChanges.length) {
      void vscode.window.showInformationMessage(t('changesView.checkin.none'));
      return;
    }
    if (!pendingPaths.length) {
      void vscode.window.showInformationMessage(t('changesView.checkin.noneIncluded'));
      return;
    }

    await this.runTextCommand('arm-tfs checkin', () => this.client.checkin(finalComment, pendingPaths, false, false, { cwdOverride: workspaceRoot }), true);
  }

  async openDiff(resource?: ArmTfsResourceState | ArmTfsUntrackedResourceState | vscode.Uri): Promise<void> {
    const targetPath = resolveResourcePath(resource) ?? getActivePath();
    if (!targetPath) {
      void vscode.window.showWarningMessage(t('warning.noFile.diff'));
      return;
    }

    const item = targetPath ? this.resourcesByPath.get(normalizeLocalPath(targetPath))?.item : undefined;
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

      const serverPath = await this.resolveServerPathForLocalFile(targetPath, workspaceRoot, item);
      const title = `${path.basename(targetPath)} (${t('version.latest')}) ↔ ${path.basename(targetPath)} (${t('version.workingTree')})`;

      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Window,
          title: `arm-tfs diff ${path.basename(targetPath)}`,
        },
        async () => {
          if (!serverPath) {
            await openLocalWorkingDiffFromEmpty(targetPath, { title });
            void vscode.window.showInformationMessage(t('info.diff.noServerVersion'));
            return;
          }

          try {
            await openLocalWorkingDiff(
              this.client,
              targetPath,
              serverPath,
              { title },
            );
          } catch (error) {
            if (!isServerItemNotFoundError(error)) {
              throw error;
            }

            await openLocalWorkingDiffFromEmpty(targetPath, { title });
            void vscode.window.showInformationMessage(t('info.diff.noServerVersion'));
          }
        },
      );
    } catch (error) {
      this.showError('arm-tfs diff', error);
    }
  }

  private async resolveServerPathForLocalFile(
    localPath: string,
    workspaceRoot: string,
    item?: StatusItem,
  ): Promise<string | undefined> {
    if (item?.serverPath) {
      return item.serverPath;
    }

    try {
      const status = await this.client.status(workspaceRoot, false, { cwdOverride: workspaceRoot });
      return resolveLocalPathToServerPath(localPath, workspaceRoot, status.workspace.mappings);
    } catch (error) {
      this.output.appendLine(`arm-tfs resolve server path ${localPath}: ${error instanceof Error ? error.message : String(error)}`);
      return undefined;
    }
  }

  private async resolveStatusItemForPath(localPath: string, workspaceRoot: string): Promise<StatusItem | undefined> {
    const cached = this.resourcesByPath.get(normalizeLocalPath(localPath))?.item;
    if (cached) {
      return cached;
    }

    try {
      const status = await this.client.status(localPath, true, { cwdOverride: getCommandCwd(workspaceRoot, localPath) });
      return status.items.find((item) => normalizeLocalPath(item.localPath) === normalizeLocalPath(localPath));
    } catch (error) {
      this.output.appendLine(`arm-tfs resolve status ${localPath}: ${error instanceof Error ? error.message : String(error)}`);
      return undefined;
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
      const ignoreMatcher = loadIgnoreMatcher(workspaceRoot);
      const status = await this.client.status(workspaceRoot, true, { cwdOverride: workspaceRoot });
      const pending = status.items.filter((item) => item.state === 'pending').map((item) => new ArmTfsResourceState(item));
      const localChanges = status.items
        .filter((item) => item.state === 'modifiedNotCheckedOut' && !ignoreMatcher.isIgnored(item.localPath))
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
          addKnownPath(knownPaths, workspaceRoot, item.localPath);
        }
        if (item.serverPath) {
          addKnownServerPath(knownPaths, workspaceRoot, item.serverPath, status.workspace.mappings);
        }
      }
      const trackedSnapshot = await this.getTrackedRelativePaths(workspaceRoot, status.workspace.mappings);
      for (const p of trackedSnapshot.paths) {
        knownPaths.add(p);
      }

      const untracked = trackedSnapshot.reliable
        ? await scanUntrackedFiles(workspaceRoot, knownPaths, { ignoreMatcher })
        : [];
      if (!trackedSnapshot.reliable) {
        this.output.appendLine('arm-tfs untracked scan skipped: server items list is unavailable, avoiding false untracked files.');
      }
      const untrackedStates = untracked.map((filePath) => new ArmTfsUntrackedResourceState(filePath));

      await this.ensureExcludedCheckinStateLoaded(workspaceRoot);
      await this.pruneExcludedCheckinState(workspaceRoot, pending);

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
   * Returns every workspace-relative path TFS tracks. Relative keys avoid false positives when
   * the same TFVC workspace metadata is opened from different local roots on macOS and Windows.
   */
  private async getTrackedRelativePaths(
    workspaceRoot: string,
    mappings: Array<{ serverPath: string; localPath: string }>,
  ): Promise<TrackedPathSnapshot> {
    if (this.knownPathsCache) {
      return this.knownPathsCache;
    }

    const result = new Set<string>();
    for (const p of readTrackedPathsFromVersionMetadata(workspaceRoot, mappings)) {
      result.add(p);
    }

    const selectedMappings = selectMappingsForWorkspaceRoot(workspaceRoot, mappings);
    let reliable = selectedMappings.length > 0;

    for (const mapping of selectedMappings) {
      if (!mapping.serverPath || !mapping.localPath) {
        reliable = false;
        continue;
      }
      try {
        const listing = await this.client.itemsList(mapping.serverPath, true, { cwdOverride: workspaceRoot });
        const serverRoot = mapping.serverPath.replace(/\/+$/, '');
        for (const entry of listing.items) {
          if (entry.isFolder) {
            continue;
          }
          const relative = entry.serverPath.startsWith(serverRoot + '/')
            ? entry.serverPath.slice(serverRoot.length + 1)
            : entry.serverPath.replace(/^\$\//, '');
          result.add(normalizeRelativePathKey(relative));
        }
      } catch (error) {
        reliable = false;
        this.output.appendLine(`arm-tfs items list ${mapping.serverPath}: ${error instanceof Error ? error.message : String(error)}`);
      }
    }

    this.knownPathsCache = { paths: result, reliable };
    return this.knownPathsCache;
  }

  /**
   * Drop the cached items list. Call after operations that change the set of tracked files
   * (add, checkin, undo of a pending add).
   */
  invalidateTrackedCache(): void {
    this.knownPathsCache = undefined;
  }

  private async ensureExcludedCheckinStateLoaded(workspaceRoot: string): Promise<void> {
    if (this.excludedCheckinWorkspaceRoot === workspaceRoot) {
      return;
    }

    const stored = this.workspaceState?.get<string[]>(getExcludedCheckinStateKey(workspaceRoot), []) ?? [];
    this.excludedCheckinPaths = new Set(stored.map(normalizeRelativePathKey));
    this.excludedCheckinWorkspaceRoot = workspaceRoot;
  }

  private async persistExcludedCheckinState(workspaceRoot: string): Promise<void> {
    this.excludedCheckinWorkspaceRoot = workspaceRoot;
    if (!this.workspaceState) {
      return;
    }

    await this.workspaceState.update(
      getExcludedCheckinStateKey(workspaceRoot),
      [...this.excludedCheckinPaths].sort((left, right) => left.localeCompare(right)),
    );
  }

  private async pruneExcludedCheckinState(workspaceRoot: string, pending: ArmTfsResourceState[]): Promise<void> {
    const pendingKeys = new Set(pending.map((resource) => getCheckinSelectionKey(workspaceRoot, resource.resourceUri.fsPath)));
    const next = new Set([...this.excludedCheckinPaths].filter((key) => pendingKeys.has(key)));
    if (next.size === this.excludedCheckinPaths.size) {
      return;
    }

    this.excludedCheckinPaths = next;
    await this.persistExcludedCheckinState(workspaceRoot);
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

async function runPathBatches(
  paths: string[],
  runner: (batch: string[]) => Promise<string>,
  batchSize = 50,
): Promise<string> {
  const outputs: string[] = [];
  for (let index = 0; index < paths.length; index += batchSize) {
    const batch = paths.slice(index, index + batchSize);
    outputs.push(await runner(batch));
  }
  return outputs.filter((item) => item.trim()).join('\n');
}

function resolveLocalPathToServerPath(
  localPath: string,
  workspaceRoot: string,
  mappings: Array<{ serverPath: string; localPath: string }>,
): string | undefined {
  const normalizedLocal = normalizeLocalPath(localPath);
  const candidates = mappings
    .map((mapping) => {
      if (!mapping.serverPath || !mapping.localPath) {
        return undefined;
      }
      const mappingLocalPath = normalizeLocalPath(mapping.localPath);
      if (!isSameOrChildPath(normalizedLocal, mappingLocalPath)) {
        return undefined;
      }
      return {
        mapping,
        mappingLocalPath,
        score: mappingLocalPath.length + getLocalMappingPreference(mapping.localPath, workspaceRoot),
      };
    })
    .filter((candidate): candidate is { mapping: { serverPath: string; localPath: string }; mappingLocalPath: string; score: number } => candidate !== undefined)
    .sort((left, right) => right.score - left.score);

  const best = candidates[0];
  if (best) {
    return joinServerPath(best.mapping.serverPath, path.relative(best.mappingLocalPath, normalizedLocal));
  }

  const selectedMapping = selectMappingsForWorkspaceRoot(workspaceRoot, mappings)[0];
  const relativePath = getWorkspaceRelativeServerPath(workspaceRoot, localPath);
  return selectedMapping && relativePath !== undefined
    ? joinServerPath(selectedMapping.serverPath, relativePath)
    : undefined;
}

function joinServerPath(serverRoot: string, relativePath: string): string {
  const root = normalizeServerPath(serverRoot);
  const suffix = normalizeServerRelativePath(relativePath);
  return suffix ? `${root}/${suffix}` : root;
}

function getWorkspaceRelativeServerPath(workspaceRoot: string, localPath: string): string | undefined {
  const normalizedRoot = normalizeLocalPath(workspaceRoot);
  const normalizedLocal = normalizeLocalPath(localPath);
  if (!isSameOrChildPath(normalizedLocal, normalizedRoot)) {
    return undefined;
  }
  return normalizeServerRelativePath(path.relative(normalizedRoot, normalizedLocal));
}

function normalizeServerRelativePath(relativePath: string): string {
  return relativePath
    .replace(/\\/g, '/')
    .replace(/^\/+/, '')
    .replace(/\/+$/, '');
}

function isServerItemNotFoundError(error: unknown): boolean {
  const message = error instanceof ArmTfsCliError
    ? `${error.message}\n${error.stdout}\n${error.stderr}`
    : error instanceof Error
      ? error.message
      : String(error);
  return /(not\s+found|does\s+not\s+exist|unable\s+to\s+find|no\s+item|TF14019|TF10169|TF10122|404)/i.test(message);
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

function readTrackedPathsFromVersionMetadata(
  workspaceRoot: string,
  mappings: Array<{ serverPath: string; localPath: string }>,
): Set<string> {
  const result = new Set<string>();
  const versionsRoot = path.join(workspaceRoot, '.tf', 'versions');
  if (!existsSync(versionsRoot)) {
    return result;
  }

  let entries;
  try {
    entries = readdirSync(versionsRoot, { withFileTypes: true });
  } catch {
    return result;
  }

  for (const entry of entries) {
    if (!entry.isFile() || !entry.name.endsWith('.json')) {
      continue;
    }

    try {
      const raw = JSON.parse(readFileSync(path.join(versionsRoot, entry.name), 'utf8')) as {
        ServerPath?: string;
        serverPath?: string;
        LocalPath?: string;
        localPath?: string;
      };
      const serverPath = raw.ServerPath ?? raw.serverPath;
      const localPath = raw.LocalPath ?? raw.localPath;

      const serverRelativePath = serverPath ? resolveServerPathToRelativePath(serverPath, mappings) : undefined;
      if (serverRelativePath) {
        result.add(serverRelativePath);
        continue;
      }

      if (localPath) {
        addKnownPath(result, workspaceRoot, localPath);
      }
    } catch {
      // Ignore corrupt or partially written tracking files.
    }
  }

  return result;
}

function resolveServerPathToRelativePath(
  serverPath: string,
  mappings: Array<{ serverPath: string; localPath: string }>,
): string | undefined {
  const normalizedServerPath = normalizeServerPath(serverPath);
  const candidates = mappings
    .filter((mapping) => {
      const mappingServerPath = normalizeServerPath(mapping.serverPath);
      return normalizedServerPath === mappingServerPath || normalizedServerPath.startsWith(`${mappingServerPath}/`);
    })
    .sort((left, right) => {
      return normalizeServerPath(right.serverPath).length - normalizeServerPath(left.serverPath).length;
    });

  const best = candidates[0];
  if (!best) {
    return undefined;
  }

  const mappingServerPath = normalizeServerPath(best.serverPath);
  return normalizedServerPath === mappingServerPath
    ? ''
    : normalizeRelativePathKey(normalizedServerPath.slice(mappingServerPath.length + 1));
}

function selectMappingsForWorkspaceRoot(
  workspaceRoot: string,
  mappings: Array<{ serverPath: string; localPath: string }>,
): Array<{ serverPath: string; localPath: string }> {
  const bestByServerPath = new Map<string, { mapping: { serverPath: string; localPath: string }; score: number }>();
  for (const mapping of mappings) {
    const key = normalizeServerPath(mapping.serverPath);
    const score = getLocalMappingPreference(mapping.localPath, workspaceRoot);
    const existing = bestByServerPath.get(key);
    if (!existing || score > existing.score) {
      bestByServerPath.set(key, { mapping, score });
    }
  }
  return [...bestByServerPath.values()]
    .filter((entry) => entry.score > 0)
    .map((entry) => entry.mapping);
}

function getLocalMappingPreference(localPath: string, workspaceRoot: string): number {
  const normalized = normalizeLocalPath(localPath);
  let score = 0;
  if (isSameOrChildPath(normalized, workspaceRoot)) {
    score += 10_000;
  }
  if (existsSync(normalized)) {
    score += 1_000;
  }
  if ((process.platform === 'win32') === isWindowsDrivePath(localPath)) {
    score += 100;
  }
  if (process.platform !== 'win32' && path.isAbsolute(localPath) && !isWindowsDrivePath(localPath)) {
    score += 100;
  }
  return score;
}

function normalizeServerPath(serverPath: string): string {
  return serverPath.trim().replace(/\/+$/, '');
}

function isSameOrChildPath(candidatePath: string, parentPath: string): boolean {
  const candidate = normalizeLocalPath(candidatePath);
  const parent = normalizeLocalPath(parentPath);
  return candidate === parent || candidate.startsWith(`${parent}${path.sep}`);
}

function isWindowsDrivePath(localPath: string): boolean {
  return /^[A-Za-z]:[\\/]/.test(localPath);
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
  options?: { maxResults?: number; maxDepth?: number; ignoreMatcher?: IgnoreMatcher },
): Promise<string[]> {
  const maxResults = options?.maxResults ?? 500;
  const maxDepth = options?.maxDepth ?? 12;
  const ignoreMatcher = options?.ignoreMatcher;
  const results: string[] = [];
  const exclude = new Set(['.tf', '.git', 'node_modules', 'packages', 'bin', 'obj', 'out', 'dist', '.vs', '.idea', '.next', 'target', '.gradle']);

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
        const fullPath = path.join(directory, entry.name);
        if (exclude.has(entry.name)) {
          continue;
        }
        if (ignoreMatcher?.isIgnored(fullPath, true)) {
          continue;
        }
        if (walk(fullPath, depth + 1)) {
          return true;
        }
      } else if (entry.isFile()) {
        const fullPath = path.join(directory, entry.name);
        const relativeKey = getWorkspaceRelativePathKey(workspaceRoot, fullPath);
        if (relativeKey !== undefined && !knownPaths.has(relativeKey) && !ignoreMatcher?.isIgnored(fullPath, false)) {
          results.push(fullPath);
        }
      }
    }
    return false;
  };

  walk(workspaceRoot, 0);
  return results;
}

function addKnownPath(knownPaths: Set<string>, workspaceRoot: string, localPath: string): void {
  const relativeKey = getWorkspaceRelativePathKey(workspaceRoot, localPath);
  if (relativeKey !== undefined) {
    knownPaths.add(relativeKey);
  }
}

function addKnownServerPath(
  knownPaths: Set<string>,
  workspaceRoot: string,
  serverPath: string,
  mappings: Array<{ serverPath: string; localPath: string }>,
): void {
  const relativeKey = resolveServerPathToRelativePath(serverPath, mappings);
  if (relativeKey !== undefined) {
    knownPaths.add(relativeKey);
    return;
  }

  const localPath = resolveServerPathToLocalPathForCurrentRoot(serverPath, mappings, workspaceRoot);
  if (localPath) {
    addKnownPath(knownPaths, workspaceRoot, localPath);
  }
}

function resolveServerPathToLocalPathForCurrentRoot(
  serverPath: string,
  mappings: Array<{ serverPath: string; localPath: string }>,
  workspaceRoot: string,
): string | undefined {
  const relativeKey = resolveServerPathToRelativePath(serverPath, mappings);
  return relativeKey !== undefined
    ? path.join(workspaceRoot, ...relativeKey.split('/'))
    : undefined;
}

function getWorkspaceRelativePathKey(workspaceRoot: string, localPath: string): string | undefined {
  const normalizedRoot = normalizeLocalPath(workspaceRoot);
  const normalizedLocal = normalizeLocalPath(localPath);
  if (!isSameOrChildPath(normalizedLocal, normalizedRoot)) {
    return undefined;
  }
  return normalizeRelativePathKey(path.relative(normalizedRoot, normalizedLocal));
}

function getCheckinSelectionKey(workspaceRoot: string, localPath: string): string {
  return getWorkspaceRelativePathKey(workspaceRoot, localPath)
    ?? normalizeLocalPath(localPath).replace(/\\/g, '/').toLowerCase();
}

function getExcludedCheckinStateKey(workspaceRoot: string): string {
  return `armTfs.excludedCheckin:${createHash('sha256').update(normalizeLocalPath(workspaceRoot)).digest('hex')}`;
}

function normalizeRelativePathKey(relativePath: string): string {
  return relativePath
    .replace(/\\/g, '/')
    .replace(/^\/+/, '')
    .replace(/\/+$/, '')
    .toLowerCase();
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
