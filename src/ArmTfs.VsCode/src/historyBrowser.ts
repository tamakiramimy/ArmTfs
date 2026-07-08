import * as path from 'node:path';
import * as vscode from 'vscode';
import type { ArmTfsCliClient } from './armTfsCliClient';
import type { ChangesetShowResponse, HistoryItem } from './contracts';
import { getUiLanguage, t, translateChangeType, translateCliMessage } from './i18n';
import { checkoutServerPathToLocalFolder } from './serverPathCheckout';
import {
  openServerVersion,
  openServerVersionDiff,
  openServerVersionDiffFromEmpty,
} from './versionedFiles';

interface HistoryTarget {
  label: string;
  serverPath: string;
  kind?: 'file' | 'folder';
}

interface HistoryBrowserOpenOptions {
  mode?: 'history' | 'fileHistory';
}

interface BrowserMessage {
  type: string;
      changesetId?: number;
      changesetIds?: number[];
      serverPath?: string;
      fromPath?: string;
      toPath?: string;
      fromVersion?: number;
      toVersion?: number;
      title?: string;
}

interface ProjectedFile {
  path: string;
  name: string;
  changeType: string;
  isBranch: boolean;
}

interface ComparedFile {
  relativePath: string;
  fromPath?: string;
  toPath?: string;
  fromChangeType?: string;
  toChangeType?: string;
}

export class ArmTfsHistoryBrowser implements vscode.Disposable {
  private panel: vscode.WebviewPanel | undefined;
  private targets: HistoryTarget[] = [];
  private initialChangesetId: number | undefined;
  private mode: 'history' | 'fileHistory' = 'history';
  private readonly disposables: vscode.Disposable[] = [];

  constructor(
    private readonly client: ArmTfsCliClient,
    private readonly output: vscode.OutputChannel,
  ) {}

  dispose(): void {
    this.panel?.dispose();
    vscode.Disposable.from(...this.disposables).dispose();
  }

  async open(serverPath: string, initialChangesetId?: number, options?: HistoryBrowserOpenOptions): Promise<void> {
    await this.openTargets([{
      label: path.posix.basename(serverPath) || serverPath,
      serverPath,
    }], initialChangesetId, options);
  }

  async openFileHistory(serverPath: string, initialChangesetId?: number): Promise<void> {
    await this.openTargets([{
      label: path.posix.basename(serverPath) || serverPath,
      serverPath,
      kind: 'file',
    }], initialChangesetId, { mode: 'fileHistory' });
  }

  async openTargets(targets: HistoryTarget[], initialChangesetId?: number, options?: HistoryBrowserOpenOptions): Promise<void> {
    this.targets = targets;
    this.initialChangesetId = initialChangesetId;
    this.mode = options?.mode ?? (this.isSingleFileTarget() ? 'fileHistory' : 'history');
    await this.focusHub();
    const panel = this.ensurePanel();
    panel.title = this.getPanelTitle(targets);
    panel.reveal(vscode.ViewColumn.Active, false);
    await this.loadHistories(initialChangesetId);
  }

  async refreshLanguage(): Promise<void> {
    if (!this.panel) {
      return;
    }

    this.panel.title = this.getPanelTitle(this.targets);
    this.panel.webview.html = this.getHtml(this.panel.webview);
    await this.loadHistories(this.initialChangesetId);
  }

  private ensurePanel(): vscode.WebviewPanel {
    if (this.panel) {
      return this.panel;
    }

    const panel = vscode.window.createWebviewPanel(
      'armTfs.historyBrowser',
      t('historyBrowser.title'),
      vscode.ViewColumn.Active,
      { enableScripts: true, retainContextWhenHidden: true },
    );
    panel.webview.html = this.getHtml(panel.webview);
    panel.onDidDispose(() => {
      this.panel = undefined;
    }, undefined, this.disposables);
    panel.webview.onDidReceiveMessage((message: BrowserMessage) => {
      void this.handleMessage(message);
    }, undefined, this.disposables);
    this.panel = panel;
    return panel;
  }

  private async loadHistories(initialChangesetId?: number): Promise<void> {
    const panel = this.panel;
    if (!panel) {
      return;
    }

    try {
      const groups = await Promise.all(this.targets.map(async (target) => ({
        ...target,
        items: (await this.client.history(target.serverPath, 100)).items,
      })));
      await panel.webview.postMessage({
        type: 'histories',
        groups,
        initialChangesetId,
        viewMode: this.mode,
        pathLabel: this.targets.length === 1
          ? this.targets[0].serverPath
          : this.targets.map((target) => target.serverPath).join('  ↔  '),
      });
      if (this.shouldShowFileHistoryByDefault()) {
        await this.loadFileHistory(this.targets[0].serverPath, initialChangesetId);
      } else if (initialChangesetId !== undefined) {
        await this.loadChangeset(initialChangesetId);
      }
    } catch (error) {
      this.showError('history', error);
    }
  }

  private async handleMessage(message: BrowserMessage): Promise<void> {
    try {
      switch (message.type) {
        case 'ready':
          await this.loadHistories(this.initialChangesetId);
          break;
        case 'reload':
          await this.loadHistories(this.initialChangesetId);
          break;
        case 'selectChangeset':
          if (message.changesetId !== undefined) {
            this.initialChangesetId = message.changesetId;
            if (this.shouldShowFileHistoryByDefault()) {
              await this.loadFileHistory(this.targets[0].serverPath, message.changesetId);
            } else {
              await this.loadChangeset(message.changesetId);
            }
          }
          break;
        case 'compareChangesets':
          if (message.changesetIds?.length === 2) {
            await this.compareChangesets(message.changesetIds[0], message.changesetIds[1]);
          }
          break;
        case 'diffPrevious':
          if (message.serverPath && message.changesetId !== undefined) {
            await this.diffPrevious(message.serverPath, message.changesetId);
          }
          break;
        case 'fileHistory':
          if (message.serverPath) {
            await this.loadFileHistory(message.serverPath);
          }
          break;
        case 'viewCurrentVersion':
          if (message.serverPath && message.changesetId !== undefined) {
            await this.openCurrentVersion(message.serverPath, message.changesetId, message.title);
          }
          break;
        case 'compareFileVersions':
          if (message.serverPath && message.changesetIds?.length === 2) {
            const versions = [...message.changesetIds].sort((left, right) => left - right);
            await this.showDiff(message.serverPath, message.serverPath, versions[0], versions[1]);
          }
          break;
        case 'compareFilePair':
          if (
            message.fromPath
            && message.toPath
            && message.fromVersion !== undefined
            && message.toVersion !== undefined
          ) {
            await this.showDiff(message.fromPath, message.toPath, message.fromVersion, message.toVersion);
          }
          break;
        case 'checkoutChangeset':
          if (message.changesetId !== undefined) {
            await this.checkoutSnapshot(message.changesetId);
          }
          break;
        case 'rollbackChangeset':
          if (message.changesetId !== undefined) {
            await this.rollbackChangeset(message.changesetId);
          }
          break;
        case 'revertToChangeset':
          if (message.changesetId !== undefined) {
            await this.revertToChangeset(message.changesetId);
          }
          break;
      }
    } catch (error) {
      this.showError(message.type, error);
    }
  }

