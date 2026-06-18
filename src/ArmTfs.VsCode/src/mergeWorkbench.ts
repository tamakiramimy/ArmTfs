import { promises as fs } from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as vscode from 'vscode';
import type { ArmTfsCliClient } from './armTfsCliClient';
import type { MergeCandidateResponse, MergeExecuteResponse } from './contracts';
import { t } from './i18n';
import { openServerVersionDiff, openServerVersionDiffFromEmpty } from './versionedFiles';

type MergePlanChange = MergeExecuteResponse['result']['changes'][number];

interface MergeWorkbenchCandidate {
  changesetId: number;
  createdAt: string;
  author?: string;
  comment?: string;
  checked: boolean;
  changes: MergePlanChange[];
  warnings: string[];
}

interface MergeConflictView {
  id: string;
  changesetId: number;
  sourceServerPath: string;
  targetServerPath: string;
  note: string;
  blocking: boolean;
}

interface MergeExecutionResolutionFileItem {
  sourceServerPath: string;
  targetServerPath: string;
  choice: 'source' | 'target' | 'manual';
  contentBase64?: string;
}

interface MergeWorkbenchState {
  sourcePath: string;
  targetPath: string;
  candidates: MergeWorkbenchCandidate[];
  selectedChangesetId?: number;
}

interface WebviewMessage {
  type: string;
  changesetId?: number;
  checked?: boolean;
  conflictId?: string;
  conflictIds?: string[];
  resolution?: 'source' | 'target' | 'manual';
  sourceServerPath?: string;
  targetServerPath?: string;
  sourceChangesetId?: number;
  targetExists?: boolean;
  selectedChangesetIds?: number[];
}

export class ArmTfsMergeWorkbench {
  static async open(
    client: ArmTfsCliClient,
    output: vscode.OutputChannel,
    sourcePath: string,
    targetPath: string,
    candidateResponse: MergeCandidateResponse,
    refreshAfterExecute: () => Promise<void>,
  ): Promise<void> {
    const panel = vscode.window.createWebviewPanel(
      'armTfsMergeWorkbench',
      `TFS Merge: ${path.posix.basename(sourcePath)} -> ${path.posix.basename(targetPath)}`,
      vscode.ViewColumn.One,
      {
        enableScripts: true,
        retainContextWhenHidden: true,
      },
    );

    const workbench = new ArmTfsMergeWorkbench(client, output, panel, refreshAfterExecute);
    await workbench.initialize(sourcePath, targetPath, candidateResponse);
  }

  private state: MergeWorkbenchState = {
    sourcePath: '$/',
    targetPath: '$/',
    candidates: [],
  };

  private readonly disposables: vscode.Disposable[] = [];
  private readonly conflictChecked = new Map<string, boolean>();
  private readonly conflictResolutions = new Map<string, 'source' | 'target' | 'manual'>();
  private readonly manualMergeContents = new Map<string, string>();

  private constructor(
    private readonly client: ArmTfsCliClient,
    private readonly output: vscode.OutputChannel,
    private readonly panel: vscode.WebviewPanel,
    private readonly refreshAfterExecute: () => Promise<void>,
  ) {
    this.disposables.push(
      this.panel.webview.onDidReceiveMessage((message: WebviewMessage) => {
        void this.handleMessage(message);
      }),
      this.panel.onDidDispose(() => vscode.Disposable.from(...this.disposables).dispose()),
    );
  }

  private async initialize(
    sourcePath: string,
    targetPath: string,
    candidateResponse: MergeCandidateResponse,
  ): Promise<void> {
    this.panel.webview.html = renderLoadingHtml(this.panel.webview);
    try {
      const candidates = await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: `arm-tfs merge plan ${path.posix.basename(sourcePath)} -> ${path.posix.basename(targetPath)}`,
        },
        async (progress) => {
          const result: MergeWorkbenchCandidate[] = [];
          for (const [index, item] of candidateResponse.items.entries()) {
            progress.report({
              message: `cs${item.changesetId} (${index + 1}/${candidateResponse.items.length})`,
            });
            const plan = await this.client.mergeExecuteJson(sourcePath, targetPath, item.changesetId, {
              dryRun: true,
            });
            result.push({
              changesetId: item.changesetId,
              createdAt: item.createdAt,
              author: item.author?.displayName,
              comment: item.comment,
              checked: true,
              changes: plan.result.changes,
              warnings: plan.result.warnings,
            });
          }
          return result;
        },
      );

      this.state = {
        sourcePath,
        targetPath,
        candidates,
        selectedChangesetId: candidates[0]?.changesetId,
      };

      // SOAP 3-way conflict preview for the whole candidate range. The REST dry-run above is
      // unreliable for conflicts (misses add/add and edit/edit when the branch point can't be
      // detected), so ask the server directly. Mark any candidate change whose target path is a
      // real conflict as status='conflict' so the conflict list surfaces it.
      if (candidates.length > 0) {
        try {
          const ids = candidates.map((c) => c.changesetId);
          const from = Math.min(...ids);
          const to = Math.max(...ids);
          const preview = await this.client.mergePreviewConflictsJson(sourcePath, targetPath, from, to);
          const conflictedTargets = new Set(
            preview.conflicts.map((c) => c.targetServerPath.toLowerCase()),
          );
          if (conflictedTargets.size > 0) {
            for (const cand of candidates) {
              cand.changes = cand.changes.map((ch) =>
                conflictedTargets.has(ch.targetServerPath.toLowerCase())
                  ? { ...ch, status: 'conflict', note: 'Both source and target modified this file (server 3-way merge conflict). Resolve (source/target/manual).' }
                  : ch,
              );
            }
          }
        } catch (previewError) {
          // Preview is best-effort; if it fails, fall back to REST-detected conflicts only.
          this.output.appendLine(`merge preview-conflicts failed: ${getErrorMessage(previewError)}`);
        }
      }

