namespace AutostartAudit.Core.Quarantine;

/// <summary>
/// Durable outcome of the mutation attempt for one journal record.
/// <see cref="Pending"/> means the record was committed but the mutation never
/// completed — the entry state is then UNKNOWN and must never be presented as
/// either "quarantined" or "clean".
/// </summary>
public enum QuarantineResult
{
    /// <summary>Journal record committed; mutation outcome not yet established.</summary>
    Pending,

    /// <summary>Mutation performed and the post-state was verified.</summary>
    Executed,

    /// <summary>
    /// Mutation did not happen or could not be verified. With an honest
    /// strategy the entry is untouched (deny/fail-closed before any write);
    /// verification failures after a partial mutation are disclosed in the
    /// record's error detail.
    /// </summary>
    Failed,
}

/// <summary>Restore lifecycle of an executed quarantine record.</summary>
public enum JournalRestoreStatus
{
    /// <summary>Never restored.</summary>
    None,

    /// <summary>Exact inverse applied and post-state verified.</summary>
    Restored,

    /// <summary>A restore attempt failed; retrying restore is allowed.</summary>
    Failed,
}

/// <summary>Everything needed to commit a journal record before any mutation.</summary>
public sealed record JournalDraft
{
    /// <summary>Normalized entry identity (source kind + scope + stable key).</summary>
    public required string EntryIdentity { get; init; }

    public required string SourceKind { get; init; }

    public required string Scope { get; init; }

    /// <summary>Normalized native key (registry key+value, task path, service name, file path).</summary>
    public required string NativeKey { get; init; }

    /// <summary>Display label at quarantine time. Presentation only, never identity.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Which strategy owns this record (exact source-kind key).</summary>
    public required string Strategy { get; init; }

    /// <summary>Verbatim before-state the strategy needs to describe the exact restore.</summary>
    public required string BeforeStateJson { get; init; }
}

/// <summary>One durable change-journal record as loaded from the store.</summary>
public sealed record JournalRecord
{
    public required long Id { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string EntryIdentity { get; init; }
    public required string SourceKind { get; init; }
    public required string Scope { get; init; }
    public required string NativeKey { get; init; }
    public required string DisplayName { get; init; }
    public required string Strategy { get; init; }
    public required string BeforeStateJson { get; init; }
    public required QuarantineResult Result { get; init; }

    /// <summary>Why a mutation/restore failed or was declined. Null on success.</summary>
    public string? ErrorDetail { get; init; }

    public required JournalRestoreStatus RestoreStatus { get; init; }

    /// <summary>Why the most recent restore attempt failed. Null unless <see cref="JournalRestoreStatus.Failed"/>.</summary>
    public string? RestoreErrorDetail { get; init; }

    public DateTimeOffset? RestoredAtUtc { get; init; }
}

/// <summary>
/// Durable change journal consumed by the coordinator. The contract that makes
/// quarantine safe: <see cref="Begin"/> commits the record <em>before</em> any
/// mutation, so a mutation is only ever attempted while a durable description
/// of its exact inverse already exists. If <see cref="Begin"/> throws, no
/// mutation happens.
/// </summary>
public interface IJournal
{
    /// <summary>Commits a pending record and returns its id. Throws on any persistence failure.</summary>
    long Begin(JournalDraft draft);

    public void MarkExecuted(long id);

    public void MarkFailed(long id, string detail);

    public void MarkRestored(long id);

    public void MarkRestoreFailed(long id, string detail);

    public JournalRecord? Get(long id);

    /// <summary>All records newest-first.</summary>
    public IReadOnlyList<JournalRecord> ListAll();
}

/// <summary>Mutation attempts refused because access was denied (elevation boundary).</summary>
public sealed class QuarantineDeniedException(string id, string detail)
    : Exception($"access denied for {id}: {detail}");

/// <summary>Mutation attempt failed and the resulting state is not established.</summary>
public sealed class QuarantineUnknownException(string id, string detail)
    : Exception($"unresolved failure for {id}: {detail}");
