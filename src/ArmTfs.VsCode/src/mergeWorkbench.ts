import { promises as fs } from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as vscode from 'vscode';
import type { ArmTfsCliClient } from './armTfsCliClient';
import type { MergeCandidateResponse, MergeExecuteResponse } from './contracts';
import { t } from './i18n';
import { getConfigValue } from './userConfig';
import { openServerVersionDiff, openServerVersionDiffFromEmpty } from './versionedFiles';

type MergePlanChange = MergeExecuteResponse['result']['changes'][number];

const DEFAULT_PLAN_CONCURRENCY = 4;
const MAX_PLAN_CONCURRENCY = 16;

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
  rangeConflicts: MergePlanChange[];
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
    rangeConflicts: [],
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
          const total = candidateResponse.items.length;
          const concurrency = getMergePlanConcurrency();
          let completed = 0;
          progress.report({
            message: `loading ${total} changeset plan(s), ${Math.min(concurrency, Math.max(total, 1))} parallel`,
          });

          return mapWithConcurrency(candidateResponse.items, concurrency, async (item) => {
            progress.report({
              message: `cs${item.changesetId} loading`,
            });
            const plan = await this.client.mergeExecuteJson(sourcePath, targetPath, item.changesetId, {
              dryRun: true,
            });
            completed += 1;
            progress.report({
              message: `cs${item.changesetId} (${completed}/${total})`,
            });
            return {
              changesetId: item.changesetId,
              createdAt: item.createdAt,
              author: item.author?.displayName,
              comment: item.comment,
              checked: true,
              changes: plan.result.changes,
              warnings: plan.result.warnings,
            };
          });
        },
      );

      this.state = {
        sourcePath,
        targetPath,
        candidates,
        rangeConflicts: [],
        selectedChangesetId: candidates[0]?.changesetId,
      };

      // SOAP 3-way merge plan for the whole candidate range. The per-changeset REST dry-run above
      // is useful for mapping changes to candidates, but it can miss conflicts. A single server
      // PendMerge over the range returns every unresolved conflict up front, so the workbench no
      // longer discovers new conflict files halfway through executing selected changesets.
      if (candidates.length > 0) {
        try {
          const ids = candidates.map((c) => c.changesetId);
          const from = Math.min(...ids);
          const to = Math.max(...ids);
          const rangePlan = await this.client.mergeExecuteRangeJson(sourcePath, targetPath, from, to, {
            dryRun: true,
          });
          this.mergeRangeConflicts(
            rangePlan.result.changes.filter((change) => change.status.toLowerCase() === 'conflict'),
          );
        } catch (previewError) {
          // Preview is best-effort; if it fails, fall back to REST-detected conflicts only.
          this.output.appendLine(`merge range dry-run failed: ${getErrorMessage(previewError)}`);
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
            label: `${path.posix.basename(sourceServerPath)} 源分支 cs${sourceChangesetId}`,
          },
          `${path.posix.basename(sourceServerPath)}: 空目标 -> 源分支 cs${sourceChangesetId}`,
        );
        return;
      }

      await openServerVersionDiff(
        this.client,
        {
          serverPath: sourceServerPath,
          version: sourceChangesetId,
          label: `${path.posix.basename(sourceServerPath)} 源分支 cs${sourceChangesetId}`,
        },
        {
          serverPath: targetServerPath,
          label: `${path.posix.basename(targetServerPath)} 目标分支最新`,
        },
        `${path.posix.basename(sourceServerPath)}: 源分支 cs${sourceChangesetId} -> 目标分支`,
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

    const unresolvedConflict = aggregateConflicts(selected).find(
      (f) => !this.conflictResolutions.has(f.targetServerPath),
    );
    if (unresolvedConflict) {
      void vscode.window.showWarningMessage('存在未解决的冲突文件，请先在冲突列表中处理后再合并。');
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
    const conflictAbortIds: number[] = [];
    const resolutionFiles: string[] = [];
    let abortedByConflict = false;
    try {
      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: `arm-tfs merge ${selected.length} changeset(s)`,
        },
        async (progress) => {
          for (const [index, candidate] of selected.entries()) {
            if (abortedByConflict) break;
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
            const conflictChanges = response.result.changes.filter((c) => c.status.toLowerCase() === 'conflict');
            if (conflictChanges.length > 0) {
              // The server's 3-way merge found conflicts the workbench preview did not surface
              // (e.g. per-changeset conflicts differ from the range preview, or state changed
              // after intermediate merges). Mark them on the candidate so the conflict list shows
              // them and the user can resolve (批量采用源/目标 or per-file), then retry.
              conflictAbortIds.push(candidate.changesetId);
              this.applyConflictResultToCandidate(candidate, conflictChanges);
              abortedByConflict = true;
            } else if (createdId !== null && createdId !== undefined) {
              createdIds.push(createdId);
            } else {
              noChangeIds.push(candidate.changesetId);
            }
            outputs.push(summarizeMergeResponse(response));
          }
        },
      );

      this.output.appendLine('> arm-tfs merge execute');
      this.output.appendLine(outputs.join('\n\n'));
      this.output.appendLine('');

      await this.refreshAfterExecute();

      if (abortedByConflict) {
        this.render();
        void vscode.window.showWarningMessage(
          `cs${conflictAbortIds.join(', cs')} 合并中止：服务器检测到冲突，已列入冲突列表。请解决（批量采用源/目标或逐个）后重试。`,
        );
      } else if (createdIds.length) {
        void vscode.window.showInformationMessage(
          t('merge.execute.success', {
            count: createdIds.length,
            created: createdIds.map((id) => `cs${id}`).join(', '),
          }),
        );
        if (noChangeIds.length) {
          void vscode.window.showWarningMessage(
            t('merge.execute.noChange', {
              changesets: noChangeIds.map((id) => `cs${id}`).join(', '),
            }),
          );
        }
      }

      if (createdIds.length && !abortedByConflict) {
        this.panel.dispose();
      }
    } catch (error) {
      this.showError('arm-tfs merge execute', error);
    } finally {
      await Promise.all(resolutionFiles.map((file) => fs.rm(path.dirname(file), { force: true, recursive: true })));
    }
  }

  /** 把执行结果里的冲突回填到候选的 changes 上，让冲突列表显示出来供用户解决。 */
  private applyConflictResultToCandidate(candidate: MergeWorkbenchCandidate, conflictChanges: MergePlanChange[]): void {
    const conflictByTarget = new Map(
      conflictChanges.map((c) => [c.targetServerPath.toLowerCase(), c]),
    );
    for (const change of candidate.changes) {
      const cc = conflictByTarget.get(change.targetServerPath.toLowerCase());
      if (cc) {
        change.status = 'conflict';
        change.note = cc.note || 'Both source and target modified this file (server 3-way merge conflict). Resolve (source/target/manual).';
        conflictByTarget.delete(change.targetServerPath.toLowerCase());
      }
    }
    // Conflict files not already in the plan (e.g. files the REST dry-run didn't list) — add them.
    for (const cc of conflictByTarget.values()) {
      candidate.changes.push({ ...cc, status: 'conflict' });
    }
  }

  /** 把 range SOAP dry-run 中发现的全部冲突存入 rangeConflicts，并标记已存在的候选变更。 */
  private mergeRangeConflicts(conflictChanges: MergePlanChange[]): void {
    if (conflictChanges.length === 0) return;
    const byTarget = new Map(
      this.state.rangeConflicts.map((c) => [c.targetServerPath.replace(/\\/g, '/').replace(/\/+$/u, '').toLowerCase(), c]),
    );
    for (const conflict of conflictChanges) {
      const key = conflict.targetServerPath.replace(/\\/g, '/').replace(/\/+$/u, '').toLowerCase();
      byTarget.set(key, { ...conflict, status: 'conflict' });
    }
    this.state.rangeConflicts = [...byTarget.values()];
    // Only mark existing candidate changes - don't add new ones
    this.markExistingCandidateChangesAsRangeConflicts(conflictChanges);
  }

  private markExistingCandidateChangesAsRangeConflicts(conflictChanges: MergePlanChange[]): void {
    const conflictByTarget = new Map(
      conflictChanges.map((c) => [c.targetServerPath.replace(/\\/g, '/').replace(/\/+$/u, '').toLowerCase(), c]),
    );
    for (const candidate of this.state.candidates) {
      for (const change of candidate.changes) {
        const key = change.targetServerPath.replace(/\\/g, '/').replace(/\/+$/u, '').toLowerCase();
        const conflict = conflictByTarget.get(key);
        if (conflict) {
          change.status = 'conflict';
          change.sourceServerPath = conflict.sourceServerPath || change.sourceServerPath;
          change.note = conflict.note || 'Both source and target modified this file.';
        }
      }
    }
  }

  private buildResolutionItems(
    selected: MergeWorkbenchCandidate[],
  ): Map<number, MergeExecutionResolutionFileItem[]> {
    const itemsByChangeset = new Map<number, MergeExecutionResolutionFileItem[]>();
    for (const candidate of selected) {
      const items: MergeExecutionResolutionFileItem[] = [];
      for (const change of candidate.changes) {
        if (change.status.toLowerCase() !== 'conflict') continue;
        const choice = this.conflictResolutions.get(change.targetServerPath);
        if (!choice) continue;

        const item: MergeExecutionResolutionFileItem = {
          sourceServerPath: change.sourceServerPath,
          targetServerPath: change.targetServerPath,
          choice,
        };
        if (choice === 'manual') {
          const content = this.manualMergeContents.get(change.targetServerPath);
          if (content === undefined)
            throw new Error(`手动合并结果不存在：${change.sourceServerPath}`);
          item.contentBase64 = Buffer.from(content, 'utf8').toString('base64');
        }
        items.push(item);
      }
      itemsByChangeset.set(candidate.changesetId, items);
    }

    return itemsByChangeset;
  }

  private async openManualMergeForConflict(conflictId: string): Promise<void> {
    // conflictId is the conflict file's target server path.
    const candidate = this.state.candidates.find((item) =>
      item.changes.some((ch) => ch.status.toLowerCase() === 'conflict' && ch.targetServerPath === conflictId));
    const change = candidate?.changes.find(
      (ch) => ch.status.toLowerCase() === 'conflict' && ch.targetServerPath === conflictId,
    );
    if (!candidate || !change) {
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
        this.manualMergeContents.get(conflictId) || '',
        change.sourceServerPath,
        change.targetServerPath,
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
  const conflictFiles = aggregateConflicts(state.candidates);
  const checkedCount = state.candidates.filter((candidate) => candidate.checked).length;
  const hasUnresolvedBlocking = conflictFiles.some((f) => !conflictResolutions.has(f.targetServerPath));
  const fileCount = aggregateFileCount(state.candidates);

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
        <section class="pane files" id="filesPane">
          <div class="pane-title">
            <span>文件变更记录 <span class="count">${fileCount} 个文件 · ${checkedCount} 个变更集合</span></span>
            <span class="pane-actions">
              <button id="expandAllFiles" title="展开全部">展开全部</button>
              <button id="collapseAllFiles" title="收缩全部">收缩全部</button>
            </span>
          </div>
          <div class="list">
            ${renderFileTree(state.candidates, state.targetPath)}
          </div>
        </section>
        <section class="pane conflicts">
          <div class="pane-title">
            <span>冲突列表 <span class="count">${conflictFiles.length} 个冲突文件</span></span>
            <span class="pane-actions">
              <button data-resolution="source" title="所有冲突采用源分支">批量采用源</button>
              <button data-resolution="target" title="所有冲突采用目标分支">批量采用目标</button>
            </span>
          </div>
          <div class="list">
            ${renderConflictTree(state.candidates, state.targetPath, conflictResolutions, manualMergeContents)}
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
    document.getElementById('expandAllFiles')?.addEventListener('click', () => {
      document.querySelectorAll('#filesPane details.tree-folder').forEach((d) => { d.open = true; });
    });
    document.getElementById('collapseAllFiles')?.addEventListener('click', () => {
      document.querySelectorAll('#filesPane details.tree-folder').forEach((d) => { d.open = false; });
    });
    document.querySelectorAll('[data-resolution]').forEach((button) => {
      button.addEventListener('click', () => {
        const conflictIds = Array.from(document.querySelectorAll('.conflict-leaf'))
          .map((node) => node.dataset.conflict);
        vscode.postMessage({
          type: 'applyBulkResolution',
          conflictIds,
          resolution: button.dataset.resolution,
        });
      });
    });
    document.querySelectorAll('[data-conflict-choice]').forEach((button) => {
      button.addEventListener('click', () => {
        const target = button.dataset.conflict;
        if (!target) return;
        const resolution = button.dataset.conflictChoice;
        if (resolution === 'manual') {
          vscode.postMessage({ type: 'openManualMerge', conflictId: target });
          return;
        }
        vscode.postMessage({ type: 'chooseResolution', conflictId: target, resolution });
      });
    });
    function refreshExecuteState() {
      const execute = document.getElementById('execute');
      if (!execute) return;
      const selectedChangesetIds = Array.from(document.querySelectorAll('[data-candidate-check]:checked')).map((input) => Number(input.dataset.candidateCheck));
      const unresolved = Array.from(document.querySelectorAll('.conflict-leaf')).some((node) => !node.dataset.choice);
      execute.disabled = selectedChangesetIds.length === 0 || unresolved;
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

function codiconFontFace(webview: vscode.Webview): string {
  const ext = vscode.extensions.getExtension('local.arm-tfs-vscode');
  if (!ext) return '';
  const fontUri = webview.asWebviewUri(vscode.Uri.joinPath(ext.extensionUri, 'media', 'codicon.ttf'));
  return `@font-face {
    font-family: 'codicon';
    src: url('${fontUri.toString()}') format('truetype');
    font-display: block;
  }`;
}

function renderShell(webview: vscode.Webview, nonce: string, body: string, script: string): string {
  const csp = [
    `default-src 'none'`,
    `style-src ${webview.cspSource} 'unsafe-inline'`,
    `font-src ${webview.cspSource}`,
    `script-src 'nonce-${nonce}'`,
  ].join('; ');

  return `<!DOCTYPE html>
<html lang="zh-CN">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="Content-Security-Policy" content="${csp}">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <style>
    ${codiconFontFace(webview)}
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
    .codicon { font-family: 'codicon'; font-size: 16px; line-height: 1; vertical-align: middle; }
    .ci-folder::before { font-family: 'codicon'; content: '\\ea83'; color: var(--vscode-symbolIcon-folderForeground, #c5c5c5); margin-right: 4px; }
    .ci-file::before { font-family: 'codicon'; content: '\\ea7b'; color: var(--vscode-symbolIcon-fileForeground, #c5c5c5); margin-right: 4px; }
    .ci-chevron-down::before { font-family: 'codicon'; content: '\\eab4'; }
    .ci-chevron-right::before { font-family: 'codicon'; content: '\\eab6'; }
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
    .pane-title .count { font-size: 12px; }
    .pane-actions { display: flex; gap: 6px; }
    .pane-actions button, .bulk button {
      border: 1px solid var(--border);
      background: var(--vscode-button-secondaryBackground);
      color: var(--vscode-button-secondaryForeground);
      min-height: 24px; padding: 2px 8px; cursor: pointer; font: inherit; font-size: 12px; border-radius: 2px;
    }
    .pane-actions button:hover, .bulk button:hover { background: var(--row); }
    .conflict-leaf .conflict-actions { display: inline-flex; gap: 4px; margin-left: 8px; }
    .conflict-leaf .conflict-actions button {
      border: 1px solid var(--border); background: var(--vscode-button-secondaryBackground);
      color: var(--vscode-button-secondaryForeground); min-height: 22px; padding: 1px 7px;
      cursor: pointer; font: inherit; font-size: 11px; border-radius: 2px;
    }
    .conflict-leaf[data-choice="source"] .choice-source,
    .conflict-leaf[data-choice="target"] .choice-target { background: var(--button, var(--vscode-button-background)); color: var(--button-fg, var(--vscode-button-foreground)); border-color: var(--button, var(--vscode-button-background)); }
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
        if (ch.sourceChangesetId >= existing.sourceChangesetId) {
          existing.sourceServerPath = ch.sourceServerPath;
          existing.sourceChangeType = ch.sourceChangeType;
          existing.targetChangeType = ch.targetChangeType;
          existing.status = ch.status;
          existing.note = ch.note;
          existing.sourceChangesetId = ch.sourceChangesetId;
          existing.targetExists = ch.targetExists;
        }
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

interface AggregatedConflict {
  targetServerPath: string;
  sourceServerPath: string;
  changesets: number[];
}

/** 聚合所有勾选 changeset 的冲突文件，按目标路径去重（一个文件只出现一次）。 */
function aggregateConflicts(
  candidates: MergeWorkbenchCandidate[],
  rangeConflicts: readonly MergePlanChange[] = [],
): AggregatedConflict[] {
  const byPath = new Map<string, AggregatedConflict>();
  for (const cand of candidates) {
    if (!cand.checked) continue;
    for (const ch of cand.changes) {
      if (ch.status.toLowerCase() !== 'conflict') continue;
      const existing = byPath.get(ch.targetServerPath);
      if (existing) {
        if (!existing.changesets.includes(cand.changesetId)) existing.changesets.push(cand.changesetId);
      } else {
        byPath.set(ch.targetServerPath, {
          targetServerPath: ch.targetServerPath,
          sourceServerPath: ch.sourceServerPath,
          changesets: [cand.changesetId],
        });
      }
    }
  }
  // Include range conflicts that aren't already tracked by candidate changes
  for (const rc of rangeConflicts) {
    if (!byPath.has(rc.targetServerPath)) {
      byPath.set(rc.targetServerPath, {
        targetServerPath: rc.targetServerPath,
        sourceServerPath: rc.sourceServerPath,
        changesets: [],
      });
    }
  }
  return [...byPath.values()];
}

type ConflictResolution = 'source' | 'target' | 'manual';

function renderConflictTree(
  candidates: MergeWorkbenchCandidate[],
  targetPath: string,
  conflictResolutions: ReadonlyMap<string, ConflictResolution>,
  manualMergeContents: ReadonlyMap<string, string>,
): string {
  const conflicts = aggregateConflicts(candidates);
  if (conflicts.length === 0) return '<div class="empty">当前勾选项没有冲突。</div>';
  const root: FileTreeNode = { name: '', children: new Map(), files: [] };
  for (const c of conflicts) {
    const segments = relativeFilePath(c.targetServerPath, targetPath).split('/').filter(Boolean);
    let node = root;
    for (let i = 0; i < segments.length - 1; i++) {
      const seg = segments[i];
      let child = node.children.get(seg);
      if (!child) { child = { name: seg, children: new Map(), files: [] }; node.children.set(seg, child); }
      node = child;
    }
    node.files.push({
      targetServerPath: c.targetServerPath,
      sourceServerPath: c.sourceServerPath,
      sourceChangeType: 'Conflict',
      targetChangeType: 'Conflict',
      status: 'conflict',
      sourceChangesetId: c.changesets[0],
      targetExists: true,
      changesets: c.changesets,
    });
  }
  const renderLeaf = (file: AggregatedFile): string => {
    const name = path.posix.basename(file.targetServerPath);
    const csList = file.changesets.map((cs) => `cs${cs}`).join(', ');
    const choice = conflictResolutions.get(file.targetServerPath);
    const hasManual = manualMergeContents.has(file.targetServerPath);
    return `
      <div class="tree-file conflict-leaf is-conflict" data-conflict="${escapeAttribute(file.targetServerPath)}" data-choice="${choice ?? ''}">
        <i class="ci-file"></i><span class="tree-file-name" title="${escapeAttribute(file.targetServerPath)}">${escapeHtml(name)}</span>
        <span class="tree-file-cs" title="涉及的变更集合">${escapeHtml(csList)}</span>
        ${hasManual ? '<span class="badge">已有手动结果</span>' : ''}
        <span class="conflict-actions">
          <button class="choice-source" data-conflict-choice="source" data-conflict="${escapeAttribute(file.targetServerPath)}">采用源</button>
          <button class="choice-target" data-conflict-choice="target" data-conflict="${escapeAttribute(file.targetServerPath)}">采用目标</button>
          <button class="choice-manual" data-conflict-choice="manual" data-conflict="${escapeAttribute(file.targetServerPath)}">手动合并</button>
        </span>
      </div>`;
  };
  return `<div class="tree">${renderTreeNodeWithLeaf(root, renderLeaf)}</div>`;
}

function renderTreeNodeWithLeaf(node: FileTreeNode, renderLeaf: (f: AggregatedFile) => string): string {
  const folders = [...node.children.values()].sort((a, b) => a.name.localeCompare(b.name));
  const files = node.files.sort((a, b) =>
    path.posix.basename(a.targetServerPath).localeCompare(path.posix.basename(b.targetServerPath)),
  );
  const items: string[] = [];
  for (const folder of folders) {
    items.push(`
      <details class="tree-folder" open>
        <summary><i class="ci-folder"></i>${escapeHtml(folder.name)} <span class="badge">${countFiles(folder)}</span></summary>
        <div class="tree-children">${renderTreeNodeWithLeaf(folder, renderLeaf)}</div>
      </details>`);
  }
  for (const file of files) items.push(renderLeaf(file));
  return items.join('');
}

function renderTreeNode(node: FileTreeNode): string {
  const folders = [...node.children.values()].sort((a, b) => a.name.localeCompare(b.name));
  const files = node.files.sort((a, b) =>
    path.posix.basename(a.targetServerPath).localeCompare(path.posix.basename(b.targetServerPath)),
  );
  const items: string[] = [];
  for (const folder of folders) {
    items.push(`
      <details class="tree-folder" open>
        <summary><i class="ci-folder"></i>${escapeHtml(folder.name)} <span class="badge">${countFiles(folder)}</span></summary>
        <div class="tree-children">${renderTreeNode(folder)}</div>
      </details>`);
  }
  for (const file of files) {
    const name = path.posix.basename(file.targetServerPath);
    const csList = file.changesets.map((cs) => `cs${cs}`).join(', ');
    const isConflict = file.status.toLowerCase() === 'conflict';
    items.push(`
      <div class="tree-file ${isConflict ? 'is-conflict' : ''}">
        <i class="ci-file"></i><span class="tree-file-name" title="${escapeAttribute(file.targetServerPath)}">${escapeHtml(name)}</span>
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

let mergeToolbarDisposables: vscode.Disposable[] = [];

async function openNativeMergeWithToolbar(
  sourceContent: string,
  targetContent: string,
  initialResult: string | undefined,
  sourceServerPath: string,
  targetServerPath: string,
): Promise<string | undefined> {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'arm-tfs-conflict-'));
  const baseName = sanitizeLocalFileName(path.posix.basename(targetServerPath || sourceServerPath) || 'merge.txt');
  const basePath = path.join(directory, `base-${baseName}`);
  const sourcePath = path.join(directory, `source-${baseName}`);
  const targetPath = path.join(directory, `target-${baseName}`);
  const resultPath = path.join(directory, `result-${baseName}`);

  await Promise.all([
    fs.writeFile(basePath, targetContent, 'utf8'),
    fs.writeFile(sourcePath, sourceContent, 'utf8'),
    fs.writeFile(targetPath, targetContent, 'utf8'),
    fs.writeFile(resultPath, initialResult ?? targetContent, 'utf8'),
  ]);

  const baseUri = vscode.Uri.file(basePath);
  const sourceUri = vscode.Uri.file(sourcePath);
  const targetUri = vscode.Uri.file(targetPath);
  const resultUri = vscode.Uri.file(resultPath);

  try {
    await vscode.commands.executeCommand('_open.mergeEditor', {
      base: baseUri,
      input1: {
        uri: sourceUri,
        title: '源分支',
        description: path.posix.basename(sourceServerPath),
        detail: sourceServerPath,
      },
      input2: {
        uri: targetUri,
        title: '目标分支',
        description: path.posix.basename(targetServerPath),
        detail: targetServerPath,
      },
      output: resultUri,
    });
  } catch (error) {
    throw new Error(`无法打开 VS Code Merge Editor：${getErrorMessage(error)}`);
  }

  const doc = await vscode.workspace.openTextDocument(resultUri);

  return new Promise<string | undefined>((resolve) => {
    const disposables: vscode.Disposable[] = [];

    const cleanup = () => {
      for (const d of disposables) d.dispose();
      disposables.length = 0;
      for (const d of mergeToolbarDisposables) d.dispose();
      mergeToolbarDisposables = [];
    };

    // Register commands FIRST
    disposables.push(
      vscode.commands.registerCommand('armTfs.mergeConflict.complete', async () => {
        await doc.save();
        const content = await fs.readFile(resultPath, 'utf8');
        cleanup();
        resolve(content);
      }),
      vscode.commands.registerCommand('armTfs.mergeConflict.undo', () => {
        cleanup();
        resolve(undefined);
      }),
    );

    // Then create status bar items that reference the commands
    const prevBtn = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 1000);
    prevBtn.text = '$(arrow-up) 上一个冲突';
    prevBtn.command = 'merge-conflict.previous';
    prevBtn.tooltip = '跳转到上一个冲突';
    prevBtn.show();

    const nextBtn = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 999);
    nextBtn.text = '$(arrow-down) 下一个冲突';
    nextBtn.command = 'merge-conflict.next';
    nextBtn.tooltip = '跳转到下一个冲突';
    nextBtn.show();

    const countItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 998);
    countItem.text = '$(git-merge) 合并冲突解决中...';
    countItem.tooltip = '正在解决合并冲突';
    countItem.show();

    const completeBtn = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 1000);
    completeBtn.text = '$(check) 完成合并';
    completeBtn.command = 'armTfs.mergeConflict.complete';
    completeBtn.tooltip = '使用当前合并结果';
    completeBtn.backgroundColor = new vscode.ThemeColor('statusBarItem.warningBackground');
    completeBtn.show();

    const undoBtn = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 999);
    undoBtn.text = '$(discard) 撤销合并';
    undoBtn.command = 'armTfs.mergeConflict.undo';
    undoBtn.tooltip = '放弃当前合并，恢复原始内容';
    undoBtn.show();

    mergeToolbarDisposables.push(prevBtn, nextBtn, countItem, completeBtn, undoBtn);

    // Handle editor close
    disposables.push(
      vscode.workspace.onDidCloseTextDocument((closed) => {
        if (closed.uri.fsPath === resultUri.fsPath) {
          cleanup();
          resolve(undefined);
        }
      }),
    );
  });
}

