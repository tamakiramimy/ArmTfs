import * as path from 'node:path';
import * as vscode from 'vscode';
import type { ArmTfsCliClient, ArmTfsConnectionEnvironment } from './armTfsCliClient';
import { t } from './i18n';
import type { ConfiguredWorkspaceMapping } from './tfvcContext';
import { setWorkspaceMappingsProvider } from './tfvcContext';
import {
  getActiveConnectionId,
  getConfigValue,
  getStoredConnectionProfiles,
  setActiveConnectionId,
  setConfigValue,
  setStoredConnectionProfiles,
  type ArmTfsStoredConnectionProfile,
} from './userConfig';

const PROFILES_KEY = 'armTfs.connections';
const ACTIVE_PROFILE_KEY = 'armTfs.activeConnection';
const PAT_SECRET_PREFIX = 'armTfs.connection.pat.';

export interface TfsConnectionProfile extends ArmTfsStoredConnectionProfile {
  id: string;
  name: string;
  serverUrl: string;
  rootPath: string;
  displayName?: string;
  mappings?: ConfiguredWorkspaceMapping[];
  pat?: string;
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

class WorkspaceMappingsSectionItem extends ConnectionTreeItem {
  constructor(activeProfileName: string | undefined, count: number) {
    const description = activeProfileName
      ? (count
        ? t('connections.workspaceMappings.countForProfile', { name: activeProfileName, count })
        : t('connections.workspaceMappings.noneForProfile', { name: activeProfileName }))
      : t('connections.workspaceMappings.noActiveProfile');
    super(
      t('connections.workspaceMappings'),
      'armTfsWorkspaceMappingsSection',
      description,
      activeProfileName ? { command: 'armTfs.workspaceMappings.add', title: t('connections.workspaceMappings.add') } : undefined,
      'link',
    );
  }
}

class WorkspaceMappingTreeItem extends ConnectionTreeItem {
  constructor(public readonly mapping: ConfiguredWorkspaceMapping) {
    super(
      mapping.serverPath,
      'armTfsWorkspaceMapping',
      path.normalize(mapping.localPath),
      undefined,
      'folder',
    );
    this.tooltip = `${mapping.serverPath} → ${mapping.localPath}`;
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
    const activeProfile = this.controller.getActiveProfile();
    const activeId = activeProfile?.id;
    const mappings = this.controller.getActiveProfileMappings();
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
      new WorkspaceMappingsSectionItem(activeProfile?.name, mappings.length),
      ...mappings.map((mapping) => new WorkspaceMappingTreeItem(mapping)),
    ];
  }
}

