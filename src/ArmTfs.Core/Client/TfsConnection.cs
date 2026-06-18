using ArmTfs.Core.Config;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace ArmTfs.Core.Client;

/// <summary>
/// 与 TFS/Azure DevOps Server 建立 REST 连接，封装认证逻辑。
/// <para>
/// <b>跨平台认证策略（按优先级）：</b>
/// <list type="number">
///   <item>PAT 认证 — 跨平台首选，ARM64 macOS / Windows ARM / Linux 均支持</item>
///   <item>Basic Auth（用户名+密码）— 适用于服务器已开启 Basic Auth 且配合 HTTPS 的场景</item>
/// </list>
/// 此类有意不包含 Windows 集成认证（NTLM/Kerberos）：
/// 相关 API（<c>WindowsCredential</c>/<c>VssFederatedCredential</c>）仅存在于
/// <c>Microsoft.VisualStudio.Services.Client</c> 包中，该包没有 netstandard2.0 目标，無法在非 Windows 平台编译。
/// </para>
/// </summary>
public class TfsConnection : IDisposable
{
    private VssConnection? _connection;
    private TfvcHttpClient? _tfvcClient;
    private readonly TfsConfig _config;

    /// <summary>初始化连接对象。不会立即建立网络连接，延迟到首次调用 <see cref="GetTfvcClient"/> 时。</summary>
    /// <param name="config">已加载并应用环境变量覆盖的配置对象</param>
    public TfsConnection(TfsConfig config)
    {
        _config = config;
    }

    /// <summary>TFS Server Collection URL，未配置时抛出 <see cref="InvalidOperationException"/></summary>
    public string ServerUrl => _config.ServerUrl
        ?? throw new InvalidOperationException("TFS server URL is not configured. Run 'arm-tfs configure' first.");

    /// <summary>
    /// 惰性建立 <see cref="VssConnection"/>，根据配置选择最适合的凭据类型。
    /// </summary>
    /// <exception cref="InvalidOperationException">无任何凭据时抛出</exception>
    private VssConnection EnsureConnection()
    {
        if (_connection is not null) return _connection;

        var serverUri = new Uri(ServerUrl);
        VssCredentials credentials;

        if (!string.IsNullOrEmpty(_config.PersonalAccessToken))
        {
            // PAT 认证：跨平台首选（ARM64 macOS / Windows ARM 均支持）
            credentials = new VssBasicCredential(string.Empty, _config.PersonalAccessToken);
        }
        else if (!string.IsNullOrEmpty(_config.Username))
        {
            // 用户名/密码 Basic 认证（需服务器开启 Basic Auth 或使用 HTTPS）
            credentials = new VssBasicCredential(_config.Username, _config.Password ?? string.Empty);
        }
        else
        {
            throw new InvalidOperationException(
                "No credentials configured. Run 'arm-tfs configure --pat <token>' or 'arm-tfs configure --username <user> --password <pass>'.");
        }

        _connection = new VssConnection(serverUri, credentials);
        return _connection;
    }

    /// <summary>
    /// 获取 <see cref="TfvcHttpClient"/> 实例，内部缓存，多次调用安全。
    /// </summary>
    public TfvcHttpClient GetTfvcClient()
    {
        if (_tfvcClient is not null) return _tfvcClient;
        _tfvcClient = EnsureConnection().GetClient<TfvcHttpClient>();
        return _tfvcClient;
    }

    /// <summary>
    /// 发送一个轻量级请求（获取分支列表）以验证凭据和连通性。
    /// 在执行实际操作前可调用此方法提前失败。
    /// </summary>
    /// <exception cref="Microsoft.VisualStudio.Services.Common.VssUnauthorizedException">凭据错误时</exception>
    public async Task TestConnectionAsync(CancellationToken ct = default)
    {
        var client = GetTfvcClient();
        // 用最小请求验证连接是否可用
        await client.GetBranchesAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 查询当前认证用户的身份 GUID（REST <c>_apis/connectionData</c>）。
    /// SOAP CreateWorkspace 要求 OwnerName = 认证用户身份；用 GUID 最稳定。
    /// 解析失败返回 null。
    /// </summary>
    public async Task<string?> GetAuthenticatedUserGuidAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = CreateHttpClient();
            var url = ServerUrl.TrimEnd('/') + "/_apis/connectionData?api-version=5.0-preview";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("authenticatedUser", out var user)
                && user.TryGetProperty("id", out var id))
            {
                return id.GetString();
            }
        }
        catch
        {
            // Ignore — caller will surface a clear error if owner cannot be resolved.
        }
        return null;
    }

    /// <summary>
    /// 创建一个带有 TFS 认证头的 <see cref="HttpClient"/>，用于 SOAP 或其他非 REST 调用。
    /// </summary>
    public virtual HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
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
        return client;
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