      for (const conflict of candidates.flatMap(findConflicts)) {
        this.conflictChecked.set(conflict.id, true);
      }
      this.render();
    } catch (error) {
      this.showError('arm-tfs merge plan', error);
      this.panel.webview.html = renderErrorHtml(this.panel.webview, getErrorMessage(error));
    }
  }

  private async handleMessage(message: WebviewMessage): Promise<void> {
    switch (message.type) {
      case 'toggleCandidate':
        if (message.changesetId !== undefined) {
          const candidate = this.state.candidates.find((item) => item.changesetId === message.changesetId);
          if (candidate && message.checked !== undefined) {
            candidate.checked = message.checked;
          }
          this.render();
        }
        break;
      case 'selectCandidate':
        this.state.selectedChangesetId = message.changesetId;
        this.render();
        break;
      case 'toggleAll':
        this.state.candidates.forEach((candidate) => {
          candidate.checked = message.checked ?? true;
        });
        this.render();
        break;
      case 'toggleConflict':
        if (message.conflictId && message.checked !== undefined) {
          this.conflictChecked.set(message.conflictId, message.checked);
        }
        break;
      case 'applyBulkResolution':
        if (message.resolution === 'source' || message.resolution === 'target') {
          for (const conflictId of message.conflictIds ?? []) {
            this.conflictResolutions.set(conflictId, message.resolution);
            this.manualMergeContents.delete(conflictId);
          }
          this.render();
        }
        break;
      case 'chooseResolution':
        if (message.conflictId && (message.resolution === 'source' || message.resolution === 'target')) {
          this.conflictResolutions.set(message.conflictId, message.resolution);
          this.manualMergeContents.delete(message.conflictId);
          this.render();
        }
        break;
      case 'openManualMerge':
        if (message.conflictId) {
          await this.openManualMergeForConflict(message.conflictId);
        }
        break;
      case 'openDiff':
        if (message.sourceServerPath && message.targetServerPath && message.sourceChangesetId !== undefined) {
          await this.openFileDiff(
            message.sourceServerPath,
            message.targetServerPath,
            message.sourceChangesetId,
            message.targetExists ?? true,
          );
        }
        break;
      case 'executeSelected':
        await this.executeSelected(message.selectedChangesetIds ?? []);
        break;
    }
  }

  private async openFileDiff(
    sourceServerPath: string,
    targetServerPath: string,
    sourceChangesetId: number,
    targetExists: boolean,
  ): Promise<void> {
    try {
      if (!targetExists) {
        await openServerVersionDiffFromEmpty(
          this.client,
          {
            serverPath: sourceServerPath,
            version: sourceChangesetId,
            label: `${path.posix.basename(sourceServerPath)} source cs${sourceChangesetId}`,
          },
          `${path.posix.basename(sourceServerPath)}: empty target -> source cs${sourceChangesetId}`,
        );
        return;
      }

      await openServerVersionDiff(
        this.client,
        {
          serverPath: sourceServerPath,
          version: sourceChangesetId,
          label: `${path.posix.basename(sourceServerPath)} source cs${sourceChangesetId}`,
        },
        {
          serverPath: targetServerPath,
          label: `${path.posix.basename(targetServerPath)} target latest`,
        },
        `${path.posix.basename(sourceServerPath)}: source cs${sourceChangesetId} -> target`,
      );
    } catch (error) {
      this.showError('arm-tfs merge file diff', error);
    }
  }

  private async executeSelected(selectedChangesetIds: number[]): Promise<void> {
    const selected = this.state.candidates.filter((candidate) => selectedChangesetIds.includes(candidate.changesetId));
    if (!selected.length) {
      void vscode.window.showWarningMessage('请选择至少一个 Changeset。');
      return;
    }

    const blocking = selected.flatMap((candidate) => findConflicts(candidate).filter((conflict) => conflict.blocking));
    if (blocking.some((conflict) => !this.conflictResolutions.has(conflict.id))) {
      void vscode.window.showWarningMessage('存在未解决的阻断项，请先处理冲突列表后再合并。');
      return;
    }

    const resolutionItemsByChangeset = this.buildResolutionItems(selected);

    const defaultComment = `Merge ${selected.map((item) => `cs${item.changesetId}`).join(', ')} from ${path.posix.basename(this.state.sourcePath)} to ${path.posix.basename(this.state.targetPath)}`;
    const comment = await vscode.window.showInputBox({
      prompt: t('sidebar.prompt.mergeComment'),
      value: defaultComment,
      ignoreFocusOut: true,
    });
    if (comment === undefined) {
      return;
    }

    const outputs: string[] = [];
    const createdIds: number[] = [];
    const noChangeIds: number[] = [];
    const resolutionFiles: string[] = [];
    try {
      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: `arm-tfs merge ${selected.length} changeset(s)`,
        },
        async (progress) => {
          for (const [index, candidate] of selected.entries()) {
            progress.report({
              message: `cs${candidate.changesetId} (${index + 1}/${selected.length})`,
            });
            const resolutionFile = await writeResolutionFile(resolutionItemsByChangeset.get(candidate.changesetId) ?? []);
            resolutionFiles.push(resolutionFile);
            const response = await this.client.mergeExecuteJson(
              this.state.sourcePath,
              this.state.targetPath,
              candidate.changesetId,
              {
                comment: comment.trim() || undefined,
                resolutionFile,
              },
            );
            const createdId = response.result.createdChangesetId;
            if (createdId !== null && createdId !== undefined) {
              createdIds.push(createdId);
            } else {
              noChangeIds.push(candidate.changesetId);
            }
            outputs.push(summarizeMergeResponse(response));
          }
        },
      );

      // Write the full outcome to the output channel (the "console") instead of opening a
      // throwaway document, and surface concise toasts that reflect what actually happened.
      this.output.appendLine('> arm-tfs merge execute');
      this.output.appendLine(outputs.join('\n\n'));
      this.output.appendLine('');

      await this.refreshAfterExecute();

      if (createdIds.length) {
        void vscode.window.showInformationMessage(
          t('merge.execute.success', {
            count: createdIds.length,
            created: createdIds.map((id) => `cs${id}`).join(', '),
          }),
        );
      }
      if (noChangeIds.length) {
        void vscode.window.showWarningMessage(
          t('merge.execute.noChange', {
            changesets: noChangeIds.map((id) => `cs${id}`).join(', '),
          }),
        );
      }

      if (createdIds.length) {
        this.panel.dispose();
      }
    } catch (error) {
      this.showError('arm-tfs merge execute', error);
    } finally {
      await Promise.all(resolutionFiles.map((file) => fs.rm(path.dirname(file), { force: true, recursive: true })));
    }
  }

  private buildResolutionItems(
    selected: MergeWorkbenchCandidate[],
  ): Map<number, MergeExecutionResolutionFileItem[]> {
    const itemsByChangeset = new Map<number, MergeExecutionResolutionFileItem[]>();
    for (const candidate of selected) {
      const items: MergeExecutionResolutionFileItem[] = [];
      for (const conflict of findConflicts(candidate).filter((item) => item.blocking)) {
        const choice = this.conflictResolutions.get(conflict.id);
        if (!choice || !conflict.sourceServerPath) {
          continue;
        }

        const item: MergeExecutionResolutionFileItem = {
          sourceServerPath: conflict.sourceServerPath,
          targetServerPath: conflict.targetServerPath,
          choice,
        };
        if (choice === 'manual') {
          const content = this.manualMergeContents.get(conflict.id);
          if (content === undefined)
            throw new Error(`手动合并结果不存在：${conflict.sourceServerPath}`);
          item.contentBase64 = Buffer.from(content, 'utf8').toString('base64');
        }
        items.push(item);
      }
      itemsByChangeset.set(candidate.changesetId, items);
    }

    return itemsByChangeset;
  }

  private async openManualMergeForConflict(conflictId: string): Promise<void> {
    const candidate = this.state.candidates.find((item) =>
      findConflicts(item).some((conflict) => conflict.id === conflictId));
    const conflict = candidate && findConflicts(candidate).find((item) => item.id === conflictId);
    if (!candidate || !conflict) {
      return;
    }

    const change = candidate.changes.find((item) => item.sourceServerPath === conflict.sourceServerPath);
    if (!change) {
      void vscode.window.showWarningMessage(`找不到 ${conflict.sourceServerPath} 的合并文件记录。`);
      return;
    }

    try {
      const [source, target] = await Promise.all([
        readServerText(this.client, change.sourceServerPath, change.sourceChangesetId),
        change.targetExists ? readServerText(this.client, change.targetServerPath) : Promise.resolve(''),
      ]);
      const content = await openManualMergePanel(
        `${path.posix.basename(change.sourceServerPath)} 手动合并`,
        source,
        target,
        this.manualMergeContents.get(conflictId) ?? source,
      );
      if (content !== undefined) {
        this.manualMergeContents.set(conflictId, content);
        this.conflictResolutions.set(conflictId, 'manual');
        this.render();
      }
    } catch (error) {
      this.showError('arm-tfs manual merge', error);
    }
  }

  private render(): void {
    this.panel.webview.html = renderWorkbenchHtml(
      this.panel.webview,
      this.state,
      this.conflictChecked,
      this.conflictResolutions,
      this.manualMergeContents,
    );
  }

  private showError(title: string, error: unknown): void {
    this.output.show(true);
    const message = getErrorMessage(error);
    this.output.appendLine(message);
    void vscode.window.showErrorMessage(t('error.failed', { title, message }));
  }
}

