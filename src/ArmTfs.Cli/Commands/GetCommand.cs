using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;
using ArmTfs.Core.Workspace;

namespace ArmTfs.Cli.Commands;

/// <summary>
/// arm-tfs get [path] — 从服务器获取最新版本到本地工作区
/// </summary>
public static class GetCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("get", "Get the latest version of files from the server.");

        var pathArg = new Argument<string>("path", () => ".", "Local path or server path to sync");
        var versionOpt = new Option<string?>("--version", "-v") { Description = "Version spec: changeset (C123), label (Lname), date (Ddate), latest (T)" };
        var forceOpt = new Option<bool>(new[] { "--force", "-f" }) { Description = "Overwrite even if local file appears up-to-date" };
        var cleanOpt = new Option<bool>("--clean") { Description = "Delete all local files before downloading (ensures 100% sync with server)" };
        var recursiveOpt = new Option<bool>(new[] { "--recursive", "-r" }, getDefaultValue: () => true) { Description = "Get all files recursively (default: true)" };
        var dryRunOpt = new Option<bool>("--dry-run") { Description = "Show what would be downloaded without writing files" };
        var localPathOpt = new Option<string?>("--local-path") { Description = "Local directory override when auto-creating a workspace from a server path" };
        var parallelOpt = new Option<int>("--parallel", () => 6) { Description = "Maximum concurrent file downloads (default: 6, range: 1-32)" };

        cmd.AddArgument(pathArg);
        cmd.AddOption(versionOpt);
        cmd.AddOption(forceOpt);
        cmd.AddOption(cleanOpt);
        cmd.AddOption(recursiveOpt);
        cmd.AddOption(dryRunOpt);
        cmd.AddOption(localPathOpt);
        cmd.AddOption(parallelOpt);

        cmd.SetHandler(async (path, versionSpec, force, clean, recursive, dryRun, localPathOverride, parallel) =>
        {
            // Parse version spec → int? (changeset) for REST
            int? version = ParseVersionSpec(versionSpec);
            Microsoft.TeamFoundation.SourceControl.WebApi.TfvcVersionDescriptor? versionDescriptor = null;
            if (!string.IsNullOrWhiteSpace(versionSpec) && version is null)
            {
                // Non-numeric version spec — pass through as label or date descriptor via TfvcVersionDescriptor
                // We'll plumb this in GetItemsAsync via the existing atChangeset=null path (not yet supported)
                // For now, try resolving label/date to a changeset ID via REST items call
                versionDescriptor = ParseVersionDescriptor(versionSpec);
            }
            var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());

            // ── 自动工作区创建 ─────────────────────────────────────────────────────
            // 如果没找到工作区，且传入的是服务器路径（$/…），则尝试根据 WorkspaceRoot
            // 映射规则自动推导本地路径并创建工作区，无需手动 workspace new。
            if (ws is null && path.StartsWith("$/", StringComparison.Ordinal))
            {
                var (autoWs, created) = WorkspaceAutoCreate.EnsureWorkspace(path, config, localPathOverride);
                if (autoWs is not null)
                {
                    ws = autoWs;
                    if (created)
                    {
                        var localPath = WorkspaceAutoCreate.ResolveLocalPath(path, config, localPathOverride)!;
                        Console.WriteLine($"Auto-created workspace at '{localPath}' → {path}");
                    }
                }
                else
                {
                    Console.Error.WriteLine(
                        "No workspace found and cannot auto-create one.\n" +
                        "Either run 'arm-tfs workspace new' first, or configure a workspace root:\n" +
                        "  arm-tfs configure --workspace-root /your/local/tfs/root");
                    Environment.ExitCode = 1;
                    return;
                }
            }
            else if (ws is null)
            {
                Console.Error.WriteLine("No workspace found. Run 'arm-tfs workspace new' first.");
                Environment.ExitCode = 1;
                return;
            }

            var meta = ws.LoadMetadata();

            // 解析服务器路径
            string serverPath;
            if (path.StartsWith("$/"))
            {
                serverPath = path;
            }
            else
            {
                var absPath = Path.GetFullPath(path);
                serverPath = ws.LocalToServerPath(absPath, meta)
                    ?? meta.Mappings.FirstOrDefault()?.ServerPath
                    ?? throw new InvalidOperationException("Cannot determine server path from local path.");
            }

            // 更新工作区的服务器 URL（如果还未设置）
            if (meta.ServerCollectionUrl == "pending" && !string.IsNullOrEmpty(config.ServerUrl))
            {
                meta = meta with { ServerCollectionUrl = config.ServerUrl };
                ws.SaveMetadata(meta);
            }

            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            Console.WriteLine($"Getting from: {serverPath}");

            // --clean: 先删除本地所有文件（保留.tf目录），确保与服务器完全一致
            if (clean)
            {
                var mapping = meta.Mappings.FirstOrDefault(m =>
                    serverPath.StartsWith(m.ServerPath, StringComparison.OrdinalIgnoreCase));
                var localRoot = mapping?.LocalPath;
                if (localRoot is not null && Directory.Exists(localRoot))
                {
                    Console.WriteLine($"Cleaning local directory: {localRoot}");
                    foreach (var dir in Directory.GetDirectories(localRoot))
                    {
                        if (Path.GetFileName(dir) == ".tf") continue;
                        Directory.Delete(dir, recursive: true);
                    }
                    foreach (var file in Directory.GetFiles(localRoot))
                    {
                        File.Delete(file);
                    }
                    Console.WriteLine("Local files cleaned. Downloading fresh copy...");
                }
                force = true;
            }
            var items = await svc.GetItemsAsync(serverPath, recursive, version).ConfigureAwait(false);
            var cloaked = meta.CloakedPaths;
            var files = items
                .Where(i => !i.IsFolder)
                .Where(i => !cloaked.Any(c => i.ServerPath.StartsWith(c + "/", StringComparison.OrdinalIgnoreCase) || string.Equals(i.ServerPath, c, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Console.WriteLine($"Found {files.Count} file(s).");

            var maxParallel = Math.Clamp(parallel, 1, 32);
            int downloaded = 0, skipped = 0, errors = 0;
            var consoleLock = new object();

            async Task ProcessItemAsync(ArmTfs.Core.Models.TfsServerItem item)
            {
                var localPath = ws.ServerToLocalPath(item.ServerPath, meta);
                if (localPath is null)
                {
                    lock (consoleLock)
                        Console.Error.WriteLine($"  [SKIP] No mapping for {item.ServerPath}");
                    Interlocked.Increment(ref skipped);
                    return;
                }

                // 检查是否需要更新
                if (!force && File.Exists(localPath))
                {
                    var tracked = ws.GetTrackedVersion(localPath);
                    if (tracked is not null &&
                        tracked.ChangesetId == item.ChangesetId &&
                        tracked.ContentHash == WorkspaceManager.ComputeFileHash(localPath))
                    {
                        if (ws.GetCachedBaseFilePath(localPath) is null)
                            ws.SaveBaseFileFromDisk(localPath);
                        Interlocked.Increment(ref skipped);
                        return;
                    }
                }

                if (dryRun)
                {
                    Console.WriteLine($"  [WOULD GET] {localPath}  (cs#{item.ChangesetId})");
                    Interlocked.Increment(ref downloaded);
                    return;
                }

                try
                {
                    var dir = Path.GetDirectoryName(localPath)!;
                    Directory.CreateDirectory(dir);

                    await using (var fileStream = File.Create(localPath))
                    {
                        await svc.DownloadFileAsync(item.ServerPath, fileStream, version).ConfigureAwait(false);
                    }

                    var hash = WorkspaceManager.ComputeFileHash(localPath);
                    ws.SaveTrackedVersion(new Core.Models.TrackedFileVersion
                    {
                        ServerPath = item.ServerPath,
                        LocalPath = localPath,
                        ChangesetId = item.ChangesetId,
                        ContentHash = hash,
                    });
                    ws.SaveBaseFileFromDisk(localPath);

                    lock (consoleLock)
                        Console.WriteLine($"  {localPath}  (cs#{item.ChangesetId})");
                    Interlocked.Increment(ref downloaded);
                }
                catch (Exception ex)
                {
                    lock (consoleLock)
                        Console.Error.WriteLine($"  [ERROR] {item.ServerPath}: {ex.Message}");
                    Interlocked.Increment(ref errors);
                }
            }

            if (dryRun || maxParallel == 1)
            {
                foreach (var item in files)
                    await ProcessItemAsync(item).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine($"Downloading with parallelism: {maxParallel}");
                using var semaphore = new SemaphoreSlim(maxParallel);
                var tasks = files.Select(async item =>
                {
                    await semaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await ProcessItemAsync(item).ConfigureAwait(false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            Console.WriteLine();
            Console.WriteLine($"Downloaded: {downloaded}  Skipped (up-to-date): {skipped}  Errors: {errors}");

            if (errors > 0) Environment.ExitCode = 1;
        }, pathArg, versionOpt, forceOpt, cleanOpt, recursiveOpt, dryRunOpt, localPathOpt, parallelOpt);

        return cmd;
    }

    /// <summary>
    /// 解析 TFS 版本规格字符串为 changeset ID（int）。
    /// 支持格式：纯数字 "123"、"C123"（Changeset）。
    /// Label "Lname" 和 Date "D..." 返回 null（需要 versionDescriptor）。
    /// </summary>
    private static int? ParseVersionSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var s = spec.Trim();
        // C123 or plain integer → changeset
        if (s.StartsWith("C", StringComparison.OrdinalIgnoreCase) && int.TryParse(s[1..], out var cs)) return cs;
        if (int.TryParse(s, out var n)) return n;
        return null;
    }

    private static Microsoft.TeamFoundation.SourceControl.WebApi.TfvcVersionDescriptor? ParseVersionDescriptor(string spec)
    {
        var s = spec.Trim();
        if (s.StartsWith("L", StringComparison.OrdinalIgnoreCase))
            return new Microsoft.TeamFoundation.SourceControl.WebApi.TfvcVersionDescriptor
            {
                VersionType = Microsoft.TeamFoundation.SourceControl.WebApi.TfvcVersionType.Changeset,
                VersionOption = Microsoft.TeamFoundation.SourceControl.WebApi.TfvcVersionOption.None,
                Version = $"L{s[1..]}",
            };
        if (s.StartsWith("D", StringComparison.OrdinalIgnoreCase))
            return new Microsoft.TeamFoundation.SourceControl.WebApi.TfvcVersionDescriptor
            {
                VersionType = Microsoft.TeamFoundation.SourceControl.WebApi.TfvcVersionType.Changeset,
                VersionOption = Microsoft.TeamFoundation.SourceControl.WebApi.TfvcVersionOption.None,
                Version = null, // date not easily representable; fall back to null (latest)
            };
        // T (latest) — return null (default behavior is latest)
        return null;
    }
}
