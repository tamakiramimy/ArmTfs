using System.CommandLine;
using ArmTfs.Core.Config;
using ArmTfs.Core.Models;
using ArmTfs.Core.Workspace;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs workspace — 工作区管理</summary>
public static class WorkspaceCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("workspace", "Manage TFVC local workspaces.");

        cmd.AddCommand(BuildNew());
        cmd.AddCommand(BuildShow());
        cmd.AddCommand(BuildMap());

        return cmd;

        // ─── workspace new ─────────────────────────────────────────────────────
        static Command BuildNew()
        {
            var cmd = new Command("new", "Create a new local workspace in the current (or specified) directory.");
            var nameOpt = new Option<string>("--name", "-n") { Description = "Workspace name", IsRequired = true };
            var serverOpt = new Option<string>("--server-path", "-s") { Description = "TFS server path (e.g. $/Project/Main)", IsRequired = true };
            var localOpt = new Option<string>("--local-path", "-l") { Description = "Local directory to map (defaults to current directory)" };
            var dirArg = new Argument<string>("directory", () => ".", "Root directory for the workspace");

            cmd.AddOption(nameOpt);
            cmd.AddOption(serverOpt);
            cmd.AddOption(localOpt);
            cmd.AddArgument(dirArg);

            cmd.SetHandler((name, serverPath, localPath, dir) =>
            {
                var resolvedDir = Path.GetFullPath(dir);
                var resolvedLocal = string.IsNullOrEmpty(localPath) ? resolvedDir : Path.GetFullPath(localPath);

                var ws = new WorkspaceManager(resolvedDir);
                if (ws.Exists)
                {
                    Console.Error.WriteLine($"A workspace already exists at '{resolvedDir}'.");
                    Environment.ExitCode = 1;
                    return;
                }

                var meta = new WorkspaceMetadata
                {
                    Name = name,
                    ServerCollectionUrl = "pending", // will be updated on first get
                    Mappings = new List<WorkspaceMapping>
                    {
                        new() { ServerPath = serverPath, LocalPath = resolvedLocal }
                    }
                };

                ws.SaveMetadata(meta);
                Console.WriteLine($"Workspace '{name}' created at '{resolvedDir}'");
                Console.WriteLine($"  Mapping: {serverPath} -> {resolvedLocal}");
            }, nameOpt, serverOpt, localOpt, dirArg);

            return cmd;
        }

        // ─── workspace show ────────────────────────────────────────────────────
        static Command BuildShow()
        {
            var cmd = new Command("show", "Show workspace information for the current directory.");

            cmd.SetHandler(() =>
            {
                var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
                if (ws is null)
                {
                    Console.Error.WriteLine("No workspace found in current directory tree.");
                    Environment.ExitCode = 1;
                    return;
                }
                var meta = ws.LoadMetadata();
                Console.WriteLine($"Workspace : {meta.Name}");
                Console.WriteLine($"Server    : {meta.ServerCollectionUrl}");
                Console.WriteLine($"Created   : {meta.CreatedAt:u}");
                Console.WriteLine();
                Console.WriteLine("Mappings:");
                foreach (var m in meta.Mappings)
                    Console.WriteLine($"  {m.ServerPath,-40} -> {m.LocalPath}");
            });

            return cmd;
        }

        // ─── workspace map ─────────────────────────────────────────────────────
        static Command BuildMap()
        {
            var cmd = new Command("map", "Add an additional path mapping to the workspace.");
            var serverOpt = new Option<string>("--server-path", "-s") { IsRequired = true };
            var localOpt = new Option<string>("--local-path", "-l") { IsRequired = true };

            cmd.AddOption(serverOpt);
            cmd.AddOption(localOpt);

            cmd.SetHandler((serverPath, localPath) =>
            {
                var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
                if (ws is null) { Console.Error.WriteLine("No workspace found."); Environment.ExitCode = 1; return; }

                var meta = ws.LoadMetadata();
                meta.Mappings.Add(new WorkspaceMapping
                {
                    ServerPath = serverPath,
                    LocalPath = Path.GetFullPath(localPath),
                });
                ws.SaveMetadata(meta);
                Console.WriteLine($"Mapping added: {serverPath} -> {Path.GetFullPath(localPath)}");
            }, serverOpt, localOpt);

            return cmd;
        }
    }
}
