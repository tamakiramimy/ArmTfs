using System.Net;
using System.Net.Http;
using System.Reflection;
using ArmTfs.Core.Client;
using ArmTfs.Core.Client.Soap;
using ArmTfs.Core.Config;

namespace ArmTfs.Core.Tests;

/// <summary>
/// 验证 TfvcSoapClient 的 SOAP 请求构造与响应解析。
/// 不依赖真实 TFS 服务器：通过 HttpMessageHandler 注入伪造响应。
/// </summary>
public class TfvcSoapClientTests
{
    [Fact]
    public void Esc_handles_xml_special_chars()
    {
        Assert.Equal("&amp;", TfvcSoapClient.Esc("&"));
        Assert.Equal("&lt;tag&gt;", TfvcSoapClient.Esc("<tag>"));
        Assert.Equal(string.Empty, TfvcSoapClient.Esc(null));
        Assert.Equal(string.Empty, TfvcSoapClient.Esc(string.Empty));
    }

    [Fact]
    public void ExtractFaultMessage_returns_decoded_faultstring()
    {
        var xml = """
                  <?xml version="1.0"?>
                  <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                    <soap:Body>
                      <soap:Fault>
                        <faultcode>soap:Server</faultcode>
                        <faultstring>TF14002: The identity is not a member of the team project.</faultstring>
                      </soap:Fault>
                    </soap:Body>
                  </soap:Envelope>
                  """;
        Assert.Equal("TF14002: The identity is not a member of the team project.",
            TfvcSoapClient.ExtractFaultMessage(xml));
    }

    [Fact]
    public void ExtractFaultMessage_falls_back_to_raw_when_no_fault()
    {
        var xml = "Internal Server Error: something broke";
        var result = TfvcSoapClient.ExtractFaultMessage(xml);
        Assert.Equal(xml, result);
    }

    [Fact]
    public void EndpointUrl_uses_repository_asmx_suffix()
    {
        var soap = BuildSoapWithFakeHandler(_ => CreateXmlResponse("<root/>"));
        Assert.EndsWith("/VersionControl/v1.0/Repository.asmx", soap.EndpointUrl);
    }

    [Fact]
    public async Task QueryWorkspaces_parses_workspace_attributes()
    {
        var soap = BuildSoapWithFakeHandler(async req =>
        {
            // Verify request contained QueryWorkspaces method
            var content = await req.Content!.ReadAsStringAsync();
            Assert.Contains("QueryWorkspaces", content);
            Assert.Contains("\"http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03/QueryWorkspaces\"",
                req.Headers.GetValues("SOAPAction").First());

            return CreateXmlResponse("""
                <?xml version="1.0"?>
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <QueryWorkspacesResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                      <QueryWorkspacesResult>
                        <Workspace name="ws1" owner="DOMAIN\alice" ownerdisp="Alice" computer="PC1" location="Server">
                          <Comment>first ws</Comment>
                        </Workspace>
                        <Workspace name="ws2" owner="DOMAIN\bob" computer="PC2">
                          <Comment />
                        </Workspace>
                      </QueryWorkspacesResult>
                    </QueryWorkspacesResponse>
                  </soap:Body>
                </soap:Envelope>
                """);
        });

        var workspaces = await soap.QueryWorkspacesAsync();

        Assert.Equal(2, workspaces.Count);
        Assert.Equal("ws1", workspaces[0].Name);
        Assert.Equal(@"DOMAIN\alice", workspaces[0].Owner);
        Assert.Equal("Alice", workspaces[0].OwnerDisplay);
        Assert.Equal("PC1", workspaces[0].Computer);
        Assert.Equal("first ws", workspaces[0].Comment);
        Assert.Equal("Server", workspaces[0].Location);
        Assert.Equal("ws2", workspaces[1].Name);
    }