function renderLoadingHtml(webview: vscode.Webview): string {
  const nonce = getNonce();
  return renderShell(webview, nonce, '<main class="center">正在加载合并计划...</main>', '');
}

function renderErrorHtml(webview: vscode.Webview, message: string): string {
  const nonce = getNonce();
  return renderShell(webview, nonce, `<main class="center error">${escapeHtml(message)}</main>`, '');
}

function renderWorkbenchHtml(
  webview: vscode.Webview,
  state: MergeWorkbenchState,
  conflictChecked: ReadonlyMap<string, boolean>,
  conflictResolutions: ReadonlyMap<string, 'source' | 'target' | 'manual'>,
  manualMergeContents: ReadonlyMap<string, string>,
): string {
  const nonce = getNonce();
  const selected = state.candidates.find((candidate) => candidate.changesetId === state.selectedChangesetId) ?? state.candidates[0];
  const conflicts = state.candidates
    .filter((candidate) => candidate.checked)
    .flatMap(findConflicts);
  const checkedCount = state.candidates.filter((candidate) => candidate.checked).length;
  const hasUnresolvedBlocking = conflicts.some((item) => item.blocking && !conflictResolutions.has(item.id));

  const body = `
    <main>
      <header class="topbar">
        <div>
          <h1>从本分支合并到目标分支</h1>
          <p><code>${escapeHtml(state.sourcePath)}</code> -> <code>${escapeHtml(state.targetPath)}</code></p>
        </div>
        <div class="toolbar">
          <button id="checkAll" title="全部勾选">全选</button>
          <button id="uncheckAll" title="全部取消勾选">全不选</button>
          <button id="execute" class="primary" ${checkedCount && !hasUnresolvedBlocking ? '' : 'disabled'}>合并 ${checkedCount}</button>
        </div>
      </header>
      <section class="grid">
        <section class="pane changesets">
          <div class="pane-title">Changesets</div>
          <div class="list">
            ${state.candidates.map((candidate) => renderCandidate(candidate, selected?.changesetId)).join('')}
          </div>
        </section>
        <section class="pane files">
          <div class="pane-title">文件变更记录 <span>${aggregateFileCount(state.candidates)} 个文件 · ${checkedCount} 个变更集合</span></div>
          <div class="list">
            ${renderFileTree(state.candidates, state.targetPath)}
          </div>
        </section>
        <section class="pane conflicts">
          <div class="pane-title">冲突列表 <span>${conflicts.length}</span></div>
          <div class="bulk">
            <button data-resolution="source">批量采用源分支</button>
            <button data-resolution="target">批量采用目标分支</button>
          </div>
          <div class="list">
            ${conflicts.length
              ? conflicts.map((conflict) => renderConflict(
                conflict,
                conflictChecked.get(conflict.id) ?? true,
                conflictResolutions.get(conflict.id),
                manualMergeContents.has(conflict.id),
              )).join('')
              : '<div class="empty">当前勾选项没有阻断冲突。</div>'}
          </div>
        </section>
      </section>
    </main>`;

  const script = `
    const vscode = acquireVsCodeApi();
    document.querySelectorAll('[data-candidate-check]').forEach((input) => {
      input.addEventListener('change', () => {
        vscode.postMessage({ type: 'toggleCandidate', changesetId: Number(input.dataset.candidateCheck), checked: input.checked });
      });
    });
    document.querySelectorAll('[data-candidate-select]').forEach((button) => {
      button.addEventListener('click', () => {
        vscode.postMessage({ type: 'selectCandidate', changesetId: Number(button.dataset.candidateSelect) });
      });
    });
    document.querySelectorAll('[data-diff]').forEach((button) => {
      button.addEventListener('click', () => {
        vscode.postMessage({
          type: 'openDiff',
          sourceServerPath: button.dataset.source,
          targetServerPath: button.dataset.target,
          sourceChangesetId: Number(button.dataset.version),
          targetExists: button.dataset.targetExists === 'true',
        });
      });
    });
    document.getElementById('checkAll')?.addEventListener('click', () => vscode.postMessage({ type: 'toggleAll', checked: true }));
    document.getElementById('uncheckAll')?.addEventListener('click', () => vscode.postMessage({ type: 'toggleAll', checked: false }));
    document.querySelectorAll('[data-conflict-check]').forEach((input) => {
      input.addEventListener('change', () => {
        vscode.postMessage({ type: 'toggleConflict', conflictId: input.dataset.conflictCheck, checked: input.checked });
      });
    });
    document.querySelectorAll('[data-resolution]').forEach((button) => {
      button.addEventListener('click', () => {
        const conflictIds = Array.from(document.querySelectorAll('[data-conflict-check]:checked'))
          .map((input) => input.dataset.conflictCheck);
        vscode.postMessage({
          type: 'applyBulkResolution',
          conflictIds,
          resolution: button.dataset.resolution,
        });
      });
    });
    document.querySelectorAll('[data-conflict-choice]').forEach((button) => {
      button.addEventListener('click', () => {
        const node = button.closest('[data-conflict]');
        if (!node) return;
        const resolution = button.dataset.conflictChoice;
        if (resolution === 'manual') {
          vscode.postMessage({ type: 'openManualMerge', conflictId: node.dataset.conflict });
          return;
        }
        vscode.postMessage({ type: 'chooseResolution', conflictId: node.dataset.conflict, resolution });
      });
    });
    function refreshExecuteState() {
      const execute = document.getElementById('execute');
      if (!execute) return;
      const selectedChangesetIds = Array.from(document.querySelectorAll('[data-candidate-check]:checked')).map((input) => Number(input.dataset.candidateCheck));
      const unresolvedBlocking = Array.from(document.querySelectorAll('[data-conflict][data-blocking="true"]')).some((node) => !node.dataset.choice);
      execute.disabled = selectedChangesetIds.length === 0 || unresolvedBlocking;
      execute.textContent = '合并 ' + selectedChangesetIds.length;
    }
    document.querySelectorAll('[data-candidate-check]').forEach((input) => input.addEventListener('change', refreshExecuteState));
    document.getElementById('execute')?.addEventListener('click', () => {
      const selectedChangesetIds = Array.from(document.querySelectorAll('[data-candidate-check]:checked')).map((input) => Number(input.dataset.candidateCheck));
      vscode.postMessage({ type: 'executeSelected', selectedChangesetIds });
    });
    refreshExecuteState();
  `;

  return renderShell(webview, nonce, body, script);
}

