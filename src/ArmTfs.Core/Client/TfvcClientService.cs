using ArmTfs.Core.Models;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using System.Text;

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

    /// <summary>初始化服务。</summary>
    /// <param name="connection">已创建的连接对象，生命周期由调用方管理</param>
    public TfvcClientService(TfsConnection connection)
    {
        _connection = connection;
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
        CancellationToken ct = default)
    {
        var client = _connection.GetTfvcClient();
        var recursion = recursive ? VersionControlRecursionType.Full : VersionControlRecursionType.None;

        TfvcVersionDescriptor? version = atChangeset.HasValue
            ? new TfvcVersionDescriptor { VersionType = TfvcVersionType.Changeset, Version = atChangeset.Value.ToString() }
            : null;

        var items = await client.GetItemsAsync(
            scopePath: serverPath,
            recursionLevel: recursion,
            versionDescriptor: version,
            cancellationToken: ct).ConfigureAwait(false);

        return items.Select(i => new TfsServerItem
        {
            ServerPath = i.Path,
            IsFolder = i.IsFolder,
            ChangesetId = i.ChangesetVersion,
            ContentLength = i.Size,
            HashValue = i.HashValue,
            CheckinDate = i.ChangeDate,
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
        var client = _connection.GetTfvcClient();

        TfvcVersionDescriptor? version = atChangeset.HasValue
            ? new TfvcVersionDescriptor { VersionType = TfvcVersionType.Changeset, Version = atChangeset.Value.ToString() }
            : null;

        var stream = await client.GetItemContentAsync(
            path: serverPath,
            versionDescriptor: version,
            cancellationToken: ct).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
            await stream.CopyToAsync(destination, ct).ConfigureAwait(false);
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
        var client = _connection.GetTfvcClient();
        var results = await client.GetChangesetsAsync(
            project: null,
            maxCommentLength: 200,
            skip: skip,
            top: top,
            orderby: orderby,
            searchCriteria: new TfvcChangesetSearchCriteria
            {
                ItemPath = serverPath,
                Author = author,
                FromDate = fromDate?.ToString("O"),
                ToDate = toDate?.ToString("O"),
            },
            cancellationToken: ct).ConfigureAwait(false);

        return results;
    }

    /// <summary>获取单个 Changeset 的详细信息，包括文件列表、工作项和审核信息。</summary>
    /// <param name="changesetId">Changeset 编号</param>
    /// <param name="ct">取消令牌</param>
    public async Task<TfvcChangeset> GetChangesetAsync(int changesetId, CancellationToken ct = default)
    {
        var client = _connection.GetTfvcClient();
        var changeset = await client.GetChangesetAsync(
            id: changesetId,
            maxChangeCount: 100,
            includeDetails: true,
            includeWorkItems: true,
            includeSourceRename: true,
            cancellationToken: ct).ConfigureAwait(false);

        if (changeset.HasMoreChanges || changeset.Changes is null)
        {
            const int pageSize = 100;
            var changes = new List<TfvcChange>();
            var skip = 0;

            while (true)
            {
                var page = await client.GetChangesetChangesAsync(
                    id: changesetId,
                    skip: skip,
                    top: pageSize,
                    cancellationToken: ct).ConfigureAwait(false);

                if (page.Count == 0)
                    break;

                changes.AddRange(page);
                if (page.Count < pageSize)
                    break;

                skip += page.Count;
            }

            changeset.Changes = changes;
            changeset.HasMoreChanges = false;
        }

        return changeset;
    }

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
        var client = _connection.GetTfvcClient();
        return await client.GetLabelsAsync(
            requestData: new TfvcLabelRequestData
            {
                Owner = owner,
                Name = name,
                LabelScope = labelScope,
                MaxItemCount = maxItemCount,
                IncludeLinks = false,
            },
            top: top,
            skip: skip,
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>获取单个 TFVC Label 详情。</summary>
    public async Task<TfvcLabel> GetLabelAsync(
        string labelId,
        int? maxItemCount = null,
        CancellationToken ct = default)
    {
        var client = _connection.GetTfvcClient();
        return await client.GetLabelAsync(
            labelId: labelId,
            requestData: new TfvcLabelRequestData
            {
                MaxItemCount = maxItemCount,
                IncludeLinks = false,
            },
            cancellationToken: ct).ConfigureAwait(false);
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
        var client = _connection.GetTfvcClient();
        var results = await client.GetShelvesetsAsync(
            requestData: new TfvcShelvesetRequestData
            {
                Owner = owner,
                Name = name,
                IncludeDetails = true,
            },
            cancellationToken: ct).ConfigureAwait(false);

        return results;
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
        var client = _connection.GetTfvcClient();
        var id = string.IsNullOrEmpty(owner) ? shelvesetName : $"{shelvesetName};{owner}";
        return await client.GetShelvesetChangesAsync(
            shelvesetId: id,
            cancellationToken: ct).ConfigureAwait(false);
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
        var client = _connection.GetTfvcClient();
        return await client.GetBranchRefsAsync(
            scopePath: scopePath,
            includeDeleted: includeDeleted,
            includeLinks: false,
            cancellationToken: ct).ConfigureAwait(false);
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
        var client = _connection.GetTfvcClient();
        return await client.GetBranchAsync(
            path: path,
            includeParent: true,
            includeChildren: includeChildren,
            cancellationToken: ct).ConfigureAwait(false);
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
        CancellationToken ct = default)
    {
        var baseInfo = await ResolveMergeBaseAsync(sourcePath, targetPath, ct).ConfigureAwait(false);
        var sourceHistory = await GetChangesetsAsync(sourcePath, top: scan, ct: ct).ConfigureAwait(false);
        var targetHistory = await GetChangesetsAsync(targetPath, top: scan, ct: ct).ConfigureAwait(false);

        var mergedRanges = new List<MergeSourceRange>();
        var sourceMatchPath = baseInfo.SourceBranchPath ?? sourcePath;

        foreach (var targetChangeset in targetHistory)
        {
            var detail = await GetChangesetAsync(targetChangeset.ChangesetId, ct).ConfigureAwait(false);
            if (detail.Changes is null)
                continue;

            foreach (var change in detail.Changes)
            {
                if (change.MergeSources is null)
                    continue;

                foreach (var mergeSource in change.MergeSources)
                {
                    if (string.IsNullOrEmpty(mergeSource.ServerItem) || !IsSameOrDescendantPath(mergeSource.ServerItem, sourceMatchPath))
                        continue;

                    mergedRanges.Add(new MergeSourceRange
                    {
                        ServerItem = mergeSource.ServerItem,
                        VersionFrom = mergeSource.VersionFrom,
                        VersionTo = mergeSource.VersionTo,
                        IsRename = mergeSource.IsRename,
                        TargetChangesetId = targetChangeset.ChangesetId,
                    });
                }
            }
        }

        var uniqueFloor = GetSourceUniqueFloor(baseInfo);
        var candidates = sourceHistory
            .Select(changeset =>
            {
                var coveringRange = mergedRanges.FirstOrDefault(range => range.Covers(changeset.ChangesetId));
                return new MergeCandidateInfo
                {
                    ChangesetId = changeset.ChangesetId,
                    CreatedAt = changeset.CreatedDate,
                    Comment = changeset.Comment,
                    AuthorDisplayName = changeset.Author?.DisplayName,
                    AuthorUniqueName = changeset.Author?.UniqueName,
                    IsMergedToTarget = coveringRange is not null,
                    CoveredByTargetChangesetId = coveringRange?.TargetChangesetId,
                    CoveredByRange = coveringRange,
                };
            })
            .Where(candidate => !uniqueFloor.HasValue || candidate.ChangesetId > uniqueFloor.Value)
            .Where(candidate => !candidate.IsMergedToTarget)
            .Take(top)
            .ToList();

        return new MergeCandidateQueryResult
        {
            BaseInfo = baseInfo,
            SourceHistoryScanned = sourceHistory.Count,
            TargetHistoryScanned = targetHistory.Count,
            SourceUniqueFloorChangesetId = uniqueFloor,
            MergedRanges = mergedRanges
                .OrderByDescending(range => range.VersionTo ?? int.MinValue)
                .ThenByDescending(range => range.TargetChangesetId)
                .ToList(),
            Candidates = candidates,
        };
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
        var client = _connection.GetTfvcClient();

        var tfvcChanges = new List<TfvcChange>();
        foreach (var (serverPath, changeType, content, baseChangesetId) in changes)
        {
            var tfvcChangeType = MapChangeType(changeType);
            var item = new TfvcItem { Path = serverPath };
            if (baseChangesetId.HasValue)
                item.ChangesetVersion = baseChangesetId.Value;

            var tfvcChange = new TfvcChange
            {
                Item = item,
                ChangeType = tfvcChangeType,
            };

            if (content is not null && changeType is Models.ChangeType.Add or Models.ChangeType.Edit)
            {
                if (TryDecodeUtf8Text(content, out var textContent))
                {
                    item.ContentMetadata = new FileContentMetadata
                    {
                        Encoding = Encoding.UTF8.CodePage,
                        ContentType = "text/plain",
                    };
                    tfvcChange.NewContent = new ItemContent
                    {
                        Content = textContent,
                        ContentType = ItemContentType.RawText,
                    };
                }
                else
                {
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
                }
            }

            tfvcChanges.Add(tfvcChange);
        }

        var changeset = new TfvcChangeset
        {
            Comment = comment,
            Changes = tfvcChanges,
        };

        return await client.CreateChangesetAsync(changeset, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>将本地 <see cref="Models.ChangeType"/> 转换为 REST API 的 <see cref="VersionControlChangeType"/>。</summary>
    private static VersionControlChangeType MapChangeType(Models.ChangeType changeType) =>
        changeType switch
        {
            Models.ChangeType.Add => VersionControlChangeType.Add,
            Models.ChangeType.Edit => VersionControlChangeType.Edit,
            Models.ChangeType.Delete => VersionControlChangeType.Delete,
            Models.ChangeType.Rename => VersionControlChangeType.Rename,
            Models.ChangeType.Undelete => VersionControlChangeType.Undelete,
            _ => VersionControlChangeType.None,
        };

    private static bool TryDecodeUtf8Text(byte[] content, out string text)
    {
        text = string.Empty;
        if (Array.IndexOf(content, (byte)0) >= 0)
            return false;

        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
