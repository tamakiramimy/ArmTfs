using System.CommandLine;
using System.CommandLine.Invocation;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs items — 列出 TFVC 服务器端路径下的条目</summary>
public static class ItemsCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("items", "List items at a TFVC server path.");

        var listCmd = new Command("list", "List direct children (or all descendants) of a server path.");

        var pathArg = new Argument<string>("path", () => "$/", "TFVC server path (e.g. $/ or $/MyProject/src)");
        var recursiveOpt = new Option<bool>(new[] { "--recursive", "-r" })
        {
            Description = "List all descendants recursively. Default: list only direct children (oneLevel)."
        };
        var formatOpt = new Option<string>("--format")
        {
            Description = "Output format: plain or json."
        };
        formatOpt.SetDefaultValue("plain");

        listCmd.AddArgument(pathArg);
        listCmd.AddOption(recursiveOpt);
        listCmd.AddOption(formatOpt);

        listCmd.SetHandler(async (InvocationContext context) =>
        {
            var path = context.ParseResult.GetValueForArgument(pathArg);
            var recursive = context.ParseResult.GetValueForOption(recursiveOpt);
            var format = context.ParseResult.GetValueForOption(formatOpt) ?? "plain";
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                // 默认 oneLevel（浅层），--recursive 时使用 Full
                var items = await svc.GetItemsAsync(
                    path,
                    recursive: recursive,
                    oneLevelOnly: !recursive
                ).ConfigureAwait(false);

                if (format == "json")
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "items.list",
                        query = new { path, recursive },
                        items = items.Select(i => new
                        {
                            serverPath = i.ServerPath,
                            isFolder = i.IsFolder,
                            changesetId = i.ChangesetId,
                            contentLength = i.IsFolder ? (long?)null : i.ContentLength,
                            checkinDate = i.CheckinDate?.ToString("O"),
                        })
                    });
                }
                else
                {
                    foreach (var item in items)
                    {
                        Console.WriteLine($"{(item.IsFolder ? "[DIR]  " : "[FILE]")} {item.ServerPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        cmd.AddCommand(listCmd);
        return cmd;
    }
}
