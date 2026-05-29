using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs history — 查看 TFVC 变更历史</summary>
public static class HistoryCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("history", "Show changeset history for a server path.");
        cmd.AddAlias("hist");

        var pathArg = new Argument<string>("path", () => ".", "Local path or server path ($/...)");
        var topOpt = new Option<int>(new[] { "--top", "-n" }, getDefaultValue: () => 20) { Description = "Maximum number of changesets to show" };
        var authorOpt = new Option<string?>(new[] { "--author", "-u" }) { Description = "Filter by author display name" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };

        cmd.AddArgument(pathArg);
        cmd.AddOption(topOpt);
        cmd.AddOption(authorOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (path, top, author, format) =>
        {
            // Resolve server path
            string? serverPath = null;
            if (path.StartsWith("$/"))
            {
                serverPath = path;
            }
            else
            {
                var ws = Core.Workspace.WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
                if (ws is not null)
                {
                    var meta = ws.LoadMetadata();
                    serverPath = ws.LocalToServerPath(Path.GetFullPath(path), meta)
                        ?? meta.Mappings.FirstOrDefault()?.ServerPath;
                }
            }
            // null serverPath means whole collection

            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                var changesets = await svc.GetChangesetsAsync(serverPath, author, top).ConfigureAwait(false);

                if (format == "json")
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "history",
                        query = new
                        {
                            inputPath = path,
                            serverPath,
                            author,
                            top,
                        },
                        items = changesets.Select(c => new
                        {
                            changesetId = c.ChangesetId,
                            createdAt = c.CreatedDate,
                            comment = c.Comment,
                            commentTruncated = c.CommentTruncated,
                            author = JsonOutput.Identity(c.Author),
                            checkedInBy = JsonOutput.Identity(c.CheckedInBy),
                        }),
                    });
                    return;
                }

                Console.WriteLine($"{"ID",-8}  {"Date",-22}  {"Author",-25}  Comment");
                Console.WriteLine($"{new string('-', 8)}  {new string('-', 22)}  {new string('-', 25)}  {new string('-', 50)}");

                foreach (var cs in changesets)
                {
                    var date = cs.CreatedDate.ToString("yyyy-MM-dd HH:mm:ss");
                    var authorName = (cs.Author?.DisplayName ?? "").PadRight(25)[..Math.Min(25, cs.Author?.DisplayName?.Length ?? 0)];
                    var comment = (cs.Comment ?? "").Replace('\n', ' ').Replace('\r', ' ');
                    if (comment.Length > 70) comment = comment[..67] + "...";
                    Console.WriteLine($"{cs.ChangesetId,-8}  {date,-22}  {authorName,-25}  {comment}");
                }
            }
            catch (Exception ex)
            {
                // 提取最内层异常消息，避免 TFS 返回 HTML 错误页被原样打出
                var inner = ex.InnerException?.Message ?? ex.Message;
                if (inner.Contains("401") || inner.Contains("Unauthorized") || ex.Message.Contains("default error template"))
                {
                    Console.Error.WriteLine("Error: Authentication failed (HTTP 401). Please check:");
                    Console.Error.WriteLine("  1. Run 'arm-tfs configure --url <url> --pat <token>' first");
                    Console.Error.WriteLine("  2. Verify your PAT is valid and has TFVC read permission");
                }
                else if (ex.Message.Contains("not configured"))
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                }
                else
                {
                    Console.Error.WriteLine($"Error: {ex.GetType().Name}: {ex.Message}");
                    if (ex.InnerException != null)
                        Console.Error.WriteLine($"  → {ex.InnerException.Message}");
                }
                Environment.ExitCode = 1;
            }
        }, pathArg, topOpt, authorOpt, formatOpt);

        return cmd;
    }
}
