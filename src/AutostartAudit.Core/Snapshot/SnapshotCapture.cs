using AutostartAudit.Core.Model;
using AutostartAudit.Core.Snapshot;
using AutostartAudit.Core.Store;

namespace AutostartAudit.Core.Snapshot;

/// <summary>
/// Capture-then-compare flow over the snapshot store. Kept separate from the
/// store so the first-run semantics stay unit-testable against any store
/// instance (including in-memory).
/// </summary>
public static class SnapshotCapture
{
    /// <summary>
    /// Saves <paramref name="document"/> and diffs it against the previous
    /// latest snapshot. On the first capture (empty store) no diff is
    /// produced: the run is reported as establishing a baseline, never as
    /// "everything is new".
    /// </summary>
    public static CaptureOutcome CaptureAndCompare(SnapshotStore store, ScanDocument document)
    {
        var previous = store.GetLatest();
        var id = store.Save(document);
        if (previous is null)
        {
            return new CaptureOutcome
            {
                IsBaseline = true,
                SnapshotId = id,
                PreviousSnapshotId = null,
                Diff = null,
            };
        }

        return new CaptureOutcome
        {
            IsBaseline = false,
            SnapshotId = id,
            PreviousSnapshotId = previous.Id,
            Diff = DiffEngine.Compare(previous.Document, document),
        };
    }
}
