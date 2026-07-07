using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs label — 查看 TFVC Labels。</summary>
public static class LabelCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("label", "List and inspect TFVC labels.");
        cmd.AddCommand(BuildList(config));
        cmd.AddCommand(BuildShow(config));
        cmd.AddCommand(BuildCreate(config));
        cmd.AddCommand(BuildDelete(config));
        return cmd;
    }

    private static Command BuildList(TfsConfig config)
    {
        var cmd = new Command("list", "List TFVC labels.");
        var ownerOpt = new Option<string?>("--owner", "Filter by owner");
        var nameOpt = new Option<string?>("--name", "Filter by label name");
        var scopeOpt = new Option<string?>("--scope", "Filter by label scope");
        var topOpt = new Option<int>("--top", () => 20) { Description = "Maximum labels to return" };
        var skipOpt = new Option<int>("--skip", () => 0) { Description = "Skip N labels" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };

        cmd.AddOption(ownerOpt);
        cmd.AddOption(nameOpt);
        cmd.AddOption(scopeOpt);
        cmd.AddOption(topOpt);
        cmd.AddOption(skipOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (owner, name, scope, top, skip, format) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                var labels = await svc.GetLabelsAsync(owner, name, scope, null, top, skip).ConfigureAwait(false);

                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "label.list",
                        query = new
                        {
                            owner,
                            name,
                            scope,
                            top,
                            skip,
                        },
                        items = labels.Select(ProjectLabelRef),
                    });
                    return;
                }

                if (labels.Count == 0)
                {
                    Console.WriteLine("No labels found.");
                    return;
                }

                Console.WriteLine($"{"ID",-8}  {"Name",-30}  {"Owner",-25}  {"Modified",-20}  Scope");
                Console.WriteLine($"{new string('-', 8)}  {new string('-', 30)}  {new string('-', 25)}  {new string('-', 20)}  {new string('-', 30)}");
                foreach (var label in labels.OrderByDescending(l => l.ModifiedDate))
                {
                    Console.WriteLine($"{label.Id,-8}  {label.Name,-30}  {(label.Owner?.DisplayName ?? string.Empty),-25}  {label.ModifiedDate.ToLocalTime():yyyy-MM-dd HH:mm,-20}  {label.LabelScope}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, ownerOpt, nameOpt, scopeOpt, topOpt, skipOpt, formatOpt);

        return cmd;
    }

    private static Command BuildShow(TfsConfig config)
    {
        var cmd = new Command("show", "Show a TFVC label and its items.");
        var idArg = new Argument<string>("id", "Label ID");
        var maxItemsOpt = new Option<int?>("--max-items") { Description = "Maximum items to include" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };

        cmd.AddArgument(idArg);
        cmd.AddOption(maxItemsOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (id, maxItems, format) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                var label = await svc.GetLabelAsync(id, maxItems).ConfigureAwait(false);

                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "label.show",
                        label = new
                        {
                            id = label.Id,
                            name = label.Name,
                            description = label.Description,
                            labelScope = label.LabelScope,
                            modifiedDate = label.ModifiedDate,
                            owner = JsonOutput.Identity(label.Owner),
                            items = label.Items?.Select(item => new
                            {
                                path = item.Path,
                                changesetVersion = item.ChangesetVersion,
                                isBranch = item.IsBranch,
                                deletionId = item.DeletionId,
                                changeDate = item.ChangeDate,
                                size = item.Size,
                                hashValue = item.HashValue,
                            }),
                        }
                    });
                    return;
                }

                Console.WriteLine($"ID         : {label.Id}");
                Console.WriteLine($"Name       : {label.Name}");
                Console.WriteLine($"Owner      : {label.Owner?.DisplayName}");
                Console.WriteLine($"Modified   : {label.ModifiedDate:u}");
                Console.WriteLine($"Scope      : {label.LabelScope}");
                Console.WriteLine($"Description: {label.Description}");
                Console.WriteLine($"Items      : {label.Items?.Count() ?? 0}");

                if (label.Items is not null)
                {
                    Console.WriteLine();
                    Console.WriteLine("Items:");
                    foreach (var item in label.Items)
                        Console.WriteLine($"  {item.Path}  (cs#{item.ChangesetVersion})");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, idArg, maxItemsOpt, formatOpt);

        return cmd;
    }

    private static object ProjectLabelRef(Microsoft.TeamFoundation.SourceControl.WebApi.TfvcLabelRef label) => new
    {
        id = label.Id,
        name = label.Name,
        description = label.Description,
        labelScope = label.LabelScope,
        modifiedDate = label.ModifiedDate,
        owner = JsonOutput.Identity(label.Owner),
    };

    private static Command BuildCreate(TfsConfig config)
    {
        var cmd = new Command("create", "Create a TFVC label on a server path.");
        var nameArg = new Argument<string>("name", "Label name");
        var pathArg = new Argument<string>("path", "Server path to label");
        var commentOpt = new Option<string?>("--comment", "-c") { Description = "Label description" };
        var versionOpt = new Option<int?>("--version", "-v") { Description = "Attach label at this changeset" };
        cmd.AddArgument(nameArg);
        cmd.AddArgument(pathArg);
        cmd.AddOption(commentOpt);
        cmd.AddOption(versionOpt);
        cmd.SetHandler(async (name, path, comment, version) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                var result = await svc.CreateLabelAsync(name, path, comment, version).ConfigureAwait(false);
                Console.WriteLine($"Label '{name}' created (result: {result}).");
            }
            catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); Environment.ExitCode = 1; }
        }, nameArg, pathArg, commentOpt, versionOpt);
        return cmd;
    }

    private static Command BuildDelete(TfsConfig config)
    {
        var cmd = new Command("delete", "Delete a TFVC label by ID or name.");
        var idArg = new Argument<string>("id", "Label ID (numeric) or label name");
        cmd.AddArgument(idArg);
        cmd.SetHandler(async (id) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                await svc.DeleteLabelAsync(id).ConfigureAwait(false);
                Console.WriteLine($"Label '{id}' deleted.");
            }
            catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); Environment.ExitCode = 1; }
        }, idArg);
        return cmd;
    }
}