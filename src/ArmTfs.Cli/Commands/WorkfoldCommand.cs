using System.CommandLine;
using ArmTfs.Core.Config;
using ArmTfs.Core.Models;
using ArmTfs.Core.Workspace;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs workfold — 管理工作区路径映射（包括 cloak）。</summary>
public static class WorkfoldCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("workfold", "Manage workspace folder mappings (map, cloak, uncloak).");

        cmd.AddCommand(BuildCloak());
        cmd.AddCommand(BuildUncloak());
        cmd.AddCommand(BuildList());

        return cmd;
    }

    private static Command BuildCloak()
    {
        var cmd = new Command("cloak", "Mark a server path as cloaked — it will be skipped during 'get'.");
        var pathArg = new Argument<string>("server-path", "Server path to cloak (e.g. $/Project/Docs)");
        cmd.AddArgument(pathArg);

        cmd.SetHandler((serverPath) =>
        {
            var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
            if (ws is null) { Console.Error.WriteLine("No workspace found."); Environment.ExitCode = 1; return; }

            var meta = ws.LoadMetadata();
            var normalized = serverPath.TrimEnd('/');

            if (meta.CloakedPaths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"'{normalized}' is already cloaked.");
                return;
            }

            meta.CloakedPaths.Add(normalized);
            ws.SaveMetadata(meta);
            Console.WriteLine($"Cloaked: {normalized}");
        }, pathArg);

        return cmd;
    }

    private static Command BuildUncloak()
    {
        var cmd = new Command("uncloak", "Remove a cloak from a server path, restoring normal mapping.");
        var pathArg = new Argument<string>("server-path", "Server path to uncloak");
        cmd.AddArgument(pathArg);

        cmd.SetHandler((serverPath) =>
        {
            var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
            if (ws is null) { Console.Error.WriteLine("No workspace found."); Environment.ExitCode = 1; return; }

            var meta = ws.LoadMetadata();
            var normalized = serverPath.TrimEnd('/');
            var removed = meta.CloakedPaths.RemoveAll(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));

            if (removed == 0)
            {
                Console.Error.WriteLine($"'{normalized}' is not cloaked.");
                Environment.ExitCode = 1;
                return;
            }

            ws.SaveMetadata(meta);
            Console.WriteLine($"Uncloaked: {normalized}");
        }, pathArg);

        return cmd;
    }

    private static Command BuildList()
    {
        var cmd = new Command("list", "List all workspace path mappings and cloaked paths.");

        cmd.SetHandler(() =>
        {
            var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
            if (ws is null) { Console.Error.WriteLine("No workspace found."); Environment.ExitCode = 1; return; }

            var meta = ws.LoadMetadata();

            Console.WriteLine("Mappings:");
            foreach (var m in meta.Mappings)
                Console.WriteLine($"  MAP    {m.ServerPath,-40} -> {m.LocalPath}");

            if (meta.CloakedPaths.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Cloaked:");
                foreach (var c in meta.CloakedPaths)
                    Console.WriteLine($"  CLOAK  {c}");
            }
        });

        return cmd;
    }
}