function renderShell(webview: vscode.Webview, nonce: string, body: string, script: string): string {
  const csp = [
    `default-src 'none'`,
    `style-src ${webview.cspSource} 'unsafe-inline'`,
    `script-src 'nonce-${nonce}'`,
  ].join('; ');

  return `<!DOCTYPE html>
<html lang="zh-CN">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="Content-Security-Policy" content="${csp}">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <style>
    :root {
      --bg: var(--vscode-editor-background);
      --fg: var(--vscode-editor-foreground);
      --muted: var(--vscode-descriptionForeground);
      --border: var(--vscode-panel-border);
      --button: var(--vscode-button-background);
      --button-fg: var(--vscode-button-foreground);
      --danger: var(--vscode-errorForeground);
      --selection: var(--vscode-list-activeSelectionBackground);
      --row: var(--vscode-list-hoverBackground);
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      background: var(--bg);
      color: var(--fg);
      font-family: var(--vscode-font-family);
      font-size: var(--vscode-font-size);
    }
    main { min-height: 100vh; }
    .center {
      display: grid;
      min-height: 100vh;
      place-items: center;
      color: var(--muted);
    }
    .error { color: var(--danger); padding: 24px; text-align: center; }
    .topbar {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      padding: 14px 18px;
      border-bottom: 1px solid var(--border);
    }
    h1 {
      margin: 0 0 6px;
      font-size: 18px;
      font-weight: 650;
      letter-spacing: 0;
    }
    p { margin: 0; color: var(--muted); }
    code { color: var(--fg); }
    .toolbar, .bulk {
      display: flex;
      gap: 8px;
      align-items: center;
      flex-wrap: wrap;
    }
    button {
      border: 1px solid var(--border);
      background: var(--vscode-button-secondaryBackground);
      color: var(--vscode-button-secondaryForeground);
      min-height: 28px;
      padding: 4px 10px;
      border-radius: 3px;
      cursor: pointer;
      font: inherit;
    }
    button:hover { background: var(--vscode-button-secondaryHoverBackground); }
    button.primary {
      background: var(--button);
      color: var(--button-fg);
      border-color: var(--button);
    }
    button:disabled {
      opacity: 0.45;
      cursor: not-allowed;
    }
    .grid {
      display: grid;
      grid-template-columns: minmax(260px, 0.95fr) minmax(360px, 1.4fr) minmax(280px, 1fr);
      height: calc(100vh - 73px);
    }
    .pane {
      min-width: 0;
      border-right: 1px solid var(--border);
      display: flex;
      flex-direction: column;
    }
    .pane:last-child { border-right: 0; }
    .pane-title {
      display: flex;
      justify-content: space-between;
      gap: 8px;
      padding: 10px 12px;
      font-weight: 650;
      border-bottom: 1px solid var(--border);
    }
    .pane-title span { color: var(--muted); font-weight: 400; }
    .list {
      overflow: auto;
      min-height: 0;
      flex: 1;
    }
    .candidate, .file, .conflict {
      border-bottom: 1px solid var(--border);
      padding: 10px 12px;
    }
    .candidate {
      display: grid;
      grid-template-columns: 24px minmax(0, 1fr);
      gap: 8px;
      align-items: start;
    }
    .candidate.active, .file:hover { background: var(--row); }
    .candidate-main, .file-main {
      min-width: 0;
      border: 0;
      background: transparent;
      color: inherit;
      padding: 0;
      text-align: left;
      cursor: pointer;
    }
    .title {
      display: flex;
      gap: 8px;
      align-items: center;
      min-width: 0;
      font-weight: 650;
    }
    .title span, .meta, .path {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .meta, .path, .note {
      color: var(--muted);
      margin-top: 4px;
      line-height: 1.45;
    }
    .badge {
      border: 1px solid var(--border);
      border-radius: 999px;
      padding: 1px 6px;
      color: var(--muted);
      font-size: 11px;
      flex: none;
    }
    .badge.warn { color: var(--danger); border-color: var(--danger); }
    .file {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      gap: 12px;
      align-items: center;
    }
    .tree { padding: 6px 8px; }
    .tree-folder { padding-left: 14px; }
    .tree-folder > summary {
      cursor: pointer; user-select: none; padding: 3px 4px; border-radius: 4px;
      font-weight: 600; color: var(--fg); list-style: none;
    }
    .tree-folder > summary::-webkit-details-marker { display: none; }
    .tree-folder > summary:hover { background: var(--row); }
    .tree-folder[open] > summary { color: var(--accent); }
    .tree-children { padding-left: 6px; border-left: 1px dashed var(--border); margin-left: 10px; }
    .tree-file {
      display: flex; flex-wrap: wrap; align-items: center; gap: 6px;
      padding: 4px 6px 4px 14px; border-radius: 4px;
    }
    .tree-file:hover { background: var(--row); }
    .tree-file-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .tree-file-cs { color: var(--muted); font-size: 11px; margin-left: auto; }
    .tree-file.is-conflict .tree-file-name { color: var(--danger); font-weight: 600; }
    .empty {
      padding: 24px 12px;
      color: var(--muted);
      text-align: center;
    }
    .bulk {
      padding: 10px 12px;
      border-bottom: 1px solid var(--border);
    }
    .conflict {
      display: grid;
      grid-template-columns: 24px minmax(0, 1fr);
      gap: 8px;
      align-items: start;
    }
    .conflict-body { min-width: 0; }
    .conflict[data-choice="source"] .choice-source,
    .conflict[data-choice="target"] .choice-target,
    .conflict[data-choice="manual"] .choice-manual {
      background: var(--button);
      color: var(--button-fg);
      border-color: var(--button);
    }
    .conflict-actions {
      display: flex;
      gap: 6px;
      margin-top: 8px;
      flex-wrap: wrap;
    }
    input[type="checkbox"] {
      margin-top: 2px;
    }
    @media (max-width: 900px) {
      .topbar { align-items: stretch; flex-direction: column; }
      .grid {
        grid-template-columns: 1fr;
        height: auto;
      }
      .pane {
        min-height: 320px;
        border-right: 0;
        border-bottom: 1px solid var(--border);
      }
    }
  </style>
</head>
<body>
${body}
<script nonce="${nonce}">
${script}
</script>
</body>
</html>`;
}

