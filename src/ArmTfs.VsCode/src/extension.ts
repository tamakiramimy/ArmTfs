import * as vscode from 'vscode';
import { ArmTfsCliClient, ArmTfsCliError } from './armTfsCliClient';
import { ArmTfsConnectionsController } from './connections';
import { ArmTfsHistoryBrowser } from './historyBrowser';
import { getUiLanguage, t, translateCliMessage, translateCliText, type UiLanguage } from './i18n';
import { checkoutServerPathToLocalFolder } from './serverPathCheckout';
import { ArmTfsScmController, ArmTfsResourceState } from './scm';
import { ArmTfsSidebarController, ArmTfsServerExplorerController } from './sidebar';
import { findTfvcWorkspaceRoot, getCommandCwd } from './tfvcContext';

export interface ArmTfsExtensionApi {
  client: ArmTfsCliClient;
}

export function activate(context: vscode.ExtensionContext): ArmTfsExtensionApi {
  const output = vscode.window.createOutputChannel('arm-tfs');
  const client = new ArmTfsCliClient(output);
  const historyBrowser = new ArmTfsHistoryBrowser(client, output);
  const scm = new ArmTfsScmController(client, output, getWorkspaceRoot());
  const sidebar = new ArmTfsSidebarController(client, output, getWorkspaceRoot(), async () => scm.refresh(), historyBrowser);
  let connections: ArmTfsConnectionsController;
  let uiInitialized = false;
  const serverExplorer = new ArmTfsServerExplorerController(
    client,
    output,
    async () => scm.refresh(),
    async (serverPath, options) => sidebar.setActiveServerPath(serverPath, options),
    async (serverPath) => connections.updateActiveRootPath(serverPath),
    historyBrowser,
  );
  connections = new ArmTfsConnectionsController(context, client, async (profile) => {
    if (!uiInitialized) {
      return;
    }
    if (profile) {
      serverExplorer.setRootPath(profile.rootPath);
      await sidebar.setActiveServerPath(profile.rootPath, {
        branchContext: true,
        syncBranchScope: true,
        syncMergeSource: false,
      });
    } else {
      serverExplorer.refresh();
      await sidebar.refreshAll();
    }
  });
  client.setConnectionEnvironmentProvider(() => connections.getActiveEnvironment());

  const refreshUi = async () => {
    await scm.refresh();
    await sidebar.refreshAll();
  };

  context.subscriptions.push(output, historyBrowser, scm, sidebar, serverExplorer, connections, vscode.window.registerFileDecorationProvider(scm));
  void (async () => {
    await connections.initialize();
    await scm.initialize();
    await sidebar.initialize();
    uiInitialized = true;
  })();

  context.subscriptions.push(
    vscode.workspace.onDidSaveTextDocument((document) => {
      if (document.uri.scheme === 'file') {
        void refreshUi();
      }
    }),
    vscode.workspace.onWillSaveTextDocument((event) => {
      event.waitUntil(sidebar.handleWillSave(event.document));
    }),
    vscode.workspace.onDidChangeTextDocument((event) => {
      if (event.document.uri.scheme === 'file') {
        void sidebar.handleTextChanged(event.document, event.contentChanges.length > 0);
      }
    }),
    vscode.window.onDidChangeActiveTextEditor(() => {
      void sidebar.handleActiveEditorChanged();
    }),
    vscode.workspace.onDidCreateFiles(() => {
      void refreshUi();
    }),
    vscode.workspace.onDidDeleteFiles(() => {
      void refreshUi();
    }),
    vscode.workspace.onDidRenameFiles(() => {
      void refreshUi();
    }),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration('armTfs.ui.language')) {
        scm.refreshLabels();
        void refreshUi();
      }
    }),
  );

  const register = (command: string, callback: (input?: CommandInput) => Promise<unknown>) => {
    context.subscriptions.push(
      vscode.commands.registerCommand(command, (...args: unknown[]) => callback(parseCommandInput(args[0]))),
    );
  };

  const registerResourceCommand = (
    command: string,
    callback: (resource?: ArmTfsResourceState | vscode.Uri) => Promise<unknown>,
  ) => {
    context.subscriptions.push(vscode.commands.registerCommand(command, (resource?: ArmTfsResourceState | vscode.Uri) => callback(resource)));
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
    await connections.refreshCliDescription();
    vscode.window.showInformationMessage(t('extension.cliUpdated'));
  });

  register('armTfs.switchLanguage', async () => {
    const configured = vscode.workspace.getConfiguration('armTfs').get<UiLanguage>('ui.language', 'auto');
    const selected = await vscode.window.showQuickPick(
      [
        { label: t('language.name.auto'), value: 'auto' as UiLanguage, description: configured === 'auto' ? t('language.current') : undefined },
        { label: t('language.name.zh-CN'), value: 'zh-CN' as UiLanguage, description: configured === 'zh-CN' ? t('language.current') : undefined },
        { label: t('language.name.en'), value: 'en' as UiLanguage, description: configured === 'en' ? t('language.current') : undefined },
      ],
      { placeHolder: t('language.switch.title') },
    );
    if (!selected) {
      return;
    }

    const target = vscode.workspace.workspaceFolders?.length ? vscode.ConfigurationTarget.Workspace : vscode.ConfigurationTarget.Global;
    await vscode.workspace.getConfiguration('armTfs').update('ui.language', selected.value, target);
    scm.refreshLabels();
    await refreshUi();
    void vscode.window.showInformationMessage(t('language.changed', { language: selected.label }));
  });

  register('armTfs.showConfig', async () => {
    return runAndShowText('arm-tfs configure --show', output, async () => client.configureShow());
  });

  register('armTfs.configurePat', async () => {
    return vscode.commands.executeCommand('armTfs.connections.add');
  });

  register('armTfs.createWorkspace', async (input) => {
    const workspaceRoot = getWorkspaceRoot() ?? '.';
    const name = readStringOption(input, 'name') ?? await promptPath('Workspace name', 'ArmTfsWorkspace');
    if (!name) {
      return;
    }

    const serverPath = readStringOption(input, 'serverPath') ?? await promptPath('Server path', '$/');
    if (!serverPath) {
      return;
    }

    const directory = readStringOption(input, 'directory') ?? await promptPath('Workspace root directory', workspaceRoot);
    if (!directory) {
      return;
    }

    const localPath = readStringOption(input, 'localPath') ?? await promptOptionalPath('Mapped local path (optional)', directory);

    const result = await runAndShowText('arm-tfs workspace new', output, async () => client.workspaceNew(name, serverPath, directory, localPath));
    await refreshUi();
    return result;
  });

  register('armTfs.checkoutServerPathToFolder', async (input) => {
    const serverPath = readServerPathOption(input) ?? await promptServerPath('TFVC server path', '$/');
    if (!serverPath) {
      return;
    }

    const localPath = readStringOption(input, 'localPath') ?? readStringOption(input, 'directory') ?? await promptLocalFolder('Checkout destination folder', getWorkspaceRoot());
    if (!localPath) {
      return;
    }

    const result = await runAndShowText('arm-tfs checkout server path', output, async () =>
      checkoutServerPathToLocalFolder(client, serverPath, localPath),
    );
    await refreshUi();
    return result;
  });

  register('armTfs.showWorkspace', async () => {
    const localContext = await resolveLocalWorkspaceContext();
    if (!localContext) {
      return undefined;
    }

    return runAndShowText('arm-tfs workspace show', output, async () => client.workspaceShow({ cwdOverride: localContext.workspaceRoot }));
  });

  register('armTfs.addWorkspaceMapping', async (input) => {
    const serverPath = readStringOption(input, 'serverPath') ?? await promptPath('Additional server path', '$/');
    if (!serverPath) {
      return;
    }

    const workspaceRoot = getWorkspaceRoot() ?? '.';
    const localPath = readStringOption(input, 'localPath') ?? await promptPath('Local path', workspaceRoot);
    if (!localPath) {
      return;
    }

    const result = await runAndShowText('arm-tfs workspace map', output, async () => client.workspaceMap(serverPath, localPath));
    await refreshUi();
    return result;
  });

  register('armTfs.runGet', async (input) => {
    const targetPath = readStringOption(input, 'path') ?? await promptPath('Get path', getActivePath() ?? '.');
    if (!targetPath) {
      return;
    }

    const mode = readStringOption(input, 'mode') ?? (await vscode.window.showQuickPick(
      [
        { label: 'Latest', description: 'Download latest server version', value: 'latest' },
        { label: 'Force latest', description: 'Overwrite even if tracked version matches', value: 'force' },
        { label: 'Dry run', description: 'Preview downloads without writing files', value: 'dryRun' },
      ],
      { placeHolder: 'Choose get mode' },
    ))?.value;
    if (!mode) {
      return;
    }

    const result = await runAndShowText('arm-tfs get', output, async () =>
      withLocalWorkspace(targetPath, (localContext) => client.get(targetPath, {
        version: readNumberOption(input, 'version'),
        force: mode === 'force',
        dryRun: mode === 'dryRun',
      }, { cwdOverride: localContext.commandCwd })),
    );
    await refreshUi();
    return result;
  });

  register('armTfs.checkout', async (input) => {
    const targetPath = readStringOption(input, 'path') ?? await promptPath('Checkout path', getActivePath() ?? '.');
    if (!targetPath) {
      return;
    }

    const recursive = readBooleanOption(input, 'recursive') ?? await promptBoolean('Include subfiles recursively?', false);
    if (recursive === undefined) {
      return;
    }

    const result = await runAndShowText('arm-tfs checkout', output, async () =>
      withLocalWorkspace(targetPath, (localContext) => client.checkout([targetPath], recursive, { cwdOverride: localContext.commandCwd })),
    );
    await refreshUi();
    return result;
  });

  register('armTfs.add', async (input) => {
    const targetPath = readStringOption(input, 'path') ?? await promptPath('Add path', getActivePath() ?? '.');
    if (!targetPath) {
      return;
    }

    const recursive = readBooleanOption(input, 'recursive') ?? await promptBoolean('Include subfiles recursively?', false);
    if (recursive === undefined) {
      return;
    }

    const result = await runAndShowText('arm-tfs add', output, async () =>
      withLocalWorkspace(targetPath, (localContext) => client.add([targetPath], recursive, { cwdOverride: localContext.commandCwd })),
    );
    await refreshUi();
    return result;
  });

  register('armTfs.undo', async (input) => {
    const targetPath = readStringOption(input, 'path') ?? await promptPath('Undo path', getActivePath() ?? '.');
    if (!targetPath) {
      return;
    }

    const noRestore = readBooleanOption(input, 'noRestore') ?? await promptBoolean('Only remove pending state without restoring file content?', false);
    if (noRestore === undefined) {
      return;
    }

    const result = await runAndShowText('arm-tfs undo', output, async () =>
      withLocalWorkspace(targetPath, (localContext) => client.undo([targetPath], noRestore, { cwdOverride: localContext.commandCwd })),
    );
    await refreshUi();
    return result;
  });

  register('armTfs.checkin', async (input) => {
    const comment = readStringOption(input, 'comment') ?? await promptPath('Checkin comment', '');
    if (!comment) {
      return;
    }

    const targetPath = readStringOption(input, 'path') ?? await promptPath('Checkin path', '.');
    if (!targetPath) {
      return;
    }

    const mode = readStringOption(input, 'mode') ?? (await vscode.window.showQuickPick(
      [
        { label: 'Submit and clear pending', value: 'submit' },
        { label: 'Dry run', value: 'dryRun' },
        { label: 'Submit but keep pending', value: 'keepPending' },
      ],
      { placeHolder: 'Choose checkin mode' },
    ))?.value;
    if (!mode) {
      return;
    }

    const result = await runAndShowText('arm-tfs checkin', output, async () =>
      withLocalWorkspace(targetPath, (localContext) =>
        client.checkin(comment, [targetPath], mode === 'keepPending', mode === 'dryRun', { cwdOverride: localContext.commandCwd })),
    );
    await refreshUi();
    return result;
  });

  register('armTfs.showStatus', async (input) => {
    const targetPath = readStringOption(input, 'path') ?? getActivePath() ?? '.';
    const localContext = await resolveLocalWorkspaceContext(targetPath);
    if (!localContext) {
      return undefined;
    }

    return runAndShow('arm-tfs status', output, async () =>
      client.status(targetPath, readBooleanOption(input, 'all') ?? true, { cwdOverride: localContext.commandCwd }),
    );
  });

  register('armTfs.showHistory', async (input) => {
    const targetPath = readStringOption(input, 'path') ?? await promptPath('History path', getActivePath() ?? '.');
    if (!targetPath) {
      return;
    }

    const top = readNumberOption(input, 'top') ?? await promptNumber('History depth', '20');
    if (top === undefined) {
      return;
    }

    return runAndShow('arm-tfs history', output, async () => {
      if (isServerPath(targetPath)) {
        return client.history(targetPath, top);
      }

      return withLocalWorkspace(targetPath, (localContext) => client.history(targetPath, top, undefined, { cwdOverride: localContext.commandCwd }));
    });
  });

  register('armTfs.showDiff', async (input) => {
    const targetPath = readStringOption(input, 'path') ?? await promptPath('Diff path', getActivePath());
    if (!targetPath) {
      return;
    }

    const compareMode = readStringOption(input, 'compareMode') ?? (await vscode.window.showQuickPick(
      [
        { label: 'Latest server version', value: 'latest' },
        { label: 'Tracked base version', value: 'base' },
      ],
      { placeHolder: 'Choose a diff base' },
    ))?.value;
    if (!compareMode) {
      return;
    }

    return runAndShow('arm-tfs diff', output, async () =>
      withLocalWorkspace(targetPath, (localContext) => client.diff(targetPath, { useBase: compareMode === 'base' }, { cwdOverride: localContext.commandCwd })),
    );
  });

  register('armTfs.showBranch', async (input) => {
    const branchPath = readStringOption(input, 'path') ?? await promptPath('Branch path', '$/');
    if (!branchPath) {
      return;
    }

    return runAndShow('arm-tfs branch', output, async () => client.branchShow(branchPath));
  });

  register('armTfs.showChangeset', async (input) => {
    const changesetId = readNumberOption(input, 'changesetId') ?? await promptNumber('Changeset ID', '1');
    if (changesetId === undefined) {
      return;
    }

    return runAndShow('arm-tfs changeset', output, async () => client.changesetShow(changesetId));
  });

  register('armTfs.showLabel', async (input) => {
    const labelId = readStringOption(input, 'labelId') ?? await promptPath('Label ID', '');
    if (!labelId) {
      return;
    }

    return runAndShow('arm-tfs label', output, async () => client.labelShow(labelId));
  });

  register('armTfs.showMergeBase', async (input) => {
    const sourcePath = readStringOption(input, 'sourcePath') ?? await promptPath(t('sidebar.prompt.mergeSourcePath'), '$/');
    if (!sourcePath) {
      return;
    }

    const targetPath = readStringOption(input, 'targetPath') ?? await promptPath(t('sidebar.prompt.mergeTargetPath'), '$/');
    if (!targetPath) {
      return;
    }

    return runAndShow('arm-tfs merge base', output, async () => client.mergeBase(sourcePath, targetPath));
  });

  register('armTfs.showMergeCandidates', async (input) => {
    const sourcePath = readStringOption(input, 'sourcePath') ?? await promptPath(t('sidebar.prompt.mergeSourcePath'), '$/');
    if (!sourcePath) {
      return;
    }

    const targetPath = readStringOption(input, 'targetPath') ?? await promptPath(t('sidebar.prompt.mergeTargetPath'), '$/');
    if (!targetPath) {
      return;
    }

    const top = readNumberOption(input, 'top') ?? await promptNumber('Candidate result count', '20');
    if (top === undefined) {
      return;
    }

    return runAndShow('arm-tfs merge candidates', output, async () =>
      client.mergeCandidates(sourcePath, targetPath, top, readNumberOption(input, 'scan') ?? 80),
    );
  });

  registerResourceCommand('armTfs.refreshScm', async () => {
    await refreshUi();
  });

  registerResourceCommand('armTfs.openResourceDiff', async (resource) => {
    await scm.openDiff(resource);
  });

  registerResourceCommand('armTfs.checkoutResource', async (resource) => {
    await scm.checkout(resource);
  });

  registerResourceCommand('armTfs.addResource', async (resource) => {
    await scm.add(resource);
  });

  registerResourceCommand('armTfs.undoResource', async (resource) => {
    await scm.undo(resource);
  });

  registerResourceCommand('armTfs.checkinFromScm', async () => {
    await scm.checkin();
  });

  return { client };
}

