using ArmTfs.Core.Models;
using ArmTfs.Core.Workspace;

namespace ArmTfs.Core.Tests;

public class WorkspaceManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkspaceManager _ws;

    public WorkspaceManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "arm-tfs-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _ws = new WorkspaceManager(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ─── Workspace metadata ────────────────────────────────────────────────────

    [Fact]
    public void SaveAndLoad_WorkspaceMetadata_RoundTrips()
    {
        var meta = new WorkspaceMetadata
        {
            Name = "TestWS",
            ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
            Mappings = new List<WorkspaceMapping>
            {
                new() { ServerPath = "$/Project/Main", LocalPath = _tempDir }
            }
        };

        _ws.SaveMetadata(meta);
        var loaded = _ws.LoadMetadata();

        Assert.Equal("TestWS", loaded.Name);
        Assert.Equal("https://tfs.example.com/tfs/DefaultCollection", loaded.ServerCollectionUrl);
        Assert.Single(loaded.Mappings);
        Assert.Equal("$/Project/Main", loaded.Mappings[0].ServerPath);
    }

    [Fact]
    public void SaveMetadata_CreatesGitIgnoreEntry()
    {
        var meta = new WorkspaceMetadata
        {
            Name = "WS",
            ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
        };
        _ws.SaveMetadata(meta);

        var gitIgnore = Path.Combine(_tempDir, ".gitignore");
        Assert.True(File.Exists(gitIgnore));
        Assert.Contains(".tf/", File.ReadAllText(gitIgnore));
    }

    [Fact]
    public void FindWorkspace_FindsWorkspaceInParentDirectory()
    {
        var meta = new WorkspaceMetadata
        {
            Name = "WS",
            ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
        };
        _ws.SaveMetadata(meta);

        var subDir = Path.Combine(_tempDir, "sub", "sub2");
        Directory.CreateDirectory(subDir);

        var found = WorkspaceManager.FindWorkspace(subDir);
        Assert.NotNull(found);
    }

    [Fact]
    public void FindWorkspace_ReturnsNull_WhenNotFound()
    {
        // Use a temp dir with no .tf directory
        var emptyDir = Path.Combine(Path.GetTempPath(), "arm-tfs-no-ws-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(emptyDir);
        try
        {
            var found = WorkspaceManager.FindWorkspace(emptyDir);
            Assert.Null(found);
        }
        finally
        {
            Directory.Delete(emptyDir);
        }
    }

    // ─── Pending changes ───────────────────────────────────────────────────────

    [Fact]
    public void AddAndLoad_PendingChange_RoundTrips()
    {
        var meta = CreateTestWorkspace();
        var filePath = Path.Combine(_tempDir, "src", "MyFile.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "// test");

        _ws.AddPendingChange(new PendingChange
        {
            ServerPath = "$/Project/Main/src/MyFile.cs",
            LocalPath = filePath,
            ChangeType = ChangeType.Edit,
        });

        var pending = _ws.LoadPendingChanges();
        Assert.Single(pending);
        Assert.Equal(ChangeType.Edit, pending[0].ChangeType);
        Assert.Equal(filePath, pending[0].LocalPath);
    }

    [Fact]
    public void AddPendingChange_ReplacesExistingForSameFile()
    {
        var meta = CreateTestWorkspace();
        var filePath = Path.Combine(_tempDir, "MyFile.cs");
        File.WriteAllText(filePath, "// test");

        _ws.AddPendingChange(new PendingChange { ServerPath = "$/P/MyFile.cs", LocalPath = filePath, ChangeType = ChangeType.Edit });
        _ws.AddPendingChange(new PendingChange { ServerPath = "$/P/MyFile.cs", LocalPath = filePath, ChangeType = ChangeType.Delete });

        var pending = _ws.LoadPendingChanges();
        Assert.Single(pending);
        Assert.Equal(ChangeType.Delete, pending[0].ChangeType);
    }

    [Fact]
    public void RemovePendingChange_RemovesCorrectEntry()
    {
        var meta = CreateTestWorkspace();
        var file1 = Path.Combine(_tempDir, "A.cs");
        var file2 = Path.Combine(_tempDir, "B.cs");
        File.WriteAllText(file1, "a"); File.WriteAllText(file2, "b");

        _ws.AddPendingChange(new PendingChange { ServerPath = "$/P/A.cs", LocalPath = file1, ChangeType = ChangeType.Edit });
        _ws.AddPendingChange(new PendingChange { ServerPath = "$/P/B.cs", LocalPath = file2, ChangeType = ChangeType.Add });

        _ws.RemovePendingChange(file1);

        var pending = _ws.LoadPendingChanges();
        Assert.Single(pending);
        Assert.Equal(file2, pending[0].LocalPath);
    }

    // ─── Path mapping ──────────────────────────────────────────────────────────

    [Fact]
    public void LocalToServerPath_ConvertsCorrectly()
    {
        var meta = new WorkspaceMetadata
        {
            Name = "WS", ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
            Mappings = new List<WorkspaceMapping>
            {
                new() { ServerPath = "$/Project/Main", LocalPath = _tempDir }
            }
        };
        _ws.SaveMetadata(meta);

        var localFile = Path.Combine(_tempDir, "src", "MyFile.cs");
        var serverPath = _ws.LocalToServerPath(localFile, meta);

        Assert.Equal("$/Project/Main/src/MyFile.cs", serverPath);
    }

    [Fact]
    public void ServerToLocalPath_ConvertsCorrectly()
    {
        var meta = new WorkspaceMetadata
        {
            Name = "WS", ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
            Mappings = new List<WorkspaceMapping>
            {
                new() { ServerPath = "$/Project/Main", LocalPath = _tempDir }
            }
        };

        var serverPath = "$/Project/Main/src/MyFile.cs";
        var localPath = _ws.ServerToLocalPath(serverPath, meta);
        var expected = Path.Combine(_tempDir, "src", "MyFile.cs");

        Assert.Equal(expected, localPath);
    }

    [Fact]
    public void ServerToLocalPath_PrefersMappingUnderCurrentWorkspaceRoot_WhenServerPathIsDuplicated()
    {
        var macLikeMapping = Path.Combine(_tempDir, "Project");
        Directory.CreateDirectory(macLikeMapping);
        var meta = new WorkspaceMetadata
        {
            Name = "WS", ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
            Mappings = new List<WorkspaceMapping>
            {
                new() { ServerPath = "$/Project/Main", LocalPath = @"D:\TFS\Project\Main" },
                new() { ServerPath = "$/Project/Main", LocalPath = macLikeMapping }
            }
        };

        var localPath = _ws.ServerToLocalPath("$/Project/Main/src/MyFile.cs", meta);

        Assert.Equal(Path.Combine(macLikeMapping, "src", "MyFile.cs"), localPath);
    }

    [Fact]
    public void LocalToServerPath_ReturnsNull_WhenNoMapping()
    {
        var meta = new WorkspaceMetadata
        {
            Name = "WS", ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
            Mappings = new List<WorkspaceMapping>
            {
                new() { ServerPath = "$/Project/Main", LocalPath = _tempDir }
            }
        };

        var outsideFile = Path.Combine(Path.GetTempPath(), "other", "file.cs");
        Assert.Null(_ws.LocalToServerPath(outsideFile, meta));
    }

    // ─── File version tracking ─────────────────────────────────────────────────

    [Fact]
    public void SaveAndGetTrackedVersion_RoundTrips()
    {
        var meta = CreateTestWorkspace();
        var localFile = Path.Combine(_tempDir, "tracked.cs");
        File.WriteAllText(localFile, "// hello");

        var version = new TrackedFileVersion
        {
            ServerPath = "$/P/tracked.cs",
            LocalPath = localFile,
            ChangesetId = 42,
            ContentHash = "abc123",
        };
        _ws.SaveTrackedVersion(version);

        var loaded = _ws.GetTrackedVersion(localFile);
        Assert.NotNull(loaded);
        Assert.Equal(42, loaded!.ChangesetId);
        Assert.Equal("abc123", loaded.ContentHash);
    }

    [Fact]
    public void GetTrackedVersion_FallsBackToServerPath_WhenLocalPathWasWrittenOnAnotherPlatform()
    {
        var meta = CreateTestWorkspace();
        var currentLocalFile = Path.Combine(_tempDir, "src", "tracked.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(currentLocalFile)!);
        File.WriteAllText(currentLocalFile, "// hello");

        _ws.SaveTrackedVersion(new TrackedFileVersion
        {
            ServerPath = "$/P/src/tracked.cs",
            LocalPath = @"D:\TFS\P\src\tracked.cs",
            ChangesetId = 108,
            ContentHash = "abc123",
        });

        var loaded = _ws.GetTrackedVersion(currentLocalFile);

        Assert.NotNull(loaded);
        Assert.Equal(108, loaded!.ChangesetId);
        Assert.Equal(currentLocalFile, loaded.LocalPath);
    }

    [Fact]
    public void SaveBaseFile_AndGetCachedBaseFilePath_RoundTrips()
    {
        var localFile = Path.Combine(_tempDir, "tracked.cs");
        _ws.SaveBaseFile(localFile, "base content"u8.ToArray());

        var cachedPath = _ws.GetCachedBaseFilePath(localFile);

        Assert.NotNull(cachedPath);
        Assert.True(File.Exists(cachedPath));
        Assert.Equal("base content", File.ReadAllText(cachedPath!));
    }

    [Fact]
    public void SaveBaseFileFromDisk_AndRemoveBaseFile_WorkCorrectly()
    {
        var localFile = Path.Combine(_tempDir, "folder", "tracked.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
        File.WriteAllText(localFile, "disk content");

        _ws.SaveBaseFileFromDisk(localFile);
        var cachedPath = _ws.GetCachedBaseFilePath(localFile);

        Assert.NotNull(cachedPath);
        Assert.Equal("disk content", File.ReadAllText(cachedPath!));

        _ws.RemoveBaseFile(localFile);
        Assert.Null(_ws.GetCachedBaseFilePath(localFile));
    }

    [Fact]
    public void ComputeFileHash_IsDeterministic()
    {
        var file = Path.Combine(_tempDir, "hash_test.txt");
        File.WriteAllText(file, "hello world");

        var h1 = WorkspaceManager.ComputeFileHash(file);
        var h2 = WorkspaceManager.ComputeFileHash(file);

        Assert.Equal(h1, h2);
        Assert.NotEmpty(h1);
    }

    // ─── 跨平台路径处理 ──────────────────────────────────────────────────────

    [Fact]
    public void NormalizeLocalPath_HandlesMixedSeparators()
    {
        // 路径混合使用 / 和 \ 分隔符时应能正确解析（Windows 上 GetFullPath 会规范化）
        var mixed = _tempDir.Replace(Path.DirectorySeparatorChar, '/') + "/sub/file.cs";
        var normalized = Path.GetFullPath(mixed);
        Assert.Equal(Path.Combine(_tempDir, "sub", "file.cs"), normalized);
    }

    [Fact]
    public void LocalToServerPath_HandlesBackwardSlashesInMapping()
    {
        // 工作区映射中的本地路径若包含反斜杠，在 macOS 上应仍能匹配由 Windows 端写入的路径
        var mappingLocal = _tempDir.Replace('/', Path.DirectorySeparatorChar);
        var meta = new WorkspaceMetadata
        {
            Name = "WS", ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
            Mappings = new List<WorkspaceMapping>
            {
                new() { ServerPath = "$/Project/Main", LocalPath = mappingLocal }
            }
        };
        _ws.SaveMetadata(meta);

        var localFile = Path.Combine(_tempDir, "src", "MyFile.cs");
        var serverPath = _ws.LocalToServerPath(localFile, meta);

        Assert.Equal("$/Project/Main/src/MyFile.cs", serverPath);
    }

    [Fact]
    public void ServerToLocalPath_ProducesPlatformNativeSeparators()
    {
        var meta = new WorkspaceMetadata
        {
            Name = "WS", ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
            Mappings = new List<WorkspaceMapping>
            {
                new() { ServerPath = "$/Project/Main", LocalPath = _tempDir }
            }
        };

        var localPath = _ws.ServerToLocalPath("$/Project/Main/src/MyFile.cs", meta);
        var expected = Path.Combine(_tempDir, "src", "MyFile.cs");

        Assert.Equal(expected, localPath);
        // 不论平台，本地路径必须使用当前平台的分隔符
        Assert.DoesNotContain(localPath!.Replace(_tempDir, ""), "/");
    }

    [Fact]
    public void GetTrackedVersion_FindsByServerPath_WhenLocalPathUsesDifferentSeparator()
    {
        // 模拟跨平台场景：版本追踪文件由 Windows 端写入（使用 \），由 macOS 端读取（使用 /）
        var meta = CreateTestWorkspace();
        var currentLocalFile = Path.Combine(_tempDir, "src", "tracked.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(currentLocalFile)!);
        File.WriteAllText(currentLocalFile, "// hello");

        // 使用与当前平台相反的分隔符写入版本文件
        var altSeparator = Path.DirectorySeparatorChar == '\\' ? '/' : '\\';
        var altLocalPath = _tempDir.Replace(Path.DirectorySeparatorChar, altSeparator)
                            + altSeparator + "src" + altSeparator + "tracked.cs";

        _ws.SaveTrackedVersion(new TrackedFileVersion
        {
            ServerPath = "$/P/src/tracked.cs",
            LocalPath = altLocalPath,
            ChangesetId = 99,
            ContentHash = "deadbeef",
        });

        var loaded = _ws.GetTrackedVersion(currentLocalFile);

        Assert.NotNull(loaded);
        Assert.Equal(99, loaded!.ChangesetId);
        Assert.Equal(currentLocalFile, loaded.LocalPath);
    }

    [Theory]
    [InlineData("$/Project/Main", "$/Project/Main/src/file.cs", "src/file.cs")]
    [InlineData("$/Project/Main", "$/Project/Main", "")]
    [InlineData("$/Project/Main/", "$/Project/Main/src/file.cs", "src/file.cs")]
    public void LocalToServerPath_HandlesTrailingSlashInMapping(string serverPath, string fullServerPath, string expectedRelative)
    {
        var meta = new WorkspaceMetadata
        {
            Name = "WS", ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
            Mappings = new List<WorkspaceMapping>
            {
                new() { ServerPath = serverPath, LocalPath = _tempDir }
            }
        };
        _ws.SaveMetadata(meta);

        var localFile = string.IsNullOrEmpty(expectedRelative)
            ? _tempDir
            : Path.Combine(_tempDir, expectedRelative.Replace('/', Path.DirectorySeparatorChar));
        var result = _ws.LocalToServerPath(localFile, meta);

        Assert.Equal(fullServerPath, result);
    }

    [Fact]
    public void IsWindowsDrivePath_DetectsBothSeparators()
    {
        // Drive-letter paths should be detected regardless of separator
        Assert.True(IsWindowsDrivePathProxy(@"C:\Users\foo"));
        Assert.True(IsWindowsDrivePathProxy(@"C:/Users/foo"));
        Assert.True(IsWindowsDrivePathProxy(@"D:\ArmTFS"));
        Assert.False(IsWindowsDrivePathProxy(@"/Users/foo"));
        Assert.False(IsWindowsDrivePathProxy(@"relative\path"));
    }

    [Fact]
    public void FindWorkspace_WorksAcrossMixedSeparators()
    {
        // 创建工作区后，用混合分隔符的子路径查找应仍能找到
        var meta = new WorkspaceMetadata
        {
            Name = "WS", ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
        };
        _ws.SaveMetadata(meta);

        var mixedSubDir = (_tempDir.Replace(Path.DirectorySeparatorChar, '/') + "/sub/deep").Replace('/', Path.DirectorySeparatorChar);
        Directory.CreateDirectory(mixedSubDir);

        var found = WorkspaceManager.FindWorkspace(mixedSubDir);
        Assert.NotNull(found);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private WorkspaceMetadata CreateTestWorkspace()
    {
        var meta = new WorkspaceMetadata
        {
            Name = "WS", ServerCollectionUrl = "https://tfs.example.com/tfs/DefaultCollection",
            Mappings = new List<WorkspaceMapping>
            {
                new() { ServerPath = "$/P", LocalPath = _tempDir }
            }
        };
        _ws.SaveMetadata(meta);
        return meta;
    }

    /// <summary>
    /// 代理调用 WorkspaceManager 的私有 IsWindowsDrivePath 方法。通过反射访问以便测试。
    /// 该方法用于检测 Windows 驱动器号路径（如 C:\ 或 D:/），无论路径分隔符是什么。
    /// </summary>
    private static bool IsWindowsDrivePathProxy(string path)
    {
        var method = typeof(WorkspaceManager).GetMethod("IsWindowsDrivePath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object[] { path })!;
    }
}
