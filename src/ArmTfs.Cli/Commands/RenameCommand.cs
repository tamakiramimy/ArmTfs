using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs rename &lt;old&gt; &lt;new&gt; — 重命名/移动服务器文件或文件夹。</summary>
public static class RenameCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("rename", "Rename or move a file/folder on the TFVC server.");
        cmd.AddAlias("move");
        cmd.AddAlias("mv");

        var oldArg = new Argument<string>("old-path", "Current server path");
        var newArg = new Argument<string>("new-path", "New server path");
        var commentOpt = new Option<string?>("--comment", "-c") { Description = "Checkin comment" };
        var ownerOpt = new Option<string?>("--soap-owner") { Description = "Override SOAP owner (GUID or DOMAIN\\\\user)" };

        cmd.AddArgument(oldArg);
        cmd.AddArgument(newArg);
        cmd.AddOption(commentOpt);
        cmd.AddOption(ownerOpt);

        cmd.SetHandler(async (oldPath, newPath, comment, soapOwner) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                var changesetId = await svc.RenameItemAsync(oldPath, newPath, comment, soapOwner).ConfigureAwait(false);
                Console.WriteLine($"Renamed. Changeset {changesetId} created.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, oldArg, newArg, commentOpt, ownerOpt);

        return cmd;
    }
}