export function deactivate(): void {}

async function runAndShow(title: string, output: vscode.OutputChannel, runner: () => Promise<unknown>): Promise<unknown> {
  try {
    const result = await runner();
    const document = await vscode.workspace.openTextDocument({
      language: 'json',
      content: JSON.stringify(result, null, 2),
    });
    await vscode.window.showTextDocument(document, { preview: false });
    vscode.window.setStatusBarMessage(t('status.completed', { title }), 2500);
    return result;
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
      void vscode.window.showErrorMessage(t('error.failed', { title, message: translateCliMessage(error.message) }));
      return undefined;
    }

    const message = translateCliMessage(error instanceof Error ? error.message : `${error}`);
    output.appendLine(message);
    void vscode.window.showErrorMessage(t('error.failed', { title, message }));
    return undefined;
  }
}

async function runAndShowText(title: string, output: vscode.OutputChannel, runner: () => Promise<string>): Promise<string | undefined> {
  try {
    const result = await runner();
    const document = await vscode.workspace.openTextDocument({
      language: 'text',
      content: translateCliText(result),
    });
    await vscode.window.showTextDocument(document, { preview: false });
    vscode.window.setStatusBarMessage(t('status.completed', { title }), 2500);
    return result;
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
      void vscode.window.showErrorMessage(t('error.failed', { title, message: translateCliMessage(error.message) }));
      return undefined;
    }

    const message = translateCliMessage(error instanceof Error ? error.message : `${error}`);
    output.appendLine(message);
    void vscode.window.showErrorMessage(t('error.failed', { title, message }));
    return undefined;
  }
}