    [Fact]
    public async Task QueryWorkspaces_passes_owner_and_computer_filters_in_body()
    {
        string capturedBody = string.Empty;
        var soap = BuildSoapWithFakeHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return CreateXmlResponse("""
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body><QueryWorkspacesResponse><QueryWorkspacesResult /></QueryWorkspacesResponse></soap:Body>
                </soap:Envelope>
                """);
        });

        await soap.QueryWorkspacesAsync("user@domain", "MyPC");

        Assert.Contains("<tns:ownerName>user@domain</tns:ownerName>", capturedBody);
        Assert.Contains("<tns:computer>MyPC</tns:computer>", capturedBody);
    }

    [Fact]
    public async Task CreateBranch_returns_changeset_id_from_response()
    {
        var soap = BuildSoapWithFakeHandler(_ => CreateXmlResponse("""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <CreateBranchResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                  <CreateBranchResult cset="2024" />
                </CreateBranchResponse>
              </soap:Body>
            </soap:Envelope>
            """));

        var cs = await soap.CreateBranchAsync("$/Proj/Src", "$/Proj/Tgt", 100, "branch test");
        Assert.Equal(2024, cs);
    }

    [Fact]
    public async Task CreateBranch_throws_SoapFaultException_on_http_error()
    {
        var soap = BuildSoapWithFakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""
                <?xml version="1.0"?>
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <soap:Fault>
                      <faultstring>TF400898: Source and target paths are identical.</faultstring>
                    </soap:Fault>
                  </soap:Body>
                </soap:Envelope>
                """, System.Text.Encoding.UTF8, "text/xml"),
        });

        var ex = await Assert.ThrowsAsync<SoapFaultException>(() =>
            soap.CreateBranchAsync("$/Proj/Same", "$/Proj/Same", null, "should fail"));
        Assert.Equal("CreateBranch", ex.Method);
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("TF400898", ex.FaultMessage);
    }

    [Fact]
    public async Task CreateWorkspace_sends_server_location_in_request()
    {
        string capturedBody = string.Empty;
        var soap = BuildSoapWithFakeHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return CreateXmlResponse("""
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <CreateWorkspaceResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                      <CreateWorkspaceResult>
                        <Workspace name="test-ws" owner="DOMAIN\alice" computer="PC1" location="Server">
                          <Comment>hello</Comment>
                        </Workspace>
                      </CreateWorkspaceResult>
                    </CreateWorkspaceResponse>
                  </soap:Body>
                </soap:Envelope>
                """);
        });

        var ws = await soap.CreateWorkspaceAsync("test-ws", @"DOMAIN\alice", "PC1", "hello");

        Assert.Contains("location=\"Server\"", capturedBody);
        Assert.Contains("name=\"test-ws\"", capturedBody);
        Assert.Equal("test-ws", ws.Name);
        Assert.Equal(@"DOMAIN\alice", ws.Owner);
    }

    [Fact]
    public async Task DeleteWorkspace_sends_correct_method()
    {
        string capturedAction = string.Empty;
        var soap = BuildSoapWithFakeHandler(req =>
        {
            capturedAction = req.Headers.GetValues("SOAPAction").First();
            return CreateXmlResponse("""
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <DeleteWorkspaceResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03"/>
                  </soap:Body>
                </soap:Envelope>
                """);
        });

        await soap.DeleteWorkspaceAsync("test-ws", @"DOMAIN\alice");
        Assert.Contains("DeleteWorkspace", capturedAction);
    }

    [Fact]
    public async Task PendMerge_parses_GetOperation_attributes()
    {
        var soap = BuildSoapWithFakeHandler(_ => CreateXmlResponse("""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <MergeResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                  <MergeResult>
                    <GetOperation itemid="555" sitem="$/P/Src/a.cs" titem="$/P/Tgt/a.cs" ct="Merge|Edit" sver="100" />
                    <GetOperation itemid="556" sitem="$/P/Src/b.cs" titem="$/P/Tgt/b.cs" ct="Merge|Add" sver="100" />
                  </MergeResult>
                </MergeResponse>
              </soap:Body>
            </soap:Envelope>
            """));

        var ops = await soap.PendMergeAsync("ws", "alice", "$/P/Src", "$/P/Tgt", 100, 100);
        Assert.Equal(2, ops.Count);
        Assert.Equal(555, ops[0].ItemId);
        Assert.Equal("$/P/Src/a.cs", ops[0].SourceServerItem);
        Assert.Equal("$/P/Tgt/a.cs", ops[0].TargetServerItem);
        Assert.Equal("Merge|Edit", ops[0].ChangeType);
        Assert.Equal(100, ops[0].VersionTo);
    }

    [Fact]
    public async Task CheckIn_returns_new_changeset_id()
    {
        var soap = BuildSoapWithFakeHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            // The MergeSources metadata should be in the request
            Assert.Contains("mergeSource=", body);
            Assert.Contains("mergeFrom=", body);
            Assert.Contains("mergeTo=", body);
            return CreateXmlResponse("""
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <CheckInResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                      <CheckInResult cset="9876" />
                    </CheckInResponse>
                  </soap:Body>
                </soap:Envelope>
                """);
        });

        var changes = new[]
        {
            new Models.Soap.SoapPendingChange
            {
                ItemId = 555,
                ServerItem = "$/P/Tgt/a.cs",
                ChangeType = "Merge|Edit",
                SourceServerItem = "$/P/Src/a.cs",
                VersionFrom = 100,
                VersionTo = 100,
            },
        };

        var cs = await soap.CheckInAsync("ws", "alice", "test merge", changes);
        Assert.Equal(9876, cs);
    }

    [Fact]
    public async Task CheckIn_throws_when_changes_empty()
    {
        var soap = BuildSoapWithFakeHandler(_ => CreateXmlResponse("<root/>"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            soap.CheckInAsync("ws", "alice", "comment", Array.Empty<Models.Soap.SoapPendingChange>()));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static TfvcSoapClient BuildSoapWithFakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        // Build a TfsConnection with a fake config so CreateHttpClient produces a working HttpClient.
        // Then swap the underlying handler via reflection-free approach: TfsConnection.CreateHttpClient
        // is `new HttpClient()`, so we can't directly inject. Instead we use the FakeConnection wrapper.
        var conn = new FakeTfsConnection(responder);
        return new TfvcSoapClient(conn);
    }

    /// <summary>Sync overload that auto-wraps in Task.FromResult — for tests not needing async response building.</summary>
    private static TfvcSoapClient BuildSoapWithFakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => BuildSoapWithFakeHandler(req => Task.FromResult(responder(req)));

    private static HttpResponseMessage CreateXmlResponse(string xml) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, System.Text.Encoding.UTF8, "text/xml"),
        };

    /// <summary>
    /// 测试用的 TfsConnection 子类，覆盖 CreateHttpClient 以注入伪造的 HttpMessageHandler。
    /// </summary>
    private sealed class FakeTfsConnection : TfsConnection
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

        public FakeTfsConnection(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
            : base(new TfsConfig
            {
                ServerUrl = "https://fake.example.com/Collection",
                PersonalAccessToken = "fake-pat",
            })
        {
            _responder = responder;
        }

        public override HttpClient CreateHttpClient()
        {
            var handler = new FakeHandler(_responder);
            return new HttpClient(handler);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _responder(request);
    }
}
