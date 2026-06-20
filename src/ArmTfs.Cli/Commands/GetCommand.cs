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
        var recursiveOpt = new Option<bool>(new[] { "--recursive", "-r" }, getDefaultValue: () => true) { Description = "Get all files recursively (default: true)" };
        var dryRunOpt = new Option<bool>("--dry-run") { Description = "Show what would be downloaded without writing files" };
        var localPathOpt = new Option<string?>("--local-path") { Description = "Local directory override when auto-creating a workspace from a server path" };

        cmd.AddArgument(pathArg);
        cmd.AddOption(versionOpt);
        cmd.AddOption(forceOpt);
        cmd.AddOption(recursiveOpt);
        cmd.AddOption(dryRunOpt);
        cmd.AddOption(localPathOpt);

        cmd.SetHandler(async (path, versionSpec, force, recursive, dryRun, localPathOverride) =>
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

            var items = await svc.GetItemsAsync(serverPath, recursive, version).ConfigureAwait(false);
            var cloaked = meta.CloakedPaths;
            var files = items
                .Where(i => !i.IsFolder)
                .Where(i => !cloaked.Any(c => i.ServerPath.StartsWith(c + "/", StringComparison.OrdinalIgnoreCase) || string.Equals(i.ServerPath, c, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Console.WriteLine($"Found {files.Count} file(s).");

            int downloaded = 0, skipped = 0, errors = 0;

            foreach (var item in files)
            {
                var localPath = ws.ServerToLocalPath(item.ServerPath, meta);
                if (localPath is null)
                {
                    Console.Error.WriteLine($"  [SKIP] No mapping for {item.ServerPath}");
                    skipped++;
                    continue;
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
                        skipped++;
                        continue;
                    }
                }

                if (dryRun)
                {
                    Console.WriteLine($"  [WOULD GET] {localPath}  (cs#{item.ChangesetId})");
                    downloaded++;
                    continue;
                }

                try
                {
                    var dir = Path.GetDirectoryName(localPath)!;
                    Directory.CreateDirectory(dir);

                    await using var fileStream = File.Create(localPath);
                    await svc.DownloadFileAsync(item.ServerPath, fileStream, version).ConfigureAwait(false);
                    fileStream.Close();

                    var hash = WorkspaceManager.ComputeFileHash(localPath);
                    ws.SaveTrackedVersion(new Core.Models.TrackedFileVersion
                    {
                        ServerPath = item.ServerPath,
                        LocalPath = localPath,
                        ChangesetId = item.ChangesetId,
                        ContentHash = hash,
                    });
                    ws.SaveBaseFileFromDisk(localPath);

                    Console.WriteLine($"  {localPath}  (cs#{item.ChangesetId})");
                    downloaded++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [ERROR] {item.ServerPath}: {ex.Message}");
                    errors++;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Downloaded: {downloaded}  Skipped (up-to-date): {skipped}  Errors: {errors}");

            if (errors > 0) Environment.ExitCode = 1;
        }, pathArg, versionOpt, forceOpt, recursiveOpt, dryRunOpt, localPathOpt);

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