async function openNativeConflictEditor(
  title: string,
  sourceContent: string,
  targetContent: string,
  initialResult: string | undefined,
  sourceServerPath: string,
  targetServerPath: string,
): Promise<string | undefined> {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'arm-tfs-conflict-'));
  const baseName = sanitizeLocalFileName(path.posix.basename(targetServerPath || sourceServerPath) || 'merge.txt');
  const sourcePath = path.join(directory, `source-${baseName}`);
  const targetPath = path.join(directory, `target-${baseName}`);
  const resultPath = path.join(directory, `result-${baseName}`);

  await Promise.all([
    fs.writeFile(sourcePath, sourceContent, 'utf8'),
    fs.writeFile(targetPath, targetContent, 'utf8'),
    fs.writeFile(
      resultPath,
      initialResult ?? buildConflictMarkerContent(sourceContent, targetContent, sourceServerPath, targetServerPath),
      'utf8',
    ),
  ]);

  const sourceUri = vscode.Uri.file(sourcePath);
  const targetUri = vscode.Uri.file(targetPath);
  const resultUri = vscode.Uri.file(resultPath);

  await vscode.commands.executeCommand(
    'vscode.diff',
    sourceUri,
    targetUri,
    `${title}: 源分支 -> 目标分支`,
    { preview: false, viewColumn: vscode.ViewColumn.Beside },
  );

  const doc = await vscode.workspace.openTextDocument(resultUri);
  await vscode.window.showTextDocument(doc, {
    preview: false,
    viewColumn: vscode.ViewColumn.Active,
  });

  for (;;) {
    const action = await vscode.window.showInformationMessage(
      '已打开 VS Code 原生冲突文件。使用编辑器中的 Accept Current / Incoming / Both 或直接编辑，保存后点击使用当前内容。',
      '使用当前内容',
      '取消',
    );
    if (action !== '使用当前内容') {
      return undefined;
    }

    await doc.save();
    const content = await fs.readFile(resultPath, 'utf8');
    if (!hasConflictMarkers(content)) {
      return content;
    }

    const markerAction = await vscode.window.showWarningMessage(
      '合并结果仍包含冲突标记。继续编辑可使用 VS Code 冲突操作清理标记。',
      '继续编辑',
      '仍然使用',
      '取消',
    );
    if (markerAction === '仍然使用') {
      return content;
    }
    if (markerAction !== '继续编辑') {
      return undefined;
    }

    await vscode.window.showTextDocument(doc, {
      preview: false,
      viewColumn: vscode.ViewColumn.Active,
    });
  }
}

