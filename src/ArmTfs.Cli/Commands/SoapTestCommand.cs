using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Client.Soap;
using ArmTfs.Core.Config;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs soap-test — 验证 TFVC SOAP 协议端点连通性。</summary>
public static class SoapTestCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("soap-test", "Verify TFVC SOAP endpoint connectivity (Repository.asmx).");

        var ownerOpt = new Option<string?>("--owner") { Description = "Filter workspaces by owner (DOMAIN\\\\user / user@domain). Defaults to all visible workspaces." };
        var computerOpt = new Option<string?>("--computer") { Description = "Filter workspaces by client computer name." };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };
        var cycleOpt = new Option<bool>("--workspace-cycle") { Description = "Create a temporary workspace then delete it (validates write path)." };
        var cycleOwnerOpt = new Option<string?>("--cycle-owner") { Description = "Owner name to use for --workspace-cycle (required when set)." };

        cmd.AddOption(ownerOpt);
        cmd.AddOption(computerOpt);
        cmd.AddOption(formatOpt);
        cmd.AddOption(cycleOpt);
        cmd.AddOption(cycleOwnerOpt);

        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            var owner = ctx.ParseResult.GetValueForOption(ownerOpt);
            var computer = ctx.ParseResult.GetValueForOption(computerOpt);
            var format = ctx.ParseResult.GetValueForOption(formatOpt) ?? "table";
            var cycle = ctx.ParseResult.GetValueForOption(cycleOpt);
            var cycleOwner = ctx.ParseResult.GetValueForOption(cycleOwnerOpt);

            try
            {
                using var conn = new TfsConnection(config);
                var soap = new TfvcSoapClient(conn);

                Console.WriteLine($"Endpoint    : {soap.EndpointUrl}");
                Console.WriteLine($"Server      : {conn.ServerUrl}");
                Console.WriteLine();

                if (cycle)
                {
                    var ownerForCycle = cycleOwner
                        ?? throw new InvalidOperationException("--workspace-cycle requires --cycle-owner.");
                    var wsName = $"arm-tfs-test-{Guid.NewGuid():N}";
                    var computerName = computer ?? Environment.MachineName;
                    Console.WriteLine($"[cycle] CreateWorkspace name='{wsName}' owner='{ownerForCycle}' computer='{computerName}'");
                    var created = await soap.CreateWorkspaceAsync(wsName, ownerForCycle, computerName, "arm-tfs soap-test cycle").ConfigureAwait(false);
                    Console.WriteLine($"[cycle]   created: {created}");
                    Console.WriteLine($"[cycle] DeleteWorkspace");
                    await soap.DeleteWorkspaceAsync(wsName, ownerForCycle).ConfigureAwait(false);
                    Console.WriteLine($"[cycle]   deleted ok");
                    Console.WriteLine();
                }

                var workspaces = await soap.QueryWorkspacesAsync(owner, computer).ConfigureAwait(false);

                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "soap-test",
                        endpoint = soap.EndpointUrl,
                        items = workspaces.Select(w => new
                        {
                            name = w.Name,
                            owner = w.Owner,
                            ownerDisplay = w.OwnerDisplay,
                            computer = w.Computer,
                            comment = w.Comment,
                            location = w.Location,
                        }),
                    });
                    return;
                }

                Console.WriteLine($"Workspaces  : {workspaces.Count}");
                if (workspaces.Count == 0)
                {
                    Console.WriteLine("(no workspaces visible — SOAP plumbing works, just empty result)");
                    return;
                }

                Console.WriteLine();
                Console.WriteLine($"{"Name",-32}  {"Owner",-32}  {"Computer",-20}  Comment");
                Console.WriteLine($"{new string('-', 32)}  {new string('-', 32)}  {new string('-', 20)}  {new string('-', 30)}");
                foreach (var ws in workspaces)
                {
                    var commentShort = ws.Comment.Length > 40 ? ws.Comment[..37] + "..." : ws.Comment;
                    Console.WriteLine($"{Truncate(ws.Name, 32),-32}  {Truncate(ws.OwnerDisplay ?? ws.Owner, 32),-32}  {Truncate(ws.Computer, 20),-20}  {commentShort}");
                }
            }
            catch (SoapFaultException ex)
            {
                Console.Error.WriteLine($"SOAP fault: {ex.Message}");
                if (!string.IsNullOrEmpty(ex.RawResponse))
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Raw response:");
                    Console.Error.WriteLine(ex.RawResponse[..Math.Min(2000, ex.RawResponse.Length)]);
                }
                Environment.ExitCode = 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return cmd;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..(max - 3)] + "...");
}
