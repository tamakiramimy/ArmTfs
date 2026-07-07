namespace ArmTfs.Core.Models.Soap;

/// <summary>
/// 用于 SOAP CheckInWithContent 的变更请求。
/// 包含变更类型、服务器路径、文件内容和基线版本。
/// </summary>
public sealed class SoapContentChange
{
    /// <summary>变更类型：Edit、Add、Delete、Undelete。</summary>
    public string ChangeType { get; init; } = "Edit";

    /// <summary>TFVC 服务器路径。</summary>
    public string ServerPath { get; init; } = string.Empty;

    /// <summary>文件内容（Delete 时为 null）。</summary>
    public byte[]? Content { get; init; }

    /// <summary>基线版本号（Edit/Delete 需要；Add 时为 null）。</summary>
    public int? BaseVersion { get; init; }

    /// <summary>Rename 操作时的目标路径。</summary>
    public string? TargetServerPath { get; init; }
}

/// <summary>
/// PendChanges 的通用请求条目。
/// </summary>
public sealed class SoapChangeRequest
{
    /// <summary>请求类型：Edit、Add、Delete、Undelete、Rename、Lock。</summary>
    public string RequestType { get; init; } = "Edit";

    /// <summary>TFVC 服务器路径。</summary>
    public string ServerPath { get; init; } = string.Empty;

    /// <summary>基线版本号；null 表示 LatestVersionSpec。</summary>
    public int? BaseVersion { get; init; }

    /// <summary>文件编码（UTF-8 = 65001；null 表示不指定）。</summary>
    public int? Encoding { get; init; }

    /// <summary>项类型（File/Folder）；null 表示不指定。</summary>
    public string? ItemType { get; init; }

    /// <summary>Rename 操作的目标路径。</summary>
    public string? TargetServerPath { get; init; }
}

/// <summary>
/// PendChanges 返回的操作结果（每个文件的 GetOperation）。
/// </summary>
public sealed class SoapPendChangeOperation
{
    /// <summary>服务器端 item ID。</summary>
    public int ItemId { get; init; }

    /// <summary>服务器路径。</summary>
    public string ServerItem { get; init; } = string.Empty;

    /// <summary>变更类型字符串。</summary>
    public string ChangeType { get; init; } = string.Empty;
}

/// <summary>
/// Local-version bookkeeping entry for Repository5.UpdateLocalVersion.
/// </summary>
public sealed class SoapLocalVersionUpdate
{
    /// <summary>Server item path whose local baseline is being recorded.</summary>
    public string ServerPath { get; init; } = string.Empty;

    /// <summary>Mapped local file path inside the temporary workspace.</summary>
    public string LocalPath { get; init; } = string.Empty;

    /// <summary>Base changeset version currently represented by the local item.</summary>
    public int LocalVersion { get; init; }

    /// <summary>Server item id required by the legacy Repository.UpdateLocalVersion SOAP shape.</summary>
    public int ItemId { get; init; }
}
