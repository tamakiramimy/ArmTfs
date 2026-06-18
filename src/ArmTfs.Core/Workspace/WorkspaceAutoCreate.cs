using ArmTfs.Core.Config;
using ArmTfs.Core.Models;

namespace ArmTfs.Core.Workspace;

/// <summary>
/// 根据服务器路径自动推导本地路径并创建工作区。
/// <para>
/// 映射规则：<c>$/Seg1/Seg2/...</c> → <c>&lt;WorkspaceRoot&gt;/Seg1/Seg2/...</c>。
/// 例如：WorkspaceRoot = /Users/foo/tfs，服务器路径 $/C02_PTL/MyProject → /Users/foo/tfs/C02_PTL/MyProject。
/// </para>
/// </summary>
public static class WorkspaceAutoCreate
{
    /// <summary>
    /// 尝试根据服务器路径自动推导本地路径并创建工作区。
    /// 如果工作区根目录未配置或服务器路径格式不对则返回 <c>null</c>。
    /// 如果目标位置已有工作区则直接返回该工作区（不重复创建）。
    /// </summary>
    /// <param name="serverPath">TFVC 服务器路径（必须以 $/ 开头）</param>
    /// <param name="config">全局配置（用于获取 WorkspaceRoot 和 ServerUrl）</param>
    /// <param name="localPathOverride">指定本地路径覆盖（为 null 时按映射规则推导）</param>
    /// <returns>创建成功或已存在的 <see cref="WorkspaceManager"/>；无法推导时返回 <c>null</c></returns>
    public static (WorkspaceManager? Workspace, bool Created) EnsureWorkspace(
        string serverPath,
        TfsConfig config,
        string? localPathOverride = null)
    {
        if (!serverPath.StartsWith("$/", StringComparison.Ordinal))
            return (null, false);

        // 推导本地路径
        string localPath;
        if (localPathOverride is not null)
        {
            localPath = Path.GetFullPath(localPathOverride);
        }
        else
        {
            var workspaceRoot = config.WorkspaceRoot;
            if (string.IsNullOrEmpty(workspaceRoot))
                return (null, false);

            // 去掉前导 "$/"，把服务器路径段按 "/" 分割，附加到 WorkspaceRoot
            var serverRelative = serverPath[2..]; // 去掉 "$/"
            // 将 "/" 转换为平台路径分隔符
            var localRelative = serverRelative.Replace('/', Path.DirectorySeparatorChar);
            localPath = Path.GetFullPath(Path.Combine(workspaceRoot, localRelative));
        }

        // 已有工作区则直接复用
        var existing = WorkspaceManager.FindWorkspace(localPath);
        if (existing is not null)
            return (existing, false);

        // 创建目录
        Directory.CreateDirectory(localPath);

        // 生成工作区名称：机器名_分支叶节点名
        var leafName = serverPath.TrimEnd('/').Split('/').Last();
        var workspaceName = $"{Environment.MachineName}_{leafName}";

        var ws = new WorkspaceManager(localPath);
        var meta = new WorkspaceMetadata
        {
            Name = workspaceName,
            ServerCollectionUrl = config.ServerUrl ?? "pending",
            Mappings = new List<WorkspaceMapping>
            {
                new() { ServerPath = serverPath, LocalPath = localPath }
            }
        };
        ws.SaveMetadata(meta);

        return (ws, true);
    }

    /// <summary>
    /// 将服务器路径推导出对应的本地路径（不创建工作区）。
    /// 仅用于显示目的，实际创建请用 <see cref="EnsureWorkspace"/>。
    /// </summary>
    public static string? ResolveLocalPath(string serverPath, TfsConfig config, string? localPathOverride = null)
    {
        if (localPathOverride is not null)
            return Path.GetFullPath(localPathOverride);

        if (!serverPath.StartsWith("$/", StringComparison.Ordinal))
            return null;

        var workspaceRoot = config.WorkspaceRoot;
        if (string.IsNullOrEmpty(workspaceRoot))
            return null;

        var serverRelative = serverPath[2..];
        var localRelative = serverRelative.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(workspaceRoot, localRelative));
    }
}
