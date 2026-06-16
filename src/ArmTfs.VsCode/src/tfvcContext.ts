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
 * Compute the default local checkout path for a TFVC server path based on the configured
 * `armTfs.localRootDirectory`. Returns undefined when no root directory is configured, so callers
 * can fall back to their previous default. The returned path is only a suggestion: callers should
 * still let the user review/edit it.
 */
export function computeLocalPathForServerPath(serverPath: string): string | undefined {
  const localRoot = getConfiguredLocalRootDirectory();
  if (!localRoot) {
    return undefined;
  }
  const relative = serverPathToRelative(serverPath);
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

  return undefined;
}

export async function findTfvcWorkspaceRoot(anchorPath?: string): Promise<string | undefined> {
  const syncResolved = findTfvcWorkspaceRootSync(anchorPath);
  if (syncResolved) {
    return syncResolved;
  }

  const metadataFiles = await vscode.workspace.findFiles(`**/${WORKSPACE_METADATA_PATH}`, SEARCH_EXCLUDES, 20);
  if (metadataFiles.length === 0) {
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

function translatePlatformSharedPath(targetPath: string): string {
  if (process.platform !== 'darwin' && process.platform !== 'win32') {
    return targetPath;
  }

  const normalized = targetPath.replace(/\\/g, '/');
  const home = getPlatformSharedHomeDirectory();
  if (!home) {
    return targetPath;
  }

  const prefixes = ['//Mac/Home/', '/Mac/Home/'];
  for (const prefix of prefixes) {
    if (normalized.toLowerCase().startsWith(prefix.toLowerCase())) {
      return path.join(home, normalized.slice(prefix.length));
    }
  }

  const withoutDrive = /^[A-Za-z]:\//.test(normalized) ? normalized.slice(3) : normalized;
  const drivePrefix = 'Mac/Home/';
  if (withoutDrive.toLowerCase().startsWith(drivePrefix.toLowerCase())) {
    return path.join(home, withoutDrive.slice(drivePrefix.length));
  }

  return targetPath;
}

function getPlatformSharedHomeDirectory(): string | undefined {
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
