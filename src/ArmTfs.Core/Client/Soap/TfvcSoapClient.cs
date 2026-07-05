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

    // ─── QueryLabels ───────────────────────────────────────────────────────────

    /// <summary>
    /// 查询 TFVC 标签列表（SOAP QueryLabels）。
    /// </summary>
    public async Task<IReadOnlyList<SoapLabel>> QueryLabelsAsync(
        string? labelName = null,
        string? labelScope = null,
        string? owner = null,
        CancellationToken ct = default)
    {
        var body = $@"    <tns:QueryLabels>
      <tns:workspaceName></tns:workspaceName>
      <tns:workspaceOwner></tns:workspaceOwner>
      <tns:labelName>{Esc(labelName ?? string.Empty)}</tns:labelName>
      <tns:labelScope>{Esc(labelScope ?? string.Empty)}</tns:labelScope>
      <tns:owner>{Esc(owner ?? string.Empty)}</tns:owner>
      <tns:filterItem xsi:nil=""true"" />
      <tns:sortBy>None</tns:sortBy>
      <tns:includeItems>false</tns:includeItems>
      <tns:generateDownloadUrls>false</tns:generateDownloadUrls>
    </tns:QueryLabels>";

        var doc = await InvokeAsync("QueryLabels", body, ct).ConfigureAwait(false);

        var result = new List<SoapLabel>();
        foreach (var label in doc.Descendants().Where(e => e.Name.LocalName == "VersionControlLabel"))
        {
            var name = AttrAny(label, "name") ?? string.Empty;
            var lid = TryParseInt(AttrAny(label, "lid")) ?? 0;
            var scope = AttrAny(label, "scope");
            var labelOwner = AttrAny(label, "owner");
            DateTimeOffset? date = DateTimeOffset.TryParse(AttrAny(label, "date"), out var d) ? d : null;
            var comment = ChildText(label, "Comment");

            result.Add(new SoapLabel(name, lid, scope, labelOwner, date, comment));
        }

        return result;
    }

    // ─── QueryShelvesets ──────────────────────────────────────────────────────

    /// <summary>
    /// 查询 Shelveset 列表（SOAP QueryShelvesets）。
    /// </summary>
    public async Task<IReadOnlyList<SoapShelveset>> QueryShelvesetsAsync(
        string? shelvesetName = null,
        string? ownerName = null,
        CancellationToken ct = default)
    {
        var nameEl = string.IsNullOrEmpty(shelvesetName)
            ? @"<tns:shelvesetName xsi:nil=""true"" />"
            : $"<tns:shelvesetName>{Esc(shelvesetName)}</tns:shelvesetName>";
        var ownerEl = string.IsNullOrEmpty(ownerName)
            ? @"<tns:ownerName xsi:nil=""true"" />"
            : $"<tns:ownerName>{Esc(ownerName)}</tns:ownerName>";

        var body = $@"    <tns:QueryShelvesets>
      {nameEl}
      {ownerEl}
    </tns:QueryShelvesets>";

        var doc = await InvokeAsync("QueryShelvesets", body, ct).ConfigureAwait(false);

        var result = new List<SoapShelveset>();
        foreach (var ss in doc.Descendants().Where(e => e.Name.LocalName == "Shelveset"))
        {
            var name = AttrAny(ss, "name") ?? string.Empty;
            var ssOwner = AttrAny(ss, "owner");
            var ssOwnerDisp = AttrAny(ss, "ownerdisp");
            DateTimeOffset? date = DateTimeOffset.TryParse(AttrAny(ss, "date"), out var d) ? d : null;
            var comment = ChildText(ss, "Comment");

            result.Add(new SoapShelveset(name, ssOwner, ssOwnerDisp, date, comment));
        }

        return result;
    }

    /// <summary>
    /// 查询 Shelveset 中的文件变更列表（SOAP QueryShelvesetChanges）。
    /// </summary>
    public async Task<IReadOnlyList<SoapShelvesetChange>> QueryShelvesetChangesAsync(
        string shelvesetName,
        string ownerName,
        CancellationToken ct = default)
    {
        var body = $@"    <tns:QueryShelvesetChanges>
      <tns:shelvesetName>{Esc(shelvesetName)}</tns:shelvesetName>
      <tns:ownerName>{Esc(ownerName)}</tns:ownerName>
      <tns:includeDownloadUrls>false</tns:includeDownloadUrls>
      <tns:pageSize>1000</tns:pageSize>
    </tns:QueryShelvesetChanges>";

        var doc = await InvokeAsync("QueryShelvesetChanges", body, ct).ConfigureAwait(false);

        var result = new List<SoapShelvesetChange>();
        foreach (var change in doc.Descendants().Where(e => e.Name.LocalName == "Change"))
        {
            var changeType = AttrAny(change, "type") ?? AttrAny(change, "chg") ?? string.Empty;
            var itemEl = change.Elements().FirstOrDefault(e => e.Name.LocalName == "Item");
            var serverPath = AttrAny(itemEl, "item") ?? string.Empty;
            var itemType = AttrAny(itemEl, "type") ?? string.Empty;
            var isFolder = string.Equals(itemType, "Folder", StringComparison.OrdinalIgnoreCase);
            var cs = TryParseInt(AttrAny(itemEl, "cs")) ?? 0;

            result.Add(new SoapShelvesetChange(serverPath, isFolder, cs, changeType));
        }

        return result;
    }

    // ─── QueryBranchObjects ──────────────────────────────────────────────────

    /// <summary>
    /// 查询 TFVC 分支对象信息（SOAP QueryBranches）。
    /// 返回指定路径的分支信息，包含父分支和子分支关系。
    /// </summary>
    public async Task<IReadOnlyList<SoapBranchObject>> QueryBranchObjectsAsync(
        string? serverPath = null,
        bool includeChildren = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(serverPath))
            return new List<SoapBranchObject>();

        // Use QueryBranches (supported on all TFS versions) instead of QueryBranchObjects
        var body = $@"    <tns:QueryBranches>
      <tns:workspaceName></tns:workspaceName>
      <tns:workspaceOwner></tns:workspaceOwner>
      <tns:items>
        <tns:ItemSpec item=""{Esc(serverPath)}"" recurse=""None"" />
      </tns:items>
    </tns:QueryBranches>";

        var doc = await InvokeAsync("QueryBranches", body, ct).ConfigureAwait(false);

        // Parse BranchRelative elements to build parent-child relationships
        var relatives = doc.Descendants()
            .Where(e => e.Name.LocalName == "BranchRelative")
            .ToList();

        // Find the requested item (reqstd=true)
        var requestedRel = relatives.FirstOrDefault(r =>
            string.Equals(AttrAny(r, "reqstd"), "true", StringComparison.OrdinalIgnoreCase));

        if (requestedRel is null && relatives.Count > 0)
            requestedRel = relatives.First();

        if (requestedRel is null)
            return new List<SoapBranchObject>();

        var requestedId = AttrAny(requestedRel, "reltoid") ?? "0";
        var branchToItem = requestedRel.Elements().FirstOrDefault(e => e.Name.LocalName == "BranchToItem");
        var requestedPath = AttrAny(branchToItem, "item") ?? serverPath;
        DateTimeOffset? dateCreated = DateTimeOffset.TryParse(AttrAny(branchToItem, "date"), out var dc) ? dc : null;

        // Find parent (the item that requested item branches FROM)
        var parentFromId = AttrAny(requestedRel, "relfromid") ?? "0";
        string? parentPath = null;
        if (parentFromId != "0")
        {
            var parentRel = relatives.FirstOrDefault(r => AttrAny(r, "reltoid") == parentFromId);
            if (parentRel != null)
            {
                var parentItem = parentRel.Elements().FirstOrDefault(e => e.Name.LocalName == "BranchToItem");
                parentPath = AttrAny(parentItem, "item");
            }
        }

        // Find children (items whose relfromid = requestedId)
        var childPaths = new List<string>();
        foreach (var rel in relatives)
        {
            if (AttrAny(rel, "relfromid") == requestedId && AttrAny(rel, "reltoid") != requestedId)
            {
                var childItem = rel.Elements().FirstOrDefault(e => e.Name.LocalName == "BranchToItem");
                var childPath = AttrAny(childItem, "item");
                if (!string.IsNullOrEmpty(childPath))
                    childPaths.Add(childPath);
            }
        }

        var result = new SoapBranchObject(
            requestedPath, null, dateCreated, parentPath, false, null,
            childPaths.Count > 0 ? childPaths : null);

        return new List<SoapBranchObject> { result };
    }

    // ─── PendChanges (General) + Upload + CheckInWithContent ─────────────────

    /// <summary>
    /// 在 workspace 中 pend 一组变更（Edit/Add/Delete/Undelete），返回每个操作的 GetOperation 信息。
    /// 这是 PendChanges 的通用版本，支持批量 ChangeRequest。
    /// </summary>
    public async Task<IReadOnlyList<SoapPendChangeOperation>> PendChangesAsync(
        string workspaceName,
        string ownerName,
        IReadOnlyList<SoapChangeRequest> changeRequests,
        CancellationToken ct = default)
    {
        if (changeRequests.Count == 0)
            throw new ArgumentException("At least one change request is required.", nameof(changeRequests));

        var changesXml = new StringBuilder();
        foreach (var req in changeRequests)
        {
            var targetAttr = string.IsNullOrEmpty(req.TargetServerPath) ? "" : $@" target=""{Esc(req.TargetServerPath)}""";
            var encAttr = req.Encoding.HasValue ? $@" enc=""{req.Encoding.Value}""" : "";
            var typeAttr = string.IsNullOrEmpty(req.ItemType) ? "" : $@" type=""{Esc(req.ItemType)}""";
            var vspecEl = req.BaseVersion.HasValue
                ? $@"<tns:vspec xsi:type=""tns:ChangesetVersionSpec"" cs=""{req.BaseVersion.Value}"" />"
                : @"<tns:vspec xsi:type=""tns:LatestVersionSpec"" />";

            changesXml.AppendLine($@"        <tns:ChangeRequest req=""{Esc(req.RequestType)}"" lock=""Unchanged""{targetAttr}{encAttr}{typeAttr}>
          <tns:item item=""{Esc(req.ServerPath)}"" recurse=""None"" did=""0"" />
          {vspecEl}
        </tns:ChangeRequest>");
        }

        var body = $@"    <tns:PendChanges>
      <tns:workspaceName>{Esc(workspaceName)}</tns:workspaceName>
      <tns:ownerName>{Esc(ownerName)}</tns:ownerName>
      <tns:changes>
{changesXml}      </tns:changes>
      <tns:pendChangesOptions>0</tns:pendChangesOptions>
      <tns:supportedFeatures>0</tns:supportedFeatures>
    </tns:PendChanges>";

        var doc = await InvokeAsync("PendChanges", body, ct).ConfigureAwait(false);

        var result = new List<SoapPendChangeOperation>();
        foreach (var op in doc.Descendants().Where(e => e.Name.LocalName == "GetOperation"))
        {
            result.Add(new SoapPendChangeOperation
            {
                ItemId = TryParseInt(AttrAny(op, "itemid")) ?? 0,
                ServerItem = AttrAny(op, "titem") ?? AttrAny(op, "sitem") ?? string.Empty,
                ChangeType = AttrAny(op, "chg") ?? string.Empty,
            });
        }

        return result;
    }

    /// <summary>
    /// 上传文件内容到 workspace 中的挂起变更。使用 TFS 的 upload.ashx 端点。
    /// 对于 Edit/Add 操作，在 PendChanges 之后、CheckIn 之前调用。
    /// </summary>
    public async Task UploadFileToWorkspaceAsync(
        string workspaceName,
        string ownerName,
        string serverPath,
        byte[] content,
        CancellationToken ct = default)
    {
        // TFS upload endpoint: {collection}/VersionControl/v1.0/upload.ashx
        // This uploads content as a chunked multipart/form-data POST
        var uploadUrl = _connection.ServerUrl.TrimEnd('/') + "/VersionControl/v1.0/upload.ashx";

        using var httpClient = _connection.CreateHttpClient();
        using var formContent = new MultipartFormDataContent();

        // The upload requires workspace context and content metadata
        formContent.Add(new StringContent(content.Length.ToString()), "filelength");
        formContent.Add(new ByteArrayContent(content), "content", "file");

        // Build the upload URL with workspace and path query parameters
        var queryUrl = $"{uploadUrl}?item={Uri.EscapeDataString(serverPath)}&wsname={Uri.EscapeDataString(workspaceName)}&wsowner={Uri.EscapeDataString(ownerName)}";

        using var response = await httpClient.PostAsync(queryUrl, formContent, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new SoapFaultException("UploadFile", response.StatusCode,
                $"Upload failed for {serverPath}: {body[..Math.Min(500, body.Length)]}", body);
        }
    }

    /// <summary>
    /// 完整的 SOAP 签入流程：CreateWorkspace → PendChanges → Upload content → CheckIn → DeleteWorkspace。
    /// 替代 REST CreateChangesetAsync，完全通过 SOAP/TFS 旧版端点实现。
    /// </summary>
    /// <param name="changes">每个变更包含: 服务器路径、变更类型(Edit/Add/Delete/Undelete)、文件内容(Delete 时为 null)、基线版本</param>
    /// <param name="comment">Changeset 注释</param>
    /// <param name="ownerName">Workspace owner（认证用户 GUID）</param>
    /// <returns>新创建的 changeset ID</returns>
    public async Task<int> CheckInWithContentAsync(
        string comment,
        string ownerName,
        IReadOnlyList<SoapContentChange> changes,
        CancellationToken ct = default)
    {
        if (changes.Count == 0)
            throw new ArgumentException("At least one change is required.", nameof(changes));

        var workspaceName = $"arm-tfs-soap-checkin-{Guid.NewGuid():N}";
        var computer = Environment.MachineName;

        // Build working folder mappings: map each unique project root
        var roots = changes
            .Select(c => GetProjectRoot(c.ServerPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var tempRoot = Path.Combine(Path.GetTempPath(), "arm-tfs-checkin", workspaceName);
        var folders = roots.Select((r, i) => (r, Path.Combine(tempRoot, $"m{i}"))).ToArray();

        SoapWorkspace? createdWs = null;
        string effectiveOwner = ownerName;

        try
        {
            // 1. Create workspace
            var ws = await CreateWorkspaceAsync(workspaceName, ownerName, computer, "arm-tfs SOAP checkin", folders, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ws.Owner)) effectiveOwner = ws.Owner;
            createdWs = ws;

            // 2. PendChanges for all items
            var changeRequests = changes.Select(c => new SoapChangeRequest
            {
                RequestType = c.ChangeType,
                ServerPath = c.ServerPath,
                BaseVersion = c.BaseVersion,
                Encoding = c.Content is not null ? 65001 : null,
                ItemType = "File",
                TargetServerPath = c.TargetServerPath,
            }).ToList();

            var pendOps = await PendChangesAsync(workspaceName, effectiveOwner, changeRequests, ct).ConfigureAwait(false);

            // 3. Upload content for Edit/Add operations
            foreach (var change in changes)
            {
                if (change.Content is null) continue;
                if (string.Equals(change.ChangeType, "Delete", StringComparison.OrdinalIgnoreCase)) continue;

                await UploadFileToWorkspaceAsync(workspaceName, effectiveOwner, change.ServerPath, change.Content, ct).ConfigureAwait(false);
            }

            // 4. CheckIn — build pending changes from the PendChanges result
            var pendingChanges = new List<SoapPendingChange>();
            foreach (var change in changes)
            {
                var matchOp = pendOps.FirstOrDefault(op =>
                    string.Equals(op.ServerItem, change.ServerPath, StringComparison.OrdinalIgnoreCase));
                pendingChanges.Add(new SoapPendingChange
                {
                    ItemId = matchOp?.ItemId ?? 0,
                    ServerItem = change.ServerPath,
                    ChangeType = change.ChangeType,
                });
            }

            var changesetId = await CheckInAsync(workspaceName, effectiveOwner, comment, pendingChanges, ct).ConfigureAwait(false);
            return changesetId;
        }
        finally
        {
            if (createdWs is not null)
            {
                try { await DeleteWorkspaceAsync(workspaceName, effectiveOwner, ct).ConfigureAwait(false); }
                catch (Exception ex) { Console.Error.WriteLine($"warning: checkin workspace cleanup failed: {ex.Message}"); }
            }
        }
    }

    private static string GetProjectRoot(string serverPath)
    {
        var parts = serverPath.TrimStart('$', '/').Split('/');
        return parts.Length >= 1 ? $"$/{parts[0]}" : serverPath;
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

    // ─── QueryItems / QueryHistory / DownloadFile ────────────────────────────

    /// <summary>
    /// 查询 TFVC 项列表（文件和文件夹）。等价于 <c>tf dir</c> / REST <c>GET /items</c>。
    /// </summary>
    /// <param name="serverPath">服务器路径，例如 <c>$/Project/Main</c></param>
    /// <param name="recursion">递归级别：Full（全递归）、OneLevel（一层）、None（仅自身）</param>
    /// <param name="atChangeset">查询指定 changeset 版本；null 则查询最新版本</param>
    public async Task<IReadOnlyList<SoapItem>> QueryItemsAsync(
        string serverPath,
        string recursion = "Full",
        int? atChangeset = null,
        CancellationToken ct = default)
    {
        var versionEl = atChangeset.HasValue
            ? $@"<tns:version xsi:type=""tns:ChangesetVersionSpec"" cs=""{atChangeset.Value}"" />"
            : @"<tns:version xsi:type=""tns:LatestVersionSpec"" />";

        var body = $@"    <tns:QueryItems>
      <tns:workspaceName></tns:workspaceName>
      <tns:workspaceOwner></tns:workspaceOwner>
      <tns:items>
        <tns:ItemSpec item=""{Esc(serverPath)}"" recurse=""{Esc(recursion)}"" />
      </tns:items>
      {versionEl}
      <tns:deletedState>NonDeleted</tns:deletedState>
      <tns:itemType>Any</tns:itemType>
      <tns:generateDownloadUrls>true</tns:generateDownloadUrls>
      <tns:options>0</tns:options>
    </tns:QueryItems>";

        var doc = await InvokeAsync("QueryItems", body, ct).ConfigureAwait(false);

        var result = new List<SoapItem>();
        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "Item"))
        {
            var itemPath = AttrAny(item, "item") ?? string.Empty;
            var type = AttrAny(item, "type") ?? string.Empty;
            var cs = TryParseInt(AttrAny(item, "cs")) ?? 0;
            _ = long.TryParse(AttrAny(item, "len"), out var len);
            var hash = AttrAny(item, "hash");
            var durl = AttrAny(item, "durl");
            DateTimeOffset? date = DateTimeOffset.TryParse(AttrAny(item, "date"), out var d) ? d : null;

            result.Add(new SoapItem(itemPath, string.Equals(type, "Folder", StringComparison.OrdinalIgnoreCase), cs, len, hash, durl, date));
        }

        return result;
    }

    /// <summary>
    /// 查询 TFVC 变更历史。等价于 <c>tf history</c> / REST <c>GET /changesets</c>。
    /// </summary>
    /// <param name="serverPath">服务器路径</param>
    /// <param name="maxCount">最大返回条数</param>
    /// <param name="includeFiles">是否包含每个 changeset 的文件变更列表</param>
    public async Task<IReadOnlyList<SoapChangeset>> QueryHistoryAsync(
        string serverPath,
        int maxCount = 100,
        bool includeFiles = false,
        CancellationToken ct = default)
    {
        var body = $@"    <tns:QueryHistory>
      <tns:workspaceName></tns:workspaceName>
      <tns:workspaceOwner></tns:workspaceOwner>
      <tns:itemSpec item=""{Esc(serverPath)}"" recurse=""Full"" />
      <tns:versionItem xsi:type=""tns:LatestVersionSpec"" />
      <tns:versionFrom xsi:type=""tns:ChangesetVersionSpec"" cs=""1"" />
      <tns:versionTo xsi:type=""tns:LatestVersionSpec"" />
      <tns:maxCount>{maxCount}</tns:maxCount>
      <tns:includeFiles>{(includeFiles ? "true" : "false")}</tns:includeFiles>
      <tns:generateDownloadUrls>false</tns:generateDownloadUrls>
      <tns:slotMode>false</tns:slotMode>
      <tns:sortAscending>false</tns:sortAscending>
    </tns:QueryHistory>";

        var doc = await InvokeAsync("QueryHistory", body, ct).ConfigureAwait(false);

        var result = new List<SoapChangeset>();
        foreach (var cs in doc.Descendants().Where(e => e.Name.LocalName == "Changeset"))
        {
            var csetId = TryParseInt(AttrAny(cs, "cset")) ?? 0;
            var author = AttrAny(cs, "cmtr");
            var authorUnique = AttrAny(cs, "cmtru");
            DateTimeOffset? date = DateTimeOffset.TryParse(AttrAny(cs, "date"), out var d) ? d : null;
            var comment = ChildText(cs, "Comment");

            result.Add(new SoapChangeset(csetId, author, authorUnique, date, comment));
        }

        return result;
    }

    // ─── QueryChangeset / QueryChangesForChangeset ─────────────────────────────

    /// <summary>
    /// 通过 SOAP QueryChangeset 获取单个 changeset 的元数据（作者、日期、注释）。
    /// 不包含文件变更列表（用 <see cref="QueryChangesForChangesetAsync"/> 获取）。
    /// </summary>
    public async Task<SoapChangeset> QueryChangesetMetadataAsync(
        int changesetId,
        CancellationToken ct = default)
    {
        var body = $@"    <tns:QueryChangeset>
      <tns:changesetId>{changesetId}</tns:changesetId>
      <tns:includeChanges>false</tns:includeChanges>
      <tns:generateDownloadUrls>false</tns:generateDownloadUrls>
      <tns:includeSourceRenames>false</tns:includeSourceRenames>
    </tns:QueryChangeset>";

        var doc = await InvokeAsync("QueryChangeset", body, ct).ConfigureAwait(false);

        // Response: <QueryChangesetResult cset="N" cmtr="Author" cmtru="unique" date="...">
        //             <Comment>text</Comment>
        //           </QueryChangesetResult>
        var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "QueryChangesetResult")
                  ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Changeset");

        if (result is null)
            throw new SoapFaultException("QueryChangeset", System.Net.HttpStatusCode.OK,
                "QueryChangeset response did not contain a result element", doc.ToString());

        var cset = TryParseInt(AttrAny(result, "cset")) ?? changesetId;
        var author = AttrAny(result, "cmtr");
        var authorUnique = AttrAny(result, "cmtru");
        DateTimeOffset? date = DateTimeOffset.TryParse(AttrAny(result, "date"), out var d) ? d : null;
        var comment = ChildText(result, "Comment");

        return new SoapChangeset(cset, author, authorUnique, date, comment);
    }

    /// <summary>
    /// 通过 SOAP QueryChangesForChangeset 获取指定 changeset 的全部文件变更（含 MergeSources）。
    /// 支持分页（pageSize=1000），自动获取所有页面。
    /// </summary>
    public async Task<IReadOnlyList<SoapChangesetChange>> QueryChangesForChangesetAsync(
        int changesetId,
        CancellationToken ct = default)
    {
        var allChanges = new List<SoapChangesetChange>();
        string? lastItem = null;
        const int pageSize = 1000;

        while (true)
        {
            var lastItemEl = lastItem is null
                ? ""
                : $"<tns:lastItem>{Esc(lastItem)}</tns:lastItem>";

            var body = $@"    <tns:QueryChangesForChangeset>
      <tns:changesetId>{changesetId}</tns:changesetId>
      <tns:generateDownloadUrls>false</tns:generateDownloadUrls>
      <tns:pageSize>{pageSize}</tns:pageSize>
      {lastItemEl}
    </tns:QueryChangesForChangeset>";

            var doc = await InvokeAsync("QueryChangesForChangeset", body, ct).ConfigureAwait(false);

            var changeElements = doc.Descendants()
                .Where(e => e.Name.LocalName == "Change")
                .ToList();

            if (changeElements.Count == 0)
                break;

            foreach (var changeEl in changeElements)
            {
                var changeType = AttrAny(changeEl, "type") ?? AttrAny(changeEl, "chg") ?? string.Empty;

                // Parse <Item> child
                var itemEl = changeEl.Elements().FirstOrDefault(e => e.Name.LocalName == "Item");
                var serverPath = AttrAny(itemEl, "item") ?? string.Empty;
                var itemType = AttrAny(itemEl, "type") ?? string.Empty;
                var isFolder = string.Equals(itemType, "Folder", StringComparison.OrdinalIgnoreCase);
                var itemCs = TryParseInt(AttrAny(itemEl, "cs")) ?? changesetId;

                // Parse <MergeSources> child
                var mergeSources = new List<SoapMergeSourceInfo>();
                var mergeSourcesEl = changeEl.Elements().FirstOrDefault(e => e.Name.LocalName == "MergeSources");
                if (mergeSourcesEl is not null)
                {
                    foreach (var ms in mergeSourcesEl.Elements().Where(e => e.Name.LocalName == "MergeSource"))
                    {
                        var sid = AttrAny(ms, "sid") ?? AttrAny(ms, "s") ?? string.Empty;
                        var vf = TryParseInt(AttrAny(ms, "vf"));
                        var vt = TryParseInt(AttrAny(ms, "vt"));
                        var isr = string.Equals(AttrAny(ms, "isr"), "true", StringComparison.OrdinalIgnoreCase);
                        mergeSources.Add(new SoapMergeSourceInfo(sid, vf, vt, isr));
                    }
                }

                allChanges.Add(new SoapChangesetChange(serverPath, isFolder, itemCs, changeType, mergeSources));
            }

            // Pagination: if we got a full page, continue from the last item
            if (changeElements.Count < pageSize)
                break;

            var lastChange = allChanges[^1];
            lastItem = lastChange.ServerPath;
        }

        return allChanges;
    }

    /// <summary>
    /// 下载指定文件的内容（返回 byte[]）。内部调用 QueryItems 获取下载 URL，然后 HTTP GET 获取内容。
    /// </summary>
    /// <param name="serverPath">文件的服务器路径</param>
    /// <param name="atChangeset">指定 changeset 版本；null 则下载最新</param>
    public async Task<byte[]> DownloadFileContentAsync(
        string serverPath,
        int? atChangeset = null,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await DownloadFileToStreamAsync(serverPath, ms, atChangeset, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <summary>
    /// 下载指定文件并写入目标流。内部调用 QueryItems 获取下载 URL，然后 HTTP GET 获取内容。
    /// </summary>
    /// <param name="serverPath">文件的服务器路径</param>
    /// <param name="destination">目标流</param>
    /// <param name="atChangeset">指定 changeset 版本；null 则下载最新</param>
    public async Task DownloadFileToStreamAsync(
        string serverPath,
        Stream destination,
        int? atChangeset = null,
        CancellationToken ct = default)
    {
        // Use QueryItems to get the download URL token
        var items = await QueryItemsAsync(serverPath, "None", atChangeset, ct).ConfigureAwait(false);
        var item = items.FirstOrDefault(i => !i.IsFolder);
        if (item is null)
            throw new FileNotFoundException($"File not found in TFVC: {serverPath}");
        if (string.IsNullOrEmpty(item.DownloadUrl))
            throw new InvalidOperationException($"No download URL returned for: {serverPath}");

        // The durl is a query-string token; prepend the collection's download handler
        var baseUrl = _connection.ServerUrl.TrimEnd('/');
        var downloadUrl = $"{baseUrl}/VersionControl/v1.0/item.ashx?{item.DownloadUrl}";

        using var httpClient = _connection.CreateHttpClient();
        using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // TFS item.ashx always returns GZip-compressed content regardless of Accept-Encoding.
        // Detect and decompress automatically.
        using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffered = new System.IO.MemoryStream();
        await responseStream.CopyToAsync(buffered, ct).ConfigureAwait(false);
        buffered.Position = 0;

        if (buffered.Length >= 2)
        {
            var header = new byte[2];
            buffered.Read(header, 0, 2);
            buffered.Position = 0;

            if (header[0] == 0x1F && header[1] == 0x8B)
            {
                // GZip compressed - decompress
                using var gzip = new System.IO.Compression.GZipStream(buffered, System.IO.Compression.CompressionMode.Decompress);
                await gzip.CopyToAsync(destination, ct).ConfigureAwait(false);
                return;
            }
        }

        // Not compressed - copy directly
        await buffered.CopyToAsync(destination, ct).ConfigureAwait(false);
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
