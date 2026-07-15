using System.Reflection;
using ArmTfs.Core.Models;
using ArmTfs.Core.Workspace;

namespace ArmTfs.Core.Tests;

/// <summary>
/// 跨平台路径处理测试。验证 macOS arm64 与 Windows 11 arm64 之间的路径互操作性。
/// 这些测试确保由一端写入的 workspace.json / 版本追踪文件能被另一端正确读取。
/// </summary>
public class CrossPlatformPathTests
{
    // ─── 路径分隔符一致性 ──────────────────────────────────────────────────

    [Theory]
    [InlineData("C:\\Users\\foo\\project", '/')]
    [InlineData("C:/Users/foo/project", '\\')]
    [InlineData("/Users/foo/project", '\\')]
    public void NormalizeBackslashes_ConvertsAllSeparatorsToForwardSlash(string input, char fromSeparator)
    {
        // 确认 Replace 能处理所有分隔符实例
        var normalized = input.Replace(fromSeparator, '/');
        Assert.DoesNotContain(fromSeparator, normalized);
    }

    [Fact]
    public void ServerPath_AlwaysUsesForwardSlash()
    {
        // TFVC 服务器路径使用 / 分隔符，无论平台
        var serverPath = "$/Project/Main/src/file.cs";
        Assert.All(serverPath, c => Assert.NotEqual('\\', c));
    }

    // ─── Windows 驱动器号路径检测 ──────────────────────────────────────────

    [Theory]
    [InlineData("C:\\Users\\foo", true)]
    [InlineData("C:/Users/foo", true)]
    [InlineData("D:\\ArmTFS\\arm-tfs.exe", true)]
    [InlineData("D:/ArmTFS/arm-tfs.exe", true)]
    [InlineData("/Users/foo", false)]
    [InlineData("relative/path", false)]
    [InlineData("relative\\path", false)]
    [InlineData("C:", false)]            // 太短
    [InlineData("C\\Users", false)]       // 缺少冒号
    [InlineData("", false)]
    public void IsWindowsDrivePath_DetectsValidDrivePaths(string path, bool expected)
    {
        var result = InvokeIsWindowsDrivePath(path);
        Assert.Equal(expected, result);
    }

    // ─── 跨平台工作区元数据读写 ──────────────────────────────────────────────

    [Fact]
    public void WorkspaceMetadata_RoundTripsAcrossPlatformSeparators()
    {
        // 模拟场景：Windows 端创建工作区，macOS 端读取
        var tempDir = Path.Combine(Path.GetTempPath(), "arm-tfs-xplat-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            // 模拟 Windows 端写入的映射路径（使用反斜杠）
            var windowsStyleLocalPath = tempDir.Replace('/', '\\');
            var ws = new WorkspaceManager(tempDir);
            var meta = new WorkspaceMetadata
            {
                Name = "XPlatWS",
                ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
                Mappings = new List<WorkspaceMapping>
                {
                    new() { ServerPath = "$/Project/Main", LocalPath = windowsStyleLocalPath }
                }
            };
            ws.SaveMetadata(meta);

            // 读取回来并验证 LocalToServerPath 仍然能正确映射
            var loaded = ws.LoadMetadata();
            var localFile = Path.Combine(tempDir, "src", "file.cs");
            var serverPath = ws.LocalToServerPath(localFile, loaded);

            Assert.Equal("$/Project/Main/src/file.cs", serverPath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ServerToLocalPath_ProducesNativeSeparator()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "arm-tfs-sep-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var ws = new WorkspaceManager(tempDir);
            var meta = new WorkspaceMetadata
            {
                Name = "WS", ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
                Mappings = new List<WorkspaceMapping>
                {
                    new() { ServerPath = "$/Project/Main", LocalPath = tempDir }
                }
            };

            var localPath = ws.ServerToLocalPath("$/Project/Main/deep/file.cs", meta);
            Assert.NotNull(localPath);
            // 返回的路径必须使用当前平台的分隔符
            var expectedSuffix = $"deep{Path.DirectorySeparatorChar}file.cs";
            Assert.EndsWith(expectedSuffix, localPath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    // ─── 跨平台版本追踪文件 ──────────────────────────────────────────────────

    [Fact]
    public void TrackedVersion_SurvivesCrossPlatformReadWrite()
    {
        // 场景：Windows 端 checkout 文件并记录版本，macOS 端读取并查找
        var tempDir = Path.Combine(Path.GetTempPath(), "arm-tfs-track-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        try
        {
            var ws = new WorkspaceManager(tempDir);
            var meta = new WorkspaceMetadata
            {
                Name = "WS", ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
                Mappings = new List<WorkspaceMapping>
                {
                    new() { ServerPath = "$/Project", LocalPath = tempDir }
                }
            };
            ws.SaveMetadata(meta);

            // 用与平台不同的分隔符写入版本追踪（模拟另一端写入）
            var altSeparator = Path.DirectorySeparatorChar == '\\' ? '/' : '\\';
            var altLocalPath = tempDir.Replace(Path.DirectorySeparatorChar, altSeparator)
                + altSeparator + "src" + altSeparator + "file.cs";

            ws.SaveTrackedVersion(new TrackedFileVersion
            {
                ServerPath = "$/Project/src/file.cs",
                LocalPath = altLocalPath,
                ChangesetId = 42,
                ContentHash = "abc",
            });

            // 当前平台查找该文件应能通过 serverPath fallback 找到
            var currentLocalFile = Path.Combine(tempDir, "src", "file.cs");
            File.WriteAllText(currentLocalFile, "// content");

            var loaded = ws.GetTrackedVersion(currentLocalFile);
            Assert.NotNull(loaded);
            Assert.Equal(42, loaded!.ChangesetId);
            Assert.Equal(currentLocalFile, loaded.LocalPath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static bool InvokeIsWindowsDrivePath(string path)
    {
        var method = typeof(WorkspaceManager).GetMethod("IsWindowsDrivePath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object[] { path })!;
    }
}
