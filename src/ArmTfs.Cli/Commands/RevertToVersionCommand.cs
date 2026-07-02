using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs revert-to-version &lt;server-path&gt; &lt;changeset-id&gt; — 将服务器路径原子回退到指定版本快照。</summary>
public static class RevertToVersionCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("revert-to-version", "Revert a server path to the state at a specific changeset version (atomic snapshot restore).");

        var pathArg = new Argument<string>("server-path", "The server path to revert (e.g. $/Project/Branch)");
        var versionArg = new Argument<int>("changeset-id", "The target changeset version to revert to");
        var commentOpt = new Option<string?>("--comment", "-c") { Description = "Checkin comment" };

        cmd.AddArgument(pathArg);
        cmd.AddArgument(versionArg);
        cmd.AddOption(commentOpt);

        cmd.SetHandler(async (serverPath, changesetId, comment) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                var effectiveComment = string.IsNullOrWhiteSpace(comment)
                    ? $"Revert {serverPath} to version cs{changesetId}"
                    : comment.Trim();

                Console.WriteLine($"Reverting {serverPath} to cs{changesetId}...");
                Console.WriteLine("Comparing current vs target version...");

                var result = await svc.RevertToVersionAsync(serverPath, changesetId, effectiveComment).ConfigureAwait(false);
                Console.WriteLine($"Revert complete. New changeset {result.ChangesetId} created.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                if (ex.InnerException != null)
                    Console.Error.WriteLine($"Detail:\n  {ex.InnerException.Message}");
                Environment.ExitCode = 1;
            }
        }, pathArg, versionArg, commentOpt);

        return cmd;
    }
}
