using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Xml.Linq;
using ArmTfs.Core.Models.Soap;

namespace ArmTfs.Core.Client.Soap;

/// <summary>
/// 通过 TFVC 旧版 SOAP 协议（Repository.asmx）调用 TFS。
/// REST API 不支持的变更类型（Branch、Merge）必须走此接口。
/// <para>
/// 协议：自定义 SOAP/XML，端点 <c>{collection}/VersionControl/v1.0/Repository.asmx</c>，
/// XML namespace 见 <see cref="Ns"/>。参考 TEE Java 源码
/// (microsoft/team-explorer-everywhere) 的方法签名构造请求。
/// </para>
/// </summary>
public sealed class TfvcSoapClient
{
    /// <summary>TFVC SOAP 协议的 XML 命名空间。</summary>
    public const string Ns = "http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03";

    private const string EndpointSuffix = "/VersionControl/v1.0/Repository.asmx";

    private readonly TfsConnection _connection;

    public TfvcSoapClient(TfsConnection connection)
    {
        _connection = connection;
    }

    /// <summary>SOAP 端点完整 URL。</summary>
    public string EndpointUrl => _connection.ServerUrl.TrimEnd('/') + EndpointSuffix;

    /// <summary>对字符串做 XML 转义，避免注入。</summary>
    public static string Esc(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : SecurityElement.Escape(value) ?? string.Empty;

    /// <summary>
    /// 发起一次 SOAP 调用，返回响应 XML。失败时抛出 <see cref="SoapFaultException"/>。
    /// </summary>
    /// <param name="method">SOAP 方法名（用于 SOAPAction header）</param>
    /// <param name="bodyInner">SOAP body 内部 XML 片段（不含 Envelope/Body wrapper）</param>
    public async Task<XDocument> InvokeAsync(string method, string bodyInner, CancellationToken ct = default)
    {
        var envelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/""
               xmlns:tns=""{Ns}""
               xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
               xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <soap:Body>
{bodyInner}
  </soap:Body>
</soap:Envelope>";

        using var httpClient = _connection.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
        request.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
        request.Headers.Add("SOAPAction", $"\"{Ns}/{method}\"");

        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // Parse defensively — TFS error bodies are usually XML but not always.
        XDocument? doc = null;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            // Non-XML body; handled below.
        }

        // TFS frequently returns a SOAP <Fault> with HTTP 200 (not just 5xx). Detect a Fault
        // element in the body regardless of status code so callers see the real server error
        // (e.g. TF14061 owner mismatch, TF14050 permission) instead of an opaque "missing
        // element" message downstream.
        var faultEl = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (!response.IsSuccessStatusCode || faultEl is not null)
        {
            var message = faultEl is not null
                ? ExtractFaultMessage(xml)
                : (string.IsNullOrWhiteSpace(xml)
                    ? $"HTTP {(int)response.StatusCode} {response.StatusCode} (empty body)"
                    : xml[..Math.Min(500, xml.Length)]);
            throw new SoapFaultException(method, response.StatusCode, message, xml);
        }

        return doc ?? throw new SoapFaultException(method, response.StatusCode, "Empty SOAP response", xml);
    }

    /// <summary>从响应 XML 中提取 <c>&lt;faultstring&gt;</c>；解析失败时返回前 500 字符。</summary>
    public static string ExtractFaultMessage(string xml)
    {
        if (string.IsNullOrEmpty(xml))
            return "(empty response)";
        var match = System.Text.RegularExpressions.Regex.Match(
            xml,
            @"<faultstring[^>]*>(.+?)</faultstring>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return match.Success
            ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value)
            : xml[..Math.Min(500, xml.Length)];
    }

    // ─── CreateBranch（已有功能，迁移到此处） ─────────────────────────────────

