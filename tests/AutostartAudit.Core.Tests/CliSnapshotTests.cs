using System.Text.Json;
using AutostartAudit.Cli;
using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;
using AutostartAudit.Core.Store;

namespace AutostartAudit.Core.Tests;

public class CliSnapshotTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"aa-cli-snap-{Guid.NewGuid():N}.db");

    private static ScanDocument ScanWith(params AutoStartEntry[] entries) => new()
    {
        Entries = entries,
        Sources = new[] { new SourceReport { SourceKind = "run-key", Capability = SourceCapability.Scanned } },
        ScanComplete = true,
    };

    private (int Code, string Out, string Err) Run(Func<ScanDocument> scan, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = App.Run(args, o, e, scan, path => new SnapshotStore(path ?? _dbPath));
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void FirstSave_ReportsBaseline_NotAddedStorm()
    {
        var scan = ScanWith(SnapshotTestFixtures.Entry("run-key", "user", "A", "A", new[] { @"C:\a.exe" }));

        var (code, stdout, stderr) = Run(() => scan, "scan", "--save", "--store", _dbPath);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, stderr.Trim());
        Assert.Contains("baseline snapshot", stdout);
        Assert.Contains("No prior snapshot", stdout);
        Assert.DoesNotContain("added", stdout); // never an "everything is new" storm
    }

    [Fact]
    public void SecondSave_PrintsDiff_WithChangeReasons()
    {
        var first = ScanWith(SnapshotTestFixtures.Entry("run-key", "user", "A", "A", new[] { @"C:\a.exe" }));
        Run(() => first, "scan", "--save", "--store", _dbPath);

        var second = ScanWith(SnapshotTestFixtures.Entry("run-key", "user", "A", "A", new[] { @"C:\b.exe" }));
        var (code, stdout, _) = Run(() => second, "scan", "--save", "--store", _dbPath);

        Assert.Equal(0, code);
        Assert.Contains("snapshot #2 saved", stdout);
        Assert.Contains("0 added, 0 removed, 1 changed, 0 unchanged", stdout);
        Assert.Contains("target-path", stdout);
    }

    [Fact]
    public void SaveJson_EmitsValidCaptureEnvelope()
    {
        var first = ScanWith(SnapshotTestFixtures.Entry("run-key", "user", "A", "A", new[] { @"C:\a.exe" }));
        var (code, stdout, _) = Run(() => first, "scan", "--save", "--json", "--store", _dbPath);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout.Trim());
        Assert.True(doc.RootElement.GetProperty("isBaseline").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("snapshotId").GetInt64());
        Assert.False(doc.RootElement.TryGetProperty("diff", out _)); // null omitted
    }

    [Fact]
    public void ScanWithoutSave_DoesNotTouchStore()
    {
        var scan = ScanWith(SnapshotTestFixtures.Entry("run-key", "user", "A", "A", new[] { @"C:\a.exe" }));
        var (code, _, _) = Run(() => scan, "scan");

        Assert.Equal(0, code);
        using var store = new SnapshotStore(_dbPath);
        Assert.Null(store.GetLatest());
    }

    [Fact]
    public void StorePath_CreatesParentDirectory()
    {
        var nested = Path.Combine(Path.GetTempPath(), $"aa-nested-{Guid.NewGuid():N}", "deep", "snapshots.db");
        try
        {
            var scan = ScanWith(SnapshotTestFixtures.Entry("run-key", "user", "A", "A", new[] { @"C:\a.exe" }));
            var (code, _, _) = Run(() => scan, "scan", "--save", "--store", nested);
            Assert.Equal(0, code);
            Assert.True(File.Exists(nested));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var root = Path.GetDirectoryName(Path.GetDirectoryName(nested)!);
            if (Directory.Exists(root!))
                Directory.Delete(root, recursive: true);
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var candidate = _dbPath + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
