using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs branch — 查询 TFVC 分支信息。</summary>
public static class BranchCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("branch", "List and inspect TFVC branches.");

        cmd.AddCommand(BuildList(config));
        cmd.AddCommand(BuildShow(config));
        cmd.AddCommand(BuildCreate(config));

        return cmd;
    }

    private static Command BuildCreate(TfsConfig config)
    {
        var cmd = new Command("create", "Create a TFVC branch from an existing branch.");
        var sourceOpt = new Option<string>("--source") { Description = "Source branch path ($/...)", IsRequired = true };
        var targetOpt = new Option<string>("--target") { Description = "New target branch path ($/...)", IsRequired = true };
        var versionOpt = new Option<int?>("--version") { Description = "Optional source changeset version" };
        var commentOpt = new Option<string?>("--comment") { Description = "Changeset comment" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };

        cmd.AddOption(sourceOpt);
        cmd.AddOption(targetOpt);
        cmd.AddOption(versionOpt);
        cmd.AddOption(commentOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (source, target, version, comment, format) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                var created = await svc.CreateBranchAsync(source, target, version, comment).ConfigureAwait(false);
                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "branch.create",
                        sourcePath = source,
                        targetPath = target,
                        sourceChangesetId = version,
                        createdChangesetId = created.ChangesetId,
                    });
                    return;
                }
                Console.WriteLine($"Created branch {target} from {source} in changeset {created.ChangesetId}.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, sourceOpt, targetOpt, versionOpt, commentOpt, formatOpt);

        return cmd;
    }

    private static Command BuildList(TfsConfig config)
    {
        var cmd = new Command("list", "List TFVC branches under a scope path.");
        var scopeArg = new Argument<string>("scope", () => "$/", "Server scope path (default: $/)");
        var includeDeletedOpt = new Option<bool>("--include-deleted") { Description = "Include deleted branches" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };

        cmd.AddArgument(scopeArg);
        cmd.AddOption(includeDeletedOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (scope, includeDeleted, format) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                var branches = await svc.GetBranchRefsAsync(scope, includeDeleted).ConfigureAwait(false);
                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "branch.list",
                        query = new
                        {
                            scope,
                            includeDeleted,
                        },
                        items = branches.Select(ProjectBranchRef),
                    });
                    return;
                }

                if (branches.Count == 0)
                {
                    Console.WriteLine("No branches found.");
                    return;
                }

                Console.WriteLine($"{"Path",-80}  {"Owner",-25}  {"Created",-20}  Deleted");
                Console.WriteLine($"{new string('-', 80)}  {new string('-', 25)}  {new string('-', 20)}  {new string('-', 7)}");
                foreach (var branch in branches.OrderBy(b => b.Path, StringComparer.OrdinalIgnoreCase))
                {
                    var owner = branch.Owner?.DisplayName ?? string.Empty;
                    var created = branch.CreatedDate == default ? string.Empty : branch.CreatedDate.ToString("yyyy-MM-dd HH:mm");
                    Console.WriteLine($"{branch.Path,-80}  {owner,-25}  {created,-20}  {(branch.IsDeleted ? "yes" : "no")}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, scopeArg, includeDeletedOpt, formatOpt);

        return cmd;
    }

    private static Command BuildShow(TfsConfig config)
    {
        var cmd = new Command("show", "Show details for a TFVC branch.");
        var pathArg = new Argument<string>("path") { Description = "Server branch path ($/...)" };
        var noChildrenOpt = new Option<bool>("--no-children") { Description = "Do not expand child branches" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };

        cmd.AddArgument(pathArg);
        cmd.AddOption(noChildrenOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (path, noChildren, format) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                var branch = await svc.GetBranchAsync(path, includeChildren: !noChildren).ConfigureAwait(false);
                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "branch.show",
                        branch = ProjectBranch(branch),
                    });
                    return;
                }

                Console.WriteLine($"Path       : {branch.Path}");
                Console.WriteLine($"Owner      : {branch.Owner?.DisplayName}");
                Console.WriteLine($"Created    : {branch.CreatedDate:u}");
                Console.WriteLine($"Deleted    : {(branch.IsDeleted ? "yes" : "no")}");
                Console.WriteLine($"Description: {branch.Description}");
                Console.WriteLine($"Parent     : {branch.Parent?.Path}");
                Console.WriteLine($"Children   : {branch.Children?.Count ?? 0}");

                if (branch.Mappings is { Count: > 0 })
                {
                    Console.WriteLine();
                    Console.WriteLine("Mappings:");
                    foreach (var mapping in branch.Mappings)
                        Console.WriteLine($"  {mapping.ServerItem,-70}  {mapping.Type,-10}  {mapping.Depth}");
                }

                if (branch.Children is { Count: > 0 })
                {
                    Console.WriteLine();
                    Console.WriteLine("Child branches:");
                    foreach (var child in branch.Children.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
                        Console.WriteLine($"  {child.Path}");
                }

                if (branch.RelatedBranches is { Count: > 0 })
                {
                    Console.WriteLine();
                    Console.WriteLine("Related branches:");
                    foreach (var related in branch.RelatedBranches.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
                        Console.WriteLine($"  {related.Path}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, pathArg, noChildrenOpt, formatOpt);

        return cmd;
    }

    private static object ProjectBranchRef(TfvcBranchRef branch) => new
    {
        path = branch.Path,
        description = branch.Description,
        owner = JsonOutput.Identity(branch.Owner),
        createdAt = branch.CreatedDate,
        isDeleted = branch.IsDeleted,
    };

    private static object ProjectBranch(TfvcBranch branch) => new
    {
        path = branch.Path,
        description = branch.Description,
        owner = JsonOutput.Identity(branch.Owner),
        createdAt = branch.CreatedDate,
        isDeleted = branch.IsDeleted,
        parentPath = branch.Parent?.Path,
        children = branch.Children?.Select(child => child.Path),
        relatedBranches = branch.RelatedBranches?.Select(related => related.Path),
        mappings = branch.Mappings?.Select(mapping => new
        {
            serverItem = mapping.ServerItem,
            type = mapping.Type,
            depth = mapping.Depth,
        }),
    };
}
