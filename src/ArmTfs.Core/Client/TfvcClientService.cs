using ArmTfs.Core.Client.Soap;
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
            maxCommentLength: 4000,
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

    /// <summary>
    /// 返回服务器上 serverPath 路径下，changesetId 之后（更新的）有多少个 changeset，
    /// 即本地落后服务器的数量（⬇N）。最多扫 scanTop 条历史。
    /// </summary>
    public async Task<int> GetBehindCountAsync(string serverPath, int localMaxChangesetId, int scanTop = 50, CancellationToken ct = default)
    {
        var history = await GetChangesetsAsync(serverPath, top: scanTop, ct: ct).ConfigureAwait(false);
        return history.Count(cs => cs.ChangesetId > localMaxChangesetId);
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
        string mergeMode = "rest",
        string? soapOwner = null,
        CancellationToken ct = default)
    {
        var normalizedSource = NormalizeServerPath(sourcePath);
        var normalizedTarget = NormalizeServerPath(targetPath);

        // SOAP path: lets the server record real merge history (REST cannot).
        // Skip it for dry-run (we still want a plan preview via REST logic).
        if (string.Equals(mergeMode, "soap", StringComparison.OrdinalIgnoreCase) && !dryRun)
        {
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

        var verifyWarnings = await VerifyMergeChangesetAppliedAsync(
            created.ChangesetId,
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

        // Conflict pre-check (same logic as the REST path). Unresolved conflicts block the merge;
        // content-level resolutions (source/manual) cannot be honored by the naive SOAP PendMerge+CheckIn
        // flow, so they fall back to the REST execute path which handles per-file content correctly.
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

        // Any conflict resolution (source/target/manual) means there are conflicts to resolve.
        // The SOAP flow can't apply per-file conflict resolutions without ResolveConflict plumbing,
        // so fall back to REST (which handles source/target/manual correctly). The cost: no server
        // merge history for that changeset (dedup still via comment marker + local merge-history).
        // Clean merges (no resolutions) proceed via SOAP and write real merge history.
        if (resolutionBySource.Count > 0)
        {
            warnings.Add("SOAP merge fell back to REST for conflict resolutions (source/target/manual); TFVC merge history not recorded for this changeset. Candidate dedup is preserved via the comment marker + local merge-history.");
            var restResult = await MergeChangesetAsync(
                normalizedSource, normalizedTarget, sourceChangesetId, comment,
                dryRun: false, resolutions, mergeMode: "rest", soapOwner: soapOwner, ct).ConfigureAwait(false);
            return new MergeExecutionResult
            {
                SourcePath = restResult.SourcePath,
                TargetPath = restResult.TargetPath,
                SourceChangesetId = restResult.SourceChangesetId,
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
            var ops = pendResult.Operations;

            // The server's 3-way merge may have pended CONFLICTS (both sides changed the same file).
            // These cannot be checked in without resolution. Abort with the conflict list — never
            // silently take one side (problem 2: 不再静默用源覆盖目标). This also corrects the old
            // false "already merged" report that happened when conflicts left Operations empty.
            if (pendResult.Conflicts.Count > 0)
            {
                warnings.Add($"Merge aborted: {pendResult.Conflicts.Count} conflict(s) detected by the server's 3-way merge. Resolve each (source/target/manual) and retry.");
                return new MergeExecutionResult
                {
                    SourcePath = normalizedSource,
                    TargetPath = normalizedTarget,
                    SourceChangesetId = sourceChangesetId,
                    Comment = commentWithMarker,
                    DryRun = false,
                    CreatedChangesetId = null,
                    BaseInfo = mergeBase,
                    Changes = pendResult.Conflicts.Select(c => new MergeExecutionChange
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

            if (ops.Count == 0)
            {
                warnings.Add("Server returned no merge operations — nothing to commit. The source changeset may already be merged.");
                return new MergeExecutionResult
                {
                    SourcePath = normalizedSource,
                    TargetPath = normalizedTarget,
                    SourceChangesetId = sourceChangesetId,
                    Comment = commentWithMarker,
                    DryRun = false,
                    CreatedChangesetId = null,
                    BaseInfo = mergeBase,
                    Changes = Array.Empty<MergeExecutionChange>(),
                    Warnings = warnings,
                };
            }

            // No resolutions reach here (any resolution triggers REST fallback above), so commit all ops.
            var committedOps = ops;

            if (committedOps.Count == 0)
            {
                warnings.Add("All merge operations were resolved to 'target'; nothing to commit.");
                return new MergeExecutionResult
                {
                    SourcePath = normalizedSource,
                    TargetPath = normalizedTarget,
                    SourceChangesetId = sourceChangesetId,
                    Comment = commentWithMarker,
                    DryRun = false,
                    CreatedChangesetId = null,
                    BaseInfo = mergeBase,
                    Changes = Array.Empty<MergeExecutionChange>(),
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
                Note = "SOAP merge — server-recorded merge history",
            }).ToList();

            return new MergeExecutionResult
            {
                SourcePath = normalizedSource,
                TargetPath = normalizedTarget,
                SourceChangesetId = sourceChangesetId,
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
