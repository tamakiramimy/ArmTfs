import * as path from 'node:path';
import * as vscode from 'vscode';
import type { ArmTfsCliClient } from './armTfsCliClient';
import type { ChangesetShowResponse, DiffResponse, HistoryItem } from './contracts';
import { getUiLanguage, translateCliMessage } from './i18n';

interface HistoryTarget {
  label: string;
  serverPath: string;
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
  private readonly disposables: vscode.Disposable[] = [];

  constructor(
    private readonly client: ArmTfsCliClient,
    private readonly output: vscode.OutputChannel,
  ) {}

  dispose(): void {
    this.panel?.dispose();
    vscode.Disposable.from(...this.disposables).dispose();
  }

  async open(serverPath: string, initialChangesetId?: number): Promise<void> {
    await this.openTargets([{
      label: path.posix.basename(serverPath) || serverPath,
      serverPath,
    }], initialChangesetId);
  }

  async openTargets(targets: HistoryTarget[], initialChangesetId?: number): Promise<void> {
    this.targets = targets;
    this.initialChangesetId = initialChangesetId;
    const panel = this.ensurePanel();
    panel.title = targets.length === 1
      ? `TFVC History: ${targets[0].label}`
      : `TFVC Compare: ${targets.map((target) => target.label).join(' ↔ ')}`;
    panel.reveal(vscode.ViewColumn.Active, false);
    await this.loadHistories(initialChangesetId);
  }

