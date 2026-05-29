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
}
