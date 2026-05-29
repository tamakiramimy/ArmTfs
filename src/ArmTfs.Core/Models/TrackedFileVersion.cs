namespace ArmTfs.Core.Models;

/// <summary>
/// 记录某个本地文件当前对应的服务器版本快照。
/// 序列化到 .tf/versions/&lt;hash&gt;.json，每个被 <c>get</c> 下载过的文件对应一个。
/// <para>
/// <c>status</c> 命令通过比较 <see cref="ContentHash"/> 与磁盘当前哈希
/// 来判断文件自上次 <c>get</c> 后是否被本地修改（未 checkout 的修改）。
/// </para>
/// </summary>
public sealed class TrackedFileVersion
{
    /// <summary>对应的服务器端 TFVC 路径</summary>
    public required string ServerPath { get; init; }

    /// <summary>本地磁盘完整路径（绝对路径）</summary>
    public required string LocalPath { get; init; }

    /// <summary>下载时对应的 Changeset 编号</summary>
    public int ChangesetId { get; init; }

    /// <summary>下载时计算的文件 SHA-256 哈希（小写十六进制）</summary>
    public required string ContentHash { get; init; }

    /// <summary>文件最后一次被 <c>get</c> 下载/更新的时间（UTC）</summary>
    public DateTimeOffset DownloadedAt { get; init; } = DateTimeOffset.UtcNow;
}
