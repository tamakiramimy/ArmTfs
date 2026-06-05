import { existsSync, statSync } from 'node:fs';
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
