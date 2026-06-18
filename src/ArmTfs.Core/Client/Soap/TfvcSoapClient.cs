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

        if (!response.IsSuccessStatusCode)
        {
            throw new SoapFaultException(method, response.StatusCode, ExtractFaultMessage(xml), xml);
        }

        try
        {
            return XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new SoapFaultException(method, response.StatusCode, $"Malformed SOAP response: {ex.Message}", xml);
        }
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
        string ownerName,
        string computer,
        string? comment = null,
        CancellationToken ct = default)
    {
        // location="Server" 表示服务端 workspace（不需要本地工作目录）
        var body = $@"    <tns:CreateWorkspace>
      <tns:workspace name=""{Esc(name)}"" owner=""{Esc(ownerName)}"" computer=""{Esc(computer)}"" location=""Server"">
        <tns:Comment>{Esc(comment ?? string.Empty)}</tns:Comment>
        <tns:Folders />
        <tns:LastAccessDate>0001-01-01T00:00:00</tns:LastAccessDate>
      </tns:workspace>
    </tns:CreateWorkspace>";

        var doc = await InvokeAsync("CreateWorkspace", body, ct).ConfigureAwait(false);

        var ws = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Workspace");
        if (ws is null)
            throw new SoapFaultException("CreateWorkspace", System.Net.HttpStatusCode.OK,
                "CreateWorkspace response did not contain a Workspace element", doc.ToString());

        return new SoapWorkspace
        {
            Name = AttrAny(ws, "name") ?? name,
            Owner = AttrAny(ws, "owner") ?? ownerName,
            OwnerDisplay = AttrAny(ws, "ownerdisp"),
            Computer = AttrAny(ws, "computer") ?? computer,
            Comment = ChildText(ws, "Comment") ?? comment ?? string.Empty,
            Location = AttrAny(ws, "location"),
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
    public async Task<IReadOnlyList<SoapMergeOperation>> PendMergeAsync(
        string workspaceName,
        string ownerName,
        string sourcePath,
        string targetPath,
        int? fromChangeset,
        int? toChangeset,
        CancellationToken ct = default)
    {
        var fromEl = fromChangeset.HasValue
            ? $@"<tns:from xsi:type=""tns:ChangesetVersionSpec""><tns:cs>{fromChangeset.Value}</tns:cs></tns:from>"
            : @"<tns:from xsi:nil=""true"" />";
        var toEl = toChangeset.HasValue
            ? $@"<tns:to xsi:type=""tns:ChangesetVersionSpec""><tns:cs>{toChangeset.Value}</tns:cs></tns:to>"
            : @"<tns:to xsi:type=""tns:LatestVersionSpec""/>";

        var body = $@"    <tns:Merge>
      <tns:workspaceName>{Esc(workspaceName)}</tns:workspaceName>
      <tns:workspaceOwner>{Esc(ownerName)}</tns:workspaceOwner>
      <tns:source>{Esc(sourcePath)}</tns:source>
      <tns:target>{Esc(targetPath)}</tns:target>
      {fromEl}
      {toEl}
      <tns:options>None</tns:options>
      <tns:lockLevel>Unchanged</tns:lockLevel>
    </tns:Merge>";

        var doc = await InvokeAsync("Merge", body, ct).ConfigureAwait(false);

        var ops = new List<SoapMergeOperation>();
        foreach (var op in doc.Descendants().Where(e => e.Name.LocalName == "GetOperation"))
        {
            ops.Add(new SoapMergeOperation
            {
                ItemId = TryParseInt(AttrAny(op, "itemid")) ?? 0,
                SourceServerItem = AttrAny(op, "sitem") ?? AttrAny(op, "srcitem") ?? string.Empty,
                TargetServerItem = AttrAny(op, "titem") ?? AttrAny(op, "targetitem") ?? string.Empty,
                ChangeType = AttrAny(op, "ct") ?? AttrAny(op, "chgEx") ?? string.Empty,
                VersionFrom = TryParseInt(AttrAny(op, "vrevto"))
                              ?? TryParseInt(AttrAny(op, "mvfrom")),
                VersionTo = TryParseInt(AttrAny(op, "sver"))
                            ?? TryParseInt(AttrAny(op, "mvto")),
                IsPending = true,
            });
        }

        return ops;
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

        var changesXml = new StringBuilder();
        foreach (var change in changes)
        {
            var mergeSourceAttrs = string.Empty;
            if (!string.IsNullOrEmpty(change.SourceServerItem) && change.VersionTo.HasValue)
            {
                var verFrom = change.VersionFrom ?? change.VersionTo.Value;
                mergeSourceAttrs = $@" mergeSource=""{Esc(change.SourceServerItem)}"" mergeFrom=""{verFrom}"" mergeTo=""{change.VersionTo.Value}""";
            }

            changesXml.AppendLine(
                $@"          <tns:PendingChange itemid=""{change.ItemId}"" item=""{Esc(change.ServerItem)}"" chg=""{Esc(change.ChangeType)}""{mergeSourceAttrs} />");
        }

        var body = $@"    <tns:CheckIn>
      <tns:workspaceName>{Esc(workspaceName)}</tns:workspaceName>
      <tns:ownerName>{Esc(ownerName)}</tns:ownerName>
      <tns:serverItems>
{string.Concat(changes.Select(c => $"        <tns:string>{Esc(c.ServerItem)}</tns:string>\n"))}      </tns:serverItems>
      <tns:info>
        <tns:Comment>{Esc(comment)}</tns:Comment>
        <tns:Changes>
{changesXml}        </tns:Changes>
      </tns:info>
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
