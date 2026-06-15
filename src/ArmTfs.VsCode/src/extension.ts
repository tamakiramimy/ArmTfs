import * as path from 'node:path';
import * as os from 'node:os';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { spawn } from 'node:child_process';
import * as vscode from 'vscode';
import { ArmTfsCliClient, ArmTfsCliError } from './armTfsCliClient';
import { ArmTfsConnectionsController } from './connections';
import { ArmTfsHistoryBrowser } from './historyBrowser';
import { getUiLanguage, t, translateCliMessage, translateCliText, type UiLanguage } from './i18n';
import { checkoutServerPathToLocalFolder } from './serverPathCheckout';
import { ArmTfsScmController, ArmTfsResourceState } from './scm';
import { ArmTfsSidebarController, ArmTfsServerExplorerController } from './sidebar';
import { findTfvcWorkspaceRoot, getCommandCwd } from './tfvcContext';
import { openServerVersionDiff } from './versionedFiles';

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

  let workspaceFileRefreshTimer: ReturnType<typeof setTimeout> | undefined;
  const scheduleWorkspaceFileRefresh = (uri: vscode.Uri) => {
    if (!shouldRefreshForFileSystemEvent(uri)) {
      return;
    }

    if (workspaceFileRefreshTimer) {
      clearTimeout(workspaceFileRefreshTimer);
    }
    workspaceFileRefreshTimer = setTimeout(() => {
      workspaceFileRefreshTimer = undefined;
      void refreshUi();
    }, 800);
  };
  const workspaceFileWatcher = vscode.workspace.createFileSystemWatcher('**/*');

  const refreshLanguageUi = async () => {
    scm.refreshLabels();
    connections.refreshLabels();
    serverExplorer.refreshLabels();
    sidebar.refreshLabels();
    await connections.refreshCliDescription();
    connections.refresh();
    serverExplorer.refresh();
    await historyBrowser.refreshLanguage();
    await refreshUi();
  };

  context.subscriptions.push(output, historyBrowser, scm, sidebar, serverExplorer, connections, workspaceFileWatcher, vscode.window.registerFileDecorationProvider(scm));
  void (async () => {
    await connections.initialize();
    await scm.initialize();
    await sidebar.initialize();
    uiInitialized = true;
    await maybeRunGuiSmoke(client, historyBrowser, output);
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
        void refreshLanguageUi();
      }
    }),
    workspaceFileWatcher.onDidChange(scheduleWorkspaceFileRefresh),
    workspaceFileWatcher.onDidCreate(scheduleWorkspaceFileRefresh),
    workspaceFileWatcher.onDidDelete(scheduleWorkspaceFileRefresh),
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
      openLabel: t('extension.prompt.selectCli'),
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
    await refreshLanguageUi();

    const localeResult = await syncVsCodeDisplayLanguage(selected.value, output);
    if (localeResult.requiresRestart) {
      const action = await vscode.window.showInformationMessage(
        t('language.changed.reload', { language: selected.label }),
        { modal: false },
        t('language.reloadNow'),
        t('language.reloadLater'),
      );
      if (action === t('language.reloadNow')) {
        const restarted = await restartVsCodeNow(output);
        if (!restarted) {
          void vscode.window.showInformationMessage(t('language.restartManual'));
        }
      }
      return;
    }

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
    const name = readStringOption(input, 'name') ?? await promptPath(t('extension.prompt.workspaceName'), 'ArmTfsWorkspace');
    if (!name) {
      return;
    }

    const serverPath = readStringOption(input, 'serverPath') ?? await promptPath(t('extension.prompt.serverPath'), '$/');
    if (!serverPath) {
      return;
    }

    const directory = readStringOption(input, 'directory') ?? await promptPath(t('extension.prompt.workspaceRootDirectory'), workspaceRoot);
    if (!directory) {
      return;
    }

    const localPath = readStringOption(input, 'localPath') ?? await promptOptionalPath(t('extension.prompt.mappedLocalPath'), directory);

    const result = await runAndShowText(t('extension.operation.workspaceNew'), output, async () => client.workspaceNew(name, serverPath, directory, localPath));
    await refreshUi();
    return result;
  });

  register('armTfs.checkoutServerPathToFolder', async (input) => {
    const serverPath = readServerPathOption(input) ?? await promptServerPath(t('extension.prompt.tfvcServerPath'), '$/');
    if (!serverPath) {
      return;
    }

    const localPath = readStringOption(input, 'localPath') ?? readStringOption(input, 'directory') ?? await promptLocalFolder(t('extension.prompt.checkoutDestination'), getWorkspaceRoot());
    if (!localPath) {
      return;
    }

    const version = readNumberOption(input, 'version');

    const result = await runAndShowText(t('extension.operation.checkoutServerPath'), output, async () =>
      checkoutServerPathToLocalFolder(client, serverPath, localPath, { version }),
    );
    await refreshUi();
    return result;
  });

  register('armTfs.showWorkspace', async () => {
    const localContext = await resolveLocalWorkspaceContext();
    if (!localContext) {
      return undefined;
    }

    return runAndShowText(t('extension.operation.workspaceShow'), output, async () => client.workspaceShow({ cwdOverride: localContext.workspaceRoot }));
  });

  register('armTfs.addWorkspaceMapping', async (input) => {
    const serverPath = readStringOption(input, 'serverPath') ?? await promptPath(t('extension.prompt.additionalServerPath'), '$/');
    if (!serverPath) {
      return;
    }

    const workspaceRoot = getWorkspaceRoot() ?? '.';
    const localPath = readStringOption(input, 'localPath') ?? await promptPath(t('extension.prompt.localPath'), workspaceRoot);
    if (!localPath) {
      return;
    }

    const result = await runAndShowText(t('extension.operation.workspaceMap'), output, async () => client.workspaceMap(serverPath, localPath));
    await refreshUi();
    return result;
  });

  register('armTfs.runGet', async (input) => {
    const targetPath = readStringOption(input, 'path') ?? await promptPath(t('extension.prompt.getPath'), getActivePath() ?? '.');
    if (!targetPath) {
      return;
    }

    const mode = readStringOption(input, 'mode') ?? (await vscode.window.showQuickPick(
      [
        { label: t('extension.getMode.latest'), description: t('extension.getMode.latest.desc'), value: 'latest' },
        { label: t('extension.getMode.force'), description: t('extension.getMode.force.desc'), value: 'force' },
        { label: t('extension.getMode.dryRun'), description: t('extension.getMode.dryRun.desc'), value: 'dryRun' },
      ],
      { placeHolder: t('extension.getMode.placeholder') },
    ))?.value;
    if (!mode) {
      return;
    }

    const result = await runAndShowText(t('extension.operation.get'), output, async () =>
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
    const targetPath = readStringOption(input, 'path') ?? await promptPath(t('extension.prompt.checkoutPath'), getActivePath() ?? '.');
    if (!targetPath) {
      return;
    }

    const recursive = readBooleanOption(input, 'recursive') ?? await promptBoolean(t('extension.prompt.includeSubfiles'), false);
    if (recursive === undefined) {
      return;
    }

    const result = await runAndShowText(t('extension.operation.checkout'), output, async () =>
      withLocalWorkspace(targetPath, (localContext) => client.checkout([targetPath], recursive, { cwdOverride: localContext.commandCwd })),
    );
    await refreshUi();
    return result;
  });

  register('armTfs.add', async (input) => {
    const targetPath = readStringOption(input, 'path') ?? await promptPath(t('extension.prompt.addPath'), getActivePath() ?? '.');
    if (!targetPath) {
      return;
    }

    const recursive = readBooleanOption(input, 'recursive') ?? await promptBoolean(t('extension.prompt.includeSubfiles'), false);
    if (recursive === undefined) {
      return;
    }

    const result = await runAndShowText(t('extension.operation.add'), output, async () =>
      withLocalWorkspace(targetPath, (localContext) => client.add([targetPath], recursive, { cwdOverride: localContext.commandCwd })),
    );
    await refreshUi();
    return result;
  });

  register('armTfs.undo', async (input) => {
    const targetPath = readStringOption(input, 'path') ?? await promptPath(t('extension.prompt.undoPath'), getActivePath() ?? '.');
    if (!targetPath) {
      return;
    }

    const noRestore = readBooleanOption(input, 'noRestore') ?? await promptBoolean(t('extension.prompt.undoNoRestore'), false);
    if (noRestore === undefined) {
      return;
    }

    const result = await runAndShowText(t('extension.operation.undo'), output, async () =>
      withLocalWorkspace(targetPath, (localContext) => client.undo([targetPath], noRestore, { cwdOverride: localContext.commandCwd })),
    );
    await refreshUi();
    return result;
  });

  register('armTfs.checkin', async (input) => {
    const comment = readStringOption(input, 'comment') ?? await promptPath(t('extension.prompt.checkinComment'), '');
    if (!comment) {
      return;
    }

    const targetPath = readStringOption(input, 'path') ?? await promptPath(t('extension.prompt.checkinPath'), '.');
    if (!targetPath) {
      return;
    }

    const mode = readStringOption(input, 'mode') ?? (await vscode.window.showQuickPick(
      [
        { label: t('extension.checkinMode.submit'), value: 'submit' },
        { label: t('extension.checkinMode.dryRun'), value: 'dryRun' },
        { label: t('extension.checkinMode.keepPending'), value: 'keepPending' },
      ],
      { placeHolder: t('extension.checkinMode.placeholder') },
    ))?.value;
    if (!mode) {
      return;
    }

    const result = await runAndShowText(t('extension.operation.checkin'), output, async () =>
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

    return runAndShow(t('extension.operation.status'), output, async () =>
      client.status(targetPath, readBooleanOption(input, 'all') ?? true, { cwdOverride: localContext.commandCwd }),
    );
  });

  register('armTfs.showHistory', async (input) => {
    const targetPath = readStringOption(input, 'path') ?? await promptPath(t('extension.prompt.historyPath'), getActivePath() ?? '.');
    if (!targetPath) {
      return;
    }

    if (isServerPath(targetPath)) {
      await historyBrowser.open(targetPath);
      return undefined;
    }

    return withLocalWorkspace(targetPath, async (localContext) => {
      const status = await client.status(targetPath, false, { cwdOverride: localContext.commandCwd });
      const serverPath = status.items.find((item) => item.localPath === targetPath)?.serverPath
        ?? status.workspace.mappings
          .filter((mapping) => targetPath === mapping.localPath || targetPath.startsWith(`${mapping.localPath}${pathSeparator()}`))
          .sort((left, right) => right.localPath.length - left.localPath.length)
          .map((mapping) => remapLocalToServer(targetPath, mapping.localPath, mapping.serverPath))[0];
      if (!serverPath) {
        throw new Error(t('extension.error.resolveServerPath', { path: targetPath }));
      }
      await historyBrowser.open(serverPath);
      return undefined;
    });
  });

  register('armTfs.showDiff', async (input) => {
    const targetPath = readStringOption(input, 'path') ?? await promptPath(t('extension.prompt.diffPath'), getActivePath());
    if (!targetPath) {
      return;
    }

    if (!isServerPath(targetPath)) {
      await scm.openDiff(vscode.Uri.file(targetPath));
      return undefined;
    }

    const compareMode = readStringOption(input, 'compareMode') ?? (await vscode.window.showQuickPick(
      [
        { label: t('extension.diffBase.latest'), value: 'latest' },
        { label: t('extension.diffBase.base'), value: 'base' },
      ],
      { placeHolder: t('extension.diffBase.placeholder') },
    ))?.value;
    if (!compareMode) {
      return;
    }

    return runAndShow(t('extension.operation.diff'), output, async () =>
      withLocalWorkspace(targetPath, (localContext) => client.diff(targetPath, { useBase: compareMode === 'base' }, { cwdOverride: localContext.commandCwd })),
    );
  });

  register('armTfs.showBranch', async (input) => {
    const branchPath = readStringOption(input, 'path') ?? await promptPath(t('extension.prompt.branchPath'), '$/');
    if (!branchPath) {
      return;
    }

    return runAndShow(t('extension.operation.branch'), output, async () => client.branchShow(branchPath));
  });

  register('armTfs.showChangeset', async (input) => {
    const changesetId = readNumberOption(input, 'changesetId') ?? await promptNumber(t('extension.prompt.changesetId'), '1');
    if (changesetId === undefined) {
      return;
    }

    return runAndShow(t('extension.operation.changeset'), output, async () => client.changesetShow(changesetId));
  });

  register('armTfs.showLabel', async (input) => {
    const labelId = readStringOption(input, 'labelId') ?? await promptPath(t('extension.prompt.labelId'), '');
    if (!labelId) {
      return;
    }

    return runAndShow(t('extension.operation.label'), output, async () => client.labelShow(labelId));
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

    return runAndShow(t('extension.operation.mergeBase'), output, async () => client.mergeBase(sourcePath, targetPath));
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

    const top = readNumberOption(input, 'top') ?? await promptNumber(t('extension.prompt.candidateResultCount'), '20');
    if (top === undefined) {
      return;
    }

    return runAndShow(t('extension.operation.mergeCandidates'), output, async () =>
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
      return input.trim().startsWith('$/') ? undefined : t('extension.validate.serverPath');
    },
  });

  return result?.trim() || undefined;
}

async function promptLocalFolder(prompt: string, defaultPath?: string): Promise<string | undefined> {
  const choices: Array<{ label: string; description?: string; value: 'workspace' | 'browse' | 'manual' }> = [];
  if (defaultPath) {
    choices.push({
      label: t('extension.localFolder.useWorkspace'),
      description: defaultPath,
      value: 'workspace',
    });
  }
  choices.push({
    label: t('extension.localFolder.browse'),
    description: t('extension.localFolder.browse.desc'),
    value: 'browse',
  });
  choices.push({
    label: t('extension.localFolder.manual'),
    description: t('extension.localFolder.manual.desc'),
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
      openLabel: t('extension.localFolder.openLabel'),
      defaultUri: defaultPath ? vscode.Uri.file(defaultPath) : undefined,
    });
    return selected?.[0]?.fsPath;
  }

  return promptPath(t('extension.prompt.localFolderPath'), defaultPath);
}