function buildConflictMarkerContent(
  sourceContent: string,
  targetContent: string,
  sourceServerPath: string,
  targetServerPath: string,
): string {
  const eol = sourceContent.includes('\r\n') || targetContent.includes('\r\n') ? '\r\n' : '\n';
  return [
    `<<<<<<< 源分支 ${sourceServerPath}`,
    trimTrailingLineBreak(sourceContent),
    '=======',
    trimTrailingLineBreak(targetContent),
    `>>>>>>> 目标分支 ${targetServerPath}`,
    '',
  ].join(eol);
}

function trimTrailingLineBreak(value: string): string {
  return value.replace(/(?:\r\n|\r|\n)+$/u, '');
}

function hasConflictMarkers(value: string): boolean {
  return /(^|\n)<<<<<<< .+(\r?\n)/u.test(value)
    || /(^|\n)=======(\r?\n)/u.test(value)
    || /(^|\n)>>>>>>> .+(\r?\n?)/u.test(value);
}

function sanitizeLocalFileName(value: string): string {
  const sanitized = value.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_').trim();
  return sanitized || 'merge.txt';
}

function openManualMergePanel(
  title: string,
  sourceContent: string,
  targetContent: string,
  initialResult: string,
  sourceBranchPath: string,
  targetBranchPath: string,
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
  panel.webview.html = renderManualMergeHtml(panel.webview, nonce, sourceContent, targetContent, initialResult, sourceBranchPath, targetBranchPath);

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
  sourceBranchPath: string,
  targetBranchPath: string,
): string {
  const csp = [
    `default-src 'none'`,
    `style-src ${webview.cspSource} 'unsafe-inline'`,
    `script-src 'nonce-${nonce}'`,
  ].join('; ');

  // Embed content safely into <script>: JSON-encode then break any </script> sequence.
  const sourceJson = JSON.stringify(sourceContent).replace(/</g, '\\u003c');
  const targetJson = JSON.stringify(targetContent).replace(/</g, '\\u003c');
  const initialJson = JSON.stringify(initialResult).replace(/</g, '\\u003c');
  const sourceBranch = escapeHtml(sourceBranchPath);
  const targetBranch = escapeHtml(targetBranchPath);

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
      --add-bg: var(--vscode-diffEditor-insertedTextBackground, rgba(0,180,0,0.15));
      --del-bg: var(--vscode-diffEditor-removedTextBackground, rgba(255,0,0,0.15));
      --add-line: var(--vscode-diffEditorGutter-insertedLineBackground, rgba(0,180,0,0.25));
      --del-line: var(--vscode-diffEditorGutter-removedLineBackground, rgba(255,0,0,0.25));
      --conflict: var(--vscode-editorWarning-foreground, #cca700);
    }
    * { box-sizing: border-box; }
    body { margin: 0; background: var(--bg); color: var(--fg); font-family: var(--vscode-font-family); font-size: var(--vscode-font-size); }
    header { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 10px 14px; border-bottom: 1px solid var(--border); }
    h1 { margin: 0; font-size: 16px; }
    .header-actions { display: flex; gap: 8px; align-items: center; }
    button { border: 1px solid var(--border); background: var(--vscode-button-secondaryBackground); color: var(--vscode-button-secondaryForeground); min-height: 28px; padding: 4px 10px; cursor: pointer; font: inherit; border-radius: 2px; }
    button.primary { background: var(--button); color: var(--button-fg); border-color: var(--button); }
    button.active { background: var(--button); color: var(--button-fg); border-color: var(--button); }
    .grid { display: grid; grid-template-rows: minmax(200px, 1fr) auto minmax(180px, 0.8fr); height: calc(100vh - 104px); }
    .top { display: grid; grid-template-columns: 1fr 1fr; min-height: 0; border-bottom: 1px solid var(--border); overflow: hidden; }
    section { min-width: 0; min-height: 0; display: flex; flex-direction: column; border-right: 1px solid var(--border); }
    section:last-child { border-right: 0; }
    .title { padding: 6px 10px; font-weight: 650; color: var(--muted); border-bottom: 1px solid var(--border); }
    pre, textarea { margin: 0; flex: 1; min-height: 0; padding: 8px; overflow: auto; white-space: pre; border: 0; outline: 0; color: var(--fg); background: var(--bg); font-family: var(--vscode-editor-font-family); font-size: var(--vscode-editor-font-size); resize: none; }
    .line { white-space: pre; }
    .line.del { background: var(--del-bg); border-left: 3px solid var(--del-line); margin-left: -3px; padding-left: 8px; }
    .line.add { background: var(--add-bg); border-left: 3px solid var(--add-line); margin-left: -3px; padding-left: 8px; }
    #hunks { overflow: auto; border-bottom: 1px solid var(--border); max-height: 38vh; }
    .hunk { border-bottom: 1px dashed var(--border); padding: 8px 10px; }
    .hunk-head { display: flex; justify-content: space-between; align-items: center; gap: 8px; margin-bottom: 6px; }
    .hunk-title { color: var(--conflict); font-weight: 650; font-size: 12px; }
    .hunk-actions { display: flex; gap: 6px; }
    .hunk-body { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
    .hunk-side { font-family: var(--vscode-editor-font-family); font-size: 12px; white-space: pre; padding: 4px 6px; border-radius: 3px; }
    .hunk-side.src { background: var(--del-bg); }
    .hunk-side.tgt { background: var(--add-bg); }
    .hunk-side .lbl { display:block; font-size: 10px; color: var(--muted); margin-bottom: 2px; }
    .hunk-ctx { font-family: var(--vscode-editor-font-family); font-size: 12px; white-space: pre; padding: 4px 8px; color: var(--muted); background: var(--bg); border-radius: 2px; margin: 4px 0; border-left: 2px solid var(--border); }
    .hunk.resolved .hunk-title { color: var(--muted); }
    footer { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 10px 14px; border-top: 1px solid var(--border); flex-wrap: wrap; }
    .footer-status { display: flex; align-items: center; gap: 16px; }
    .footer-nav { display: flex; gap: 8px; }
    .footer-actions { display: flex; gap: 8px; }
    #remainingCount { color: var(--conflict); }
    @media (max-width: 900px) { .top { grid-template-columns: 1fr; } .hunk-body { grid-template-columns: 1fr; } footer { flex-direction: column; align-items: stretch; } }
  </style>
</head>
<body>
  <header>
    <h1>手动合并（左：源分支 · 右：目标分支）</h1>
    <div class="header-actions">
      <button id="prevConflict" title="跳转到上一个冲突">⬆️ 上一个冲突</button>
      <button id="nextConflict" title="跳转到下一个冲突">下一个冲突 ⬇️</button>
      <button id="acceptAllSource" title="所有冲突块采用源分支">全部采用源</button>
      <button id="acceptAllTarget" title="所有冲突块采用目标分支">全部采用目标</button>
      <button id="cancel">取消</button>
      <button id="save" class="primary">使用合并结果</button>
    </div>
  </header>
  <main class="grid">
    <div class="top">
      <section>
        <div class="title">源分支文件 · ${sourceBranch}</div>
        <pre id="sourcePane"></pre>
      </section>
      <section>
        <div class="title">目标分支文件 · ${targetBranch}</div>
        <pre id="targetPane"></pre>
      </section>
    </div>
    <section id="hunksSection">
      <div class="title">冲突区块（逐块选择采用哪一侧；下方结果会自动更新）</div>
      <div id="hunks"></div>
    </section>
    <section>
      <div class="title">合并结果（可手动编辑）</div>
      <textarea id="result"></textarea>
    </section>
  </main>
  <footer>
    <div class="footer-status">
      <span>剩余 <strong id="remainingCount">0</strong> 个冲突</span>
      <div class="footer-nav">
        <button id="prevConflictFooter" title="跳转到上一个冲突">⬆️ 上一个冲突</button>
        <button id="nextConflictFooter" title="跳转到下一个冲突">下一个冲突 ⬇️</button>
      </div>
    </div>
    <div class="footer-actions">
      <button id="undoMerge" title="重置所有冲突选择">撤销合并</button>
      <button id="completeMerge" class="primary" title="完成合并并使用结果">完成合并</button>
    </div>
  </footer>
  <script nonce="${nonce}">
    const vscode = acquireVsCodeApi();
    const sourceText = ${sourceJson};
    const targetText = ${targetJson};
    const initialResult = ${initialJson};
    const sourceLines = sourceText.split('\\n');
    const targetLines = targetText.split('\\n');

    // LCS diff → ops
    const n = sourceLines.length, m = targetLines.length;
    const dp = Array.from({length: n + 1}, () => new Array(m + 1).fill(0));
    for (let i = n - 1; i >= 0; i--) {
      for (let j = m - 1; j >= 0; j--) {
        dp[i][j] = sourceLines[i] === targetLines[j] ? dp[i+1][j+1] + 1 : Math.max(dp[i+1][j], dp[i][j+1]);
      }
    }
    const ops = [];
    let i = 0, j = 0;
    while (i < n && j < m) {
      if (sourceLines[i] === targetLines[j]) { ops.push({t:'eq', s:sourceLines[i]}); i++; j++; }
      else if (dp[i+1][j] >= dp[i][j+1]) { ops.push({t:'del', s:sourceLines[i]}); i++; }
      else { ops.push({t:'ins', s:targetLines[j]}); j++; }
    }
    while (i < n) { ops.push({t:'del', s:sourceLines[i]}); i++; }
    while (j < m) { ops.push({t:'ins', s:targetLines[j]}); j++; }

    // group into hunks: runs of del/ins, with position tracking for context
    const hunks = [];
    let k = 0;
    while (k < ops.length) {
      if (ops[k].t === 'eq') { k++; continue; }
      const startIdx = k;
      const h = { src: [], tgt: [], choice: null, opsStart: startIdx, opsEnd: 0 };
      while (k < ops.length && ops[k].t !== 'eq') {
        if (ops[k].t === 'del') h.src.push(ops[k].s);
        else h.tgt.push(ops[k].s);
        k++;
      }
      h.opsEnd = k;
      hunks.push(h);
    }

    function esc(s) { return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }

    // render side-by-side panes with highlighting
    const srcHtml = [], tgtHtml = [];
    for (const op of ops) {
      if (op.t === 'eq') { srcHtml.push('<span class="line">'+esc(op.s)+'</span>'); tgtHtml.push('<span class="line">'+esc(op.s)+'</span>'); }
      else if (op.t === 'del') { srcHtml.push('<span class="line del">'+esc(op.s)+'</span>'); }
      else { tgtHtml.push('<span class="line add">'+esc(op.s)+'</span>'); }
    }
    document.getElementById('sourcePane').innerHTML = srcHtml.join('\\n');
    document.getElementById('targetPane').innerHTML = tgtHtml.join('\\n');

    // build result from ops + hunk choices (unresolved hunks become conflict markers)
    function buildResult() {
      const result = [];
      let p = 0;
      let h = 0;
      while (p < ops.length) {
        if (ops[p].t === 'eq') { result.push(ops[p].s); p++; continue; }
        const cur = hunks[h]; h++;
        const c = cur.choice;
        if (c === 'source') result.push(...cur.src);
        else if (c === 'target') result.push(...cur.tgt);
        else if (c === 'both') { result.push(...cur.src); result.push(...cur.tgt); }
        else {
          result.push('<<<<<<< 源分支');
          result.push(...cur.src);
          result.push('=======');
          result.push(...cur.tgt);
          result.push('>>>>>>> 目标分支');
        }
        while (p < ops.length && ops[p].t !== 'eq') p++;
      }
      return result.join('\\n');
    }

    function renderHunks() {
      const box = document.getElementById('hunks');
      if (hunks.length === 0) { box.innerHTML = '<div style="padding:12px;color:var(--muted)">两侧内容相同，没有冲突区块。</div>'; return; }
      const CONTEXT = 3;
      box.innerHTML = hunks.map((h, idx) => {
        const choice = h.choice;
        // Gather context lines before and after the hunk
        const ctxBefore = [];
        for (let c = Math.max(0, h.opsStart - CONTEXT); c < h.opsStart; c++) {
          if (ops[c].t === 'eq') ctxBefore.push(ops[c].s);
        }
        const ctxAfter = [];
        for (let c = h.opsEnd; c < Math.min(ops.length, h.opsEnd + CONTEXT); c++) {
          if (ops[c].t === 'eq') ctxAfter.push(ops[c].s);
        }
        const ctxBeforeHtml = ctxBefore.length ? '<div class="hunk-ctx">' + ctxBefore.map(l => esc(l)).join('\\n') + '</div>' : '';
        const ctxAfterHtml = ctxAfter.length ? '<div class="hunk-ctx">' + ctxAfter.map(l => esc(l)).join('\\n') + '</div>' : '';
        const srcPre = h.src.length ? esc(h.src.join('\\n')) : '(无)';
        const tgtPre = h.tgt.length ? esc(h.tgt.join('\\n')) : '(无)';
        return '<div class="hunk ' + (choice ? 'resolved' : '') + '" data-idx="'+idx+'">'
          + '<div class="hunk-head"><span class="hunk-title">冲突 #' + (idx+1) + ' / ' + hunks.length + (choice ? ' · 已采用 '+({source:'源',target:'目标',both:'两者'}[choice]) : '') + '</span>'
          + '<span class="hunk-actions">'
          + '<button data-hunk="'+idx+'" data-choice="source" class="'+(choice==='source'?'active':'')+'">采用源</button>'
          + '<button data-hunk="'+idx+'" data-choice="target" class="'+(choice==='target'?'active':'')+'">采用目标</button>'
          + '<button data-hunk="'+idx+'" data-choice="both" class="'+(choice==='both'?'active':'')+'">两者都采用</button>'
          + '</span></div>'
          + ctxBeforeHtml
          + '<div class="hunk-body"><div class="hunk-side src"><span class="lbl">源分支</span>'+srcPre+'</div><div class="hunk-side tgt"><span class="lbl">目标分支</span>'+tgtPre+'</div></div>'
          + ctxAfterHtml
          + '</div>';
      }).join('');
      box.querySelectorAll('button[data-hunk]').forEach((b) => {
        b.addEventListener('click', () => {
          const idx = Number(b.dataset.hunk);
          hunks[idx].choice = b.dataset.choice;
          renderHunks();
          document.getElementById('result').value = buildResult();
        });
      });
      updateConflictCount();
    }

    function updateConflictCount() {
      const remaining = hunks.filter(h => !h.choice).length;
      document.getElementById('remainingCount').textContent = remaining;
    }

    function scrollToConflict(direction) {
      const hunkElements = Array.from(document.querySelectorAll('.hunk'));
      if (hunkElements.length === 0) return;

      const currentVisible = hunkElements.find(el => {
        const rect = el.getBoundingClientRect();
        return rect.top >= 0 && rect.bottom <= window.innerHeight;
      });

      let targetIdx = 0;
      if (currentVisible) {
        const currentIdx = hunkElements.indexOf(currentVisible);
        targetIdx = direction === 'next'
          ? (currentIdx + 1) % hunkElements.length
          : (currentIdx - 1 + hunkElements.length) % hunkElements.length;
      }

      hunkElements[targetIdx].scrollIntoView({ behavior: 'smooth', block: 'center' });
      hunkElements[targetIdx].style.backgroundColor = 'rgba(255, 255, 0, 0.2)';
      setTimeout(() => { hunkElements[targetIdx].style.backgroundColor = ''; }, 1000);
    }

    // initialize: if re-opening with saved content, use it; else build with conflict markers
    if (initialResult && initialResult.length) {
      document.getElementById('result').value = initialResult;
    } else {
      document.getElementById('result').value = buildResult();
    }
    renderHunks();

    document.getElementById('acceptAllSource').addEventListener('click', () => {
      hunks.forEach((h) => { h.choice = 'source'; });
      renderHunks();
      document.getElementById('result').value = buildResult();
    });
    document.getElementById('acceptAllTarget').addEventListener('click', () => {
      hunks.forEach((h) => { h.choice = 'target'; });
      renderHunks();
      document.getElementById('result').value = buildResult();
    });
    document.getElementById('cancel').addEventListener('click', () => vscode.postMessage({ type: 'cancel' }));
    document.getElementById('save').addEventListener('click', () => {
      vscode.postMessage({ type: 'save', content: document.getElementById('result').value });
    });

    // 导航功能
    document.getElementById('prevConflict').addEventListener('click', () => scrollToConflict('prev'));
    document.getElementById('nextConflict').addEventListener('click', () => scrollToConflict('next'));
    document.getElementById('prevConflictFooter').addEventListener('click', () => scrollToConflict('prev'));
    document.getElementById('nextConflictFooter').addEventListener('click', () => scrollToConflict('next'));

    // 撤销合并
    document.getElementById('undoMerge').addEventListener('click', () => {
      if (confirm('确定要重置所有冲突选择吗？')) {
        hunks.forEach((h) => { h.choice = null; });
        renderHunks();
        document.getElementById('result').value = buildResult();
      }
    });

    // 完成合并
    document.getElementById('completeMerge').addEventListener('click', () => {
      const remaining = hunks.filter(h => !h.choice).length;
      if (remaining > 0) {
        alert('还有 ' + remaining + ' 个冲突未解决，请先解决所有冲突。');
        return;
      }
      vscode.postMessage({ type: 'save', content: document.getElementById('result').value });
    });

    // 滚动同步：左/右/结果三栏联动
    const srcPane = document.getElementById('sourcePane');
    const tgtPane = document.getElementById('targetPane');
    const resultArea = document.getElementById('result');
    let syncing = false;

    function syncScroll(source, targets) {
      if (syncing) return;
      syncing = true;
      const ratio = source.scrollTop / (source.scrollHeight - source.clientHeight || 1);
      for (const t of targets) {
        t.scrollTop = ratio * (t.scrollHeight - t.clientHeight || 1);
      }
      syncing = false;
    }

    srcPane.addEventListener('scroll', () => syncScroll(srcPane, [tgtPane, resultArea]));
    tgtPane.addEventListener('scroll', () => syncScroll(tgtPane, [srcPane, resultArea]));
    resultArea.addEventListener('scroll', () => syncScroll(resultArea, [srcPane, tgtPane]));
  </script>
</body>
</html>`;
}

function getMergePlanConcurrency(): number {
  const configured = Number(getConfigValue<number>('merge.planConcurrency', DEFAULT_PLAN_CONCURRENCY));
  if (!Number.isFinite(configured)) {
    return DEFAULT_PLAN_CONCURRENCY;
  }
  return Math.min(MAX_PLAN_CONCURRENCY, Math.max(1, Math.floor(configured)));
}

async function mapWithConcurrency<T, TResult>(
  items: readonly T[],
  concurrency: number,
  mapper: (item: T, index: number) => Promise<TResult>,
): Promise<TResult[]> {
  if (items.length === 0) {
    return [];
  }
  const workerCount = Math.min(items.length, Math.max(1, Math.floor(concurrency)));
  const results = new Array<TResult>(items.length);
  let nextIndex = 0;
  await Promise.all(Array.from({ length: workerCount }, async () => {
    for (;;) {
      const index = nextIndex;
      nextIndex += 1;
      if (index >= items.length) {
        return;
      }
      results[index] = await mapper(items[index], index);
    }
  }));
  return results;
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