    /// <summary>
    /// 创建 TFVC 分支。返回新创建的 changeset ID。
    /// REST changeset API 不支持 <c>Branch</c> 变更类型，必须走 SOAP。
    /// </summary>
    public async Task<int> CreateBranchAsync(
        string sourcePath,
        string targetPath,
        int? sourceChangesetId,
        string comment,
        CancellationToken ct = default)
    {
        var versionElement = sourceChangesetId.HasValue
            ? $@"<tns:version xsi:type=""tns:ChangesetVersionSpec""><tns:cs>{sourceChangesetId.Value}</tns:cs></tns:version>"
            : @"<tns:version xsi:type=""tns:LatestVersionSpec""/>";

        var body = $@"    <tns:CreateBranch>
      <tns:sourcePath>{Esc(sourcePath)}</tns:sourcePath>
      <tns:targetPath>{Esc(targetPath)}</tns:targetPath>
      {versionElement}
      <tns:info><tns:Comment>{Esc(comment)}</tns:Comment></tns:info>
    </tns:CreateBranch>";

        var doc = await InvokeAsync("CreateBranch", body, ct).ConfigureAwait(false);

        // Response shape: <CreateBranchResponse><CreateBranchResult cset="123"/></CreateBranchResponse>
        var result = doc.Descendants(XName.Get("CreateBranchResult", Ns)).FirstOrDefault()
                  ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CreateBranchResult");

        var csetAttr = result?.Attribute("cset")?.Value;
        if (string.IsNullOrEmpty(csetAttr) || !int.TryParse(csetAttr, out var changesetId))
        {
            // Fallback: regex on the raw XML text (some servers use lowercase or different casing)
            var match = System.Text.RegularExpressions.Regex.Match(doc.ToString(), @"cset=""(\d+)""");
            if (!match.Success)
                throw new SoapFaultException("CreateBranch", System.Net.HttpStatusCode.OK,
                    "CreateBranch response did not contain cset attribute", doc.ToString());
            changesetId = int.Parse(match.Groups[1].Value);
        }

        return changesetId;
    }

    // ─── QueryWorkspaces（连通性自检） ────────────────────────────────────────

    /// <summary>
    /// 查询 workspace 列表。可按 owner 或 computer 过滤；不传任一参数则返回当前认证用户能看到的全部。
    /// 这是只读、安全的连通性测试方法。
    /// </summary>
    public async Task<IReadOnlyList<SoapWorkspace>> QueryWorkspacesAsync(
        string? ownerName = null,
        string? computer = null,
        CancellationToken ct = default)
    {
        var ownerEl = ownerName is null
            ? @"<tns:ownerName xsi:nil=""true"" />"
            : $"<tns:ownerName>{Esc(ownerName)}</tns:ownerName>";
        var computerEl = computer is null
            ? @"<tns:computer xsi:nil=""true"" />"
            : $"<tns:computer>{Esc(computer)}</tns:computer>";

        var body = $@"    <tns:QueryWorkspaces>
      {ownerEl}
      {computerEl}
    </tns:QueryWorkspaces>";

        var doc = await InvokeAsync("QueryWorkspaces", body, ct).ConfigureAwait(false);

        var result = new List<SoapWorkspace>();
        foreach (var ws in doc.Descendants().Where(e => e.Name.LocalName == "Workspace"))
        {
            result.Add(new SoapWorkspace
            {
                Name = ws.Attribute("name")?.Value ?? AttrAny(ws, "Name") ?? string.Empty,
                Owner = ws.Attribute("owner")?.Value ?? AttrAny(ws, "Owner") ?? string.Empty,
                OwnerDisplay = ws.Attribute("ownerdisp")?.Value ?? AttrAny(ws, "OwnerDisplay"),
                Computer = ws.Attribute("computer")?.Value ?? AttrAny(ws, "Computer") ?? string.Empty,
                Comment = ChildText(ws, "Comment") ?? string.Empty,
                Location = ws.Attribute("location")?.Value ?? AttrAny(ws, "Location"),
                LastAccessChangesetId = TryParseInt(ws.Attribute("lastAccessDate")?.Value),
            });
        }

        return result;
    }

