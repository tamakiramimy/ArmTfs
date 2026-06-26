namespace ArmTfs.Core.Models.Soap;

/// <summary>
/// PendMerge 返回的服务器端 GetOperation。
/// 描述每个文件需要执行的操作（下载、合并、删除等）以及合并源版本范围。
/// </summary>
public sealed class SoapMergeOperation
{
    /// <summary>服务器端 item ID。</summary>
    public int ItemId { get; init; }

    /// <summary>源分支上的服务器路径。</summary>
    public string SourceServerItem { get; init; } = string.Empty;

    /// <summary>目标分支上的服务器路径（合并后该文件的最终位置）。</summary>
    public string TargetServerItem { get; init; } = string.Empty;

    /// <summary>变更类型字符串，TFS 协议格式（例如 "Merge|Edit"、"Merge|Add"、"Merge|Delete"）。</summary>
    public string ChangeType { get; init; } = string.Empty;

    /// <summary>合并的起始版本（源 changeset）。</summary>
    public int? VersionFrom { get; init; }

    /// <summary>合并的结束版本（源 changeset）。</summary>
    public int? VersionTo { get; init; }

    /// <summary>是否为 PendingChange（需要客户端 CheckIn 确认）。</summary>
    public bool IsPending { get; init; } = true;

    public override string ToString() => $"{ChangeType} {SourceServerItem} → {TargetServerItem} (cs{VersionFrom}-{VersionTo})";
}

/// <summary>PendMerge 返回的冲突（服务器 3-way merge 检测到双方都改了同一文件）。</summary>
public sealed class SoapMergeConflict
{
    /// <summary>服务器端 conflict ID；SOAP Resolve 需要这个 ID。</summary>
    public int ConflictId { get; init; }

    /// <summary>目标分支上的服务器路径（"your" side）。</summary>
    public string TargetServerItem { get; init; } = string.Empty;

    /// <summary>源分支上的服务器路径（"their" side）。</summary>
    public string SourceServerItem { get; init; } = string.Empty;

    /// <summary>冲突类型（如 "Merge"）。</summary>
    public string ConflictType { get; init; } = string.Empty;

    /// <summary>基线变更类型（如 "Edit Merge"）。</summary>
    public string BaseChangeType { get; init; } = string.Empty;
}

/// <summary>PendMerge 的结果：可提交的操作 + 待解决的冲突。</summary>
public sealed class PendMergeResult
{
    public IReadOnlyList<SoapMergeOperation> Operations { get; init; } = Array.Empty<SoapMergeOperation>();
    public IReadOnlyList<SoapMergeConflict> Conflicts { get; init; } = Array.Empty<SoapMergeConflict>();
}

/// <summary>Resolve 返回的服务器端操作与被联动解决的冲突。</summary>
public sealed class ResolveConflictResult
{
    public IReadOnlyList<SoapMergeOperation> Operations { get; init; } = Array.Empty<SoapMergeOperation>();
    public IReadOnlyList<SoapMergeOperation> UndoOperations { get; init; } = Array.Empty<SoapMergeOperation>();
    public IReadOnlyList<SoapMergeConflict> ResolvedConflicts { get; init; } = Array.Empty<SoapMergeConflict>();
}