  private ensurePanel(): vscode.WebviewPanel {
    if (this.panel) {
      return this.panel;
    }

    const panel = vscode.window.createWebviewPanel(
      'armTfs.historyBrowser',
      'TFVC History',
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
      await panel.webview.postMessage({ type: 'histories', groups, initialChangesetId });
      if (initialChangesetId !== undefined) {
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
          await this.loadHistories();
          break;
        case 'selectChangeset':
          if (message.changesetId !== undefined) {
            await this.loadChangeset(message.changesetId);
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
      }
    } catch (error) {
      this.showError(message.type, error);
    }
  }

  private async loadChangeset(changesetId: number): Promise<void> {
    const detail = await this.client.changesetShow(changesetId);
    const files = this.projectFiles(detail);
    await this.panel?.webview.postMessage({
      type: 'changeset',
      changeset: detail.changeset,
      files,
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
      type: 'comparison',
      from: from.changeset,
      to: to.changeset,
      files,
    });
  }

  private async diffPrevious(serverPath: string, changesetId: number): Promise<void> {
    const history = await this.client.history(serverPath, 100);
    const previous = history.items
      .map((item) => item.changesetId)
      .filter((id) => id < changesetId)
      .sort((left, right) => right - left)[0];
    if (previous === undefined) {
      void vscode.window.showInformationMessage(
        getUiLanguage() === 'zh-CN' ? '未找到该文件的上一历史版本。' : 'No previous file version was found.',
      );
      return;
    }
    await this.showDiff(serverPath, serverPath, previous, changesetId);
  }

  private async loadFileHistory(serverPath: string): Promise<void> {
    const history = await this.client.history(serverPath, 100);
    await this.panel?.webview.postMessage({
      type: 'fileHistory',
      serverPath,
      items: history.items,
    });
  }

  private async showDiff(
    fromPath: string,
    toPath: string,
    fromVersion: number,
    toVersion: number,
  ): Promise<void> {
    const diff = await this.client.diffVersions(
      fromPath,
      fromVersion,
      toVersion,
      { toServerPath: toPath },
    );
    await showDiffDocument(`${fromPath} ↔ ${toPath}`, diff);
  }

  private projectFiles(detail: ChangesetShowResponse): Array<{
    path: string;
    name: string;
    changeType: string;
    isBranch: boolean;
  }> {
    return (detail.changeset.changes ?? [])
      .filter((change) => change.item?.path)
      .map((change) => ({
        path: change.item!.path,
        name: path.posix.basename(change.item!.path),
        changeType: change.changeType,
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
        current[`${side}ChangeType`] = change.changeType;
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
    void vscode.window.showErrorMessage(`arm-tfs ${operation}: ${message}`);
    void this.panel?.webview.postMessage({ type: 'error', message });
  }

  private getHtml(webview: vscode.Webview): string {
    const nonce = getNonce();
    const zh = getUiLanguage() === 'zh-CN';
    const labels = {
      title: zh ? 'TFVC 版本历史' : 'TFVC History',
      refresh: zh ? '刷新' : 'Refresh',
      compare: zh ? '比较选中的两个变更集' : 'Compare selected changesets',
      hint: zh ? 'Ctrl/Command 点击可选择两条记录进行比较' : 'Ctrl/Command-click to select two records',
      files: zh ? '变更文件' : 'Changed files',
      diff: zh ? '与上一版本比较' : 'Diff previous',
      history: zh ? '文件历史' : 'File history',
      compareVersions: zh ? '比较选中的两个文件版本' : 'Compare selected file versions',
      empty: zh ? '请选择一个变更集查看文件列表。' : 'Select a changeset to inspect its files.',
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
    body { margin: 0; color: var(--vscode-foreground); background: var(--vscode-editor-background); font-family: var(--vscode-font-family); }
    header { display:flex; align-items:center; gap:12px; padding:14px 18px; border-bottom:1px solid var(--vscode-panel-border); position:sticky; top:0; background:var(--vscode-editor-background); z-index:3; }
    header h1 { font-size:16px; margin:0; flex:1; }
    button { color:var(--vscode-button-foreground); background:var(--vscode-button-background); border:0; padding:6px 10px; cursor:pointer; }
    button:hover { background:var(--vscode-button-hoverBackground); }
    button.secondary { color:var(--vscode-foreground); background:var(--vscode-button-secondaryBackground); }
    button:disabled { opacity:.45; cursor:default; }
    .layout { display:grid; grid-template-columns:minmax(300px, 38%) 1fr; height:calc(100vh - 53px); }
    .history { overflow:auto; border-right:1px solid var(--vscode-panel-border); }
    .details { overflow:auto; padding:18px; }
    .target { padding:10px 12px 6px; font-size:12px; font-weight:700; color:var(--vscode-descriptionForeground); background:var(--vscode-sideBarSectionHeader-background); position:sticky; top:0; }
    .changeset { display:grid; grid-template-columns:22px 82px 1fr; gap:8px; padding:9px 12px; border-bottom:1px solid color-mix(in srgb, var(--vscode-panel-border) 55%, transparent); cursor:pointer; }
    .changeset:hover, .changeset.active { background:var(--vscode-list-hoverBackground); }
    .changeset strong { color:var(--vscode-textLink-foreground); }
    .meta { color:var(--vscode-descriptionForeground); font-size:12px; margin-top:3px; }
    .comment { white-space:pre-wrap; overflow-wrap:anywhere; }
    .hint { color:var(--vscode-descriptionForeground); font-size:12px; }
    .card { border:1px solid var(--vscode-panel-border); margin-bottom:14px; }
    .card-title { padding:9px 11px; font-weight:700; background:var(--vscode-sideBarSectionHeader-background); }
    table { width:100%; border-collapse:collapse; }
    td, th { text-align:left; padding:8px 10px; border-top:1px solid var(--vscode-panel-border); vertical-align:top; }
    th { color:var(--vscode-descriptionForeground); font-size:12px; }
    .path { font-family:var(--vscode-editor-font-family); overflow-wrap:anywhere; }
    .actions { white-space:nowrap; }
    .actions button { margin-left:5px; padding:4px 7px; }
    .error { color:var(--vscode-errorForeground); padding:10px; border:1px solid var(--vscode-inputValidation-errorBorder); }
    @media (max-width:800px) { .layout { grid-template-columns:1fr; height:auto; } .history { max-height:45vh; border-right:0; border-bottom:1px solid var(--vscode-panel-border); } }
  </style>
</head>
<body>
  <header>
    <h1>${escapeHtml(labels.title)}</h1>
    <span class="hint">${escapeHtml(labels.hint)}</span>
    <button id="compare" disabled>${escapeHtml(labels.compare)}</button>
    <button id="reload" class="secondary">${escapeHtml(labels.refresh)}</button>
  </header>
  <main class="layout">
    <section id="history" class="history"></section>
    <section id="details" class="details"><p class="hint">${escapeHtml(labels.empty)}</p></section>
  </main>
  <script nonce="${nonce}">
    const vscode = acquireVsCodeApi();
    const labels = ${JSON.stringify(labels)};
    const historyEl = document.getElementById('history');
    const detailsEl = document.getElementById('details');
    const compareButton = document.getElementById('compare');
    let selected = [];

    document.getElementById('reload').addEventListener('click', () => vscode.postMessage({ type: 'reload' }));
    compareButton.addEventListener('click', () => vscode.postMessage({ type: 'compareChangesets', changesetIds: selected }));

    function toggleSelection(id, checked) {
      selected = selected.filter(value => value !== id);
      if (checked) selected.push(id);
      if (selected.length > 2) selected.shift();
      document.querySelectorAll('.changeset input').forEach(input => {
        input.checked = selected.includes(Number(input.dataset.id));
      });
      compareButton.disabled = selected.length !== 2;
    }

    function renderHistories(groups) {
      selected = [];
      compareButton.disabled = true;
      historyEl.innerHTML = groups.map(group => \`
        <div class="target">\${escape(group.label)} <span class="hint">\${escape(group.serverPath)}</span></div>
        \${group.items.map(item => \`
          <div class="changeset" data-id="\${item.changesetId}">
            <input type="checkbox" data-id="\${item.changesetId}" aria-label="select">
            <strong>cs\${item.changesetId}</strong>
            <div>
              <div class="comment">\${escape(item.comment || '')}</div>
              <div class="meta">\${escape(item.author?.displayName || '')} · \${formatDate(item.createdAt)}</div>
            </div>
          </div>\`).join('')}
      \`).join('');
      historyEl.querySelectorAll('.changeset').forEach(row => {
        const id = Number(row.dataset.id);
        row.addEventListener('click', event => {
          if (event.target instanceof HTMLInputElement) {
            toggleSelection(id, event.target.checked);
            return;
          }
          if (event.ctrlKey || event.metaKey) {
            toggleSelection(id, !selected.includes(id));
            return;
          }
          document.querySelectorAll('.changeset').forEach(item => item.classList.remove('active'));
          row.classList.add('active');
          vscode.postMessage({ type: 'selectChangeset', changesetId: id });
        });
      });
    }

    function renderChangeset(message) {
      const cs = message.changeset;
      detailsEl.innerHTML = \`
        <div class="card">
          <div class="card-title">cs\${cs.changesetId} · \${escape(cs.author?.displayName || '')}</div>
          <div style="padding:11px"><div class="comment">\${escape(cs.comment || '')}</div><div class="meta">\${formatDate(cs.createdAt)}</div></div>
        </div>
        <div class="card">
          <div class="card-title">\${labels.files} (\${message.files.length})</div>
          <table><thead><tr><th>Type</th><th>Path</th><th></th></tr></thead><tbody>
          \${message.files.map(file => \`
            <tr><td>\${escape(file.changeType)}</td><td class="path">\${escape(file.path)}</td>
            <td class="actions">\${file.isBranch ? '' : \`
              <button data-action="diff" data-path="\${attr(file.path)}" data-version="\${cs.changesetId}">\${labels.diff}</button>
              <button class="secondary" data-action="history" data-path="\${attr(file.path)}">\${labels.history}</button>\`}</td></tr>
          \`).join('')}
          </tbody></table>
        </div>\`;
      bindFileActions();
    }

    function renderComparison(message) {
      detailsEl.innerHTML = \`
        <div class="card"><div class="card-title">cs\${message.from.changesetId} ↔ cs\${message.to.changesetId}</div>
        <div style="padding:11px" class="hint">\${escape(message.from.comment || '')}<br>↔<br>\${escape(message.to.comment || '')}</div></div>
        <div class="card"><div class="card-title">\${labels.files} (\${message.files.length})</div>
        <table><thead><tr><th>Path</th><th>From</th><th>To</th><th></th></tr></thead><tbody>
        \${message.files.map(file => \`
          <tr><td class="path">\${escape(file.relativePath)}</td><td>\${escape(file.fromChangeType || '—')}</td><td>\${escape(file.toChangeType || '—')}</td>
          <td class="actions">\${file.fromPath && file.toPath ? \`<button data-action="pair" data-from="\${attr(file.fromPath)}" data-to="\${attr(file.toPath)}" data-from-version="\${message.from.changesetId}" data-to-version="\${message.to.changesetId}">Diff</button>\` : ''}</td></tr>
        \`).join('')}</tbody></table></div>\`;
      detailsEl.querySelectorAll('[data-action="pair"]').forEach(button => button.addEventListener('click', () => {
        vscode.postMessage({
          type: 'compareFilePair',
          fromPath: button.dataset.from,
          toPath: button.dataset.to,
          fromVersion: Number(button.dataset.fromVersion),
          toVersion: Number(button.dataset.toVersion),
        });
      }));
    }

    function renderFileHistory(message) {
      detailsEl.innerHTML = \`
        <div class="card"><div class="card-title">\${labels.history}: \${escape(message.serverPath)}</div>
        <div style="padding:9px 11px"><button id="compare-file" disabled>\${labels.compareVersions}</button></div>
        <table><tbody>\${message.items.map(item => \`
          <tr><td><input type="checkbox" class="file-version" value="\${item.changesetId}"></td><td><strong>cs\${item.changesetId}</strong></td>
          <td><div>\${escape(item.comment || '')}</div><div class="meta">\${escape(item.author?.displayName || '')} · \${formatDate(item.createdAt)}</div></td></tr>
        \`).join('')}</tbody></table></div>\`;
      let versions = [];
      const button = document.getElementById('compare-file');
      detailsEl.querySelectorAll('.file-version').forEach(input => input.addEventListener('change', () => {
        versions = [...detailsEl.querySelectorAll('.file-version:checked')].map(item => Number(item.value));
        if (versions.length > 2) {
          input.checked = false;
          versions = versions.slice(0, 2);
        }
        button.disabled = versions.length !== 2;
      }));
      button.addEventListener('click', () => vscode.postMessage({ type: 'compareFileVersions', serverPath: message.serverPath, changesetIds: versions }));
    }

    function bindFileActions() {
      detailsEl.querySelectorAll('[data-action="diff"]').forEach(button => button.addEventListener('click', () => {
        vscode.postMessage({ type: 'diffPrevious', serverPath: button.dataset.path, changesetId: Number(button.dataset.version) });
      }));
      detailsEl.querySelectorAll('[data-action="history"]').forEach(button => button.addEventListener('click', () => {
        vscode.postMessage({ type: 'fileHistory', serverPath: button.dataset.path });
      }));
    }

    window.addEventListener('message', event => {
      const message = event.data;
      if (message.type === 'histories') renderHistories(message.groups);
      if (message.type === 'changeset') renderChangeset(message);
      if (message.type === 'comparison') renderComparison(message);
      if (message.type === 'fileHistory') renderFileHistory(message);
      if (message.type === 'error') detailsEl.innerHTML = '<div class="error">' + escape(message.message) + '</div>';
    });

    function formatDate(value) { return value ? new Date(value).toLocaleString() : ''; }
    function escape(value) { const div = document.createElement('div'); div.textContent = String(value ?? ''); return div.innerHTML; }
    function attr(value) { return escape(value).replace(/"/g, '&quot;'); }
    vscode.postMessage({ type: 'ready' });
  </script>
</body>
</html>`;
  }
}

async function showDiffDocument(title: string, diff: DiffResponse): Promise<void> {
  const content = diff.result.kind === 'text'
    ? diff.result.patch ?? ''
    : diff.result.kind === 'binary'
      ? `${title}\n\nBinary files differ.`
      : `${title}\n\nNo differences.`;
  const document = await vscode.workspace.openTextDocument({
    language: diff.result.kind === 'text' ? 'diff' : 'text',
    content,
  });
  await vscode.window.showTextDocument(document, { preview: false });
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
