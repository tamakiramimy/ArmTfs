using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs changeset — 查看 Changeset 详情。</summary>
public static class ChangesetCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("changeset", "Inspect TFVC changesets.");
        cmd.AddCommand(BuildShow(config));
        return cmd;
    }

    private static Command BuildShow(TfsConfig config)
    {
        var cmd = new Command("show", "Show details for a TFVC changeset.");
        var idArg = new Argument<int>("id", "Changeset ID");
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };

        cmd.AddArgument(idArg);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (id, format) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                var changeset = await svc.GetChangesetAsync(id).ConfigureAwait(false);

                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "changeset.show",
                        changeset = ProjectChangeset(changeset),
                    });
                    return;
                }

                Console.WriteLine($"Changeset : {changeset.ChangesetId}");
                Console.WriteLine($"Author    : {changeset.Author?.DisplayName}");
                Console.WriteLine($"CheckedIn : {changeset.CheckedInBy?.DisplayName}");
                Console.WriteLine($"Created   : {changeset.CreatedDate.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"Comment   : {changeset.Comment}");
                Console.WriteLine($"Changes   : {changeset.Changes?.Count() ?? 0}");

                if (changeset.Changes?.Any() == true)
                {
                    Console.WriteLine();
                    Console.WriteLine("Files:");
                    foreach (var change in changeset.Changes)
                        Console.WriteLine($"  {change.ChangeType,-20}  {change.Item?.Path}");
                }

                if (changeset.WorkItems?.Any() == true)
                {
                    Console.WriteLine();
                    Console.WriteLine("Work items:");
                    foreach (var workItem in changeset.WorkItems)
                        Console.WriteLine($"  #{workItem.Id,-8}  {workItem.Title}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, idArg, formatOpt);

        return cmd;
    }

    internal static object ProjectChangeset(TfvcChangeset changeset) => new
    {
        changesetId = changeset.ChangesetId,
        createdAt = changeset.CreatedDate,
        comment = changeset.Comment,
        commentTruncated = changeset.CommentTruncated,
        author = JsonOutput.Identity(changeset.Author),
        checkedInBy = JsonOutput.Identity(changeset.CheckedInBy),
        hasMoreChanges = changeset.HasMoreChanges,
        changes = changeset.Changes?.Select(change => new
        {
            changeType = change.ChangeType.ToString(),
            item = change.Item is null ? null : new
            {
                path = change.Item.Path,
                changesetVersion = change.Item.ChangesetVersion,
                deletionId = change.Item.DeletionId,
                isBranch = change.Item.IsBranch,
                changeDate = change.Item.ChangeDate,
                size = change.Item.Size,
                hashValue = change.Item.HashValue,
            },
            pendingVersion = change.PendingVersion,
            mergeSources = change.MergeSources?.Select(source => new
            {
                serverItem = source.ServerItem,
                versionFrom = source.VersionFrom,
                versionTo = source.VersionTo,
                isRename = source.IsRename,
            }),
        }),
        workItems = changeset.WorkItems?.Select(workItem => new
        {
            id = workItem.Id,
            title = workItem.Title,
            state = workItem.State,
            assignedTo = workItem.AssignedTo,
            workItemType = workItem.WorkItemType,
            url = workItem.Url,
        }),
        checkinNotes = changeset.CheckinNotes?.Select(note => new
        {
            name = note.Name,
            value = note.Value,
        }),
        policyOverride = changeset.PolicyOverride is null ? null : new
        {
            comment = changeset.PolicyOverride.Comment,
            policyFailures = changeset.PolicyOverride.PolicyFailures?.Select(failure => new
            {
                policyName = failure.PolicyName,
                message = failure.Message,
            }),
        },
        teamProjectIds = changeset.TeamProjectIds,
    };
}