async function promptPath(prompt: string, value?: string): Promise<string | undefined> {
  return vscode.window.showInputBox({
    prompt,
    value,
    ignoreFocusOut: true,
  });
}

async function promptOptionalPath(prompt: string, value?: string): Promise<string | undefined> {
  const result = await vscode.window.showInputBox({
    prompt,
    value,
    ignoreFocusOut: true,
  });

  if (result === undefined) {
    return undefined;
  }

  return result.trim() || undefined;
}

async function promptServerPath(prompt: string, value?: string): Promise<string | undefined> {
  const result = await vscode.window.showInputBox({
    prompt,
    value,
    ignoreFocusOut: true,
    validateInput(input) {
      return input.trim().startsWith('$/') ? undefined : 'Enter a TFVC server path starting with $/.';
    },
  });

  return result?.trim() || undefined;
}

async function promptLocalFolder(prompt: string, defaultPath?: string): Promise<string | undefined> {
  const choices: Array<{ label: string; description?: string; value: 'workspace' | 'browse' | 'manual' }> = [];
  if (defaultPath) {
    choices.push({
      label: 'Use current workspace folder',
      description: defaultPath,
      value: 'workspace',
    });
  }
  choices.push({
    label: 'Browse for folder...',
    description: 'Pick an existing local folder',
    value: 'browse',
  });
  choices.push({
    label: 'Enter path manually...',
    description: 'Create or reuse any local folder path',
    value: 'manual',
  });

  const choice = await vscode.window.showQuickPick(choices, {
    placeHolder: prompt,
    ignoreFocusOut: true,
  });
  if (!choice) {
    return undefined;
  }

  if (choice.value === 'workspace') {
    return defaultPath;
  }

  if (choice.value === 'browse') {
    const selected = await vscode.window.showOpenDialog({
      canSelectFiles: false,
      canSelectFolders: true,
      canSelectMany: false,
      openLabel: 'Use as checkout destination',
      defaultUri: defaultPath ? vscode.Uri.file(defaultPath) : undefined,
    });
    return selected?.[0]?.fsPath;
  }

  return promptPath('Local folder path', defaultPath);
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

async function promptBoolean(prompt: string, defaultValue: boolean): Promise<boolean | undefined> {
  const selected = await vscode.window.showQuickPick(
    [
      { label: defaultValue ? 'Yes' : 'No', value: defaultValue },
      { label: defaultValue ? 'No' : 'Yes', value: !defaultValue },
    ],
    { placeHolder: prompt },
  );

  return selected?.value;
}

type CommandInput = Record<string, unknown>;

function parseCommandInput(raw: unknown): CommandInput | undefined {
  if (raw === undefined || raw === null) {
    return undefined;
  }

  if (raw instanceof vscode.Uri) {
    return { path: raw.fsPath };
  }

  if (isScmResourceState(raw)) {
    return { path: raw.resourceUri.fsPath };
  }

  if (typeof raw === 'string') {
    try {
      const parsed = JSON.parse(raw);
      return isCommandInput(parsed) ? parsed : undefined;
    } catch {
      return undefined;
    }
  }

  if (isBranchNodeLike(raw)) {
    return {
      path: raw.branch.path,
      serverPath: raw.branch.path,
    };
  }

  return isCommandInput(raw) ? raw : undefined;
}

function isBranchNodeLike(value: unknown): value is { branch: { path: string } } {
  if (typeof value !== 'object' || value === null || !('branch' in value)) {
    return false;
  }

  const branch = (value as { branch?: unknown }).branch;
  return typeof branch === 'object' && branch !== null && 'path' in branch && typeof (branch as { path: unknown }).path === 'string';
}

function isCommandInput(value: unknown): value is CommandInput {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isScmResourceState(value: unknown): value is vscode.SourceControlResourceState {
  if (typeof value !== 'object' || value === null || !('resourceUri' in value)) {
    return false;
  }

  const candidate = (value as { resourceUri?: unknown }).resourceUri;
  return candidate instanceof vscode.Uri;
}

function readStringOption(input: CommandInput | undefined, key: string): string | undefined {
  const value = input?.[key];
  return typeof value === 'string' && value.trim() ? value : undefined;
}

function readServerPathOption(input: CommandInput | undefined): string | undefined {
  const candidate = readStringOption(input, 'serverPath') ?? readStringOption(input, 'path');
  return candidate && isServerPath(candidate) ? candidate : undefined;
}

function readNumberOption(input: CommandInput | undefined, key: string): number | undefined {
  const value = input?.[key];
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === 'string' && /^\d+$/.test(value)) {
    return Number.parseInt(value, 10);
  }

  return undefined;
}

function readBooleanOption(input: CommandInput | undefined, key: string): boolean | undefined {
  const value = input?.[key];
  if (typeof value === 'boolean') {
    return value;
  }

  if (typeof value === 'string') {
    if (value === 'true') {
      return true;
    }
    if (value === 'false') {
      return false;
    }
  }

  return undefined;
}

function getActivePath(): string | undefined {
  const activeUri = vscode.window.activeTextEditor?.document.uri;
  if (activeUri?.scheme === 'file') {
    return activeUri.fsPath;
  }

  return undefined;
}

function getWorkspaceRoot(): string | undefined {
  return vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
}

interface LocalWorkspaceContext {
  workspaceRoot: string;
  commandCwd: string;
}

async function resolveLocalWorkspaceContext(targetPath?: string): Promise<LocalWorkspaceContext | undefined> {
  const workspaceRoot = await findTfvcWorkspaceRoot(targetPath ?? getActivePath() ?? getWorkspaceRoot());
  if (!workspaceRoot) {
    void vscode.window.showWarningMessage(t('warning.noWorkspace.path'));
    return undefined;
  }

  return {
    workspaceRoot,
    commandCwd: getCommandCwd(workspaceRoot, targetPath),
  };
}

async function withLocalWorkspace<T>(targetPath: string | undefined, runner: (localContext: LocalWorkspaceContext) => Promise<T>): Promise<T> {
  const localContext = await resolveLocalWorkspaceContext(targetPath);
  if (!localContext) {
    throw new Error(t('warning.noWorkspace.path'));
  }

  return runner(localContext);
}

function isServerPath(targetPath: string): boolean {
  return targetPath.startsWith('$/');
}
