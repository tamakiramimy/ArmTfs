import * as vscode from 'vscode';
import type { ArmTfsCliClient, ArmTfsConnectionEnvironment } from './armTfsCliClient';
import { t } from './i18n';

const PROFILES_KEY = 'armTfs.connections';
const ACTIVE_PROFILE_KEY = 'armTfs.activeConnection';
const PAT_SECRET_PREFIX = 'armTfs.connection.pat.';

export interface TfsConnectionProfile {
  id: string;
  name: string;
  serverUrl: string;
  rootPath: string;
  displayName?: string;
}

class ConnectionTreeItem extends vscode.TreeItem {
  constructor(
    label: string,
    contextValue: string,
    description?: string,
    command?: vscode.Command,
    icon?: string,
  ) {
    super(label, vscode.TreeItemCollapsibleState.None);
    this.contextValue = contextValue;
    this.description = description;
    this.command = command;
    this.iconPath = new vscode.ThemeIcon(icon ?? 'gear');
  }
}

class ProfileTreeItem extends ConnectionTreeItem {
  constructor(public readonly profile: TfsConnectionProfile, active: boolean) {
    super(
      profile.name,
      'armTfsConnectionProfile',
      active ? t('connections.active') : profile.serverUrl,
      { command: 'armTfs.connections.select', title: t('connections.select'), arguments: [profile] },
      active ? 'check' : 'server',
    );
    this.tooltip = `${profile.name}\n${profile.serverUrl}\n${profile.rootPath}`;
  }
}

class ConnectionsProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
  private readonly emitter = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this.emitter.event;

  constructor(private readonly controller: ArmTfsConnectionsController) {}

  refresh(): void {
    this.emitter.fire();
  }

  getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
    return element;
  }

  getChildren(): vscode.TreeItem[] {
    const invocation = this.controller.getCliDescription();
    const profiles = this.controller.getProfiles();
    const activeId = this.controller.getActiveProfile()?.id;
    return [
      new ConnectionTreeItem(
        t('connections.cli'),
        'armTfsCliConfiguration',
        invocation || t('connections.cli.unconfigured'),
        { command: 'armTfs.configureCliCommand', title: t('connections.configureCli') },
        invocation ? 'terminal' : 'warning',
      ),
      new ConnectionTreeItem(
        t('connections.add'),
        'armTfsAddConnection',
        profiles.length ? t('connections.count', { count: profiles.length }) : t('connections.none'),
        { command: 'armTfs.connections.add', title: t('connections.add') },
        'add',
      ),
      ...profiles.map((profile) => new ProfileTreeItem(profile, profile.id === activeId)),
    ];
  }
}

