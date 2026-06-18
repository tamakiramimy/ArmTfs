import * as path from 'node:path';
import { existsSync } from 'node:fs';
import * as vscode from 'vscode';
import { ArmTfsCliClient } from './armTfsCliClient';
import { t } from './i18n';
import { findTfvcWorkspaceRoot, getCommandCwd } from './tfvcContext';

const WORKSPACE_METADATA_PATH = path.join('.tf', 'workspace.json');

export async function checkoutServerPathToLocalFolder(
  client: ArmTfsCliClient,
  serverPath: string,
  localPath: string,
  options?: { version?: number },
): Promise<string> {
  const normalizedServerPath = serverPath.trim();
  const normalizedLocalPath = path.resolve(localPath);

  await vscode.workspace.fs.createDirectory(vscode.Uri.file(normalizedLocalPath));

  // Determine whether the target localPath already has its own workspace metadata.
  // We check ONLY the target directory itself — never a parent or sibling directory.
  // This prevents the common mistake of "polluting" an unrelated project's workspace.json
  // by adding mappings to it every time a new branch is checked out nearby.
  const ownWorkspaceMetadata = path.join(normalizedLocalPath, WORKSPACE_METADATA_PATH);
  const hasOwnWorkspace = existsSync(ownWorkspaceMetadata);

  const steps: string[] = [];

  if (hasOwnWorkspace) {
    // The target directory already has a workspace — reuse it.
    // Only add a mapping if the server path isn't already mapped.
    const workspaceRoot = normalizedLocalPath;
    const commandCwd = getCommandCwd(workspaceRoot, normalizedLocalPath);
    const status = await client.status(workspaceRoot, false, { cwdOverride: workspaceRoot });
    const existingMapping = status.workspace.mappings.find((mapping) => sameLocalPath(mapping.localPath, normalizedLocalPath));

    if (existingMapping && existingMapping.serverPath !== normalizedServerPath) {
      throw new Error(t('error.localFolderMapped', {
        localPath: normalizedLocalPath,
        serverPath: existingMapping.serverPath,
      }));
    }

    const hasRequestedMapping = status.workspace.mappings.some((mapping) =>
      sameLocalPath(mapping.localPath, normalizedLocalPath) && mapping.serverPath === normalizedServerPath,
    );

    if (!hasRequestedMapping) {
      steps.push(await client.workspaceMap(normalizedServerPath, normalizedLocalPath, { cwdOverride: workspaceRoot }));
    }

    steps.push(await client.get(normalizedLocalPath, { version: options?.version }, { cwdOverride: commandCwd }));
    return steps.join('\n\n');
  }

  // No workspace in the target directory — create a fresh one scoped exactly to this path.
  // Do NOT search parent directories; every branch/project gets its own isolated workspace.
  const workspaceName = buildWorkspaceName(normalizedServerPath, normalizedLocalPath);
  steps.push(await client.workspaceNew(workspaceName, normalizedServerPath, normalizedLocalPath, normalizedLocalPath, { cwdOverride: normalizedLocalPath }));
  steps.push(await client.get(normalizedLocalPath, { version: options?.version }, { cwdOverride: normalizedLocalPath }));
  return steps.join('\n\n');
}

function buildWorkspaceName(serverPath: string, localPath: string): string {
  const leaf = serverPath.split('/').filter(Boolean).pop() ?? path.basename(localPath) ?? 'workspace';
  const normalizedLeaf = leaf.replace(/[^A-Za-z0-9._-]+/g, '-').replace(/^-+|-+$/g, '') || 'workspace';
  return `arm-tfs-${normalizedLeaf}`;
}

function sameLocalPath(leftPath: string, rightPath: string): boolean {
  return normalizeLocalPath(leftPath) === normalizeLocalPath(rightPath);
}

function normalizeLocalPath(targetPath: string): string {
  return path.resolve(targetPath).toLowerCase();
}
