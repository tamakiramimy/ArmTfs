import { execFile } from 'node:child_process';
import { existsSync } from 'node:fs';
import * as path from 'node:path';
import { promisify } from 'node:util';
import * as vscode from 'vscode';
import type {
  BranchListResponse,
  BranchCreateResponse,
  BranchShowResponse,
  ChangesetShowResponse,
  DiffResponse,
  HistoryResponse,
  ItemContentResponse,
  ItemsListResponse,
  LabelListResponse,
  LabelShowResponse,
  MergeBaseResponse,
  MergeCandidateResponse,
  MergeExecuteResponse,
  StatusResponse,
} from './contracts';

const execFileAsync = promisify(execFile);

export interface ArmTfsRunOptions {
  cwdOverride?: string;
}

export interface ArmTfsConnectionEnvironment {
  serverUrl: string;
  pat: string;
  displayName?: string;
}

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
  private connectionEnvironmentProvider?: () => Promise<ArmTfsConnectionEnvironment | undefined>;

  constructor(private readonly output: vscode.OutputChannel) {}

  setConnectionEnvironmentProvider(
    provider: () => Promise<ArmTfsConnectionEnvironment | undefined>,
  ): void {
    this.connectionEnvironmentProvider = provider;
  }

  async describeResolvedInvocation(): Promise<string> {
    const invocation = await this.resolveInvocation([]);
    return this.formatInvocation(invocation);
  }

  status(targetPath = '.', all = true, options?: ArmTfsRunOptions): Promise<StatusResponse> {
    const args = ['status', targetPath, '--format', 'json'];
    if (all) {
      args.splice(2, 0, '--all');
    }

    return this.executeJson<StatusResponse>(args, options);
  }

  configureShow(options?: ArmTfsRunOptions): Promise<string> {
    return this.executeText(['configure', '--show'], options);
  }

  workspaceNew(name: string, serverPath: string, directory = '.', localPath?: string, options?: ArmTfsRunOptions): Promise<string> {
    const args = ['workspace', 'new', '--name', name, '--server-path', serverPath];
    if (localPath) {
      args.push('--local-path', localPath);
    }
    args.push(directory);

    return this.executeText(args, options);
  }

  workspaceShow(options?: ArmTfsRunOptions): Promise<string> {
    return this.executeText(['workspace', 'show'], options);
  }

  workspaceMap(serverPath: string, localPath: string, options?: ArmTfsRunOptions): Promise<string> {
    return this.executeText(['workspace', 'map', '--server-path', serverPath, '--local-path', localPath], options);
  }

  get(targetPath = '.', options?: { version?: number; force?: boolean; recursive?: boolean; dryRun?: boolean }, runOptions?: ArmTfsRunOptions): Promise<string> {
    const args = ['get', targetPath];
    if (options?.version !== undefined) {
      args.push('--version', String(options.version));
    }
    if (options?.force) {
      args.push('--force');
    }
    if (options?.recursive === false) {
      args.push('--recursive', 'false');
    }
    if (options?.dryRun) {
      args.push('--dry-run');
    }

    return this.executeText(args, runOptions);
  }

  checkout(paths: string[], recursive = false, options?: ArmTfsRunOptions): Promise<string> {
    const args = ['checkout', ...paths];
    if (recursive) {
      args.push('--recursive');
    }

    return this.executeText(args, options);
  }

  add(paths: string[], recursive = false, options?: ArmTfsRunOptions): Promise<string> {
    const args = ['add', ...paths];
    if (recursive) {
      args.push('--recursive');
    }

    return this.executeText(args, options);
  }

  undo(paths: string[], noRestore = false, options?: ArmTfsRunOptions): Promise<string> {
    const args = ['undo', ...paths];
    if (noRestore) {
      args.push('--no-restore');
    }

    return this.executeText(args, options);
  }

  checkin(comment: string, paths: string[], keepPending = false, dryRun = false, options?: ArmTfsRunOptions): Promise<string> {
    const args = ['checkin', '--comment', comment, ...paths];
    if (dryRun) {
      args.push('--dry-run');
    }
    if (keepPending) {
      args.push('--keep-pending');
    }

    return this.executeText(args, options);
  }

  history(targetPath = '.', top = 20, author?: string, options?: ArmTfsRunOptions): Promise<HistoryResponse> {
    const args = ['history', targetPath, '--top', String(top), '--format', 'json'];
    if (author) {
      args.splice(4, 0, '--author', author);
    }

    return this.executeJson<HistoryResponse>(args, options);
  }

  itemContent(serverPath: string, version?: number, options?: ArmTfsRunOptions): Promise<ItemContentResponse> {
    const args = ['items', 'cat', serverPath, '--format', 'json'];
    if (version !== undefined) {
      args.splice(3, 0, '--version', String(version));
    }
    return this.executeJson<ItemContentResponse>(args, options);
  }

  diff(targetPath: string, options?: { useBase?: boolean; changesetId?: number; ignoreWhitespace?: boolean }, runOptions?: ArmTfsRunOptions): Promise<DiffResponse> {
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

    return this.executeJson<DiffResponse>(args, runOptions);
  }

  diffVersions(
    serverPath: string,
    fromChangesetId: number,
    toChangesetId: number,
    options?: { ignoreWhitespace?: boolean; toServerPath?: string },
    runOptions?: ArmTfsRunOptions,
  ): Promise<DiffResponse> {
    const args = [
      'diff',
      serverPath,
      '--from-version',
      String(fromChangesetId),
      '--to-version',
      String(toChangesetId),
      '--format',
      'json',
    ];
    if (options?.toServerPath && options.toServerPath !== serverPath) {
      args.splice(args.length - 2, 0, '--to-path', options.toServerPath);
    }
    if (options?.ignoreWhitespace) {
      args.push('--ignore-whitespace');
    }
    return this.executeJson<DiffResponse>(args, runOptions);
  }

  branchList(scope = '$/', options?: ArmTfsRunOptions): Promise<BranchListResponse> {
    return this.executeJson<BranchListResponse>(['branch', 'list', scope, '--format', 'json'], options);
  }

  branchShow(branchPath: string, options?: ArmTfsRunOptions): Promise<BranchShowResponse> {
    return this.executeJson<BranchShowResponse>(['branch', 'show', branchPath, '--format', 'json'], options);
  }

  branchCreate(
    sourcePath: string,
    targetPath: string,
    options?: { version?: number; comment?: string },
    runOptions?: ArmTfsRunOptions,
  ): Promise<BranchCreateResponse> {
    const args = ['branch', 'create', '--source', sourcePath, '--target', targetPath, '--format', 'json'];
    if (options?.version !== undefined) {
      args.push('--version', String(options.version));
    }
    if (options?.comment) {
      args.push('--comment', options.comment);
    }
    return this.executeJson<BranchCreateResponse>(args, runOptions);
  }

  changesetShow(changesetId: number, options?: ArmTfsRunOptions): Promise<ChangesetShowResponse> {
    return this.executeJson<ChangesetShowResponse>(['changeset', 'show', String(changesetId), '--format', 'json'], options);
  }

  labelList(top = 20, options?: ArmTfsRunOptions): Promise<LabelListResponse> {
    return this.executeJson<LabelListResponse>(['label', 'list', '--top', String(top), '--format', 'json'], options);
  }

  labelShow(labelId: string, maxItems?: number, options?: ArmTfsRunOptions): Promise<LabelShowResponse> {
    const args = ['label', 'show', labelId, '--format', 'json'];
    if (maxItems !== undefined) {
      args.splice(3, 0, '--max-items', String(maxItems));
    }

    return this.executeJson<LabelShowResponse>(args, options);
  }

  mergeBase(sourcePath: string, targetPath: string, options?: ArmTfsRunOptions): Promise<MergeBaseResponse> {
    return this.executeJson<MergeBaseResponse>([
      'merge',
      'base',
      '--source',
      sourcePath,
      '--target',
      targetPath,
      '--format',
      'json',
    ], options);
  }

  mergeCandidates(sourcePath: string, targetPath: string, top = 20, scan = 80, options?: ArmTfsRunOptions): Promise<MergeCandidateResponse> {
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
    ], options);
  }

  mergeExecute(
    sourcePath: string,
    targetPath: string,
    changesetId: number,
    options?: { comment?: string; dryRun?: boolean; resolutionFile?: string },
    runOptions?: ArmTfsRunOptions,
  ): Promise<string> {
    const args = [
      'merge',
      'execute',
      '--source',
      sourcePath,
      '--target',
      targetPath,
      '--changeset',
      String(changesetId),
    ];
    if (options?.comment) {
      args.push('--comment', options.comment);
    }
    if (options?.dryRun) {
      args.push('--dry-run');
    }
    if (options?.resolutionFile) {
      args.push('--resolution-file', options.resolutionFile);
    }

    return this.executeText(args, runOptions);
  }

  mergeExecuteJson(
    sourcePath: string,
    targetPath: string,
    changesetId: number,
    options?: { comment?: string; dryRun?: boolean; resolutionFile?: string },
    runOptions?: ArmTfsRunOptions,
  ): Promise<MergeExecuteResponse> {
    const args = [
      'merge',
      'execute',
      '--source',
      sourcePath,
      '--target',
      targetPath,
      '--changeset',
      String(changesetId),
      '--format',
      'json',
    ];
    if (options?.comment) {
      args.push('--comment', options.comment);
    }
    if (options?.dryRun) {
      args.push('--dry-run');
    }
    if (options?.resolutionFile) {
      args.push('--resolution-file', options.resolutionFile);
    }

    return this.executeJson<MergeExecuteResponse>(args, runOptions);
  }

  /**
   * List items at the given TFVC server path (one-level shallow by default).
   * Pass recursive=true to get all descendants.
   */
  itemsList(serverPath = '$/', recursive = false, options?: ArmTfsRunOptions): Promise<ItemsListResponse> {
    const args = ['items', 'list', serverPath, '--format', 'json'];
    if (recursive) {
      args.push('--recursive');
    }

    return this.executeJson<ItemsListResponse>(args, options);
  }

  /**
   * Configure TFS server URL, PAT, and display name by calling the CLI's configure subcommand.
   * The PAT is passed as a command-line argument — avoid reuse in non-configuration contexts.
   */
  configurePat(serverUrl: string, pat: string, displayName?: string, options?: ArmTfsRunOptions): Promise<string> {
    const args = ['configure', '--url', serverUrl, '--pat', pat];
    if (displayName) {
      args.push('--display-name', displayName);
    }

    return this.executeText(args, options);
  }

  private async executeJson<T>(commandArgs: string[], options?: ArmTfsRunOptions): Promise<T> {
    const { stdout, stderr } = await this.execute(commandArgs, options);
    const raw = stdout.trim();
    if (!raw) {
      throw new ArmTfsCliError(
        stderr.trim() || 'arm-tfs returned no JSON output.',
        await this.resolveInvocation(commandArgs, options?.cwdOverride),
        stdout,
        stderr,
      );
    }

    return JSON.parse(raw) as T;
  }

  private async executeText(commandArgs: string[], options?: ArmTfsRunOptions): Promise<string> {
    const { stdout, stderr } = await this.execute(commandArgs, options);
    const text = [stdout.trim(), stderr.trim()].filter(Boolean).join('\n');
    return text || 'Command completed with no output.';
  }

  private async execute(commandArgs: string[], options?: ArmTfsRunOptions): Promise<{ stdout: string; stderr: string }> {
    const invocation = await this.resolveInvocation(commandArgs, options?.cwdOverride);
    this.output.appendLine(`> ${this.formatInvocation(invocation)}`);
    const connection = await this.connectionEnvironmentProvider?.();
    const env = connection
      ? {
          ...process.env,
          ARM_TFS_URL: connection.serverUrl,
          ARM_TFS_PAT: connection.pat,
          ARM_TFS_DISPLAY_NAME: connection.displayName ?? '',
        }
      : process.env;

    try {
      const { stdout, stderr } = await execFileAsync(invocation.command, invocation.args, {
        cwd: invocation.cwd,
        env,
        maxBuffer: 10 * 1024 * 1024,
      });

      if (stderr.trim()) {
        this.output.appendLine(stderr.trim());
      }

      return { stdout, stderr };
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

  private async resolveInvocation(commandArgs: string[], cwdOverride?: string): Promise<ArmTfsInvocation> {
    const config = vscode.workspace.getConfiguration('armTfs');
    const configuredCommand = config.get<string>('cli.command')?.trim() ?? '';
    const configuredArgs = config.get<string[]>('cli.commandArgs') ?? [];
    const configuredCwd = config.get<string>('cli.cwd')?.trim() ?? '';
    const cwd = cwdOverride || configuredCwd || this.getDefaultCwd();

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
