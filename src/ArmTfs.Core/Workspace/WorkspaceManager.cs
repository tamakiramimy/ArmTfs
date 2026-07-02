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
///       base/            - 已下载/提交后的基线文件内容缓存
///       versions/        - 已追踪文件的版本信息（每文件一个 JSON）
/// </summary>
public sealed class WorkspaceManager
{
    private const string TfDir = ".tf";
    private const string WorkspaceFile = "workspace.json";
    private const string PendingFile = "pending.json";
    private const string MergeHistoryFile = "merge-history.json";
    private const string BaseDir = "base";
    private const string VersionsDir = "versions";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _rootPath;
    private Dictionary<string, TrackedFileVersion>? _trackedByLocalPath;
    private Dictionary<string, TrackedFileVersion>? _trackedByServerPath;

    /// <summary>初始化，使用指定目录作为工作区根目录。</summary>
    /// <param name="rootPath">工作区根目录（内部会转为绝对路径）</param>
    public WorkspaceManager(string rootPath)
    {
        _rootPath = NormalizeLocalPath(rootPath);
    }

    private string TfPath => Path.Combine(_rootPath, TfDir);
    private string WorkspacePath => Path.Combine(TfPath, WorkspaceFile);
    private string PendingPath => Path.Combine(TfPath, PendingFile);
    private string MergeHistoryPath => Path.Combine(TfPath, MergeHistoryFile);
    private string BaseFilesPath => Path.Combine(TfPath, BaseDir);
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
        var normalizedLocalPath = NormalizeLocalPath(localPath);
        var file = GetVersionFilePath(normalizedLocalPath);
        if (File.Exists(file))
        {
            var json = File.ReadAllText(file);
            var direct = JsonSerializer.Deserialize<TrackedFileVersion>(json, _jsonOptions);
            return direct is null ? null : WithCurrentLocalPath(direct, normalizedLocalPath);
        }

        var byLocalPath = FindTrackedVersionByNormalizedPath(normalizedLocalPath);
        if (byLocalPath is not null)
            return byLocalPath;

        try
        {
            var metadata = LoadMetadata();
            var serverPath = LocalToServerPath(normalizedLocalPath, metadata);
            if (!string.IsNullOrWhiteSpace(serverPath))
            {
                var byServerPath = FindTrackedVersionByServerPath(serverPath);
                if (byServerPath is not null)
                    return WithCurrentLocalPath(byServerPath, normalizedLocalPath);
            }
        }
        catch
        {
            // If workspace metadata is unavailable/corrupt, fall back to the direct lookup result.
        }

