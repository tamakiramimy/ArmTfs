import { createHash } from 'node:crypto';
import { promises as fs } from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as vscode from 'vscode';
import type { ArmTfsCliClient } from './armTfsCliClient';
import type { ItemContentResponse } from './contracts';
import { getUiLanguage } from './i18n';

interface ServerVersionSpec {
  serverPath: string;
  version?: number;
  label?: string;
}

interface MaterializedServerVersion {
  uri: vscode.Uri;
  filePath: string;
  isBinary: boolean;
  displayLabel: string;
}

export async function materializeServerVersion(
  client: ArmTfsCliClient,
  spec: ServerVersionSpec,
): Promise<MaterializedServerVersion> {
  const response = await client.itemContent(spec.serverPath, spec.version);
  const filePath = buildTempPath(spec.serverPath, spec.version);
  await fs.mkdir(path.dirname(filePath), { recursive: true });
  await fs.writeFile(filePath, decodeContent(response));
  return {
    uri: vscode.Uri.file(filePath),
    filePath,
    isBinary: response.item.isBinary,
    displayLabel: spec.label ?? buildVersionLabel(spec.serverPath, spec.version),
  };
}

export async function openServerVersionDiff(
  client: ArmTfsCliClient,
  left: ServerVersionSpec,
  right: ServerVersionSpec,
  title?: string,
): Promise<void> {
  const [leftFile, rightFile] = await Promise.all([
    materializeServerVersion(client, left),
    materializeServerVersion(client, right),
  ]);

  if (leftFile.isBinary || rightFile.isBinary) {
    await openBinaryNotice(title ?? `${leftFile.displayLabel} ↔ ${rightFile.displayLabel}`, leftFile, rightFile);
    return;
  }

  await vscode.commands.executeCommand(
    'vscode.diff',
    leftFile.uri,
    rightFile.uri,
    title ?? `${leftFile.displayLabel} ↔ ${rightFile.displayLabel}`,
    { preview: false },
  );
}

export async function openServerVersion(
  client: ArmTfsCliClient,
  spec: ServerVersionSpec,
  options?: { title?: string },
): Promise<void> {
  const file = await materializeServerVersion(client, spec);
  await vscode.commands.executeCommand(
    'vscode.open',
    file.uri,
    {
      preview: false,
      viewColumn: vscode.ViewColumn.Active,
    },
  );
  if (options?.title) {
    void vscode.window.showInformationMessage(options.title);
  }
}

export async function openServerVersionDiffFromEmpty(
  client: ArmTfsCliClient,
  right: ServerVersionSpec,
  title?: string,
): Promise<void> {
  const rightFile = await materializeServerVersion(client, right);
  const leftFile = await materializeEmptyVersion(right.serverPath, right.version);

  if (rightFile.isBinary) {
    await openBinaryNotice(
      title ?? `${leftFile.displayLabel} ↔ ${rightFile.displayLabel}`,
      leftFile,
      rightFile,
    );
    return;
  }

  await vscode.commands.executeCommand(
    'vscode.diff',
    leftFile.uri,
    rightFile.uri,
    title ?? `${leftFile.displayLabel} ↔ ${rightFile.displayLabel}`,
    { preview: false },
  );
}

export async function openLocalWorkingDiff(
  client: ArmTfsCliClient,
  localPath: string,
  serverPath: string,
  options?: { version?: number; title?: string },
): Promise<void> {
  const leftFile = await materializeServerVersion(client, {
    serverPath,
    version: options?.version,
    label: `${path.basename(localPath)} (${options?.version ? `cs${options.version}` : 'server'})`,
  });

  if (leftFile.isBinary) {
    const document = await vscode.workspace.openTextDocument({
      language: 'text',
      content: binaryNoticeText(leftFile.displayLabel, localPath, leftFile.filePath),
    });
    await vscode.window.showTextDocument(document, { preview: false });
    return;
  }

  await vscode.commands.executeCommand(
    'vscode.diff',
    leftFile.uri,
    vscode.Uri.file(localPath),
    options?.title ?? `${leftFile.displayLabel} ↔ ${path.basename(localPath)} (working tree)`,
    { preview: false },
  );
}

function buildTempPath(serverPath: string, version?: number): string {
  const ext = path.posix.extname(serverPath);
  const base = sanitize(path.posix.basename(serverPath, ext)) || 'file';
  const hash = createHash('sha1').update(`${serverPath}@@${version ?? 'latest'}`).digest('hex').slice(0, 12);
  const versionLabel = version !== undefined ? `cs${version}` : 'latest';
  return path.join(os.tmpdir(), 'arm-tfs-vscode', 'versions', hash, `${base}__${versionLabel}${ext}`);
}

async function materializeEmptyVersion(
  serverPath: string,
  version?: number,
): Promise<MaterializedServerVersion> {
  const filePath = buildEmptyTempPath(serverPath, version);
  await fs.mkdir(path.dirname(filePath), { recursive: true });
  await fs.writeFile(filePath, '');
  return {
    uri: vscode.Uri.file(filePath),
    filePath,
    isBinary: false,
    displayLabel: getUiLanguage() === 'zh-CN'
      ? `${path.posix.basename(serverPath)} (空白基线)`
      : `${path.posix.basename(serverPath)} (empty baseline)`,
  };
}

function buildEmptyTempPath(serverPath: string, version?: number): string {
  const ext = path.posix.extname(serverPath);
  const base = sanitize(path.posix.basename(serverPath, ext)) || 'file';
  const hash = createHash('sha1').update(`${serverPath}@@empty@@${version ?? 'latest'}`).digest('hex').slice(0, 12);
  const versionLabel = version !== undefined ? `cs${version}` : 'latest';
  return path.join(os.tmpdir(), 'arm-tfs-vscode', 'versions', hash, `${base}__empty_before_${versionLabel}${ext}`);
}

function sanitize(value: string): string {
  return value.replace(/[^A-Za-z0-9._-]+/g, '-').replace(/^-+|-+$/g, '');
}

function decodeContent(response: ItemContentResponse): Buffer {
  return Buffer.from(response.item.contentBase64, 'base64');
}

function buildVersionLabel(serverPath: string, version?: number): string {
  return `${path.posix.basename(serverPath)} (${version !== undefined ? `cs${version}` : 'latest'})`;
}

async function openBinaryNotice(
  title: string,
  leftFile: MaterializedServerVersion,
  rightFile: MaterializedServerVersion,
): Promise<void> {
  const document = await vscode.workspace.openTextDocument({
    language: 'text',
    content: binaryNoticeText(title, leftFile.filePath, rightFile.filePath),
  });
  await vscode.window.showTextDocument(document, { preview: false });
}

function binaryNoticeText(title: string, leftPath: string, rightPath: string): string {
  const zh = getUiLanguage() === 'zh-CN';
  return [
    title,
    '',
    zh ? '当前比较的是二进制文件，已生成临时版本文件。' : 'This is a binary comparison. Temporary version files were generated.',
    `${zh ? '左侧' : 'Left'}: ${leftPath}`,
    `${zh ? '右侧' : 'Right'}: ${rightPath}`,
  ].join('\n');
}
