namespace ArmTfs.Core.Models.Soap;

/// <summary>Shelve 操作中的单个文件变更描述。</summary>
public sealed class SoapShelveChange
{
    /// <summary>服务器路径</summary>
    public string ServerPath { get; init; } = string.Empty;

    /// <summary>变更类型字符串（Add/Edit/Delete/Rename/Undelete）</summary>
    public string ChangeTypeStr { get; init; } = "Edit";

    /// <summary>文件内容（null 表示不上传，用于 Delete）</summary>
    public byte[]? Content { get; init; }

    /// <summary>基准版本（用于 Edit/Delete，告知服务器基于哪个版本变更）</summary>
    public int? BaseVersion { get; init; }
}
