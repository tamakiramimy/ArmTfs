import { execFile } from 'node:child_process';
import { existsSync } from 'node:fs';
import * as path from 'node:path';
import { promisify } from 'node:util';
import * as vscode from 'vscode';
import type {
  BranchListResponse,
  BranchShowResponse,
  ChangesetShowResponse,
  DiffResponse,
  HistoryResponse,
  LabelListResponse,
  LabelShowResponse,
  MergeBaseResponse,
  MergeCandidateResponse,
  StatusResponse,
} from './contracts';

const execFileAsync = promisify(execFile);

export interface ArmTfsInvocation {
  command: string;
  args: string[];
  cwd?: string;
}

export class ArmTfsCliError extends Error {
  constructor(
    message: string,
    public readonly invocation: ArmTfsInvocation,
    public readonly stdout: string,
    public readonly stderr: string,
  ) {
    super(message);
  }
}

export class ArmTfsCliClient {
  constructor(private readonly output: vscode.OutputChannel) {}

  async describeResolvedInvocation(): Promise<string> {
    const invocation = await this.resolveInvocation([]);
    return this.formatInvocation(invocation);
  }

  status(targetPath = '.', all = true): Promise<StatusResponse> {
    const args = ['status', targetPath, '--format', 'json'];
    if (all) {
      args.splice(2, 0, '--all');
    }

    return this.executeJson<StatusResponse>(args);
  }

  history(targetPath = '.', top = 20, author?: string): Promise<HistoryResponse> {
    const args = ['history', targetPath, '--top', String(top), '--format', 'json'];
    if (author) {
      args.splice(4, 0, '--author', author);
    }

    return this.executeJson<HistoryResponse>(args);
  }

  diff(targetPath: string, options?: { useBase?: boolean; changesetId?: number; ignoreWhitespace?: boolean }): Promise<DiffResponse> {
    const args = ['diff', targetPath, '--format', 'json'];
    if (options?.useBase) {
      args.splice(2, 0, '--base');
    }
    if (options?.changesetId !== undefined) {
      args.splice(2, 0, '--version', String(options.changesetId));
    }
    if (options?.ignoreWhitespace) {
      args.splice(2, 0, '--ignore-whitespace');
    }

    return this.executeJson<DiffResponse>(args);
  }

  branchList(scope = '$/'): Promise<BranchListResponse> {
    return this.executeJson<BranchListResponse>(['branch', 'list', scope, '--format', 'json']);
  }

  branchShow(branchPath: string): Promise<BranchShowResponse> {
    return this.executeJson<BranchShowResponse>(['branch', 'show', branchPath, '--format', 'json']);
  }

  changesetShow(changesetId: number): Promise<ChangesetShowResponse> {
    return this.executeJson<ChangesetShowResponse>(['changeset', 'show', String(changesetId), '--format', 'json']);
  }

  labelList(top = 20): Promise<LabelListResponse> {
    return this.executeJson<LabelListResponse>(['label', 'list', '--top', String(top), '--format', 'json']);
  }

  labelShow(labelId: string, maxItems?: number): Promise<LabelShowResponse> {
    const args = ['label', 'show', labelId, '--format', 'json'];
    if (maxItems !== undefined) {
      args.splice(3, 0, '--max-items', String(maxItems));
    }

    return this.executeJson<LabelShowResponse>(args);
  }

  mergeBase(sourcePath: string, targetPath: string): Promise<MergeBaseResponse> {
    return this.executeJson<MergeBaseResponse>([
      'merge',
      'base',
      '--source',
      sourcePath,
      '--target',
      targetPath,
      '--format',
      'json',
    ]);
  }

  mergeCandidates(sourcePath: string, targetPath: string, top = 20, scan = 80): Promise<MergeCandidateResponse> {
    return this.executeJson<MergeCandidateResponse>([
      'merge',
      'candidate',
      '--source',
      sourcePath,
      '--target',
      targetPath,
      '--top',
      String(top),
      '--scan',
      String(scan),
      '--format',
      'json',
    ]);
  }