function renderCandidate(candidate: MergeWorkbenchCandidate, selectedChangesetId?: number): string {
  const conflictCount = findConflicts(candidate).length;
  const comment = candidate.comment?.trim() || '(no comment)';
  return `
    <article class="candidate ${candidate.changesetId === selectedChangesetId ? 'active' : ''}">
      <input type="checkbox" data-candidate-check="${candidate.changesetId}" ${candidate.checked ? 'checked' : ''} aria-label="勾选 cs${candidate.changesetId}">
      <button class="candidate-main" data-candidate-select="${candidate.changesetId}">
        <div class="title">
          <span>cs${candidate.changesetId}</span>
          <span class="badge">${candidate.changes.length} files</span>
          ${conflictCount ? `<span class="badge warn">${conflictCount} conflicts</span>` : ''}
        </div>
        <div class="meta">${escapeHtml(candidate.author ?? 'unknown')} | ${escapeHtml(candidate.createdAt.slice(0, 10))}</div>
        <div class="meta">${escapeHtml(comment)}</div>
      </button>
    </article>`;
}

type FileTreeNode = {
  name: string;
  children: Map<string, FileTreeNode>;
  files: AggregatedFile[];
};

interface AggregatedFile {
  targetServerPath: string;
  sourceServerPath: string;
  sourceChangeType: string;
  targetChangeType: string;
  status: string;
  note?: string;
  sourceChangesetId: number;
  targetExists: boolean;
  changesets: number[];
}

