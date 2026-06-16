import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';

const WORKSPACE_METADATA_PATH = path.join('.tf', 'workspace.json');
const SEARCH_EXCLUDES = '**/{node_modules,.git,bin,obj,out}/**';

export function getConfiguredWorkspaceRoot(): string | undefined {
  const configured = vscode.workspace.getConfiguration('armTfs').get<string>('workspaceRoot')?.trim();
  return configured ? path.resolve(configured) : undefined;
}

/**
 * The local root directory under which TFVC server paths are checked out, configured via
 * `armTfs.localRootDirectory`. When set, branch/folder checkouts can auto-compute their local
 * destination so the user no longer has to pick a folder manually every time.
 */
export function getConfiguredLocalRootDirectory(): string | undefined {
  const config = vscode.workspace.getConfiguration('armTfs');
  const configured = config.get<string>('tfsRootDirectory')?.trim()
    || config.get<string>('localRootDirectory')?.trim();
  return configured ? path.resolve(configured) : undefined;
}

export interface ConfiguredWorkspaceMapping {
  serverPath: string;
  localPath: string;
}

export type WorkspaceMappingsProvider = () => ConfiguredWorkspaceMapping[];

let mappingsProvider: WorkspaceMappingsProvider | undefined;

/**
 * Register a provider that supplies the workspace mappings to use for path resolution.
 * Typically the ConnectionsController registers itself so the active profile's mappings
 * are used. Falls back to the legacy global `armTfs.workspaceMappings` setting.
 */
export function setWorkspaceMappingsProvider(provider: WorkspaceMappingsProvider | undefined): void {
  mappingsProvider = provider;
}

/**
 * Return workspace mappings from the registered provider (e.g. the active TFS profile).
 * Falls back to the legacy global `armTfs.workspaceMappings` setting for backward compatibility.
 */
export function getConfiguredWorkspaceMappings(): ConfiguredWorkspaceMapping[] {
  const fromProvider = mappingsProvider?.();
  if (fromProvider && fromProvider.length) {
    return fromProvider
      .filter((entry) => entry.serverPath?.startsWith('$/') && entry.localPath?.trim())
      .map((entry) => ({
        serverPath: entry.serverPath.trim(),
        localPath: path.resolve(entry.localPath.trim()),
      }));
  }

  const legacy = vscode.workspace.getConfiguration('armTfs').get<Array<{ serverPath?: string; localPath?: string }>>('workspaceMappings', []);
  return legacy
    .filter((entry) => entry.serverPath?.startsWith('$/') && entry.localPath?.trim())
    .map((entry) => ({
      serverPath: entry.serverPath!.trim(),
      localPath: path.resolve(entry.localPath!.trim()),
    }));
}

/**
 * Given a local file/directory path, find the most-specific configured workspace mapping
 * that covers it. Returns undefined if no configured mapping applies.
 */
export function findConfiguredMappingForLocalPath(localPath: string): ConfiguredWorkspaceMapping | undefined {
  const normalized = normalizeForCompare(localPath);
  const mappings = getConfiguredWorkspaceMappings();
  let best: ConfiguredWorkspaceMapping | undefined;
  for (const mapping of mappings) {
    const mappingNorm = normalizeForCompare(mapping.localPath);
    if (normalized === mappingNorm || normalized.startsWith(`${mappingNorm}${path.sep}`)) {
      if (!best || mapping.localPath.length > best.localPath.length) {
        best = mapping;
      }
    }
  }
  return best;
}

/**
 * Convert a TFVC server path (e.g. `$/Project/Branch/sub`) into a path segment relative to the
 * server root, using the host platform's path separator.
 */