        return null;
    }

    /// <summary>将文件的版本快照序列化写入 .tf/versions/ 目录。</summary>
    public void SaveTrackedVersion(TrackedFileVersion version)
    {
        Directory.CreateDirectory(VersionsPath);
        var file = GetVersionFilePath(version.LocalPath);
        File.WriteAllText(file, JsonSerializer.Serialize(version, _jsonOptions));
        _trackedByLocalPath = null;
        _trackedByServerPath = null;
    }

    /// <summary>返回本地工作区中所有已追踪文件的最大 ChangesetId；无追踪文件时返回 null。</summary>
    public int? GetLocalMaxChangesetId()
    {
        if (!Directory.Exists(VersionsPath))
            return null;

        int max = 0;
        foreach (var file in Directory.EnumerateFiles(VersionsPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var version = JsonSerializer.Deserialize<TrackedFileVersion>(json, _jsonOptions);
                if (version is not null && version.ChangesetId > max)
                    max = version.ChangesetId;
            }
            catch
            {
                // Skip unreadable version files.
            }
        }

        return max > 0 ? max : null;
    }

    /// <summary>删除对应本地文件的版本快照（文件已删除时调用）。</summary>
    public void RemoveTrackedVersion(string localPath)
    {
        var file = GetVersionFilePath(localPath);
        if (File.Exists(file)) File.Delete(file);
        _trackedByLocalPath = null;
        _trackedByServerPath = null;
    }

    /// <summary>获取对应本地文件的基线缓存文件路径；不存在时返回 <c>null</c>。</summary>
    public string? GetCachedBaseFilePath(string localPath)
    {
        var file = GetBaseFilePath(localPath);
        return File.Exists(file) ? file : null;
    }

    /// <summary>将指定字节内容保存为本地文件的基线缓存。</summary>
    public void SaveBaseFile(string localPath, byte[] content)
    {
        Directory.CreateDirectory(BaseFilesPath);
        File.WriteAllBytes(GetBaseFilePath(localPath), content);
    }

    /// <summary>使用当前磁盘文件内容刷新其基线缓存。</summary>
    public void SaveBaseFileFromDisk(string localPath)
    {
        var normalizedPath = NormalizeLocalPath(localPath);
        Directory.CreateDirectory(BaseFilesPath);
        File.Copy(normalizedPath, GetBaseFilePath(normalizedPath), overwrite: true);
    }

    /// <summary>删除对应本地文件的基线缓存。</summary>
    public void RemoveBaseFile(string localPath)
    {
        var file = GetBaseFilePath(localPath);
        if (File.Exists(file)) File.Delete(file);
    }

    // ─── 合并追踪 ──────────────────────────────────────────────────────────────

    /// <summary>加载本地合并历史记录；文件不存在时返回空记录。</summary>
    public MergeHistory LoadMergeHistory()
    {
        if (!File.Exists(MergeHistoryPath))
            return new MergeHistory();

        try
        {
            var json = File.ReadAllText(MergeHistoryPath);
            return JsonSerializer.Deserialize<MergeHistory>(json, _jsonOptions) ?? new MergeHistory();
        }
        catch
        {
            return new MergeHistory();
        }
    }

    /// <summary>保存合并历史记录到 .tf/merge-history.json。</summary>
    public void SaveMergeHistory(MergeHistory history)
    {
        Directory.CreateDirectory(TfPath);
        File.WriteAllText(MergeHistoryPath, JsonSerializer.Serialize(history, _jsonOptions));
    }

    /// <summary>记录一次 REST merge 执行结果。</summary>
    public void RecordMerge(MergeRecord record)
    {
        var history = LoadMergeHistory();
        history.Merges.Add(record);
        SaveMergeHistory(history);
    }

    /// <summary>
    /// 查询指定源/目标路径对下，已通过 REST 方式合并的源 changeset ID 集合。
    /// 用于过滤合并候选列表。
    /// </summary>
    public HashSet<int> GetLocallyMergedChangesetIds(string sourcePath, string targetPath)
    {
        var history = LoadMergeHistory();
        return history.Merges
            .Where(m =>
                string.Equals(NormalizeTfvcPath(m.SourcePath), NormalizeTfvcPath(sourcePath), StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeTfvcPath(m.TargetPath), NormalizeTfvcPath(targetPath), StringComparison.OrdinalIgnoreCase))
            .Select(m => m.SourceChangesetId)
            .ToHashSet();
    }

    private static string NormalizeTfvcPath(string path) => path.TrimEnd('/');

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
        foreach (var mapping in metadata.Mappings
                     .Where(m => serverPath.StartsWith(m.ServerPath, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(m => m.ServerPath.Length)
                     .ThenByDescending(GetLocalMappingPreference))
        {
            var relative = serverPath[mapping.ServerPath.Length..].TrimStart('/');
            var localRelative = relative.Replace('/', Path.DirectorySeparatorChar);
            return string.IsNullOrEmpty(localRelative)
                ? NormalizeLocalPath(mapping.LocalPath)
                : NormalizeLocalPath(Path.Combine(mapping.LocalPath, localRelative));
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

    private string GetBaseFilePath(string localPath)
    {
        var key = NormalizeLocalPath(localPath).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        var extension = Path.GetExtension(key);
        return Path.Combine(BaseFilesPath, $"{hash}{extension}");
    }

    private TrackedFileVersion? FindTrackedVersionByNormalizedPath(string localPath)
    {
        var normalizedLocalPath = NormalizeLocalPath(localPath);
        EnsureTrackedVersionIndex();
        return _trackedByLocalPath!.TryGetValue(NormalizeLocalKey(normalizedLocalPath), out var version)
            ? WithCurrentLocalPath(version, normalizedLocalPath)
            : null;
    }

    private TrackedFileVersion? FindTrackedVersionByServerPath(string serverPath)
    {
        EnsureTrackedVersionIndex();
        return _trackedByServerPath!.TryGetValue(NormalizeServerKey(serverPath), out var version)
            ? version
            : null;
    }

    private void EnsureTrackedVersionIndex()
    {
        if (_trackedByLocalPath is not null && _trackedByServerPath is not null)
            return;

        _trackedByLocalPath = new Dictionary<string, TrackedFileVersion>(StringComparer.OrdinalIgnoreCase);
        _trackedByServerPath = new Dictionary<string, TrackedFileVersion>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(VersionsPath))
            return;

        foreach (var versionFile in Directory.EnumerateFiles(VersionsPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(versionFile);
                var candidate = JsonSerializer.Deserialize<TrackedFileVersion>(json, _jsonOptions);
                if (candidate is null || string.IsNullOrWhiteSpace(candidate.LocalPath))
                    continue;

                _trackedByLocalPath.TryAdd(NormalizeLocalKey(candidate.LocalPath), candidate);
                if (!string.IsNullOrWhiteSpace(candidate.ServerPath))
                    _trackedByServerPath.TryAdd(NormalizeServerKey(candidate.ServerPath), candidate);
            }
            catch
            {
                // Ignore corrupt or partially written version metadata.
            }
        }
    }

    private static TrackedFileVersion WithCurrentLocalPath(TrackedFileVersion version, string localPath)
    {
        return new TrackedFileVersion
        {
            ServerPath = version.ServerPath,
            LocalPath = localPath,
            ChangesetId = version.ChangesetId,
            ContentHash = version.ContentHash,
            DownloadedAt = version.DownloadedAt,
        };
    }

    private static string NormalizeLocalPath(string path)
    {
        var fullPath = Path.GetFullPath(TranslatePlatformSharedPath(path));

        if (OperatingSystem.IsMacOS() && fullPath.StartsWith("/private/tmp", StringComparison.Ordinal))
        {
            var suffix = fullPath["/private/tmp".Length..];
            return "/tmp" + suffix;
        }

        return fullPath;
    }

    private int GetLocalMappingPreference(WorkspaceMapping mapping)
    {
        var score = 0;
        var mappedPath = NormalizeLocalPath(mapping.LocalPath);
        if (IsSameOrChildPath(_rootPath, mappedPath))
            score += 10_000;

        if (Directory.Exists(mappedPath) || File.Exists(mappedPath))
            score += 1_000;

        if (OperatingSystem.IsWindows() == IsWindowsDrivePath(mapping.LocalPath))
            score += 100;
        if ((OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) && Path.IsPathRooted(mapping.LocalPath) && !IsWindowsDrivePath(mapping.LocalPath))
            score += 100;

        return score;
    }

    private static bool IsSameOrChildPath(string candidatePath, string parentPath)
    {
        var candidate = NormalizeLocalPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = NormalizeLocalPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsDrivePath(string path)
    {
        return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
    }

    private static string NormalizeLocalKey(string path) => NormalizeLocalPath(path).ToLowerInvariant();

    private static string NormalizeServerKey(string path) => NormalizeTfvcPath(path).ToLowerInvariant();

    private static string TranslatePlatformSharedPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var sharedHome = GetPlatformSharedHomeDirectory();
        if (sharedHome is null)
            return path;

        foreach (var prefix in new[] { "//Mac/Home/", "/Mac/Home/" })
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(sharedHome, normalized[prefix.Length..]);
        }

        var withoutDrive = normalized.Length >= 3 && char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/'
            ? normalized[3..]
            : normalized;
        if (withoutDrive.StartsWith("Mac/Home/", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(sharedHome, withoutDrive["Mac/Home/".Length..]);

        return path;
    }

    private static string? GetPlatformSharedHomeDirectory()
    {
        if (OperatingSystem.IsMacOS())
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            const string windowsSharedHome = @"C:\Mac\Home";
            if (Directory.Exists(windowsSharedHome))
                return windowsSharedHome;
        }

        return null;
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
