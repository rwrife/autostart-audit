using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Quarantine;

/// <summary>Outcome classification of one strategy mutation or restore call.</summary>
public enum MutationStatus
{
    /// <summary>Mutation performed and its post-state verified.</summary>
    Ok,

    /// <summary>Refused with access denied — the entry is untouched.</summary>
    Denied,

    /// <summary>Failed or unverifiable. The record detail states what is known about the state.</summary>
    Failed,
}

/// <summary>Result of one strategy mutation/restore attempt.</summary>
public sealed record MutationResult(MutationStatus Status, string? Detail = null);

/// <summary>
/// A strategy's answer to "can I describe an exact restore for this entry
/// right now?". A <c>false</c> answer makes the entry read-only — the
/// refuse-to-act rule. The reason is always disclosed.
/// </summary>
public sealed record QuarantineDecision
{
    public required bool CanQuarantine { get; init; }

    /// <summary>Why the entry is read-only. Set exactly when <see cref="CanQuarantine"/> is false.</summary>
    public string? Reason { get; init; }

    /// <summary>Verbatim before-state JSON to journal. Set exactly when <see cref="CanQuarantine"/> is true.</summary>
    public string? BeforeStateJson { get; init; }

    public static QuarantineDecision ReadOnly(string reason) =>
        new() { CanQuarantine = false, Reason = reason };

    public static QuarantineDecision Ready(string beforeStateJson) =>
        new() { CanQuarantine = true, BeforeStateJson = beforeStateJson };
}

/// <summary>
/// Per-source quarantine strategy. Every method must be safe: <see cref="Plan"/>
/// performs live re-reads and returns read-only (never throws) when an exact
/// restore cannot be described; <see cref="Execute"/> and <see cref="Restore"/>
/// operate purely from the journaled before-state so an elevated helper process
/// can run them without the original scan entry.
/// </summary>
public interface IQuarantineStrategy
{
    /// <summary>The source kind this strategy handles (e.g. "run-key").</summary>
    string SourceKind { get; }

    QuarantineDecision Plan(AutoStartEntry entry);

    MutationResult Execute(string beforeStateJson);

    MutationResult Restore(string beforeStateJson);
}

/// <summary>Result of asking for a single-action elevated run of one journal record.</summary>
public enum ElevatedRunStatus
{
    /// <summary>No elevation channel exists in this build/host; the entry is untouched.</summary>
    NotAvailable,

    /// <summary>The user declined the elevation prompt; the entry is untouched.</summary>
    Declined,

    /// <summary>The elevated child ran the record; the journal states the verified outcome.</summary>
    Completed,

    /// <summary>The child ran but did not exit cleanly; the journal is re-read to decide honestly.</summary>
    Uncertain,
}

/// <summary>
/// Single-action elevation boundary: the record-to-run is already committed in
/// the shared journal, so the elevated child needs nothing but the record id.
/// Declining produces no partial write because the child performs (or fails)
/// the whole mutation itself.
/// </summary>
public interface IElevatedRecordRunner
{
    ElevatedRunStatus RunQuarantine(long recordId);

    ElevatedRunStatus RunRestore(long recordId);
}

/// <summary>Status of a coordinator quarantine request.</summary>
public enum QuarantineStatus
{
    /// <summary>Journal committed, mutation performed, post-state verified.</summary>
    Quarantined,

    /// <summary>Refuse-to-act: no journal record, no mutation. The entry is read-only.</summary>
    ReadOnly,

    /// <summary>Failed (journal error, drift, verification failure, unavailable elevation).</summary>
    Failed,

    /// <summary>Single-action elevation was declined; the entry is untouched.</summary>
    ElevationDeclined,
}

/// <summary>Status of a coordinator restore request.</summary>
public enum RestoreStatus
{
    /// <summary>Exact inverse applied and verified.</summary>
    Restored,

    /// <summary>The record was never restored (or elevation for it was declined).</summary>
    Failed,

    /// <summary>Single-action elevation for the restore was declined; nothing else changed.</summary>
    ElevationDeclined,

    /// <summary>Refuse-to-act: the record cannot be restored (unknown id, never executed, no strategy).</summary>
    ReadOnly,
}

/// <summary>Outcome of one quarantine request.</summary>
public sealed record QuarantineOutcome
{
    public required QuarantineStatus Status { get; init; }

    /// <summary>Journal record id, or null when refuse-to-act skipped journaling entirely.</summary>
    public long? RecordId { get; init; }

    public string? Detail { get; init; }
}

/// <summary>Outcome of one restore request.</summary>
public sealed record RestoreOutcome
{
    public required RestoreStatus Status { get; init; }

    public required long RecordId { get; init; }

    public string? Detail { get; init; }
}
