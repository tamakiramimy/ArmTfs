import * as vscode from 'vscode';
import { t, translateCliText } from './i18n';

/**
 * Report the textual output of an arm-tfs command without disrupting the editor.
 *
 * Previously command results were dumped into a freshly opened text document, which users
 * found intrusive (a new tab opened for almost every action). Instead we now:
 *  - append the full output to the shared arm-tfs OutputChannel (the "console"), and
 *  - surface a concise toast in the bottom-right corner with a "Show Output" action.
 */
export function reportCommandOutput(
  output: vscode.OutputChannel,
  title: string,
  result: string,
  options?: { summary?: string },
): void {
  const text = translateCliText(result ?? '').trim();

  output.appendLine(`> ${title}`);
  if (text) {
    output.appendLine(text);
  }
  output.appendLine('');

  const showAction = t('command.output.show');
  const message = options?.summary?.trim() || t('command.output.completed', { title });
  void vscode.window.showInformationMessage(message, showAction).then((choice) => {
    if (choice === showAction) {
      output.show(true);
    }
  });
}

/**
 * Report a structured (JSON-serializable) command result to the OutputChannel + a toast.
 */
export function reportCommandResult(
  output: vscode.OutputChannel,
  title: string,
  result: unknown,
  options?: { summary?: string },
): void {
  let serialized: string;
  try {
    serialized = JSON.stringify(result, null, 2);
  } catch {
    serialized = String(result);
  }
  reportCommandOutput(output, title, serialized, options);
}
