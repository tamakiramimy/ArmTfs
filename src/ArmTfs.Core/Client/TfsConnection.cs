using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArmTfs.Core.Config;
using ArmTfs.Core.Models.Soap;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace ArmTfs.Core.Client;

/// <summary>
/// 封装对 TFS/Azure DevOps Server 的连接和认证逻辑，为 SOAP 客户端提供 HttpClient 和配置访问。
/// <para>
/// <b>跨平台认证策略（按优先级）：</b>
/// <list type="number">
///   <item>PAT 认证 — 跨平台首选，ARM64 macOS / Windows ARM / Linux 均支持</item>
///   <item>Basic Auth（用户名+密码）— 适用于服务器已开启 Basic Auth 且配合 HTTPS 的场景</item>
/// </list>
/// 此类有意不包含 Windows 集成认证（NTLM/Kerberos）：
/// 相关 API 仅存在于 Windows 专用包中，无法在非 Windows 平台编译。
/// </para>
/// </summary>
public class TfsConnection : IDisposable
{
    private readonly TfsConfig _config;
    private readonly object _httpClientGate = new();
    private readonly object _restClientGate = new();
    private AuthenticatedTfsUser? _authenticatedUser;
    private HttpClient? _httpClient;
    private VssConnection? _restConnection;
    private TfvcHttpClient? _tfvcClient;

    /// <summary>初始化连接对象。不会立即建立网络连接。</summary>
    /// <param name="config">已加载并应用环境变量覆盖的配置对象</param>
    public TfsConnection(TfsConfig config)
    {
        _config = config;
    }

    /// <summary>TFS Server Collection URL，未配置时抛出 <see cref="InvalidOperationException"/></summary>
    public string ServerUrl => _config.ServerUrl
        ?? throw new InvalidOperationException("TFS server URL is not configured. Run 'arm-tfs configure' first.");

