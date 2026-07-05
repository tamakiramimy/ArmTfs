using ArmTfs.Core.Client.Soap;
using ArmTfs.Core.Models;
using ArmTfs.Core.Workspace;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using System.Text;
using System.Text.RegularExpressions;

namespace ArmTfs.Core.Client;

/// <summary>
/// 封装 <see cref="TfvcHttpClient"/>，提供面向业务的 TFVC 操作接口。
/// 所有操作均为 REST 调用，无平台原生依赖，支持 ARM64 macOS / Windows ARM / Linux。
/// <para>
/// 调用方应自行维护 <see cref="TfsConnection"/> 的生命周期（<c>using</c> 语句）。
/// </para>
/// </summary>
public sealed class TfvcClientService
{
    private readonly TfsConnection _connection;
    private readonly WorkspaceManager? _workspaceManager;

    /// <summary>初始化服务。</summary>
    /// <param name="connection">已创建的连接对象，生命周期由调用方管理</param>
    /// <param name="workspaceManager">可选的本地工作区管理器，用于合并历史追踪</param>
    public TfvcClientService(TfsConnection connection, WorkspaceManager? workspaceManager = null)
    {
        _connection = connection;
        _workspaceManager = workspaceManager;
    }

    // ─── 条目查询 ──────────────────────────────────────────────────────────────

    /// <summary>获取服务器路径下的所有文件条目。</summary>
    /// <param name="serverPath">TFVC 服务器路径，如 $/MyProject/Main</param>
    /// <param name="recursive">是否递归列出子目录（默认 true）</param>
    /// <param name="atChangeset">指定 Changeset 版本号；<c>null</c> 表示最新版本</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>服务器条目列表，包含文件和目录</returns>
    public async Task<IReadOnlyList<TfsServerItem>> GetItemsAsync(
        string serverPath,
        bool recursive = true,
        int? atChangeset = null,
        CancellationToken ct = default,
        bool oneLevelOnly = false)
    {
        var soap = new Soap.TfvcSoapClient(_connection);
        var recursion = oneLevelOnly ? "OneLevel" : (recursive ? "Full" : "None");
        var items = await soap.QueryItemsAsync(serverPath, recursion, atChangeset, ct).ConfigureAwait(false);

        return items.Select(i => new TfsServerItem
        {
            ServerPath = i.ServerPath,
            IsFolder = i.IsFolder,
            ChangesetId = i.ChangesetId,
            ContentLength = i.ContentLength,
            HashValue = i.HashValue,
            CheckinDate = i.CheckinDate,
        }).ToList();
    }

    /// <summary>下载单个文件的内容并写入目标流。</summary>
    /// <param name="serverPath">TFVC 服务器文件路径</param>
    /// <param name="destination">写入目标（不自动 Seek/重置，调用方需自行准备）</param>
    /// <param name="atChangeset">指定 Changeset 版本号；<c>null</c> 表示最新</param>
    /// <param name="ct">取消令牌</param>
    public async Task DownloadFileAsync(
        string serverPath,
        Stream destination,
        int? atChangeset = null,
        CancellationToken ct = default)
    {
        var soap = new Soap.TfvcSoapClient(_connection);
        await soap.DownloadFileToStreamAsync(serverPath, destination, atChangeset, ct).ConfigureAwait(false);
    }

    // ─── Changeset 查询 ────────────────────────────────────────────────────────

