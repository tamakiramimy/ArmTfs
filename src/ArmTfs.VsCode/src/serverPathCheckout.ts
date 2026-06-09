import * as path from 'node:path';
import * as vscode from 'vscode';
import { ArmTfsCliClient } from './armTfsCliClient';
import { t } from './i18n';
import { findTfvcWorkspaceRoot, getCommandCwd } from './tfvcContext';

export async function checkoutServerPathToLocalFolder(
  client: ArmTfsCliClient,
  serverPath: string,
  localPath: string,
  options?: { version?: number },
): Promise<string> {
  const normalizedServerPath = serverPath.trim();
  const normalizedLocalPath = path.resolve(localPath);

  await vscode.workspace.fs.createDirectory(vscode.Uri.file(normalizedLocalPath));

  const workspaceRoot = await findTfvcWorkspaceRoot(normalizedLocalPath);
  const steps: string[] = [];

  if (workspaceRoot) {
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
