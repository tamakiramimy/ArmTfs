namespace ArmTfs.Core.Models;

public sealed class MergeBaseInfo
{
    public string SourcePath { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public string? SourceBranchPath { get; init; }
    public string? TargetBranchPath { get; init; }
    public IReadOnlyList<string> SourceAncestry { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TargetAncestry { get; init; } = Array.Empty<string>();
    public string? CommonAncestorPath { get; init; }
    public string Relationship { get; init; } = "unresolved";
    public DateTime? SourceBranchCreatedAt { get; init; }
    public DateTime? TargetBranchCreatedAt { get; init; }
    public int? SourceBranchPointChangesetId { get; init; }
    public int? TargetBranchPointChangesetId { get; init; }
    public string Confidence { get; init; } = "low";
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class MergeSourceRange
{
    public string ServerItem { get; init; } = string.Empty;
    public int? VersionFrom { get; init; }
    public int? VersionTo { get; init; }
    public bool IsRename { get; init; }
    public int TargetChangesetId { get; init; }

    public bool Covers(int changesetId)
    {
        if (!VersionTo.HasValue)
            return false;

        var from = VersionFrom ?? VersionTo.Value;
        var to = VersionTo.Value;
        if (from > to)
            (from, to) = (to, from);

        return changesetId >= from && changesetId <= to;
    }
}

public sealed class MergeCandidateInfo
{
    public int ChangesetId { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? Comment { get; init; }
    public string? AuthorDisplayName { get; init; }
    public string? AuthorUniqueName { get; init; }
    public bool IsMergedToTarget { get; init; }
    public int? CoveredByTargetChangesetId { get; init; }
    public MergeSourceRange? CoveredByRange { get; init; }
}

public sealed class MergeCandidateQueryResult
{
    public MergeBaseInfo BaseInfo { get; init; } = new();
    public int SourceHistoryScanned { get; init; }
    public int TargetHistoryScanned { get; init; }
    public int? SourceUniqueFloorChangesetId { get; init; }
    public IReadOnlyList<MergeSourceRange> MergedRanges { get; init; } = Array.Empty<MergeSourceRange>();
    public IReadOnlyList<MergeCandidateInfo> Candidates { get; init; } = Array.Empty<MergeCandidateInfo>();
}

public sealed class MergeExecutionChange
{
    public string SourceServerPath { get; init; } = string.Empty;
    public string TargetServerPath { get; init; } = string.Empty;
    public int SourceChangesetId { get; init; }
    public string SourceChangeType { get; init; } = string.Empty;
    public string TargetChangeType { get; init; } = string.Empty;
    public bool TargetExists { get; init; }
    public bool HasContent { get; init; }
    public string Status { get; init; } = "planned";
    public string? Resolution { get; init; }
    public string? Note { get; init; }
}

public sealed class MergeExecutionResolution
{
    public string SourceServerPath { get; init; } = string.Empty;
    public string TargetServerPath { get; init; } = string.Empty;
    public int? SourceChangesetId { get; init; }
    public string Choice { get; init; } = "source";
    public string? ContentBase64 { get; init; }
}

public sealed class MergeExecutionResult
{
    public string SourcePath { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public int SourceChangesetId { get; init; }
    public int SourceFromChangesetId { get; init; }
    public int SourceToChangesetId { get; init; }
    public string Comment { get; init; } = string.Empty;
    public bool DryRun { get; init; }
    public int? CreatedChangesetId { get; init; }
    public MergeBaseInfo BaseInfo { get; init; } = new();
    public IReadOnlyList<MergeExecutionChange> Changes { get; init; } = Array.Empty<MergeExecutionChange>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>SOAP 服务器 3-way merge 预检出的冲突（未解决）。</summary>
public sealed class MergeConflictPreview
{
    public string SourceServerPath { get; init; } = string.Empty;
    public string TargetServerPath { get; init; } = string.Empty;
    public string ConflictType { get; init; } = string.Empty;
}
