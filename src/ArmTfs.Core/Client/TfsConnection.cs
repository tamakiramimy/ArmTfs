using ArmTfs.Core.Config;

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
    /// 通过 SOAP QueryWorkspaces 获取当前认证用户的身份标识（Owner 字段）。
    /// SOAP CreateWorkspace 要求 OwnerName 与认证用户匹配；从已有 workspace 的
    /// Owner 属性提取最可靠。若无工作区则退回到配置的 DisplayName / Username。
    /// 纯 SOAP，无 REST 依赖。
    /// </summary>
    public async Task<string?> GetAuthenticatedUserGuidAsync(CancellationToken ct = default)
    {
        try
        {
            var soap = new Soap.TfvcSoapClient(this);
            var workspaces = await soap.QueryWorkspacesAsync(ct: ct).ConfigureAwait(false);
            var owner = workspaces.FirstOrDefault()?.Owner;
            if (!string.IsNullOrWhiteSpace(owner))
                return owner;
            // Fallback: use configured display name or username when no workspaces exist yet.
            if (!string.IsNullOrWhiteSpace(_config.UserDisplayName))
                return _config.UserDisplayName;
            if (!string.IsNullOrWhiteSpace(_config.Username))
                return _config.Username;
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
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler);
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

    public void Dispose() { }
}
