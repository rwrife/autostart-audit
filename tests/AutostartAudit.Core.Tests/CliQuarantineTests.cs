using AutostartAudit.Cli;
using AutostartAudit.Core.Model;
using AutostartAudit.Core.Quarantine;
using AutostartAudit.Core.Scan;
using AutostartAudit.Core.Snapshot;
using AutostartAudit.Core.Store;

namespace AutostartAudit.Core.Tests;

/// <summary>
/// CLI wiring for the change-journal surface: the elevated-child record
/// runner, journal listing, and restore. Uses a temp journal DB; never the
/// real user profile.
/// </summary>
public sealed class CliQuarantineTests : IDisposable
{
    private readonly string _journalPath;

    public CliQuarantineTests()
    {
        _journalPath = Path.Combine(Path.GetTempPath(), "aa-cli-" + Guid.NewGuid().ToString("N") + ".db");
    }

    private static int Run(IReadOnlyList<string> args, StringWriter output, StringWriter error, string journalPath) =>
        App.Run(args, output, error,
            () => ScanDocument.Empty,
            path => new SnapshotStore(path ?? ":memory:"),
            path => new ChangeJournal(path ?? journalPath));

    private static int RunWithScan(IReadOnlyList<string> args, StringWriter output, StringWriter error,
        ScanDocument document) =>
        App.Run(args, output, error,
            () => document,
            path => new SnapshotStore(path ?? ":memory:"),
            path => new ChangeJournal(path));

    [Fact]
    public void RunRecord_QuarantineOnUnknownRecord_ExitNonZero()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Run(new[] { "run-record", "42", "quarantine", "--journal", _journalPath }, output, error, _journalPath);
        Assert.Equal(1, code);
        Assert.Contains("does not exist", error.ToString());
    }

    [Fact]
    public void Journal_Empty_IsEmptyOutputExitZero()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, Run(new[] { "journal", "--journal", _journalPath }, output, error, _journalPath));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void Journal_ListsRecordStates()
    {
        using (var journal = new ChangeJournal(_journalPath))
        {
            var id = journal.Begin(new JournalDraft
            {
                EntryIdentity = "run-key|user|x",
                SourceKind = "run-key",
                Scope = "user",
                NativeKey = "x",
                DisplayName = "Tool",
                Strategy = "run-key",
                BeforeStateJson = "{}",
            });
            journal.MarkExecuted(id);
        }

        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, Run(new[] { "journal", "--journal", _journalPath }, output, error, _journalPath));
        var line = Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("run-key/user 'Tool'", line);
        Assert.Contains("result=Executed", line);
        Assert.Contains("restore=None", line);
    }

    [Fact]
    public void Quarantine_NoMatchingEntry_ExitTwo()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = RunWithScan(new[] { "quarantine", @"hkcu\nope\missing", "--journal", _journalPath },
            output, error, new ScanDocument
            {
                Entries = Array.Empty<AutostartAudit.Core.Model.AutoStartEntry>(),
                Sources = Array.Empty<AutostartAudit.Core.Model.SourceReport>(),
                ScanComplete = false,
            });
        Assert.Equal(2, code);
        Assert.Contains("no scan entry matches", error.ToString());
    }

    [Fact]
    public void Quarantine_MatchesByIdentityNotDisplayName()
    {
        // A single matching entry must be found by normalized native key.
        // On this (non-Windows) test host zero strategies are registered, so
        // the honest outcome is ReadOnly — never a silent success or a crash.
        var entry = QuarantineTestEntries.RunKey("user", "PrettyLabel", @"C:\t.exe");
        var output = new StringWriter();
        var error = new StringWriter();
        var code = RunWithScan(new[] { "quarantine", entry.NativeKey!, "--journal", _journalPath },
            output, error, new ScanDocument
            {
                Entries = new[] { entry },
                Sources = Array.Empty<AutostartAudit.Core.Model.SourceReport>(),
                ScanComplete = false,
            });

        if (OperatingSystem.IsWindows())
            Assert.Contains("quarantine:", output.ToString());
        else
        {
            Assert.Equal(1, code);
            Assert.Contains("ReadOnly", output.ToString());
            Assert.Contains("no quarantine strategy", output.ToString());
        }
    }

    [Fact]
    public void Restore_UnknownId_ReportsFailureExitNonZero()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Run(new[] { "restore", "7", "--journal", _journalPath }, output, error, _journalPath);
        // Record does not exist: the exception surfaces as an unhandled error?
        // No — App.Run has no try/catch there, so assert current contract:
        Assert.Equal(1, code);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_journalPath); File.Delete(_journalPath + "-wal"); File.Delete(_journalPath + "-shm"); } catch (IOException) { }
    }
}
