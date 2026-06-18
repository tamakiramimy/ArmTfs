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