    /// <summary>
    /// 通过 SOAP QueryWorkspaces 发送一个轻量级请求以验证凭据和连通性。
    /// 在执行实际操作前可调用此方法提前失败。纯 SOAP，无 REST 依赖。
    /// </summary>
    public async Task TestConnectionAsync(CancellationToken ct = default)
    {
        // Use SOAP QueryWorkspaces as a lightweight ping — read-only, no side effects.
        var soap = new Soap.TfvcSoapClient(this);
        await soap.QueryWorkspacesAsync(ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 获取当前认证用户的 TFVC owner GUID。
    /// 优先通过 connectionData 读取服务器确认的 authenticatedUser/authorizedUser，
    /// 仅在该接口不可用时再退回到严格受限的工作区启发式与显式用户名覆盖。
    /// </summary>
    public async Task<string?> GetAuthenticatedUserGuidAsync(CancellationToken ct = default)
    {
        var user = await GetAuthenticatedUserAsync(ct).ConfigureAwait(false);
        return user?.Id;
    }

    /// <summary>
    /// 获取当前认证用户的身份信息。
    /// </summary>
    public async Task<AuthenticatedTfsUser?> GetAuthenticatedUserAsync(CancellationToken ct = default)
    {
        if (_authenticatedUser is not null)
            return _authenticatedUser;

        if (LooksLikeOwnerIdentity(_config.Username))
        {
            _authenticatedUser = new AuthenticatedTfsUser(_config.Username!, _config.UserDisplayName, _config.Username);
            return _authenticatedUser;
        }

        try
        {
            var fromConnectionData = await TryGetAuthenticatedUserFromConnectionDataAsync(ct).ConfigureAwait(false);
            if (fromConnectionData is not null)
            {
                _authenticatedUser = fromConnectionData;
                return _authenticatedUser;
            }
        }
        catch
        {
            // Fall through to the strict workspace heuristics below.
        }

        try
        {
            var inferred = await TryInferAuthenticatedUserFromWorkspacesAsync(ct).ConfigureAwait(false);
            if (inferred is not null)
            {
                _authenticatedUser = inferred;
                return _authenticatedUser;
            }
        }
        catch
        {
            // Ignore — caller will surface a clear error if owner cannot be resolved.
        }

        return null;
    }

    private async Task<AuthenticatedTfsUser?> TryGetAuthenticatedUserFromConnectionDataAsync(CancellationToken ct)
    {
        var requestUri = ServerUrl.TrimEnd('/') + "/_apis/connectionData?connectOptions=1&lastChangeId=-1&lastChangeId64=-1";

        var client = CreateHttpClient();
        using var response = await client.GetAsync(requestUri, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!TryReadAuthenticatedUser(document.RootElement, "authenticatedUser", out var user)
            && !TryReadAuthenticatedUser(document.RootElement, "authorizedUser", out user))
        {
            return null;
        }

        return user;
    }

    private async Task<AuthenticatedTfsUser?> TryInferAuthenticatedUserFromWorkspacesAsync(CancellationToken ct)
    {
        var soap = new Soap.TfvcSoapClient(this);
        var workspaces = await soap.QueryWorkspacesAsync(ct: ct).ConfigureAwait(false);
        if (workspaces.Count == 0)
            return null;

        var currentComputer = Environment.MachineName;
        var currentAccountHint = ExtractAccountHint(currentComputer);

        var exactComputerMatches = workspaces
            .Where(ws => string.Equals(ws.Computer, currentComputer, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var exactComputerUser = UniqueOwnerFromWorkspaces(exactComputerMatches);
        if (exactComputerUser is not null)
            return exactComputerUser;

        if (!string.IsNullOrWhiteSpace(currentAccountHint))
        {
            var accountMatches = workspaces
                .Where(ws =>
                    (!string.IsNullOrWhiteSpace(ws.Computer) && ws.Computer.Contains(currentAccountHint, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(ws.OwnerDisplay) && ws.OwnerDisplay.Contains(currentAccountHint, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var accountUser = UniqueOwnerFromWorkspaces(accountMatches);
            if (accountUser is not null)
                return accountUser;
        }

        if (!string.IsNullOrWhiteSpace(_config.Username))
        {
            var usernameMatches = workspaces
                .Where(ws =>
                    ws.Owner.Contains(_config.Username, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(ws.OwnerDisplay) && ws.OwnerDisplay.Contains(_config.Username, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var usernameUser = UniqueOwnerFromWorkspaces(usernameMatches);
            if (usernameUser is not null)
                return usernameUser;
        }

        if (!string.IsNullOrWhiteSpace(_config.UserDisplayName))
        {
            var displayMatches = workspaces
                .Where(ws => !string.IsNullOrWhiteSpace(ws.OwnerDisplay)
                    && ws.OwnerDisplay.Contains(_config.UserDisplayName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var displayUser = UniqueOwnerFromWorkspaces(displayMatches);
            if (displayUser is not null)
                return displayUser;
        }

        return UniqueOwnerFromWorkspaces(workspaces);
    }

    private static AuthenticatedTfsUser? UniqueOwnerFromWorkspaces(IReadOnlyList<SoapWorkspace> workspaces)
    {
        if (workspaces.Count == 0)
            return null;

        var owners = workspaces
            .Where(ws => !string.IsNullOrWhiteSpace(ws.Owner))
            .GroupBy(ws => ws.Owner, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (owners.Count != 1)
            return null;

        var exemplar = owners[0].First();
        return new AuthenticatedTfsUser(exemplar.Owner, exemplar.OwnerDisplay, ExtractAccountHint(exemplar.Computer));
    }

    private static bool TryReadAuthenticatedUser(
        JsonElement root,
        string propertyName,
        out AuthenticatedTfsUser? user)
    {
        user = null;
        if (!root.TryGetProperty(propertyName, out var userElement)
            || userElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!userElement.TryGetProperty("id", out var idElement))
            return false;

        var id = idElement.GetString();
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var displayName = userElement.TryGetProperty("providerDisplayName", out var displayElement)
            ? displayElement.GetString()
            : null;

        string? account = null;
        if (userElement.TryGetProperty("properties", out var propertiesElement)
            && propertiesElement.ValueKind == JsonValueKind.Object
            && propertiesElement.TryGetProperty("Account", out var accountElement)
            && accountElement.ValueKind == JsonValueKind.Object
            && accountElement.TryGetProperty("$value", out var accountValueElement))
        {
            account = accountValueElement.GetString();
        }

        user = new AuthenticatedTfsUser(id, displayName, account);
        return true;
    }

    private static string? ExtractAccountHint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = Regex.Match(value, @"\d{8}");
        return match.Success ? match.Value : null;
    }

    private static bool LooksLikeOwnerIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return Guid.TryParse(value, out _)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('@', StringComparison.Ordinal);
    }

    /// <summary>
    /// 创建一个带有 TFS 认证头的 <see cref="HttpClient"/>，用于 SOAP 或其他非 REST 调用。
    /// </summary>
    public virtual HttpClient CreateHttpClient()
    {
        if (_httpClient is not null)
            return _httpClient;

        lock (_httpClientGate)
        {
            if (_httpClient is not null)
                return _httpClient;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            };
            var client = new HttpClient(handler)
            {
                // We always pass explicit CancellationToken values from the command layer.
                // Disabling HttpClient's implicit timeout avoids spawning short-lived timer-queue
                // work for every SOAP process, which has been intermittently crashing on macOS
                // under rapid branch/history refresh pressure.
                Timeout = Timeout.InfiniteTimeSpan,
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            if (!string.IsNullOrEmpty(_config.PersonalAccessToken))
            {
                var token = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{_config.PersonalAccessToken}"));
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
            }
            else if (!string.IsNullOrEmpty(_config.Username))
            {
                var token = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{_config.Username}:{_config.Password}"));
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
            }

            _httpClient = client;
            return _httpClient;
        }
    }

    public TfvcHttpClient GetTfvcClient()
    {
        if (_tfvcClient is not null)
            return _tfvcClient;

        lock (_restClientGate)
        {
            if (_tfvcClient is not null)
                return _tfvcClient;

            _restConnection ??= CreateRestConnection();
            _tfvcClient = _restConnection.GetClient<TfvcHttpClient>();
            return _tfvcClient;
        }
    }

    private VssConnection CreateRestConnection()
    {
        var serverUri = new Uri(ServerUrl);
        VssCredentials credentials;

        if (!string.IsNullOrEmpty(_config.PersonalAccessToken))
        {
            credentials = new VssBasicCredential(string.Empty, _config.PersonalAccessToken);
        }
        else if (!string.IsNullOrEmpty(_config.Username))
        {
            credentials = new VssBasicCredential(_config.Username, _config.Password ?? string.Empty);
        }
        else
        {
            throw new InvalidOperationException(
                "No credentials configured. Run 'arm-tfs configure --pat <token>' or 'arm-tfs configure --username <user> --password <pass>'.");
        }

        return new VssConnection(serverUri, credentials);
    }

    public void Dispose()
    {
        _restConnection?.Dispose();
    }

    public sealed record AuthenticatedTfsUser(string Id, string? DisplayName, string? Account);
}
