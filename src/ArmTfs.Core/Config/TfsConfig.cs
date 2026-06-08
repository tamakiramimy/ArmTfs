using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArmTfs.Core.Config;

/// <summary>
/// 用户级全局配置，序列化到 <c>~/.arm-tfs/config.json</c>。
/// 运行时可通过环境变量覆盖，优先级：环境变量 &gt; 配置文件。
/// </summary>
public sealed class TfsConfig
{
    /// <summary>TFS/Azure DevOps Server Collection URL（如 https://tfs.example.com/DefaultCollection）</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Personal Access Token（跨平台首选认证方式）</summary>
    public string? PersonalAccessToken { get; set; }

    /// <summary>显示名称，用于 Changeset 的 Owner 字段（可为空，服务器会利用 Token 关联的账户）</summary>
    public string? UserDisplayName { get; set; }

    /// <summary>
    /// Basic Auth 用户名（DOMAIN\user 或 user@domain 格式）。
    /// 优先使用 PAT；仅在服务器支持 Basic Auth 且无法申请 PAT 时使用。
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Basic Auth 密码。建议优先使用 PAT；Basic Auth 明文密码在网络传输中存在风险，必须配合 HTTPS。
    /// </summary>
    public string? Password { get; set; }

    /// <summary>默认项目名（查询时可省略 project 参数）</summary>
    public string? DefaultProject { get; set; }

    /// <summary>输出格式：plain（默认）或 json</summary>
    public string OutputFormat { get; set; } = "plain";

    [JsonIgnore]
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 默认配置文件路径（<c>~/.arm-tfs/config.json</c>）。
    /// 在不同 OS 上均能正确解析 UserProfile 目录。
    /// </summary>
    [JsonIgnore]
    public static string DefaultConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".arm-tfs", "config.json");

    /// <summary>
    /// 从指定路径读取配置。如果文件不存在则返回默认空配置对象。
    /// </summary>
    /// <param name="path">配置文件路径；<c>null</c> 表示使用 <see cref="DefaultConfigPath"/></param>
    public static TfsConfig Load(string? path = null)
    {
        var filePath = path ?? DefaultConfigPath;
        if (!File.Exists(filePath))
            return new TfsConfig();

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<TfsConfig>(json, _jsonOptions) ?? new TfsConfig();
    }

    /// <summary>
    /// 将当前配置保存到磁盘，自动创建上级目录。
    /// </summary>
    /// <param name="path">目标路径；<c>null</c> 表示使用 <see cref="DefaultConfigPath"/></param>
    public void Save(string? path = null)
    {
        var filePath = path ?? DefaultConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, JsonSerializer.Serialize(this, _jsonOptions));
    }

    /// <summary>
    /// 将当前进程中可用的认证相关环境变量覆盖到对应属性。
    /// 环境变量的优先级高于配置文件，便于 CI/CD 和容器场景使用。
    /// </summary>
    public void ApplyEnvironmentOverrides()
    {
        var envUrl = Environment.GetEnvironmentVariable("ARM_TFS_URL");
        if (!string.IsNullOrEmpty(envUrl)) ServerUrl = envUrl;

        var envPat = Environment.GetEnvironmentVariable("ARM_TFS_PAT");
        if (!string.IsNullOrEmpty(envPat)) PersonalAccessToken = envPat;

        var envDisplayName = Environment.GetEnvironmentVariable("ARM_TFS_DISPLAY_NAME");
        if (!string.IsNullOrEmpty(envDisplayName)) UserDisplayName = envDisplayName;

        var envUser = Environment.GetEnvironmentVariable("ARM_TFS_USER");
        if (!string.IsNullOrEmpty(envUser)) Username = envUser;

        var envPass = Environment.GetEnvironmentVariable("ARM_TFS_PASSWORD");
        if (!string.IsNullOrEmpty(envPass)) Password = envPass;
    }
}
