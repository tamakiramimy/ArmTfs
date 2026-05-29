using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;
using ArmTfs.Core.Models;
using ArmTfs.Core.Workspace;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs undo [files...] — 撤销挂起变更，恢复文件到服务器版本</summary>
public static class UndoCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("undo", "Undo pending changes and restore files to their server version.");

        var pathsArg = new Argument<string[]>("paths") { Description = "Files or '.' for all pending changes", Arity = ArgumentArity.OneOrMore };
        var noRestoreOpt = new Option<bool>("--no-restore") { Description = "Remove from pending list without restoring file content" };

        cmd.AddArgument(pathsArg);
        cmd.AddOption(noRestoreOpt);

        cmd.SetHandler(async (paths, noRestore) =>
        {
            var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
            if (ws is null) { Console.Error.WriteLine("No workspace found."); Environment.ExitCode = 1; return; }

            var meta = ws.LoadMetadata();
            var pending = ws.LoadPendingChanges().ToList();

            if (pending.Count == 0) { Console.WriteLine("No pending changes to undo."); return; }

            IEnumerable<PendingChange> toUndo;
            if (paths.Length == 1 && paths[0] == ".")
            {
                toUndo = pending;
            }
            else
            {
                var absPaths = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                toUndo = pending.Where(p => absPaths.Contains(p.LocalPath));
            }

            using var conn = new TfsConnection(config);
            var svc = new Core.Client.TfvcClientService(conn);

            foreach (var change in toUndo.ToList())
            {
                if (!noRestore && change.ChangeType != ChangeType.Add)
                {
                    // Restore from server
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(change.LocalPath)!);
                        await using var fs = File.Create(change.LocalPath);
                        await svc.DownloadFileAsync(change.ServerPath, fs).ConfigureAwait(false);
                        Console.WriteLine($"  [RESTORED] {change.LocalPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  [ERROR] Could not restore {change.LocalPath}: {ex.Message}");
                    }
                }
                else if (change.ChangeType == ChangeType.Add && !noRestore)
                {
                    // For adds, just remove from pending — don't delete the local file
                    Console.WriteLine($"  [UNDO ADD] {change.LocalPath}  (local file kept)");
                }

                ws.RemovePendingChange(change.LocalPath);
            }
        }, pathsArg, noRestoreOpt);

        return cmd;
    }
}
