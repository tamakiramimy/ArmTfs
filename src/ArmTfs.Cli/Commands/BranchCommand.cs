using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;
using ArmTfs.Core.Models;
using ArmTfs.Core.Workspace;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs branch — 查询 TFVC 分支信息。</summary>
public static class BranchCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("branch", "List and inspect TFVC branches.");

        cmd.AddCommand(BuildList(config));
        cmd.AddCommand(BuildShow(config));
        cmd.AddCommand(BuildCreate(config));
        cmd.AddCommand(BuildDelete(config));

        return cmd;
    }

    private static Command BuildDelete(TfsConfig config)
    {
        var cmd = new Command("delete", "Delete a TFVC branch (reversible delete changeset; not tf destroy).");
        var pathOpt = new Option<string>("--path") { Description = "Branch path to delete ($/...)", IsRequired = true };
        var commentOpt = new Option<string?>("--comment") { Description = "Changeset comment" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };

        cmd.AddOption(pathOpt);
        cmd.AddOption(commentOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (path, comment, format) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                if (!path.StartsWith("$/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Branch path must be a server path starting with $/.");
                var resolved = path;
                var created = await svc.DeleteItemAsync(resolved, comment).ConfigureAwait(false);

                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "branch.delete",
                        path = resolved,
                        createdChangesetId = created.ChangesetId,
                    });
                    return;
                }

                Console.WriteLine($"Deleted branch : {resolved}");
                Console.WriteLine($"Changeset      : cs#{created.ChangesetId}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, pathOpt, commentOpt, formatOpt);

        return cmd;
    }

    private static Command BuildCreate(TfsConfig config)
    {
        var cmd = new Command("create", "Create a TFVC branch from an existing branch.");
        var sourceOpt = new Option<string>("--source") { Description = "Source branch path ($/...)", IsRequired = true };
        var targetOpt = new Option<string>("--target") { Description = "New target branch path ($/...)", IsRequired = true };
        var versionOpt = new Option<int?>("--version") { Description = "Optional source changeset version" };
        var commentOpt = new Option<string?>("--comment") { Description = "Changeset comment" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };

        cmd.AddOption(sourceOpt);
        cmd.AddOption(targetOpt);
        cmd.AddOption(versionOpt);
        cmd.AddOption(commentOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (source, target, version, comment, format) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                var created = await svc.CreateBranchAsync(source, target, version, comment).ConfigureAwait(false);
                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "branch.create",
                        sourcePath = source,
                        targetPath = target,
                        sourceChangesetId = version,
                        createdChangesetId = created.ChangesetId,
                    });
                    return;
                }
                Console.WriteLine($"Created branch {target} from {source} in changeset {created.ChangesetId}.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, sourceOpt, targetOpt, versionOpt, commentOpt, formatOpt);

        return cmd;
    }

    private static Command BuildList(TfsConfig config)
    {
        var cmd = new Command("list", "List TFVC branches under a scope path.");
        var scopeArg = new Argument<string>("scope", () => "$/", "Server scope path (default: $/)");
        var includeDeletedOpt = new Option<bool>("--include-deleted") { Description = "Include deleted branches" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };
        var getOpt = new Option<bool>("--get") { Description = "After listing, get latest files for each branch (auto-creates workspace if needed)" };
        var localPathOpt = new Option<string?>("--local-path") { Description = "Local directory override when auto-creating a workspace (only used with --get and a single branch scope)" };

        cmd.AddArgument(scopeArg);
        cmd.AddOption(includeDeletedOpt);
        cmd.AddOption(formatOpt);
        cmd.AddOption(getOpt);
        cmd.AddOption(localPathOpt);

        cmd.SetHandler(async (scope, includeDeleted, format, get, localPathOverride) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                var branches = await svc.GetBranchRefsAsync(scope, includeDeleted).ConfigureAwait(false);
                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "branch.list",
                        query = new
                        {
                            scope,
                            includeDeleted,
                        },
                        items = branches.Select(ProjectBranchRef),
                    });

                    if (get)
                        await GetBranchesAsync(branches.Select(b => b.Path), config, svc, localPathOverride).ConfigureAwait(false);

                    return;
                }

                if (branches.Count == 0)
                {
                    Console.WriteLine("No branches found.");
                    return;
                }

                Console.WriteLine($"{"Path",-80}  {"Owner",-25}  {"Created",-20}  Deleted");
                Console.WriteLine($"{new string('-', 80)}  {new string('-', 25)}  {new string('-', 20)}  {new string('-', 7)}");
                foreach (var branch in branches.OrderBy(b => b.Path, StringComparer.OrdinalIgnoreCase))
                {
                    var owner = branch.Owner?.DisplayName ?? string.Empty;
                    var created = branch.CreatedDate == default ? string.Empty : branch.CreatedDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                    Console.WriteLine($"{branch.Path,-80}  {owner,-25}  {created,-20}  {(branch.IsDeleted ? "yes" : "no")}");
                }

                if (get)
                    await GetBranchesAsync(branches.Select(b => b.Path), config, svc, localPathOverride).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, scopeArg, includeDeletedOpt, formatOpt, getOpt, localPathOpt);

        return cmd;
    }

    private static Command BuildShow(TfsConfig config)
    {
        var cmd = new Command("show", "Show details for a TFVC branch.");
        var pathArg = new Argument<string>("path") { Description = "Server branch path ($/...)" };
        var noChildrenOpt = new Option<bool>("--no-children") { Description = "Do not expand child branches" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };
        var getOpt = new Option<bool>("--get") { Description = "Get latest files for this branch after showing info (auto-creates workspace if needed)" };
        var localPathOpt = new Option<string?>("--local-path") { Description = "Local directory override when auto-creating a workspace" };

        cmd.AddArgument(pathArg);
        cmd.AddOption(noChildrenOpt);
        cmd.AddOption(formatOpt);
        cmd.AddOption(getOpt);
        cmd.AddOption(localPathOpt);

        cmd.SetHandler(async (path, noChildren, format, get, localPathOverride) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                var branch = await svc.GetBranchAsync(path, includeChildren: !noChildren).ConfigureAwait(false);
                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "branch.show",
                        branch = ProjectBranch(branch),
                    });

                    if (get)
                        await GetBranchesAsync(new[] { branch.Path }, config, svc, localPathOverride).ConfigureAwait(false);

                    return;
                }

                Console.WriteLine($"Path       : {branch.Path}");
                Console.WriteLine($"Owner      : {branch.Owner?.DisplayName}");
                Console.WriteLine($"Created    : {branch.CreatedDate.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"Deleted    : {(branch.IsDeleted ? "yes" : "no")}");
                Console.WriteLine($"Description: {branch.Description}");
                Console.WriteLine($"Parent     : {branch.Parent?.Path}");
                Console.WriteLine($"Children   : {branch.Children?.Count ?? 0}");

                if (branch.Mappings is { Count: > 0 })
                {
                    Console.WriteLine();
                    Console.WriteLine("Mappings:");
                    foreach (var mapping in branch.Mappings)
                        Console.WriteLine($"  {mapping.ServerItem,-70}  {mapping.Type,-10}  {mapping.Depth}");
                }

                if (branch.Children is { Count: > 0 })
                {
                    Console.WriteLine();
                    Console.WriteLine("Child branches:");
                    foreach (var child in branch.Children.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
                        Console.WriteLine($"  {child.Path}");
                }

                if (branch.RelatedBranches is { Count: > 0 })
                {
                    Console.WriteLine();
                    Console.WriteLine("Related branches:");
                    foreach (var related in branch.RelatedBranches.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
                        Console.WriteLine($"  {related.Path}");
                }

                if (get)
                    await GetBranchesAsync(new[] { branch.Path }, config, svc, localPathOverride).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, pathArg, noChildrenOpt, formatOpt, getOpt, localPathOpt);

        return cmd;
    }

    // ─── 自动工作区创建 + 拉取逻辑 ─────────────────────────────────────────────

    /// <summary>
    /// 对给定的服务器路径列表，逐一自动创建工作区（如有需要）并拉取最新文件。
    /// </summary>
    private static async Task GetBranchesAsync(
        IEnumerable<string> serverPaths,
        TfsConfig config,
        TfvcClientService svc,
        string? localPathOverride)
    {
        foreach (var serverPath in serverPaths)
        {
            Console.WriteLine();
            Console.WriteLine($"── Getting: {serverPath}");

            // 推导或查找工作区
            var ws = WorkspaceManager.FindWorkspace(
                WorkspaceAutoCreate.ResolveLocalPath(serverPath, config, localPathOverride)
                ?? Directory.GetCurrentDirectory());

            if (ws is null)
            {
                var (autoWs, created) = WorkspaceAutoCreate.EnsureWorkspace(serverPath, config, localPathOverride);
                if (autoWs is null)
                {
                    Console.Error.WriteLine(
                        $"  [SKIP] Cannot auto-create workspace for {serverPath}.\n" +
                        "  Configure workspace root first:\n" +
                        "    arm-tfs configure --workspace-root /your/local/tfs/root");
                    Environment.ExitCode = 1;
                    continue;
                }
                ws = autoWs;
                if (created)
                {
                    var localPath = WorkspaceAutoCreate.ResolveLocalPath(serverPath, config, localPathOverride)!;
                    Console.WriteLine($"  Auto-created workspace at '{localPath}'");
                }
            }

            var meta = ws.LoadMetadata();

            // 更新工作区的服务器 URL（如果还未设置）
            if (meta.ServerCollectionUrl == "pending" && !string.IsNullOrEmpty(config.ServerUrl))
            {
                meta = meta with { ServerCollectionUrl = config.ServerUrl };
                ws.SaveMetadata(meta);
            }

            Console.WriteLine($"  Local path : {meta.Mappings.FirstOrDefault()?.LocalPath ?? "(unknown)"}");

            try
            {
                var items = await svc.GetItemsAsync(serverPath, recursive: true).ConfigureAwait(false);
                var files = items.Where(i => !i.IsFolder).ToList();
                Console.WriteLine($"  Found {files.Count} file(s).");

                int downloaded = 0, skipped = 0, errors = 0;

                foreach (var item in files)
                {
                    var localFilePath = ws.ServerToLocalPath(item.ServerPath, meta);
                    if (localFilePath is null)
                    {
                        skipped++;
                        continue;
                    }

                    if (System.IO.File.Exists(localFilePath))
                    {
                        var tracked = ws.GetTrackedVersion(localFilePath);
                        if (tracked is not null &&
                            tracked.ChangesetId == item.ChangesetId &&
                            tracked.ContentHash == WorkspaceManager.ComputeFileHash(localFilePath))
                        {
                            if (ws.GetCachedBaseFilePath(localFilePath) is null)
                                ws.SaveBaseFileFromDisk(localFilePath);
                            skipped++;
                            continue;
                        }
                    }

                    try
                    {
                        var dir = Path.GetDirectoryName(localFilePath)!;
                        Directory.CreateDirectory(dir);

                        await using var fileStream = System.IO.File.Create(localFilePath);
                        await svc.DownloadFileAsync(item.ServerPath, fileStream).ConfigureAwait(false);
                        fileStream.Close();

                        var hash = WorkspaceManager.ComputeFileHash(localFilePath);
                        ws.SaveTrackedVersion(new TrackedFileVersion
                        {
                            ServerPath = item.ServerPath,
                            LocalPath = localFilePath,
                            ChangesetId = item.ChangesetId,
                            ContentHash = hash,
                        });
                        ws.SaveBaseFileFromDisk(localFilePath);

                        downloaded++;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  [ERROR] {item.ServerPath}: {ex.Message}");
                        errors++;
                    }
                }

                Console.WriteLine($"  Downloaded: {downloaded}  Skipped (up-to-date): {skipped}  Errors: {errors}");
                if (errors > 0) Environment.ExitCode = 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [ERROR] Failed to get files for {serverPath}: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }
    }

    // ─── 投影辅助 ─────────────────────────────────────────────────────────────

    private static object ProjectBranchRef(TfvcBranchRef branch) => new
    {
        path = branch.Path,
        description = branch.Description,
        owner = JsonOutput.Identity(branch.Owner),
        createdAt = branch.CreatedDate,
        isDeleted = branch.IsDeleted,
    };

    private static object ProjectBranch(TfvcBranch branch) => new
    {
        path = branch.Path,
        description = branch.Description,
        owner = JsonOutput.Identity(branch.Owner),
        createdAt = branch.CreatedDate,
        isDeleted = branch.IsDeleted,
        parentPath = branch.Parent?.Path,
        children = branch.Children?.Select(child => child.Path),
        relatedBranches = branch.RelatedBranches?.Select(related => related.Path),
        mappings = branch.Mappings?.Select(mapping => new
        {
            serverItem = mapping.ServerItem,
            type = mapping.Type,
            depth = mapping.Depth,
        }),
    };
}
