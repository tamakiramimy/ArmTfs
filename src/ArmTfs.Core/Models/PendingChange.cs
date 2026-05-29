namespace ArmTfs.Core.Models;

/// <summary>
/// 本地挂起变更的类型标志（可按位组合，与 TFVC VersionControlChangeType 对齐）。
/// </summary>
public enum ChangeType
{
    /// <summary>无变更（占位，不应出现在实际挂起列表中）</summary>
    None = 0,
    /// <summary>新增文件（尚未在服务器上存在）</summary>
    Add = 1,
    /// <summary>编辑已有文件（内容发生变化）</summary>
    Edit = 2,
    /// <summary>删除文件（签入后服务器端删除）</summary>
    Delete = 4,
    /// <summary>重命名/移动文件（SourceServerPath 保存原路径）</summary>
    Rename = 8,
    /// <summary>还原已删除文件</summary>
    Undelete = 16,
}

/// <summary>
/// 单条本地挂起变更，序列化到 .tf/pending.json。
/// 每个被 checkout/add/delete 的文件对应一条记录。
/// </summary>
public sealed class PendingChange
{
    /// <summary>服务器端 TFVC 路径（如 $/MyProject/src/Foo.cs）</summary>
    public required string ServerPath { get; init; }

    /// <summary>本地磁盘完整路径（绝对路径，OS 风格分隔符）</summary>
    public required string LocalPath { get; init; }

    /// <summary>变更类型</summary>
    public ChangeType ChangeType { get; init; }

    /// <summary>
    /// Rename 操作时的原服务器路径。
    /// 签入时用于告知服务器 rename 的源和目标。
    /// </summary>
    public string? SourceServerPath { get; init; }

    /// <summary>
    /// checkout/add 时记录的文件 SHA-256 哈希（小写十六进制）。
    /// <c>status</c> 命令用它与当前磁盘内容比较，判断文件是否真正被修改。
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>添加到挂起变更列表的时间（UTC）</summary>
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}
