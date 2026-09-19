using AutostartAudit.Core.Model;
using AutostartAudit.Core.Quarantine;
using AutostartAudit.Core.Snapshot;
using AutostartAudit.Core.Store;

namespace AutostartAudit.App.Model;

/// <summary>
/// Everything the UI needs from the machine, behind one seam so view models
/// are fully testable with a fixture-backed implementation (no real machine
/// reads or mutations in tests). The production implementation lives in the
/// WPF app and wraps <c>ScanEngine</c>, <c>SnapshotStore</c>,
/// <c>ChangeJournal</c>, and <c>QuarantineCoordinator</c> from Core.
/// </summary>
public interface IScanService
{
    Task<ScanDocument> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>Save the document as a snapshot and diff it against the previous one.</summary>
    Task<CaptureOutcome> CaptureAndDiffAsync(ScanDocument document, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JournalRecord>> ListJournalAsync(CancellationToken cancellationToken = default);

    /// <summary>Ask whether an exact-restore plan exists right now (drives the quarantine affordance). Never mutates.</summary>
    QuarantineDecision AssessQuarantine(AutoStartEntry entry);

    Task<QuarantineOutcome> QuarantineAsync(AutoStartEntry entry, CancellationToken cancellationToken = default);

    Task<RestoreOutcome> RestoreAsync(long recordId, CancellationToken cancellationToken = default);
}
