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
        var soap = BuildSoapWithFakeHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
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
    public async Task QueryItems_parses_itemid_attribute()
    {
        var soap = BuildSoapWithFakeHandler(_ => CreateXmlResponse("""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <QueryItemsResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                  <QueryItemsResult>
                    <Item itemid="12483428" item="$/P/a.txt" type="File" cs="42" len="5" hash="abc" />
                  </QueryItemsResult>
                </QueryItemsResponse>
              </soap:Body>
            </soap:Envelope>
            """));

        var items = await soap.QueryItemsAsync("$/P/a.txt", recursion: "None");

        Assert.Single(items);
        Assert.Equal(12483428, items[0].ItemId);
        Assert.Equal("$/P/a.txt", items[0].ServerPath);
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
        var soap = BuildSoapWithFakeHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
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

        var result = await soap.PendMergeAsync("ws", "alice", "$/P/Src", "$/P/Tgt", 100, 100);
        var ops = result.Operations;
        Assert.Equal(2, ops.Count);
        Assert.Equal(555, ops[0].ItemId);
        Assert.Equal("$/P/Src/a.cs", ops[0].SourceServerItem);
        Assert.Equal("$/P/Tgt/a.cs", ops[0].TargetServerItem);
        Assert.Equal("Merge|Edit", ops[0].ChangeType);
        Assert.Equal(100, ops[0].VersionTo);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public async Task PendMerge_parses_conflicts()
    {
        var soap = BuildSoapWithFakeHandler(_ => CreateXmlResponse("""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <MergeResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                  <MergeResult />
                  <conflicts>
                    <Conflict ysitem="$/P/Tgt/a.cs" tsitem="$/P/Src/a.cs" ctype="Merge" bchg="Edit Merge" isresolved="false" />
                    <Conflict ysitem="$/P/Tgt/b.cs" tsitem="$/P/Src/b.cs" ctype="Merge" bchg="Encoding Branch Merge" isresolved="true" />
                  </conflicts>
                </MergeResponse>
              </soap:Body>
            </soap:Envelope>
            """));

        var result = await soap.PendMergeAsync("ws", "alice", "$/P/Src", "$/P/Tgt", 100, 100);
        Assert.Empty(result.Operations);
        // Only the UNRESOLVED conflict (isresolved="false") is reported; the resolved one is skipped.
        Assert.Single(result.Conflicts);
        Assert.Equal(0, result.Conflicts[0].ConflictId);
        Assert.Equal("$/P/Tgt/a.cs", result.Conflicts[0].TargetServerItem);
        Assert.Equal("$/P/Src/a.cs", result.Conflicts[0].SourceServerItem);
        Assert.Equal("Merge", result.Conflicts[0].ConflictType);
    }

    [Fact]
    public async Task PendMerge_parses_conflict_id()
    {
        var soap = BuildSoapWithFakeHandler(_ => CreateXmlResponse("""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <MergeResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                  <MergeResult />
                  <conflicts>
                    <Conflict cid="42" ysitem="$/P/Tgt/a.cs" tsitem="$/P/Src/a.cs" ctype="Merge" bchg="Edit Merge" isresolved="false" />
                  </conflicts>
                </MergeResponse>
              </soap:Body>
            </soap:Envelope>
            """));

        var result = await soap.PendMergeAsync("ws", "alice", "$/P/Src", "$/P/Tgt", 100, 100);

        Assert.Single(result.Conflicts);
        Assert.Equal(42, result.Conflicts[0].ConflictId);
    }

    [Fact]
    public async Task QueryConflicts_sends_item_specs_and_parses_conflict_id()
    {
        string capturedBody = string.Empty;
        var soap = BuildSoapWithFakeHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return CreateXmlResponse("""
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <QueryConflictsResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                      <QueryConflictsResult>
                        <Conflict cid="99" ysitem="$/P/Tgt/a.cs" tsitem="$/P/Src/a.cs" ctype="Merge" bchg="Edit Merge" isresolved="false" />
                      </QueryConflictsResult>
                    </QueryConflictsResponse>
                  </soap:Body>
                </soap:Envelope>
                """);
        });

        var conflicts = await soap.QueryConflictsAsync("ws", "alice", new[] { "$/P/Tgt" });

        Assert.Contains("<tns:QueryConflicts>", capturedBody);
        Assert.Contains("<tns:workspaceName>ws</tns:workspaceName>", capturedBody);
        Assert.Contains("<tns:ownerName>alice</tns:ownerName>", capturedBody);
        Assert.Contains("<tns:ItemSpec item=\"$/P/Tgt\" recurse=\"Full\" did=\"0\" />", capturedBody);
        Assert.Single(conflicts);
        Assert.Equal(99, conflicts[0].ConflictId);
    }

    [Fact]
    public async Task ResolveConflict_sends_accept_theirs_and_parses_operations()
    {
        string capturedBody = string.Empty;
        string capturedAction = string.Empty;
        var soap = BuildSoapWithFakeHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            capturedAction = req.Headers.GetValues("SOAPAction").First();
            return CreateXmlResponse("""
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <ResolveResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                      <ResolveResult>
                        <GetOperation itemid="555" sitem="$/P/Src/a.cs" titem="$/P/Tgt/a.cs" chg="Merge|Edit" mvfrom="100" mvto="101" />
                      </ResolveResult>
                      <undoOperations />
                      <resolvedConflicts>
                        <Conflict cid="42" ysitem="$/P/Tgt/a.cs" tsitem="$/P/Src/a.cs" ctype="Merge" bchg="Edit Merge" isresolved="true" />
                      </resolvedConflicts>
                    </ResolveResponse>
                  </soap:Body>
                </soap:Envelope>
                """);
        });

        var result = await soap.ResolveConflictAsync("ws", "alice", 42, "AcceptTheirs");

        Assert.Contains("Resolve", capturedAction);
        Assert.Contains("<tns:Resolve>", capturedBody);
        Assert.Contains("<tns:conflictId>42</tns:conflictId>", capturedBody);
        Assert.Contains("<tns:resolution>AcceptTheirs</tns:resolution>", capturedBody);
        Assert.Contains("<tns:newPath xsi:nil=\"true\" />", capturedBody);
        Assert.Contains("<tns:encoding>-2</tns:encoding>", capturedBody);
        Assert.Contains("<tns:lockLevel>Unchanged</tns:lockLevel>", capturedBody);
        Assert.Single(result.Operations);
        Assert.Equal("$/P/Src/a.cs", result.Operations[0].SourceServerItem);
        Assert.Equal("$/P/Tgt/a.cs", result.Operations[0].TargetServerItem);
        Assert.Equal("Merge|Edit", result.Operations[0].ChangeType);
        Assert.Equal(100, result.Operations[0].VersionFrom);
        Assert.Equal(101, result.Operations[0].VersionTo);
        Assert.Empty(result.UndoOperations);
        Assert.Single(result.ResolvedConflicts);
        Assert.Equal(42, result.ResolvedConflicts[0].ConflictId);
    }

    [Fact]
    public async Task CheckIn_returns_new_changeset_id()
    {
        var soap = BuildSoapWithFakeHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            // CheckIn must send merge lineage as <MergeSource s= vf= vt=> inside <MergeSources>
            // (per Repository.asmx WSDL), plus the required checkinOptions/deferCheckIn/checkInTicket.
            Assert.Contains("<tns:MergeSource", body);
            Assert.Contains("vf=", body);
            Assert.Contains("vt=", body);
            Assert.Contains("<tns:checkinOptions>None</tns:checkinOptions>", body);
            Assert.Contains("<tns:checkInTicket>0</tns:checkInTicket>", body);
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
    public async Task UpdateLocalVersion_sends_legacy_local_version_shape()
    {
        string capturedBody = string.Empty;
        var soap = BuildSoapWithFakeHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            Assert.Contains("/UpdateLocalVersion\"", req.Headers.GetValues("SOAPAction").First());
            return CreateXmlResponse("""
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <UpdateLocalVersionResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03" />
                  </soap:Body>
                </soap:Envelope>
                """);
        });

        await soap.UpdateLocalVersionAsync(
            "ws",
            "owner-guid",
            new[]
            {
                new Models.Soap.SoapLocalVersionUpdate
                {
                    ServerPath = "$/P/a.txt",
                    LocalPath = @"C:\temp\a.txt",
                    LocalVersion = 42,
                    ItemId = 123,
                },
            });

        Assert.Contains("<UpdateLocalVersion xmlns=\"http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03\">", capturedBody);
        Assert.Contains("<workspaceName>ws</workspaceName>", capturedBody);
        Assert.Contains("<ownerName>owner-guid</ownerName>", capturedBody);
        Assert.Contains("<LocalVersionUpdate xsi:type=\"LocalVersionUpdate\"", capturedBody);
        Assert.Contains("tlocal=\"C:\\temp\\a.txt\"", capturedBody);
        Assert.Contains("lver=\"42\"", capturedBody);
        Assert.DoesNotContain("sitem=", capturedBody);
        Assert.Contains("itemid=\"123\"", capturedBody);
        Assert.DoesNotContain("maxClientPathLength", capturedBody);
    }

    [Fact]
    public async Task CheckInWithContent_updates_local_version_before_pending_edits()
    {
        var actions = new List<string>();
        string updateLocalVersionBody = string.Empty;

        var soap = BuildSoapWithFakeHandler(async req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/upload.ashx", StringComparison.OrdinalIgnoreCase))
            {
                actions.Add("Upload");
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var body = await req.Content!.ReadAsStringAsync();
            var action = req.Headers.GetValues("SOAPAction").First();

            if (action.Contains("/CreateWorkspace\"", StringComparison.Ordinal))
            {
                actions.Add("CreateWorkspace");
                return CreateXmlResponse("""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <CreateWorkspaceResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                          <CreateWorkspaceResult computer="PC1" islocal="false" name="ws" owner="owner-guid" ownerdisp="Owner" permissions="9">
                            <Comment>arm-tfs SOAP checkin</Comment>
                            <Folders />
                          </CreateWorkspaceResult>
                        </CreateWorkspaceResponse>
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            if (action.Contains("/QueryItems\"", StringComparison.Ordinal))
            {
                actions.Add("QueryItems");
                Assert.Contains("$/P/a.txt", body);
                return CreateXmlResponse("""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <QueryItemsResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                          <QueryItemsResult>
                            <Item itemid="123" item="$/P/a.txt" type="File" cs="42" len="5" />
                          </QueryItemsResult>
                        </QueryItemsResponse>
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            if (action.Contains("/UpdateLocalVersion\"", StringComparison.Ordinal))
            {
                actions.Add("UpdateLocalVersion");
                updateLocalVersionBody = body;
                return CreateXmlResponse("""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <UpdateLocalVersionResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03" />
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            if (action.Contains("/PendChanges\"", StringComparison.Ordinal))
            {
                actions.Add("PendChanges");
                return CreateXmlResponse("""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <PendChangesResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                          <PendChangesResult>
                            <GetOperation itemid="123" titem="$/P/a.txt" chg="Edit" />
                          </PendChangesResult>
                        </PendChangesResponse>
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            if (action.Contains("/CheckIn\"", StringComparison.Ordinal))
            {
                actions.Add("CheckIn");
                return CreateXmlResponse("""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <CheckInResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                          <CheckInResult cset="45678" />
                        </CheckInResponse>
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            if (action.Contains("/DeleteWorkspace\"", StringComparison.Ordinal))
            {
                actions.Add("DeleteWorkspace");
                return CreateXmlResponse("""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <DeleteWorkspaceResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03" />
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            throw new InvalidOperationException($"Unexpected SOAP action: {action}");
        });

        var changeset = await soap.CheckInWithContentAsync(
            "checkin",
            "owner-guid",
            new[]
            {
                new Models.Soap.SoapContentChange
                {
                    ChangeType = "Edit",
                    ServerPath = "$/P/a.txt",
                    Content = System.Text.Encoding.UTF8.GetBytes("hello"),
                    BaseVersion = 42,
                },
            });

        Assert.Equal(45678, changeset);
        Assert.Equal(new[] { "CreateWorkspace", "QueryItems", "UpdateLocalVersion", "PendChanges", "Upload", "CheckIn", "DeleteWorkspace" }, actions);
        Assert.Contains("itemid=\"123\"", updateLocalVersionBody);
        Assert.Contains("lver=\"42\"", updateLocalVersionBody);
        Assert.Contains("tlocal=", updateLocalVersionBody);
    }

    [Fact]
    public async Task CheckInWithContent_keeps_original_owner_when_CreateWorkspace_returns_display_owner()
    {
        const string originalOwner = "0cbfc36d-7ad1-4d7d-919e-581a311ce2e2";
        const string returnedDisplayOwner = "display-owner";
        var nonCreateSoapBodies = new List<string>();
        string uploadBody = string.Empty;

        var soap = BuildSoapWithFakeHandler(async req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/upload.ashx", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await req.Content!.ReadAsByteArrayAsync();
                uploadBody = System.Text.Encoding.UTF8.GetString(bytes);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var body = await req.Content!.ReadAsStringAsync();
            var action = req.Headers.GetValues("SOAPAction").First();

            if (action.Contains("/CreateWorkspace\"", StringComparison.Ordinal))
            {
                Assert.Contains($"owner=\"{originalOwner}\"", body);
                return CreateXmlResponse($"""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <CreateWorkspaceResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                          <CreateWorkspaceResult computer="PC1" islocal="false" name="ws" owner="{returnedDisplayOwner}" ownerdisp="{returnedDisplayOwner}" permissions="9">
                            <Comment>arm-tfs SOAP checkin</Comment>
                            <Folders />
                          </CreateWorkspaceResult>
                        </CreateWorkspaceResponse>
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            nonCreateSoapBodies.Add(body);

            if (action.Contains("/PendChanges\"", StringComparison.Ordinal))
            {
                return CreateXmlResponse("""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <PendChangesResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                          <PendChangesResult>
                            <GetOperation itemid="123" titem="$/P/a.txt" chg="Add" />
                          </PendChangesResult>
                        </PendChangesResponse>
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            if (action.Contains("/CheckIn\"", StringComparison.Ordinal))
            {
                return CreateXmlResponse("""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <CheckInResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                          <CheckInResult cset="12345" />
                        </CheckInResponse>
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            if (action.Contains("/DeleteWorkspace\"", StringComparison.Ordinal))
            {
                return CreateXmlResponse("""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <DeleteWorkspaceResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03" />
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            throw new InvalidOperationException($"Unexpected SOAP action: {action}");
        });

        var changeset = await soap.CheckInWithContentAsync(
            "checkin",
            originalOwner,
            new[]
            {
                new Models.Soap.SoapContentChange
                {
                    ChangeType = "Add",
                    ServerPath = "$/P/a.txt",
                    Content = System.Text.Encoding.UTF8.GetBytes("hello"),
                },
            });

        Assert.Equal(12345, changeset);
        Assert.NotEmpty(nonCreateSoapBodies);
        Assert.All(nonCreateSoapBodies, body =>
        {
            Assert.Contains($"<tns:ownerName>{originalOwner}</tns:ownerName>", body);
            Assert.DoesNotContain(returnedDisplayOwner, body);
        });
        Assert.Contains(originalOwner, uploadBody);
        Assert.DoesNotContain(returnedDisplayOwner, uploadBody);
    }

    [Fact]
    public async Task TfvcClientService_Checkin_retries_with_authenticated_owner_from_TF204017()
    {
        const string wrongOwner = "8adbeef9-f3da-4bd2-8d3f-7974d73f8b18";
        const string authenticatedOwner = "0cbfc36d-7ad1-4d7d-919e-581a311ce2e2";
        var createOwners = new List<string>();
        var pendOwners = new List<string>();
        var firstPend = true;

        using var connection = new FakeTfsConnection(async req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/upload.ashx", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var body = await req.Content!.ReadAsStringAsync();
            var action = req.Headers.GetValues("SOAPAction").First();

            if (action.Contains("/QueryWorkspaces\"", StringComparison.Ordinal))
            {
                return CreateXmlResponse($"""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <QueryWorkspacesResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                          <QueryWorkspacesResult>
                            <Workspace name="other-user-ws" owner="{wrongOwner}" ownerdisp="Wrong User" computer="PC1" />
                          </QueryWorkspacesResult>
                        </QueryWorkspacesResponse>
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            if (action.Contains("/CreateWorkspace\"", StringComparison.Ordinal))
            {
                var owner = body.Contains(wrongOwner, StringComparison.Ordinal) ? wrongOwner : authenticatedOwner;
                createOwners.Add(owner);
                return CreateXmlResponse($"""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <CreateWorkspaceResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                          <CreateWorkspaceResult computer="PC1" islocal="false" name="ws" owner="{owner}" ownerdisp="Owner" permissions="9">
                            <Comment>arm-tfs SOAP checkin</Comment>
                            <Folders />
                          </CreateWorkspaceResult>
                        </CreateWorkspaceResponse>
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            if (action.Contains("/PendChanges\"", StringComparison.Ordinal))
            {
                var owner = body.Contains(wrongOwner, StringComparison.Ordinal) ? wrongOwner : authenticatedOwner;
                pendOwners.Add(owner);
                if (firstPend)
                {
                    firstPend = false;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent($"""
                            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                              <soap:Body>
                                <soap:Fault>
                                  <faultcode>soap:Server</faultcode>
                                  <faultstring>TF204017: Cannot complete the operation because the user ({authenticatedOwner}) does not have Use permission.</faultstring>
                                </soap:Fault>
                              </soap:Body>
                            </soap:Envelope>
                            """, System.Text.Encoding.UTF8, "text/xml")
                    };
                }

                return CreateXmlResponse("""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <PendChangesResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                          <PendChangesResult>
                            <GetOperation itemid="123" titem="$/P/a.txt" chg="Add" />
                          </PendChangesResult>
                        </PendChangesResponse>
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            if (action.Contains("/CheckIn\"", StringComparison.Ordinal))
            {
                return CreateXmlResponse("""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <CheckInResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                          <CheckInResult cset="23456" />
                        </CheckInResponse>
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            if (action.Contains("/DeleteWorkspace\"", StringComparison.Ordinal))
            {
                return CreateXmlResponse("""
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <DeleteWorkspaceResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03" />
                      </soap:Body>
                    </soap:Envelope>
                    """);
            }

            throw new InvalidOperationException($"Unexpected SOAP action: {action}");
        });
        var service = new TfvcClientService(connection);

        var changes = new List<(string serverPath, Models.ChangeType changeType, byte[]? content, int? baseChangesetId)>
        {
            ("$/P/a.txt", Models.ChangeType.Add, System.Text.Encoding.UTF8.GetBytes("hello"), null),
        };

        var result = await service.CheckinAsync("checkin", changes);

        Assert.Equal(23456, result.ChangesetId);
        Assert.Equal(new[] { wrongOwner, authenticatedOwner }, createOwners);
        Assert.Equal(new[] { wrongOwner, authenticatedOwner }, pendOwners);
    }

    [Fact]
    public async Task CheckIn_throws_when_changes_empty()
    {
        var soap = BuildSoapWithFakeHandler(_ => CreateXmlResponse("<root/>"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            soap.CheckInAsync("ws", "alice", "comment", Array.Empty<Models.Soap.SoapPendingChange>()));
    }

    [Fact]
    public async Task Invoke_throws_on_fault_returned_with_http_200()
    {
        // TFS frequently returns a SOAP <Fault> with HTTP 200 (not 5xx). InvokeAsync must detect
        // the Fault element in the body regardless of status code and surface the real message.
        var soap = BuildSoapWithFakeHandler(_ => CreateXmlResponse("""
            <?xml version="1.0"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <soap:Fault>
                  <faultcode>soap:Server</faultcode>
                  <faultstring>TF14061: The workspace owner cannot be different from the authenticated user.</faultstring>
                </soap:Fault>
              </soap:Body>
            </soap:Envelope>
            """));

        var ex = await Assert.ThrowsAsync<SoapFaultException>(() =>
            soap.CreateWorkspaceAsync("ws", "WRONG\\owner", "PC1", "test"));
        Assert.Equal("CreateWorkspace", ex.Method);
        Assert.Equal(HttpStatusCode.OK, ex.StatusCode);
        Assert.Contains("TF14061", ex.FaultMessage);
    }

    [Fact]
    public async Task CreateWorkspace_parses_CreateWorkspaceResult_shape()
    {
        // Real TFS returns the created workspace as <CreateWorkspaceResult .../> with attributes
        // on the result element (no child <Workspace>). Regression guard for the parser.
        var soap = BuildSoapWithFakeHandler(_ => CreateXmlResponse("""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <CreateWorkspaceResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03">
                  <CreateWorkspaceResult computer="MR50240429MC1" islocal="false" name="arm-tfs-soap-merge-abc" owner="8adbeef9-f3da-4bd2-8d3f-7974d73f8b18" ownerdisp="杨峰" permissions="9">
                    <Comment>arm-tfs SOAP merge</Comment>
                    <Folders />
                    <LastAccessDate>2026-06-18T16:32:21.82Z</LastAccessDate>
                  </CreateWorkspaceResult>
                </CreateWorkspaceResponse>
              </soap:Body>
            </soap:Envelope>
            """));

        var ws = await soap.CreateWorkspaceAsync("arm-tfs-soap-merge-abc", "8adbeef9-f3da-4bd2-8d3f-7974d73f8b18", "MR50240429MC1", "arm-tfs SOAP merge");

        Assert.Equal("arm-tfs-soap-merge-abc", ws.Name);
        Assert.Equal("MR50240429MC1", ws.Computer);
        Assert.Equal("8adbeef9-f3da-4bd2-8d3f-7974d73f8b18", ws.Owner);
        Assert.Equal("杨峰", ws.OwnerDisplay);
        Assert.Equal("Server", ws.Location);
        Assert.Equal("arm-tfs SOAP merge", ws.Comment);
    }

    [Fact]
    public async Task PendMerge_sends_itemspec_and_optionsEx_per_wsdl()
    {
        string capturedBody = string.Empty;
        var soap = BuildSoapWithFakeHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return CreateXmlResponse("""
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body><MergeResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03"><MergeResult /></MergeResponse></soap:Body>
                </soap:Envelope>
                """);
        });

        await soap.PendMergeAsync("ws", "alice", "$/P/Src", "$/P/Tgt", 100, 100);

        // source/target must be ItemSpec elements (item= attribute), not plain strings.
        Assert.Contains("<tns:source item=\"$/P/Src\"", capturedBody);
        Assert.Contains("<tns:target item=\"$/P/Tgt\"", capturedBody);
        // ChangesetVersionSpec.cs is an attribute.
        Assert.Contains("cs=\"100\"", capturedBody);
        Assert.DoesNotContain("<tns:cs>", capturedBody);
        // optionsEx is required (minOccurs=1).
        Assert.Contains("<tns:optionsEx>0</tns:optionsEx>", capturedBody);
    }

    [Fact]
    public async Task PendMerge_sends_changeset_range_for_batch_plan()
    {
        string capturedBody = string.Empty;
        var soap = BuildSoapWithFakeHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return CreateXmlResponse("""
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body><MergeResponse xmlns="http://schemas.microsoft.com/TeamFoundation/2005/06/VersionControl/ClientServices/03"><MergeResult /></MergeResponse></soap:Body>
                </soap:Envelope>
                """);
        });

        await soap.PendMergeAsync("ws", "alice", "$/P/Src", "$/P/Tgt", 100, 110);

        Assert.Contains("<tns:from xsi:type=\"tns:ChangesetVersionSpec\" cs=\"100\" />", capturedBody);
        Assert.Contains("<tns:to xsi:type=\"tns:ChangesetVersionSpec\" cs=\"110\" />", capturedBody);
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
