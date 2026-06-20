using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs lock / unlock — 锁定/解锁服务器文件（独占编辑锁）。</summary>
public static class LockCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("lock", "Lock or unlock a file/folder on the TFVC server.");

        cmd.AddCommand(BuildLock(config));
        cmd.AddCommand(BuildUnlock(config));

        return cmd;
    }

    private static Command BuildLock(TfsConfig config)
    {
        var cmd = new Command("set", "Place a checkout lock on a server path.");

        var pathArg = new Argument<string>("path", "Server path to lock");
        var ownerOpt = new Option<string?>("--soap-owner") { Description = "Override SOAP workspace owner (GUID or DOMAIN\\\\user)" };

        cmd.AddArgument(pathArg);
        cmd.AddOption(ownerOpt);

        cmd.SetHandler(async (path, soapOwner) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                var n = await svc.LockItemAsync(path, lockIt: true, soapOwner: soapOwner).ConfigureAwait(false);
                Console.WriteLine($"Lock applied ({n} operation(s)).");
            }
            catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); Environment.ExitCode = 1; }
        }, pathArg, ownerOpt);

        return cmd;
    }

    private static Command BuildUnlock(TfsConfig config)
    {
        var cmd = new Command("unset", "Remove a lock from a server path.");

        var pathArg = new Argument<string>("path", "Server path to unlock");
        var ownerOpt = new Option<string?>("--soap-owner") { Description = "Override SOAP workspace owner" };

        cmd.AddArgument(pathArg);
        cmd.AddOption(ownerOpt);

        cmd.SetHandler(async (path, soapOwner) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);
            try
            {
                var n = await svc.LockItemAsync(path, lockIt: false, soapOwner: soapOwner).ConfigureAwait(false);
                Console.WriteLine($"Lock removed ({n} operation(s)).");
            }
            catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); Environment.ExitCode = 1; }
        }, pathArg, ownerOpt);

        return cmd;
    }
}