export function serverPathToRelative(serverPath: string): string {
  const trimmed = serverPath.trim().replace(/^\$\//, '').replace(/^\/+/, '').replace(/\/+$/, '');
  if (!trimmed) {
    return '';
  }
  return trimmed.split('/').filter(Boolean).join(path.sep);
}

/**
 * Compute the default local path for a TFVC server path. Priority:
 *  1. Configured workspace mappings (active TFS profile or global setting). The most-specific
 *     mapping that is an ancestor of the server path wins.
 *  2. Fallback to `armTfs.tfsRootDirectory` (legacy single-root setting).
 * Returns undefined when neither source has a usable mapping. The result is a suggestion —
 * callers should still let the user review/edit it.
 */
export function computeLocalPathForServerPath(serverPath: string): string | undefined {
  const trimmedServer = serverPath.trim();
  const normalizedServer = trimmedServer.replace(/\/+$/, '');

  // Strategy 1: configured workspace mappings — pick the deepest one that is an ancestor
  // of the requested server path.
  const mappings = getConfiguredWorkspaceMappings();
  let best: { mapping: ConfiguredWorkspaceMapping; matchLen: number } | undefined;
  for (const mapping of mappings) {
    const mappingServerNorm = mapping.serverPath.replace(/\/+$/, '');
    if (normalizedServer === mappingServerNorm
      || normalizedServer.startsWith(`${mappingServerNorm}/`)
      || mappingServerNorm === '$') {
      const len = mappingServerNorm.length;
      if (!best || len > best.matchLen) {
        best = { mapping, matchLen: len };
      }
    }
  }
  if (best) {
    const mappingServerNorm = best.mapping.serverPath.replace(/\/+$/, '');
    let suffix = '';
    if (mappingServerNorm === '$' || mappingServerNorm === '$/') {
      suffix = normalizedServer.replace(/^\$\/?/, '');
    } else if (normalizedServer.startsWith(`${mappingServerNorm}/`)) {
      suffix = normalizedServer.slice(mappingServerNorm.length + 1);
    }
    return suffix
      ? path.join(best.mapping.localPath, ...suffix.split('/').filter(Boolean))
      : best.mapping.localPath;
  }

  // Strategy 2: legacy tfsRootDirectory.
  const localRoot = getConfiguredLocalRootDirectory();
  if (!localRoot) {
    return undefined;
  }
  const relative = serverPathToRelative(trimmedServer);
  return relative ? path.join(localRoot, relative) : localRoot;
}

export function getActiveFilePath(): string | undefined {
  const activeUri = vscode.window.activeTextEditor?.document.uri;
  return activeUri?.scheme === 'file' ? activeUri.fsPath : undefined;
}

export function findTfvcWorkspaceRootSync(anchorPath?: string): string | undefined {
  const configuredRoot = getConfiguredWorkspaceRoot();
  if (configuredRoot && hasWorkspaceMetadata(configuredRoot)) {
    return configuredRoot;
  }

  const candidates = [anchorPath, getActiveFilePath(), ...getWorkspaceFolderPaths()];
  for (const candidate of candidates) {
    const resolved = findWorkspaceRootUpward(candidate);
    if (resolved) {
      return resolved;
    }
  }

  // Fallback: use the root of a configured workspace mapping that covers the anchor path
  if (anchorPath) {
    const configuredMapping = findConfiguredMappingForLocalPath(anchorPath);
    if (configuredMapping) {
      return configuredMapping.localPath;
    }
  }

  return undefined;
}

export async function findTfvcWorkspaceRoot(anchorPath?: string): Promise<string | undefined> {
  const syncResolved = findTfvcWorkspaceRootSync(anchorPath);
  if (syncResolved) {
    return syncResolved;
  }

  const metadataFiles = await vscode.workspace.findFiles(`**/${WORKSPACE_METADATA_PATH}`, SEARCH_EXCLUDES, 20);
  if (metadataFiles.length === 0) {
    // Final fallback: configured mappings (no .tf/workspace.json found anywhere)
    if (anchorPath) {
      const configuredMapping = findConfiguredMappingForLocalPath(anchorPath);
      if (configuredMapping) {
        return configuredMapping.localPath;
      }
    }
    return undefined;
  }

  if (anchorPath) {
    const normalizedAnchor = path.resolve(anchorPath);
    const preferred = metadataFiles
      .map((uri) => path.dirname(path.dirname(uri.fsPath)))
      .sort((left, right) => distanceScore(left, normalizedAnchor) - distanceScore(right, normalizedAnchor))[0];
    if (preferred) {
      return preferred;
    }
  }

  return path.dirname(path.dirname(metadataFiles[0].fsPath));
}

export function getCommandCwd(workspaceRoot: string, targetPath?: string): string {
  if (!targetPath) {
    return workspaceRoot;
  }

  const normalizedTarget = path.resolve(targetPath);
  if (!isPathWithin(workspaceRoot, normalizedTarget)) {
    return workspaceRoot;
  }

  try {
    return statSync(normalizedTarget).isDirectory() ? normalizedTarget : path.dirname(normalizedTarget);
  } catch {
    return path.extname(normalizedTarget) ? path.dirname(normalizedTarget) : workspaceRoot;
  }
}

export function isPathWithin(parentPath: string, candidatePath: string): boolean {
  const normalizedParent = normalizeForCompare(parentPath);
  const normalizedCandidate = normalizeForCompare(candidatePath);
  return normalizedCandidate === normalizedParent || normalizedCandidate.startsWith(`${normalizedParent}${path.sep}`);
}

export function getWorkspaceFolderPaths(): string[] {
  return vscode.workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [];
}

export interface DiscoveredTfvcMapping {
  workspaceRoot: string;
  serverPath: string;
  localPath: string;
}

export function discoverTfvcMappingForPath(localPath?: string): DiscoveredTfvcMapping | undefined {
  if (!localPath) {
    return undefined;
  }
  const normalizedLocalPath = normalizeForCompare(localPath);

  // Strategy 1 (most reliable): walk the full ancestor chain upward from the requested path.
  // A TFVC workspace's metadata (.tf/workspace.json) always lives at or above the opened
  // file/folder, so we must check every ancestor directory regardless of depth. The previous
  // implementation only looked a few levels up and then scanned downward, which frequently
  // missed workspaces whose root was further up the tree (e.g. a single mapping at the drive
  // root). This caused the "cannot identify TFS path" failures.
  const upwardMatch = findMappingUpward(localPath, normalizedLocalPath);
  if (upwardMatch) {
    return upwardMatch;
  }

  // Strategy 2 (fallback): scan nearby directories (siblings/cousins) for metadata whose
  // mappings happen to cover the requested path. Useful when the path is not itself inside the
  // mapped tree but a related workspace lives next to it.
  const searchRoots = buildNearbySearchRoots(localPath);
  const matches: DiscoveredTfvcMapping[] = [];
  for (const searchRoot of searchRoots) {
    for (const metadataPath of findMetadataFiles(searchRoot, 4)) {
      matches.push(...readMappingsCovering(metadataPath, normalizedLocalPath));
    }
    if (matches.length) {
      return matches.sort((left, right) => right.localPath.length - left.localPath.length)[0];
    }
  }

  // Strategy 3 (configured mappings): use mappings from armTfs.workspaceMappings settings.
  // These are defined by the user and don't require .tf/workspace.json to be present at the
  // target path. We compute the server path from the configured mapping, using the same
  // relative-offset logic as the workspace metadata parser.
  const configuredMapping = findConfiguredMappingForLocalPath(localPath);
  if (configuredMapping) {
    const mappingNorm = normalizeForCompare(configuredMapping.localPath);
    const localNorm = normalizeForCompare(localPath);
    let serverPath = configuredMapping.serverPath;
    if (localNorm !== mappingNorm) {
      const relative = path.relative(configuredMapping.localPath, path.resolve(localPath));
      const serverRelative = relative.split(path.sep).join('/');
      serverPath = `${configuredMapping.serverPath.replace(/\/+$/, '')}/${serverRelative}`;
    }
    return {
      workspaceRoot: configuredMapping.localPath,
      serverPath,
      localPath: configuredMapping.localPath,
    };
  }

  return undefined;
}

/**
 * Walk upward from the given path, checking every ancestor directory for a TFVC workspace
 * metadata file whose mappings cover the path. Returns the most specific (longest) mapping.
 */
function findMappingUpward(localPath: string, normalizedLocalPath: string): DiscoveredTfvcMapping | undefined {
  let current = path.resolve(localPath);
  try {
    if (!statSync(current).isDirectory()) {
      current = path.dirname(current);
    }
  } catch {
    current = path.dirname(current);
  }

  while (true) {
    const metadataPath = path.join(current, WORKSPACE_METADATA_PATH);
    if (existsSync(metadataPath)) {
      const matches = readMappingsCovering(metadataPath, normalizedLocalPath);
      if (matches.length) {
        return matches.sort((left, right) => right.localPath.length - left.localPath.length)[0];
      }
    }

    const parent = path.dirname(current);
    if (parent === current) {
      return undefined;
    }
    current = parent;
  }
}

/**
 * Read the workspace metadata at the given path and return the mappings that cover (are an
 * ancestor of, or equal to) the requested local path.
 */
function readMappingsCovering(metadataPath: string, normalizedLocalPath: string): DiscoveredTfvcMapping[] {
  const matches: DiscoveredTfvcMapping[] = [];
  try {
    const metadata = JSON.parse(readFileSync(metadataPath, 'utf8')) as {
      Mappings?: Array<Record<string, string | undefined>>;
      mappings?: Array<Record<string, string | undefined>>;
    };
    const mappings = metadata.Mappings ?? metadata.mappings ?? [];
    for (const mapping of mappings) {
      const serverPath = mapping.ServerPath ?? mapping.serverPath;
      const mappedLocalPath = mapping.LocalPath ?? mapping.localPath;
      if (!serverPath || !mappedLocalPath) {
        continue;
      }
      const normalizedMapping = normalizeForCompare(mappedLocalPath);
      if (
        normalizedLocalPath === normalizedMapping
        || normalizedLocalPath.startsWith(`${normalizedMapping}${path.sep}`)
      ) {
        matches.push({
          workspaceRoot: path.dirname(path.dirname(metadataPath)),
          serverPath,
          localPath: translatePlatformSharedPath(mappedLocalPath),
        });
      }
    }
  } catch {
    // Ignore unrelated or incomplete metadata while scanning.
  }
  return matches;
}

function hasWorkspaceMetadata(candidateRoot: string): boolean {
  return existsSync(path.join(candidateRoot, WORKSPACE_METADATA_PATH));
}

function findWorkspaceRootUpward(startPath?: string): string | undefined {
  if (!startPath) {
    return undefined;
  }

  let currentPath = path.resolve(startPath);
  try {
    if (!statSync(currentPath).isDirectory()) {
      currentPath = path.dirname(currentPath);
    }
  } catch {
    currentPath = path.dirname(currentPath);
  }

  while (true) {
    if (hasWorkspaceMetadata(currentPath)) {
      return currentPath;
    }

    const parent = path.dirname(currentPath);
    if (parent === currentPath) {
      return undefined;
    }
    currentPath = parent;
  }
}

function normalizeForCompare(targetPath: string): string {
  return path.resolve(translatePlatformSharedPath(targetPath)).toLowerCase();
}

/**
 * Translate a workspace mapping's local path between macOS and Windows when the user runs the
 * VM (Parallels Desktop) and the same TFS workspace is opened from both sides.
 *
 * Equivalence (Parallels shared volume):
 *   macOS:    /Users/<user>/<rest>
 *   Windows:  C:\Mac\Home\<user>\<rest>     (drive-letter form)
 *   Windows:  \\Mac\Home\<user>\<rest>      (UNC form)
 *
 * The function takes a path written by either side and returns the equivalent path on the
 * current platform, so that mapping comparisons (string-prefix checks) work correctly.
 *
 * Important: it must not depend on process.env.HOME — that includes the username and would
 * collide with the username already present in the mapping. We use literal "/Users" and
 * "C:\\Mac\\Home" as the platform anchors instead.
 */
function translatePlatformSharedPath(targetPath: string): string {
  if (process.platform !== 'darwin' && process.platform !== 'win32') {
    return targetPath;
  }

  const normalized = targetPath.replace(/\\/g, '/');

  // Win UNC or drive-stripped form: //Mac/Home/<user>/... or /Mac/Home/<user>/...
  for (const prefix of ['//Mac/Home/', '/Mac/Home/']) {
    if (normalized.toLowerCase().startsWith(prefix.toLowerCase())) {
      const rest = normalized.slice(prefix.length);
      return process.platform === 'darwin'
        ? path.join('/Users', rest)
        : path.join('C:\\Mac\\Home', ...rest.split('/'));
    }
  }

  // Win drive-letter form: C:/Mac/Home/<user>/...  (any drive letter)
  const driveMatch = normalized.match(/^[A-Za-z]:\/(.*)$/);
  if (driveMatch) {
    const withoutDrive = driveMatch[1];
    if (withoutDrive.toLowerCase().startsWith('mac/home/')) {
      const rest = withoutDrive.slice('mac/home/'.length);
      return process.platform === 'darwin'
        ? path.join('/Users', rest)
        : path.join('C:\\Mac\\Home', ...rest.split('/'));
    }
  }

  // macOS path written from the macOS side, opened on Windows: /Users/<user>/... → C:\Mac\Home\<user>\...
  if (process.platform === 'win32' && normalized.toLowerCase().startsWith('/users/')) {
    const rest = normalized.slice('/Users/'.length);
    return path.join('C:\\Mac\\Home', ...rest.split('/'));
  }

  return targetPath;
}

function getPlatformSharedHomeDirectory(): string | undefined {
  // Kept for backward compatibility with any external import. Internally
  // translatePlatformSharedPath no longer relies on it.
  if (process.platform === 'darwin') {
    return process.env.HOME;
  }
  if (process.platform === 'win32' && existsSync('C:\\Mac\\Home')) {
    return 'C:\\Mac\\Home';
  }
  return undefined;
}

function distanceScore(candidateRoot: string, anchorPath: string): number {
  const normalizedRoot = normalizeForCompare(candidateRoot);
  const normalizedAnchor = normalizeForCompare(anchorPath);
  if (normalizedAnchor.startsWith(normalizedRoot)) {
    return normalizedAnchor.length - normalizedRoot.length;
  }
  return Math.abs(normalizedAnchor.length - normalizedRoot.length) + 100000;
}

function buildNearbySearchRoots(localPath: string): string[] {
  const roots: string[] = [];
  let current = path.resolve(localPath);
  for (let depth = 0; depth < 3; depth += 1) {
    const parent = path.dirname(current);
    if (parent === current) {
      break;
    }
    roots.push(parent);
    current = parent;
  }
  return [...new Set(roots)];
}

function findMetadataFiles(root: string, maxDepth: number): string[] {
  const results: string[] = [];
  const visit = (directory: string, depth: number) => {
    if (depth > maxDepth) {
      return;
    }
    const directMetadata = path.join(directory, WORKSPACE_METADATA_PATH);
    if (existsSync(directMetadata)) {
      results.push(directMetadata);
    }
    if (depth === maxDepth) {
      return;
    }
    let entries;
    try {
      entries = readdirSync(directory, { withFileTypes: true });
    } catch {
      return;
    }
    for (const entry of entries) {
      if (!entry.isDirectory() || ['node_modules', '.git', 'bin', 'obj', 'out'].includes(entry.name)) {
        continue;
      }
      visit(path.join(directory, entry.name), depth + 1);
    }
  };
  visit(root, 0);
  return results;
}
