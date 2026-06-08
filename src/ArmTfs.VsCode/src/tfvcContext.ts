import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';

const WORKSPACE_METADATA_PATH = path.join('.tf', 'workspace.json');
const SEARCH_EXCLUDES = '**/{node_modules,.git,bin,obj,out}/**';

export function getConfiguredWorkspaceRoot(): string | undefined {
  const configured = vscode.workspace.getConfiguration('armTfs').get<string>('workspaceRoot')?.trim();
  return configured ? path.resolve(configured) : undefined;
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
  const searchRoots = buildNearbySearchRoots(localPath);
  const matches: DiscoveredTfvcMapping[] = [];

  for (const searchRoot of searchRoots) {
    for (const metadataPath of findMetadataFiles(searchRoot, 4)) {
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
              localPath: mappedLocalPath,
            });
          }
        }
      } catch {
        // Ignore unrelated or incomplete metadata while scanning nearby folders.
      }
    }
    if (matches.length) {
      return matches.sort((left, right) => right.localPath.length - left.localPath.length)[0];
    }
  }
  return undefined;
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
  return path.resolve(targetPath).toLowerCase();
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
