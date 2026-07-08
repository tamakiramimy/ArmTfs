import * as path from 'node:path';
import * as vscode from 'vscode';
import type { ArmTfsCliClient } from './armTfsCliClient';
import type { ArmTfsScmController, ArmTfsResourceState, ArmTfsUntrackedResourceState } from './scm';
import { t } from './i18n';

type ChangeGroupKind = 'pending' | 'working' | 'conflicts';

class ChangeGroupItem extends vscode.TreeItem {
  constructor(public readonly kind: ChangeGroupKind, count: number, description?: string) {
    super(labelForKind(kind), vscode.TreeItemCollapsibleState.Expanded);
    this.contextValue = `armTfsChangeGroup.${kind}`;
    this.description = description ?? count.toString();
    this.iconPath = new vscode.ThemeIcon(iconForKind(kind));
  }
}

class ChangeFileItem extends vscode.TreeItem {
  constructor(
    public readonly kind: ChangeGroupKind,
    public readonly resource: ArmTfsResourceState | ArmTfsUntrackedResourceState,
    public readonly excludedFromCheckin: boolean,
  ) {
    const label = path.basename(resource.resourceUri.fsPath);
    super(label, vscode.TreeItemCollapsibleState.None);
    this.contextValue = contextForResource(kind, resource, excludedFromCheckin);
    this.resourceUri = resource.resourceUri;
    const directory = path.dirname(resource.resourceUri.fsPath);
    this.description = excludedFromCheckin
      ? `${directory} • ${t('changesView.checkin.excludedBadge')}`
      : directory;
    this.tooltip = excludedFromCheckin
      ? `${resource.resourceUri.fsPath}\n${t('changesView.checkin.excludedTooltip')}`
      : resource.resourceUri.fsPath;
    this.command = { command: 'armTfs.openResourceDiff', title: t('command.openTfsDiff'), arguments: [resource] };
  }
}

class ChangesProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
  private readonly emitter = new vscode.EventEmitter<vscode.TreeItem | undefined>();
  readonly onDidChangeTreeData = this.emitter.event;

  constructor(private readonly scm: ArmTfsScmController) {}

  refresh(): void {
    this.emitter.fire(undefined);
  }

  getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
    return element;
  }

  getChildren(element?: vscode.TreeItem): vscode.TreeItem[] {
    if (!element) {
      const groups: ChangeGroupItem[] = [];
      if (this.scm.pendingChanges.length) {
        const pendingTotal = this.scm.pendingChanges.length;
        const pendingIncluded = this.scm.getIncludedPendingChangeCount();
        groups.push(new ChangeGroupItem(
          'pending',
          pendingTotal,
          pendingIncluded === pendingTotal ? pendingTotal.toString() : `${pendingIncluded}/${pendingTotal}`,
        ));
      }
      if (this.scm.conflicts.length) {
        groups.push(new ChangeGroupItem('conflicts', this.scm.conflicts.length));
      }
      const workingCount = this.scm.localChanges.length + this.scm.untrackedFiles.length;
      if (workingCount) {
        groups.push(new ChangeGroupItem('working', workingCount));
      }
      if (groups.length === 0) {
        const empty = new vscode.TreeItem(t('changesView.empty'), vscode.TreeItemCollapsibleState.None);
        empty.iconPath = new vscode.ThemeIcon('check');
        empty.contextValue = 'armTfsChangesEmpty';
        return [empty];
      }
      return groups;
    }

    if (element instanceof ChangeGroupItem) {
      switch (element.kind) {
        case 'pending':
          return sortPendingResources(this.scm.pendingChanges, this.scm)
            .map((r) => new ChangeFileItem('pending', r, this.scm.isExcludedFromCheckin(r)));
        case 'working':
          return [
            ...this.scm.localChanges.map((r) => new ChangeFileItem('working', r, false)),
            ...this.scm.untrackedFiles.map((r) => new ChangeFileItem('working', r, false)),
          ];
        case 'conflicts':
          return sortPendingResources(this.scm.conflicts, this.scm)
            .map((r) => new ChangeFileItem('conflicts', r, this.scm.isExcludedFromCheckin(r)));
      }
    }
    return [];
  }
}

/**
 * Owns the `armTfs.changes` TreeView under the TFS activity bar. All TFS-related changes
 * (pending / local / conflicts / untracked) live here so the user does not have to look at
 * the built-in Source Control panel — keeping arm-tfs separate from git.
 */
export class ArmTfsChangesViewController implements vscode.Disposable {
  private readonly provider: ChangesProvider;
  private readonly treeView: vscode.TreeView<vscode.TreeItem>;
  private readonly disposables: vscode.Disposable[] = [];

