using System.CommandLine;
using System.Text;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;
using ArmTfs.Core.Workspace;
using DiffPlex;
using DiffPlex.Renderer;

namespace ArmTfs.Cli.Commands;

/// <summary>
/// arm-tfs diff [path] — 比较本地文件与服务器版本的差异。
/// 首版只支持单文件：
///   - 默认比较本地文件 vs 服务器最新版本
///   - --base 比较本地文件 vs tracked base 版本
///   - --version 指定对比的 changeset
/// </summary>
public static class DiffCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("diff", "Show differences between a local file and its TFVC server version.");

        var pathArg = new Argument<string>("path", "Local path or server path ($/...) to diff");
        var baseOpt = new Option<bool>("--base") { Description = "Compare against the tracked base version instead of the latest server version" };
        var versionOpt = new Option<int?>("--version") { Description = "Compare against a specific changeset version" };
        var fromVersionOpt = new Option<int?>("--from-version") { Description = "Server-to-server comparison: older changeset version" };
        var toVersionOpt = new Option<int?>("--to-version") { Description = "Server-to-server comparison: newer changeset version" };
        var toPathOpt = new Option<string?>("--to-path") { Description = "Server-to-server comparison: target TFVC path when comparing branches" };
        var ignoreWhitespaceOpt = new Option<bool>("--ignore-whitespace") { Description = "Ignore whitespace-only differences for text files" };
        var formatOpt = new Option<string>("--format", () => "text") { Description = "Output format: text | json" };

        cmd.AddArgument(pathArg);
        cmd.AddOption(baseOpt);
        cmd.AddOption(versionOpt);
        cmd.AddOption(fromVersionOpt);
        cmd.AddOption(toVersionOpt);
        cmd.AddOption(toPathOpt);
        cmd.AddOption(ignoreWhitespaceOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (path, useBase, version, fromVersion, toVersion, toPath, ignoreWhitespace, format) =>
        {
            if (fromVersion.HasValue || toVersion.HasValue)
            {
                if (!fromVersion.HasValue || !toVersion.HasValue)
                {
                    Console.Error.WriteLine("Both '--from-version' and '--to-version' are required.");
                    Environment.ExitCode = 1;
                    return;
                }
                if (!path.StartsWith("$/", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("Server version comparison requires a TFVC server path starting with $/.");
                    Environment.ExitCode = 1;
                    return;
                }

                await CompareServerVersionsAsync(
                    config,
                    path,
                    string.IsNullOrWhiteSpace(toPath) ? path : toPath,
                    fromVersion.Value,
                    toVersion.Value,
                    ignoreWhitespace,
                    format).ConfigureAwait(false);
                return;
            }

            if (useBase && version.HasValue)
            {
                Console.Error.WriteLine("Choose either '--base' or '--version', not both.");
                Environment.ExitCode = 1;
                return;
            }

            var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
            if (ws is null)
            {
                Console.Error.WriteLine("No workspace found. Run 'arm-tfs workspace new' first.");
                Environment.ExitCode = 1;
                return;
            }

            var meta = ws.LoadMetadata();

            string localPath;
            string serverPath;

            if (path.StartsWith("$/", StringComparison.Ordinal))
            {
                serverPath = path;
                localPath = ws.ServerToLocalPath(serverPath, meta) ?? string.Empty;
                if (string.IsNullOrEmpty(localPath))
                {
                    Console.Error.WriteLine($"No workspace mapping found for '{serverPath}'.");
                    Environment.ExitCode = 1;
                    return;
                }
            }
            else
            {
                localPath = Path.GetFullPath(path);
                serverPath = ws.LocalToServerPath(localPath, meta) ?? string.Empty;
                if (string.IsNullOrEmpty(serverPath))
                {
                    Console.Error.WriteLine($"No TFVC mapping found for '{localPath}'.");
                    Environment.ExitCode = 1;
                    return;
                }
            }

            if (!File.Exists(localPath))
            {
                Console.Error.WriteLine($"Local file not found: {localPath}");
                Environment.ExitCode = 1;
                return;
            }

            var tracked = ws.GetTrackedVersion(localPath);
            var pendingChange = ws.LoadPendingChanges().FirstOrDefault(p =>
                string.Equals(p.LocalPath, localPath, StringComparison.OrdinalIgnoreCase));
            if (useBase && tracked is null)
            {
                Console.Error.WriteLine("No tracked base version found for this file. Run 'arm-tfs get' first or omit '--base'.");
                Environment.ExitCode = 1;
                return;
            }

            var targetVersion = useBase ? tracked!.ChangesetId : version;
            var targetLabel = useBase
                ? $"base cs#{tracked!.ChangesetId}"
                : version.HasValue
                    ? $"cs#{version.Value}"
                    : "latest";

            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            byte[] serverBytes;
            var cachedBasePath = useBase ? ws.GetCachedBaseFilePath(localPath) : null;
            if (!string.IsNullOrEmpty(cachedBasePath))
            {
                serverBytes = await File.ReadAllBytesAsync(cachedBasePath).ConfigureAwait(false);
            }
            else
            {
                await using var stream = new MemoryStream();
                await svc.DownloadFileAsync(serverPath, stream, targetVersion).ConfigureAwait(false);
                serverBytes = stream.ToArray();
            }

            var localBytes = await File.ReadAllBytesAsync(localPath).ConfigureAwait(false);
            var same = serverBytes.AsSpan().SequenceEqual(localBytes);
            var currentHash = WorkspaceManager.ComputeFileHash(localPath);

            object workspaceState;
            if (pendingChange is not null)
            {
                workspaceState = new
                {
                    state = "pending",
                    changeType = JsonOutput.EnumValue(pendingChange.ChangeType),
                    trackedChangesetId = tracked?.ChangesetId,
                };
            }
            else if (tracked is not null && !string.Equals(currentHash, tracked.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                workspaceState = new
                {
                    state = "modifiedNotCheckedOut",
                    changeType = (string?)null,
                    trackedChangesetId = tracked.ChangesetId,
                };
            }
            else
            {
                workspaceState = new
                {
                    state = "clean",
                    changeType = (string?)null,
                    trackedChangesetId = tracked?.ChangesetId,
                };
            }

            string resultKind;
            string? patch = null;

            if (same)
            {
                resultKind = "none";
            }

            else
            {
                var serverIsText = TryDecodeUtf8Text(serverBytes, out var serverText);
                var localIsText = TryDecodeUtf8Text(localBytes, out var localText);
                if (!serverIsText || !localIsText)
                {
                    resultKind = "binary";
                }
                else
                {
                    var oldFileName = $"{serverPath} ({targetLabel})";
                    var newFileName = $"{Path.GetFileName(localPath)} (local)";
                    patch = UnidiffRenderer.GenerateUnidiff(
                        serverText,
                        localText,
                        oldFileName,
                        newFileName,
                        ignoreWhitespace,
                        ignoreCase: false,
                        contextLines: 3).TrimEnd();
                    resultKind = string.IsNullOrWhiteSpace(patch) ? "none" : "text";
                    if (resultKind == "none")
                        patch = null;
                }
            }

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                JsonOutput.Write(new
                {
                    schemaVersion = 1,
                    command = "diff",
                    target = new
                    {
                        inputPath = path,
                        localPath,
                        serverPath,
                    },
                    compareTo = new
                    {
                        mode = useBase ? "base" : version.HasValue ? "changeset" : "latest",
                        changesetId = targetVersion,
                    },
                    workspaceState,
                    result = new
                    {
                        kind = resultKind,
                        localSize = localBytes.Length,
                        serverSize = serverBytes.Length,
                        patch,
                    }
                });
                return;
            }

            Console.WriteLine($"Diffing: {localPath}");
            Console.WriteLine($"  Server Path : {serverPath}");
            Console.WriteLine($"  Compare To  : {targetLabel}");
            Console.WriteLine();

            if (resultKind == "none")
            {
                Console.WriteLine("No differences.");
                return;
            }

            if (resultKind == "binary")
            {
                Console.WriteLine("Binary files differ.");
                Console.WriteLine($"  Local Size  : {localBytes.Length} byte(s)");
                Console.WriteLine($"  Server Size : {serverBytes.Length} byte(s)");
                return;
            }

            Console.WriteLine(patch);
        }, pathArg, baseOpt, versionOpt, fromVersionOpt, toVersionOpt, toPathOpt, ignoreWhitespaceOpt, formatOpt);

        return cmd;
    }

    private static async Task CompareServerVersionsAsync(
        TfsConfig config,
        string fromServerPath,
        string toServerPath,
        int fromVersion,
        int toVersion,
        bool ignoreWhitespace,
        string format)
    {
        using var conn = new TfsConnection(config);
        var svc = new TfvcClientService(conn);
        await using var fromStream = new MemoryStream();
        await using var toStream = new MemoryStream();
        await svc.DownloadFileAsync(fromServerPath, fromStream, fromVersion).ConfigureAwait(false);
        await svc.DownloadFileAsync(toServerPath, toStream, toVersion).ConfigureAwait(false);

        var fromBytes = fromStream.ToArray();
        var toBytes = toStream.ToArray();
        var same = fromBytes.AsSpan().SequenceEqual(toBytes);
        var resultKind = "none";
        string? patch = null;

        if (!same)
        {
            if (!TryDecodeUtf8Text(fromBytes, out var fromText) || !TryDecodeUtf8Text(toBytes, out var toText))
            {
                resultKind = "binary";
            }
            else
            {
                patch = UnidiffRenderer.GenerateUnidiff(
                    fromText,
                    toText,
                    $"{fromServerPath} (cs#{fromVersion})",
                    $"{toServerPath} (cs#{toVersion})",
                    ignoreWhitespace,
                    ignoreCase: false,
                    contextLines: 3).TrimEnd();
                resultKind = string.IsNullOrWhiteSpace(patch) ? "none" : "text";
                if (resultKind == "none")
                    patch = null;
            }
        }

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            JsonOutput.Write(new
            {
                schemaVersion = 1,
                command = "diff.versions",
                target = new
                {
                    inputPath = fromServerPath,
                    localPath = (string?)null,
                    serverPath = fromServerPath,
                    toServerPath,
                },
                compareTo = new
                {
                    mode = "changesetRange",
                    fromChangesetId = fromVersion,
                    toChangesetId = toVersion,
                },
                workspaceState = new { state = "serverComparison" },
                result = new
                {
                    kind = resultKind,
                    localSize = toBytes.Length,
                    serverSize = fromBytes.Length,
                    patch,
                }
            });
            return;
        }

        Console.WriteLine(resultKind switch
        {
            "none" => "No differences.",
            "binary" => "Binary files differ.",
            _ => patch,
        });
    }

    private static bool TryDecodeUtf8Text(byte[] content, out string text)
    {
        text = string.Empty;
        if (Array.IndexOf(content, (byte)0) >= 0)
            return false;

        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
