using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArmTfs.Core.Models;

namespace ArmTfs.Core.Workspace;

/// <summary>
/// 管理本地工作区元数据（.tf/ 目录）。
/// 
/// 目录结构：
///   &lt;workspace_root&gt;/
///     .tf/
///       workspace.json   - 工作区定义（名称、服务器URL、路径映射）
///       pending.json     - 挂起变更列表
///       versions/        - 已追踪文件的版本信息（每文件一个 JSON）
/// </summary>
public sealed class WorkspaceManager
{
    private const string TfDir = ".tf";
    private const string WorkspaceFile = "workspace.json";
    private const string PendingFile = "pending.json";
    private const string VersionsDir = "versions";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _rootPath;

    /// <summary>初始化，使用指定目录作为工作区根目录。</summary>
    /// <param name="rootPath">工作区根目录（内部会转为绝对路径）</param>
    public WorkspaceManager(string rootPath)
    {
        _rootPath = NormalizeLocalPath(rootPath);
    }

    private string TfPath => Path.Combine(_rootPath, TfDir);
    private string WorkspacePath => Path.Combine(TfPath, WorkspaceFile);
    private string PendingPath => Path.Combine(TfPath, PendingFile);
    private string VersionsPath => Path.Combine(TfPath, VersionsDir);

    // ─── 工作区搜索 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 从给定目录向上搜索，找到最近的包含 .tf/workspace.json 的目录。
    /// </summary>
    public static WorkspaceManager? FindWorkspace(string startPath)
    {
        var dir = NormalizeLocalPath(startPath);
        while (true)
        {
            var candidate = Path.Combine(dir, TfDir, WorkspaceFile);
            if (File.Exists(candidate))
                return new WorkspaceManager(dir);

            var parent = Directory.GetParent(dir);
            if (parent is null) return null;
            dir = parent.FullName;
        }
    }

    // ─── 工作区元数据 ──────────────────────────────────────────────────────────

    /// <summary>工作区元数据文件是否已存在</summary>
    public bool Exists => File.Exists(WorkspacePath);

    /// <summary>
    /// 从 .tf/workspace.json 加载工作区元数据。
    /// </summary>
    /// <exception cref="InvalidOperationException">文件不存在时抛出</exception>
    public WorkspaceMetadata LoadMetadata()
    {
        EnsureExists();
        var json = File.ReadAllText(WorkspacePath);
        return JsonSerializer.Deserialize<WorkspaceMetadata>(json, _jsonOptions)
               ?? throw new InvalidOperationException("Invalid workspace.json");
    }

    /// <summary>将工作区元数据序列化保存，并自动更新 .gitignore。</summary>
    public void SaveMetadata(WorkspaceMetadata metadata)
    {
        Directory.CreateDirectory(TfPath);
        File.WriteAllText(WorkspacePath, JsonSerializer.Serialize(metadata, _jsonOptions));
        EnsureGitIgnore();
    }

    // ─── 挂起变更 ──────────────────────────────────────────────────────────────

    /// <summary>从 .tf/pending.json 加载挂起变更列表；文件不存在时返回空列表。</summary>
    public IReadOnlyList<PendingChange> LoadPendingChanges()
    {
        if (!File.Exists(PendingPath)) return Array.Empty<PendingChange>();
        var json = File.ReadAllText(PendingPath);
        return JsonSerializer.Deserialize<List<PendingChange>>(json, _jsonOptions)
               ?? new List<PendingChange>();
    }

    /// <summary>将全量挂起变更列表序列化写入 .tf/pending.json。</summary>
    public void SavePendingChanges(IEnumerable<PendingChange> changes)
    {
        Directory.CreateDirectory(TfPath);
        File.WriteAllText(PendingPath, JsonSerializer.Serialize(changes.ToList(), _jsonOptions));
    }

    /// <summary>
    /// 添加一条挂起变更。若同一本地路径已存在则替换（如先 add 后再 checkout 同一文件）。
    /// </summary>
    public void AddPendingChange(PendingChange change)
    {
        var pending = LoadPendingChanges().ToList();
        // 同一文件已存在则替换
        pending.RemoveAll(p => string.Equals(p.LocalPath, change.LocalPath, StringComparison.OrdinalIgnoreCase));
        pending.Add(change);
        SavePendingChanges(pending);
    }