export class ArmTfsConnectionsController implements vscode.Disposable {
  private readonly provider: ConnectionsProvider;
  private readonly disposables: vscode.Disposable[] = [];
  private readonly treeView: vscode.TreeView<vscode.TreeItem>;
  private cliDescription = '';

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly client: ArmTfsCliClient,
    private readonly onConnectionChanged: (profile: TfsConnectionProfile | undefined) => Promise<void>,
  ) {
    this.provider = new ConnectionsProvider(this);
    this.treeView = vscode.window.createTreeView('armTfs.connections', {
      treeDataProvider: this.provider,
    });
    this.refreshLabels();
    setWorkspaceMappingsProvider(() => this.getActiveProfileMappings());
    this.disposables.push(
      this.treeView,
      vscode.commands.registerCommand('armTfs.connections.add', () => this.addProfile()),
      vscode.commands.registerCommand('armTfs.connections.edit', (node?: ProfileTreeItem | TfsConnectionProfile) => this.editProfile(this.unwrap(node))),
      vscode.commands.registerCommand('armTfs.connections.delete', (node?: ProfileTreeItem | TfsConnectionProfile) => this.deleteProfile(this.unwrap(node))),
      vscode.commands.registerCommand('armTfs.connections.select', (node?: ProfileTreeItem | TfsConnectionProfile) => this.selectProfile(this.unwrap(node))),
      vscode.commands.registerCommand('armTfs.workspaceMappings.add', () => this.addWorkspaceMapping()),
      vscode.commands.registerCommand('armTfs.workspaceMappings.delete', (node?: WorkspaceMappingTreeItem) => this.deleteWorkspaceMapping(node?.mapping)),
      vscode.workspace.onDidChangeConfiguration((e) => {
        if (e.affectsConfiguration('armTfs.workspaceMappings')) {
          this.provider.refresh();
        }
      }),
    );
  }

  async initialize(): Promise<void> {
    await this.migrateLegacyConnectionState();
    await this.refreshCliDescription();
    const profiles = this.getProfiles();
    let active = this.getActiveProfile();
    if (!active && profiles.length) {
      active = profiles[0];
      setActiveConnectionId(profiles[0].id);
    }
    await this.activateProfile(active);
  }

  dispose(): void {
    setWorkspaceMappingsProvider(undefined);
    vscode.Disposable.from(...this.disposables).dispose();
  }

  getProfiles(): TfsConnectionProfile[] {
    return getStoredConnectionProfiles();
  }

  getActiveProfile(): TfsConnectionProfile | undefined {
    const id = getActiveConnectionId();
    return this.getProfiles().find((profile) => profile.id === id);
  }

  getCliDescription(): string {
    return this.cliDescription;
  }

  /**
   * Return the workspace mappings of the currently active TFS connection profile.
   * Empty array when there is no active profile.
   */
  getActiveProfileMappings(): ConfiguredWorkspaceMapping[] {
    const active = this.getActiveProfile();
    if (!active?.mappings) {
      return [];
    }
    return active.mappings.filter((m) => m.serverPath?.startsWith('$/') && m.localPath?.trim());
  }

  /**
   * Persist a new mappings array on the given profile and broadcast the change.
   */
  private async updateProfileMappings(profileId: string, mappings: ConfiguredWorkspaceMapping[]): Promise<void> {
    const profiles = this.getProfiles().map((profile) =>
      profile.id === profileId ? { ...profile, mappings } : profile,
    );
    setStoredConnectionProfiles(profiles);
    this.provider.refresh();
  }

  refresh(): void {
    this.provider.refresh();
  }

  refreshLabels(): void {
    this.treeView.title = t('view.connections');
  }

  async getActiveEnvironment(): Promise<ArmTfsConnectionEnvironment | undefined> {
    const profile = this.getActiveProfile();
    if (!profile) {
      return undefined;
    }
    const pat = profile.pat ?? await this.context.secrets.get(`${PAT_SECRET_PREFIX}${profile.id}`);
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
    const configuredCommand = getConfigValue<string>('cli.command', '').trim();
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
    setStoredConnectionProfiles(this.getProfiles().map((profile) => profile.id === active.id ? updated : profile));
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
    setStoredConnectionProfiles(profiles);
    setActiveConnectionId(profile.metadata.id);
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
    setStoredConnectionProfiles(this.getProfiles().map((item) => item.id === profile.id ? edited.metadata : item));
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
    setStoredConnectionProfiles(remaining);
    await this.context.secrets.delete(`${PAT_SECRET_PREFIX}${profile.id}`);
    if (wasActive) {
      setActiveConnectionId(remaining[0]?.id);
      await this.activateProfile(remaining[0]);
    } else {
      this.provider.refresh();
    }
  }

  private async selectProfile(profile?: TfsConnectionProfile): Promise<void> {
    if (!profile) {
      return;
    }
    setActiveConnectionId(profile.id);
    await this.activateProfile(profile);
  }

  private async activateProfile(profile: TfsConnectionProfile | undefined): Promise<void> {
    if (profile) {
      // Only seed serverExplorer.rootPath as a fallback when nothing else is set.
      // We deliberately do NOT touch branch.scope here — the active branch must come from
      // the workspace's .tf/workspace.json (auto-discovery), not from the profile's root.
      // Otherwise opening a specific branch checkout would silently get re-pointed to '$/'.
      const currentRoot = getConfigValue<string>('serverExplorer.rootPath', '').trim();
      if (!currentRoot) {
        setConfigValue('serverExplorer.rootPath', profile.rootPath);
      }
    }
    this.provider.refresh();
    await this.onConnectionChanged(profile);
  }

  private async migrateLegacyConnectionState(): Promise<void> {
    if (getStoredConnectionProfiles().length) {
      return;
    }

    const legacyProfiles = this.context.globalState.get<TfsConnectionProfile[]>(PROFILES_KEY, []);
    if (!legacyProfiles.length) {
      return;
    }

    const migrated: TfsConnectionProfile[] = [];
    for (const profile of legacyProfiles) {
      const pat = profile.pat ?? await this.context.secrets.get(`${PAT_SECRET_PREFIX}${profile.id}`);
      migrated.push({ ...profile, pat });
    }

    setStoredConnectionProfiles(migrated);
    const legacyActiveId = this.context.globalState.get<string>(ACTIVE_PROFILE_KEY);
    setActiveConnectionId(legacyActiveId ?? migrated[0]?.id);
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
    const existingPat = existing?.pat ?? (existing ? await this.context.secrets.get(`${PAT_SECRET_PREFIX}${existing.id}`) : undefined);
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

    // Local root mapping for the connection's rootPath. Asked here in the same flow as URL/PAT
    // so the user does not have to set it up separately every time. Persisted as the first
    // entry of profile.mappings.
    const existingRootMapping = existing?.mappings?.find((m) => m.serverPath === (existing.rootPath ?? rootPath));
    const localRoot = await vscode.window.showInputBox({
      prompt: t('connections.prompt.localRoot', { name: name.trim() }),
      placeHolder: t('connections.prompt.localRoot.placeholder'),
      value: existingRootMapping?.localPath ?? '',
      ignoreFocusOut: true,
      validateInput: (value) => {
        const trimmed = value.trim();
        if (!trimmed) {
          // Allow empty — user may want to set it up later via the standalone tree command.
          return undefined;
        }
        return (trimmed.startsWith('/') || trimmed.startsWith('~') || /^[A-Za-z]:[/\\]/.test(trimmed))
          ? undefined
          : t('connections.workspaceMappings.validate.localPath');
      },
    });
    if (localRoot === undefined) {
      return undefined;
    }

    const trimmedRoot = rootPath.trim();
    const trimmedLocal = localRoot.trim();
    let mappings = existing?.mappings ?? [];
    if (trimmedLocal) {
      const expanded = expandHome(trimmedLocal);
      // Replace any existing mapping for the connection's rootPath with the new local root.
      mappings = [
        { serverPath: trimmedRoot, localPath: expanded },
        ...mappings.filter((m) => m.serverPath !== trimmedRoot && m.serverPath !== existing?.rootPath),
      ];
    }

    return {
      metadata: {
        id: existing?.id ?? `${Date.now()}-${Math.random().toString(36).slice(2, 10)}`,
        name: name.trim(),
        serverUrl: serverUrl.trim().replace(/\/+$/, ''),
        rootPath: trimmedRoot,
        displayName: displayName.trim() || undefined,
        mappings,
        pat: pat.trim() || existingPat!,
      },
      pat: pat.trim() || existingPat!,
    };
  }

  private async addWorkspaceMapping(): Promise<void> {
    const active = this.getActiveProfile();
    if (!active) {
      void vscode.window.showWarningMessage(t('connections.workspaceMappings.noActiveProfile'));
      return;
    }

    const serverPath = await vscode.window.showInputBox({
      prompt: t('connections.workspaceMappings.prompt.serverPath', { name: active.name }),
      value: '$/',
      ignoreFocusOut: true,
      validateInput: (value) => value.trim().startsWith('$/') ? undefined : t('connections.workspaceMappings.validate.serverPath'),
    });
    if (!serverPath) {
      return;
    }

    const localPath = await vscode.window.showInputBox({
      prompt: t('connections.workspaceMappings.prompt.localPath', { name: active.name }),
      ignoreFocusOut: true,
      validateInput: (value) => {
        const trimmed = value.trim();
        return (trimmed.startsWith('/') || trimmed.startsWith('~') || /^[A-Za-z]:[/\\]/.test(trimmed))
          ? undefined
          : t('connections.workspaceMappings.validate.localPath');
      },
    });
    if (!localPath) {
      return;
    }

    const expandedLocal = expandHome(localPath.trim());
    const trimmedServer = serverPath.trim();
    const existing = active.mappings ?? [];
    const duplicate = existing.find(
      (m) => m.serverPath === trimmedServer || m.localPath.toLowerCase() === expandedLocal.toLowerCase(),
    );
    if (duplicate) {
      void vscode.window.showWarningMessage(
        t('error.localFolderMapped', { localPath: duplicate.localPath, serverPath: duplicate.serverPath }),
      );
      return;
    }

    const updated = [...existing, { serverPath: trimmedServer, localPath: expandedLocal }];
    await this.updateProfileMappings(active.id, updated);

    // Also register the mapping with the TFS workspace immediately
    try {
      await this.client.workspaceMap(trimmedServer, expandedLocal);
      void vscode.window.showInformationMessage(
        t('connections.workspaceMappings.registered', { serverPath: trimmedServer, localPath: expandedLocal }),
      );
    } catch {
      // Mapping is saved on the profile; CLI registration may fail if no workspace exists yet
    }
  }

  private async deleteWorkspaceMapping(mapping?: ConfiguredWorkspaceMapping): Promise<void> {
    if (!mapping) {
      return;
    }
    const active = this.getActiveProfile();
    if (!active) {
      return;
    }

    const confirmed = await vscode.window.showWarningMessage(
      t('connections.workspaceMappings.delete.confirm', { serverPath: mapping.serverPath, localPath: mapping.localPath }),
      { modal: true },
      t('connections.workspaceMappings.delete'),
    );
    if (!confirmed) {
      return;
    }

    const remaining = (active.mappings ?? []).filter(
      (m) => !(m.serverPath === mapping.serverPath && m.localPath === mapping.localPath),
    );
    await this.updateProfileMappings(active.id, remaining);
  }
}

function expandHome(value: string): string {
  if (value.startsWith('~/') || value === '~') {
    const home = process.env.HOME ?? process.env.USERPROFILE;
    if (home) {
      return path.resolve(value === '~' ? home : path.join(home, value.slice(2)));
    }
  }
  return path.resolve(value);
}
