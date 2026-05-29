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
        CancellationToken ct = default)
    {
        var client = _connection.GetTfvcClient();
        var results = await client.GetChangesetsAsync(
            project: null,
            maxCommentLength: 200,
            skip: 0,
            top: top,
            orderby: "id desc",
            searchCriteria: new TfvcChangesetSearchCriteria
            {
                ItemPath = serverPath,
                Author = author,
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
        return await client.GetChangesetAsync(
            id: changesetId,
            includeDetails: true,
            includeWorkItems: true,
            includeSourceRename: true,
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