  private async loadChangeset(changesetId: number): Promise<void> {
    const detail = await this.client.changesetShow(changesetId);
    const files = this.projectFiles(detail);
    const target = this.findTargetForChangeset(detail) ?? this.targets[0];
    await this.panel?.webview.postMessage({
      type: 'changesetDetails',
      changeset: detail.changeset,
      files,
      scopePath: target?.serverPath ?? this.targets[0]?.serverPath ?? '',
    });
  }

  private async compareChangesets(firstId: number, secondId: number): Promise<void> {
    const [first, second] = await Promise.all([
      this.client.changesetShow(firstId),
      this.client.changesetShow(secondId),
    ]);
    const from = firstId <= secondId ? first : second;
    const to = firstId <= secondId ? second : first;
    const fromTarget = this.findTargetForChangeset(from);
    const toTarget = this.findTargetForChangeset(to);
    const files = this.buildComparedFiles(from, to, fromTarget, toTarget);
    await this.panel?.webview.postMessage({
      type: 'comparisonDetails',
      from: from.changeset,
      to: to.changeset,
      files,
      leftPath: fromTarget?.serverPath ?? this.targets[0]?.serverPath ?? '',
      rightPath: toTarget?.serverPath ?? this.targets[this.targets.length - 1]?.serverPath ?? '',
    });
  }

  private async diffPrevious(serverPath: string, changesetId: number): Promise<void> {
    const history = await this.client.history(serverPath, 100);
    const previous = history.items
      .map((item) => item.changesetId)
      .filter((id) => id < changesetId)
      .sort((left, right) => right - left)[0];
    if (previous === undefined) {
      await openServerVersionDiffFromEmpty(
      this.client,
      {
        serverPath,
        version: changesetId,
        label: `${path.posix.basename(serverPath)} (cs${changesetId})`,
      },
      `${path.posix.basename(serverPath)}: ${t('version.emptyBaseline')} ↔ cs${changesetId}`,
    );
      return;
    }
    await this.showDiff(serverPath, serverPath, previous, changesetId);
  }

  private async openCurrentVersion(serverPath: string, changesetId: number, title?: string): Promise<void> {
    await openServerVersion(
      this.client,
      {
        serverPath,
        version: changesetId,
        label: `${path.posix.basename(serverPath)} (cs${changesetId})`,
      },
      { title },
    );
  }

  private async loadFileHistory(serverPath: string, activeChangesetId?: number): Promise<void> {
    const history = await this.client.history(serverPath, 100);
    await this.panel?.webview.postMessage({
      type: 'fileHistoryDetails',
      serverPath,
      activeChangesetId,
      items: history.items,
    });
  }

  private isSingleFileTarget(): boolean {
    return this.targets.length === 1 && this.targets[0]?.kind === 'file';
  }

  private shouldShowFileHistoryByDefault(): boolean {
    return this.mode === 'fileHistory' && this.isSingleFileTarget();
  }

  private async focusHub(): Promise<void> {
    try {
      await vscode.commands.executeCommand('workbench.view.extension.armTfsHub');
    } catch {
      // Best-effort focus only.
    }
  }

  private async showDiff(
    fromPath: string,
    toPath: string,
    fromVersion: number,
    toVersion: number,
  ): Promise<void> {
    await openServerVersionDiff(
      this.client,
      {
        serverPath: fromPath,
        version: fromVersion,
        label: `${path.posix.basename(fromPath)} (cs${fromVersion})`,
      },
      {
        serverPath: toPath,
        version: toVersion,
        label: `${path.posix.basename(toPath)} (cs${toVersion})`,
      },
      `${path.posix.basename(toPath)}: cs${fromVersion} ↔ cs${toVersion}`,
    );
  }