function relativeFilePath(serverPath: string, rootPath: string): string {
  const root = rootPath.endsWith('/') ? rootPath : rootPath + '/';
  return serverPath.toLowerCase().startsWith(root.toLowerCase())
    ? serverPath.slice(root.length)
    : serverPath;
}

function aggregateFileCount(candidates: MergeWorkbenchCandidate[]): number {
  const paths = new Set<string>();
  for (const c of candidates) {
    if (!c.checked) continue;
    for (const ch of c.changes) paths.add(ch.targetServerPath);
  }
  return paths.size;
}

function buildFileTree(candidates: MergeWorkbenchCandidate[], targetPath: string): FileTreeNode {
  const root: FileTreeNode = { name: '', children: new Map(), files: [] };
  const byPath = new Map<string, AggregatedFile>();
  for (const cand of candidates) {
    if (!cand.checked) continue;
    for (const ch of cand.changes) {
      const existing = byPath.get(ch.targetServerPath);
      if (existing) {
        if (!existing.changesets.includes(cand.changesetId)) existing.changesets.push(cand.changesetId);
      } else {
        byPath.set(ch.targetServerPath, {
          targetServerPath: ch.targetServerPath,
          sourceServerPath: ch.sourceServerPath,
          sourceChangeType: ch.sourceChangeType,
          targetChangeType: ch.targetChangeType,
          status: ch.status,
          note: ch.note,
          sourceChangesetId: ch.sourceChangesetId,
          targetExists: ch.targetExists,
          changesets: [cand.changesetId],
        });
      }
    }
  }

  for (const file of byPath.values()) {
    const segments = relativeFilePath(file.targetServerPath, targetPath).split('/').filter(Boolean);
    let node = root;
    for (let i = 0; i < segments.length - 1; i++) {
      const seg = segments[i];
      let child = node.children.get(seg);
      if (!child) {
        child = { name: seg, children: new Map(), files: [] };
        node.children.set(seg, child);
      }
      node = child;
    }
    node.files.push(file);
  }
  return root;
}

function countFiles(node: FileTreeNode): number {
  let n = node.files.length;
  for (const child of node.children.values()) n += countFiles(child);
  return n;
}

