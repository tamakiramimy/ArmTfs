namespace ArmTfs.Core.Models.Soap;

/// <summary>TFVC SOAP 协议中的 Workspace 描述。</summary>
public sealed class SoapWorkspace
{
    /// <summary>Workspace 名称（在同一 owner 下唯一）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Workspace owner（可能是 DOMAIN\user / user@domain / Display Name 任一格式）。</summary>
    public string Owner { get; init; } = string.Empty;

    /// <summary>Owner 的展示名（可选）。</summary>
    public string? OwnerDisplay { get; init; }

    /// <summary>创建该 workspace 的客户端机器名。</summary>
    public string Computer { get; init; } = string.Empty;

    /// <summary>Workspace 备注。</summary>
    public string Comment { get; init; } = string.Empty;

    /// <summary>Workspace 类型：local | server。新版 TFS 默认 server，旧版默认 local。</summary>
    public string? Location { get; init; }

    /// <summary>最近一次 update 的 changeset ID（可选）。</summary>
    public int? LastAccessChangesetId { get; init; }

    public override string ToString() => $"{Name} ({Owner}@{Computer})";
}
