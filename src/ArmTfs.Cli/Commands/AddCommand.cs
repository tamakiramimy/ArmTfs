using System.CommandLine;
using ArmTfs.Core.Config;
using ArmTfs.Core.Models;
using ArmTfs.Core.Workspace;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs add [files...] — 将新文件标记为挂起新增</summary>
public static class AddCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("add", "Mark new files as pending add.");

        var pathsArg = new Argument<string[]>("paths") { Description = "Files to add to source control", Arity = ArgumentArity.OneOrMore };
        var recursiveOpt = new Option<bool>("--recursive", "-r") { Description = "Add all files under directory recursively" };

        cmd.AddArgument(pathsArg);
        cmd.AddOption(recursiveOpt);

        cmd.SetHandler((paths, recursive) =>
        {
            var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
            if (ws is null) { Console.Error.WriteLine("No workspace found."); Environment.ExitCode = 1; return; }

            var meta = ws.LoadMetadata();

            foreach (var rawPath in paths)
            {
                var absPath = Path.GetFullPath(rawPath);

                IEnumerable<string> filesToAdd;
                if (Directory.Exists(absPath))
                {
                    filesToAdd = recursive
                        ? Directory.EnumerateFiles(absPath, "*", SearchOption.AllDirectories)
                            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".tf" + Path.DirectorySeparatorChar))
                        : Array.Empty<string>();
                }
                else if (File.Exists(absPath))
                {
                    filesToAdd = new[] { absPath };
                }
                else
                {
                    Console.Error.WriteLine($"  [NOT FOUND] {rawPath}");
                    continue;
                }

                foreach (var file in filesToAdd)
                {
                    var serverPath = ws.LocalToServerPath(file, meta);
                    if (serverPath is null)
                    {
                        Console.Error.WriteLine($"  [NO MAPPING] {file}");
                        continue;
                    }

                    var tracked = ws.GetTrackedVersion(file);
                    if (tracked is not null)
                    {
                        Console.Error.WriteLine($"  [ALREADY TRACKED] {file} — use 'checkout' to edit existing files");
                        continue;
                    }

                    ws.AddPendingChange(new PendingChange
                    {
                        ServerPath = serverPath,
                        LocalPath = file,
                        ChangeType = ChangeType.Add,
                        ContentHash = WorkspaceManager.ComputeFileHash(file),
                    });
                    Console.WriteLine($"  [ADD] {file}");
                }
            }
        }, pathsArg, recursiveOpt);

        return cmd;
    }
}