async function promptNumber(prompt: string, value: string): Promise<number | undefined> {
  const raw = await vscode.window.showInputBox({
    prompt,
    value,
    ignoreFocusOut: true,
    validateInput(input) {
      return /^\d+$/.test(input) ? undefined : t('extension.validate.nonNegativeInteger');
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
      { label: defaultValue ? t('extension.boolean.yes') : t('extension.boolean.no'), value: defaultValue },
      { label: defaultValue ? t('extension.boolean.no') : t('extension.boolean.yes'), value: !defaultValue },
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

  if (isServerExplorerNodeLike(raw)) {
    return {
      path: raw.entry.serverPath,
      serverPath: raw.entry.serverPath,
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

function isServerExplorerNodeLike(value: unknown): value is { entry: { serverPath: string } } {
  if (typeof value !== 'object' || value === null || !('entry' in value)) {
    return false;
  }

  const entry = (value as { entry?: unknown }).entry;
  return typeof entry === 'object'
    && entry !== null
    && 'serverPath' in entry
    && typeof (entry as { serverPath: unknown }).serverPath === 'string';
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

function shouldRefreshForFileSystemEvent(uri: vscode.Uri): boolean {
  if (uri.scheme !== 'file') {
    return false;
  }

  const normalized = uri.fsPath.replace(/\\/g, '/').toLowerCase();
  return !/(^|\/)(\.git|\.tf|node_modules|bin|obj|out)(\/|$)/.test(normalized);
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

function remapLocalToServer(targetPath: string, localRoot: string, serverRoot: string): string {
  if (targetPath === localRoot) {
    return serverRoot;
  }

  const relativePath = path.relative(localRoot, targetPath).split(path.sep).join('/');
  return `${serverRoot.replace(/\/+$/, '')}/${relativePath.replace(/^\/+/, '')}`;
}

function pathSeparator(): string {
  return path.sep;
}

interface GuiSmokeScenario {
  historyPath?: string;
  initialChangesetId?: number;
  diff?: {
    fromPath: string;
    toPath?: string;
    fromVersion: number;
    toVersion: number;
    title?: string;
  };
}

async function maybeRunGuiSmoke(
  client: ArmTfsCliClient,
  historyBrowser: ArmTfsHistoryBrowser,
  output: vscode.OutputChannel,
): Promise<void> {
  const raw = process.env.ARM_TFS_GUI_SMOKE
    ?? (existsSync('/tmp/arm-tfs-gui-smoke.json') ? readFileSync('/tmp/arm-tfs-gui-smoke.json', 'utf8') : undefined);
  if (!raw) {
    return;
  }

  try {
    const scenario = JSON.parse(raw) as GuiSmokeScenario;
    if (scenario.historyPath) {
      await historyBrowser.open(scenario.historyPath, scenario.initialChangesetId);
    }
    if (scenario.diff) {
      await openServerVersionDiff(
        client,
        {
          serverPath: scenario.diff.fromPath,
          version: scenario.diff.fromVersion,
        },
        {
          serverPath: scenario.diff.toPath ?? scenario.diff.fromPath,
          version: scenario.diff.toVersion,
        },
        scenario.diff.title,
      );
    }
    output.appendLine('arm-tfs GUI smoke scenario executed.');
  } catch (error) {
    output.appendLine(`arm-tfs GUI smoke failed: ${error instanceof Error ? error.message : `${error}`}`);
  }
}

interface LocaleSyncResult {
  requiresRestart: boolean;
}

async function syncVsCodeDisplayLanguage(language: UiLanguage, output: vscode.OutputChannel): Promise<LocaleSyncResult> {
  const desiredLocale = language === 'auto' ? undefined : normalizeVsCodeLocale(language);
  const argvPath = getVsCodeArgvFilePath();
  const argvData = readVsCodeArgv(argvPath);
  const currentLocale = typeof argvData.locale === 'string' ? argvData.locale.toLowerCase() : undefined;
  const nextLocale = desiredLocale?.toLowerCase();

  if (currentLocale === nextLocale) {
    return { requiresRestart: false };
  }

  try {
    mkdirSync(path.dirname(argvPath), { recursive: true });
    if (desiredLocale) {
      argvData.locale = desiredLocale;
    } else {
      delete argvData.locale;
    }
    writeFileSync(argvPath, `${JSON.stringify(argvData, null, 2)}\n`, 'utf8');
    cleanupLegacyLocaleFiles(output);
    output.appendLine(
      desiredLocale
        ? `Updated VS Code runtime locale: ${desiredLocale} (${argvPath})`
        : `Cleared VS Code runtime locale override (${argvPath})`,
    );
    return { requiresRestart: true };
  } catch (error) {
    output.appendLine(`Unable to update VS Code runtime args at ${argvPath}: ${error instanceof Error ? error.message : `${error}`}`);
    return { requiresRestart: false };
  }
}

function normalizeVsCodeLocale(language: Exclude<UiLanguage, 'auto'>): string {
  return language.toLowerCase() === 'zh-cn' ? 'zh-cn' : 'en';
}

function readVsCodeArgv(argvPath: string): Record<string, unknown> {
  if (!existsSync(argvPath)) {
    return {};
  }

  try {
    const raw = readFileSync(argvPath, 'utf8');
    const json = raw
      .replace(/\/\*[\s\S]*?\*\//g, '')
      .replace(/^\s*\/\/.*$/gm, '')
      .trim();
    if (!json) {
      return {};
    }

    const parsed = JSON.parse(json);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
      ? { ...(parsed as Record<string, unknown>) }
      : {};
  } catch {
    return {};
  }
}

function getVsCodeArgvFilePath(): string {
  return path.join(os.homedir(), mapVsCodeArgvFolder(), 'argv.json');
}

function mapVsCodeArgvFolder(): string {
  const appName = vscode.env.appName.toLowerCase();
  if (appName.includes('insiders')) {
    return '.vscode-insiders';
  }
  if (appName.includes('exploration')) {
    return '.vscode-exploration';
  }
  if (appName.includes('vscodium')) {
    return '.vscode-oss';
  }
  if (appName.includes('cursor')) {
    return '.cursor';
  }
  return '.vscode';
}

function cleanupLegacyLocaleFiles(output: vscode.OutputChannel): void {
  const legacyPaths = [
    path.join(os.homedir(), 'Library', 'Application Support', 'Code', 'locale.json'),
    path.join(os.homedir(), 'Library', 'Application Support', 'Code', 'User', 'locale.json'),
  ];

  for (const legacyPath of legacyPaths) {
    try {
      if (existsSync(legacyPath)) {
        rmSync(legacyPath, { force: true });
        output.appendLine(`Removed legacy locale override: ${legacyPath}`);
      }
    } catch (error) {
      output.appendLine(`Unable to remove legacy locale override ${legacyPath}: ${error instanceof Error ? error.message : `${error}`}`);
    }
  }
}

async function restartVsCodeNow(output: vscode.OutputChannel): Promise<boolean> {
  try {
    if (process.platform === 'darwin') {
      const command = `sleep 1; open -a ${shellQuote(vscode.env.appName)}`;
      spawn('/bin/sh', ['-lc', command], { detached: true, stdio: 'ignore' }).unref();
      await vscode.commands.executeCommand('workbench.action.quit');
      return true;
    }

    if (process.platform === 'win32') {
      const cliName = mapVsCodeCliName();
      const command = `ping 127.0.0.1 -n 2 >NUL & start "" ${windowsQuote(cliName)}`;
      spawn('cmd.exe', ['/c', command], { detached: true, stdio: 'ignore' }).unref();
      await vscode.commands.executeCommand('workbench.action.quit');
      return true;
    }

    const cliName = mapVsCodeCliName();
    const command = `sleep 1; ${shellQuote(cliName)} >/dev/null 2>&1 &`;
    spawn('/bin/sh', ['-lc', command], { detached: true, stdio: 'ignore' }).unref();
    await vscode.commands.executeCommand('workbench.action.quit');
    return true;
  } catch (error) {
    output.appendLine(`Unable to restart VS Code automatically: ${error instanceof Error ? error.message : `${error}`}`);
    return false;
  }
}

function mapVsCodeCliName(): string {
  const appName = vscode.env.appName.toLowerCase();
  if (appName.includes('insiders')) {
    return 'code-insiders';
  }
  if (appName.includes('vscodium')) {
    return 'codium';
  }
  if (appName.includes('cursor')) {
    return 'cursor';
  }
  if (appName.includes('exploration')) {
    return 'code-exploration';
  }
  return 'code';
}

function shellQuote(value: string): string {
  return `'${value.replace(/'/g, `'\\''`)}'`;
}

function windowsQuote(value: string): string {
  return `"${value.replace(/"/g, '\\"')}"`;
}
