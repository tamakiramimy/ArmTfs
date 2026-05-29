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
}
