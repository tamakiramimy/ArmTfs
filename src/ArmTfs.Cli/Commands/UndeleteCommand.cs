using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs undelete &lt;path&gt; — 还原已删除的服务器文件/文件夹。</summary>
public static class UndeleteCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("undelete", "Restore a previously deleted file or folder on the TFVC server.");

        var pathArg = new Argument<string>("path", "Server path of the deleted item");
        var commentOpt = new Option<string?>("--comment", "-c") { Description = "Checkin comment" };
        var deletionIdOpt = new Option<int?>("--deletion-id") { Description = "Specific deletion ID (optional; omit to use the most recent deletion)" };

        cmd.AddArgument(pathArg);
        cmd.AddOption(commentOpt);
        cmd.AddOption(deletionIdOpt);

        cmd.SetHandler(async (path, comment, deletionId) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                var result = await svc.UndeleteItemAsync(path, comment, deletionId).ConfigureAwait(false);
                Console.WriteLine($"Undeleted. Changeset {result.ChangesetId} created.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, pathArg, commentOpt, deletionIdOpt);

        return cmd;
    }
}
