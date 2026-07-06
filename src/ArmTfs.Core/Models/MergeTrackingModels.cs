namespace ArmTfs.Core.Models;

/// <summary>
/// 记录一次合并操作的执行结果。
/// 采用手动内容复制方式完成合并（服务器不记录合并历史），
/// 因此需要在本地追踪已合并的 changeset 以避免候选列表重复显示。
/// </summary>
public sealed class MergeRecord
{
    /// <summary>被合并的源 changeset ID</summary>
    public int SourceChangesetId { get; init; }

    /// <summary>源分支路径</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>目标分支路径</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>合并提交后创建的目标 changeset ID</summary>
    public int? TargetChangesetId { get; init; }

    /// <summary>合并执行时间 (UTC)</summary>
    public DateTime MergedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>合并方式：manual-takesource | soap-merge | soap-takesource</summary>
    public string Method { get; init; } = "manual-takesource";
}

/// <summary>
/// .tf/merge-history.json 的顶层容器
/// </summary>
public sealed class MergeHistory
{
    public IList<MergeRecord> Merges { get; init; } = new List<MergeRecord>();
}

/// <summary>
/// 嵌入 checkin comment 中的合并元数据标记，用于跨工作区识别已合并内容。
/// 格式: [arm-tfs-merge:source=$/Path;cs=12345;target=$/Path]
/// </summary>
public static class MergeCommentMarker
{
    private const string Prefix = "[arm-tfs-merge:";
    private const string Suffix = "]";

    /// <summary>生成嵌入到 checkin comment 末尾的合并标记</summary>
    public static string Build(string sourcePath, int sourceChangesetId, string targetPath)
    {
        return $"{Prefix}source={sourcePath};cs={sourceChangesetId};target={targetPath}{Suffix}";
    }

    /// <summary>从 checkin comment 中提取合并标记信息</summary>
    public static MergeMarkerInfo? Parse(string? comment)
    {
        if (string.IsNullOrEmpty(comment))
            return null;

        var startIdx = comment.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0)
            return null;

        var endIdx = comment.IndexOf(Suffix, startIdx + Prefix.Length, StringComparison.Ordinal);
        if (endIdx < 0)
            return null;

        var payload = comment[(startIdx + Prefix.Length)..endIdx];
        var parts = payload.Split(';', StringSplitOptions.RemoveEmptyEntries);

        string? source = null, target = null;
        int? cs = null;

        foreach (var part in parts)
        {
            var eqIdx = part.IndexOf('=');
            if (eqIdx <= 0)
                continue;

            var key = part[..eqIdx].Trim();
            var value = part[(eqIdx + 1)..].Trim();

            switch (key.ToLowerInvariant())
            {
                case "source":
                    source = value;
                    break;
                case "target":
                    target = value;
                    break;
                case "cs":
                    if (int.TryParse(value, out var csVal))
                        cs = csVal;
                    break;
            }
        }

        if (source is null || cs is null)
            return null;

        return new MergeMarkerInfo
        {
            SourcePath = source,
            SourceChangesetId = cs.Value,
            TargetPath = target ?? string.Empty,
        };
    }
}

/// <summary>从 comment 标记中解析出的合并信息</summary>
public sealed class MergeMarkerInfo
{
    public string SourcePath { get; init; } = string.Empty;
    public int SourceChangesetId { get; init; }
    public string TargetPath { get; init; } = string.Empty;
}