export class ArmTfsConnectionsController implements vscode.Disposable {
  private readonly provider: ConnectionsProvider;
  private readonly disposables: vscode.Disposable[] = [];
  private cliDescription = '';

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly client: ArmTfsCliClient,
    private readonly onConnectionChanged: (profile: TfsConnectionProfile | undefined) => Promise<void>,
  ) {
    this.provider = new ConnectionsProvider(this);
    this.disposables.push(
      vscode.window.registerTreeDataProvider('armTfs.connections', this.provider),
      vscode.commands.registerCommand('armTfs.connections.add', () => this.addProfile()),
      vscode.commands.registerCommand('armTfs.connections.edit', (node?: ProfileTreeItem | TfsConnectionProfile) => this.editProfile(this.unwrap(node))),
      vscode.commands.registerCommand('armTfs.connections.delete', (node?: ProfileTreeItem | TfsConnectionProfile) => this.deleteProfile(this.unwrap(node))),
      vscode.commands.registerCommand('armTfs.connections.select', (node?: ProfileTreeItem | TfsConnectionProfile) => this.selectProfile(this.unwrap(node))),
    );
  }

  async initialize(): Promise<void> {
    await this.refreshCliDescription();
    const profiles = this.getProfiles();
    let active = this.getActiveProfile();
    if (!active && profiles.length) {
      active = profiles[0];
      await this.context.globalState.update(ACTIVE_PROFILE_KEY, profiles[0].id);
    }
    await this.activateProfile(active);
  }

  dispose(): void {
    vscode.Disposable.from(...this.disposables).dispose();
  }

  getProfiles(): TfsConnectionProfile[] {
    return this.context.globalState.get<TfsConnectionProfile[]>(PROFILES_KEY, []);
  }

  getActiveProfile(): TfsConnectionProfile | undefined {
    const id = this.context.globalState.get<string>(ACTIVE_PROFILE_KEY);
    return this.getProfiles().find((profile) => profile.id === id);
  }

  getCliDescription(): string {
    return this.cliDescription;
  }

  async getActiveEnvironment(): Promise<ArmTfsConnectionEnvironment | undefined> {
    const profile = this.getActiveProfile();
    if (!profile) {
      return undefined;
    }
    const pat = await this.context.secrets.get(`${PAT_SECRET_PREFIX}${profile.id}`);
    if (!pat) {
      return undefined;
    }
    return {
      serverUrl: profile.serverUrl,
      pat,
      displayName: profile.displayName,
    };
  }

  async refreshCliDescription(): Promise<void> {
    const configuredCommand = vscode.workspace.getConfiguration('armTfs').get<string>('cli.command')?.trim();
    if (!configuredCommand) {
      this.cliDescription = '';
      this.provider.refresh();
      return;
    }
    try {
      this.cliDescription = await this.client.describeResolvedInvocation();
    } catch {
      this.cliDescription = '';
    }
    this.provider.refresh();
  }

  async updateActiveRootPath(rootPath: string): Promise<void> {
    const active = this.getActiveProfile();
    if (!active) {
      return;
    }
    const updated = { ...active, rootPath };
    await this.context.globalState.update(
      PROFILES_KEY,
      this.getProfiles().map((profile) => profile.id === active.id ? updated : profile),
    );
    this.provider.refresh();
  }

  private unwrap(node?: ProfileTreeItem | TfsConnectionProfile): TfsConnectionProfile | undefined {
    return node instanceof ProfileTreeItem ? node.profile : node;
  }

  private async addProfile(): Promise<void> {
    const profile = await this.promptProfile();
    if (!profile) {
      return;
    }
    const profiles = [...this.getProfiles(), profile.metadata];
    await this.context.globalState.update(PROFILES_KEY, profiles);
    await this.context.globalState.update(ACTIVE_PROFILE_KEY, profile.metadata.id);
    await this.context.secrets.store(`${PAT_SECRET_PREFIX}${profile.metadata.id}`, profile.pat);
    await this.activateProfile(profile.metadata);
  }

  private async editProfile(profile?: TfsConnectionProfile): Promise<void> {
    if (!profile) {
      return;
    }
    const edited = await this.promptProfile(profile);
    if (!edited) {
      return;
    }
    await this.context.globalState.update(
      PROFILES_KEY,
      this.getProfiles().map((item) => item.id === profile.id ? edited.metadata : item),
    );
    if (edited.pat) {
      await this.context.secrets.store(`${PAT_SECRET_PREFIX}${profile.id}`, edited.pat);
    }
    if (this.getActiveProfile()?.id === profile.id) {
      await this.activateProfile(edited.metadata);
    } else {
      this.provider.refresh();
    }
  }

  private async deleteProfile(profile?: TfsConnectionProfile): Promise<void> {
    if (!profile) {
      return;
    }
    const confirmed = await vscode.window.showWarningMessage(
      t('connections.delete.confirm', { name: profile.name }),
      { modal: true },
      t('connections.delete'),
    );
    if (!confirmed) {
      return;
    }
    const wasActive = this.getActiveProfile()?.id === profile.id;
    const remaining = this.getProfiles().filter((item) => item.id !== profile.id);
    await this.context.globalState.update(PROFILES_KEY, remaining);
    await this.context.secrets.delete(`${PAT_SECRET_PREFIX}${profile.id}`);
    if (wasActive) {
      await this.context.globalState.update(ACTIVE_PROFILE_KEY, remaining[0]?.id);
      await this.activateProfile(remaining[0]);
    } else {
      this.provider.refresh();
    }
  }

  private async selectProfile(profile?: TfsConnectionProfile): Promise<void> {
    if (!profile) {
      return;
    }
    await this.context.globalState.update(ACTIVE_PROFILE_KEY, profile.id);
    await this.activateProfile(profile);
  }

  private async activateProfile(profile: TfsConnectionProfile | undefined): Promise<void> {
    if (profile) {
      const target = vscode.workspace.workspaceFolders?.length
        ? vscode.ConfigurationTarget.Workspace
        : vscode.ConfigurationTarget.Global;
      const config = vscode.workspace.getConfiguration('armTfs');
      await config.update('serverExplorer.rootPath', profile.rootPath, target);
      await config.update('branch.scope', profile.rootPath, target);
    }
    this.provider.refresh();
    await this.onConnectionChanged(profile);
  }

  private async promptProfile(existing?: TfsConnectionProfile): Promise<{
    metadata: TfsConnectionProfile;
    pat: string;
  } | undefined> {
    const name = await vscode.window.showInputBox({
      prompt: t('connections.prompt.name'),
      value: existing?.name,
      ignoreFocusOut: true,
      validateInput: (value) => value.trim() ? undefined : t('connections.validate.required'),
    });
    if (!name) {
      return undefined;
    }
    const serverUrl = await vscode.window.showInputBox({
      prompt: t('extension.prompt.serverUrl'),
      value: existing?.serverUrl,
      ignoreFocusOut: true,
      validateInput: (value) => /^https?:\/\//i.test(value.trim()) ? undefined : t('extension.validate.httpUrl'),
    });
    if (!serverUrl) {
      return undefined;
    }
    const existingPat = existing ? await this.context.secrets.get(`${PAT_SECRET_PREFIX}${existing.id}`) : undefined;
    const pat = await vscode.window.showInputBox({
      prompt: existingPat ? t('connections.prompt.patOptional') : t('extension.prompt.pat'),
      password: true,
      ignoreFocusOut: true,
      validateInput: (value) => value.trim() || existingPat ? undefined : t('extension.validate.pat'),
    });
    if (pat === undefined || (!pat.trim() && !existingPat)) {
      return undefined;
    }
    const displayName = await vscode.window.showInputBox({
      prompt: t('extension.prompt.displayName'),
      value: existing?.displayName,
      ignoreFocusOut: true,
    });
    if (displayName === undefined) {
      return undefined;
    }
    const rootPath = await vscode.window.showInputBox({
      prompt: t('serverExplorer.prompt.rootPath'),
      value: existing?.rootPath ?? '$/',
      ignoreFocusOut: true,
      validateInput: (value) => value.trim().startsWith('$/') ? undefined : t('sidebar.validate.serverPath'),
    });
    if (!rootPath) {
      return undefined;
    }

    return {
      metadata: {
        id: existing?.id ?? `${Date.now()}-${Math.random().toString(36).slice(2, 10)}`,
        name: name.trim(),
        serverUrl: serverUrl.trim().replace(/\/+$/, ''),
        rootPath: rootPath.trim(),
        displayName: displayName.trim() || undefined,
      },
      pat: pat.trim() || existingPat!,
    };
  }
}
