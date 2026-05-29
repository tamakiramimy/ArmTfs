using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;
using ArmTfs.Core.Models;
using ArmTfs.Core.Workspace;

namespace ArmTfs.Cli.Commands;

/// <summary>
/// <c>arm-tfs checkin</c> / <c>arm-tfs ci</c> — 将本地挂起变更提交到服务器。
/// <para>
/// 流程：
/// <list type="number">
///   <item>从 .tf/pending.json 读取待提交变更</item>
///   <item>按路径过滤（默认提交所有挂起变更）</item>
///   <item>读取 Add/Edit 文件内容，通过 REST API 创建 Changeset</item>
///   <item>提交成功后更新 .tf/versions/ 并清理 pending.json</item>
/// </list>
/// </para>
/// </summary>
public static class CheckinCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("checkin", "Check in pending changes to the server.");
        cmd.AddAlias("ci");

        var commentOpt = new Option<string>(new[] { "--comment", "-c" }) { Description = "Checkin comment (required)", IsRequired = true };
        var pathsArg = new Argument<string[]>("paths", () => new[] { "." }) { Description = "Files to check in (default: all pending)" };
        var dryRunOpt = new Option<bool>("--dry-run") { Description = "Show what would be checked in without committing" };
        var keepOpt = new Option<bool>("--keep-pending") { Description = "Keep changes in pending list after successful checkin" };

        cmd.AddOption(commentOpt);
        cmd.AddArgument(pathsArg);
        cmd.AddOption(dryRunOpt);
        cmd.AddOption(keepOpt);

        cmd.SetHandler(async (comment, paths, dryRun, keepPending) =>
        {
            var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
            if (ws is null) { Console.Error.WriteLine("No workspace found."); Environment.ExitCode = 1; return; }

            var meta = ws.LoadMetadata();
            var pending = ws.LoadPendingChanges().ToList();

            if (pending.Count == 0) { Console.WriteLine("Nothing to check in."); return; }

            // 按路径过滤：支持单个文件、目录前缀匹配，'.' 表示提交全部
            // Filter to requested paths
            IList<PendingChange> toCheckin;
            if (paths.Length == 1 && paths[0] == ".")
            {
                toCheckin = pending;
            }
            else
            {
                var absPaths = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                toCheckin = pending.Where(p =>
                    absPaths.Contains(p.LocalPath) ||
                    absPaths.Any(ap => p.LocalPath.StartsWith(ap + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                ).ToList();
            }

            if (toCheckin.Count == 0) { Console.WriteLine("No pending changes match the specified paths."); return; }

            Console.WriteLine("Pending changes to check in:");
            foreach (var c in toCheckin)
            {
                var rel = Path.GetRelativePath(Directory.GetCurrentDirectory(), c.LocalPath);
                Console.WriteLine($"  {c.ChangeType,-8}  {rel}");
            }
            Console.WriteLine();

            if (dryRun) { Console.WriteLine("[DRY RUN] No changes committed."); return; }

            // 读取 Add/Edit 文件内容；Delete 不需要内容，传 null 即可
            // Read file content for each change
            var changes = new List<(string serverPath, ChangeType changeType, byte[]? content, int? baseChangesetId)>();
            foreach (var change in toCheckin)
            {
                byte[]? content = null;
                int? baseChangesetId = null;
                if (change.ChangeType is ChangeType.Add or ChangeType.Edit)
                {
                    if (!File.Exists(change.LocalPath))
                    {
                        Console.Error.WriteLine($"  [ERROR] File not found: {change.LocalPath}");
                        Environment.ExitCode = 1;
                        return;
                    }
                    content = await File.ReadAllBytesAsync(change.LocalPath).ConfigureAwait(false);
                }

                if (change.ChangeType is not ChangeType.Add)
                {
                    var tracked = ws.GetTrackedVersion(change.LocalPath);
                    if (tracked is null)
                    {
                        Console.Error.WriteLine($"  [ERROR] No tracked server version found for: {change.LocalPath}");
                        Environment.ExitCode = 1;
                        return;
                    }

                    baseChangesetId = tracked.ChangesetId;
                }

                changes.Add((change.ServerPath, change.ChangeType, content, baseChangesetId));
            }

            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                var result = await svc.CheckinAsync(comment, changes).ConfigureAwait(false);
                Console.WriteLine($"Changeset {result.ChangesetId} created successfully.");
                Console.WriteLine($"  Author : {result.Author?.DisplayName}");
                Console.WriteLine($"  Date   : {result.CreatedDate}");
                Console.WriteLine($"  Comment: {result.Comment}");

                if (!keepPending)
                {
                    // 提交成功后更新版本追踪：
                    //   Delete → 移除版本快照
                    //   Add/Edit → 用新 ChangesetId + 当前文件哈希更新快照
                    // 然后移除对应的挂起变更记录
                    // Update tracking info and remove pending changes
                    foreach (var change in toCheckin)
                    {
                        if (change.ChangeType == ChangeType.Delete)
                        {
                            ws.RemoveTrackedVersion(change.LocalPath);
                        }
                        else if (File.Exists(change.LocalPath))
                        {
                            ws.SaveTrackedVersion(new TrackedFileVersion
                            {
                                ServerPath = change.ServerPath,
                                LocalPath = change.LocalPath,
                                ChangesetId = result.ChangesetId,
                                ContentHash = WorkspaceManager.ComputeFileHash(change.LocalPath),
                            });
                        }
                        ws.RemovePendingChange(change.LocalPath);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Checkin failed: {ex.Message}");
                if (ex.InnerException is not null)
                    Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
                Environment.ExitCode = 1;
            }
        }, commentOpt, pathsArg, dryRunOpt, keepOpt);

        return cmd;
    }
}
