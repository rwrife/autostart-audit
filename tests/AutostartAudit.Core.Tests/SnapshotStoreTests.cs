using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;
using AutostartAudit.Core.Snapshot;
using AutostartAudit.Core.Store;

namespace AutostartAudit.Core.Tests;

public class SnapshotStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"aa-snapshots-{Guid.NewGuid():N}.db");

    private static ScanDocument Doc(params AutoStartEntry[] entries) => new()
    {
        Entries = entries,
        Sources = new[] { new SourceReport { SourceKind = "run-key", Capability = SourceCapability.Scanned } },
        ScanComplete = true,
    };

    [Fact]
    public void RoundTrip_PreservesEveryEvidenceFieldByteForByte_ThroughSqlite()
    {
        var entry = SnapshotTestFixtures.Entry("run-key", "user", @"HKCU\Run\App", "App",
            new[] { @"C:\Apps\app.exe" }, signing: SigningStatus.Signed, enabled: true) with
        {
            SignerSubject = "CN=Vendor, O=Vendor Inc",
            SourceName = "App",
            State = "running",
            StartType = "automatic-delayed",
            TriggerSummary = "logon",
            Evidence = new[] { "evidence line one", "evidence line two" },
            TargetSignatures = new[]
            {
                new TargetSignature { Path = @"C:\Apps\app.exe", Status = SigningStatus.Signed, Publisher = "CN=Vendor" },
            },
            SigningAggregation = SigningAggregation.Single,
        };
        var document = new ScanDocument
        {
            Entries = new[] { entry },
            Sources = new[] { new SourceReport { SourceKind = "run-key", Capability = SourceCapability.Scanned } },
            ScanComplete = false,
        };

        long id;
        using (var store = new SnapshotStore(_dbPath))
            id = store.Save(document, new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

        using var reopened = new SnapshotStore(_dbPath);
        var loaded = reopened.GetById(id);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded!.Id);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), loaded.CapturedAtUtc);
        Assert.Equal(SnapshotStore.CurrentSchemaVersion, loaded.SchemaVersion);
        // The stored document re-serializes to the exact canonical JSON that
        // was written: nothing in the evidence model is lossy through SQLite.
        Assert.Equal(document.ToJson(), loaded.Document.ToJson());
        Assert.False(loaded.Document.ScanComplete);
        var loadedEntry = Assert.Single(loaded.Document.Entries);
        Assert.Equal("CN=Vendor, O=Vendor Inc", loadedEntry.SignerSubject);
        Assert.Equal("automatic-delayed", loadedEntry.StartType);
        Assert.Equal(new[] { "evidence line one", "evidence line two" }, loadedEntry.Evidence);
    }

    [Fact]
    public void Latest_ReturnsMostRecent_AndListAll_OldestFirst()
    {
        using var store = new SnapshotStore(_dbPath);
        var first = store.Save(Doc(), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var second = store.Save(Doc(SnapshotTestFixtures.Entry("run-key", "user", "K", "N", new[] { "t" })));

        var latest = store.GetLatest();
        Assert.NotNull(latest);
        Assert.Equal(second, latest!.Id);
        var all = store.ListAll();
        Assert.Equal(new[] { first, second }, all.Select(s => s.Id));
    }

    [Fact]
    public void GetLatest_OnEmptyStore_ReturnsNull()
    {
        using var store = new SnapshotStore(_dbPath);
        Assert.Null(store.GetLatest());
    }

    [Fact]
    public void ReopeningExistingDatabase_KeepsData_AndSameSchemaVersion()
    {
        long id;
        using (var store = new SnapshotStore(_dbPath))
            id = store.Save(Doc());
        using (var store = new SnapshotStore(_dbPath))
        {
            var latest = store.GetLatest();
            Assert.NotNull(latest);
            Assert.Equal(id, latest!.Id);
            Assert.Equal(SnapshotStore.CurrentSchemaVersion, latest.SchemaVersion);
        }
    }

    [Fact]
    public void FutureSchemaVersion_FailsClosed_RatherThanGuessing()
    {
        // Simulate a database written by a newer build.
        using (var setup = new SnapshotStore(_dbPath)) { }
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE schema_info SET version = 99;";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<InvalidOperationException>(() => new SnapshotStore(_dbPath));
        Assert.Contains("newer than this build supports", ex.Message);
    }

    [Fact]
    public void FirstCapture_IsBaseline_AndYieldsNoDiffStorm()
    {
        using var store = new SnapshotStore(_dbPath);
        var doc = Doc(
            SnapshotTestFixtures.Entry("run-key", "user", "A", "A", new[] { @"C:\a.exe" }),
            SnapshotTestFixtures.Entry("run-key", "user", "B", "B", new[] { @"C:\b.exe" }));

        var outcome = SnapshotCapture.CaptureAndCompare(store, doc);

        Assert.True(outcome.IsBaseline);
        Assert.Null(outcome.Diff);
        Assert.Null(outcome.PreviousSnapshotId);
        // The baseline snapshot itself is durably stored for future diffs.
        Assert.Equal(2, store.GetLatest()!.Document.Entries.Count);
    }

    [Fact]
    public void SecondCapture_DiffsAgainstPreviousAndReportsPairing()
    {
        using var store = new SnapshotStore(_dbPath);
        var before = Doc(SnapshotTestFixtures.Entry("run-key", "user", "A", "A", new[] { @"C:\a.exe" }));
        SnapshotCapture.CaptureAndCompare(store, before);

        var after = Doc(SnapshotTestFixtures.Entry("run-key", "user", "A", "A", new[] { @"C:\a2.exe" }));
        var outcome = SnapshotCapture.CaptureAndCompare(store, after);

        Assert.False(outcome.IsBaseline);
        Assert.NotNull(outcome.Diff);
        Assert.Single(outcome.Diff!.Changed);
        Assert.Contains(DiffEngine.ReasonPath, outcome.Diff.Changed[0].Reasons);
        Assert.Equal(store.ListAll()[0].Id, outcome.PreviousSnapshotId);
    }

    [Fact]
    public void DefaultDatabasePath_LivesUnderPerUserAppData()
    {
        var path = SnapshotStore.DefaultDatabasePath;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(root, path);
        Assert.Contains("autostart-audit", path);
        Assert.EndsWith("snapshots.db", path);
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