  constructor(private readonly scm: ArmTfsScmController, private readonly client: ArmTfsCliClient) {
    this.provider = new ChangesProvider(scm);
    this.treeView = vscode.window.createTreeView('armTfs.changes', {
      treeDataProvider: this.provider,
      canSelectMany: true,
    });
    this.refreshTitle();

    this.disposables.push(
      this.treeView,
      this.scm.onDidChangeChanges(() => {
        this.provider.refresh();
        this.refreshTitle();
      }),
      vscode.commands.registerCommand('armTfs.changes.refresh', () => this.scm.refresh()),
      vscode.commands.registerCommand('armTfs.changes.checkin', () => this.scm.checkin()),
      vscode.commands.registerCommand('armTfs.changes.openFile', (item?: ChangeFileItem) => {
        if (item?.resource) {
          void vscode.commands.executeCommand('vscode.open', item.resource.resourceUri);
        }
      }),
      vscode.commands.registerCommand('armTfs.changes.add', (item?: ChangeFileItem) => {
        if (item?.resource) {
          return this.scm.stage(item.resource);
        }
      }),
      vscode.commands.registerCommand('armTfs.changes.addAllUntracked', () => this.scm.stageAllWorkingChanges()),
      vscode.commands.registerCommand('armTfs.changes.stageAll', () => this.scm.stageAllWorkingChanges()),
      vscode.commands.registerCommand('armTfs.changes.ignore', (item?: ChangeFileItem) => {
        if (item?.resource) {
          return this.scm.ignore(item.resource);
        }
      }),
      vscode.commands.registerCommand('armTfs.changes.checkout', (item?: ChangeFileItem) => {
        if (item?.resource) {
          return this.scm.checkout(item.resource as ArmTfsResourceState);
        }
      }),
      vscode.commands.registerCommand('armTfs.changes.undo', (item?: ChangeFileItem) => {
        if (item?.resource) {
          return this.scm.undo(item.resource as ArmTfsResourceState);
        }
      }),
      vscode.commands.registerCommand('armTfs.changes.unstage', (item?: ChangeFileItem) => {
        if (item?.resource) {
          return this.scm.excludeFromCheckin(item.resource as ArmTfsResourceState);
        }
      }),
      vscode.commands.registerCommand('armTfs.changes.stageCheckin', (item?: ChangeFileItem) => {
        if (item?.resource) {
          return this.scm.stageCheckin(item.resource as ArmTfsResourceState);
        }
      }),
      vscode.commands.registerCommand('armTfs.changes.unstageAll', () => this.scm.unstageAllPendingChanges()),
      vscode.commands.registerCommand('armTfs.changes.undoPendingAdds', () => this.scm.unstageAllPendingChanges()),
      vscode.commands.registerCommand('armTfs.changes.openDiff', (item?: ChangeFileItem) => {
        if (item?.resource) {
          return this.scm.openDiff(item.resource);
        }
      }),
      vscode.commands.registerCommand('armTfs.changes.deleteUntracked', async (item?: ChangeFileItem) => {
        if (!item?.resource) {
          return;
        }
        const fsPath = item.resource.resourceUri.fsPath;
        const choice = await vscode.window.showWarningMessage(
          t('changesView.delete.confirm', { path: path.basename(fsPath) }),
          { modal: true, detail: fsPath },
          t('changesView.delete.action'),
        );
        if (choice !== t('changesView.delete.action')) {
          return;
        }
        try {
          // useTrash: true → moves to system trash, easy to recover by mistake.
          await vscode.workspace.fs.delete(item.resource.resourceUri, { recursive: false, useTrash: true });
          await this.scm.refresh();
        } catch (error) {
          void vscode.window.showErrorMessage(
            t('changesView.delete.failed', { message: error instanceof Error ? error.message : String(error) }),
          );
        }
      }),
      vscode.commands.registerCommand('armTfs.changes.revertLocal', async (item?: ChangeFileItem) => {
        if (!item?.resource) {
          return;
        }
        return this.scm.revertLocalChange(item.resource as ArmTfsResourceState);
      }),
    );
  }

  dispose(): void {
    vscode.Disposable.from(...this.disposables).dispose();
  }

  refreshLabels(): void {
    this.refreshTitle();
    this.provider.refresh();
  }

  private refreshTitle(): void {
    const total =
      this.scm.pendingChanges.length +
      this.scm.localChanges.length +
      this.scm.conflicts.length +
      this.scm.untrackedFiles.length;
    this.treeView.title = total
      ? t('view.changes.withCount', { count: total })
      : t('view.changes');
  }
}

function labelForKind(kind: ChangeGroupKind): string {
  switch (kind) {
    case 'pending':
      return t('scm.group.pendingChanges');
    case 'working':
      return t('scm.group.changes');
    case 'conflicts':
      return t('scm.group.conflicts');
  }
}

function iconForKind(kind: ChangeGroupKind): string {
  switch (kind) {
    case 'pending':
      return 'git-commit';
    case 'working':
      return 'edit';
    case 'conflicts':
      return 'warning';
  }
}

function contextForResource(
  kind: ChangeGroupKind,
  resource: ArmTfsResourceState | ArmTfsUntrackedResourceState,
  excludedFromCheckin: boolean,
): string {
  if ('localPath' in resource) {
    return 'armTfsChangeFile.untracked';
  }
  if (kind === 'working') {
    return 'armTfsChangeFile.localChanges';
  }
  if (kind === 'pending') {
    return excludedFromCheckin ? 'armTfsChangeFile.pendingExcluded' : 'armTfsChangeFile.pending';
  }
  return excludedFromCheckin ? 'armTfsChangeFile.conflictsExcluded' : 'armTfsChangeFile.conflicts';
}

function sortPendingResources<T extends ArmTfsResourceState>(resources: T[], scm: ArmTfsScmController): T[] {
  return [...resources].sort((left, right) => {
    const leftExcluded = scm.isExcludedFromCheckin(left);
    const rightExcluded = scm.isExcludedFromCheckin(right);
    if (leftExcluded !== rightExcluded) {
      return leftExcluded ? 1 : -1;
    }
    return left.resourceUri.fsPath.localeCompare(right.resourceUri.fsPath);
  });
}
