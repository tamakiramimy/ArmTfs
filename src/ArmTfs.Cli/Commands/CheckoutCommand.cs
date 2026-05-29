using System.CommandLine;
using ArmTfs.Core.Config;
using ArmTfs.Core.Models;
using ArmTfs.Core.Workspace;

namespace ArmTfs.Cli.Commands;

/// <summary>
/// arm-tfs checkout [files...] — 将文件标记为挂起编辑
///
/// 与服务器工作区（Server Workspace）不同，本工具使用本地工作区模式：
/// checkout 是纯本地操作，不向服务器发送任何请求。
/// 文件已存在于磁盘即可 checkout。
/// </summary>
public static class CheckoutCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("checkout", "Mark files as pending edit (local workspace mode).");
        cmd.AddAlias("co");
        cmd.AddAlias("edit");

        var pathsArg = new Argument<string[]>("paths") { Description = "Files or directories to check out for edit", Arity = ArgumentArity.OneOrMore };
        var recursiveOpt = new Option<bool>("--recursive", "-r") { Description = "Include all files under directory" };

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

                IEnumerable<string> filesToCheckout;
                if (Directory.Exists(absPath))
                {
                    filesToCheckout = recursive
                        ? Directory.EnumerateFiles(absPath, "*", SearchOption.AllDirectories)
                            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".tf" + Path.DirectorySeparatorChar))
                        : Array.Empty<string>();
                }
                else if (File.Exists(absPath))
                {
                    filesToCheckout = new[] { absPath };
                }
                else
                {
                    Console.Error.WriteLine($"  [NOT FOUND] {rawPath}");
                    continue;
                }

                foreach (var file in filesToCheckout)
                {
                    var serverPath = ws.LocalToServerPath(file, meta);
                    if (serverPath is null)
                    {
                        Console.Error.WriteLine($"  [NO MAPPING] {file}");
                        continue;
                    }

                    var existing = ws.LoadPendingChanges()
                        .FirstOrDefault(p => string.Equals(p.LocalPath, file, StringComparison.OrdinalIgnoreCase));

                    if (existing is not null)
                    {
                        Console.WriteLine($"  [ALREADY PENDING] {file}  ({existing.ChangeType})");
                        continue;
                    }

                    ws.AddPendingChange(new PendingChange
                    {
                        ServerPath = serverPath,
                        LocalPath = file,
                        ChangeType = ChangeType.Edit,
                        ContentHash = File.Exists(file) ? WorkspaceManager.ComputeFileHash(file) : null,
                    });
                    Console.WriteLine($"  [EDIT] {file}");
                }
            }
        }, pathsArg, recursiveOpt);

        return cmd;
    }
}