  private async executeJson<T>(commandArgs: string[]): Promise<T> {
    const invocation = await this.resolveInvocation(commandArgs);
    this.output.appendLine(`> ${this.formatInvocation(invocation)}`);

    try {
      const { stdout, stderr } = await execFileAsync(invocation.command, invocation.args, {
        cwd: invocation.cwd,
        maxBuffer: 10 * 1024 * 1024,
      });

      if (stderr.trim()) {
        this.output.appendLine(stderr.trim());
      }

      const raw = stdout.trim();
      if (!raw) {
        throw new ArmTfsCliError('arm-tfs returned no JSON output.', invocation, stdout, stderr);
      }

      return JSON.parse(raw) as T;
    } catch (error) {
      if (error instanceof ArmTfsCliError) {
        throw error;
      }

      const stdout = typeof error === 'object' && error !== null && 'stdout' in error ? String((error as { stdout?: string }).stdout ?? '') : '';
      const stderr = typeof error === 'object' && error !== null && 'stderr' in error ? String((error as { stderr?: string }).stderr ?? '') : '';
      const message = error instanceof Error ? error.message : 'Failed to execute arm-tfs.';

      throw new ArmTfsCliError(message, invocation, stdout, stderr);
    }
  }

  private async resolveInvocation(commandArgs: string[]): Promise<ArmTfsInvocation> {
    const config = vscode.workspace.getConfiguration('armTfs');
    const configuredCommand = config.get<string>('cli.command')?.trim() ?? '';
    const configuredArgs = config.get<string[]>('cli.commandArgs') ?? [];
    const configuredCwd = config.get<string>('cli.cwd')?.trim() ?? '';
    const cwd = configuredCwd || this.getDefaultCwd();

    if (configuredCommand) {
      return {
        command: configuredCommand,
        args: [...configuredArgs, ...commandArgs],
        cwd,
      };
    }

    const preferWorkspaceBuild = config.get<boolean>('cli.preferWorkspaceBuild', true);
    if (preferWorkspaceBuild) {
      const workspaceBuild = await this.tryResolveWorkspaceBuild(commandArgs, cwd);
      if (workspaceBuild) {
        return workspaceBuild;
      }
    }

    return {
      command: 'arm-tfs',
      args: commandArgs,
      cwd,
    };
  }

  private async tryResolveWorkspaceBuild(commandArgs: string[], cwd?: string): Promise<ArmTfsInvocation | undefined> {
    const workspaceRoot = this.getDefaultCwd();
    if (!workspaceRoot) {
      return undefined;
    }

    const dotnetAvailable = await this.commandExists('dotnet');
    for (const rid of this.getRidCandidates()) {
      const dllCandidates = [
        path.join(workspaceRoot, 'src', 'ArmTfs.Cli', 'bin', 'Release', 'net8.0', rid, 'arm-tfs.dll'),
        path.join(workspaceRoot, 'src', 'ArmTfs.Cli', 'bin', 'Debug', 'net8.0', rid, 'arm-tfs.dll'),
      ];

      for (const dllPath of dllCandidates) {
        if (dotnetAvailable && existsSync(dllPath)) {
          return {
            command: 'dotnet',
            args: [dllPath, ...commandArgs],
            cwd,
          };
        }
      }

      const executableName = process.platform === 'win32' ? 'arm-tfs.exe' : 'arm-tfs';
      const executableCandidates = [
        path.join(workspaceRoot, 'publish', rid, executableName),
      ];

      for (const executablePath of executableCandidates) {
        if (existsSync(executablePath)) {
          return {
            command: executablePath,
            args: commandArgs,
            cwd,
          };
        }
      }
    }

    return undefined;
  }

  private getRidCandidates(): string[] {
    const candidates = new Set<string>();

    if (process.platform === 'darwin') {
      candidates.add(process.arch === 'arm64' ? 'osx-arm64' : 'osx-x64');
      candidates.add('osx-arm64');
    } else if (process.platform === 'win32') {
      candidates.add(process.arch === 'arm64' ? 'win-arm64' : 'win-x64');
      candidates.add('win-x64');
      candidates.add('win-arm64');
    } else {
      candidates.add(process.arch === 'arm64' ? 'linux-arm64' : 'linux-x64');
      candidates.add('linux-x64');
      candidates.add('linux-arm64');
    }

    return [...candidates];
  }

  private getDefaultCwd(): string | undefined {
    return vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  }

  private async commandExists(command: string): Promise<boolean> {
    const probe = process.platform === 'win32' ? 'where' : 'which';

    try {
      await execFileAsync(probe, [command]);
      return true;
    } catch {
      return false;
    }
  }

  private formatInvocation(invocation: ArmTfsInvocation): string {
    const parts = [invocation.command, ...invocation.args].map((part) => (part.includes(' ') ? JSON.stringify(part) : part));
    return invocation.cwd ? `${parts.join(' ')}  (cwd: ${invocation.cwd})` : parts.join(' ');
  }
}