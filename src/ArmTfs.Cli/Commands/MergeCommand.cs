using System.CommandLine;
using ArmTfs.Core.Client;
using ArmTfs.Core.Config;
using ArmTfs.Core.Models;
using ArmTfs.Core.Workspace;

namespace ArmTfs.Cli.Commands;

/// <summary>arm-tfs merge — 查询 TFVC merge 相关信息。</summary>
public static class MergeCommand
{
    public static Command Build(TfsConfig config)
    {
        var cmd = new Command("merge", "Query TFVC merge candidates and merge base information.");
        cmd.AddCommand(BuildCandidate(config));
        cmd.AddCommand(BuildBase(config));
        return cmd;
    }

    private static Command BuildCandidate(TfsConfig config)
    {
        var cmd = new Command("candidate", "Query merge candidates between a source and target path.");
        var sourceOpt = new Option<string>("--source") { Description = "Source branch or folder path ($/...)" };
        var targetOpt = new Option<string>("--target") { Description = "Target branch or folder path ($/...)" };
        var topOpt = new Option<int>("--top", () => 20) { Description = "Maximum candidate changesets to return" };
        var scanOpt = new Option<int>("--scan", () => 80) { Description = "How many source/target history entries to scan while inferring candidates" };
        var formatOpt = new Option<string>("--format", () => "table") { Description = "Output format: table | json" };

        sourceOpt.IsRequired = true;
        targetOpt.IsRequired = true;

        cmd.AddOption(sourceOpt);
        cmd.AddOption(targetOpt);
        cmd.AddOption(topOpt);
        cmd.AddOption(scanOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (source, target, top, scan, format) =>
        {
            using var conn = new TfsConnection(config);
            var svc = new TfvcClientService(conn);

            try
            {
                var resolvedSource = ResolveServerPath(source);
                var resolvedTarget = ResolveServerPath(target);
                var result = await svc.GetMergeCandidatesAsync(resolvedSource, resolvedTarget, top, scan).ConfigureAwait(false);

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
                        Console.WriteLine($"{candidate.ChangesetId,-8}  {candidate.CreatedAt:yyyy-MM-dd HH:mm:ss,-20}  {(candidate.AuthorDisplayName ?? string.Empty),-24}  {comment}");
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
        }, sourceOpt, targetOpt, topOpt, scanOpt, formatOpt);

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
}