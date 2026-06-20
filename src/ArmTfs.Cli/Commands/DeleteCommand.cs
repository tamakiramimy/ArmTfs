using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs delete &lt;path&gt; — 删除服务器文件或文件夹（可撤销删除）。</summary>
public static class DeleteCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("delete", "Delete a file or folder from the TFVC server (recoverable).");
        cmd.AddAlias("del");

        var pathArg = new Argument<string>("path", "Server path to delete (e.g. $/Project/src/File.cs)");
        var commentOpt = new Option<string?>("--comment", "-c") { Description = "Checkin comment" };

        cmd.AddArgument(pathArg);
        cmd.AddOption(commentOpt);

        cmd.SetHandler(async (path, comment) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                var result = await svc.DeleteItemAsync(path, comment).ConfigureAwait(false);
                Console.WriteLine($"Deleted. Changeset {result.ChangesetId} created.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, pathArg, commentOpt);

        return cmd;
    }
}
