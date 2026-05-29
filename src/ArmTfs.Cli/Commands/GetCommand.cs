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
        var versionOpt = new Option<int?>("--version", "-v") { Description = "Download a specific changeset version" };
        var forceOpt = new Option<bool>(new[] { "--force", "-f" }) { Description = "Overwrite even if local file appears up-to-date" };
        var recursiveOpt = new Option<bool>(new[] { "--recursive", "-r" }, getDefaultValue: () => true) { Description = "Get all files recursively (default: true)" };
        var dryRunOpt = new Option<bool>("--dry-run") { Description = "Show what would be downloaded without writing files" };

        cmd.AddArgument(pathArg);
        cmd.AddOption(versionOpt);
        cmd.AddOption(forceOpt);
        cmd.AddOption(recursiveOpt);
        cmd.AddOption(dryRunOpt);

        cmd.SetHandler(async (path, version, force, recursive, dryRun) =>
        {
            var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
            if (ws is null) { Console.Error.WriteLine("No workspace found. Run 'arm-tfs workspace new' first."); Environment.ExitCode = 1; return; }

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
            var files = items.Where(i => !i.IsFolder).ToList();

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
        }, pathArg, versionOpt, forceOpt, recursiveOpt, dryRunOpt);

        return cmd;
    }
}