function renderFileTree(candidates: MergeWorkbenchCandidate[], targetPath: string): string {
  const checked = candidates.filter((c) => c.checked);
  if (checked.length === 0) return '<div class="empty">勾选变更集合后将显示所有文件变更。</div>';
  const root = buildFileTree(candidates, targetPath);
  if (countFiles(root) === 0) return '<div class="empty">勾选的变更集合没有可执行的文件变更。</div>';
  return `<div class="tree">${renderTreeNode(root)}</div>`;
}

function renderTreeNode(node: FileTreeNode): string {
  const folders = [...node.children.values()].sort((a, b) => a.name.localeCompare(b.name));
  const files = node.files.sort((a, b) =>
    path.posix.basename(a.targetServerPath).localeCompare(path.posix.basename(b.targetServerPath)),
  );
  const items: string[] = [];
  for (const folder of folders) {
    items.push(`
      <details class="tree-folder">
        <summary>📁 ${escapeHtml(folder.name)} <span class="badge">${countFiles(folder)}</span></summary>
        <div class="tree-children">${renderTreeNode(folder)}</div>
      </details>`);
  }
  for (const file of files) {
    const name = path.posix.basename(file.targetServerPath);
    const csList = file.changesets.map((cs) => `cs${cs}`).join(', ');
    const isConflict = file.status.toLowerCase() === 'conflict';
    items.push(`
      <div class="tree-file ${isConflict ? 'is-conflict' : ''}">
        <span class="tree-file-name" title="${escapeAttribute(file.targetServerPath)}">📄 ${escapeHtml(name)}</span>
        <span class="badge">${escapeHtml(file.sourceChangeType)}→${escapeHtml(file.targetChangeType)}</span>
        ${isConflict ? '<span class="badge warn">冲突</span>' : ''}
        <span class="tree-file-cs" title="涉及的变更集合">${escapeHtml(csList)}</span>
        <button data-diff data-source="${escapeAttribute(file.sourceServerPath)}" data-target="${escapeAttribute(file.targetServerPath)}" data-version="${file.sourceChangesetId}" data-target-exists="${file.targetExists ? 'true' : 'false'}" title="打开左右文件对比">对比</button>
      </div>`);
  }
  return items.join('');
}

function renderConflict(
  conflict: MergeConflictView,
  checked: boolean,
  choice?: 'source' | 'target' | 'manual',
  hasManualResult = false,
): string {
  return `
    <article
      class="conflict"
      data-conflict="${escapeAttribute(conflict.id)}"
      data-blocking="${conflict.blocking ? 'true' : 'false'}"
      data-choice="${choice ?? ''}">
      <input
        type="checkbox"
        data-conflict-check="${escapeAttribute(conflict.id)}"
        ${checked ? 'checked' : ''}
        aria-label="选择 cs${conflict.changesetId} 冲突">
      <div class="conflict-body">
        <div class="title">
          <span>cs${conflict.changesetId}</span>
          <span class="badge ${conflict.blocking ? 'warn' : ''}">${conflict.blocking ? 'blocking' : 'review'}</span>
          ${hasManualResult ? '<span class="badge">已有手动结果</span>' : ''}
        </div>
        <div class="path">${escapeHtml(conflict.sourceServerPath)}</div>
        <div class="path">${escapeHtml(conflict.targetServerPath)}</div>
        <div class="note">${escapeHtml(conflict.note)}</div>
        <div class="conflict-actions">
          <button class="choice-source" data-conflict-choice="source">采用源分支</button>
          <button class="choice-target" data-conflict-choice="target">采用目标分支</button>
          <button class="choice-manual" data-conflict-choice="manual">手动合并</button>
        </div>
      </div>
    </article>`;
}

function summarizeMergeResponse(response: MergeExecuteResponse): string {
  const result = response.result;
  const lines: string[] = [];
  const createdId = result.createdChangesetId;
  const header = createdId !== null && createdId !== undefined
    ? `cs${result.sourceChangesetId} -> created cs${createdId}`
    : `cs${result.sourceChangesetId} -> no changeset created`;
  lines.push(header);
  for (const change of result.changes) {
    lines.push(`  [${change.status}] ${change.targetServerPath} (${change.sourceChangeType} -> ${change.targetChangeType})`);
    if (change.note) {
      lines.push(`      note: ${change.note}`);
    }
  }
  for (const warning of result.warnings) {
    lines.push(`  warning: ${warning}`);
  }
  return lines.join('\n');
}

function findConflicts(candidate: MergeWorkbenchCandidate): MergeConflictView[] {
  return candidate.changes
    .filter((change) => {
      const status = change.status.toLowerCase();
      // 'conflict' = both sides modified the same file (needs source/target/manual resolution).
      // 'skipped'   = unsupported change type that the merge plan could not execute.
      // Both must be surfaced and resolved before the merge can proceed.
      return status === 'conflict' || status === 'skipped';
    })
    .map((change) => ({
      id: `${candidate.changesetId}:${change.sourceServerPath}`,
      changesetId: candidate.changesetId,
      sourceServerPath: change.sourceServerPath,
      targetServerPath: change.targetServerPath,
      note: change.note || `Status: ${change.status}`,
      blocking: true,
    }));
}

async function writeResolutionFile(items: MergeExecutionResolutionFileItem[]): Promise<string> {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'arm-tfs-merge-'));
  const filePath = path.join(directory, 'resolutions.json');
  await fs.writeFile(filePath, JSON.stringify(items, null, 2), 'utf8');
  return filePath;
}

