using System.IO;
using AutostartAudit.Core.Model;
using AutostartAudit.Core.Quarantine;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

public class StartupFolderQuarantineStrategyTests : IDisposable
{
    private readonly string _root;
    private readonly string _startupDir;
    private readonly string _quarantineRoot;

    public StartupFolderQuarantineStrategyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aa-quarantine-" + Guid.NewGuid().ToString("N"));
        _startupDir = Path.Combine(_root, "Startup");
        _quarantineRoot = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(_startupDir);
    }

    private AutoStartEntry FileEntry(string fileName, long size) => new()
    {
        SourceKind = StartupFolderSource.Kind,
        Scope = "user",
        StableKey = StableKey.Build(StartupFolderSource.Kind, "user", Path.Combine(_startupDir, fileName)),
        NativeKey = Path.Combine(_startupDir, fileName),
        DisplayName = fileName,
        TargetPaths = new[] { Path.Combine(_startupDir, fileName) },
        RawValueSnapshot = $"{Path.Combine(_startupDir, fileName)} (size={size})",
        Signing = SigningStatus.Unverified,
        Observation = ObservationHealth.Ok,
    };

    [Fact]
    public void RoundTrip_MoveAndMoveBack_PreservesContentAndAttributes()
    {
        var file = Path.Combine(_startupDir, "launcher.exe");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 5 });
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        var strategy = new StartupFolderQuarantineStrategy(new SystemFileMutator(), _quarantineRoot);
        var plan = strategy.Plan(FileEntry("launcher.exe", 5));
        Assert.True(plan.CanQuarantine, plan.Reason);

        Assert.Equal(MutationStatus.Ok, strategy.Execute(plan.BeforeStateJson!).Status);
        Assert.False(File.Exists(file));
        var moved = Directory.GetFiles(_quarantineRoot, "launcher.exe", SearchOption.AllDirectories);
        Assert.Single(moved);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(moved[0]));

        Assert.Equal(MutationStatus.Ok, strategy.Restore(plan.BeforeStateJson!).Status);
        Assert.True(File.Exists(file));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(file));
        Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
        Assert.Empty(Directory.GetFiles(_quarantineRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void SizeDriftSinceScan_RefusesToQuarantine()
    {
        var file = Path.Combine(_startupDir, "launcher.exe");
        File.WriteAllBytes(file, new byte[] { 9, 9 }); // scan said size 500
        var strategy = new StartupFolderQuarantineStrategy(new SystemFileMutator(), _quarantineRoot);

        var plan = strategy.Plan(FileEntry("launcher.exe", 500));
        Assert.False(plan.CanQuarantine);
        Assert.Contains("size changed", plan.Reason);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void MissingFile_RefusesToQuarantine()
    {
        var strategy = new StartupFolderQuarantineStrategy(new SystemFileMutator(), _quarantineRoot);
        var plan = strategy.Plan(FileEntry("gone.exe", 10));
        Assert.False(plan.CanQuarantine);
        Assert.Contains("no longer present", plan.Reason);
    }

    [Fact]
    public void OriginalNameRespawnedBeforeRestore_RefusesToClobber()
    {
        var file = Path.Combine(_startupDir, "launcher.exe");
        File.WriteAllBytes(file, new byte[] { 1 });
        var strategy = new StartupFolderQuarantineStrategy(new SystemFileMutator(), _quarantineRoot);
        var plan = strategy.Plan(FileEntry("launcher.exe", 1));
        Assert.Equal(MutationStatus.Ok, strategy.Execute(plan.BeforeStateJson!).Status);

        // Something recreated the original path meanwhile.
        File.WriteAllText(file, "intruder");

        var restored = strategy.Restore(plan.BeforeStateJson!);
        Assert.Equal(MutationStatus.Failed, restored.Status);
        Assert.Contains("occupied", restored.Detail);
        Assert.Equal("intruder", File.ReadAllText(file));
    }

    [Fact]
    public void DeniedMove_ReportsDenied()
    {
        var file = Path.Combine(_startupDir, "launcher.exe");
        File.WriteAllBytes(file, new byte[] { 1 });
        var strategy = new StartupFolderQuarantineStrategy(new DeniedOnMoveMutator(), _quarantineRoot);
        var plan = strategy.Plan(FileEntry("launcher.exe", 1));
        Assert.True(plan.CanQuarantine, plan.Reason);
        var executed = strategy.Execute(plan.BeforeStateJson!);
        Assert.Equal(MutationStatus.Denied, executed.Status);
    }

    private sealed class DeniedOnMoveMutator : IFileSystemMutator
    {
        private readonly SystemFileMutator _inner = new();

        public void EnsureDirectory(string path) => _inner.EnsureDirectory(path);
        public long? TryGetLength(string path) => _inner.TryGetLength(path);
        public FileAttributes? TryGetAttributes(string path) => _inner.TryGetAttributes(path);
        public void SetAttributes(string path, FileAttributes attributes) => _inner.SetAttributes(path, attributes);
        public bool TryExclusiveMove(string source, string target) =>
            throw new FolderAccessDeniedException(source, "move denied (test)");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