    /// <summary>移除对应本地路径的挂起变更记录（大小写不敏感）。</summary>
    public void RemovePendingChange(string localPath)
    {
        var pending = LoadPendingChanges().ToList();
        var removed = pending.RemoveAll(p =>
            string.Equals(p.LocalPath, localPath, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
            SavePendingChanges(pending);
    }

    // ─── 文件版本追踪 ──────────────────────────────────────────────────────────

    /// <summary>获取指定本地文件的版本追踪信息；文件未被追踪时返回 <c>null</c>。</summary>
    public TrackedFileVersion? GetTrackedVersion(string localPath)
    {
        var file = GetVersionFilePath(localPath);
        if (!File.Exists(file)) return null;
        var json = File.ReadAllText(file);
        return JsonSerializer.Deserialize<TrackedFileVersion>(json, _jsonOptions);
    }

    /// <summary>将文件的版本快照序列化写入 .tf/versions/ 目录。</summary>
    public void SaveTrackedVersion(TrackedFileVersion version)
    {
        Directory.CreateDirectory(VersionsPath);
        var file = GetVersionFilePath(version.LocalPath);
        File.WriteAllText(file, JsonSerializer.Serialize(version, _jsonOptions));
    }

    /// <summary>删除对应本地文件的版本快照（文件已删除时调用）。</summary>
    public void RemoveTrackedVersion(string localPath)
    {
        var file = GetVersionFilePath(localPath);
        if (File.Exists(file)) File.Delete(file);
    }

    // ─── 映射辅助 ──────────────────────────────────────────────────────────────

    /// <summary>将本地路径转换为服务器路径（按工作区映射规则）</summary>
    public string? LocalToServerPath(string localPath, WorkspaceMetadata metadata)
    {
        var absLocal = NormalizeLocalPath(localPath);
        foreach (var mapping in metadata.Mappings.OrderByDescending(m => m.LocalPath.Length))
        {
            var absMapping = NormalizeLocalPath(mapping.LocalPath);
            if (absLocal.StartsWith(absMapping, StringComparison.OrdinalIgnoreCase))
            {
                var relative = absLocal[absMapping.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var serverRelative = relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
                return string.IsNullOrEmpty(serverRelative)
                    ? mapping.ServerPath
                    : $"{mapping.ServerPath.TrimEnd('/')}/{serverRelative}";
            }
        }
        return null;
    }

    /// <summary>将服务器路径转换为本地路径（按工作区映射规则）</summary>
    public string? ServerToLocalPath(string serverPath, WorkspaceMetadata metadata)
    {
        foreach (var mapping in metadata.Mappings.OrderByDescending(m => m.ServerPath.Length))
        {
            if (serverPath.StartsWith(mapping.ServerPath, StringComparison.OrdinalIgnoreCase))
            {
                var relative = serverPath[mapping.ServerPath.Length..].TrimStart('/');
                var localRelative = relative.Replace('/', Path.DirectorySeparatorChar);
                return string.IsNullOrEmpty(localRelative)
                    ? NormalizeLocalPath(mapping.LocalPath)
                    : NormalizeLocalPath(Path.Combine(mapping.LocalPath, localRelative));
            }
        }
        return null;
    }

    // ─── 文件哈希 ──────────────────────────────────────────────────────────────

    /// <summary>计算磁盘文件的 SHA-256 哈希（小写十六进制）。</summary>
    /// <param name="filePath">要计算的文件路径</param>
    public static string ComputeFileHash(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>计算内存中字节数组的 SHA-256 哈希，用于网络下载内容的完整性校验。</summary>
    public static string ComputeContentHash(byte[] content)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(content)).ToLowerInvariant();
    }

    // ─── 私有辅助 ──────────────────────────────────────────────────────────────

    private string GetVersionFilePath(string localPath)
    {
        // 用路径的哈希作为版本文件名，避免路径分隔符问题
        var key = NormalizeLocalPath(localPath).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(VersionsPath, $"{hash}.json");
    }

    private static string NormalizeLocalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        if (OperatingSystem.IsMacOS() && fullPath.StartsWith("/private/tmp", StringComparison.Ordinal))
        {
            var suffix = fullPath["/private/tmp".Length..];
            return "/tmp" + suffix;
        }

        return fullPath;
    }

    private void EnsureExists()
    {
        if (!File.Exists(WorkspacePath))
            throw new InvalidOperationException(
                $"No workspace found at '{_rootPath}'. Run 'arm-tfs workspace new' to create one.");
    }

    /// <summary>确保 .tf 目录被 Git 忽略</summary>
    private void EnsureGitIgnore()
    {
        var gitIgnore = Path.Combine(_rootPath, ".gitignore");
        const string entry = ".tf/";
        if (!File.Exists(gitIgnore))
        {
            File.WriteAllText(gitIgnore, $"{entry}\n");
            return;
        }
        var content = File.ReadAllText(gitIgnore);
        if (!content.Contains(entry, StringComparison.Ordinal))
            File.AppendAllText(gitIgnore, $"\n{entry}\n");
    }
}
