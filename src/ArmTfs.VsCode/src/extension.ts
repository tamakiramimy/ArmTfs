import * as vscode from 'vscode';
import { ArmTfsCliClient, ArmTfsCliError } from './armTfsCliClient';

export interface ArmTfsExtensionApi {
  client: ArmTfsCliClient;
}

export function activate(context: vscode.ExtensionContext): ArmTfsExtensionApi {
  const output = vscode.window.createOutputChannel('arm-tfs');
  const client = new ArmTfsCliClient(output);

  context.subscriptions.push(output);

  const register = (command: string, callback: () => Promise<void>) => {
    context.subscriptions.push(vscode.commands.registerCommand(command, callback));
  };

  register('armTfs.configureCliCommand', async () => {
    const picked = await vscode.window.showOpenDialog({
      canSelectFiles: true,
      canSelectFolders: false,
      canSelectMany: false,
      openLabel: 'Select arm-tfs executable or DLL',
    });
    if (!picked?.length) {
      return;
    }

    const selectedPath = picked[0].fsPath;
    const target = vscode.workspace.workspaceFolders?.length ? vscode.ConfigurationTarget.Workspace : vscode.ConfigurationTarget.Global;
    const config = vscode.workspace.getConfiguration('armTfs');

    if (selectedPath.toLowerCase().endsWith('.dll')) {
      await config.update('cli.command', 'dotnet', target);
      await config.update('cli.commandArgs', [selectedPath], target);
    } else {
      await config.update('cli.command', selectedPath, target);
      await config.update('cli.commandArgs', [], target);
    }

    const resolved = await client.describeResolvedInvocation();
    output.appendLine(`Configured CLI: ${resolved}`);
    vscode.window.showInformationMessage('arm-tfs CLI command updated.');
  });

  register('armTfs.showStatus', async () => {
    await runAndShow('arm-tfs status', output, async () => client.status(getActivePath() ?? '.', true));
  });

  register('armTfs.showHistory', async () => {
    const targetPath = await promptPath('History path', getActivePath() ?? '.');
    if (!targetPath) {
      return;
    }

    const top = await promptNumber('History depth', '20');
    if (top === undefined) {
      return;
    }

    await runAndShow('arm-tfs history', output, async () => client.history(targetPath, top));
  });

  register('armTfs.showDiff', async () => {
    const targetPath = await promptPath('Diff path', getActivePath());
    if (!targetPath) {
      return;
    }

    const compareMode = await vscode.window.showQuickPick(
      [
        { label: 'Latest server version', value: 'latest' },
        { label: 'Tracked base version', value: 'base' },
      ],
      { placeHolder: 'Choose a diff base' },
    );
    if (!compareMode) {
      return;
    }

    await runAndShow('arm-tfs diff', output, async () => client.diff(targetPath, { useBase: compareMode.value === 'base' }));
  });

  register('armTfs.showBranch', async () => {
    const branchPath = await promptPath('Branch path', '$/');
    if (!branchPath) {
      return;
    }

    await runAndShow('arm-tfs branch', output, async () => client.branchShow(branchPath));
  });

  register('armTfs.showChangeset', async () => {
    const changesetId = await promptNumber('Changeset ID', '1');
    if (changesetId === undefined) {
      return;
    }

    await runAndShow('arm-tfs changeset', output, async () => client.changesetShow(changesetId));
  });

  register('armTfs.showLabel', async () => {
    const labelId = await promptPath('Label ID', '');
    if (!labelId) {
      return;
    }

    await runAndShow('arm-tfs label', output, async () => client.labelShow(labelId));
  });

  register('armTfs.showMergeBase', async () => {
    const sourcePath = await promptPath('Merge source path', '$/');
    if (!sourcePath) {
      return;
    }

    const targetPath = await promptPath('Merge target path', '$/');
    if (!targetPath) {
      return;
    }

    await runAndShow('arm-tfs merge base', output, async () => client.mergeBase(sourcePath, targetPath));
  });

  register('armTfs.showMergeCandidates', async () => {
    const sourcePath = await promptPath('Merge source path', '$/');
    if (!sourcePath) {
      return;
    }

    const targetPath = await promptPath('Merge target path', '$/');
    if (!targetPath) {
      return;
    }

    const top = await promptNumber('Candidate result count', '20');
    if (top === undefined) {
      return;
    }

    await runAndShow('arm-tfs merge candidates', output, async () => client.mergeCandidates(sourcePath, targetPath, top, 80));
  });

  return { client };
}

export function deactivate(): void {}

async function runAndShow(title: string, output: vscode.OutputChannel, runner: () => Promise<unknown>): Promise<void> {
  try {
    const result = await runner();
    const document = await vscode.workspace.openTextDocument({
      language: 'json',
      content: JSON.stringify(result, null, 2),
    });
    await vscode.window.showTextDocument(document, { preview: false });
    vscode.window.setStatusBarMessage(`${title} completed`, 2500);
  } catch (error) {
    output.show(true);
    if (error instanceof ArmTfsCliError) {
      output.appendLine(error.message);
      if (error.stdout.trim()) {
        output.appendLine(error.stdout.trim());
      }
      if (error.stderr.trim()) {
        output.appendLine(error.stderr.trim());
      }
      void vscode.window.showErrorMessage(`${title} failed: ${error.message}`);
      return;
    }

    const message = error instanceof Error ? error.message : `${error}`;
    output.appendLine(message);
    void vscode.window.showErrorMessage(`${title} failed: ${message}`);
  }
}

async function promptPath(prompt: string, value?: string): Promise<string | undefined> {
  return vscode.window.showInputBox({
    prompt,
    value,
    ignoreFocusOut: true,
  });
}

async function promptNumber(prompt: string, value: string): Promise<number | undefined> {
  const raw = await vscode.window.showInputBox({
    prompt,
    value,
    ignoreFocusOut: true,
    validateInput(input) {
      return /^\d+$/.test(input) ? undefined : 'Enter a non-negative integer.';
    },
  });
  if (raw === undefined) {
    return undefined;
  }

  return Number.parseInt(raw, 10);
}

function getActivePath(): string | undefined {
  const activeUri = vscode.window.activeTextEditor?.document.uri;
  if (activeUri?.scheme === 'file') {
    return activeUri.fsPath;
  }

  return undefined;
}