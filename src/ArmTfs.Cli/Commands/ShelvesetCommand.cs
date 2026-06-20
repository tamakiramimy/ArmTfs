using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs shelveset — Shelveset 查询</summary>
public static class ShelvesetCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("shelveset", "List and inspect TFVC shelvesets.");
        cmd.AddAlias("shelve");

        cmd.AddCommand(BuildList(config));
        cmd.AddCommand(BuildShow(config));
        cmd.AddCommand(BuildCreate(config));
        cmd.AddCommand(BuildUnshelve(config));
        cmd.AddCommand(BuildDelete(config));

        return cmd;
    }

    private static Command BuildList(TfsConfig config)
    {
        var cmd = new Command("list", "List available shelvesets.");

        var ownerOpt = new Option<string?>("--owner", "-o") { Description = "Filter by owner (default: all)" };
        var nameOpt = new Option<string?>("--name", "-n") { Description = "Filter by shelveset name pattern" };

        cmd.AddOption(ownerOpt);
        cmd.AddOption(nameOpt);

        cmd.SetHandler(async (owner, name) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                var shelvesets = await svc.GetShelvesetsAsync(owner, name).ConfigureAwait(false);

                if (!shelvesets.Any()) { Console.WriteLine("No shelvesets found."); return; }

                Console.WriteLine($"{"Name",-40}  {"Owner",-25}  {"Date",-22}  Comment");
                Console.WriteLine($"{new string('-', 40)}  {new string('-', 25)}  {new string('-', 22)}  {new string('-', 40)}");

                foreach (var ss in shelvesets.OrderByDescending(s => s.CreatedDate))
                {
                    var ownerName = (ss.Owner?.DisplayName ?? "").PadRight(25);
                    var date = ss.CreatedDate.ToString("yyyy-MM-dd HH:mm");
                    var comment = (ss.Comment ?? "").Replace('\n', ' ');
                    if (comment.Length > 50) comment = comment[..47] + "...";
                    Console.WriteLine($"{ss.Name,-40}  {ownerName,-25}  {date,-22}  {comment}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, ownerOpt, nameOpt);

        return cmd;
    }

    private static Command BuildShow(TfsConfig config)
    {
        var cmd = new Command("show", "Show details and file changes in a shelveset.");

        var nameArg = new Argument<string>("name") { Description = "Shelveset name (use 'name;owner' to specify owner)" };

        cmd.AddArgument(nameArg);

        cmd.SetHandler(async (name) =>
        {
            // Parse "name;owner" format
            string shelvesetName = name, owner = "";
            var semicolonIdx = name.IndexOf(';');
            if (semicolonIdx >= 0)
            {
                shelvesetName = name[..semicolonIdx];
                owner = name[(semicolonIdx + 1)..];
            }

            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                var shelvesets = await svc.GetShelvesetsAsync(
                    string.IsNullOrEmpty(owner) ? null : owner,
                    shelvesetName).ConfigureAwait(false);

                var ss = shelvesets.FirstOrDefault();
                if (ss is null) { Console.Error.WriteLine($"Shelveset '{name}' not found."); Environment.ExitCode = 1; return; }

                Console.WriteLine($"Name    : {ss.Name}");
                Console.WriteLine($"Owner   : {ss.Owner?.DisplayName}");
                Console.WriteLine($"Date    : {ss.CreatedDate:u}");
                Console.WriteLine($"Comment : {ss.Comment}");
                Console.WriteLine();

                var resolvedOwner = string.IsNullOrEmpty(owner)
                    ? ss.Owner?.UniqueName
                    : owner;
                var changes = await svc.GetShelvesetChangesAsync(ss.Name, resolvedOwner).ConfigureAwait(false);

                Console.WriteLine($"Files ({changes.Count}):");
                foreach (var change in changes)
                {
                    Console.WriteLine($"  {change.ChangeType,-15}  {change.Item?.Path}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, nameArg);

        return cmd;
    }

    private static Command BuildCreate(TfsConfig config)
    {
        var cmd = new Command("create", "Create a shelveset from pending changes (local workspace).");

        var nameArg = new Argument<string>("name", "Shelveset name");
        var pathsArg = new Argument<string[]>("paths", () => new[] { "." }) { Description = "Local files to shelve (defaults to all pending)", Arity = ArgumentArity.ZeroOrMore };
        var commentOpt = new Option<string?>("--comment", "-c") { Description = "Shelveset comment" };
        var replaceOpt = new Option<bool>("--replace") { Description = "Replace existing shelveset with the same name" };
        var ownerOpt = new Option<string?>("--soap-owner") { Description = "Override SOAP owner (GUID or DOMAIN\\\\user)" };

        cmd.AddArgument(nameArg);
        cmd.AddArgument(pathsArg);
        cmd.AddOption(commentOpt);
        cmd.AddOption(replaceOpt);
        cmd.AddOption(ownerOpt);

        cmd.SetHandler(async (name, paths, comment, replace, soapOwner) =>
        {
            var ws = ArmTfs.Core.Workspace.WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
            if (ws is null) { Console.Error.WriteLine("No workspace found."); Environment.ExitCode = 1; return; }

            var pending = ws.LoadPendingChanges().ToList();
            if (pending.Count == 0) { Console.WriteLine("Nothing pending to shelve."); return; }

            // Filter by paths
            IList<ArmTfs.Core.Models.PendingChange> toShelve;
            if (paths.Length == 0 || (paths.Length == 1 && paths[0] == "."))
                toShelve = pending;
            else
            {
                var absPaths = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                toShelve = pending.Where(p =>
                    absPaths.Contains(p.LocalPath) ||
                    absPaths.Any(ap => p.LocalPath.StartsWith(ap + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                ).ToList();
            }

            if (toShelve.Count == 0) { Console.WriteLine("No pending changes match the specified paths."); return; }

            // Build changes list
            var changes = new List<(string serverPath, ArmTfs.Core.Models.ChangeType changeType, byte[]? content, int? baseVersion)>();
            foreach (var c in toShelve)
            {
                byte[]? content = null;
                int? baseVersion = null;
                if (c.ChangeType is ArmTfs.Core.Models.ChangeType.Add or ArmTfs.Core.Models.ChangeType.Edit)
                {
                    if (File.Exists(c.LocalPath))
                        content = await File.ReadAllBytesAsync(c.LocalPath).ConfigureAwait(false);
                }
                if (c.ChangeType is not ArmTfs.Core.Models.ChangeType.Add)
                {
                    var tracked = ws.GetTrackedVersion(c.LocalPath);
                    baseVersion = tracked?.ChangesetId;
                }
                changes.Add((c.ServerPath, c.ChangeType, content, baseVersion));
            }

            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                await svc.CreateShelvesetAsync(name, changes, comment, replace, soapOwner).ConfigureAwait(false);
                Console.WriteLine($"Shelveset '{name}' created with {changes.Count} change(s).");
            }
            catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); Environment.ExitCode = 1; }
        }, nameArg, pathsArg, commentOpt, replaceOpt, ownerOpt);

        return cmd;
    }

    private static Command BuildUnshelve(TfsConfig config)
    {
        var cmd = new Command("unshelve", "Apply a shelveset's changes to the local workspace.");

        var nameArg = new Argument<string>("name", "Shelveset name (use 'name;owner' to specify owner)");
        cmd.AddArgument(nameArg);

        cmd.SetHandler(async (name) =>
        {
            string shelvesetName = name, owner = "";
            var semi = name.IndexOf(';');
            if (semi >= 0) { shelvesetName = name[..semi]; owner = name[(semi + 1)..]; }

            var ws = ArmTfs.Core.Workspace.WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
            if (ws is null) { Console.Error.WriteLine("No workspace found."); Environment.ExitCode = 1; return; }

            var meta = ws.LoadMetadata();
            var mapping = meta.Mappings.FirstOrDefault();
            if (mapping is null) { Console.Error.WriteLine("No workspace mapping."); Environment.ExitCode = 1; return; }

            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                var written = await svc.UnshelveAsync(
                    shelvesetName,
                    string.IsNullOrEmpty(owner) ? null : owner,
                    mapping.LocalPath,
                    mapping.ServerPath).ConfigureAwait(false);

                Console.WriteLine($"Unshelved {written.Count} file(s):");
                foreach (var f in written) Console.WriteLine($"  {f}");
            }
            catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); Environment.ExitCode = 1; }
        }, nameArg);

        return cmd;
    }

    private static Command BuildDelete(TfsConfig config)
    {
        var cmd = new Command("delete", "Delete a shelveset from the server.");

        var nameArg = new Argument<string>("name", "Shelveset name (use 'name;owner' to specify owner)");
        var ownerOpt = new Option<string?>("--owner", "-o") { Description = "Owner of the shelveset" };
        var soapOwnerOpt = new Option<string?>("--soap-owner") { Description = "Override SOAP owner (GUID)" };

        cmd.AddArgument(nameArg);
        cmd.AddOption(ownerOpt);
        cmd.AddOption(soapOwnerOpt);

        cmd.SetHandler(async (name, owner, soapOwner) =>
        {
            string shelvesetName = name, resolvedOwner = owner ?? "";
            var semi = name.IndexOf(';');
            if (semi >= 0) { shelvesetName = name[..semi]; if (string.IsNullOrEmpty(resolvedOwner)) resolvedOwner = name[(semi + 1)..]; }

            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                await svc.DeleteShelvesetAsync(shelvesetName, resolvedOwner, soapOwner).ConfigureAwait(false);
                Console.WriteLine($"Shelveset '{shelvesetName}' deleted.");
            }
            catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); Environment.ExitCode = 1; }
        }, nameArg, ownerOpt, soapOwnerOpt);

        return cmd;
    }
}
