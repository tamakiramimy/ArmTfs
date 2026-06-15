using System.CommandLine;
using ArmTfs.Core.Config;
using ArmTfs.Core.Models;
using ArmTfs.Core.Workspace;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs status — 显示挂起变更和本地修改</summary>
public static class StatusCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("status", "Show pending changes and locally modified files.");

        var pathArg = new Argument<string>("path", () => ".", "Directory or file to check");
        var allOpt = new Option<bool>("--all", "-a") { Description = "Also show tracked files that are locally modified (not yet checked out)" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };

        cmd.AddArgument(pathArg);
        cmd.AddOption(allOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler((path, all, format) =>
        {
            var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
            if (ws is null) { Console.Error.WriteLine("No workspace found."); Environment.ExitCode = 1; return; }

            var meta = ws.LoadMetadata();
            var pending = ws.LoadPendingChanges();

            // Filter by path if provided
            var absPath = Path.GetFullPath(path);
            var filtered = pending.Where(p =>
                p.LocalPath.StartsWith(absPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.LocalPath, absPath, StringComparison.OrdinalIgnoreCase)).ToList();

            var modifiedItems = new List<object>();
            if (all)
            {
                var checkedOut = filtered.Select(p => p.LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var file in EnumerateTrackedFiles(absPath))
                {
                    if (checkedOut.Contains(file)) continue;
                    var tracked = ws.GetTrackedVersion(file);
                    if (tracked is null) continue;
                    if (!File.Exists(file)) continue;

                    var currentHash = WorkspaceManager.ComputeFileHash(file);
                    if (!string.Equals(currentHash, tracked.ContentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        modifiedItems.Add(new
                        {
                            state = "modifiedNotCheckedOut",
                            serverPath = tracked.ServerPath,
                            localPath = file,
                            trackedChangesetId = tracked.ChangesetId,
                            downloadedAt = tracked.DownloadedAt,
                        });
                    }
                }
            }

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                var pendingItems = filtered.OrderBy(p => p.LocalPath).Select(pc =>
                {
                    var tracked = ws.GetTrackedVersion(pc.LocalPath);
                    return (object)new
                    {
                        state = "pending",
                        changeType = JsonOutput.EnumValue(pc.ChangeType),
                        serverPath = pc.ServerPath,
                        localPath = pc.LocalPath,
                        addedAt = pc.AddedAt,
                        sourceServerPath = pc.SourceServerPath,
                        baselineChangesetId = tracked?.ChangesetId,
                        downloadedAt = tracked?.DownloadedAt,
                    };
                });

                JsonOutput.Write(new
                {
                    schemaVersion = 1,
                    command = "status",
                    workspace = JsonOutput.Workspace(meta),
                    scope = new
                    {
                        inputPath = path,
                        resolvedLocalPath = absPath,
                    },
                    items = pendingItems.Concat(modifiedItems),
                });
                return;
            }

            if (filtered.Count == 0 && !all)
            {
                Console.WriteLine("No pending changes.");
                return;
            }

            if (filtered.Count > 0)
            {
                Console.WriteLine("Pending changes:");
                Console.WriteLine($"  {"Change",-10}  {"File",-60}  {"Server Path"}");
                Console.WriteLine($"  {new string('-', 10)}  {new string('-', 60)}  {new string('-', 40)}");

                foreach (var pc in filtered.OrderBy(p => p.LocalPath))
                {
                    var changeLabel = pc.ChangeType.ToString().ToUpperInvariant();
                    var relLocal = Path.GetRelativePath(Directory.GetCurrentDirectory(), pc.LocalPath);
                    Console.WriteLine($"  {changeLabel,-10}  {relLocal,-60}  {pc.ServerPath}");
                }
            }

            // Show locally modified (edited without checkout) files
            if (all)
            {
                Console.WriteLine();
                Console.WriteLine("Locally modified (not checked out):");
                bool found = false;
                foreach (var item in modifiedItems.Cast<dynamic>())
                {
                    var rel = Path.GetRelativePath(Directory.GetCurrentDirectory(), (string)item.localPath);
                    Console.WriteLine($"  [MODIFIED] {rel}");
                    found = true;
                }

                if (!found)
                    Console.WriteLine("  (none)");
            }
        }, pathArg, allOpt, formatOpt);

        return cmd;
    }

    private static IEnumerable<string> EnumerateTrackedFiles(string path)
    {
        if (File.Exists(path)) { yield return path; yield break; }
        if (!Directory.Exists(path)) yield break;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Contains(".tf")) continue; // skip .tf metadata
            yield return file;
        }
    }
}