async function readServerText(client: ArmTfsCliClient, serverPath: string, version?: number): Promise<string> {
  const response = await client.itemContent(serverPath, version);
  if (response.item.isBinary) {
    throw new Error(`无法手动合并二进制文件：${serverPath}`);
  }
  return Buffer.from(response.item.contentBase64, 'base64').toString('utf8');
}

function openManualMergePanel(
  title: string,
  sourceContent: string,
  targetContent: string,
  initialResult: string,
): Promise<string | undefined> {
  const panel = vscode.window.createWebviewPanel(
    'armTfsManualMerge',
    title,
    vscode.ViewColumn.Active,
    {
      enableScripts: true,
      retainContextWhenHidden: true,
    },
  );
  const nonce = getNonce();
  panel.webview.html = renderManualMergeHtml(panel.webview, nonce, sourceContent, targetContent, initialResult);

  return new Promise((resolve) => {
    const disposable = panel.webview.onDidReceiveMessage((message: { type: string; content?: string }) => {
      if (message.type === 'save') {
        disposable.dispose();
        resolve(message.content ?? '');
        panel.dispose();
      }
      if (message.type === 'cancel') {
        disposable.dispose();
        resolve(undefined);
        panel.dispose();
      }
    });
    panel.onDidDispose(() => {
      disposable.dispose();
      resolve(undefined);
    });
  });
}

function renderManualMergeHtml(
  webview: vscode.Webview,
  nonce: string,
  sourceContent: string,
  targetContent: string,
  initialResult: string,
): string {
  const csp = [
    `default-src 'none'`,
    `style-src ${webview.cspSource} 'unsafe-inline'`,
    `script-src 'nonce-${nonce}'`,
  ].join('; ');

  return `<!DOCTYPE html>
<html lang="zh-CN">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="Content-Security-Policy" content="${csp}">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <style>
    :root {
      --bg: var(--vscode-editor-background);
      --fg: var(--vscode-editor-foreground);
      --muted: var(--vscode-descriptionForeground);
      --border: var(--vscode-panel-border);
      --button: var(--vscode-button-background);
      --button-fg: var(--vscode-button-foreground);
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      background: var(--bg);
      color: var(--fg);
      font-family: var(--vscode-font-family);
      font-size: var(--vscode-font-size);
    }
    header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 12px;
      padding: 10px 14px;
      border-bottom: 1px solid var(--border);
    }
    h1 { margin: 0; font-size: 16px; }
    button {
      border: 1px solid var(--border);
      background: var(--vscode-button-secondaryBackground);
      color: var(--vscode-button-secondaryForeground);
      min-height: 28px;
      padding: 4px 10px;
      cursor: pointer;
      font: inherit;
    }
    button.primary {
      background: var(--button);
      color: var(--button-fg);
      border-color: var(--button);
    }
    .grid {
      display: grid;
      grid-template-rows: minmax(220px, 1fr) minmax(220px, 1fr);
      height: calc(100vh - 50px);
    }
    .top {
      display: grid;
      grid-template-columns: 1fr 1fr;
      min-height: 0;
      border-bottom: 1px solid var(--border);
    }
    section {
      min-width: 0;
      min-height: 0;
      display: flex;
      flex-direction: column;
      border-right: 1px solid var(--border);
    }
    section:last-child { border-right: 0; }
    .title {
      padding: 8px 10px;
      font-weight: 650;
      color: var(--muted);
      border-bottom: 1px solid var(--border);
    }
    pre, textarea {
      margin: 0;
      flex: 1;
      min-height: 0;
      padding: 10px;
      overflow: auto;
      white-space: pre;
      border: 0;
      outline: 0;
      color: var(--fg);
      background: var(--bg);
      font-family: var(--vscode-editor-font-family);
      font-size: var(--vscode-editor-font-size);
      resize: none;
    }
    @media (max-width: 900px) {
      .top { grid-template-columns: 1fr; }
    }
  </style>
</head>
<body>
  <header>
    <h1>手动合并</h1>
    <div>
      <button id="cancel">取消</button>
      <button id="save" class="primary">使用合并结果</button>
    </div>
  </header>
  <main class="grid">
    <div class="top">
      <section>
        <div class="title">源分支文件</div>
        <pre>${escapeHtml(sourceContent)}</pre>
      </section>
      <section>
        <div class="title">目标分支文件</div>
        <pre>${escapeHtml(targetContent)}</pre>
      </section>
    </div>
    <section>
      <div class="title">手动合并结果</div>
      <textarea id="result">${escapeHtml(initialResult)}</textarea>
    </section>
  </main>
  <script nonce="${nonce}">
    const vscode = acquireVsCodeApi();
    document.getElementById('cancel').addEventListener('click', () => vscode.postMessage({ type: 'cancel' }));
    document.getElementById('save').addEventListener('click', () => {
      vscode.postMessage({ type: 'save', content: document.getElementById('result').value });
    });
  </script>
</body>
</html>`;
}

function getNonce(): string {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  let text = '';
  for (let i = 0; i < 32; i += 1) {
    text += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return text;
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function escapeAttribute(value: string): string {
  return escapeHtml(value).replace(/`/g, '&#96;');
}

function getErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
