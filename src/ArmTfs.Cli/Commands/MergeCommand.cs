using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;
using ArmTfs.Core.Models;
using ArmTfs.Core.Workspace;
using System.Text.Json;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs merge — 查询 TFVC merge 相关信息。</summary>
public static class MergeCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("merge", "Query TFVC merge candidates and merge base information.");
        cmd.AddCommand(BuildCandidate(config));
        cmd.AddCommand(BuildBase(config));
        cmd.AddCommand(BuildExecute(config));
        cmd.AddCommand(BuildPreviewConflicts(config));
        return cmd;
    }

    private static Command BuildPreviewConflicts(TfsConfig config)
    {
        var cmd = new Command("preview-conflicts", "Preview real merge conflicts (server 3-way) for a changeset range without committing. Uses SOAP.");
        var sourceOpt = new Option<string>("--source") { Description = "Source branch or folder path ($/...)" };
        var targetOpt = new Option<string>("--target") { Description = "Target branch or folder path ($/...)" };
        var fromOpt = new Option<int>("--from") { Description = "From source changeset (inclusive)", IsRequired = true };
        var toOpt = new Option<int>("--to") { Description = "To source changeset (inclusive)", IsRequired = true };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };

        sourceOpt.IsRequired = true;
        targetOpt.IsRequired = true;

        cmd.AddOption(sourceOpt);
        cmd.AddOption(targetOpt);
        cmd.AddOption(fromOpt);
        cmd.AddOption(toOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (source, target, from, to, format) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                var resolvedSource = ResolveServerPath(source);
                var resolvedTarget = ResolveServerPath(target);
                var conflicts = await svc.PreviewMergeConflictsAsync(resolvedSource, resolvedTarget, from, to).ConfigureAwait(false);

                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "merge.preview-conflicts",
                        query = new { sourcePath = resolvedSource, targetPath = resolvedTarget, from, to },
                        conflictCount = conflicts.Count,
                        conflicts = conflicts.Select(c => new
                        {
                            sourceServerPath = c.SourceServerPath,
                            targetServerPath = c.TargetServerPath,
                            conflictType = c.ConflictType,
                        }),
                    });
                    return;
                }

                Console.WriteLine($"Source    : {resolvedSource}");
                Console.WriteLine($"Target    : {resolvedTarget}");
                Console.WriteLine($"Range     : cs#{from} ~ cs#{to}");
                Console.WriteLine($"Conflicts : {conflicts.Count}");
                if (conflicts.Count > 0)
                {
                    Console.WriteLine();
                    foreach (var c in conflicts)
                        Console.WriteLine($"  {c.TargetServerPath}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                if (ex is ArmTfs.Core.Client.Soap.SoapFaultException soap && !string.IsNullOrEmpty(soap.RawResponse))
                {
                    Console.Error.WriteLine("Raw response:");
                    Console.Error.WriteLine(soap.RawResponse[..Math.Min(1500, soap.RawResponse.Length)]);
                }
                Environment.ExitCode = 1;
            }
        }, sourceOpt, targetOpt, fromOpt, toOpt, formatOpt);

        return cmd;
    }

    private static Command BuildExecute(TfsConfig config)
    {
        var cmd = new Command("execute", "Execute or preview a TFVC merge from source to target.");
        var sourceOpt = new Option<string>("--source") { Description = "Source branch or folder path ($/...)" };
        var targetOpt = new Option<string>("--target") { Description = "Target branch or folder path ($/...)" };
        var changesetOpt = new Option<int?>("--changeset") { Description = "Single source changeset ID to merge" };
        var fromOpt = new Option<int?>("--from") { Description = "First source changeset in a SOAP range merge plan (inclusive)" };
        var toOpt = new Option<int?>("--to") { Description = "Last source changeset in a SOAP range merge plan (inclusive)" };
        var commentOpt = new Option<string?>("--comment") { Description = "Comment for the created merge changeset" };
        var dryRunOpt = new Option<bool>("--dry-run") { Description = "Show the merge plan without creating a TFVC changeset" };
        var resolutionFileOpt = new Option<FileInfo?>("--resolution-file") { Description = "JSON file with per-file merge resolutions" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };
        var modeOpt = new Option<string>("--mode", () => "soap") { Description = "Merge protocol: soap (default, real merge history via Repository.asmx). Only soap is supported." };
        var soapOwnerOpt = new Option<string?>("--soap-owner") { Description = "Owner identity for the temporary SOAP workspace. Omit to auto-resolve the authenticated user's TFVC owner GUID." };

        sourceOpt.IsRequired = true;
        targetOpt.IsRequired = true;

        cmd.AddOption(sourceOpt);
        cmd.AddOption(targetOpt);
        cmd.AddOption(changesetOpt);
        cmd.AddOption(fromOpt);
        cmd.AddOption(toOpt);
        cmd.AddOption(commentOpt);
        cmd.AddOption(dryRunOpt);
        cmd.AddOption(resolutionFileOpt);
        cmd.AddOption(formatOpt);
        cmd.AddOption(modeOpt);
        cmd.AddOption(soapOwnerOpt);

        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            var source = ctx.ParseResult.GetValueForOption(sourceOpt)!;
            var target = ctx.ParseResult.GetValueForOption(targetOpt)!;
            var changesetId = ctx.ParseResult.GetValueForOption(changesetOpt);
            var fromChangeset = ctx.ParseResult.GetValueForOption(fromOpt);
            var toChangeset = ctx.ParseResult.GetValueForOption(toOpt);
            var comment = ctx.ParseResult.GetValueForOption(commentOpt);
            var dryRun = ctx.ParseResult.GetValueForOption(dryRunOpt);
            var resolutionFile = ctx.ParseResult.GetValueForOption(resolutionFileOpt);
            var format = ctx.ParseResult.GetValueForOption(formatOpt) ?? "table";
            var mode = ctx.ParseResult.GetValueForOption(modeOpt) ?? "soap";
            var soapOwner = ctx.ParseResult.GetValueForOption(soapOwnerOpt);

            var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn, ws);

            try
            {
                var resolvedSource = ResolveServerPath(source);
                var resolvedTarget = ResolveServerPath(target);
                var hasSingle = changesetId.HasValue;
                var hasRange = fromChangeset.HasValue || toChangeset.HasValue;
                if (hasSingle == hasRange || (hasRange && (!fromChangeset.HasValue || !toChangeset.HasValue)))
                    throw new InvalidOperationException("Specify either --changeset, or both --from and --to.");

                MergeExecutionResult result;
                if (hasRange)
                {
                    if (!string.Equals(mode, "soap", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Range merge is SOAP-only. Omit --mode or use --mode soap.");

                    var resolutions = LoadMergeResolutions(resolutionFile);
                    result = await svc.MergeChangesetRangeViaSoapAsync(
                        resolvedSource, resolvedTarget,
                        fromChangeset!.Value, toChangeset!.Value,
                        comment,
                        dryRun,
                        soapOwner: soapOwner,
                        resolutions: resolutions).ConfigureAwait(false);
                }
                else
                {
                    var resolutions = LoadMergeResolutions(resolutionFile);
                    result = await svc.MergeChangesetAsync(
                        resolvedSource, resolvedTarget, changesetId!.Value, comment, dryRun, resolutions,
                        mergeMode: mode,
                        soapOwner: soapOwner).ConfigureAwait(false);
                }

                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "merge.execute",
                        result = ProjectExecutionResult(result),
                    });
                    return;
                }

                Console.WriteLine($"Source      : {result.SourcePath}");
                Console.WriteLine($"Target      : {result.TargetPath}");
                var isRange = result.SourceFromChangesetId > 0
                    && result.SourceToChangesetId > 0
                    && result.SourceFromChangesetId != result.SourceToChangesetId;
                Console.WriteLine(isRange
                    ? $"Range       : cs#{result.SourceFromChangesetId} ~ cs#{result.SourceToChangesetId}"
                    : $"Changeset   : cs#{result.SourceChangesetId}");
                Console.WriteLine($"Mode        : {(result.DryRun ? "dry-run" : "execute")}");
                Console.WriteLine($"Comment     : {result.Comment}");
                if (result.CreatedChangesetId.HasValue)
                    Console.WriteLine($"Created     : cs#{result.CreatedChangesetId.Value}");

                Console.WriteLine();
                Console.WriteLine($"{"Target Change",-18}  {"Target Path",-80}  Source");
                Console.WriteLine($"{new string('-', 18)}  {new string('-', 80)}  {new string('-', 40)}");
                foreach (var change in result.Changes)
                {
                    Console.WriteLine($"{change.TargetChangeType,-18}  {change.TargetServerPath,-80}  {change.SourceServerPath}");
                    if (!string.IsNullOrEmpty(change.Note))
                        Console.WriteLine($"{string.Empty,-18}  note: {change.Note}");
                }

                if (result.Warnings.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Warnings:");
                    foreach (var warning in result.Warnings)
                        Console.WriteLine($"  - {warning}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                if (ex is ArmTfs.Core.Client.Soap.SoapFaultException soap)
                {
                    Console.Error.WriteLine($"SOAP method  : {soap.Method}");
                    Console.Error.WriteLine($"HTTP status  : {(int)soap.StatusCode} {soap.StatusCode}");
                    if (!string.IsNullOrEmpty(soap.RawResponse))
                    {
                        Console.Error.WriteLine("Raw response :");
                        Console.Error.WriteLine(soap.RawResponse[..Math.Min(2000, soap.RawResponse.Length)]);
                    }
                }
                else
                {
                    // Non-SOAP errors: print full detail incl. inner
                    // exceptions so "部分更改无效" type rejections show which change/file was rejected.
                    Console.Error.WriteLine("Detail:");
                    for (var e = ex; e is not null; e = e.InnerException)
                    {
                        Console.Error.WriteLine($"  {e.GetType().FullName}: {e.Message}");
                    }
                }
                Environment.ExitCode = 1;
            }
        });

        return cmd;
    }

    private static Command BuildCandidate(TfsConfig config)
    {
        var cmd = new Command("candidate", "Query merge candidates between a source and target path.");
        var sourceOpt = new Option<string>("--source") { Description = "Source branch or folder path ($/...)" };
        var targetOpt = new Option<string>("--target") { Description = "Target branch or folder path ($/...)" };
        var topOpt = new Option<int>("--top", () => 20) { Description = "Maximum candidate changesets to return" };
        var scanOpt = new Option<int>("--scan", () => 80) { Description = "How many source/target history entries to scan while inferring candidates" };
        var forceOpt = new Option<bool>("--force") { Description = "Ignore merge history (show all source changesets as candidates, useful after rollback)" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };

        sourceOpt.IsRequired = true;
        targetOpt.IsRequired = true;

        cmd.AddOption(sourceOpt);
        cmd.AddOption(targetOpt);
        cmd.AddOption(topOpt);
        cmd.AddOption(scanOpt);
        cmd.AddOption(forceOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (source, target, top, scan, force, format) =>
        {
            var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory());
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn, ws);

            try
            {
                var resolvedSource = ResolveServerPath(source);
                var resolvedTarget = ResolveServerPath(target);
                var locallyMerged = ws?.GetLocallyMergedChangesetIds(resolvedSource, resolvedTarget);
                var result = await svc.GetMergeCandidatesAsync(resolvedSource, resolvedTarget, top, scan, force, locallyMerged).ConfigureAwait(false);

                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "merge.candidate",
                        query = new
                        {
                            sourcePath = resolvedSource,
                            targetPath = resolvedTarget,
                            top,
                            scan,
                            inferenceMode = "branch-history",
                        },
                        mergeBase = ProjectBaseInfo(result.BaseInfo),
                        summary = new
                        {
                            sourceHistoryScanned = result.SourceHistoryScanned,
                            targetHistoryScanned = result.TargetHistoryScanned,
                            sourceUniqueFloorChangesetId = result.SourceUniqueFloorChangesetId,
                            mergedRangesCount = result.MergedRanges.Count,
                        },
                        items = result.Candidates.Select(ProjectCandidate),
                    });
                    return;
                }

                Console.WriteLine($"Source      : {resolvedSource}");
                Console.WriteLine($"Target      : {resolvedTarget}");
                Console.WriteLine($"Relationship: {result.BaseInfo.Relationship}");
                Console.WriteLine($"Base        : {result.BaseInfo.CommonAncestorPath}");
                Console.WriteLine($"Confidence  : {result.BaseInfo.Confidence}");
                Console.WriteLine($"Scan        : source {result.SourceHistoryScanned}, target {result.TargetHistoryScanned}, merged ranges {result.MergedRanges.Count}");
                if (result.SourceUniqueFloorChangesetId.HasValue)
                    Console.WriteLine($"Source floor: cs#{result.SourceUniqueFloorChangesetId.Value}");

                if (result.Candidates.Count == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("No inferred merge candidates found in the scanned history window.");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine($"{"ID",-8}  {"Date",-20}  {"Author",-24}  Comment");
                    Console.WriteLine($"{new string('-', 8)}  {new string('-', 20)}  {new string('-', 24)}  {new string('-', 50)}");
                    foreach (var candidate in result.Candidates)
                    {
                        var comment = (candidate.Comment ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
                        if (comment.Length > 70) comment = comment[..67] + "...";
                        Console.WriteLine($"{candidate.ChangesetId,-8}  {candidate.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss,-20}  {(candidate.AuthorDisplayName ?? string.Empty),-24}  {comment}");
                    }
                }

                if (result.BaseInfo.Notes.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Notes:");
                    foreach (var note in result.BaseInfo.Notes)
                        Console.WriteLine($"  - {note}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, sourceOpt, targetOpt, topOpt, scanOpt, forceOpt, formatOpt);

        return cmd;
    }

    private static Command BuildBase(TfsConfig config)
    {
        var cmd = new Command("base", "Resolve the inferred merge base between a source and target path.");
        var sourceOpt = new Option<string>("--source") { Description = "Source branch or folder path ($/...)" };
        var targetOpt = new Option<string>("--target") { Description = "Target branch or folder path ($/...)" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };

        sourceOpt.IsRequired = true;
        targetOpt.IsRequired = true;

        cmd.AddOption(sourceOpt);
        cmd.AddOption(targetOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (source, target, format) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                var resolvedSource = ResolveServerPath(source);
                var resolvedTarget = ResolveServerPath(target);
                var mergeBase = await svc.ResolveMergeBaseAsync(resolvedSource, resolvedTarget).ConfigureAwait(false);

                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    JsonOutput.Write(new
                    {
                        schemaVersion = 1,
                        command = "merge.base",
                        query = new
                        {
                            sourcePath = resolvedSource,
                            targetPath = resolvedTarget,
                            inferenceMode = "branch-history",
                        },
                        mergeBase = ProjectBaseInfo(mergeBase),
                    });
                    return;
                }

                Console.WriteLine($"Source branch : {mergeBase.SourceBranchPath}");
                Console.WriteLine($"Target branch : {mergeBase.TargetBranchPath}");
                Console.WriteLine($"Relationship  : {mergeBase.Relationship}");
                Console.WriteLine($"Common base   : {mergeBase.CommonAncestorPath}");
                Console.WriteLine($"Confidence    : {mergeBase.Confidence}");
                Console.WriteLine($"Source point  : {(mergeBase.SourceBranchPointChangesetId.HasValue ? $"cs#{mergeBase.SourceBranchPointChangesetId.Value}" : "(unknown)")}");
                Console.WriteLine($"Target point  : {(mergeBase.TargetBranchPointChangesetId.HasValue ? $"cs#{mergeBase.TargetBranchPointChangesetId.Value}" : "(unknown)")}");

                if (mergeBase.SourceAncestry.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Source ancestry:");
                    foreach (var path in mergeBase.SourceAncestry)
                        Console.WriteLine($"  {path}");
                }

                if (mergeBase.TargetAncestry.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Target ancestry:");
                    foreach (var path in mergeBase.TargetAncestry)
                        Console.WriteLine($"  {path}");
                }

                if (mergeBase.Notes.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Notes:");
                    foreach (var note in mergeBase.Notes)
                        Console.WriteLine($"  - {note}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, sourceOpt, targetOpt, formatOpt);

        return cmd;
    }

    private static string ResolveServerPath(string input)
    {
        if (input.StartsWith("$/", StringComparison.Ordinal))
            return input;

        var ws = WorkspaceManager.FindWorkspace(Directory.GetCurrentDirectory())
            ?? throw new InvalidOperationException("No workspace found to resolve local merge path.");
        var meta = ws.LoadMetadata();
        return ws.LocalToServerPath(Path.GetFullPath(input), meta)
            ?? throw new InvalidOperationException($"Unable to resolve '{input}' to a TFVC server path.");
    }

    private static object ProjectBaseInfo(MergeBaseInfo mergeBase) => new
    {
        sourcePath = mergeBase.SourcePath,
        targetPath = mergeBase.TargetPath,
        sourceBranchPath = mergeBase.SourceBranchPath,
        targetBranchPath = mergeBase.TargetBranchPath,
        sourceAncestry = mergeBase.SourceAncestry,
        targetAncestry = mergeBase.TargetAncestry,
        commonAncestorPath = mergeBase.CommonAncestorPath,
        relationship = mergeBase.Relationship,
        sourceBranchCreatedAt = mergeBase.SourceBranchCreatedAt,
        targetBranchCreatedAt = mergeBase.TargetBranchCreatedAt,
        sourceBranchPointChangesetId = mergeBase.SourceBranchPointChangesetId,
        targetBranchPointChangesetId = mergeBase.TargetBranchPointChangesetId,
        confidence = mergeBase.Confidence,
        notes = mergeBase.Notes,
    };

    private static object ProjectCandidate(MergeCandidateInfo candidate) => new
    {
        changesetId = candidate.ChangesetId,
        createdAt = candidate.CreatedAt,
        comment = candidate.Comment,
        author = new
        {
            displayName = candidate.AuthorDisplayName,
            uniqueName = candidate.AuthorUniqueName,
        },
        isMergedToTarget = candidate.IsMergedToTarget,
        coveredByTargetChangesetId = candidate.CoveredByTargetChangesetId,
        coveredByRange = candidate.CoveredByRange is null ? null : new
        {
            serverItem = candidate.CoveredByRange.ServerItem,
            versionFrom = candidate.CoveredByRange.VersionFrom,
            versionTo = candidate.CoveredByRange.VersionTo,
            isRename = candidate.CoveredByRange.IsRename,
            targetChangesetId = candidate.CoveredByRange.TargetChangesetId,
        },
    };

    private static object ProjectExecutionResult(MergeExecutionResult result) => new
    {
        sourcePath = result.SourcePath,
        targetPath = result.TargetPath,
        sourceChangesetId = result.SourceChangesetId,
        sourceFromChangesetId = result.SourceFromChangesetId,
        sourceToChangesetId = result.SourceToChangesetId,
        comment = result.Comment,
        dryRun = result.DryRun,
        createdChangesetId = result.CreatedChangesetId,
        mergeBase = ProjectBaseInfo(result.BaseInfo),
        changes = result.Changes.Select(change => new
        {
            sourceServerPath = change.SourceServerPath,
            targetServerPath = change.TargetServerPath,
            sourceChangesetId = change.SourceChangesetId,
            sourceChangeType = change.SourceChangeType,
            targetChangeType = change.TargetChangeType,
            targetExists = change.TargetExists,
            hasContent = change.HasContent,
            status = change.Status,
            resolution = change.Resolution,
            note = change.Note,
        }),
        warnings = result.Warnings,
    };

    private static IReadOnlyList<MergeExecutionResolution>? LoadMergeResolutions(FileInfo? resolutionFile)
    {
        if (resolutionFile is null)
            return null;

        if (!resolutionFile.Exists)
            throw new FileNotFoundException("Merge resolution file was not found.", resolutionFile.FullName);

        var json = File.ReadAllText(resolutionFile.FullName);
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<MergeExecutionResolution>();

        return JsonSerializer.Deserialize<IReadOnlyList<MergeExecutionResolution>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? Array.Empty<MergeExecutionResolution>();
    }
}