    private static string? ChildText(XElement element, string localName)
    {
        return element.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static int? TryParseInt(string? value) =>
        int.TryParse(value, out var n) ? n : null;

    // ─── Workspace 生命周期 ───────────────────────────────────────────────────

    /// <summary>
    /// 创建一个 server-side workspace（无本地映射，纯服务器侧）。
    /// 用于 PendMerge / CheckIn 这类需要 workspace 上下文的临时操作。
    /// </summary>
    /// <param name="name">Workspace 名称（同一 owner 下唯一）</param>
    /// <param name="ownerName">Owner（DOMAIN\user / user@domain），需与认证身份匹配</param>
    /// <param name="computer">客户端机器名</param>
    /// <param name="comment">备注</param>
    public async Task<SoapWorkspace> CreateWorkspaceAsync(
        string name,
        string? ownerName,
        string computer,
        string? comment = null,
        IReadOnlyList<(string ServerItem, string LocalPath)>? workingFolders = null,
        CancellationToken ct = default)
    {
        // OwnerName is REQUIRED by TFS (omitting it throws ArgumentNullException). The caller must
        // pass the authenticated user's identity (GUID) — see TfvcClientService owner resolution.
        // PendMerge requires a working-folder mapping for the merge target, so the caller passes one.
        var foldersXml = (workingFolders is null || workingFolders.Count == 0)
            ? "<tns:Folders />"
            : "<tns:Folders>" + string.Concat(workingFolders.Select(f =>
                $@"<tns:WorkingFolder item=""{Esc(f.ServerItem)}"" local=""{Esc(f.LocalPath)}"" type=""Map"" />")) + "</tns:Folders>";

        var body = $@"    <tns:CreateWorkspace>
      <tns:workspace name=""{Esc(name)}"" owner=""{Esc(ownerName ?? string.Empty)}"" computer=""{Esc(computer)}"" location=""Server"">
        <tns:Comment>{Esc(comment ?? string.Empty)}</tns:Comment>
        {foldersXml}
        <tns:LastAccessDate>0001-01-01T00:00:00</tns:LastAccessDate>
      </tns:workspace>
    </tns:CreateWorkspace>";

        var doc = await InvokeAsync("CreateWorkspace", body, ct).ConfigureAwait(false);

        // TFS returns the created workspace as <CreateWorkspaceResult .../> with the workspace
        // attributes (name/owner/computer/ownerdisp/...) directly on the result element — NOT as a
        // child <Workspace> element. Accept both shapes for robustness.
        var ws = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CreateWorkspaceResult")
                ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Workspace");
        if (ws is null)
        {
            var raw = doc.ToString();
            throw new SoapFaultException("CreateWorkspace", System.Net.HttpStatusCode.OK,
                $"CreateWorkspace response did not contain a workspace element. Raw response (first 800 chars): {raw[..Math.Min(800, raw.Length)]}", raw);
        }

        return new SoapWorkspace
        {
            Name = AttrAny(ws, "name") ?? name,
            Owner = AttrAny(ws, "owner") ?? ownerName ?? string.Empty,
            OwnerDisplay = AttrAny(ws, "ownerdisp"),
            Computer = AttrAny(ws, "computer") ?? computer,
            Comment = ChildText(ws, "Comment") ?? comment ?? string.Empty,
            Location = AttrAny(ws, "location")
                ?? (string.Equals(AttrAny(ws, "islocal"), "false", StringComparison.OrdinalIgnoreCase) ? "Server" : null),
        };
    }

    /// <summary>删除 workspace，幂等（不存在时抛出 SoapFaultException 由调用方决定是否忽略）。</summary>
    public async Task DeleteWorkspaceAsync(
        string workspaceName,
        string ownerName,
        CancellationToken ct = default)
    {
        var body = $@"    <tns:DeleteWorkspace>
      <tns:workspaceName>{Esc(workspaceName)}</tns:workspaceName>
      <tns:ownerName>{Esc(ownerName)}</tns:ownerName>
    </tns:DeleteWorkspace>";

        await InvokeAsync("DeleteWorkspace", body, ct).ConfigureAwait(false);
    }

    // ─── PendMerge / CheckIn 核心流程 ─────────────────────────────────────────

    /// <summary>
    /// 在指定 workspace 中暂挂一次合并操作。服务器返回每个受影响文件的 GetOperation。
    /// </summary>
    /// <param name="workspaceName">服务端 workspace 名（先 CreateWorkspaceAsync）</param>
    /// <param name="ownerName">workspace owner</param>
    /// <param name="sourcePath">源分支服务器路径</param>
    /// <param name="targetPath">目标分支服务器路径</param>
    /// <param name="fromChangeset">起始 changeset；null 表示从最早开始</param>
    /// <param name="toChangeset">结束 changeset；null 表示到最新</param>
    public async Task<PendMergeResult> PendMergeAsync(
        string workspaceName,
        string ownerName,
        string sourcePath,
        string targetPath,
        int? fromChangeset,
        int? toChangeset,
        CancellationToken ct = default)
    {
        // ChangesetVersionSpec.cs is an ATTRIBUTE (not a child element), per the Repository.asmx WSDL.
        var fromEl = fromChangeset.HasValue
            ? $@"<tns:from xsi:type=""tns:ChangesetVersionSpec"" cs=""{fromChangeset.Value}"" />"
            : @"<tns:from xsi:nil=""true"" />";
        var toEl = toChangeset.HasValue
            ? $@"<tns:to xsi:type=""tns:ChangesetVersionSpec"" cs=""{toChangeset.Value}"" />"
            : @"<tns:to xsi:type=""tns:LatestVersionSpec"" />";

        // Per WSDL: source/target are ItemSpec (complexType with item/recurse/did attributes),
        // NOT plain strings. optionsEx (int, minOccurs=1) is required and was previously omitted
        // (TFS returned "项不能为 null 或空").
        var body = $@"    <tns:Merge>
      <tns:workspaceName>{Esc(workspaceName)}</tns:workspaceName>
      <tns:workspaceOwner>{Esc(ownerName)}</tns:workspaceOwner>
      <tns:source item=""{Esc(sourcePath)}"" recurse=""Full"" did=""0"" />
      <tns:target item=""{Esc(targetPath)}"" recurse=""Full"" did=""0"" />
      {fromEl}
      {toEl}
      <tns:options>None</tns:options>
      <tns:lockLevel>Unchanged</tns:lockLevel>
      <tns:optionsEx>0</tns:optionsEx>
    </tns:Merge>";

        var doc = await InvokeAsync("Merge", body, ct).ConfigureAwait(false);

        var ops = ParseGetOperations(doc.Descendants().Where(e => e.Name.LocalName == "GetOperation"));

        // The server's 3-way merge may pend CONFLICTS instead of operations when both sides changed
        // the same file. These appear under <conflicts><Conflict .../> (ysitem = target/your side,
        // tsitem = source/their side). Only UNRESOLVED conflicts (isresolved="false") block the
        // check-in — resolved ones (isresolved="true", e.g. auto-resolved branch relationships) are
        // informational and are ignored. Unresolved conflicts must be resolved before check-in; never
        // silently take one side.
        var conflicts = ParseConflicts(
            doc.Descendants().Where(e => e.Name.LocalName == "Conflict"),
            unresolvedOnly: true);

        return new PendMergeResult { Operations = ops, Conflicts = conflicts };
    }

    /// <summary>查询 workspace 中尚未解决的冲突。</summary>
    public async Task<IReadOnlyList<SoapMergeConflict>> QueryConflictsAsync(
        string workspaceName,
        string ownerName,
        IReadOnlyList<string>? serverItems = null,
        CancellationToken ct = default)
    {
        var itemsXml = (serverItems is null || serverItems.Count == 0)
            ? "<tns:items />"
            : "<tns:items>" + string.Concat(serverItems.Select(item =>
                $@"<tns:ItemSpec item=""{Esc(item)}"" recurse=""Full"" did=""0"" />")) + "</tns:items>";

        var body = $@"    <tns:QueryConflicts>
      <tns:workspaceName>{Esc(workspaceName)}</tns:workspaceName>
      <tns:ownerName>{Esc(ownerName)}</tns:ownerName>
      {itemsXml}
    </tns:QueryConflicts>";

        var doc = await InvokeAsync("QueryConflicts", body, ct).ConfigureAwait(false);
        return ParseConflicts(
            doc.Descendants().Where(e => e.Name.LocalName == "Conflict"),
            unresolvedOnly: true);
    }

    /// <summary>
    /// 解决一个已存在的 workspace 冲突。对应 Repository.asmx 的 <c>Resolve</c> 方法。
    /// resolution 应为 <c>AcceptTheirs</c>、<c>AcceptYours</c> 或 <c>AcceptMerge</c>。
    /// </summary>
    public async Task<ResolveConflictResult> ResolveConflictAsync(
        string workspaceName,
        string ownerName,
        int conflictId,
        string resolution,
        string? newPath = null,
        int encoding = -2,
        string lockLevel = "Unchanged",
        CancellationToken ct = default)
    {
        if (conflictId <= 0)
            throw new ArgumentOutOfRangeException(nameof(conflictId), "Conflict ID must be positive.");

        var normalizedResolution = NormalizeSoapResolution(resolution);
        var normalizedLockLevel = NormalizeSoapLockLevel(lockLevel);
        var newPathEl = string.IsNullOrWhiteSpace(newPath)
            ? @"<tns:newPath xsi:nil=""true"" />"
            : $"<tns:newPath>{Esc(newPath)}</tns:newPath>";

        // Signature from Repository.asmx / TEE:
        // Resolve(workspaceName, ownerName, conflictId, resolution, newPath, encoding, lockLevel)
        // encoding=-2 means VersionControlConstants.ENCODING_UNCHANGED.
        var body = $@"    <tns:Resolve>
      <tns:workspaceName>{Esc(workspaceName)}</tns:workspaceName>
      <tns:ownerName>{Esc(ownerName)}</tns:ownerName>
      <tns:conflictId>{conflictId}</tns:conflictId>
      <tns:resolution>{Esc(normalizedResolution)}</tns:resolution>
      {newPathEl}
      <tns:encoding>{encoding}</tns:encoding>
      <tns:lockLevel>{Esc(normalizedLockLevel)}</tns:lockLevel>
    </tns:Resolve>";

        var doc = await InvokeAsync("Resolve", body, ct).ConfigureAwait(false);
        var resolveResult = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ResolveResult");
        var undoOperations = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "undoOperations");
        var resolvedConflicts = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "resolvedConflicts");

