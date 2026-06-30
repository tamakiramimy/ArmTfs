import { chmodSync, existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as vscode from 'vscode';

type JsonObject = Record<string, unknown>;

export interface ArmTfsStoredConnectionProfile {
  id: string;
  name: string;
  serverUrl: string;
  rootPath: string;
  displayName?: string;
  mappings?: Array<{ serverPath: string; localPath: string }>;
  pat?: string;
}

interface ArmTfsUserConfig extends JsonObject {
  version?: number;
}

const USER_CONFIG_FILE = 'vscode-config.json';

const MIGRATED_SETTING_KEYS = [
  'cli.command',
  'cli.commandArgs',
  'cli.cwd',
  'cli.preferWorkspaceBuild',
  'workspaceRoot',
  'tfsRootDirectory',
  'localRootDirectory',
  'ui.language',
  'autoCheckout.mode',
  'autoCheckout.confirm',
  'branch.scope',
  'history.top',
  'merge.sourcePath',
  'merge.targetPath',
  'merge.candidateTop',
  'merge.candidateScan',
  'workspaceMappings',
  'serverExplorer.rootPath',
] as const;

export function getArmTfsUserConfigPath(): string {
  return path.join(getArmTfsUserConfigDirectory(), USER_CONFIG_FILE);
}

export function getConfigValue<T>(key: string, defaultValue: T): T {
  const normalizedKey = normalizeConfigKey(key);
  const userValue = getByPath(readUserConfig(), normalizedKey);
  if (userValue !== undefined) {
    return userValue as T;
  }
  return vscode.workspace.getConfiguration('armTfs').get<T>(normalizedKey, defaultValue);
}

export function setConfigValue(key: string, value: unknown): void {
  const normalizedKey = normalizeConfigKey(key);
  const config = readUserConfig();
  setByPath(config, normalizedKey, value);
  writeUserConfig(config);
}

export function migrateArmTfsSettingsToUserConfig(): void {
  const config = readUserConfig();
  const vscodeConfig = vscode.workspace.getConfiguration('armTfs');
  let changed = false;

  for (const key of MIGRATED_SETTING_KEYS) {
    if (getByPath(config, key) !== undefined) {
      continue;
    }
    const inspected = vscodeConfig.inspect(key);
    const value = inspected?.workspaceFolderValue
      ?? inspected?.workspaceValue
      ?? inspected?.globalValue;
    if (value !== undefined) {
      setByPath(config, key, value);
      changed = true;
    }
  }

  if (changed) {
    writeUserConfig(config);
  }
}

export function getStoredConnectionProfiles(): ArmTfsStoredConnectionProfile[] {
  const profiles = getByPath(readUserConfig(), 'connections.profiles');
  if (!Array.isArray(profiles)) {
    return [];
  }
  return profiles.filter(isStoredConnectionProfile);
}

export function setStoredConnectionProfiles(profiles: ArmTfsStoredConnectionProfile[]): void {
  setConfigValue('connections.profiles', profiles);
}

export function getActiveConnectionId(): string | undefined {
  const value = getByPath(readUserConfig(), 'connections.activeProfileId');
  return typeof value === 'string' && value.trim() ? value : undefined;
}

export function setActiveConnectionId(profileId: string | undefined): void {
  setConfigValue('connections.activeProfileId', profileId);
}

function getArmTfsUserConfigDirectory(): string {
  if (process.platform === 'win32') {
    const appData = process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming');
    return path.join(appData, 'arm-tfs');
  }
  if (process.platform === 'darwin') {
    return path.join(os.homedir(), 'Library', 'Application Support', 'arm-tfs');
  }
  const xdgConfig = process.env.XDG_CONFIG_HOME || path.join(os.homedir(), '.config');
  return path.join(xdgConfig, 'arm-tfs');
}

function readUserConfig(): ArmTfsUserConfig {
  const filePath = getArmTfsUserConfigPath();
  if (!existsSync(filePath)) {
    return { version: 1 };
  }
  try {
    const parsed = JSON.parse(readFileSync(filePath, 'utf8')) as unknown;
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
      ? { version: 1, ...(parsed as JsonObject) }
      : { version: 1 };
  } catch {
    return { version: 1 };
  }
}

function writeUserConfig(config: ArmTfsUserConfig): void {
  const filePath = getArmTfsUserConfigPath();
  mkdirSync(path.dirname(filePath), { recursive: true });
  const next = JSON.stringify({ version: 1, ...config }, null, 2);
  const tempPath = `${filePath}.${process.pid}.tmp`;
  writeFileSync(tempPath, `${next}\n`, { mode: 0o600 });
  renameSync(tempPath, filePath);
  try {
    chmodSync(filePath, 0o600);
  } catch {
    // Some Windows file systems ignore POSIX modes.
  }
}

function normalizeConfigKey(key: string): string {
  return key.startsWith('armTfs.') ? key.slice('armTfs.'.length) : key;
}

function getByPath(source: JsonObject, key: string): unknown {
  let current: unknown = source;
  for (const part of key.split('.')) {
    if (!current || typeof current !== 'object' || Array.isArray(current)) {
      return undefined;
    }
    current = (current as JsonObject)[part];
  }
  return current;
}

function setByPath(target: JsonObject, key: string, value: unknown): void {
  const parts = key.split('.');
  let current = target;
  for (const part of parts.slice(0, -1)) {
    const existing = current[part];
    if (!existing || typeof existing !== 'object' || Array.isArray(existing)) {
      current[part] = {};
    }
    current = current[part] as JsonObject;
  }
  current[parts[parts.length - 1]] = value;
}

function isStoredConnectionProfile(value: unknown): value is ArmTfsStoredConnectionProfile {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return false;
  }
  const candidate = value as Record<string, unknown>;
  return typeof candidate.id === 'string'
    && typeof candidate.name === 'string'
    && typeof candidate.serverUrl === 'string'
    && typeof candidate.rootPath === 'string';
}
