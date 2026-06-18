namespace ArmTfs.Core.Models.Soap;

/// <summary>
/// 提交到 CheckIn 的待提交变更。
/// 由 PendMerge 返回的 GetOperation 转换而来；CheckIn 时上传给服务器，
/// 服务器据此写入带 MergeSources 的合并历史。
/// </summary>
public sealed class SoapPendingChange
{
    /// <summary>服务器端 item ID。</summary>
    public int ItemId { get; init; }

    /// <summary>目标分支服务器路径。</summary>
    public string ServerItem { get; init; } = string.Empty;

    /// <summary>变更类型字符串（"Merge|Edit"、"Merge|Add" 等）。</summary>
    public string ChangeType { get; init; } = string.Empty;

    /// <summary>源分支服务器路径（合并自）。</summary>
    public string? SourceServerItem { get; init; }

    /// <summary>合并起始版本。</summary>
    public int? VersionFrom { get; init; }

    /// <summary>合并结束版本。</summary>
    public int? VersionTo { get; init; }

    /// <summary>变更前的版本（base version）。</summary>
    public int? VersionBase { get; init; }

    /// <summary>是否为 branch 变更。</summary>
    public bool IsBranch { get; init; }
}
