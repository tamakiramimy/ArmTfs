using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs rollback &lt;changesetId&gt; — 回滚指定 changeset 的内容变更。</summary>
public static class RollbackCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("rollback", "Rollback changes introduced by a specific changeset (creates a new inverse changeset).");

        var changesetArg = new Argument<int>("changeset-id", "The changeset ID to roll back");
        var commentOpt = new Option<string?>("--comment", "-c") { Description = "Checkin comment (defaults to 'Rollback changeset N')" };

        cmd.AddArgument(changesetArg);
        cmd.AddOption(commentOpt);

        cmd.SetHandler(async (changesetId, comment) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                Console.WriteLine($"Rolling back changeset {changesetId}...");
                var result = await svc.RollbackChangesetAsync(changesetId, comment).ConfigureAwait(false);
                Console.WriteLine($"Rollback complete. New changeset {result.ChangesetId} created.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, changesetArg, commentOpt);

        return cmd;
    }
}
