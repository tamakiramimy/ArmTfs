import * as path from 'node:path';
import * as vscode from 'vscode';
import type { ArmTfsScmController, ArmTfsResourceState, ArmTfsUntrackedResourceState } from './scm';
import { t } from './i18n';

type ChangeKind = 'pending' | 'localChanges' | 'conflicts' | 'untracked';

class ChangeGroupItem extends vscode.TreeItem {
  constructor(public readonly kind: ChangeKind, count: number) {
    super(labelForKind(kind), vscode.TreeItemCollapsibleState.Expanded);
    this.contextValue = `armTfsChangeGroup.${kind}`;
    this.description = count.toString();
    this.iconPath = new vscode.ThemeIcon(iconForKind(kind));
  }
}

class ChangeFileItem extends vscode.TreeItem {
  constructor(
    public readonly kind: ChangeKind,
    public readonly resource: ArmTfsResourceState | ArmTfsUntrackedResourceState,
  ) {
    const label = path.basename(resource.resourceUri.fsPath);
    super(label, vscode.TreeItemCollapsibleState.None);
    this.contextValue = kind === 'untracked' ? 'armTfsChangeFile.untracked' : `armTfsChangeFile.${kind}`;
    this.resourceUri = resource.resourceUri;
    this.description = path.dirname(resource.resourceUri.fsPath);
    this.tooltip = resource.resourceUri.fsPath;
    this.command = kind === 'untracked'
      ? { command: 'vscode.open', title: 'Open', arguments: [resource.resourceUri] }
      : { command: 'armTfs.openResourceDiff', title: t('command.openTfsDiff'), arguments: [resource] };
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
        groups.push(new ChangeGroupItem('pending', this.scm.pendingChanges.length));
      }
      if (this.scm.localChanges.length) {
        groups.push(new ChangeGroupItem('localChanges', this.scm.localChanges.length));
      }
      if (this.scm.conflicts.length) {
        groups.push(new ChangeGroupItem('conflicts', this.scm.conflicts.length));
      }
      if (this.scm.untrackedFiles.length) {
        groups.push(new ChangeGroupItem('untracked', this.scm.untrackedFiles.length));
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
          return this.scm.pendingChanges.map((r) => new ChangeFileItem('pending', r));
        case 'localChanges':
          return this.scm.localChanges.map((r) => new ChangeFileItem('localChanges', r));
        case 'conflicts':
          return this.scm.conflicts.map((r) => new ChangeFileItem('conflicts', r));
        case 'untracked':
          return this.scm.untrackedFiles.map((r) => new ChangeFileItem('untracked', r));
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

  constructor(private readonly scm: ArmTfsScmController) {
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
          return this.scm.add(item.resource);
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
      vscode.commands.registerCommand('armTfs.changes.openDiff', (item?: ChangeFileItem) => {
        if (item?.resource) {
          return this.scm.openDiff(item.resource as ArmTfsResourceState);
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

function labelForKind(kind: ChangeKind): string {
  switch (kind) {
    case 'pending':
      return t('scm.group.changes');
    case 'localChanges':
      return t('scm.group.localChanges');
    case 'conflicts':
      return t('scm.group.conflicts');
    case 'untracked':
      return t('scm.group.untracked');
  }
}

function iconForKind(kind: ChangeKind): string {
  switch (kind) {
    case 'pending':
      return 'git-commit';
    case 'localChanges':
      return 'edit';
    case 'conflicts':
      return 'warning';
    case 'untracked':
      return 'new-file';
  }
}