  private async rollbackChangeset(changesetId: number): Promise<void> {
    const confirm = await vscode.window.showWarningMessage(
      `确定要回滚 cs${changesetId} 吗？\n\n此操作会创建一个反向changeset来撤销该变更集的所有文件修改。此操作不可逆。`,
      { modal: true },
      '确定回滚',
    );
    if (confirm !== '确定回滚') { return; }

    const comment = await vscode.window.showInputBox({
      prompt: '回滚注释',
      value: `Rollback changeset ${changesetId}`,
      ignoreFocusOut: true,
    });
    if (comment === undefined) { return; }

    try {
      const result = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `Rolling back cs${changesetId}...` },
        () => this.client.rollback(changesetId, comment.trim() || undefined),
      );
      void vscode.window.showInformationMessage(`回滚成功：${result}`);
      await this.loadHistories();
    } catch (error) {
      void vscode.window.showErrorMessage(`回滚失败：${error instanceof Error ? error.message : String(error)}`);
    }
  }

  private async revertToChangeset(targetChangesetId: number): Promise<void> {
    const serverPath = this.targets[0]?.serverPath;
    if (!serverPath) { return; }

    const confirm = await vscode.window.showWarningMessage(
      `⚠️ 危险操作！\n\n将把 ${serverPath} 的文件内容恢复到 cs${targetChangesetId} 的状态。\n\n这会创建一个新的changeset，将所有文件恢复到该版本时的快照。\n\n确定继续？`,
      { modal: true },
      '确定回退',
    );
    if (confirm !== '确定回退') { return; }

    try {
      const result = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `回退 ${serverPath} 到 cs${targetChangesetId}...` },
        () => this.client.revertToVersion(serverPath, targetChangesetId, `Revert ${serverPath} to cs${targetChangesetId}`),
      );
      // 服务器恢复后，同步本地文件（clean get）
      await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: '同步本地文件...' },
        () => this.client.get(serverPath, { clean: true, force: true }),
      );
      void vscode.window.showInformationMessage(`回退成功：${result}\n本地文件已同步。`);
      await this.loadHistories();
    } catch (error) {
      void vscode.window.showErrorMessage(`回退失败：${error instanceof Error ? error.message : String(error)}`);
    }
  }

  private async checkoutSnapshot(changesetId: number): Promise<void> {
    const detail = await this.client.changesetShow(changesetId);
    const target = this.findTargetForChangeset(detail) ?? this.targets[0];
    if (!target) {
      return;
    }

    const picked = await vscode.window.showOpenDialog({
      canSelectFiles: false,
      canSelectFolders: true,
      canSelectMany: false,
      openLabel: t('historyBrowser.dialog.checkoutFolder'),
    });
    if (!picked?.length) {
      return;
    }

    const result = await checkoutServerPathToLocalFolder(
      this.client,
      target.serverPath,
      picked[0].fsPath,
      { version: changesetId },
    );
    if (result.trim()) {
      this.output.appendLine(result.trim());
      this.output.show(true);
    }
    void vscode.window.showInformationMessage(
      t('historyBrowser.status.downloadedChangeset', { changeset: changesetId, path: picked[0].fsPath }),
    );
  }

  private projectFiles(detail: ChangesetShowResponse): ProjectedFile[] {
    return (detail.changeset.changes ?? [])
      .filter((change) => change.item?.path)
      .map((change) => ({
        path: change.item!.path,
        name: path.posix.basename(change.item!.path),
        changeType: translateChangeType(change.changeType),
        isBranch: change.item?.isBranch ?? false,
      }))
      .sort((left, right) => left.path.localeCompare(right.path, undefined, { sensitivity: 'base' }));
  }

  private findTargetForChangeset(detail: ChangesetShowResponse): HistoryTarget | undefined {
    const changedPaths = (detail.changeset.changes ?? [])
      .map((change) => change.item?.path)
      .filter((value): value is string => Boolean(value));
    return this.targets
      .filter((target) => changedPaths.some((changedPath) => isSameOrChild(changedPath, target.serverPath)))
      .sort((left, right) => right.serverPath.length - left.serverPath.length)[0];
  }

  private buildComparedFiles(
    from: ChangesetShowResponse,
    to: ChangesetShowResponse,
    fromTarget?: HistoryTarget,
    toTarget?: HistoryTarget,
  ): ComparedFile[] {
    const byRelativePath = new Map<string, ComparedFile>();
    const add = (
      detail: ChangesetShowResponse,
      target: HistoryTarget | undefined,
      side: 'from' | 'to',
    ) => {
      for (const change of detail.changeset.changes ?? []) {
        const serverPath = change.item?.path;
        if (!serverPath || change.item?.isBranch) {
          continue;
        }
        const relativePath = target && isSameOrChild(serverPath, target.serverPath)
          ? serverPath.slice(target.serverPath.length).replace(/^\/+/, '')
          : serverPath;
        const current = byRelativePath.get(relativePath) ?? { relativePath };
        current[`${side}Path`] = serverPath;
        current[`${side}ChangeType`] = translateChangeType(change.changeType);
        byRelativePath.set(relativePath, current);
      }
    };

    add(from, fromTarget, 'from');
    add(to, toTarget, 'to');

    for (const file of byRelativePath.values()) {
      if (!file.fromPath && fromTarget && !file.relativePath.startsWith('$/')) {
        file.fromPath = joinServerPath(fromTarget.serverPath, file.relativePath);
      }
      if (!file.toPath && toTarget && !file.relativePath.startsWith('$/')) {
        file.toPath = joinServerPath(toTarget.serverPath, file.relativePath);
      }
    }

    return [...byRelativePath.values()].sort((left, right) =>
      left.relativePath.localeCompare(right.relativePath, undefined, { sensitivity: 'base' }),
    );
  }

  private showError(operation: string, error: unknown): void {
    const message = translateCliMessage(error instanceof Error ? error.message : `${error}`);
    this.output.appendLine(`arm-tfs history browser ${operation}: ${message}`);
    void vscode.window.showErrorMessage(t('historyBrowser.error', { operation, message }));
    void this.panel?.webview.postMessage({ type: 'error', message });
  }

  private getPanelTitle(targets: HistoryTarget[]): string {
    if (targets.length <= 1) {
      const label = targets[0]?.label ?? '';
      return label
        ? t('historyBrowser.panel.history', { label })
        : t('historyBrowser.title');
    }

    return t('historyBrowser.panel.compare', { label: targets.map((target) => target.label).join(' ↔ ') });
  }

  private getHtml(webview: vscode.Webview): string {
    const nonce = getNonce();
    const zh = getUiLanguage() === 'zh-CN';
    const labels = {
      title: t('historyBrowser.title'),
      refresh: zh ? '刷新' : 'Refresh',
      compare: zh ? '比较选中项' : 'Compare Selected',
      compareShort: zh ? '比较' : 'Compare',
      refreshShort: zh ? '刷新' : 'Refresh',
      getSnapshot: zh ? '获取此版本' : 'Get This Version',
      getSnapshotShort: zh ? '获取' : 'Get',
      changeset: zh ? '变更集' : 'Changeset',
      user: zh ? '用户' : 'User',
      date: zh ? '日期' : 'Date',
      comment: zh ? '注释' : 'Comment',
      changes: zh ? '更改' : 'Changes',
      previous: zh ? '与上一版本比较' : 'Compare With Previous',
      previousShort: zh ? '对比' : 'Diff',
      fileHistory: zh ? '查看历史记录' : 'View History',
      fileHistoryShort: zh ? '历史' : 'History',
      viewCurrent: zh ? '查看当前版本' : 'View Current Version',
      viewCurrentShort: zh ? '查看' : 'View',
      compareVersions: zh ? '比较选中的两个版本' : 'Compare Selected Versions',
      compareVersionsShort: zh ? '比较版本' : 'Compare',
      empty: zh ? '请选择一条变更记录查看文件列表。' : 'Select a changeset to inspect changed files.',
      detail: zh ? '变更集详细信息' : 'Changeset Details',
      changedFiles: zh ? '更改文件' : 'Changed Files',
      sourcePath: zh ? '源位置' : 'Source Location',
      from: t('version.left'),
      to: t('version.right'),
      contextDetails: zh ? '查看详细信息' : 'View Details',
      contextCompare: zh ? '比较选中的两个变更集' : 'Compare selected changesets',
      contextGet: zh ? '获取此版本到文件夹' : 'Get this version to folder',
      contextRollback: zh ? '回滚此变更集' : 'Rollback this changeset',
      contextRevertTo: zh ? '回退到此版本（回滚之后的所有提交）' : 'Revert to this version (rollback all after)',
      contextDiff: zh ? '与上一版本比较' : 'Compare with previous',
      contextHistory: zh ? '查看文件历史' : 'View file history',
      contextViewCurrent: zh ? '查看当前版本' : 'View current version',
      contextPair: zh ? '比较这两个版本' : 'Compare these versions',
      filePath: zh ? '文件路径' : 'File Path',
      modeHistory: zh ? '历史记录' : 'History',
      modeFileHistory: zh ? '文件历史记录' : 'File History',
      modeCompare: zh ? '变更集比较' : 'Changeset Comparison',
      noChangeType: t('changeType.none'),
      changeTypes: {
        add: t('changeType.add'),
        edit: t('changeType.edit'),
        delete: t('changeType.delete'),
        rename: t('changeType.rename'),
        merge: t('changeType.merge'),
        branch: t('changeType.branch'),
        undelete: t('changeType.undelete'),
        rollback: t('changeType.rollback'),
        encoding: t('changeType.encoding'),
        lock: t('changeType.lock'),
        sourcerename: t('changeType.sourceRename'),
        targetrename: t('changeType.targetRename'),
        sourcedelete: t('changeType.sourceDelete'),
        targetdelete: t('changeType.targetDelete'),
      },
    };

    return `<!doctype html>
<html lang="${zh ? 'zh-CN' : 'en'}">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}';">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>${escapeHtml(labels.title)}</title>
  <style>
    :root { color-scheme: light dark; }
    * { box-sizing: border-box; }
    body { margin: 0; color: var(--vscode-foreground); background: var(--vscode-editor-background); font-family: var(--vscode-font-family); }
    header { display:grid; grid-template-columns: 1fr auto auto; gap:12px; align-items:center; padding:14px 18px; border-bottom:1px solid var(--vscode-panel-border); }
    .pathbar { display:flex; flex-direction:column; gap:4px; min-width:0; }
    .pathbar strong { font-size: 15px; }
    .pathbar span { color: var(--vscode-descriptionForeground); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    button { color:var(--vscode-foreground); background:var(--vscode-button-secondaryBackground); border:1px solid var(--vscode-panel-border); padding:6px 10px; cursor:pointer; border-radius:6px; }
    button:hover { background:var(--vscode-list-hoverBackground); }
    button.secondary { color:var(--vscode-foreground); background:var(--vscode-button-secondaryBackground); }
    button:disabled { opacity:.45; cursor:default; }
    .layout { display:grid; grid-template-columns: minmax(560px, 58%) 1fr; height: calc(100vh - 73px); }
    .master { overflow:auto; border-right:1px solid var(--vscode-panel-border); }
    .detail { overflow:auto; }
    .section-title { padding:10px 14px; font-weight:700; border-bottom:1px solid var(--vscode-panel-border); background: var(--vscode-sideBarSectionHeader-background); position: sticky; top: 0; z-index: 1; }
    table.grid { width:100%; border-collapse:collapse; table-layout:fixed; }
    .grid thead th { position: sticky; top: 42px; z-index: 1; background: var(--vscode-editor-background); color: var(--vscode-descriptionForeground); font-size:12px; border-bottom:1px solid var(--vscode-panel-border); padding:8px 10px; text-align:left; }
    .grid td { padding:8px 10px; border-bottom:1px solid color-mix(in srgb, var(--vscode-panel-border) 55%, transparent); vertical-align:top; }
    .grid tbody tr { cursor:pointer; }
    .grid tbody tr:hover { background: var(--vscode-list-hoverBackground); }
    .grid tbody tr.active { background: var(--vscode-list-activeSelectionBackground); color: var(--vscode-list-activeSelectionForeground); }
    .grid tbody tr.multi { background: color-mix(in srgb, var(--vscode-list-hoverBackground) 70%, transparent); }
    .cs-col { width:118px; }
    .user-col { width:160px; }
    .date-col { width:180px; }
    .path-col { width:42%; }
    .comment-cell, .path-text { white-space:pre-wrap; overflow-wrap:anywhere; }
    .detail-wrap { padding:16px; display:flex; flex-direction:column; gap:14px; }
    .card { border:1px solid var(--vscode-panel-border); }
    .card-header { padding:10px 12px; border-bottom:1px solid var(--vscode-panel-border); background: var(--vscode-sideBarSectionHeader-background); font-weight:700; display:flex; align-items:center; justify-content:space-between; gap:10px; }
    .card-body { padding:12px; }
    .meta { color: var(--vscode-descriptionForeground); font-size:12px; }
    .tree { font-family: var(--vscode-editor-font-family); font-size:13px; }
    .tree-node { margin-left: 14px; }
    .tree-label { display:flex; align-items:center; gap:8px; padding:4px 6px; border-radius:4px; cursor:pointer; }
    .tree-label:hover { background: var(--vscode-list-hoverBackground); }
    .tree-label.selected { background: var(--vscode-list-activeSelectionBackground); color: var(--vscode-list-activeSelectionForeground); }
    .folder > .tree-label::before { content: '▾'; width: 12px; color: var(--vscode-descriptionForeground); }
    .folder.collapsed > .tree-label::before { content: '▸'; }
    .file > .tree-label::before { content: '•'; width: 12px; color: var(--vscode-descriptionForeground); }
    .folder.collapsed > .tree-children { display: none; }
    .badge { color: var(--vscode-descriptionForeground); font-size: 12px; }
    .toolbar { display:flex; flex-wrap:wrap; gap:8px; }
    .action-btn { display:inline-flex; align-items:center; gap:6px; min-height:30px; }
    .action-btn.compact { padding:6px 8px; }
    .action-icon { width:14px; text-align:center; font-size:13px; opacity:.92; }
    .empty { padding:16px; color: var(--vscode-descriptionForeground); }
    .error { margin:16px; color: var(--vscode-errorForeground); padding:10px; border:1px solid var(--vscode-inputValidation-errorBorder); }
    .context-menu { position: fixed; z-index: 20; min-width: 220px; border: 1px solid var(--vscode-panel-border); background: var(--vscode-menu-background, var(--vscode-editor-background)); box-shadow: 0 8px 24px rgba(0,0,0,.25); display: none; }
    .context-menu button { display:block; width:100%; text-align:left; background:transparent; color:var(--vscode-menu-foreground, var(--vscode-foreground)); padding:8px 12px; }
    .context-menu button:hover { background:var(--vscode-list-hoverBackground); }
    @media (max-width: 1100px) { .layout { grid-template-columns:1fr; height:auto; } .master { max-height:44vh; border-right:0; border-bottom:1px solid var(--vscode-panel-border); } .grid thead th { top: 42px; } }
  </style>
</head>
<body>
  <header>
    <div class="pathbar">
      <strong>${escapeHtml(labels.title)}</strong>
      <span id="pathLabel"></span>
    </div>
    <button id="compare" class="action-btn compact" title="${escapeHtml(labels.compare)}" disabled></button>
    <button id="reload" class="action-btn compact secondary" title="${escapeHtml(labels.refresh)}"></button>
  </header>
  <main class="layout">
    <section class="master">
      <div class="section-title" id="masterTitle">${escapeHtml(labels.modeHistory)}</div>
      <table class="grid" id="historyTable">
        <thead>
          <tr>
            <th class="cs-col">${escapeHtml(labels.changeset)}</th>
            <th class="user-col">${escapeHtml(labels.user)}</th>
            <th class="date-col">${escapeHtml(labels.date)}</th>
            <th>${escapeHtml(labels.comment)}</th>
          </tr>
        </thead>
        <tbody id="historyBody"></tbody>
      </table>
    </section>
    <section class="detail">
      <div class="detail-wrap" id="detailWrap">
        <div class="empty">${escapeHtml(labels.empty)}</div>
      </div>
    </section>
  </main>
  <div class="context-menu" id="contextMenu"></div>
  <script nonce="${nonce}">
    const vscode = acquireVsCodeApi();
    const labels = ${JSON.stringify(labels)};
    const pathLabel = document.getElementById('pathLabel');
    const masterTitle = document.getElementById('masterTitle');
    const historyBody = document.getElementById('historyBody');
    const detailWrap = document.getElementById('detailWrap');
    const compareButton = document.getElementById('compare');
    const contextMenu = document.getElementById('contextMenu');
    const state = {
      historyRows: [],
      selectedChangesets: [],
      activeChangesetId: null,
      historyMode: 'history',
      historyTargetPath: '',
      latestPayload: null,
      latestPathLabel: '',
      fileHistoryPath: '',
      fileHistorySelected: [],
      selectedFilePath: null,
    };

    setButtonMarkup(compareButton, '⇄', labels.compareShort);
    setButtonMarkup(document.getElementById('reload'), '↻', labels.refreshShort);

    document.getElementById('reload').addEventListener('click', () => vscode.postMessage({ type: 'reload' }));
    compareButton.addEventListener('click', () => {
      const selected = getComparablePair(state.selectedChangesets);
      if (selected.length === 2) {
        if (state.historyMode === 'fileHistory' && state.historyTargetPath) {
          vscode.postMessage({ type: 'compareFileVersions', serverPath: state.historyTargetPath, changesetIds: selected });
        } else {
          vscode.postMessage({ type: 'compareChangesets', changesetIds: selected });
        }
      }
    });
    window.addEventListener('click', () => hideContextMenu());
    window.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') hideContextMenu();
    });

    function normalizeSelection(values) {
      return [...new Set(values)].slice(-2);
    }

    function getComparablePair(values) {
      return normalizeSelection(values);
    }

    function setHistorySelection(values) {
      state.selectedChangesets = normalizeSelection(values);
    }

    function setFileHistorySelection(values) {
      state.fileHistorySelected = normalizeSelection(values);
    }

    function renderHistories(groups, initialChangesetId, currentPathLabel, viewMode) {
      state.historyRows = groups.flatMap(group => group.items.map(item => ({ ...item, scopePath: group.serverPath, scopeLabel: group.label })));
      state.historyMode = viewMode || 'history';
      state.historyTargetPath = groups.length === 1 ? (groups[0].serverPath || '') : '';
      setHistorySelection([]);
      state.activeChangesetId = initialChangesetId ?? null;
      state.latestPathLabel = currentPathLabel || '';
      pathLabel.textContent = currentPathLabel || '';
      masterTitle.textContent = state.historyMode === 'fileHistory' ? labels.modeFileHistory : labels.modeHistory;
      compareButton.disabled = true;

      historyBody.innerHTML = state.historyRows.map(item => \`
        <tr data-id="\${item.changesetId}">
          <td class="cs-col">cs\${item.changesetId}</td>
          <td class="user-col">\${escape(item.author?.displayName || item.checkedInBy?.displayName || '')}</td>
          <td class="date-col">\${escape(formatDate(item.createdAt))}</td>
          <td class="comment-cell">\${escape(item.comment || '')}</td>
        </tr>\`).join('');

      historyBody.querySelectorAll('tr').forEach(row => {
        const changesetId = Number(row.dataset.id);
        row.addEventListener('click', event => handleHistoryRowClick(event, changesetId));
        row.addEventListener('dblclick', () => activateChangeset(changesetId));
        row.addEventListener('contextmenu', event => {
          event.preventDefault();
          const selected = getComparablePair(state.selectedChangesets.includes(changesetId)
            ? [...state.selectedChangesets]
            : [...state.selectedChangesets, changesetId]);
          if (!state.selectedChangesets.includes(changesetId)) {
            setHistorySelection(selected);
            refreshHistorySelection();
          }
          const actions = state.historyMode === 'fileHistory' && state.historyTargetPath
            ? [
              { label: labels.contextViewCurrent, run: () => vscode.postMessage({ type: 'viewCurrentVersion', serverPath: state.historyTargetPath, changesetId }) },
              { label: labels.contextDiff, run: () => vscode.postMessage({ type: 'diffPrevious', serverPath: state.historyTargetPath, changesetId }) },
              { label: labels.contextPair, disabled: selected.length !== 2, run: () => vscode.postMessage({ type: 'compareFileVersions', serverPath: state.historyTargetPath, changesetIds: selected }) },
            ]
            : [
              { label: labels.contextDetails, run: () => activateChangeset(changesetId) },
              { label: labels.contextCompare, disabled: selected.length !== 2, run: () => vscode.postMessage({ type: 'compareChangesets', changesetIds: selected }) },
              { label: labels.contextGet, run: () => vscode.postMessage({ type: 'checkoutChangeset', changesetId }) },
              { label: labels.contextRollback, run: () => vscode.postMessage({ type: 'rollbackChangeset', changesetId }) },
              { label: labels.contextRevertTo, run: () => vscode.postMessage({ type: 'revertToChangeset', changesetId }) },
            ];
          showContextMenu(event.clientX, event.clientY, actions);
        });
      });

      refreshHistorySelection();
      if (initialChangesetId) {
        activateChangeset(initialChangesetId);
      }
    }

    function handleHistoryRowClick(event, changesetId) {
      if (event.metaKey || event.ctrlKey) {
        toggleHistorySelection(changesetId);
        return;
      }
      setHistorySelection([changesetId]);
      state.activeChangesetId = changesetId;
      refreshHistorySelection();
      vscode.postMessage({ type: 'selectChangeset', changesetId });
    }

    function toggleHistorySelection(changesetId) {
      if (state.selectedChangesets.includes(changesetId)) {
        setHistorySelection(state.selectedChangesets.filter(value => value !== changesetId));
      } else {
        setHistorySelection([...state.selectedChangesets, changesetId]);
      }
      refreshHistorySelection();
    }

    function activateChangeset(changesetId) {
      setHistorySelection([changesetId]);
      state.activeChangesetId = changesetId;
      refreshHistorySelection();
      vscode.postMessage({ type: 'selectChangeset', changesetId });
    }

    function refreshHistorySelection() {
      compareButton.disabled = state.selectedChangesets.length !== 2;
      historyBody.querySelectorAll('tr').forEach(row => {
        const changesetId = Number(row.dataset.id);
        row.classList.toggle('active', state.activeChangesetId === changesetId);
        row.classList.toggle('multi', state.selectedChangesets.includes(changesetId) && state.activeChangesetId !== changesetId);
      });
    }

    function renderChangesetDetails(message) {
      state.latestPayload = message;
      state.selectedFilePath = null;
      masterTitle.textContent = labels.modeHistory;
      const filesTree = buildChangesetTree(message.files, message.scopePath);
      detailWrap.innerHTML = \`
        <div class="card">
          <div class="card-header">
            <span>\${labels.detail} · cs\${message.changeset.changesetId}</span>
            <button data-role="get-snapshot" class="action-btn compact" title="\${escape(labels.getSnapshot)}"></button>
          </div>
          <div class="card-body">
            <div><strong>\${escape(message.changeset.comment || '')}</strong></div>
            <div class="meta">\${escape(message.changeset.author?.displayName || '')} · \${escape(formatDate(message.changeset.createdAt))}</div>
            <div class="meta" style="margin-top:6px;">\${labels.sourcePath}: \${escape(message.scopePath || '')}</div>
          </div>
        </div>
        <div class="card">
          <div class="card-header">
            <span>\${labels.changedFiles} (\${message.files.length})</span>
            <div class="toolbar" id="fileToolbar"></div>
          </div>
          <div class="card-body">
            <div class="tree" id="fileTree"></div>
          </div>
        </div>\`;

      detailWrap.querySelector('[data-role="get-snapshot"]').addEventListener('click', () => {
        vscode.postMessage({ type: 'checkoutChangeset', changesetId: message.changeset.changesetId });
      });
      setButtonMarkup(detailWrap.querySelector('[data-role="get-snapshot"]'), '↓', labels.getSnapshotShort);
      renderTreeInto(detailWrap.querySelector('#fileTree'), filesTree, 'changeset', message.changeset.changesetId);
      updateFileToolbar('changeset', message.changeset.changesetId);
    }

    function renderComparisonDetails(message) {
      state.latestPayload = message;
      state.selectedFilePath = null;
      masterTitle.textContent = labels.modeCompare;
      const filesTree = buildComparedTree(message.files);
      detailWrap.innerHTML = \`
        <div class="card">
          <div class="card-header">
            <span>cs\${message.from.changesetId} ↔ cs\${message.to.changesetId}</span>
          </div>
          <div class="card-body">
            <div class="meta">\${labels.from}: \${escape(message.leftPath || '')}</div>
            <div class="meta">\${labels.to}: \${escape(message.rightPath || '')}</div>
            <div style="margin-top:8px;"><strong>\${escape(message.from.comment || '')}</strong></div>
            <div class="meta">\${escape(formatDate(message.from.createdAt))}</div>
            <div style="margin-top:10px;"><strong>\${escape(message.to.comment || '')}</strong></div>
            <div class="meta">\${escape(formatDate(message.to.createdAt))}</div>
          </div>
        </div>
        <div class="card">
          <div class="card-header">
            <span>\${labels.changedFiles} (\${message.files.length})</span>
            <div class="toolbar" id="fileToolbar"></div>
          </div>
          <div class="card-body">
            <div class="tree" id="fileTree"></div>
          </div>
        </div>\`;

      renderTreeInto(detailWrap.querySelector('#fileTree'), filesTree, 'comparison', message);
      updateFileToolbar('comparison', message);
    }

    function renderFileHistoryDetails(message) {
      state.latestPayload = message;
      state.fileHistoryPath = message.serverPath;
      setFileHistorySelection(message.activeChangesetId ? [message.activeChangesetId] : []);
      masterTitle.textContent = labels.modeFileHistory;
      detailWrap.innerHTML = \`
        <div class="card">
          <div class="card-header">
            <span>\${labels.fileHistory}</span>
            <div class="toolbar">
              <button id="viewFileVersion" class="action-btn compact" title="\${escape(labels.viewCurrent)}" disabled></button>
              <button id="compareFileVersions" class="action-btn compact" title="\${escape(labels.compareVersions)}" disabled></button>
            </div>
          </div>
          <div class="card-body">
            <div class="meta">\${labels.filePath}: \${escape(message.serverPath)}</div>
          </div>
        </div>
        <div class="card">
          <table class="grid">
            <thead>
              <tr>
                <th class="cs-col">\${labels.changeset}</th>
                <th class="user-col">\${labels.user}</th>
                <th class="date-col">\${labels.date}</th>
                <th>${escapeHtml(labels.comment)}</th>
              </tr>
            </thead>
            <tbody id="fileHistoryBody">
              \${message.items.map(item => \`
                <tr data-id="\${item.changesetId}">
                  <td class="cs-col">cs\${item.changesetId}</td>
                  <td class="user-col">\${escape(item.author?.displayName || item.checkedInBy?.displayName || '')}</td>
                  <td class="date-col">\${escape(formatDate(item.createdAt))}</td>
                  <td class="comment-cell">\${escape(item.comment || '')}</td>
                </tr>\`).join('')}
            </tbody>
          </table>
        </div>\`;

      const viewFileVersion = detailWrap.querySelector('#viewFileVersion');
      const compareFileVersions = detailWrap.querySelector('#compareFileVersions');
      setButtonMarkup(viewFileVersion, '◨', labels.viewCurrentShort);
      setButtonMarkup(compareFileVersions, '⇄', labels.compareVersionsShort);
      const rows = detailWrap.querySelectorAll('#fileHistoryBody tr');
      rows.forEach(row => {
        const changesetId = Number(row.dataset.id);
        row.addEventListener('click', event => {
          if (event.metaKey || event.ctrlKey) {
            toggleFileHistorySelection(changesetId);
          } else {
            setFileHistorySelection([changesetId]);
            refreshFileHistorySelection();
          }
        });
        row.addEventListener('dblclick', () => vscode.postMessage({ type: 'diffPrevious', serverPath: message.serverPath, changesetId }));
        row.addEventListener('contextmenu', event => {
          event.preventDefault();
          const selected = getComparablePair(state.fileHistorySelected.includes(changesetId)
            ? [...state.fileHistorySelected]
            : [...state.fileHistorySelected, changesetId]);
          if (!state.fileHistorySelected.includes(changesetId)) {
            setFileHistorySelection(selected);
            refreshFileHistorySelection();
          }
          showContextMenu(event.clientX, event.clientY, [
            { label: labels.contextViewCurrent, run: () => vscode.postMessage({ type: 'viewCurrentVersion', serverPath: message.serverPath, changesetId }) },
            { label: labels.contextDiff, run: () => vscode.postMessage({ type: 'diffPrevious', serverPath: message.serverPath, changesetId }) },
            { label: labels.contextPair, disabled: selected.length !== 2, run: () => vscode.postMessage({ type: 'compareFileVersions', serverPath: message.serverPath, changesetIds: selected }) },
          ]);
        });
      });

      viewFileVersion.addEventListener('click', () => {
        if (state.fileHistorySelected.length === 1) {
          vscode.postMessage({
            type: 'viewCurrentVersion',
            serverPath: message.serverPath,
            changesetId: state.fileHistorySelected[0],
          });
        }
      });
      compareFileVersions.addEventListener('click', () => {
        const selected = getComparablePair(state.fileHistorySelected);
        if (selected.length === 2) {
          vscode.postMessage({ type: 'compareFileVersions', serverPath: message.serverPath, changesetIds: selected });
        }
      });
      refreshFileHistorySelection();
    }

    function toggleFileHistorySelection(changesetId) {
      if (state.fileHistorySelected.includes(changesetId)) {
        setFileHistorySelection(state.fileHistorySelected.filter(value => value !== changesetId));
      } else {
        setFileHistorySelection([...state.fileHistorySelected, changesetId]);
      }
      refreshFileHistorySelection();
    }

    function refreshFileHistorySelection() {
      const rows = detailWrap.querySelectorAll('#fileHistoryBody tr');
      rows.forEach(row => {
        const changesetId = Number(row.dataset.id);
        row.classList.toggle('active', state.fileHistorySelected.length === 1 && state.fileHistorySelected[0] === changesetId);
        row.classList.toggle('multi', state.fileHistorySelected.includes(changesetId) && !(state.fileHistorySelected.length === 1 && state.fileHistorySelected[0] === changesetId));
      });
      const compareFileVersions = detailWrap.querySelector('#compareFileVersions');
      const viewFileVersion = detailWrap.querySelector('#viewFileVersion');
      if (compareFileVersions) {
        compareFileVersions.disabled = state.fileHistorySelected.length !== 2;
      }
      if (viewFileVersion) {
        viewFileVersion.disabled = state.fileHistorySelected.length !== 1;
      }
    }

    function buildChangesetTree(files, scopePath) {
      return buildTree(files.map(file => ({
        ...file,
        relativePath: makeRelativePath(file.path, scopePath),
      })));
    }

    function buildComparedTree(files) {
      return buildTree(files.map(file => ({
        ...file,
        path: file.relativePath,
        relativePath: file.relativePath,
        changeType: formatComparedChangeType(file.fromChangeType, file.toChangeType),
      })));
    }

    function buildTree(entries) {
      const root = [];
      const folderMap = new Map();
      for (const entry of entries) {
        const parts = (entry.relativePath || entry.path || '').split('/').filter(Boolean);
        let container = root;
        let folderPath = '';
        for (let index = 0; index < parts.length; index += 1) {
          const part = parts[index];
          const isLeaf = index === parts.length - 1;
          folderPath = folderPath ? folderPath + '/' + part : part;
          if (isLeaf) {
            container.push({ kind: 'file', name: part, fullPath: entry.path, entry });
            continue;
          }
          const key = folderPath;
          let folder = folderMap.get(key);
          if (!folder) {
            folder = { kind: 'folder', name: part, children: [], id: key };
            folderMap.set(key, folder);
            container.push(folder);
          }
          container = folder.children;
        }
      }
      sortTree(root);
      return root;
    }

    function sortTree(nodes) {
      nodes.sort((left, right) => {
        if (left.kind !== right.kind) return left.kind === 'folder' ? -1 : 1;
        return left.name.localeCompare(right.name, undefined, { sensitivity: 'base' });
      });
      nodes.filter(node => node.kind === 'folder').forEach(node => sortTree(node.children));
    }

    function renderTreeInto(host, nodes, mode, payload) {
      host.innerHTML = '';
      if (!nodes.length) {
        host.innerHTML = '<div class="empty">' + escape(labels.empty) + '</div>';
        return;
      }
      nodes.forEach(node => host.appendChild(renderTreeNode(node, mode, payload)));
    }

    function renderTreeNode(node, mode, payload) {
      const wrapper = document.createElement('div');
      wrapper.className = 'tree-node ' + node.kind;
      const label = document.createElement('div');
      label.className = 'tree-label';
      label.innerHTML = '<span>' + escape(node.name) + '</span>' + (node.kind === 'file' ? '<span class="badge">' + escape(formatChangeTypeLabel(node.entry.changeType || '')) + '</span>' : '');
      wrapper.appendChild(label);

      if (node.kind === 'folder') {
        wrapper.classList.add('folder');
        const children = document.createElement('div');
        children.className = 'tree-children';
        node.children.forEach(child => children.appendChild(renderTreeNode(child, mode, payload)));
        label.addEventListener('click', () => wrapper.classList.toggle('collapsed'));
        wrapper.appendChild(children);
        return wrapper;
      }

      wrapper.classList.add('file');
      label.dataset.path = node.entry.path;
      label.dataset.mode = mode;
      label.addEventListener('click', () => {
        state.selectedFilePath = node.entry.path;
        hostSelectFile();
        updateFileToolbar(mode, payload, node.entry);
      });
      label.addEventListener('dblclick', () => runPrimaryFileAction(mode, payload, node.entry));
      label.addEventListener('contextmenu', event => {
        event.preventDefault();
        state.selectedFilePath = node.entry.path;
        hostSelectFile();
        updateFileToolbar(mode, payload, node.entry);
        if (mode === 'changeset') {
          showContextMenu(event.clientX, event.clientY, [
            { label: labels.contextViewCurrent, run: () => vscode.postMessage({ type: 'viewCurrentVersion', serverPath: node.entry.path, changesetId: payload }) },
            { label: labels.contextDiff, run: () => vscode.postMessage({ type: 'diffPrevious', serverPath: node.entry.path, changesetId: payload }) },
            { label: labels.contextHistory, run: () => vscode.postMessage({ type: 'fileHistory', serverPath: node.entry.path }) },
          ]);
          return;
        }
        showContextMenu(event.clientX, event.clientY, [
          { label: labels.contextViewCurrent, run: () => vscode.postMessage({ type: 'viewCurrentVersion', serverPath: node.entry.toPath || node.entry.fromPath, changesetId: payload.to.changesetId }) },
          { label: labels.contextPair, run: () => vscode.postMessage({ type: 'compareFilePair', fromPath: node.entry.fromPath, toPath: node.entry.toPath, fromVersion: payload.from.changesetId, toVersion: payload.to.changesetId }) },
          { label: labels.contextHistory, run: () => vscode.postMessage({ type: 'fileHistory', serverPath: node.entry.toPath || node.entry.fromPath }) },
        ]);
      });
      return wrapper;
    }

    function runPrimaryFileAction(mode, payload, entry) {
      if (mode === 'changeset') {
        vscode.postMessage({ type: 'diffPrevious', serverPath: entry.path, changesetId: payload });
        return;
      }
      vscode.postMessage({ type: 'compareFilePair', fromPath: entry.fromPath, toPath: entry.toPath, fromVersion: payload.from.changesetId, toVersion: payload.to.changesetId });
    }

    function hostSelectFile() {
      detailWrap.querySelectorAll('.tree-label').forEach(node => node.classList.toggle('selected', node.dataset.path === state.selectedFilePath));
    }

    function updateFileToolbar(mode, payload, entry) {
      const toolbar = detailWrap.querySelector('#fileToolbar');
      if (!toolbar) return;
      const selectedEntry = entry || findSelectedEntry(mode);
      if (!selectedEntry) {
        toolbar.innerHTML = '<span class="meta">' + escape(labels.empty) + '</span>';
        return;
      }
      if (mode === 'changeset') {
        toolbar.innerHTML = \`
          <button data-role="viewCurrent" class="action-btn compact" title="\${escape(labels.viewCurrent)}"></button>
          <button data-role="diffPrevious" class="action-btn compact" title="\${escape(labels.previous)}"></button>
          <button data-role="fileHistory" class="action-btn compact secondary" title="\${escape(labels.fileHistory)}"></button>\`;
        setButtonMarkup(toolbar.querySelector('[data-role="viewCurrent"]'), '◨', labels.viewCurrentShort);
        setButtonMarkup(toolbar.querySelector('[data-role="diffPrevious"]'), '≶', labels.previousShort);
        setButtonMarkup(toolbar.querySelector('[data-role="fileHistory"]'), '🕘', labels.fileHistoryShort);
        toolbar.querySelector('[data-role="viewCurrent"]').addEventListener('click', () => {
          vscode.postMessage({ type: 'viewCurrentVersion', serverPath: selectedEntry.path, changesetId: payload });
        });
        toolbar.querySelector('[data-role="diffPrevious"]').addEventListener('click', () => {
          vscode.postMessage({ type: 'diffPrevious', serverPath: selectedEntry.path, changesetId: payload });
        });
        toolbar.querySelector('[data-role="fileHistory"]').addEventListener('click', () => {
          vscode.postMessage({ type: 'fileHistory', serverPath: selectedEntry.path });
        });
        return;
      }

      toolbar.innerHTML = \`
        <button data-role="viewCurrent" class="action-btn compact" title="\${escape(labels.viewCurrent)}"></button>
        <button data-role="comparePair" class="action-btn compact" title="\${escape(labels.compareVersions)}"></button>
        <button data-role="fileHistory" class="action-btn compact secondary" title="\${escape(labels.fileHistory)}"></button>\`;
      setButtonMarkup(toolbar.querySelector('[data-role="viewCurrent"]'), '◨', labels.viewCurrentShort);
      setButtonMarkup(toolbar.querySelector('[data-role="comparePair"]'), '⇄', labels.compareVersionsShort);
      setButtonMarkup(toolbar.querySelector('[data-role="fileHistory"]'), '🕘', labels.fileHistoryShort);
      toolbar.querySelector('[data-role="viewCurrent"]').addEventListener('click', () => {
        vscode.postMessage({
          type: 'viewCurrentVersion',
          serverPath: selectedEntry.toPath || selectedEntry.fromPath,
          changesetId: payload.to.changesetId,
        });
      });
      toolbar.querySelector('[data-role="comparePair"]').addEventListener('click', () => {
        vscode.postMessage({
          type: 'compareFilePair',
          fromPath: selectedEntry.fromPath,
          toPath: selectedEntry.toPath,
          fromVersion: payload.from.changesetId,
          toVersion: payload.to.changesetId,
        });
      });
      toolbar.querySelector('[data-role="fileHistory"]').addEventListener('click', () => {
        vscode.postMessage({ type: 'fileHistory', serverPath: selectedEntry.toPath || selectedEntry.fromPath });
      });
    }

    function findSelectedEntry(mode) {
      if (!state.selectedFilePath || !state.latestPayload) {
        return null;
      }
      const entries = mode === 'changeset' ? (state.latestPayload.files || []) : (state.latestPayload.files || []);
      return entries.find(item => item.path === state.selectedFilePath || item.relativePath === state.selectedFilePath || item.toPath === state.selectedFilePath || item.fromPath === state.selectedFilePath) || null;
    }

    function makeRelativePath(fullPath, scopePath) {
      if (scopePath && fullPath.startsWith(scopePath + '/')) {
        return fullPath.slice(scopePath.length + 1);
      }
      if (scopePath === fullPath) {
        return pathLeaf(fullPath);
      }
      return fullPath.replace(/^\\$\\//, '');
    }

    function pathLeaf(value) {
      const parts = value.split('/').filter(Boolean);
      return parts[parts.length - 1] || value;
    }

    function showContextMenu(x, y, actions) {
      contextMenu.innerHTML = actions.map((action, index) => \`<button data-index="\${index}" \${action.disabled ? 'disabled' : ''}>\${escape(action.label)}</button>\`).join('');
      contextMenu.style.display = 'block';
      contextMenu.style.left = x + 'px';
      contextMenu.style.top = y + 'px';
      contextMenu.querySelectorAll('button').forEach(button => {
        button.addEventListener('click', event => {
          event.stopPropagation();
          const action = actions[Number(button.dataset.index)];
          hideContextMenu();
          if (!action.disabled) action.run();
        });
      });
    }

    function hideContextMenu() {
      contextMenu.style.display = 'none';
      contextMenu.innerHTML = '';
    }

    function setButtonMarkup(button, icon, label) {
      if (!button) {
        return;
      }
      button.innerHTML = '<span class="action-icon">' + escape(icon) + '</span><span>' + escape(label) + '</span>';
    }

    function formatComparedChangeType(fromChangeType, toChangeType) {
      return formatChangeTypeLabel(fromChangeType || labels.noChangeType) + ' → ' + formatChangeTypeLabel(toChangeType || labels.noChangeType);
    }

    function formatChangeTypeLabel(value) {
      if (!value) {
        return '';
      }
      return String(value)
        .split(',')
        .map(part => part.trim())
        .filter(Boolean)
        .map(part => {
          const normalized = part.toLowerCase();
          return labels.changeTypes[normalized] || part;
        })
        .join(', ');
    }

    window.addEventListener('message', event => {
      const message = event.data;
      if (message.type === 'histories') renderHistories(message.groups, message.initialChangesetId, message.pathLabel, message.viewMode);
      if (message.type === 'changesetDetails') renderChangesetDetails(message);
      if (message.type === 'comparisonDetails') renderComparisonDetails(message);
      if (message.type === 'fileHistoryDetails') renderFileHistoryDetails(message);
      if (message.type === 'error') detailWrap.innerHTML = '<div class="error">' + escape(message.message) + '</div>';
    });

    function formatDate(value) { return value ? new Date(value).toLocaleString() : ''; }
    function escape(value) { const div = document.createElement('div'); div.textContent = String(value ?? ''); return div.innerHTML; }
    vscode.postMessage({ type: 'ready' });
  </script>
</body>
</html>`;
  }
}

function isSameOrChild(candidate: string, parent: string): boolean {
  const normalizedCandidate = candidate.toLowerCase().replace(/\/+$/, '');
  const normalizedParent = parent.toLowerCase().replace(/\/+$/, '');
  return normalizedCandidate === normalizedParent || normalizedCandidate.startsWith(`${normalizedParent}/`);
}

function joinServerPath(root: string, relativePath: string): string {
  return `${root.replace(/\/+$/, '')}/${relativePath.replace(/^\/+/, '')}`;
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}

function getNonce(): string {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  let value = '';
  for (let index = 0; index < 32; index += 1) {
    value += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return value;
}