        return new ResolveConflictResult
        {
            Operations = ParseGetOperations((resolveResult?.Elements() ?? Enumerable.Empty<XElement>())
                .Where(e => e.Name.LocalName == "GetOperation")),
            UndoOperations = ParseGetOperations((undoOperations?.Elements() ?? Enumerable.Empty<XElement>())
                .Where(e => e.Name.LocalName == "GetOperation")),
            ResolvedConflicts = ParseConflicts((resolvedConflicts?.Elements() ?? Enumerable.Empty<XElement>())
                .Where(e => e.Name.LocalName == "Conflict"), unresolvedOnly: false),
        };
    }

    private static IReadOnlyList<SoapMergeOperation> ParseGetOperations(IEnumerable<XElement> operations)
    {
        return operations.Select(op => new SoapMergeOperation
        {
            ItemId = TryParseInt(AttrAny(op, "itemid")) ?? 0,
            SourceServerItem = AttrAny(op, "sitem") ?? AttrAny(op, "srcitem") ?? string.Empty,
            TargetServerItem = AttrAny(op, "titem")
                               ?? AttrAny(op, "targetitem")
                               ?? AttrAny(op, "sitem")
                               ?? string.Empty,
            // GetOperation exposes the pending change type as the `chg` attribute (ChangeType list).
            ChangeType = AttrAny(op, "chg") ?? AttrAny(op, "ct") ?? string.Empty,
            VersionFrom = TryParseInt(AttrAny(op, "vrevto"))
                          ?? TryParseInt(AttrAny(op, "mvfrom")),
            VersionTo = TryParseInt(AttrAny(op, "sver"))
                        ?? TryParseInt(AttrAny(op, "mvto")),
            IsPending = true,
        }).ToList();
    }

    private static IReadOnlyList<SoapMergeConflict> ParseConflicts(IEnumerable<XElement> conflicts, bool unresolvedOnly)
    {
        var result = new List<SoapMergeConflict>();
        foreach (var c in conflicts)
        {
            var isResolved = string.Equals(AttrAny(c, "isresolved"), "true", StringComparison.OrdinalIgnoreCase);
            if (unresolvedOnly && isResolved)
                continue;

            result.Add(new SoapMergeConflict
            {
                ConflictId = TryParseInt(AttrAny(c, "cid")) ?? 0,
                TargetServerItem = AttrAny(c, "ysitem") ?? AttrAny(c, "tgtitem") ?? string.Empty,
                SourceServerItem = AttrAny(c, "tsitem") ?? AttrAny(c, "srcitem") ?? string.Empty,
                ConflictType = AttrAny(c, "ctype") ?? string.Empty,
                BaseChangeType = AttrAny(c, "bchg") ?? string.Empty,
            });
        }

        return result;
    }

    private static string NormalizeSoapResolution(string resolution)
    {
        if (string.Equals(resolution, "AcceptTheirs", StringComparison.OrdinalIgnoreCase))
            return "AcceptTheirs";
        if (string.Equals(resolution, "AcceptYours", StringComparison.OrdinalIgnoreCase))
            return "AcceptYours";
        if (string.Equals(resolution, "AcceptMerge", StringComparison.OrdinalIgnoreCase))
            return "AcceptMerge";

        throw new ArgumentException($"Unsupported SOAP conflict resolution: {resolution}", nameof(resolution));
    }

    private static string NormalizeSoapLockLevel(string lockLevel)
    {
        if (string.Equals(lockLevel, "None", StringComparison.OrdinalIgnoreCase))
            return "None";
        if (string.Equals(lockLevel, "Checkin", StringComparison.OrdinalIgnoreCase))
            return "Checkin";
        if (string.Equals(lockLevel, "CheckOut", StringComparison.OrdinalIgnoreCase))
            return "CheckOut";
        if (string.Equals(lockLevel, "Unchanged", StringComparison.OrdinalIgnoreCase))
            return "Unchanged";

        throw new ArgumentException($"Unsupported SOAP lock level: {lockLevel}", nameof(lockLevel));
    }

    /// <summary>
    /// 提交带 merge 元数据的 changeset。服务器据此写入合并历史，<c>tf merges</c> 与 TFS Web UI 可见。
    /// </summary>
    /// <returns>新创建的 changeset ID</returns>
    public async Task<int> CheckInAsync(
        string workspaceName,
        string ownerName,
        string comment,
        IReadOnlyList<SoapPendingChange> changes,
        CancellationToken ct = default)
    {
        if (changes.Count == 0)
            throw new ArgumentException("CheckIn requires at least one pending change.", nameof(changes));

        // Per WSDL: info.Changes is ArrayOfChange. Each <Change> has a required `type` (ChangeType)
        // and `typeEx` (int) attribute, an <Item> child (server path, with required `date` attr), and
        // a <MergeSources> child whose <MergeSource> entries (s/vf/vt attributes) are what make TFS
        // record real merge history. The CheckIn method also requires checkinNotificationInfo,
        // checkinOptions, deferCheckIn and checkInTicket (all minOccurs=1).
        var changesXml = new StringBuilder();
        foreach (var change in changes)
        {
            var mergeSourcesXml = string.Empty;
            if (!string.IsNullOrEmpty(change.SourceServerItem) && change.VersionTo.HasValue)
            {
                var verFrom = change.VersionFrom ?? change.VersionTo.Value;
                mergeSourcesXml = $@"<tns:MergeSources><tns:MergeSource s=""{Esc(change.SourceServerItem)}"" vf=""{verFrom}"" vt=""{change.VersionTo.Value}"" /></tns:MergeSources>";
            }

            var chg = string.IsNullOrEmpty(change.ChangeType) ? "Merge" : change.ChangeType;
            changesXml.AppendLine(
                $@"          <tns:Change type=""{Esc(chg)}"" typeEx=""0""><tns:Item itemid=""{change.ItemId}"" item=""{Esc(change.ServerItem)}"" type=""File"" date=""0001-01-01T00:00:00"" />{mergeSourcesXml}</tns:Change>");
        }

        var body = $@"    <tns:CheckIn>
      <tns:workspaceName>{Esc(workspaceName)}</tns:workspaceName>
      <tns:ownerName>{Esc(ownerName)}</tns:ownerName>
      <tns:serverItems>
{string.Concat(changes.Select(c => $"        <tns:string>{Esc(c.ServerItem)}</tns:string>\n"))}      </tns:serverItems>
      <tns:info date=""0001-01-01T00:00:00"" cset=""0"">
        <tns:Comment>{Esc(comment)}</tns:Comment>
        <tns:Changes>
{changesXml}        </tns:Changes>
      </tns:info>
      <tns:checkinNotificationInfo />
      <tns:checkinOptions>None</tns:checkinOptions>
      <tns:deferCheckIn>false</tns:deferCheckIn>
      <tns:checkInTicket>0</tns:checkInTicket>
    </tns:CheckIn>";

        var doc = await InvokeAsync("CheckIn", body, ct).ConfigureAwait(false);

        // Response: <CheckInResult cset="N" ... />
        var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CheckInResult")
                  ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Changeset");
        var csetAttr = AttrAny(result, "cset") ?? AttrAny(result, "Cset");
        if (string.IsNullOrEmpty(csetAttr) || !int.TryParse(csetAttr, out var changesetId))
        {
            var match = System.Text.RegularExpressions.Regex.Match(doc.ToString(), @"cset=""(\d+)""");
            if (!match.Success)
                throw new SoapFaultException("CheckIn", System.Net.HttpStatusCode.OK,
                    "CheckIn response did not contain cset attribute", doc.ToString());
            changesetId = int.Parse(match.Groups[1].Value);
        }

        return changesetId;
    }

    // ─── PendChanges / Lock / Shelve ─────────────────────────────────────────

    /// <summary>
    /// 对指定路径施加/解除锁定（tf lock）。
    /// lockLevel: CheckOut（独占锁）、None（解锁）、Checkin（签入时锁）。
    /// 返回影响的操作数。
    /// </summary>
    public async Task<int> PendLockAsync(
        string workspaceName,
        string ownerName,
        string serverPath,
        string lockLevel = "CheckOut",
        CancellationToken ct = default)
    {
        var body = $@"    <tns:PendChanges>
      <tns:workspaceName>{Esc(workspaceName)}</tns:workspaceName>
      <tns:ownerName>{Esc(ownerName)}</tns:ownerName>
      <tns:changes>
        <tns:ChangeRequest req=""Lock"" lock=""{Esc(lockLevel)}"">
          <tns:item item=""{Esc(serverPath)}"" recurse=""None"" did=""0"" />
          <tns:vspec xsi:type=""tns:LatestVersionSpec"" />
        </tns:ChangeRequest>
      </tns:changes>
      <tns:pendChangesOptions>0</tns:pendChangesOptions>
      <tns:supportedFeatures>0</tns:supportedFeatures>
    </tns:PendChanges>";

        var doc = await InvokeAsync("PendChanges", body, ct).ConfigureAwait(false);
        var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "PendChangesResult");
        var countAttr = AttrAny(result, "count") ?? AttrAny(result, "Count");
        return int.TryParse(countAttr, out var n) ? n : 0;
    }

    /// <summary>
    /// Shelve：将 workspace 中的挂起变更保存为 shelveset，不签入到服务器历史。
    /// replace=true 时覆盖同名 shelveset。
    /// </summary>
    public async Task ShelveAsync(
        string workspaceName,
        string ownerName,
        string shelvesetName,
        IReadOnlyList<Models.Soap.SoapShelveChange> changes,
        string comment,
        bool replace = false,
        CancellationToken ct = default)
    {
        // First pend the changes in the workspace so the server knows what to shelve
        var changesXml = new System.Text.StringBuilder();
        foreach (var c in changes)
        {
            var verEl = c.BaseVersion.HasValue
                ? $@"<tns:vspec xsi:type=""tns:ChangesetVersionSpec"" cs=""{c.BaseVersion.Value}"" />"
                : @"<tns:vspec xsi:type=""tns:LatestVersionSpec"" />";
            changesXml.AppendLine($@"        <tns:ChangeRequest req=""{Esc(c.ChangeTypeStr)}"" lock=""Unchanged"" enc=""65001"" type=""File"">
          <tns:item item=""{Esc(c.ServerPath)}"" recurse=""None"" did=""0"" />
          {verEl}
        </tns:ChangeRequest>");
        }

        var pendBody = $@"    <tns:PendChanges>
      <tns:workspaceName>{Esc(workspaceName)}</tns:workspaceName>
      <tns:ownerName>{Esc(ownerName)}</tns:ownerName>
      <tns:changes>
{changesXml}      </tns:changes>
      <tns:pendChangesOptions>0</tns:pendChangesOptions>
      <tns:supportedFeatures>0</tns:supportedFeatures>
    </tns:PendChanges>";

        await InvokeAsync("PendChanges", pendBody, ct).ConfigureAwait(false);

        // Now shelve
        var serverItemsXml = string.Concat(changes.Select(c => $"        <tns:string>{Esc(c.ServerPath)}</tns:string>\n"));
        var shelveBody = $@"    <tns:Shelve>
      <tns:workspaceName>{Esc(workspaceName)}</tns:workspaceName>
      <tns:workspaceOwner>{Esc(ownerName)}</tns:workspaceOwner>
      <tns:serverItems>
{serverItemsXml}      </tns:serverItems>
      <tns:shelveset name=""{Esc(shelvesetName)}"" owner=""{Esc(ownerName)}"" date=""0001-01-01T00:00:00"">
        <tns:Comment>{Esc(comment)}</tns:Comment>
      </tns:shelveset>
      <tns:replace>{(replace ? "true" : "false")}</tns:replace>
      <tns:move>false</tns:move>
    </tns:Shelve>";

        await InvokeAsync("Shelve", shelveBody, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除指定 shelveset（tf shelve /delete）。
    /// </summary>
    public async Task DeleteShelvesetAsync(
        string shelvesetName,
        string ownerName,
        CancellationToken ct = default)
    {
        var body = $@"    <tns:DeleteShelveset>
      <tns:shelvesetName>{Esc(shelvesetName)}</tns:shelvesetName>
      <tns:ownerName>{Esc(ownerName)}</tns:ownerName>
    </tns:DeleteShelveset>";

        await InvokeAsync("DeleteShelveset", body, ct).ConfigureAwait(false);
    }

    // ─── Label ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 创建（或更新）TFVC Label，并将指定路径附加到该 label。
    /// TFS REST API 不支持 label 创建，必须使用 SOAP LabelItem。
    /// </summary>
    public async Task<string> LabelItemAsync(
        string labelName,
        string serverPath,
        string? comment = null,
        int? atChangeset = null,
        string? scope = null,
        CancellationToken ct = default)
    {
        var versionEl = atChangeset.HasValue
            ? $@"<tns:Version xsi:type=""tns:ChangesetVersionSpec"" cs=""{atChangeset.Value}"" />"
            : @"<tns:Version xsi:type=""tns:LatestVersionSpec"" />";

        // VersionControlLabel requires date attribute; label/scope are attributes on the label element.
        // labelSpecs is a sibling element (not nested inside label).
        var body = $@"    <tns:LabelItem>
      <tns:label name=""{Esc(labelName)}"" date=""0001-01-01T00:00:00"">
        <tns:Comment>{Esc(comment ?? string.Empty)}</tns:Comment>
      </tns:label>
      <tns:labelSpecs>
        <tns:LabelItemSpec>
          <tns:ItemSpec item=""{Esc(serverPath)}"" recurse=""Full"" did=""0"" />
          {versionEl}
        </tns:LabelItemSpec>
      </tns:labelSpecs>
      <tns:children>Replace</tns:children>
    </tns:LabelItem>";

        var doc = await InvokeAsync("LabelItem", body, ct).ConfigureAwait(false);

        // Check for failures
        var failures = doc.Descendants().Where(e => e.Name.LocalName == "Failure").ToList();
        if (failures.Count > 0)
        {
            var msg = failures.First().Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value
                   ?? failures.First().ToString();
            throw new SoapFaultException("LabelItem", System.Net.HttpStatusCode.OK, msg, doc.ToString());
        }

        return labelName;
    }

    /// <summary>
    /// 删除 TFVC Label（SOAP UnlabelItem）。
    /// </summary>
    public async Task DeleteLabelAsync(
        string labelName,
        string scope = "$/",
        CancellationToken ct = default)
    {
        var body = $@"    <tns:UnlabelItem>
      <tns:labelName>{Esc(labelName)}</tns:labelName>
      <tns:labelScope>{Esc(scope)}</tns:labelScope>
      <tns:items>
        <tns:ItemSpec item=""{Esc(scope)}"" recurse=""Full"" did=""0"" />
      </tns:items>
      <tns:version xsi:type=""tns:LatestVersionSpec"" />
    </tns:UnlabelItem>";

        var doc = await InvokeAsync("UnlabelItem", body, ct).ConfigureAwait(false);

        // Check for failures
        var failures = doc.Descendants().Where(e => e.Name.LocalName == "Failure").ToList();
        if (failures.Count > 0)
        {
            var msg = failures.First().Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value
                   ?? failures.First().ToString();
            throw new SoapFaultException("UnlabelItem", System.Net.HttpStatusCode.OK, msg, doc.ToString());
        }
    }

    /// <summary>
    /// 在 workspace 中 Pend 一个 Rename 操作，返回目标文件的 itemId（用于 CheckIn）。
    /// </summary>
    public async Task<int> PendRenameAsync(
        string workspaceName,
        string ownerName,
        string oldServerPath,
        string newServerPath,
        int baseVersion,
        CancellationToken ct = default)
    {
        var body = $@"    <tns:PendChanges>
      <tns:workspaceName>{Esc(workspaceName)}</tns:workspaceName>
      <tns:ownerName>{Esc(ownerName)}</tns:ownerName>
      <tns:changes>
        <tns:ChangeRequest req=""Rename"" target=""{Esc(newServerPath)}"" lock=""Unchanged"">
          <tns:item item=""{Esc(oldServerPath)}"" recurse=""None"" did=""0"" />
          <tns:vspec xsi:type=""tns:ChangesetVersionSpec"" cs=""{baseVersion}"" />
        </tns:ChangeRequest>
      </tns:changes>
      <tns:pendChangesOptions>0</tns:pendChangesOptions>
      <tns:supportedFeatures>0</tns:supportedFeatures>
    </tns:PendChanges>";

        var doc = await InvokeAsync("PendChanges", body, ct).ConfigureAwait(false);

        // Find the GetOperation for the renamed (new) path
        foreach (var op in doc.Descendants().Where(e => e.Name.LocalName == "GetOperation"))
        {
            var titem = AttrAny(op, "titem") ?? AttrAny(op, "targetitem") ?? AttrAny(op, "sitem") ?? string.Empty;
            if (string.Equals(titem, newServerPath, StringComparison.OrdinalIgnoreCase))
                return TryParseInt(AttrAny(op, "itemid")) ?? 0;
        }

        // Fallback: return any item id found
        foreach (var op in doc.Descendants().Where(e => e.Name.LocalName == "GetOperation"))
            return TryParseInt(AttrAny(op, "itemid")) ?? 0;

        return 0;
    }

    private static string? AttrAny(XElement? element, string localName)
    {
        if (element is null) return null;
        return element.Attributes()
            .FirstOrDefault(a => string.Equals(a.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }
}

/// <summary>SOAP 调用失败时抛出（HTTP 非 2xx，或响应含 SOAP Fault）。</summary>
public sealed class SoapFaultException : InvalidOperationException
{
    public string Method { get; }
    public System.Net.HttpStatusCode StatusCode { get; }
    public string FaultMessage { get; }
    public string RawResponse { get; }

    public SoapFaultException(string method, System.Net.HttpStatusCode statusCode, string faultMessage, string rawResponse)
        : base($"SOAP {method} failed ({(int)statusCode} {statusCode}): {faultMessage}")
    {
        Method = method;
        StatusCode = statusCode;
        FaultMessage = faultMessage;
        RawResponse = rawResponse;
    }
}
