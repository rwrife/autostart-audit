using AutostartAudit.Core.Quarantine;

namespace AutostartAudit.Core.Tests;

public sealed class ChangeJournalTests : IDisposable
{
    private readonly string _path;

    public ChangeJournalTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "aa-journal-" + Guid.NewGuid().ToString("N") + ".db");
    }

    private static JournalDraft Draft(string name = "MyApp") => new()
    {
        EntryIdentity = $"run-key|user|hkcu\\...\\run\\{name}",
        SourceKind = "run-key",
        Scope = "user",
        NativeKey = $@"hkcu\...\run\{name}",
        DisplayName = name,
        Strategy = "run-key",
        BeforeStateJson = "{\"data\":\"x\"}",
    };

    [Fact]
    public void RecordsSurviveReopen_WithFullState()
    {
        long id;
        using (var journal = new ChangeJournal(_path))
        {
            id = journal.Begin(Draft());
            journal.MarkExecuted(id);
        }

        using (var reopened = new ChangeJournal(_path))
        {
            var record = reopened.Get(id)!;
            Assert.Equal(QuarantineResult.Executed, record.Result);
            Assert.Equal(JournalRestoreStatus.None, record.RestoreStatus);
            Assert.Equal("MyApp", record.DisplayName);
            Assert.Equal("{\"data\":\"x\"}", record.BeforeStateJson);
        }
    }

    [Fact]
    public void BeginThenCrash_LeavesPending_NotClean()
    {
        long id;
        using (var journal = new ChangeJournal(_path))
            id = journal.Begin(Draft());

        using (var reopened = new ChangeJournal(_path))
        {
            var record = reopened.Get(id)!;
            // A record whose mutation outcome was never established must read
            // Pending — never "clean"/"quarantined".
            Assert.Equal(QuarantineResult.Pending, record.Result);
        }
    }

    [Fact]
    public void RestoreTransitions_Persist()
    {
        long id;
        using (var journal = new ChangeJournal(_path))
        {
            id = journal.Begin(Draft());
            journal.MarkExecuted(id);
            journal.MarkRestoreFailed(id, "target occupied");
        }
        using (var reopened = new ChangeJournal(_path))
        {
            var record = reopened.Get(id)!;
            Assert.Equal(JournalRestoreStatus.Failed, record.RestoreStatus);
            Assert.Equal("target occupied", record.RestoreErrorDetail);
            reopened.MarkRestored(id);
        }
        using (var again = new ChangeJournal(_path))
        {
            var record = again.Get(id)!;
            Assert.Equal(JournalRestoreStatus.Restored, record.RestoreStatus);
            Assert.Null(record.RestoreErrorDetail);
            Assert.NotNull(record.RestoredAtUtc);
        }
    }

    [Fact]
    public void ListAll_IsNewestFirst()
    {
        using var journal = new ChangeJournal(_path);
        var first = journal.Begin(Draft("A"));
        var second = journal.Begin(Draft("B"));
        var all = journal.ListAll();
        Assert.Equal(2, all.Count);
        Assert.Equal(second, all[0].Id);
        Assert.Equal(first, all[1].Id);
    }

    [Fact]
    public void TransitionOnUnknownId_Throws()
    {
        using var journal = new ChangeJournal(_path);
        Assert.Throws<InvalidOperationException>(() => journal.MarkExecuted(9999));
        Assert.Throws<InvalidOperationException>(() => journal.MarkRestored(9999));
    }

    [Fact]
    public void SchemaVersionGuard_FailClosedForUnknownOlderVersion()
    {
        // Write a DB claiming an older unsupported schema version.
        using (var setup = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            setup.Open();
            using var command = setup.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_info (version INTEGER NOT NULL);
                INSERT INTO schema_info (version) VALUES (0);
                """;
            command.ExecuteNonQuery();
        }
        var error = Assert.Throws<InvalidOperationException>(() => _ = new ChangeJournal(_path));
        Assert.Contains("No migration path", error.Message);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); File.Delete(_path + "-wal"); File.Delete(_path + "-shm"); } catch (IOException) { }
    }
}
