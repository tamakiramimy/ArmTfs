using ArmTfs.Core.Models;
using ArmTfs.Core.Workspace;
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
        var client = _connection.GetTfvcClient();
        VersionControlRecursionType recursion;
        if (oneLevelOnly)
            recursion = VersionControlRecursionType.OneLevel;
        else if (recursive)
            recursion = VersionControlRecursionType.Full;
        else
            recursion = VersionControlRecursionType.None;

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

        var sourceVersion = sourceChangesetId ?? await TryGetItemVersionAsync(normalizedSource, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Source branch '{normalizedSource}' does not exist.");
        if (await TryGetItemVersionAsync(normalizedTarget, ct).ConfigureAwait(false) is not null)
            throw new InvalidOperationException($"Target branch '{normalizedTarget}' already exists.");

        var change = new TfvcChange
        {
            ChangeType = VersionControlChangeType.Branch,
            SourceServerItem = normalizedSource,
            Item = new TfvcItem
            {
                Path = normalizedTarget,
                ChangesetVersion = sourceVersion,
                IsBranch = true,
                IsFolder = true,
            },
        };

        return await _connection.GetTfvcClient().CreateChangesetAsync(new TfvcChangeset
        {
            Comment = string.IsNullOrWhiteSpace(comment)
                ? $"Branch {normalizedTarget} from {normalizedSource} at cs#{sourceVersion}"
                : comment.Trim(),
            Changes = new[] { change },
        }, cancellationToken: ct).ConfigureAwait(false);
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
        IReadOnlySet<int>? locallyMergedIds = null,
        CancellationToken ct = default)
    {
        var baseInfo = await ResolveMergeBaseAsync(sourcePath, targetPath, ct).ConfigureAwait(false);
        var sourceHistory = await GetChangesetsAsync(sourcePath, top: scan, ct: ct).ConfigureAwait(false);
        var targetHistory = await GetChangesetsAsync(targetPath, top: scan, ct: ct).ConfigureAwait(false);

        var mergedRanges = new List<MergeSourceRange>();
        var sourceMatchPath = baseInfo.SourceBranchPath ?? sourcePath;

        // Collect comment-marker-tracked merges (REST merges that don't write server merge history).
        var commentTrackedIds = new HashSet<int>();
        foreach (var targetChangeset in targetHistory)
        {
            var detail = await GetChangesetAsync(targetChangeset.ChangesetId, ct).ConfigureAwait(false);

            var marker = MergeCommentMarker.Parse(detail.Comment);
            if (marker is not null
                && marker.SourceChangesetId > 0
                && IsSameOrDescendantPath(sourcePath, marker.SourcePath)
                && IsSameOrDescendantPath(targetPath, marker.TargetPath))
            {
                commentTrackedIds.Add(marker.SourceChangesetId);
            }

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
        var candidates = new List<MergeCandidateInfo>();
        foreach (var changeset in sourceHistory)
        {
            if (uniqueFloor.HasValue && changeset.ChangesetId <= uniqueFloor.Value)
                continue;

            var coveringRange = mergedRanges.FirstOrDefault(range => range.Covers(changeset.ChangesetId));
            if (coveringRange is not null)
                continue;

            // Filter: merged via arm-tfs comment marker in target history
            if (commentTrackedIds.Contains(changeset.ChangesetId))
                continue;

            // Filter: merged via local .tf/merge-history.json
            if (locallyMergedIds?.Contains(changeset.ChangesetId) == true)
                continue;

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
            TargetHistoryScanned = targetHistory.Count,
            SourceUniqueFloorChangesetId = uniqueFloor,
            MergedRanges = mergedRanges
                .OrderByDescending(range => range.VersionTo ?? int.MinValue)
                .ThenByDescending(range => range.TargetChangesetId)
                .ToList(),
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
        CancellationToken ct = default)
    {
        var normalizedSource = NormalizeServerPath(sourcePath);
        var normalizedTarget = NormalizeServerPath(targetPath);
        var mergeBase = await ResolveMergeBaseAsync(normalizedSource, normalizedTarget, ct).ConfigureAwait(false);
        var detail = await GetChangesetAsync(sourceChangesetId, ct).ConfigureAwait(false);
        var effectiveComment = string.IsNullOrWhiteSpace(comment)
            ? $"Merge cs#{sourceChangesetId} from {normalizedSource} to {normalizedTarget}"
            : comment.Trim();

        var warnings = new List<string>();
        var plannedChanges = new List<MergeExecutionChange>();
        var tfvcChanges = new List<TfvcChange>();
        var expectedContentByTarget = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var expectedDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolutionBySource = (resolutions ?? Array.Empty<MergeExecutionResolution>())
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceServerPath))
            .GroupBy(item => NormalizeServerPath(item.SourceServerPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

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

            byte[]? content = null;
            var requiresContent = targetChangeType.HasFlag(VersionControlChangeType.Add) || targetChangeType.HasFlag(VersionControlChangeType.Edit);
            if (resolutionChoice == "manual")
            {
                content = DecodeManualMergeContent(resolution, sourceItemPath);
            }
            else if (requiresContent)
            {
                content = await DownloadFileContentAsync(sourceItemPath, change.Item?.ChangesetVersion ?? sourceChangesetId, ct).ConfigureAwait(false);
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
            tfvcChanges.Add(BuildTfvcChange(
                targetServerPath,
                targetChangeType,
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

        if (dryRun || tfvcChanges.Count == 0)
        {
            if (!dryRun && tfvcChanges.Count == 0)
                warnings.Add("No executable merge changes were produced for the requested source changeset.");

            return new MergeExecutionResult
            {
                SourcePath = normalizedSource,
                TargetPath = normalizedTarget,
                SourceChangesetId = sourceChangesetId,
                Comment = effectiveComment,
                DryRun = dryRun,
                BaseInfo = mergeBase,
                Changes = plannedChanges,
                Warnings = warnings,
            };
        }

        warnings.Add("Changes were applied as a direct check-in (take-source). TFVC merge history is not recorded because the REST changeset API does not support merge change types.");

        // Embed a structured marker so the candidate filter can detect this merge cross-workspace.
        var commentWithMarker = effectiveComment.TrimEnd()
            + " "
            + MergeCommentMarker.Build(normalizedSource, sourceChangesetId, normalizedTarget);

        var created = await _connection.GetTfvcClient().CreateChangesetAsync(new TfvcChangeset
        {
            Comment = commentWithMarker,
            Changes = tfvcChanges,
        }, cancellationToken: ct).ConfigureAwait(false);

        await VerifyMergeChangesetAppliedAsync(
            created.ChangesetId,
            tfvcChanges.Select(change => change.Item?.Path).Where(path => !string.IsNullOrEmpty(path)).Cast<string>().ToArray(),
            expectedContentByTarget,
            expectedDeletes,
            ct).ConfigureAwait(false);

        // Record in local merge-history so the candidate list clears immediately,
        // even before the target history scan picks up the comment marker.
        _workspaceManager?.RecordMerge(new MergeRecord
        {
            SourceChangesetId = sourceChangesetId,
            SourcePath = normalizedSource,
            TargetPath = normalizedTarget,
            TargetChangesetId = created.ChangesetId,
            MergedAtUtc = DateTime.UtcNow,
            Method = "rest-takesource",
        });

        return new MergeExecutionResult
        {
            SourcePath = normalizedSource,
            TargetPath = normalizedTarget,
            SourceChangesetId = sourceChangesetId,
            Comment = commentWithMarker,
            DryRun = false,
            CreatedChangesetId = created.ChangesetId,
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

    private async Task VerifyMergeChangesetAppliedAsync(
        int createdChangesetId,
        IReadOnlyCollection<string> expectedTargetPaths,
        IReadOnlyDictionary<string, byte[]> expectedContentByTarget,
        IReadOnlySet<string> expectedDeletes,
        CancellationToken ct)
    {
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
                    if (!actual.AsSpan().SequenceEqual(expectedContent))
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

        foreach (var (targetPath, expectedContent) in expectedContentByTarget)
        {
            // Already verified above as part of server-dedup check — skip re-download.
            if (!changedPaths.Contains(NormalizeServerPath(targetPath)))
                continue;

            var actualContent = await DownloadFileContentAsync(targetPath, null, ct).ConfigureAwait(false);
            if (!actualContent.AsSpan().SequenceEqual(expectedContent))
                throw new InvalidOperationException($"Merge verification failed: target file '{targetPath}' content does not match the source content after cs#{createdChangesetId}.");
        }

        foreach (var targetPath in expectedDeletes)
        {
            if (await TryGetItemVersionAsync(targetPath, ct).ConfigureAwait(false) is not null)
                throw new InvalidOperationException($"Merge verification failed: target file '{targetPath}' still exists after delete merge cs#{createdChangesetId}.");
        }
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
            var item = await _connection.GetTfvcClient().GetItemAsync(serverPath, cancellationToken: ct).ConfigureAwait(false);
            return item?.ChangesetVersion;
        }
        catch
        {
            return null;
        }
    }

    private async Task<byte[]> DownloadFileContentAsync(string serverPath, int? changesetId, CancellationToken ct)
    {
        await using var stream = new MemoryStream();
        await DownloadFileAsync(serverPath, stream, changesetId, ct).ConfigureAwait(false);
        return stream.ToArray();
    }

    private static string NormalizeServerPath(string serverPath) => serverPath.TrimEnd('/');

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

            var sourceVersion = change.Item.ChangesetVersion > 0
                ? change.Item.ChangesetVersion
                : detail.ChangesetId;
            var sourceItem = await TryGetItemSnapshotAsync(sourceItemPath, sourceVersion, ct).ConfigureAwait(false);
            if (sourceItem is null || targetItem is null)
                return false;

            if (!string.IsNullOrEmpty(sourceItem.HashValue) && !string.IsNullOrEmpty(targetItem.HashValue))
            {
                if (!string.Equals(sourceItem.HashValue, targetItem.HashValue, StringComparison.Ordinal))
                    return false;
                continue;
            }

            var sourceContent = await DownloadFileContentAsync(sourceItemPath, sourceVersion, ct).ConfigureAwait(false);
            var targetContent = await DownloadFileContentAsync(targetItemPath, null, ct).ConfigureAwait(false);
            if (!sourceContent.AsSpan().SequenceEqual(targetContent))
                return false;
        }

        return true;
    }

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

    private static string NormalizeResolutionChoice(string? choice)
    {
        if (string.Equals(choice, "target", StringComparison.OrdinalIgnoreCase))
            return "target";

        if (string.Equals(choice, "manual", StringComparison.OrdinalIgnoreCase))
            return "manual";

        return "source";
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
        var client = _connection.GetTfvcClient();

        var tfvcChanges = new List<TfvcChange>();
        foreach (var (serverPath, changeType, content, baseChangesetId) in changes)
        {
            var tfvcChangeType = MapChangeType(changeType);
            tfvcChanges.Add(BuildTfvcChange(serverPath, tfvcChangeType, content, baseChangesetId));
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
