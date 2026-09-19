using AutostartAudit.App.Model;
using AutostartAudit.Core.Model;
using AutostartAudit.Core.Quarantine;
using AutostartAudit.Core.Scan;
using AutostartAudit.Core.Snapshot;
using AutostartAudit.Core.Store;

namespace AutostartAudit.App;

/// <summary>
/// Production <see cref="IScanService"/>: real scan engine, SQLite snapshot
/// store, durable change journal, and the journal-first quarantine
/// coordinator with single-action elevation for machine scope. All blocking
/// work runs on the thread pool; nothing here touches the UI thread.
/// </summary>
public sealed class LiveWorkspaceService : IScanService, IDisposable
{
    private readonly SnapshotStore _store;
    private readonly Func<ChangeJournal> _journalFactory;

    public LiveWorkspaceService(SnapshotStore store, Func<ChangeJournal> journalFactory)
    {
        _store = store;
        _journalFactory = journalFactory;
    }

    public static LiveWorkspaceService ForCurrentOS() =>
        new(new SnapshotStore(), () => new ChangeJournal());

    public Task<ScanDocument> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(ScanEngine.ScanAll, cancellationToken);

    public Task<CaptureOutcome> CaptureAndDiffAsync(ScanDocument document, CancellationToken cancellationToken = default) =>
        Task.Run(() => SnapshotCapture.CaptureAndCompare(_store, document), cancellationToken);

    public Task<IReadOnlyList<JournalRecord>> ListJournalAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<JournalRecord>>(() =>
        {
            using var journal = _journalFactory();
            return journal.ListAll();
        }, cancellationToken);

    public QuarantineDecision AssessQuarantine(AutoStartEntry entry)
    {
        // Plan performs live re-reads of the entry's source (read-only).
        using var scope = BeginCoordinator();
        return scope.Coordinator.Assess(entry);
    }

    public Task<QuarantineOutcome> QuarantineAsync(AutoStartEntry entry, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var scope = BeginCoordinator();
            return scope.Coordinator.Quarantine(entry);
        }, cancellationToken);

    public Task<RestoreOutcome> RestoreAsync(long recordId, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var scope = BeginCoordinator();
            return scope.Coordinator.Restore(recordId);
        }, cancellationToken);

    private CoordinatorScope BeginCoordinator()
    {
        var journal = _journalFactory();
        var runner = new ElevatedProcessRunner(
            () => Environment.ProcessPath
                ?? throw new InvalidOperationException("cannot resolve the executable path for elevation"),
            ChangeJournal.DefaultDatabasePath);
        return new CoordinatorScope(journal, new QuarantineCoordinator(
            journal, DefaultQuarantine.StrategiesForCurrentOS(), runner));
    }

    private sealed record CoordinatorScope(ChangeJournal Journal, QuarantineCoordinator Coordinator)
        : IDisposable
    {
        public void Dispose() => Journal.Dispose();
    }

    public void Dispose() => _store.Dispose();
}
