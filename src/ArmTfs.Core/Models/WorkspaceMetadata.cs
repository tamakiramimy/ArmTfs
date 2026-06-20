namespace ArmTfs.Core.Models;

/// <summary>
/// 工作区中一条服务器路径 → 本地路径的映射规则。
/// 对应 TF 工作区的 Working Folder 概念。
/// </summary>
public sealed record WorkspaceMapping
{
    /// <summary>TFVC 服务器路径（如 $/MyProject/Main）</summary>
    public required string ServerPath { get; init; }

    /// <summary>对应的本地文件夹完整路径（绝对路径）</summary>
    public required string LocalPath { get; init; }
}

/// <summary>
/// 本地工作区的全局定义，序列化到 .tf/workspace.json。
/// 工作区是 arm-tfs 一切操作的起点：路径映射、服务器 URL 均从这里读取。
/// </summary>
public sealed record WorkspaceMetadata
{
    /// <summary>工作区名称（默认使用主机名，可自定义）</summary>
    public required string Name { get; init; }

    /// <summary>工作区所有者（显示名称，可为空）</summary>
    public string? Owner { get; init; }

    /// <summary>TFS/Azure DevOps Server Collection URL（如 https://tfs/DefaultCollection）</summary>
    public required string ServerCollectionUrl { get; init; }

    /// <summary>服务器路径 → 本地路径映射列表（顺序无关，匹配时按路径长度降序优先）</summary>
    public List<WorkspaceMapping> Mappings { get; init; } = new();

    /// <summary>
    /// Cloaked server paths — these paths are excluded from <c>get</c> operations.
    /// Use <c>arm-tfs workfold cloak</c> to add entries and <c>uncloak</c> to remove.
    /// </summary>
    public List<string> CloakedPaths { get; init; } = new();

    /// <summary>工作区创建时间（UTC）</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