    /// <summary>按条件查询 Changeset 列表，默认按 ID 降序返回。</summary>
    /// <param name="serverPath">TFVC 服务器路径过滤；<c>null</c> 表示不过滤</param>
    /// <param name="author">按作者显示名过滤；<c>null</c> 表示不过滤</param>
    /// <param name="top">最大返回条数（默认 20）</param>
    /// <param name="ct">取消令牌</param>
    public async Task<IReadOnlyList<TfvcChangesetRef>> GetChangesetsAsync(
        string? serverPath = null,
        string? author = null,
        int top = 20,
        int skip = 0,
        string orderby = "id desc",
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var soap = new Soap.TfvcSoapClient(_connection);
        var queryPath = serverPath ?? "$/";
        var history = await soap.QueryHistoryAsync(queryPath, top + skip, false, ct).ConfigureAwait(false);

        IEnumerable<Models.Soap.SoapChangeset> filtered = history;

        // Apply author filter (SOAP QueryHistory doesn't support author filtering directly)
        if (!string.IsNullOrEmpty(author))
            filtered = filtered.Where(h =>
                (h.Author != null && h.Author.Contains(author, StringComparison.OrdinalIgnoreCase)) ||
                (h.AuthorUniqueName != null && h.AuthorUniqueName.Contains(author, StringComparison.OrdinalIgnoreCase)));

        // Apply date filters
        if (fromDate.HasValue)
            filtered = filtered.Where(h => h.CreatedDate.HasValue && h.CreatedDate.Value >= fromDate.Value);
        if (toDate.HasValue)
            filtered = filtered.Where(h => h.CreatedDate.HasValue && h.CreatedDate.Value <= toDate.Value);

        // Apply ordering (SOAP returns desc by default)
        if (orderby.Contains("asc", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.OrderBy(h => h.ChangesetId);

        // Apply skip/take
        var results = filtered.Skip(skip).Take(top).ToList();

        return results.Select(h => new TfvcChangesetRef
        {
            ChangesetId = h.ChangesetId,
            Author = h.Author != null ? new IdentityRef { DisplayName = h.Author, UniqueName = h.AuthorUniqueName ?? h.Author } : null,
            CreatedDate = h.CreatedDate?.DateTime ?? DateTime.MinValue,
            Comment = h.Comment,
        }).ToList();
    }

    /// <summary>
    /// 返回服务器上 serverPath 路径下，changesetId 之后（更新的）有多少个 changeset，
    /// 即本地落后服务器的数量（⬇N）。最多扫 scanTop 条历史。
    /// </summary>
    public async Task<int> GetBehindCountAsync(string serverPath, int localMaxChangesetId, int scanTop = 50, CancellationToken ct = default)
    {
        var history = await GetChangesetsAsync(serverPath, top: scanTop, ct: ct).ConfigureAwait(false);
        return history.Count(cs => cs.ChangesetId > localMaxChangesetId);
    }

    /// <summary>获取单个 Changeset 的详细信息，包括文件列表和合并来源。</summary>
    /// <param name="changesetId">Changeset 编号</param>
    /// <param name="ct">取消令牌</param>
    public async Task<TfvcChangeset> GetChangesetAsync(int changesetId, CancellationToken ct = default)
    {
        var soap = new Soap.TfvcSoapClient(_connection);

        // 1. Get changeset metadata (author, date, comment)
        var metadata = await soap.QueryChangesetMetadataAsync(changesetId, ct).ConfigureAwait(false);

        // 2. Get all file changes with merge sources
        var soapChanges = await soap.QueryChangesForChangesetAsync(changesetId, ct).ConfigureAwait(false);

        // 3. Build TfvcChangeset from SOAP results
        var changeset = new TfvcChangeset
        {
            ChangesetId = metadata.ChangesetId,
            Author = metadata.Author != null
                ? new IdentityRef { DisplayName = metadata.Author, UniqueName = metadata.AuthorUniqueName ?? metadata.Author }
                : null,
            CreatedDate = metadata.CreatedDate?.DateTime ?? DateTime.MinValue,
            Comment = metadata.Comment,
            HasMoreChanges = false,
        };

        var changes = new List<TfvcChange>();
        foreach (var sc in soapChanges)
        {
            var changeType = ParseSoapChangeType(sc.ChangeType);
            var tfvcChange = new TfvcChange
            {
                Item = new TfvcItem
                {
                    Path = sc.ServerPath,
                    IsFolder = sc.IsFolder,
                    ChangesetVersion = sc.ItemChangesetVersion,
                },
                ChangeType = changeType,
            };

            if (sc.MergeSources.Count > 0)
            {
                tfvcChange.MergeSources = sc.MergeSources.Select(ms => new TfvcMergeSource
                {
                    ServerItem = ms.ServerItem,
                    VersionFrom = ms.VersionFrom ?? 0,
                    VersionTo = ms.VersionTo ?? 0,
                    IsRename = ms.IsRename,
                }).ToList();
            }

            changes.Add(tfvcChange);
        }

        changeset.Changes = changes;
        return changeset;
    }

    /// <summary>
    /// 解析 SOAP 返回的 ChangeType 字符串（空格分隔的 flags）为 VersionControlChangeType 枚举。
    /// 例如 "Edit Merge" → VersionControlChangeType.Edit | VersionControlChangeType.Merge
    /// </summary>
    private static VersionControlChangeType ParseSoapChangeType(string? typeStr)
    {
        if (string.IsNullOrWhiteSpace(typeStr))
            return VersionControlChangeType.None;

        var result = VersionControlChangeType.None;
        foreach (var part in typeStr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<VersionControlChangeType>(part, true, out var parsed))
                result |= parsed;
        }

        return result;
    }

    /// <summary>
    /// 通过 SOAP Repository.asmx CreateBranch 接口创建 TFVC 分支。
    /// REST Changeset API 不支持 Branch 变更类型，必须使用旧版 SOAP 接口。
    /// </summary>
    public async Task<TfvcChangesetRef> CreateBranchAsync(
        string sourcePath,
        string targetPath,
        int? sourceChangesetId = null,
        string? comment = null,
        CancellationToken ct = default)
    {
        var normalizedSource = NormalizeServerPath(sourcePath);
        var normalizedTarget = NormalizeServerPath(targetPath);
        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source and target branch paths must be different.");

        // Verify source exists
        if (await TryGetItemVersionAsync(normalizedSource, ct).ConfigureAwait(false) is null)
            throw new InvalidOperationException($"Source branch '{normalizedSource}' does not exist.");

        var effectiveComment = string.IsNullOrWhiteSpace(comment)
            ? $"Branch {normalizedTarget} from {normalizedSource}"
            : comment.Trim();

        var changesetId = await CreateBranchViaSoapAsync(
            normalizedSource, normalizedTarget, sourceChangesetId, effectiveComment, ct).ConfigureAwait(false);

        return new TfvcChangesetRef { ChangesetId = changesetId };
    }

    private async Task<int> CreateBranchViaSoapAsync(
        string sourcePath,
        string targetPath,
        int? sourceChangesetId,
        string comment,
        CancellationToken ct)
    {
        // TFS REST API does not support Branch change type; use legacy SOAP endpoint.
        var soap = new TfvcSoapClient(_connection);
        return await soap.CreateBranchAsync(sourcePath, targetPath, sourceChangesetId, comment, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除一个服务器路径（文件或文件夹/分支）。通过创建一个含 Delete 变更的 changeset 实现，
    /// 即 TFS 的"可撤销删除"（区别于 tf destroy 永久销毁）。删除文件夹/分支时 TFS 递归删除其下所有内容。
    /// </summary>
    public async Task<TfvcChangesetRef> DeleteItemAsync(
        string serverPath,
        string? comment = null,
        CancellationToken ct = default)
    {
        var normalized = NormalizeServerPath(serverPath);
        var effectiveComment = string.IsNullOrWhiteSpace(comment)
            ? $"Delete {normalized}"
            : comment.Trim();

        // Verify the item exists (and is not already deleted). TFS requires the item's current
        // changesetVersion as the base for a Delete change ("请指定项版本").
        var version = await TryGetItemVersionAsync(normalized, ct).ConfigureAwait(false);
        if (!version.HasValue)
        {
            throw new InvalidOperationException($"'{normalized}' does not exist (or is already deleted) on the server.");
        }

        return await CheckinAsync(
            effectiveComment,
            new[] { (normalized, Models.ChangeType.Delete, (byte[]?)null, version) },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 重命名/移动服务器文件（通过 Add 新路径 + Delete 旧路径实现内容迁移）。
    /// 注意：TFS SOAP PendChanges Rename 需要本地工作区文件同步，纯服务器侧无法使用真正 Rename 变更类型。
    /// 此实现通过下载源内容并用 Add+Delete 提交，效果等同于移动文件。
    /// </summary>
    public async Task<int> RenameItemAsync(
        string oldServerPath,
        string newServerPath,
        string? comment = null,
        string? soapOwner = null,
        CancellationToken ct = default)
    {
        var normalizedOld = NormalizeServerPath(oldServerPath);
        var normalizedNew = NormalizeServerPath(newServerPath);
        if (string.Equals(normalizedOld, normalizedNew, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source and target paths are the same.");

        var oldVersion = await TryGetItemVersionAsync(normalizedOld, ct).ConfigureAwait(false);
        if (!oldVersion.HasValue)
            throw new InvalidOperationException($"'{normalizedOld}' does not exist on the server.");

        var effectiveComment = string.IsNullOrWhiteSpace(comment)
            ? $"Move {normalizedOld} to {normalizedNew}"
            : comment.Trim();

        // Download source content
        var content = await DownloadFileContentAsync(normalizedOld, null, ct).ConfigureAwait(false);

        // Create a single changeset via SOAP: Add new path + Delete old path
        var soapChanges = new List<(string serverPath, string changeType, byte[]? content, int? baseVersion)>
        {
            (normalizedNew, "Add", content, null),
            (normalizedOld, "Delete", null, oldVersion),
        };
        return await CreateChangesetViaSoapAsync(effectiveComment, soapChanges, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 还原已删除的服务器文件/文件夹（tf undelete）。
    /// 通过查找删除前的最后版本内容并重新 Add 实现（REST Undelete 变更类型不被服务器支持）。
    /// </summary>
    public async Task<TfvcChangesetRef> UndeleteItemAsync(
        string serverPath,
        string? comment = null,
        int? deletionId = null,
        CancellationToken ct = default)
    {
        var normalized = NormalizeServerPath(serverPath);
        var effectiveComment = string.IsNullOrWhiteSpace(comment)
            ? $"Undelete {normalized}"
            : comment.Trim();

        // Find the latest changeset that had the file (before deletion)
        // We scan recent changesets to find the deletion event and get the prior version
        var history = await GetChangesetsAsync(serverPath: normalized, top: 10, ct: ct).ConfigureAwait(false);
        var deletionCs = history.FirstOrDefault(cs =>
        {
            // We check changeset details to find delete
            return true; // simplification: take latest - 1
        });

        // Try to download the file at the version just before the first history entry
        // (which was likely the deletion changeset). Fall back to latest-1.
        int? restoreFromVersion = null;
        foreach (var cs in history)
        {
            var detail = await GetChangesetAsync(cs.ChangesetId, ct).ConfigureAwait(false);
            var change = detail.Changes?.FirstOrDefault(c =>
                string.Equals(c.Item?.Path, normalized, StringComparison.OrdinalIgnoreCase));
            if (change is not null)
            {
                if (change.ChangeType.HasFlag(VersionControlChangeType.Delete))
                {
                    // Download from the changeset BEFORE this delete
                    restoreFromVersion = cs.ChangesetId - 1;
                    break;
                }
            }
        }

        if (restoreFromVersion is null)
            throw new InvalidOperationException($"Cannot find a prior version of '{normalized}' to restore. The file may not have been deleted recently.");

        byte[] content;
        try
        {
            content = await DownloadFileContentAsync(normalized, restoreFromVersion, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cannot download prior version of '{normalized}' at CS#{restoreFromVersion}: {ex.Message}", ex);
        }

        // Re-add the file with its restored content via SOAP
        var soapChanges = new List<(string serverPath, string changeType, byte[]? content, int? baseVersion)>
        {
            (normalized, "Add", content, null),
        };
        var changesetId = await CreateChangesetViaSoapAsync(effectiveComment, soapChanges, ct).ConfigureAwait(false);
        return new TfvcChangesetRef { ChangesetId = changesetId };
    }

    /// <summary>
    /// 回滚指定 changeset 的内容变更（tf rollback）。
    /// 对于 changeset 中每个文件变更：
    ///   Edit/Add → 还原到该 changeset 之前的版本内容（如是首次 Add 则 Delete）
    ///   Delete   → 还原(Undelete)该文件
    /// 生成一个新的 changeset，服务器历史可追溯。
    /// </summary>
    public async Task<TfvcChangesetRef> RollbackChangesetAsync(
        int changesetId,
        string? comment = null,
        CancellationToken ct = default)
    {
        var detail = await GetChangesetAsync(changesetId, ct).ConfigureAwait(false);
        var changes = (detail.Changes ?? Array.Empty<TfvcChange>())
            .Where(c => c.Item?.IsFolder != true && !string.IsNullOrEmpty(c.Item?.Path))
            .ToList();

        if (changes.Count == 0)
            throw new InvalidOperationException($"Changeset {changesetId} has no file changes to roll back.");

        var effectiveComment = string.IsNullOrWhiteSpace(comment)
            ? $"Rollback changeset {changesetId}"
            : comment.Trim();

        var soapChanges = new List<(string serverPath, string changeType, byte[]? content, int? baseVersion)>();

        foreach (var c in changes)
        {
            var path = c.Item!.Path!;
            var ct2 = c.ChangeType;

            if (ct2.HasFlag(VersionControlChangeType.Undelete))
            {
                // The file was undeleted in this changeset — rolling back means deleting it again
                var ver = await TryGetItemVersionAsync(path, ct).ConfigureAwait(false);
                if (ver.HasValue)
                    soapChanges.Add((path, "Delete", null, ver));
            }
            else if (ct2.HasFlag(VersionControlChangeType.Add))
            {
                // The file was added in this changeset — rolling back means deleting it
                var ver = await TryGetItemVersionAsync(path, ct).ConfigureAwait(false);
                if (ver.HasValue)
                    soapChanges.Add((path, "Delete", null, ver));
            }
            else if (ct2.HasFlag(VersionControlChangeType.Delete))
            {
                // The file was deleted — rolling back means re-adding it
                var prevVersion = changesetId - 1;
                byte[]? content = null;
                try
                {
                    content = await DownloadFileContentAsync(path, prevVersion, ct).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }
                soapChanges.Add((path, "Add", content, null));
            }
            else if (ct2.HasFlag(VersionControlChangeType.Edit) || ct2.HasFlag(VersionControlChangeType.Rename))
            {
                // Restore content from the changeset BEFORE this one
                var prevVersion = changesetId - 1;
                byte[]? content = null;
                try
                {
                    content = await DownloadFileContentAsync(path, prevVersion, ct).ConfigureAwait(false);
                }
                catch
                {
                    // File may not have existed before changesetId; skip
                    continue;
                }

                var currentVer = await TryGetItemVersionAsync(path, ct).ConfigureAwait(false);
                if (currentVer is null) continue;

                soapChanges.Add((path, "Edit", content, currentVer));
            }
        }

        if (soapChanges.Count == 0)
            throw new InvalidOperationException($"No rollback-able changes found in changeset {changesetId}.");

        var csetId = await CreateChangesetViaSoapAsync(effectiveComment, soapChanges, ct).ConfigureAwait(false);
        return new TfvcChangesetRef { ChangesetId = csetId };
    }

    /// <summary>
    /// Revert a server path to the exact state at a specific changeset version.
    /// Compares file trees at the target version and the current (latest) version,
    /// then creates one atomic changeset that makes them match.
    /// Uses proper diff (Delete/Add/Edit) and retries excluding files with pending changes.
    /// </summary>
    public async Task<TfvcChangesetRef> RevertToVersionAsync(
        string serverPath,
        int targetChangesetId,
        string? comment = null,
        CancellationToken ct = default)
    {
        var normalizedPath = NormalizeServerPath(serverPath);

        // Clean up any stale SOAP workspaces that might be holding pending changes
        await CleanupStaleSoapWorkspacesAsync(ct).ConfigureAwait(false);

        var effectiveComment = string.IsNullOrWhiteSpace(comment)
            ? $"Revert {normalizedPath} to version cs{targetChangesetId}"
            : comment.Trim();

        // Get ALL files at target version
        var targetItems = await GetItemsAsync(normalizedPath, recursive: true, atChangeset: targetChangesetId, ct: ct).ConfigureAwait(false);
        // Get ALL files at current version
        var currentItems = await GetItemsAsync(normalizedPath, recursive: true, atChangeset: null, ct: ct).ConfigureAwait(false);

        var targetFiles = targetItems
            .Where(i => !i.IsFolder)
            .ToDictionary(i => i.ServerPath, StringComparer.OrdinalIgnoreCase);
        var currentFiles = currentItems
            .Where(i => !i.IsFolder)
            .ToDictionary(i => i.ServerPath, StringComparer.OrdinalIgnoreCase);

        // Build change list: make current state match target state (using SOAP tuples)
        var soapChanges = new List<(string serverPath, string changeType, byte[]? content, int? baseVersion)>();

        // Files in current but NOT in target -> Delete
        foreach (var (path, item) in currentFiles)
        {
            if (!targetFiles.ContainsKey(path))
            {
                soapChanges.Add((path, "Delete", null, item.ChangesetId));
            }
        }

        // Files in target but NOT in current -> Add
        foreach (var (path, item) in targetFiles)
        {
            if (!currentFiles.ContainsKey(path))
            {
                var content = await DownloadFileContentAsync(path, targetChangesetId, ct).ConfigureAwait(false);
                soapChanges.Add((path, "Add", content, null));
            }
        }

        // Files in BOTH -> Edit (overwrite with target content if different)
        foreach (var (path, targetItem) in targetFiles)
        {
            if (!currentFiles.TryGetValue(path, out var currentItem)) continue;
            if (targetItem.ChangesetId == currentItem.ChangesetId) continue; // Same version, skip

            byte[] targetContent;
            byte[] currentContent;
            try
            {
                targetContent = await DownloadFileContentAsync(path, targetChangesetId, ct).ConfigureAwait(false);
                currentContent = await DownloadFileContentAsync(path, null, ct).ConfigureAwait(false);
            }
            catch { continue; }

            if (ContentEquals(targetContent, currentContent)) continue;
            soapChanges.Add((path, "Edit", targetContent, currentItem.ChangesetId));
        }

        if (soapChanges.Count == 0)
            throw new InvalidOperationException($"Already at target state (cs{targetChangesetId}).");

        // Try to submit. If pending changes block it, retry excluding problematic files.
        var skippedFiles = new List<string>();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var submitComment = skippedFiles.Count > 0
                    ? $"{effectiveComment} [skipped {skippedFiles.Count} file(s) with pending changes]"
                    : effectiveComment;
                var csetId = await CreateChangesetViaSoapAsync(submitComment, soapChanges, ct).ConfigureAwait(false);
                return new TfvcChangesetRef { ChangesetId = csetId };
            }
            catch (Exception ex) when (ex.Message.Contains("挂起的更改") || ex.Message.Contains("pending change"))
            {
                // Extract the blocking file path from error message
                // Pattern: "项 $/path/to/file 已有挂起的更改"
                var match = Regex.Match(
                    ex.Message, @"项\s+(.*?)\s+已有挂起的更改");
                if (!match.Success) throw;

                var blockedPath = match.Groups[1].Value.Trim();
                skippedFiles.Add(blockedPath);

                // Remove ALL changes related to this path
                soapChanges.RemoveAll(c =>
                    string.Equals(c.serverPath, blockedPath, StringComparison.OrdinalIgnoreCase));

                if (soapChanges.Count == 0)
                    throw new InvalidOperationException(
                        $"All changes blocked by pending changes. Blocked files: {string.Join(", ", skippedFiles)}");
            }
        }

        throw new InvalidOperationException(
            $"Too many files with pending changes. Skipped: {string.Join(", ", skippedFiles)}");
    }

    /// <summary>
    /// 创建 TFVC Label（给服务器路径打标签）。
    /// 通过 SOAP LabelItem 实现（REST 不支持创建 label）。
    /// </summary>
    public async Task<string> CreateLabelAsync(
        string labelName,
        string serverPath,
        string? comment = null,
        int? atChangeset = null,
        CancellationToken ct = default)
    {
        var normalized = NormalizeServerPath(serverPath);
        var soap = new Soap.TfvcSoapClient(_connection);
        return await soap.LabelItemAsync(labelName, normalized, comment, atChangeset, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除 TFVC Label（先通过 SOAP QueryLabels 获取 scope，再 SOAP UnlabelItem）。
    /// labelId 参数可为标签名或数字 ID。
    /// </summary>
    public async Task DeleteLabelAsync(string labelId, CancellationToken ct = default)
    {
        // Resolve the label's actual scope via SOAP QueryLabels (UnlabelItem requires exact scope)
        string? scope = null;
        string labelName = labelId;

        try
        {
            var soap = new Soap.TfvcSoapClient(_connection);
            var labels = await soap.QueryLabelsAsync(labelName: labelId, ct: ct).ConfigureAwait(false);
            var found = labels.FirstOrDefault(l => string.Equals(l.Name, labelId, StringComparison.OrdinalIgnoreCase))
                     ?? labels.FirstOrDefault();
            if (found is not null)
            {
                scope = found.Scope;
                labelName = found.Name;
            }
        }
        catch
        {
            // Fall through with null scope — SOAP will fail with informative error
        }

        var soapClient = new Soap.TfvcSoapClient(_connection);
        await soapClient.DeleteLabelAsync(labelName, scope ?? "$/", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 锁定/解锁服务器文件（tf lock / tf lock /lock:none）。
    /// 通过 SOAP PendChanges 实现：lockLevel CheckOut（独占）或 Unchanged（解锁）。
    /// </summary>
    public async Task<int> LockItemAsync(
        string serverPath,
        bool lockIt,
        string? soapOwner = null,
        CancellationToken ct = default)
    {
        var normalized = NormalizeServerPath(serverPath);

        var owner = soapOwner;
        if (string.IsNullOrWhiteSpace(owner))
            owner = await _connection.GetAuthenticatedUserGuidAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Cannot resolve authenticated user for lock. Pass --soap-owner.");

        var soap = new Soap.TfvcSoapClient(_connection);
        var workspaceName = $"arm-tfs-lock-{Guid.NewGuid():N}";
        var computer = Environment.MachineName;
        var tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arm-tfs-lock", workspaceName);
        var folders = new[] { (normalized, tempRoot) };

        Models.Soap.SoapWorkspace? created = null;
        try
        {
            var ws = await soap.CreateWorkspaceAsync(workspaceName, owner, computer, "arm-tfs lock", folders, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ws.Owner)) owner = ws.Owner;
            created = ws;

            var lockLevel = lockIt ? "CheckOut" : "None";
            return await soap.PendLockAsync(workspaceName, owner, normalized, lockLevel, ct).ConfigureAwait(false);
        }
        finally
        {
            if (created is not null)
            {
                try { await soap.DeleteWorkspaceAsync(workspaceName, owner, ct).ConfigureAwait(false); }
                catch (Exception ex) { Console.Error.WriteLine($"warning: lock workspace cleanup failed: {ex.Message}"); }
            }
        }
    }

    /// <summary>
    /// 创建 Shelveset（暂存当前工作区的挂起变更，不 checkin）。
    /// 通过 SOAP ShelveChanges 实现：指定 serverPath 列表（或全部挂起变更）。
    /// </summary>
    public async Task<string> CreateShelvesetAsync(
        string shelvesetName,
        IReadOnlyList<(string serverPath, Models.ChangeType changeType, byte[]? content, int? baseVersion)> changes,
        string? comment = null,
        bool replace = false,
        string? soapOwner = null,
        CancellationToken ct = default)
    {
        if (changes.Count == 0)
            throw new ArgumentException("At least one change is required to shelve.", nameof(changes));

        var owner = soapOwner;
        if (string.IsNullOrWhiteSpace(owner))
            owner = await _connection.GetAuthenticatedUserGuidAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Cannot resolve authenticated user. Pass --soap-owner.");

        var soap = new Soap.TfvcSoapClient(_connection);
        var workspaceName = $"arm-tfs-shelve-{Guid.NewGuid():N}";
        var computer = Environment.MachineName;

        // Build working folder mappings: every unique server root referenced by the changes
        var roots = changes.Select(c => GetServerRoot(c.serverPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arm-tfs-shelve", workspaceName);
        var folders = roots.Select((r, i) => (r, System.IO.Path.Combine(tempRoot, $"m{i}"))).ToList();

        Models.Soap.SoapWorkspace? created = null;
        try
        {
            var ws = await soap.CreateWorkspaceAsync(workspaceName, owner, computer, "arm-tfs shelve", folders, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ws.Owner)) owner = ws.Owner;
            created = ws;

            // Pend the changes in this temporary workspace
            var pendChanges = new List<Models.Soap.SoapShelveChange>();
            foreach (var (serverPath, changeType, content, baseVersion) in changes)
            {
                // SOAP Shelve with Add requires UploadFile (MTOM) which is complex.
                // Treat Add as Edit (pending server-side change for already-existing file).
                var soapType = changeType == Models.ChangeType.Add ? "Edit" : ChangeTypeToSoapString(changeType);
                pendChanges.Add(new Models.Soap.SoapShelveChange
                {
                    ServerPath = serverPath,
                    ChangeTypeStr = soapType,
                    Content = content,
                    BaseVersion = baseVersion,
                });
            }

            await soap.ShelveAsync(workspaceName, owner, shelvesetName, pendChanges, comment ?? string.Empty, replace, ct).ConfigureAwait(false);
            return shelvesetName;
        }
        finally
        {
            if (created is not null)
            {
                try { await soap.DeleteWorkspaceAsync(workspaceName, owner, ct).ConfigureAwait(false); }
                catch (Exception ex) { Console.Error.WriteLine($"warning: shelve workspace cleanup failed: {ex.Message}"); }
            }
        }
    }

    /// <summary>
    /// Unshelve：将 shelveset 内容下载到本地工作区（不 checkin，放入挂起变更）。
    /// REST GetShelvesetChanges → download each file content → write to local workspace.
    /// </summary>
    public async Task<IReadOnlyList<string>> UnshelveAsync(
        string shelvesetName,
        string? owner,
        string localWorkspaceRoot,
        string serverRoot,
        CancellationToken ct = default)
    {
        // Resolve owner to GUID if needed (REST API requires name;GUID format)
        string? resolvedOwner = owner;
        if (string.IsNullOrEmpty(resolvedOwner))
        {
            // Find the shelveset to get the owner GUID
            var ssList = await GetShelvesetsAsync(name: shelvesetName, ct: ct).ConfigureAwait(false);
            var found = ssList.FirstOrDefault(s => string.Equals(s.Name, shelvesetName, StringComparison.OrdinalIgnoreCase));
            resolvedOwner = found?.Owner?.Id?.ToString() ?? found?.Owner?.UniqueName;
        }

        var changes = await GetShelvesetChangesAsync(shelvesetName, resolvedOwner, ct).ConfigureAwait(false);
        var written = new List<string>();

        foreach (var c in changes)
        {
            if (c.Item?.IsFolder == true || string.IsNullOrEmpty(c.Item?.Path)) continue;
            var serverPath = c.Item.Path;
            var relative = serverPath.StartsWith(serverRoot, StringComparison.OrdinalIgnoreCase)
                ? serverPath[serverRoot.Length..].TrimStart('/')
                : System.IO.Path.GetFileName(serverPath);
            var localPath = System.IO.Path.Combine(localWorkspaceRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(localPath)!);

            if (!c.ChangeType.HasFlag(VersionControlChangeType.Delete))
            {
                await using var stream = System.IO.File.Create(localPath);
                await DownloadFileAsync(serverPath, stream, c.Item.ChangesetVersion, ct).ConfigureAwait(false);
            }
            written.Add($"{c.ChangeType} {serverPath}");
        }

        return written;
    }

    /// <summary>
    /// 删除 Shelveset（SOAP DeleteShelveset）。
    /// </summary>
    public async Task DeleteShelvesetAsync(
        string shelvesetName,
        string? ownerName,
        string? soapOwner = null,
        CancellationToken ct = default)
    {
        var owner = soapOwner ?? ownerName;
        if (string.IsNullOrWhiteSpace(owner))
            owner = await _connection.GetAuthenticatedUserGuidAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Cannot resolve owner. Pass --owner or --soap-owner.");

        var soap = new Soap.TfvcSoapClient(_connection);
        await soap.DeleteShelvesetAsync(shelvesetName, owner, ct).ConfigureAwait(false);
    }

    private static string GetServerRoot(string serverPath)
    {
        // Return top-level project ($/Project)
        var parts = serverPath.TrimStart('/').Split('/');
        return parts.Length >= 2 ? $"$/{parts[1]}" : serverPath;
    }

    private static string ChangeTypeToSoapString(Models.ChangeType ct) => ct switch
    {
        Models.ChangeType.Add => "Add",
        Models.ChangeType.Edit => "Edit",
        Models.ChangeType.Delete => "Delete",
        Models.ChangeType.Rename => "Rename",
        Models.ChangeType.Undelete => "Undelete",
        _ => "Edit",
    };

    /// <summary>查询 TFVC Label 列表。</summary>
    public async Task<IReadOnlyList<TfvcLabelRef>> GetLabelsAsync(
        string? owner = null,
        string? name = null,
        string? labelScope = null,
        int? maxItemCount = null,
        int top = 20,
        int skip = 0,
        CancellationToken ct = default)
    {
        var soap = new Soap.TfvcSoapClient(_connection);
        var labels = await soap.QueryLabelsAsync(labelName: name, labelScope: labelScope, owner: owner, ct: ct).ConfigureAwait(false);

        return labels.Skip(skip).Take(top).Select(l => new TfvcLabelRef
        {
            Id = l.LabelId,
            Name = l.Name,
            Description = l.Comment,
            LabelScope = l.Scope,
            ModifiedDate = l.Date?.DateTime ?? DateTime.MinValue,
            Owner = l.Owner != null ? new IdentityRef { DisplayName = l.Owner, UniqueName = l.Owner } : null,
        }).ToList();
    }

    /// <summary>获取单个 TFVC Label 详情。</summary>
    public async Task<TfvcLabel> GetLabelAsync(
        string labelId,
        int? maxItemCount = null,
        CancellationToken ct = default)
    {
        var soap = new Soap.TfvcSoapClient(_connection);
        var labels = await soap.QueryLabelsAsync(labelName: labelId, ct: ct).ConfigureAwait(false);
        var found = labels.FirstOrDefault(l => string.Equals(l.Name, labelId, StringComparison.OrdinalIgnoreCase))
                 ?? labels.FirstOrDefault(l => l.LabelId.ToString() == labelId);

        if (found is null)
            throw new InvalidOperationException($"Label '{labelId}' not found.");

        return new TfvcLabel
        {
            Id = found.LabelId,
            Name = found.Name,
            Description = found.Comment,
            LabelScope = found.Scope,
            ModifiedDate = found.Date?.DateTime ?? DateTime.MinValue,
            Owner = found.Owner != null ? new IdentityRef { DisplayName = found.Owner, UniqueName = found.Owner } : null,
        };
    }

    // ─── Shelveset ─────────────────────────────────────────────────────────────

    /// <summary>查询 Shelveset 列表。</summary>
    /// <param name="owner">按所有者过滤；<c>null</c> 表示不过滤</param>
    /// <param name="name">按 Shelveset 名过滤；<c>null</c> 表示不过滤</param>
    /// <param name="ct">取消令牌</param>
    public async Task<IReadOnlyList<TfvcShelvesetRef>> GetShelvesetsAsync(
        string? owner = null,
        string? name = null,
        CancellationToken ct = default)
    {
        var soap = new Soap.TfvcSoapClient(_connection);
        var shelvesets = await soap.QueryShelvesetsAsync(shelvesetName: name, ownerName: owner, ct: ct).ConfigureAwait(false);

        return shelvesets.Select(ss => new TfvcShelvesetRef
        {
            Name = ss.Name,
            Owner = ss.Owner != null ? new IdentityRef
            {
                DisplayName = ss.OwnerDisplayName ?? ss.Owner,
                UniqueName = ss.Owner,
                Id = ss.Owner,
            } : null,
            CreatedDate = ss.Date?.DateTime ?? DateTime.MinValue,
            Comment = ss.Comment,
        }).ToList();
    }

    /// <summary>获取指定 Shelveset 中的文件变更列表。</summary>
    /// <param name="shelvesetName">Shelveset 名称（不含所有者后缀）</param>
    /// <param name="owner">所有者登录名；<c>null</c> 表示不指定（服务器匹配任意所有者）</param>
    /// <param name="ct">取消令牌</param>
    public async Task<IReadOnlyList<TfvcChange>> GetShelvesetChangesAsync(
        string shelvesetName,
        string? owner = null,
        CancellationToken ct = default)
    {
        // If owner is not provided, try to find the shelveset first to get the owner
        var resolvedOwner = owner;
        if (string.IsNullOrEmpty(resolvedOwner))
        {
            var soap = new Soap.TfvcSoapClient(_connection);
            var shelvesets = await soap.QueryShelvesetsAsync(shelvesetName: shelvesetName, ct: ct).ConfigureAwait(false);
            var found = shelvesets.FirstOrDefault(s => string.Equals(s.Name, shelvesetName, StringComparison.OrdinalIgnoreCase));
            resolvedOwner = found?.Owner;
        }

        if (string.IsNullOrEmpty(resolvedOwner))
            resolvedOwner = await _connection.GetAuthenticatedUserGuidAsync(ct).ConfigureAwait(false) ?? string.Empty;

        var soapClient = new Soap.TfvcSoapClient(_connection);
        var changes = await soapClient.QueryShelvesetChangesAsync(shelvesetName, resolvedOwner, ct).ConfigureAwait(false);

        return changes.Select(c =>
        {
            var changeType = ParseSoapChangeType(c.ChangeType);
            return new TfvcChange
            {
                Item = new TfvcItem
                {
                    Path = c.ServerPath,
                    IsFolder = c.IsFolder,
                    ChangesetVersion = c.ChangesetVersion,
                },
                ChangeType = changeType,
            };
        }).ToList();
    }

    // ─── Branch ───────────────────────────────────────────────────────────────

    /// <summary>查询 TFVC 分支引用列表。</summary>
    /// <param name="scopePath">作用域路径；默认为 $/</param>
    /// <param name="includeDeleted">是否包含已删除分支</param>
    /// <param name="ct">取消令牌</param>
    public async Task<IReadOnlyList<TfvcBranchRef>> GetBranchRefsAsync(
        string scopePath = "$/",
        bool includeDeleted = false,
        CancellationToken ct = default)
    {
        var soap = new Soap.TfvcSoapClient(_connection);
        var branches = await soap.QueryBranchObjectsAsync(scopePath, includeChildren: false, ct: ct).ConfigureAwait(false);

        IEnumerable<Models.Soap.SoapBranchObject> filtered = branches;
        if (!includeDeleted)
            filtered = filtered.Where(b => !b.IsDeleted);

        return filtered.Select(b => new TfvcBranchRef
        {
            Path = b.Path,
            Description = b.Description,
            Owner = b.Owner != null ? new IdentityRef { DisplayName = b.Owner, UniqueName = b.Owner } : null,
            CreatedDate = b.DateCreated?.DateTime ?? DateTime.MinValue,
            IsDeleted = b.IsDeleted,
        }).ToList();
    }

    /// <summary>获取单个分支详情。</summary>
    /// <param name="path">TFVC 分支路径</param>
    /// <param name="includeChildren">是否包含子分支</param>
    /// <param name="ct">取消令牌</param>
    public async Task<TfvcBranch> GetBranchAsync(
        string path,
        bool includeChildren = true,
        CancellationToken ct = default)
    {
        var soap = new Soap.TfvcSoapClient(_connection);
        var branches = await soap.QueryBranchObjectsAsync(path, includeChildren: includeChildren, ct: ct).ConfigureAwait(false);
        var found = branches.FirstOrDefault(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase))
                 ?? branches.FirstOrDefault();

        if (found is null)
            throw new InvalidOperationException($"Branch '{path}' not found.");

        var result = new TfvcBranch
        {
            Path = found.Path,
            Description = found.Description,
            Owner = found.Owner != null ? new IdentityRef { DisplayName = found.Owner, UniqueName = found.Owner } : null,
            CreatedDate = found.DateCreated?.DateTime ?? DateTime.MinValue,
            IsDeleted = found.IsDeleted,
            Parent = !string.IsNullOrEmpty(found.ParentPath) ? new TfvcShallowBranchRef { Path = found.ParentPath } : null,
            Children = found.ChildPaths?.Select(cp => new TfvcBranch { Path = cp }).ToList(),
        };

        return result;
    }

    // ─── Merge Query ──────────────────────────────────────────────────────────

    public async Task<MergeBaseInfo> ResolveMergeBaseAsync(
        string sourcePath,
        string targetPath,
        CancellationToken ct = default)
    {
        var notes = new List<string>();

        var sourceBranch = await TryResolveBranchAsync(sourcePath, ct).ConfigureAwait(false);
        var targetBranch = await TryResolveBranchAsync(targetPath, ct).ConfigureAwait(false);

        if (sourceBranch is null)
            notes.Add("Unable to resolve source path to a TFVC branch root.");
        if (targetBranch is null)
            notes.Add("Unable to resolve target path to a TFVC branch root.");

        var sourceAncestry = sourceBranch is null
            ? Array.Empty<string>()
            : (await GetBranchAncestryAsync(sourceBranch, ct).ConfigureAwait(false)).Select(b => b.Path).ToArray();
        var targetAncestry = targetBranch is null
            ? Array.Empty<string>()
            : (await GetBranchAncestryAsync(targetBranch, ct).ConfigureAwait(false)).Select(b => b.Path).ToArray();

        var targetSet = targetAncestry.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commonAncestor = sourceAncestry.FirstOrDefault(targetSet.Contains);

        var relationship = "unresolved";
        if (sourceBranch is not null && targetBranch is not null)
        {
            if (string.Equals(sourceBranch.Path, targetBranch.Path, StringComparison.OrdinalIgnoreCase))
                relationship = "sameBranch";
            else if (string.Equals(commonAncestor, sourceBranch.Path, StringComparison.OrdinalIgnoreCase))
                relationship = "sourceAncestorOfTarget";
            else if (string.Equals(commonAncestor, targetBranch.Path, StringComparison.OrdinalIgnoreCase))
                relationship = "targetAncestorOfSource";
            else if (!string.IsNullOrEmpty(commonAncestor))
                relationship = "divergedSiblings";
            else
                relationship = "unrelated";
        }

        var sourceBranchPoint = sourceBranch is null
            ? null
            : await FindBranchPointChangesetAsync(sourceBranch, ct).ConfigureAwait(false);
        var targetBranchPoint = targetBranch is null
            ? null
            : await FindBranchPointChangesetAsync(targetBranch, ct).ConfigureAwait(false);

        if (sourceBranch is not null && !sourceBranchPoint.HasValue)
            notes.Add("Could not confidently detect source branch creation changeset from history.");
        if (targetBranch is not null && !targetBranchPoint.HasValue)
            notes.Add("Could not confidently detect target branch creation changeset from history.");

        var confidence = commonAncestor is not null
            ? sourceBranchPoint.HasValue || targetBranchPoint.HasValue
                ? "medium"
                : "low"
            : "low";

        if (relationship is "sameBranch")
        {
            confidence = "high";
            notes.Add("Source and target resolve to the same branch root.");
        }
        else if (relationship is not "unresolved" && relationship is not "unrelated")
        {
            notes.Add("Merge base is inferred from branch ancestry plus creation-time history, not from a server merge-base API.");
        }

        return new MergeBaseInfo
        {
            SourcePath = sourcePath,
            TargetPath = targetPath,
            SourceBranchPath = sourceBranch?.Path,
            TargetBranchPath = targetBranch?.Path,
            SourceAncestry = sourceAncestry,
            TargetAncestry = targetAncestry,
            CommonAncestorPath = commonAncestor,
            Relationship = relationship,
            SourceBranchCreatedAt = sourceBranch?.CreatedDate,
            TargetBranchCreatedAt = targetBranch?.CreatedDate,
            SourceBranchPointChangesetId = sourceBranchPoint,
            TargetBranchPointChangesetId = targetBranchPoint,
            Confidence = confidence,
            Notes = notes,
        };
    }

    public async Task<MergeCandidateQueryResult> GetMergeCandidatesAsync(
        string sourcePath,
        string targetPath,
        int top = 20,
        int scan = 80,
        bool ignoreMergeHistory = false,
        IReadOnlySet<int>? locallyMergedIds = null,
        CancellationToken ct = default)
    {
        var baseInfo = await ResolveMergeBaseAsync(sourcePath, targetPath, ct).ConfigureAwait(false);
        var sourceHistory = await GetChangesetsAsync(sourcePath, top: scan, ct: ct).ConfigureAwait(false);

        var commentTrackedIds = new HashSet<int>();

        // When ignoreMergeHistory is true, skip all merge-history detection
        // This allows re-merging after a rollback
        if (!ignoreMergeHistory)
        {
            // Use SOAP range merge dry-run to determine which source changesets
            // actually need merging. Only changesets that produce file changes are candidates.
            if (sourceHistory.Count > 0)
            {
                try
                {
                    var firstCs = sourceHistory.Last().ChangesetId;  // oldest
                    var lastCs = sourceHistory.First().ChangesetId;  // newest
                    var rangePlan = await MergeChangesetRangeViaSoapAsync(
                        sourcePath, targetPath, firstCs, lastCs,
                        dryRun: true, ct: ct).ConfigureAwait(false);
                    if (rangePlan.Changes.Count == 0)
                    {
                        // Everything in the scanned range is already merged
                        return new MergeCandidateQueryResult
                        {
                            BaseInfo = baseInfo,
                            SourceHistoryScanned = sourceHistory.Count,
                            TargetHistoryScanned = 0,
                            SourceUniqueFloorChangesetId = GetSourceUniqueFloor(baseInfo),
                            MergedRanges = Array.Empty<MergeSourceRange>(),
                            Candidates = Array.Empty<MergeCandidateInfo>(),
                        };
                    }

                    // Extract the source changeset IDs that actually need merging
                    var neededSourceChangesets = new HashSet<int>(
                        rangePlan.Changes
                            .Select(c => c.SourceChangesetId)
                            .Where(id => id > 0));

                    // Filter source history to only include changesets in the needed set
                    // or changesets NEWER than the max needed (they might have inter-dependencies)
                    if (neededSourceChangesets.Count > 0)
                    {
                        var maxNeeded = neededSourceChangesets.Max();
                        sourceHistory = sourceHistory
                            .Where(cs => neededSourceChangesets.Contains(cs.ChangesetId) || cs.ChangesetId >= maxNeeded)
                            .ToList();
                    }
                }
                catch
                {
                    // Dry-run failed — fall through and show full candidate list
                }
            }

            // Lightweight comment-marker detection from target history
            var targetHistory = await GetChangesetsAsync(targetPath, top: scan, ct: ct).ConfigureAwait(false);
            foreach (var targetChangeset in targetHistory)
            {
                var marker = MergeCommentMarker.Parse(targetChangeset.Comment);
                if (marker is not null
                    && marker.SourceChangesetId > 0
                    && IsSameOrDescendantPath(sourcePath, marker.SourcePath)
                    && IsSameOrDescendantPath(targetPath, marker.TargetPath))
                {
                    commentTrackedIds.Add(marker.SourceChangesetId);
                }
            }
        }

        var uniqueFloor = GetSourceUniqueFloor(baseInfo);
        var candidates = new List<MergeCandidateInfo>();
        foreach (var changeset in sourceHistory)
        {
            if (uniqueFloor.HasValue && changeset.ChangesetId <= uniqueFloor.Value)
                continue;

            if (!ignoreMergeHistory)
            {
                if (commentTrackedIds.Contains(changeset.ChangesetId))
                    continue;

                if (locallyMergedIds?.Contains(changeset.ChangesetId) == true)
                    continue;
            }

            // Filter: this source changeset is itself a merge FROM the target branch
            // (a "trunk → branch" merge recorded by arm-tfs). Its content originates from
            // the target, so merging it back is redundant. Detect via its own comment marker.
            var ownMarker = MergeCommentMarker.Parse(changeset.Comment);
            if (ownMarker is not null
                && IsSameOrDescendantPath(targetPath, ownMarker.SourcePath)
                && IsSameOrDescendantPath(sourcePath, ownMarker.TargetPath))
            {
                continue;
            }

            var detail = await GetChangesetAsync(changeset.ChangesetId, ct).ConfigureAwait(false);
            if (IsChangesetOriginatingFromTarget(detail, sourcePath, targetPath))
                continue;

            // Branch metadata is not consistently available on every TFS server. As a
            // fallback, compare the source snapshot with the target's latest content.
            // This prevents already-synchronized branch creation changesets from being
            // offered as merge candidates.
            if (await IsChangesetContentCoveredByTargetAsync(
                    detail,
                    sourcePath,
                    targetPath,
                    ct).ConfigureAwait(false))
            {
                continue;
            }

            candidates.Add(new MergeCandidateInfo
            {
                ChangesetId = changeset.ChangesetId,
                CreatedAt = changeset.CreatedDate,
                Comment = changeset.Comment,
                AuthorDisplayName = changeset.Author?.DisplayName,
                AuthorUniqueName = changeset.Author?.UniqueName,
                IsMergedToTarget = false,
            });

            if (candidates.Count >= top)
                break;
        }

        return new MergeCandidateQueryResult
        {
            BaseInfo = baseInfo,
            SourceHistoryScanned = sourceHistory.Count,
            TargetHistoryScanned = 0,
            SourceUniqueFloorChangesetId = uniqueFloor,
            MergedRanges = Array.Empty<MergeSourceRange>(),
            Candidates = candidates,
        };
    }

    public async Task<MergeExecutionResult> MergeChangesetAsync(
        string sourcePath,
        string targetPath,
        int sourceChangesetId,
        string? comment = null,
        bool dryRun = false,
        IReadOnlyList<MergeExecutionResolution>? resolutions = null,
        string mergeMode = "rest",
        string? soapOwner = null,
        CancellationToken ct = default)
    {
        var normalizedSource = NormalizeServerPath(sourcePath);
        var normalizedTarget = NormalizeServerPath(targetPath);

        // SOAP path: lets the server record real merge history (REST cannot).
        // Also use SOAP for dry-run to get accurate 3-way conflict detection.
        if (string.Equals(mergeMode, "soap", StringComparison.OrdinalIgnoreCase))
        {
            if (dryRun)
            {
                // For dry-run, delegate to range-merge SOAP with from=to=changeset
                return await MergeChangesetRangeViaSoapAsync(
                    normalizedSource,
                    normalizedTarget,
                    sourceChangesetId,
                    sourceChangesetId,
                    comment: null,
                    dryRun: true,
                    soapOwner: soapOwner,
                    ct: ct).ConfigureAwait(false);
            }
            return await MergeChangesetViaSoapAsync(
                normalizedSource,
                normalizedTarget,
                sourceChangesetId,
                comment,
                soapOwner,
                resolutions,
                ct).ConfigureAwait(false);
        }

        var mergeBase = await ResolveMergeBaseAsync(normalizedSource, normalizedTarget, ct).ConfigureAwait(false);
        var detail = await GetChangesetAsync(sourceChangesetId, ct).ConfigureAwait(false);
        var effectiveComment = string.IsNullOrWhiteSpace(comment)
            ? $"Merge cs#{sourceChangesetId} from {normalizedSource} to {normalizedTarget}"
            : comment.Trim();

        var warnings = new List<string>();
        var plannedChanges = new List<MergeExecutionChange>();
        var tfvcChanges = new List<TfvcChange>();
        var soapMergeChanges = new List<(string serverPath, string changeType, byte[]? content, int? baseVersion)>();
        var expectedContentByTarget = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var expectedDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolutionBySource = BuildResolutionBySource(resolutions);

        // Pre-assess conflicts (downloads 3-way content for auto-take-source files). The result is
        // reused both to block the merge on unresolved conflicts and to avoid a second source fetch.
        var conflictAssessments = await AssessMergeConflictsAsync(
            detail, normalizedSource, normalizedTarget, mergeBase, resolutionBySource, ct).ConfigureAwait(false);
        var hasBlockingConflict = false;

        foreach (var change in detail.Changes ?? Array.Empty<TfvcChange>())
        {
            var sourceItemPath = change.Item?.Path;
            if (string.IsNullOrEmpty(sourceItemPath) || !IsSameOrDescendantPath(sourceItemPath, normalizedSource))
                continue;

            if (change.Item?.IsFolder == true)
                continue;

            var targetServerPath = RemapServerPath(sourceItemPath, normalizedSource, normalizedTarget);
            var targetExists = await TryGetItemVersionAsync(targetServerPath, ct).ConfigureAwait(false);
            resolutionBySource.TryGetValue(NormalizeServerPath(sourceItemPath), out var resolution);
            var resolutionChoice = NormalizeResolutionChoice(resolution?.Choice);
            if (IsMergeMetadataOnly(change.ChangeType))
            {
                plannedChanges.Add(new MergeExecutionChange
                {
                    SourceServerPath = sourceItemPath,
                    TargetServerPath = targetServerPath,
                    SourceChangesetId = sourceChangesetId,
                    SourceChangeType = change.ChangeType.ToString(),
                    TargetChangeType = VersionControlChangeType.None.ToString(),
                    TargetExists = targetExists.HasValue,
                    HasContent = false,
                    Status = "ignored",
                    Note = "Merge metadata only; no file content change is required.",
                });
                continue;
            }

            if (resolutionChoice == "target")
            {
                plannedChanges.Add(new MergeExecutionChange
                {
                    SourceServerPath = sourceItemPath,
                    TargetServerPath = targetServerPath,
                    SourceChangesetId = sourceChangesetId,
                    SourceChangeType = change.ChangeType.ToString(),
                    TargetChangeType = VersionControlChangeType.None.ToString(),
                    TargetExists = targetExists.HasValue,
                    HasContent = false,
                    Status = dryRun ? "plannedTarget" : "resolvedTarget",
                    Resolution = "target",
                    Note = "Resolved by keeping the target branch version.",
                });
                continue;
            }

            var targetChangeType = ResolveMergeChangeType(change.ChangeType, targetExists.HasValue, out var note);
            if (resolutionChoice == "manual")
            {
                targetChangeType = targetExists.HasValue ? VersionControlChangeType.Edit : VersionControlChangeType.Add;
                note = "Resolved with manually merged content.";
            }
            else if (targetChangeType == VersionControlChangeType.None && resolutionChoice == "source" && CanForceSourceContentMerge(change.ChangeType))
            {
                targetChangeType = targetExists.HasValue ? VersionControlChangeType.Edit : VersionControlChangeType.Add;
                note = "Resolved by taking source branch content.";
            }

            if (targetChangeType == VersionControlChangeType.None)
            {
                warnings.Add(note ?? $"Skipped unsupported merge change for {sourceItemPath}.");
                plannedChanges.Add(new MergeExecutionChange
                {
                    SourceServerPath = sourceItemPath,
                    TargetServerPath = targetServerPath,
                    SourceChangesetId = sourceChangesetId,
                    SourceChangeType = change.ChangeType.ToString(),
                    TargetChangeType = VersionControlChangeType.None.ToString(),
                    TargetExists = targetExists.HasValue,
                    HasContent = false,
                    Status = "skipped",
                    Resolution = resolutionChoice,
                    Note = note,
                });
                continue;
            }

            conflictAssessments.TryGetValue(NormalizeServerPath(sourceItemPath), out var assessed);

            byte[]? content = null;
            var requiresContent = targetChangeType.HasFlag(VersionControlChangeType.Add) || targetChangeType.HasFlag(VersionControlChangeType.Edit);
            if (resolutionChoice == "manual")
            {
                content = DecodeManualMergeContent(resolution, sourceItemPath);
            }
            else if (requiresContent)
            {
                // Reuse the source content already fetched by the conflict assessment when available
                // (avoids a second download of the same source version).
                content = (assessed is not null && assessed.SourceContent is not null)
                    ? assessed.SourceContent
                    : await DownloadFileContentAsync(sourceItemPath, change.Item?.ChangesetVersion ?? sourceChangesetId, ct).ConfigureAwait(false);
            }

            // Conflict detection: when both source and target modified the same file relative to the
            // merge base AND no explicit resolution was provided, mark the change as a conflict and
            // block the merge — never silently overwrite the target with the source version. The user
            // must provide a resolution (source/target/manual) before the merge can proceed.
            if (resolutionChoice is null && assessed is not null && assessed.IsConflict)
            {
                hasBlockingConflict = true;
                plannedChanges.Add(new MergeExecutionChange
                {
                    SourceServerPath = sourceItemPath,
                    TargetServerPath = targetServerPath,
                    SourceChangesetId = sourceChangesetId,
                    SourceChangeType = change.ChangeType.ToString(),
                    TargetChangeType = targetChangeType.ToString(),
                    TargetExists = true,
                    HasContent = content is not null,
                    Status = "conflict",
                    Resolution = resolutionChoice,
                    Note = "Both source and target modified this file since the merge base. Provide a resolution (source/target/manual) to proceed.",
                });
                continue;
            }

            plannedChanges.Add(new MergeExecutionChange
            {
                SourceServerPath = sourceItemPath,
                TargetServerPath = targetServerPath,
                SourceChangesetId = sourceChangesetId,
                SourceChangeType = change.ChangeType.ToString(),
                TargetChangeType = targetChangeType.ToString(),
                TargetExists = targetExists.HasValue,
                HasContent = content is not null,
                Status = dryRun ? "planned" : "ready",
                Resolution = resolutionChoice,
                Note = note,
            });

            if (dryRun)
                continue;

            // The REST changeset API does not accept merge change types or merge sources, so we
            // push a plain Add/Edit/Delete carrying the source content instead of merge metadata.
            // TFS requires the item's current changesetVersion for an Edit ("请指定项版本"), so we
            // pass the target's current version (targetExists) as the base.
            tfvcChanges.Add(BuildTfvcChange(
                targetServerPath,
                targetChangeType,
                content,
                targetExists));
            soapMergeChanges.Add((
                targetServerPath,
                targetChangeType.ToString(),
                content,
                targetExists));

            if (targetChangeType.HasFlag(VersionControlChangeType.Delete))
            {
                expectedDeletes.Add(targetServerPath);
            }
            else if (content is not null)
            {
                expectedContentByTarget[targetServerPath] = content;
            }
        }

        if (dryRun || tfvcChanges.Count == 0 || (!dryRun && hasBlockingConflict))
        {
            if (!dryRun && hasBlockingConflict)
            {
                var conflictCount = plannedChanges.Count(c => string.Equals(c.Status, "conflict", StringComparison.OrdinalIgnoreCase));
                warnings.Add($"Merge aborted: {conflictCount} file(s) have unresolved conflicts. Provide a resolution (source/target/manual) for each conflicted file and retry.");
            }
            else if (dryRun && hasBlockingConflict)
            {
                var conflictCount = plannedChanges.Count(c => string.Equals(c.Status, "conflict", StringComparison.OrdinalIgnoreCase));
                warnings.Add($"{conflictCount} conflict(s) detected. Resolve them (source/target/manual) before executing the merge.");
            }
            else if (!dryRun && tfvcChanges.Count == 0)
                warnings.Add("No executable merge changes were produced for the requested source changeset.");

            return new MergeExecutionResult
            {
                SourcePath = normalizedSource,
                TargetPath = normalizedTarget,
                SourceChangesetId = sourceChangesetId,
                SourceFromChangesetId = sourceChangesetId,
                SourceToChangesetId = sourceChangesetId,
                Comment = effectiveComment,
                DryRun = dryRun,
                BaseInfo = mergeBase,
                Changes = plannedChanges,
                Warnings = warnings,
            };
        }

        warnings.Add("Changes were applied as a direct check-in (take-source) via SOAP. TFVC merge history is not recorded via this path.");

        // Embed a structured marker so the candidate filter can detect this merge cross-workspace.
        var commentWithMarker = effectiveComment.TrimEnd()
            + " "
            + MergeCommentMarker.Build(normalizedSource, sourceChangesetId, normalizedTarget);

        var createdChangesetId = await CreateChangesetViaSoapAsync(commentWithMarker, soapMergeChanges, ct).ConfigureAwait(false);

        var verifyWarnings = await VerifyMergeChangesetAppliedAsync(
            createdChangesetId,
            tfvcChanges.Select(change => change.Item?.Path).Where(path => !string.IsNullOrEmpty(path)).Cast<string>().ToArray(),
            expectedContentByTarget,
            expectedDeletes,
            ct).ConfigureAwait(false);
        warnings.AddRange(verifyWarnings);

        // Record in local merge-history so the candidate list clears immediately,
        // even before the target history scan picks up the comment marker.
        _workspaceManager?.RecordMerge(new MergeRecord
        {
            SourceChangesetId = sourceChangesetId,
            SourcePath = normalizedSource,
            TargetPath = normalizedTarget,
            TargetChangesetId = createdChangesetId,
            MergedAtUtc = DateTime.UtcNow,
            Method = "soap-takesource",
        });

        return new MergeExecutionResult
        {
            SourcePath = normalizedSource,
            TargetPath = normalizedTarget,
            SourceChangesetId = sourceChangesetId,
            SourceFromChangesetId = sourceChangesetId,
            SourceToChangesetId = sourceChangesetId,
            Comment = commentWithMarker,
            DryRun = false,
            CreatedChangesetId = createdChangesetId,
            BaseInfo = mergeBase,
            Changes = plannedChanges.Select(change => new MergeExecutionChange
            {
                SourceServerPath = change.SourceServerPath,
                TargetServerPath = change.TargetServerPath,
                SourceChangesetId = change.SourceChangesetId,
                SourceChangeType = change.SourceChangeType,
                TargetChangeType = change.TargetChangeType,
                TargetExists = change.TargetExists,
                HasContent = change.HasContent,
                Status = "created",
                Resolution = change.Resolution,
                Note = change.Note,
            }).ToList(),
            Warnings = warnings,
        };
    }

    /// <summary>
    /// 走 TFVC SOAP 协议执行合并：CreateWorkspace → PendMerge → CheckIn → DeleteWorkspace。
    /// 服务器会写入真实的 merge history（含 MergeSources 元数据），TFS Web UI / tf merges 可见。
    /// </summary>
    private async Task<MergeExecutionResult> MergeChangesetViaSoapAsync(
        string normalizedSource,
        string normalizedTarget,
        int sourceChangesetId,
        string? comment,
        string? soapOwner,
        IReadOnlyList<MergeExecutionResolution>? resolutions,
        CancellationToken ct)
    {
        var mergeBase = await ResolveMergeBaseAsync(normalizedSource, normalizedTarget, ct).ConfigureAwait(false);
        var effectiveComment = string.IsNullOrWhiteSpace(comment)
            ? $"Merge cs#{sourceChangesetId} from {normalizedSource} to {normalizedTarget}"
            : comment.Trim();

        // Embed comment marker so candidate filter still works alongside server merge history.
        var commentWithMarker = effectiveComment.TrimEnd()
            + " "
            + Models.MergeCommentMarker.Build(normalizedSource, sourceChangesetId, normalizedTarget);

        var soap = new Soap.TfvcSoapClient(_connection);
        var resolutionBySource = BuildResolutionBySource(resolutions);
        var resolutionByTarget = BuildResolutionByTarget(resolutions);
        var warnings = new List<string>();

        // Resolve the workspace owner = the authenticated user's identity GUID. TFS CreateWorkspace
        // requires OwnerName and it MUST equal the authenticated user (else Merge fails with
        // TF204017 no Use permission). We must NOT infer it from QueryWorkspaces — that returns other
        // users' workspaces on this server and previously picked the wrong identity. The authenticated
        // user's GUID comes from REST connectionData. An explicit --soap-owner overrides this.
        var owner = soapOwner;
        if (string.IsNullOrWhiteSpace(owner))
        {
            owner = await _connection.GetAuthenticatedUserGuidAsync(ct).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new InvalidOperationException(
                "SOAP merge could not resolve the authenticated user identity for the workspace owner. "
                + "Pass --soap-owner explicitly, or ensure the PAT is valid.");
        }

        // Conflict pre-check (same logic as the REST path). Unresolved conflicts block the merge.
        // Explicit source/target choices are later resolved through SOAP Resolve; manual content
        // falls back to REST because Repository.asmx Resolve does not accept file bytes.
        var detail = await GetChangesetAsync(sourceChangesetId, ct).ConfigureAwait(false);
        var conflictAssessments = await AssessMergeConflictsAsync(
            detail, normalizedSource, normalizedTarget, mergeBase, resolutionBySource, ct).ConfigureAwait(false);

        var unresolvedConflicts = conflictAssessments.Values
            .Where(a => a.IsConflict)
            .Select(a => a.SourceServerPath)
            .ToList();
        if (unresolvedConflicts.Count > 0)
        {
            warnings.Add($"Merge aborted: {unresolvedConflicts.Count} file(s) have unresolved conflicts. Provide a resolution (source/target/manual) for each and retry.");
            return new MergeExecutionResult
            {
                SourcePath = normalizedSource,
                TargetPath = normalizedTarget,
                SourceChangesetId = sourceChangesetId,
                SourceFromChangesetId = sourceChangesetId,
                SourceToChangesetId = sourceChangesetId,
                Comment = commentWithMarker,
                DryRun = false,
                CreatedChangesetId = null,
                BaseInfo = mergeBase,
                Changes = unresolvedConflicts.Select(p => new MergeExecutionChange
                {
                    SourceServerPath = p,
                    TargetServerPath = RemapServerPath(p, normalizedSource, normalizedTarget),
                    SourceChangesetId = sourceChangesetId,
                    Status = "conflict",
                    TargetExists = true,
                    Note = "Both source and target modified this file since the merge base. Provide a resolution (source/target/manual) to proceed.",
                }).ToList(),
                Warnings = warnings,
            };
        }

        // SOAP Resolve can ask the server to take either side directly:
        // source -> AcceptTheirs, target -> AcceptYours. Manual content is different: Repository.asmx
        // Resolve(AcceptMerge) expects a real local workspace file that already contains the merged
        // content. This tool currently uses a temporary server workspace, so manual content still
        // falls back to the REST content check-in path.
        if (resolutionBySource.Values.Any(r => NormalizeResolutionChoice(r.Choice) == "manual"))
        {
            warnings.Add("SOAP merge fell back to REST for manual conflict content; TFVC merge history is not recorded for this changeset. Source/target resolutions are handled via SOAP Resolve.");
            var restResult = await MergeChangesetAsync(
                normalizedSource, normalizedTarget, sourceChangesetId, comment,
                dryRun: false, resolutions, mergeMode: "rest", soapOwner: soapOwner, ct).ConfigureAwait(false);
            return new MergeExecutionResult
            {
                SourcePath = restResult.SourcePath,
                TargetPath = restResult.TargetPath,
                SourceChangesetId = restResult.SourceChangesetId,
                SourceFromChangesetId = restResult.SourceFromChangesetId,
                SourceToChangesetId = restResult.SourceToChangesetId,
                Comment = restResult.Comment,
                DryRun = restResult.DryRun,
                CreatedChangesetId = restResult.CreatedChangesetId,
                BaseInfo = restResult.BaseInfo,
                Changes = restResult.Changes,
                Warnings = warnings.Concat(restResult.Warnings).ToList(),
            };
        }

        var workspaceName = $"arm-tfs-soap-merge-{Guid.NewGuid():N}";
        var computer = Environment.MachineName;
        // PendMerge requires the merge target to be mapped in the workspace (a server workspace
        // mapping is server-side bookkeeping; no local files are downloaded for a merge+checkin).
        // Map source and target to distinct temp local paths so neither side is "unmapped".
        var mergeTempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arm-tfs-merge", workspaceName);
        var workingFolders = new[]
        {
            (normalizedTarget, System.IO.Path.Combine(mergeTempRoot, "target")),
            (normalizedSource, System.IO.Path.Combine(mergeTempRoot, "source")),
        };
        SoapWorkspaceCreated? createdWs = null;

        try
        {
            // CreateWorkspace with owner = authenticated user GUID. Reuse this owner for every
            // subsequent call so the workspace lookup (by name+owner) matches and the caller has
            // Use permission. Prefer the server-recorded owner from the response when available.
            var createdWorkspace = await soap.CreateWorkspaceAsync(
                workspaceName, owner, computer, "arm-tfs SOAP merge", workingFolders, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(createdWorkspace.Owner))
            {
                owner = createdWorkspace.Owner;
            }
            createdWs = new SoapWorkspaceCreated(workspaceName, owner);

            var pendResult = await soap.PendMergeAsync(
                workspaceName, owner,
                normalizedSource, normalizedTarget,
                sourceChangesetId, sourceChangesetId,
                ct).ConfigureAwait(false);
            var ops = pendResult.Operations.ToList();
            var resolvedConflictChanges = new List<MergeExecutionChange>();

            // The server's 3-way merge may have pended CONFLICTS (both sides changed the same file).
            // Resolve source/target choices via SOAP Resolve, never silently take one side. If any
            // conflict lacks a user choice, abort with the full list so the workbench can display it.
            if (pendResult.Conflicts.Count > 0)
            {
                var missingResolutions = new List<Models.Soap.SoapMergeConflict>();
                var resolvedOps = new List<Models.Soap.SoapMergeOperation>();
                foreach (var conflict in pendResult.Conflicts)
                {
                    var resolution = FindResolutionForSoapConflict(conflict, resolutionBySource, resolutionByTarget);
                    var choice = NormalizeResolutionChoice(resolution?.Choice);
                    if (choice is null)
                    {
                        missingResolutions.Add(conflict);
                        continue;
                    }

                    var conflictId = await ResolveSoapConflictIdAsync(
                        soap, workspaceName, owner, conflict, normalizedTarget, ct).ConfigureAwait(false);
                    if (conflictId <= 0)
                    {
                        missingResolutions.Add(conflict);
                        continue;
                    }

                    var soapResolution = MapToSoapResolution(choice);
                    var resolveResult = await soap.ResolveConflictAsync(
                        workspaceName, owner, conflictId, soapResolution, ct: ct).ConfigureAwait(false);
                    resolvedOps.AddRange(resolveResult.Operations.Select(op =>
                        NormalizeResolvedOperation(op, conflict, sourceChangesetId)));
                    resolvedConflictChanges.Add(new MergeExecutionChange
                    {
                        SourceServerPath = conflict.SourceServerItem,
                        TargetServerPath = conflict.TargetServerItem,
                        SourceChangesetId = sourceChangesetId,
                        SourceChangeType = conflict.BaseChangeType,
                        TargetChangeType = conflict.ConflictType,
                        Status = choice == "source" ? "resolvedSource" : "resolvedTarget",
                        Resolution = choice,
                        TargetExists = true,
                        Note = choice == "source"
                            ? "SOAP Resolve accepted the source branch version (AcceptTheirs)."
                            : "SOAP Resolve kept the target branch version (AcceptYours).",
                    });
                }

                if (missingResolutions.Count > 0)
                {
                    warnings.Add($"Merge aborted: {missingResolutions.Count} conflict(s) detected by the server's 3-way merge. Resolve each (source/target/manual) and retry.");
                    return new MergeExecutionResult
                    {
                        SourcePath = normalizedSource,
                        TargetPath = normalizedTarget,
                        SourceChangesetId = sourceChangesetId,
                        SourceFromChangesetId = sourceChangesetId,
                        SourceToChangesetId = sourceChangesetId,
                        Comment = commentWithMarker,
                        DryRun = false,
                        CreatedChangesetId = null,
                        BaseInfo = mergeBase,
                        Changes = missingResolutions.Select(c => new MergeExecutionChange
                        {
                            SourceServerPath = c.SourceServerItem,
                            TargetServerPath = c.TargetServerItem,
                            SourceChangesetId = sourceChangesetId,
                            Status = "conflict",
                            TargetExists = true,
                            Note = "Both source and target modified this file (server 3-way merge conflict). Provide a resolution (source/target/manual) to proceed.",
                        }).ToList(),
                        Warnings = warnings,
                    };
                }

                ops.AddRange(resolvedOps);

                var remainingConflicts = await soap.QueryConflictsAsync(
                    workspaceName, owner, new[] { normalizedTarget }, ct).ConfigureAwait(false);
                if (remainingConflicts.Count > 0)
                {
                    warnings.Add($"Merge aborted: {remainingConflicts.Count} conflict(s) remain after SOAP Resolve.");
                    return new MergeExecutionResult
                    {
                        SourcePath = normalizedSource,
                        TargetPath = normalizedTarget,
                        SourceChangesetId = sourceChangesetId,
                        SourceFromChangesetId = sourceChangesetId,
                        SourceToChangesetId = sourceChangesetId,
                        Comment = commentWithMarker,
                        DryRun = false,
                        CreatedChangesetId = null,
                        BaseInfo = mergeBase,
                        Changes = remainingConflicts.Select(c => new MergeExecutionChange
                        {
                            SourceServerPath = c.SourceServerItem,
                            TargetServerPath = c.TargetServerItem,
                            SourceChangesetId = sourceChangesetId,
                            Status = "conflict",
                            TargetExists = true,
                            Note = "Conflict remained after SOAP Resolve. Re-open the merge plan and resolve it again.",
                        }).ToList(),
                        Warnings = warnings,
                    };
                }
            }

            if (ops.Count == 0)
            {
                warnings.Add(resolvedConflictChanges.Count > 0
                    ? "SOAP Resolve completed, but the server returned no merge operations to commit. Target-side resolutions may not require a changeset."
                    : "Server returned no merge operations — nothing to commit. The source changeset may already be merged.");
                return new MergeExecutionResult
                {
                    SourcePath = normalizedSource,
                    TargetPath = normalizedTarget,
                    SourceChangesetId = sourceChangesetId,
                    SourceFromChangesetId = sourceChangesetId,
                    SourceToChangesetId = sourceChangesetId,
                    Comment = commentWithMarker,
                    DryRun = false,
                    CreatedChangesetId = null,
                    BaseInfo = mergeBase,
                    Changes = resolvedConflictChanges,
                    Warnings = warnings,
                };
            }

            var committedOps = DeduplicateSoapMergeOperations(ops);

            if (committedOps.Count == 0)
            {
                warnings.Add("All merge operations were resolved to 'target'; nothing to commit.");
                return new MergeExecutionResult
                {
                    SourcePath = normalizedSource,
                    TargetPath = normalizedTarget,
                    SourceChangesetId = sourceChangesetId,
                    SourceFromChangesetId = sourceChangesetId,
                    SourceToChangesetId = sourceChangesetId,
                    Comment = commentWithMarker,
                    DryRun = false,
                    CreatedChangesetId = null,
                    BaseInfo = mergeBase,
                    Changes = resolvedConflictChanges,
                    Warnings = warnings,
                };
            }

            var pendingChanges = committedOps.Select(op => new Models.Soap.SoapPendingChange
            {
                ItemId = op.ItemId,
                ServerItem = op.TargetServerItem,
                ChangeType = string.IsNullOrEmpty(op.ChangeType) ? "Merge" : op.ChangeType,
                SourceServerItem = op.SourceServerItem,
                VersionFrom = op.VersionFrom ?? sourceChangesetId,
                VersionTo = op.VersionTo ?? sourceChangesetId,
            }).ToList();

            var newChangesetId = await soap.CheckInAsync(
                workspaceName, owner,
                commentWithMarker,
                pendingChanges,
                ct).ConfigureAwait(false);

            _workspaceManager?.RecordMerge(new Models.MergeRecord
            {
                SourceChangesetId = sourceChangesetId,
                SourcePath = normalizedSource,
                TargetPath = normalizedTarget,
                TargetChangesetId = newChangesetId,
                MergedAtUtc = DateTime.UtcNow,
                Method = "soap-merge",
            });

            var executionChanges = committedOps.Select(op => new MergeExecutionChange
            {
                SourceServerPath = op.SourceServerItem,
                TargetServerPath = op.TargetServerItem,
                SourceChangesetId = sourceChangesetId,
                SourceChangeType = op.ChangeType,
                TargetChangeType = op.ChangeType,
                TargetExists = true,
                HasContent = false,
                Status = "created",
                Resolution = ResolveOperationResolution(op, resolutionBySource),
                Note = "SOAP merge — server-recorded merge history",
            }).ToList();
            foreach (var resolvedConflictChange in resolvedConflictChanges)
            {
                if (executionChanges.Any(change =>
                    string.Equals(change.TargetServerPath, resolvedConflictChange.TargetServerPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                executionChanges.Add(new MergeExecutionChange
                {
                    SourceServerPath = resolvedConflictChange.SourceServerPath,
                    TargetServerPath = resolvedConflictChange.TargetServerPath,
                    SourceChangesetId = resolvedConflictChange.SourceChangesetId,
                    SourceChangeType = resolvedConflictChange.SourceChangeType,
                    TargetChangeType = resolvedConflictChange.TargetChangeType,
                    TargetExists = resolvedConflictChange.TargetExists,
                    HasContent = resolvedConflictChange.HasContent,
                    Status = "created",
                    Resolution = resolvedConflictChange.Resolution,
                    Note = resolvedConflictChange.Note,
                });
            }

            return new MergeExecutionResult
            {
                SourcePath = normalizedSource,
                TargetPath = normalizedTarget,
                SourceChangesetId = sourceChangesetId,
                SourceFromChangesetId = sourceChangesetId,
                SourceToChangesetId = sourceChangesetId,
                Comment = commentWithMarker,
                DryRun = false,
                CreatedChangesetId = newChangesetId,
                BaseInfo = mergeBase,
                Changes = executionChanges,
                Warnings = warnings,
            };
        }
        finally
        {
            if (createdWs is not null)
            {
                try
                {
                    await soap.DeleteWorkspaceAsync(createdWs.Name, createdWs.Owner, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Workspace cleanup failure is non-fatal — log to stderr but don't mask the original error.
                    Console.Error.WriteLine($"warning: failed to delete temp SOAP workspace '{createdWs.Name}': {ex.Message}");
                }
            }
        }
    }

    private sealed record SoapWorkspaceCreated(string Name, string Owner);

    private static Dictionary<string, MergeExecutionResolution> BuildResolutionByTarget(
        IReadOnlyList<MergeExecutionResolution>? resolutions)
    {
        return (resolutions ?? Array.Empty<MergeExecutionResolution>())
            .Where(item => !string.IsNullOrWhiteSpace(item.TargetServerPath))
            .GroupBy(item => NormalizeServerPath(item.TargetServerPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
    }

    private static MergeExecutionResolution? FindResolutionForSoapConflict(
        Models.Soap.SoapMergeConflict conflict,
        IReadOnlyDictionary<string, MergeExecutionResolution> resolutionBySource,
        IReadOnlyDictionary<string, MergeExecutionResolution> resolutionByTarget)
    {
        if (!string.IsNullOrWhiteSpace(conflict.SourceServerItem)
            && resolutionBySource.TryGetValue(NormalizeServerPath(conflict.SourceServerItem), out var bySource))
        {
            return bySource;
        }

        if (!string.IsNullOrWhiteSpace(conflict.TargetServerItem)
            && resolutionByTarget.TryGetValue(NormalizeServerPath(conflict.TargetServerItem), out var byTarget))
        {
            return byTarget;
        }

        return null;
    }

    private async Task<int> ResolveSoapConflictIdAsync(
        Soap.TfvcSoapClient soap,
        string workspaceName,
        string owner,
        Models.Soap.SoapMergeConflict conflict,
        string normalizedTarget,
        CancellationToken ct)
    {
        if (conflict.ConflictId > 0)
            return conflict.ConflictId;

        var queryScope = !string.IsNullOrWhiteSpace(conflict.TargetServerItem)
            ? conflict.TargetServerItem
            : normalizedTarget;
        var conflicts = await soap.QueryConflictsAsync(
            workspaceName,
            owner,
            new[] { queryScope },
            ct).ConfigureAwait(false);

        var match = conflicts.FirstOrDefault(candidate => SameSoapConflict(candidate, conflict));
        return match?.ConflictId ?? 0;
    }

    private static bool SameSoapConflict(
        Models.Soap.SoapMergeConflict candidate,
        Models.Soap.SoapMergeConflict expected)
    {
        var sourceMatches = string.IsNullOrWhiteSpace(expected.SourceServerItem)
            || string.Equals(NormalizeServerPath(candidate.SourceServerItem), NormalizeServerPath(expected.SourceServerItem), StringComparison.OrdinalIgnoreCase);
        var targetMatches = string.IsNullOrWhiteSpace(expected.TargetServerItem)
            || string.Equals(NormalizeServerPath(candidate.TargetServerItem), NormalizeServerPath(expected.TargetServerItem), StringComparison.OrdinalIgnoreCase);
        return sourceMatches && targetMatches;
    }

    private static string MapToSoapResolution(string choice) => choice switch
    {
        "source" => "AcceptTheirs",
        "target" => "AcceptYours",
        _ => throw new InvalidOperationException($"SOAP Resolve only supports source/target choices, got '{choice}'."),
    };

    private static Models.Soap.SoapMergeOperation NormalizeResolvedOperation(
        Models.Soap.SoapMergeOperation operation,
        Models.Soap.SoapMergeConflict conflict,
        int sourceChangesetId)
    {
        return new Models.Soap.SoapMergeOperation
        {
            ItemId = operation.ItemId,
            SourceServerItem = string.IsNullOrWhiteSpace(operation.SourceServerItem)
                ? conflict.SourceServerItem
                : operation.SourceServerItem,
            TargetServerItem = string.IsNullOrWhiteSpace(operation.TargetServerItem)
                ? conflict.TargetServerItem
                : operation.TargetServerItem,
            ChangeType = string.IsNullOrWhiteSpace(operation.ChangeType) ? "Merge" : operation.ChangeType,
            VersionFrom = operation.VersionFrom ?? sourceChangesetId,
            VersionTo = operation.VersionTo ?? sourceChangesetId,
            IsPending = operation.IsPending,
        };
    }

    private static IReadOnlyList<Models.Soap.SoapMergeOperation> DeduplicateSoapMergeOperations(
        IEnumerable<Models.Soap.SoapMergeOperation> operations)
    {
        var result = new List<Models.Soap.SoapMergeOperation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in operations)
        {
            if (string.IsNullOrWhiteSpace(op.TargetServerItem))
                continue;
            var key = $"{NormalizeServerPath(op.TargetServerItem)}|{NormalizeServerPath(op.SourceServerItem)}|{op.VersionFrom}|{op.VersionTo}|{op.ChangeType}";
            if (seen.Add(key))
                result.Add(op);
        }

        return result;
    }

    private static string? ResolveOperationResolution(
        Models.Soap.SoapMergeOperation operation,
        IReadOnlyDictionary<string, MergeExecutionResolution> resolutionBySource)
    {
        if (string.IsNullOrWhiteSpace(operation.SourceServerItem))
            return null;

        return resolutionBySource.TryGetValue(NormalizeServerPath(operation.SourceServerItem), out var resolution)
            ? NormalizeResolutionChoice(resolution.Choice)
            : null;
    }

    /// <summary>
    /// 走 TFVC SOAP 协议对一段 source changeset 做一次服务器端 merge plan / merge。
    /// dry-run 时只 PendMerge 并返回全量 operations/conflicts；非 dry-run 且无冲突时一次 CheckIn。
    /// </summary>
    public async Task<MergeExecutionResult> MergeChangesetRangeViaSoapAsync(
        string sourcePath,
        string targetPath,
        int fromChangeset,
        int toChangeset,
        string? comment = null,
        bool dryRun = false,
        string? soapOwner = null,
        IReadOnlyList<MergeExecutionResolution>? resolutions = null,
        CancellationToken ct = default)
    {
        if (fromChangeset <= 0 || toChangeset <= 0)
            throw new ArgumentOutOfRangeException(nameof(fromChangeset), "Changeset range must be positive.");
        if (fromChangeset > toChangeset)
            throw new InvalidOperationException("--from must be less than or equal to --to.");

        var normalizedSource = NormalizeServerPath(sourcePath);
        var normalizedTarget = NormalizeServerPath(targetPath);
        var mergeBase = await ResolveMergeBaseAsync(normalizedSource, normalizedTarget, ct).ConfigureAwait(false);
        var effectiveComment = string.IsNullOrWhiteSpace(comment)
            ? $"Merge cs#{fromChangeset}~cs#{toChangeset} from {normalizedSource} to {normalizedTarget}"
            : comment.Trim();

        var soap = new Soap.TfvcSoapClient(_connection);
        var owner = soapOwner;
        if (string.IsNullOrWhiteSpace(owner))
            owner = await _connection.GetAuthenticatedUserGuidAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new InvalidOperationException(
                "SOAP range merge could not resolve the authenticated user identity for the workspace owner. "
                + "Pass --soap-owner explicitly, or ensure the PAT is valid.");
        }

        var workspaceName = $"arm-tfs-soap-range-{Guid.NewGuid():N}";
        var computer = Environment.MachineName;
        var mergeTempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arm-tfs-merge", workspaceName);
        var workingFolders = new[]
        {
            (normalizedTarget, System.IO.Path.Combine(mergeTempRoot, "target")),
            (normalizedSource, System.IO.Path.Combine(mergeTempRoot, "source")),
        };
        SoapWorkspaceCreated? createdWs = null;

        try
        {
            var createdWorkspace = await soap.CreateWorkspaceAsync(
                workspaceName, owner, computer, "arm-tfs SOAP range merge", workingFolders, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(createdWorkspace.Owner))
                owner = createdWorkspace.Owner;
            createdWs = new SoapWorkspaceCreated(workspaceName, owner);

            var pendResult = await soap.PendMergeAsync(
                workspaceName, owner,
                normalizedSource, normalizedTarget,
                fromChangeset, toChangeset,
                ct).ConfigureAwait(false);

            var warnings = new List<string>();
            var plannedChanges = pendResult.Operations.Select(op => new MergeExecutionChange
            {
                SourceServerPath = op.SourceServerItem,
                TargetServerPath = op.TargetServerItem,
                SourceChangesetId = op.VersionTo ?? toChangeset,
                SourceChangeType = op.ChangeType,
                TargetChangeType = op.ChangeType,
                TargetExists = !op.ChangeType.Contains("Add", StringComparison.OrdinalIgnoreCase),
                HasContent = false,
                Status = dryRun ? "planned" : "ready",
                Note = "SOAP merge plan — server-computed operation",
            }).ToList();

            var resolvedConflictChanges = new List<MergeExecutionChange>();
            var hasUnresolvedConflicts = false;
            var manualContentFiles = new List<(string TargetPath, byte[] Content)>();

            if (pendResult.Conflicts.Count > 0)
            {
                if (!dryRun && resolutions is not null && resolutions.Count > 0)
                {
                    // Try to resolve conflicts using provided resolutions
                    var resolutionBySource = BuildResolutionBySource(resolutions);
                    var resolutionByTarget = BuildResolutionByTarget(resolutions);
                    var unresolvedConflicts = new List<Models.Soap.SoapMergeConflict>();

                    foreach (var conflict in pendResult.Conflicts)
                    {
                        var resolution = FindResolutionForSoapConflict(conflict, resolutionBySource, resolutionByTarget);
                        var choice = NormalizeResolutionChoice(resolution?.Choice);
                        if (choice is null)
                        {
                            unresolvedConflicts.Add(conflict);
                            continue;
                        }

                        if (choice == "manual")
                        {
                            var manualConflictId = await ResolveSoapConflictIdAsync(
                                soap, createdWs.Name, createdWs.Owner, conflict, normalizedTarget, ct).ConfigureAwait(false);
                            if (manualConflictId <= 0)
                            {
                                unresolvedConflicts.Add(conflict);
                                continue;
                            }

                            // Use AcceptTheirs first (makes CheckIn succeed), then apply manual content after
                            await soap.ResolveConflictAsync(
                                createdWs.Name, createdWs.Owner, manualConflictId, "AcceptTheirs", ct: ct).ConfigureAwait(false);

                            if (!string.IsNullOrEmpty(resolution.ContentBase64))
                            {
                                manualContentFiles.Add((conflict.TargetServerItem, Convert.FromBase64String(resolution.ContentBase64)));
                            }
                            continue;
                        }

                        var conflictId = await ResolveSoapConflictIdAsync(
                            soap, createdWs.Name, createdWs.Owner, conflict, normalizedTarget, ct).ConfigureAwait(false);
                        if (conflictId <= 0)
                        {
                            unresolvedConflicts.Add(conflict);
                            continue;
                        }

                        var soapResolution = MapToSoapResolution(choice);
                        var resolveResult = await soap.ResolveConflictAsync(
                            createdWs.Name, createdWs.Owner, conflictId, soapResolution, ct: ct).ConfigureAwait(false);

                        resolvedConflictChanges.Add(new MergeExecutionChange
                        {
                            SourceServerPath = conflict.SourceServerItem,
                            TargetServerPath = conflict.TargetServerItem,
                            SourceChangesetId = toChangeset,
                            SourceChangeType = conflict.BaseChangeType,
                            TargetChangeType = conflict.ConflictType,
                            Status = choice == "source" ? "resolvedSource" : "resolvedTarget",
                            Resolution = choice,
                            TargetExists = true,
                            Note = choice == "source"
                                ? "SOAP Resolve accepted the source branch version (AcceptTheirs)."
                                : "SOAP Resolve kept the target branch version (AcceptYours).",
                        });
                    }

                    if (unresolvedConflicts.Count > 0)
                    {
                        hasUnresolvedConflicts = true;
                        warnings.Add($"Merge range has {unresolvedConflicts.Count} unresolved conflict(s). Resolve every listed file before executing.");
                        plannedChanges.AddRange(unresolvedConflicts.Select(c => new MergeExecutionChange
                        {
                            SourceServerPath = c.SourceServerItem,
                            TargetServerPath = c.TargetServerItem,
                            SourceChangesetId = toChangeset,
                            SourceChangeType = c.BaseChangeType,
                            TargetChangeType = c.ConflictType,
                            TargetExists = true,
                            HasContent = false,
                            Status = "conflict",
                            Note = "Server 3-way merge conflict for the requested range.",
                        }));
                    }
                    else
                    {
                        // All conflicts resolved - verify no remaining conflicts on server
                        var remainingConflicts = await soap.QueryConflictsAsync(
                            createdWs.Name, createdWs.Owner, new[] { normalizedTarget }, ct).ConfigureAwait(false);
                        if (remainingConflicts.Count > 0)
                        {
                            hasUnresolvedConflicts = true;
                            warnings.Add($"Merge range has {remainingConflicts.Count} conflict(s) remaining after SOAP Resolve.");
                            plannedChanges.AddRange(remainingConflicts.Select(c => new MergeExecutionChange
                            {
                                SourceServerPath = c.SourceServerItem,
                                TargetServerPath = c.TargetServerItem,
                                SourceChangesetId = toChangeset,
                                SourceChangeType = c.BaseChangeType,
                                TargetChangeType = c.ConflictType,
                                TargetExists = true,
                                HasContent = false,
                                Status = "conflict",
                                Note = "Conflict remains after SOAP Resolve.",
                            }));
                        }
                    }
                }
                else
                {
                    // No resolutions provided - report conflicts (existing behavior)
                    hasUnresolvedConflicts = true;
                    warnings.Add($"Merge range has {pendResult.Conflicts.Count} unresolved conflict(s). Resolve every listed file before executing.");
                    plannedChanges.AddRange(pendResult.Conflicts.Select(c => new MergeExecutionChange
                    {
                        SourceServerPath = c.SourceServerItem,
                        TargetServerPath = c.TargetServerItem,
                        SourceChangesetId = toChangeset,
                        SourceChangeType = c.BaseChangeType,
                        TargetChangeType = c.ConflictType,
                        TargetExists = true,
                        HasContent = false,
                        Status = "conflict",
                        Note = "Server 3-way merge conflict for the requested range.",
                    }));
                }
            }

            if (dryRun || hasUnresolvedConflicts || pendResult.Operations.Count == 0)
            {
                if (pendResult.Operations.Count == 0 && pendResult.Conflicts.Count == 0)
                    warnings.Add("Server returned no merge operations for the requested range. It may already be merged.");

                return new MergeExecutionResult
                {
                    SourcePath = normalizedSource,
                    TargetPath = normalizedTarget,
                    SourceChangesetId = toChangeset,
                    SourceFromChangesetId = fromChangeset,
                    SourceToChangesetId = toChangeset,
                    Comment = effectiveComment,
                    DryRun = dryRun,
                    CreatedChangesetId = null,
                    BaseInfo = mergeBase,
                    Changes = plannedChanges,
                    Warnings = warnings,
                };
            }

            var pendingChanges = pendResult.Operations.Select(op => new Models.Soap.SoapPendingChange
            {
                ItemId = op.ItemId,
                ServerItem = op.TargetServerItem,
                ChangeType = string.IsNullOrEmpty(op.ChangeType) ? "Merge" : op.ChangeType,
                SourceServerItem = op.SourceServerItem,
                VersionFrom = op.VersionFrom ?? fromChangeset,
                VersionTo = op.VersionTo ?? toChangeset,
            }).ToList();

            var newChangesetId = await soap.CheckInAsync(
                workspaceName, owner,
                effectiveComment,
                pendingChanges,
                ct).ConfigureAwait(false);

            foreach (var mergedSourceChangeset in pendingChanges
                         .Select(c => c.VersionTo)
                         .Where(id => id.HasValue)
                         .Select(id => id!.Value)
                         .Distinct())
            {
                _workspaceManager?.RecordMerge(new Models.MergeRecord
                {
                    SourceChangesetId = mergedSourceChangeset,
                    SourcePath = normalizedSource,
                    TargetPath = normalizedTarget,
                    TargetChangesetId = newChangesetId,
                    MergedAtUtc = DateTime.UtcNow,
                    Method = "soap-merge-range",
                });
            }

            return new MergeExecutionResult
            {
                SourcePath = normalizedSource,
                TargetPath = normalizedTarget,
                SourceChangesetId = toChangeset,
                SourceFromChangesetId = fromChangeset,
                SourceToChangesetId = toChangeset,
                Comment = effectiveComment,
                DryRun = false,
                CreatedChangesetId = newChangesetId,
                BaseInfo = mergeBase,
                Changes = plannedChanges.Select(change => new MergeExecutionChange
                {
                    SourceServerPath = change.SourceServerPath,
                    TargetServerPath = change.TargetServerPath,
                    SourceChangesetId = change.SourceChangesetId,
                    SourceChangeType = change.SourceChangeType,
                    TargetChangeType = change.TargetChangeType,
                    TargetExists = change.TargetExists,
                    HasContent = change.HasContent,
                    Status = "created",
                    Resolution = change.Resolution,
                    Note = "SOAP range merge — server-recorded merge history",
                }).Concat(resolvedConflictChanges).ToList(),
                Warnings = warnings,
            };
        }
        finally
        {
            if (createdWs is not null)
            {
                try
                {
                    await soap.DeleteWorkspaceAsync(createdWs.Name, createdWs.Owner, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"warning: failed to delete temp SOAP range workspace '{createdWs.Name}': {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// 用 SOAP 服务器 3-way merge 预检一段 changeset 范围 [fromChangeset, toChangeset] 的冲突，
    /// 不实际提交。在临时 server workspace 里 PendMerge，读取 isresolved=false 的冲突，然后删除 workspace。
    /// 比 REST 启发式准确（REST 在分支点检测失败或文件在 base 不存在时会漏检 add/add、edit/edit 冲突）。
    /// </summary>
    public async Task<IReadOnlyList<MergeConflictPreview>> PreviewMergeConflictsAsync(
        string sourcePath,
        string targetPath,
        int fromChangeset,
        int toChangeset,
        CancellationToken ct = default)
    {
        var normalizedSource = NormalizeServerPath(sourcePath);
        var normalizedTarget = NormalizeServerPath(targetPath);
        var soap = new Soap.TfvcSoapClient(_connection);

        var owner = await _connection.GetAuthenticatedUserGuidAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "SOAP conflict preview could not resolve the authenticated user identity. "
                + "Ensure the PAT is valid or pass --soap-owner.");

        var workspaceName = $"arm-tfs-preview-{Guid.NewGuid():N}";
        var computer = Environment.MachineName;
        var mergeTempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arm-tfs-preview", workspaceName);
        var workingFolders = new[]
        {
            (normalizedTarget, System.IO.Path.Combine(mergeTempRoot, "target")),
            (normalizedSource, System.IO.Path.Combine(mergeTempRoot, "source")),
        };
        SoapWorkspaceCreated? createdWs = null;

        try
        {
            var created = await soap.CreateWorkspaceAsync(
                workspaceName, owner, computer, "arm-tfs conflict preview", workingFolders, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(created.Owner))
                owner = created.Owner;
            createdWs = new SoapWorkspaceCreated(workspaceName, owner);

            var pend = await soap.PendMergeAsync(
                workspaceName, owner,
                normalizedSource, normalizedTarget,
                fromChangeset, toChangeset,
                ct).ConfigureAwait(false);

            return pend.Conflicts
                .Select(c => new MergeConflictPreview
                {
                    SourceServerPath = c.SourceServerItem,
                    TargetServerPath = c.TargetServerItem,
                    ConflictType = c.ConflictType,
                })
                .ToList();
        }
        finally
        {
            if (createdWs is not null)
            {
                try
                {
                    await soap.DeleteWorkspaceAsync(createdWs.Name, createdWs.Owner, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"warning: failed to delete temp SOAP preview workspace '{createdWs.Name}': {ex.Message}");
                }
            }
        }
    }

    private async Task<IReadOnlyList<string>> VerifyMergeChangesetAppliedAsync(
        int createdChangesetId,
        IReadOnlyCollection<string> expectedTargetPaths,
        IReadOnlyDictionary<string, byte[]> expectedContentByTarget,
        IReadOnlySet<string> expectedDeletes,
        CancellationToken ct)
    {
        var verifyWarnings = new List<string>();
        var createdDetail = await GetChangesetAsync(createdChangesetId, ct).ConfigureAwait(false);
        var changedPaths = (createdDetail.Changes ?? Array.Empty<TfvcChange>())
            .Select(change => change.Item?.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeServerPath(path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // TFS REST CreateChangeset silently drops files whose content is identical to the
        // current target version (server-side dedup). For those paths, verify that the
        // target now has the expected content instead of failing the whole merge.
        var missingPaths = expectedTargetPaths
            .Where(path => !changedPaths.Contains(NormalizeServerPath(path)))
            .ToList();

        var trulyMissing = new List<string>();
        foreach (var missingPath in missingPaths)
        {
            if (expectedContentByTarget.TryGetValue(missingPath, out var expectedContent))
            {
                // Server dedup: file was not written because it already has the right content.
                // Confirm by downloading the current target version.
                try
                {
                    var actual = await DownloadFileContentAsync(missingPath, null, ct).ConfigureAwait(false);
                    if (!ContentEquals(actual, expectedContent))
                        trulyMissing.Add(missingPath);
                    // else: target already has exact content — dedup is fine, skip
                }
                catch
                {
                    trulyMissing.Add(missingPath);
                }
            }
            else if (expectedDeletes.Contains(missingPath))
            {
                // Delete paths are checked separately below; skip here.
            }
            else
            {
                trulyMissing.Add(missingPath);
            }
        }

        if (trulyMissing.Count > 0)
            throw new InvalidOperationException($"Merge verification failed: created changeset cs#{createdChangesetId} did not contain expected target path(s): {string.Join(", ", trulyMissing)}.");

        // For files that ARE in the created changeset, the merge landed. TFS may normalize content
        // on check-in (BOM / encoding / line-ending conversion), so a byte-level mismatch here is
        // NOT a real failure — warn instead of throwing (which would falsely report the merge as
        // failed after the changeset was already created).
        foreach (var (targetPath, expectedContent) in expectedContentByTarget)
        {
            // Already verified above as part of server-dedup check — skip re-download.
            if (!changedPaths.Contains(NormalizeServerPath(targetPath)))
                continue;

            try
            {
                var actualContent = await DownloadFileContentAsync(targetPath, null, ct).ConfigureAwait(false);
                if (!ContentEquals(actualContent, expectedContent))
                    verifyWarnings.Add($"Verified: '{targetPath}' was committed in cs#{createdChangesetId}, but its stored content differs from the pushed bytes (TFS likely normalized BOM/encoding/line-endings). The merge still landed.");
            }
            catch (Exception ex)
            {
                verifyWarnings.Add($"Could not re-verify '{targetPath}' after cs#{createdChangesetId}: {ex.Message}. The merge still landed.");
            }
        }

        foreach (var targetPath in expectedDeletes)
        {
            if (await TryGetItemVersionAsync(targetPath, ct).ConfigureAwait(false) is not null)
                throw new InvalidOperationException($"Merge verification failed: target file '{targetPath}' still exists after delete merge cs#{createdChangesetId}.");
        }

        return verifyWarnings;
    }

    private async Task<TfvcBranch?> TryResolveBranchAsync(string path, CancellationToken ct)
    {
        foreach (var candidate in EnumerateServerPathCandidates(path))
        {
            try
            {
                return await GetBranchAsync(candidate, includeChildren: false, ct).ConfigureAwait(false);
            }
            catch
            {
                // Not a branch root or inaccessible; continue walking up the path.
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<TfvcBranch>> GetBranchAncestryAsync(TfvcBranch branch, CancellationToken ct)
    {
        var ancestry = new List<TfvcBranch>();
        TfvcBranch? current = branch;

        while (current is not null)
        {
            ancestry.Add(current);
            var parentPath = current.Parent?.Path;
            if (string.IsNullOrEmpty(parentPath))
                break;

            current = await GetBranchAsync(parentPath, includeChildren: false, ct).ConfigureAwait(false);
        }

        return ancestry;
    }

    private async Task<int?> FindBranchPointChangesetAsync(TfvcBranch branch, CancellationToken ct)
    {
        if (branch.CreatedDate == default)
            return null;

        var windowStart = branch.CreatedDate.AddMinutes(-15);
        var windowEnd = branch.CreatedDate.AddMinutes(15);
        var changesets = await GetChangesetsAsync(
            branch.Path,
            top: 20,
            orderby: "id asc",
            fromDate: windowStart,
            toDate: windowEnd,
            ct: ct).ConfigureAwait(false);

        foreach (var changeset in changesets.OrderBy(c => c.CreatedDate))
        {
            var detail = await GetChangesetAsync(changeset.ChangesetId, ct).ConfigureAwait(false);
            if (detail.Changes?.Any(change =>
                    change.Item?.Path is not null &&
                    IsSameOrDescendantPath(change.Item.Path, branch.Path) &&
                    change.ChangeType.ToString().Contains("Branch", StringComparison.OrdinalIgnoreCase)) == true)
            {
                return changeset.ChangesetId;
            }
        }

        return changesets.OrderBy(c => c.CreatedDate).Select(c => (int?)c.ChangesetId).FirstOrDefault();
    }

    private static IEnumerable<string> EnumerateServerPathCandidates(string serverPath)
    {
        if (!serverPath.StartsWith("$/", StringComparison.Ordinal))
            yield break;

        var trimmed = serverPath.TrimEnd('/');
        while (trimmed.Length > 2)
        {
            yield return trimmed;

            var idx = trimmed.LastIndexOf('/');
            if (idx <= 1)
                break;

            trimmed = trimmed[..idx];
        }
    }

    private static bool IsSameOrDescendantPath(string path, string candidateRoot)
    {
        if (string.Equals(path, candidateRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        var normalizedRoot = candidateRoot.EndsWith('/') ? candidateRoot : candidateRoot + "/";
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static int? GetSourceUniqueFloor(MergeBaseInfo baseInfo)
    {
        return baseInfo.Relationship switch
        {
            "sourceAncestorOfTarget" => baseInfo.TargetBranchPointChangesetId,
            "targetAncestorOfSource" => baseInfo.SourceBranchPointChangesetId,
            "divergedSiblings" => baseInfo.SourceBranchPointChangesetId,
            "sameBranch" => int.MaxValue,
            _ => baseInfo.SourceBranchPointChangesetId,
        };
    }

    private async Task<int?> TryGetItemVersionAsync(string serverPath, CancellationToken ct)
    {
        try
        {
            var soap = new Soap.TfvcSoapClient(_connection);
            var items = await soap.QueryItemsAsync(serverPath, "None", null, ct).ConfigureAwait(false);
            var item = items.FirstOrDefault(i => !i.IsFolder);
            return item?.ChangesetId;
        }
        catch
        {
            return null;
        }
    }

    private async Task<byte[]> DownloadFileContentAsync(string serverPath, int? changesetId, CancellationToken ct)
    {
        var soap = new Soap.TfvcSoapClient(_connection);
        return await soap.DownloadFileContentAsync(serverPath, changesetId, ct).ConfigureAwait(false);
    }

    private static string NormalizeServerPath(string serverPath) => serverPath.TrimEnd('/');

    /// <summary>
    /// Compares two byte arrays for content equality, normalizing CRLF to LF before comparing.
    /// TFS may serve files with different line endings than what was uploaded (e.g., CRLF ↔ LF),
    /// so a raw byte comparison would produce false negatives after a successful merge.
    /// </summary>
    internal static bool ContentEquals(byte[] a, byte[] b)
    {
        if (a.AsSpan().SequenceEqual(b))
            return true;

        // Normalize CRLF → LF and re-compare to handle server line-ending normalization.
        static byte[] NormalizeCrLf(byte[] data)
        {
            var result = new List<byte>(data.Length);
            for (var i = 0; i < data.Length; i++)
            {
                if (data[i] == (byte)'\r' && i + 1 < data.Length && data[i + 1] == (byte)'\n')
                    continue; // skip CR in CRLF pair
                result.Add(data[i]);
            }
            return result.ToArray();
        }

        return NormalizeCrLf(a).AsSpan().SequenceEqual(NormalizeCrLf(b));
    }

    private static string RemapServerPath(string sourceServerPath, string sourceRoot, string targetRoot)
    {
        if (string.Equals(sourceServerPath, sourceRoot, StringComparison.OrdinalIgnoreCase))
            return targetRoot;

        var relative = sourceServerPath[sourceRoot.Length..].TrimStart('/');
        return string.IsNullOrEmpty(relative)
            ? targetRoot
            : $"{targetRoot}/{relative}";
    }

    private static VersionControlChangeType ResolveMergeChangeType(VersionControlChangeType sourceChangeType, bool targetExists, out string? note)
    {
        note = null;

        if (sourceChangeType.HasFlag(VersionControlChangeType.Rename) || sourceChangeType.HasFlag(VersionControlChangeType.SourceRename) || sourceChangeType.HasFlag(VersionControlChangeType.TargetRename))
        {
            note = "Rename-based merge is not supported in the first execute implementation.";
            return VersionControlChangeType.None;
        }

        // NOTE: The REST "create changeset" (push) API used by CreateChangesetAsync does not
        // support the TFVC `Merge` change-type flag — the server rejects any combination such as
        // "Add, Merge" or "Branch, Merge" with "unsupported change type". Merge metadata (the
        // server-side merge history link) therefore cannot be recorded through this endpoint. To
        // make the merge actually land, we apply the source content to the target as a plain
        // check-in: Edit when the file already exists in the target, Add when it is new, and Delete
        // when the source removed it. The merge lineage is not recorded (callers are warned).
        if (sourceChangeType.HasFlag(VersionControlChangeType.Delete))
        {
            if (!targetExists)
            {
                note = "Target item does not exist, so the delete merge was skipped.";
                return VersionControlChangeType.None;
            }

            return VersionControlChangeType.Delete;
        }

        if (sourceChangeType.HasFlag(VersionControlChangeType.Add)
            || sourceChangeType.HasFlag(VersionControlChangeType.Edit)
            || sourceChangeType.HasFlag(VersionControlChangeType.Branch))
        {
            return targetExists ? VersionControlChangeType.Edit : VersionControlChangeType.Add;
        }

        note = $"Unsupported TFVC change type: {sourceChangeType}.";
        return VersionControlChangeType.None;
    }

    private async Task<bool> IsChangesetContentCoveredByTargetAsync(
        TfvcChangeset detail,
        string sourcePath,
        string targetPath,
        CancellationToken ct)
    {
        var normalizedSource = NormalizeServerPath(sourcePath);
        var normalizedTarget = NormalizeServerPath(targetPath);
        var relevantChanges = (detail.Changes ?? Array.Empty<TfvcChange>())
            .Where(change =>
                change.Item?.IsFolder != true
                && !string.IsNullOrEmpty(change.Item?.Path)
                && IsSameOrDescendantPath(change.Item.Path, normalizedSource))
            .ToList();

        if (relevantChanges.Count == 0)
            return true;

        foreach (var change in relevantChanges)
        {
            var sourceItemPath = change.Item!.Path!;
            var targetItemPath = RemapServerPath(sourceItemPath, normalizedSource, normalizedTarget);
            var targetItem = await TryGetItemSnapshotAsync(targetItemPath, null, ct).ConfigureAwait(false);

            if (change.ChangeType.HasFlag(VersionControlChangeType.Delete))
            {
                if (targetItem is not null)
                    return false;
                continue;
            }

            // Compare the LATEST source content to the target's latest. Using the changeset's own
            // version would flag superseded changesets (whose content was later overwritten on the
            // source branch) as candidates — merging them would regress the target with stale content.
            // If the latest source already matches the target, the file is in sync → exclude.
            var sourceItem = await TryGetItemSnapshotAsync(sourceItemPath, null, ct).ConfigureAwait(false);
            if (sourceItem is null || targetItem is null)
                return false;

            if (!string.IsNullOrEmpty(sourceItem.HashValue) && !string.IsNullOrEmpty(targetItem.HashValue))
            {
                if (!string.Equals(sourceItem.HashValue, targetItem.HashValue, StringComparison.Ordinal))
                    return false;
                continue;
            }

            var sourceContent = await DownloadFileContentAsync(sourceItemPath, null, ct).ConfigureAwait(false);
            var targetContent = await DownloadFileContentAsync(targetItemPath, null, ct).ConfigureAwait(false);
            if (!ContentEquals(sourceContent, targetContent))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 判定相对 merge base 是否存在内容冲突：目标侧已改动，且目标当前内容既不是 base 版本、
    /// 也不同于源侧内容（即源与目标都改了同一文件且结果不一致）。
    /// 纯函数，便于单元测试。任一输入为 null 时返回 false（无法判定，按非冲突处理）。
    /// </summary>
    internal static bool IsContentConflict(byte[]? targetCurrent, byte[]? targetBase, byte[]? sourceContent)
    {
        if (targetCurrent is null || targetBase is null || sourceContent is null)
            return false;

        // Target unchanged since base → no conflict (only source side changed).
        if (ContentEquals(targetCurrent, targetBase))
            return false;

        // Target already matches source → no conflict (same end state).
        if (ContentEquals(targetCurrent, sourceContent))
            return false;

        return true;
    }

    /// <summary>
    /// 对源 changeset 中每个可能"用源覆盖目标"的文件，预先评估是否存在冲突。
    /// 仅对 (a) 源路径属于合并源、(b) 非 folder、(c) 非 metadata-only、(d) 源变更为 Add/Edit/Branch、
    /// (e) 目标已存在、(f) 未提供显式 resolution（resolutionChoice 为 null）的文件下载三方内容并判定。
    /// 显式 resolution（source/target/manual）由调用方语义保证不再视为阻断冲突，故不在此评估。
    /// 返回按规范化源路径索引的评估结果（含已下载的源内容，供后续合并复用，避免重复下载）。
    /// </summary>
    private async Task<Dictionary<string, MergeConflictAssessment>> AssessMergeConflictsAsync(
        TfvcChangeset detail,
        string normalizedSource,
        string normalizedTarget,
        MergeBaseInfo mergeBase,
        IReadOnlyDictionary<string, MergeExecutionResolution> resolutionBySource,
        CancellationToken ct)
    {
        var assessments = new Dictionary<string, MergeConflictAssessment>(StringComparer.OrdinalIgnoreCase);
        var baseChangesetId = mergeBase.TargetBranchPointChangesetId ?? mergeBase.SourceBranchPointChangesetId;
        if (!baseChangesetId.HasValue)
            return assessments;

        foreach (var change in detail.Changes ?? Array.Empty<TfvcChange>())
        {
            var sourceItemPath = change.Item?.Path;
            if (string.IsNullOrEmpty(sourceItemPath) || !IsSameOrDescendantPath(sourceItemPath, normalizedSource))
                continue;
            if (change.Item?.IsFolder == true)
                continue;
            if (IsMergeMetadataOnly(change.ChangeType))
                continue;
            if (!CanForceSourceContentMerge(change.ChangeType))
                continue;

            resolutionBySource.TryGetValue(NormalizeServerPath(sourceItemPath), out var resolution);
            var resolutionChoice = NormalizeResolutionChoice(resolution?.Choice);

            // Only the auto-take-source path (no explicit resolution) needs conflict detection.
            // Explicit source/target/manual are user resolutions and must not block.
            if (resolutionChoice is not null)
                continue;

            var targetServerPath = RemapServerPath(sourceItemPath, normalizedSource, normalizedTarget);
            var targetVersion = await TryGetItemVersionAsync(targetServerPath, ct).ConfigureAwait(false);
            if (!targetVersion.HasValue)
                continue; // target doesn't exist → Add, no conflict possible.

            var sourceVersion = change.Item?.ChangesetVersion > 0
                ? change.Item.ChangesetVersion
                : detail.ChangesetId;

            byte[]? sourceContent = null;
            byte[]? targetCurrent = null;
            byte[]? targetBase = null;
            try
            {
                sourceContent = await DownloadFileContentAsync(sourceItemPath, sourceVersion, ct).ConfigureAwait(false);
                targetCurrent = await DownloadFileContentAsync(targetServerPath, null, ct).ConfigureAwait(false);
                targetBase = await DownloadFileContentAsync(targetServerPath, baseChangesetId.Value, ct).ConfigureAwait(false);
            }
            catch
            {
                // Unable to fetch one of the three versions (e.g., file didn't exist at base).
                // Cannot assess → treat as non-conflict; the merge loop will proceed (take source).
                continue;
            }

            assessments[NormalizeServerPath(sourceItemPath)] = new MergeConflictAssessment(
                SourceServerPath: sourceItemPath,
                TargetServerPath: targetServerPath,
                SourceContent: sourceContent,
                IsConflict: IsContentConflict(targetCurrent, targetBase, sourceContent),
                ResolutionChoice: null);
        }

        return assessments;
    }

    /// <summary>从 resolutions 列表构建按规范化源路径索引的字典（重复键取最后一个）。</summary>
    private static Dictionary<string, MergeExecutionResolution> BuildResolutionBySource(
        IReadOnlyList<MergeExecutionResolution>? resolutions)
    {
        return (resolutions ?? Array.Empty<MergeExecutionResolution>())
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceServerPath))
            .GroupBy(item => NormalizeServerPath(item.SourceServerPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>单文件冲突评估结果（含已下载的源内容，便于合并阶段复用）。</summary>
    private sealed record MergeConflictAssessment(
        string SourceServerPath,
        string TargetServerPath,
        byte[]? SourceContent,
        bool IsConflict,
        string? ResolutionChoice);

    private static bool IsChangesetOriginatingFromTarget(
        TfvcChangeset detail,
        string sourcePath,
        string targetPath)
    {
        var normalizedSource = NormalizeServerPath(sourcePath);
        var normalizedTarget = NormalizeServerPath(targetPath);
        var relevantChanges = (detail.Changes ?? Array.Empty<TfvcChange>())
            .Where(change =>
                change.Item?.IsFolder != true
                && !string.IsNullOrEmpty(change.Item?.Path)
                && IsSameOrDescendantPath(change.Item.Path, normalizedSource))
            .ToList();

        return relevantChanges.Count > 0
            && relevantChanges.All(change =>
                change.MergeSources?.Any(source =>
                    !string.IsNullOrEmpty(source.ServerItem)
                    && IsSameOrDescendantPath(source.ServerItem, normalizedTarget)) == true);
    }

    private async Task<TfsServerItem?> TryGetItemSnapshotAsync(
        string serverPath,
        int? changesetId,
        CancellationToken ct)
    {
        try
        {
            return (await GetItemsAsync(
                    serverPath,
                    recursive: false,
                    atChangeset: changesetId,
                    ct: ct).ConfigureAwait(false))
                .FirstOrDefault(item => string.Equals(item.ServerPath, serverPath, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    internal static string? NormalizeResolutionChoice(string? choice)
    {
        if (string.IsNullOrWhiteSpace(choice))
            return null;

        if (string.Equals(choice, "target", StringComparison.OrdinalIgnoreCase))
            return "target";

        if (string.Equals(choice, "manual", StringComparison.OrdinalIgnoreCase))
            return "manual";

        if (string.Equals(choice, "source", StringComparison.OrdinalIgnoreCase))
            return "source";

        throw new InvalidOperationException($"Unsupported merge resolution choice: {choice}.");
    }

    private static bool CanForceSourceContentMerge(VersionControlChangeType sourceChangeType)
    {
        if (sourceChangeType.HasFlag(VersionControlChangeType.Delete))
            return false;

        return sourceChangeType.HasFlag(VersionControlChangeType.Add)
            || sourceChangeType.HasFlag(VersionControlChangeType.Edit)
            || sourceChangeType.HasFlag(VersionControlChangeType.Branch);
    }

    private static bool IsMergeMetadataOnly(VersionControlChangeType sourceChangeType)
    {
        var contentFlags = VersionControlChangeType.Add
            | VersionControlChangeType.Edit
            | VersionControlChangeType.Delete
            | VersionControlChangeType.Rename
            | VersionControlChangeType.SourceRename
            | VersionControlChangeType.TargetRename
            | VersionControlChangeType.Branch;
        return sourceChangeType.HasFlag(VersionControlChangeType.Merge)
            && (sourceChangeType & contentFlags) == VersionControlChangeType.None;
    }

    private static byte[] DecodeManualMergeContent(MergeExecutionResolution? resolution, string sourceItemPath)
    {
        if (string.IsNullOrWhiteSpace(resolution?.ContentBase64))
            throw new InvalidOperationException($"Manual merge content is missing for {sourceItemPath}.");

        return Convert.FromBase64String(resolution.ContentBase64);
    }

    // ─── Checkin ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 将一组本地挂起变更提交到服务器，创建新的 Changeset。
    /// 调用方需自行读取文件内容并以字节数组形式传入。
    /// </summary>
    /// <param name="comment">Changeset 注释（不得为空）</param>
    /// <param name="changes">
    ///   要提交的变更元组列表。每个元组包含：
    ///   <list type="bullet">
    ///     <item><c>serverPath</c>: TFVC 服务器路径</item>
    ///     <item><c>changeType</c>: 变更类型</item>
    ///     <item><c>content</c>: Add/Edit 时的文件内容（Delete 时为 null）</item>
    ///   </list>
    /// </param>
    /// <param name="ct">取消令牌</param>
    /// <returns>新创建的 Changeset 的引用信息</returns>
    public async Task<TfvcChangesetRef> CheckinAsync(
        string comment,
        IEnumerable<(string serverPath, Models.ChangeType changeType, byte[]? content, int? baseChangesetId)> changes,
        CancellationToken ct = default)
    {
        var soapChanges = changes.Select(c => (
            c.serverPath,
            changeType: MapChangeTypeToSoapString(c.changeType),
            c.content,
            c.baseChangesetId
        )).ToList();

        var csetId = await CreateChangesetViaSoapAsync(comment, soapChanges, ct).ConfigureAwait(false);
        return new TfvcChangesetRef { ChangesetId = csetId };
    }

    /// <summary>将本地 <see cref="Models.ChangeType"/> 转换为 SOAP PendChanges 的请求类型字符串。</summary>
    private static string MapChangeTypeToSoapString(Models.ChangeType changeType) =>
        changeType switch
        {
            Models.ChangeType.Add => "Add",
            Models.ChangeType.Edit => "Edit",
            Models.ChangeType.Delete => "Delete",
            Models.ChangeType.Rename => "Rename",
            Models.ChangeType.Undelete => "Undelete",
            _ => "Edit",
        };

    private static TfvcChange BuildTfvcChange(
        string serverPath,
        VersionControlChangeType changeType,
        byte[]? content,
        int? baseChangesetId,
        IEnumerable<TfvcMergeSource>? mergeSources = null)
    {
        var item = new TfvcItem { Path = serverPath };
        if (baseChangesetId.HasValue)
            item.ChangesetVersion = baseChangesetId.Value;

        var tfvcChange = new TfvcChange
        {
            Item = item,
            ChangeType = changeType,
            MergeSources = mergeSources,
        };

        if (content is null)
            return tfvcChange;

        if (TryDecodeUtf8Text(content, out var textContent, out var hasBom))
        {
            item.ContentMetadata = new FileContentMetadata
            {
                // UTF-8 with BOM → codepage 65001 with BOM marker; plain UTF-8 → 65001 without
                Encoding = hasBom ? 65001 : Encoding.UTF8.CodePage,
                ContentType = "text/plain",
            };
            tfvcChange.NewContent = new ItemContent
            {
                Content = textContent,
                ContentType = ItemContentType.RawText,
            };
            return tfvcChange;
        }

        item.ContentMetadata = new FileContentMetadata
        {
            Encoding = -1,
            IsBinary = true,
        };
        tfvcChange.NewContent = new ItemContent
        {
            Content = Convert.ToBase64String(content),
            ContentType = ItemContentType.Base64Encoded,
        };

        return tfvcChange;
    }

    private static bool TryDecodeUtf8Text(byte[] content, out string text, out bool hasBom)
    {
        text = string.Empty;
        hasBom = false;
        if (Array.IndexOf(content, (byte)0) >= 0)
            return false;

        // Detect and strip UTF-8 BOM (EF BB BF) before sending to the server.
        // TFS rejects content that starts with the BOM bytes as plain text, resulting in
        // "参数值无效" (invalid parameter value) errors on Windows ARM builds.
        ReadOnlySpan<byte> payload = content;
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            hasBom = true;
            payload = content.AsSpan(3);
        }

        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(payload);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// 通过 SOAP 协议创建 changeset（替代 REST CreateChangesetAsync）。
    /// 流程：CreateWorkspace → PendChanges → Upload → CheckIn → DeleteWorkspace。
    /// </summary>
    private async Task<int> CreateChangesetViaSoapAsync(
        string comment,
        IReadOnlyList<(string serverPath, string changeType, byte[]? content, int? baseVersion)> changes,
        CancellationToken ct)
    {
        var owner = await ResolveOwnerForSoapAsync(ct).ConfigureAwait(false);
        var soap = new Soap.TfvcSoapClient(_connection);

        var soapChanges = changes.Select(c => new Models.Soap.SoapContentChange
        {
            ChangeType = c.changeType,
            ServerPath = c.serverPath,
            Content = c.content,
            BaseVersion = c.baseVersion,
        }).ToList();

        return await soap.CheckInWithContentAsync(comment, owner, soapChanges, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve the authenticated user identity (GUID) for SOAP workspace operations.
    /// </summary>
    private async Task<string> ResolveOwnerForSoapAsync(CancellationToken ct)
    {
        var owner = await _connection.GetAuthenticatedUserGuidAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(owner))
            throw new InvalidOperationException(
                "Cannot resolve authenticated user for SOAP operation. Ensure the PAT is valid.");
        return owner;
    }

    /// <summary>
    /// Delete any stale arm-tfs temporary SOAP workspaces that might be holding pending changes.
    /// These are left over from interrupted merge/preview operations.
    /// </summary>
    private async Task CleanupStaleSoapWorkspacesAsync(CancellationToken ct = default)
    {
        try
        {
            var soap = new Soap.TfvcSoapClient(_connection);
            var owner = await _connection.GetAuthenticatedUserGuidAsync(ct).ConfigureAwait(false);
            var workspaces = await soap.QueryWorkspacesAsync(owner, null, ct).ConfigureAwait(false);

            foreach (var ws in workspaces)
            {
                if (ws.Name.StartsWith("arm-tfs-soap-", StringComparison.OrdinalIgnoreCase)
                    || ws.Name.StartsWith("arm-tfs-preview-", StringComparison.OrdinalIgnoreCase)
                    || ws.Name.StartsWith("arm-tfs-lock-", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await soap.DeleteWorkspaceAsync(ws.Name, ws.Owner, ct).ConfigureAwait(false);
                    }
                    catch { /* best effort */ }
                }
            }
        }
        catch { /* best effort - don't fail the revert if cleanup fails */ }
    }
}
