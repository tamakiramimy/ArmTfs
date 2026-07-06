namespace ArmTfs.Core.Models;

/// <summary>
/// TFVC 服务器端条目的快照信息，由 <c>GetItemsAsync</c> 返回。
/// TFVC 服务器端条目快照，由 SOAP QueryItems 返回。
/// </summary>
public sealed class TfsServerItem
{
    /// <summary>服务器端 TFVC 路径（如 $/MyProject/src/Foo.cs）</summary>
    public required string ServerPath { get; init; }

    /// <summary>是否为目录（文件夹），<c>true</c> 时 ContentLength 为 0</summary>
    public bool IsFolder { get; init; }

    /// <summary>该文件最后一次被修改的 Changeset 编号</summary>
    public int ChangesetId { get; init; }

    /// <summary>文件字节大小；对目录该值为 0</summary>
    public long ContentLength { get; init; }

    /// <summary>服务器端内容哈希（MD5 Base64，用于快速比对，非安全用途）</summary>
    public string? HashValue { get; init; }

    /// <summary>该版本的签入时间</summary>
    public DateTimeOffset? CheckinDate { get; init; }
}
