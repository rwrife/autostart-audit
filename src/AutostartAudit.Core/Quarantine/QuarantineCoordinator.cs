using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Quarantine;

/// <summary>
/// Orchestrates quarantine and restore through the journal-first contract:
/// <list type="number">
/// <item>Ask the strategy for an exact-restore plan. No plan → the entry is
/// read-only and NOTHING happens (refuse-to-act).</item>
/// <item>Commit the journal record BEFORE any mutation. A journal failure
/// aborts the operation with zero side effects.</item>
/// <item>Only then perform the mutation, and durably record the outcome —
/// including declined elevation and unverified results, which stay visible
/// instead of masquerading as clean or done.</item>
/// </list>
/// User-scope work runs in-process unelevated. Machine-scope work delegates
/// the single already-journaled record to an <see cref="IElevatedRecordRunner"/>;
/// a declined prompt leaves the record Pending (never "quarantined").
/// </summary>
public sealed class QuarantineCoordinator
{
    private readonly IJournal _journal;
    private readonly IReadOnlyDictionary<string, IQuarantineStrategy> _strategies;
    private readonly IElevatedRecordRunner _elevated;

    public QuarantineCoordinator(
        IJournal journal,
        IEnumerable<IQuarantineStrategy> strategies,
        IElevatedRecordRunner? elevated = null)
    {
        _journal = journal;
        _strategies = strategies.ToDictionary(s => s.SourceKind, StringComparer.Ordinal);
        _elevated = elevated ?? new NoElevation();
    }

    /// <summary>True when some strategy can describe an exact restore for the entry right now.</summary>
    public QuarantineDecision Assess(AutoStartEntry entry) =>
        _strategies.TryGetValue(entry.SourceKind, out var strategy)
            ? strategy.Plan(entry)
            : QuarantineDecision.ReadOnly($"no quarantine strategy is registered for source '{entry.SourceKind}'");

    public QuarantineOutcome Quarantine(AutoStartEntry entry)
    {
        if (!_strategies.TryGetValue(entry.SourceKind, out var strategy))
            return new QuarantineOutcome
            {
                Status = QuarantineStatus.ReadOnly,
                Detail = $"no quarantine strategy is registered for source '{entry.SourceKind}'",
            };

        var plan = strategy.Plan(entry);
        if (!plan.CanQuarantine)
            return new QuarantineOutcome { Status = QuarantineStatus.ReadOnly, Detail = plan.Reason };

        long recordId;
        try
        {
            // Journal-first: the durable inverse description exists before we
            // touch the machine. If this throws, nothing has been mutated.
            recordId = _journal.Begin(new JournalDraft
            {
                EntryIdentity = StableKey.Build(entry.SourceKind, entry.Scope,
                    entry.NativeKey ?? entry.StableKey),
                SourceKind = entry.SourceKind,
                Scope = entry.Scope,
                NativeKey = entry.NativeKey ?? entry.StableKey,
                DisplayName = entry.DisplayName,
                Strategy = strategy.SourceKind,
                BeforeStateJson = plan.BeforeStateJson!,
            });
        }
        catch (Exception ex)
        {
            return new QuarantineOutcome
            {
                Status = QuarantineStatus.Failed,
                Detail = $"journal commit failed; nothing was mutated: {ex.Message}",
            };
        }

        return ExecuteRecord(recordId);
    }

    /// <summary>
    /// Performs the mutation for one already-journaled record directly in
    /// this process — the elevation channel is deliberately bypassed here
    /// because the caller is either a user-scope retry or the elevated child
    /// that UAC already granted. Records already in a terminal state are
    /// never re-mutated.
    /// </summary>
    public QuarantineOutcome RunQuarantineRecord(long recordId)
    {
        var record = _journal.Get(recordId)
            ?? throw new InvalidOperationException($"journal record {recordId} does not exist");
        if (record.Result == QuarantineResult.Executed)
            return new QuarantineOutcome { Status = QuarantineStatus.Quarantined, RecordId = recordId, Detail = "record already executed" };
        return PerformQuarantine(recordId);
    }

    public RestoreOutcome Restore(long recordId)
    {
        var record = _journal.Get(recordId)
            ?? throw new InvalidOperationException($"journal record {recordId} does not exist");
        if (record.Result != QuarantineResult.Executed)
            return new RestoreOutcome
            {
                Status = RestoreStatus.ReadOnly,
                RecordId = recordId,
                Detail = $"record result is {record.Result}; there is no verified quarantine to undo",
            };
        if (record.RestoreStatus == JournalRestoreStatus.Restored)
            return new RestoreOutcome { Status = RestoreStatus.Restored, RecordId = recordId, Detail = "record was already restored" };
        if (!_strategies.ContainsKey(record.SourceKind))
            return new RestoreOutcome
            {
                Status = RestoreStatus.ReadOnly,
                RecordId = recordId,
                Detail = $"no strategy is registered for source '{record.SourceKind}'",
            };

        if (string.Equals(record.Scope, "machine", StringComparison.OrdinalIgnoreCase))
        {
            var run = _elevated.RunRestore(recordId);
            if (run is ElevatedRunStatus.Declined or ElevatedRunStatus.NotAvailable)
                return new RestoreOutcome
                {
                    Status = RestoreStatus.ElevationDeclined,
                    RecordId = recordId,
                    Detail = run == ElevatedRunStatus.Declined
                        ? "elevation for restore was declined; nothing was changed"
                        : "no elevation channel is available for machine-scope restore",
                };
            return FinalizeRestore(recordId, run);
        }

        return PerformRestore(recordId);
    }

    /// <summary>
    /// Performs the exact inverse for one journaled record in this process.
    /// The elevated child calls this after the parent committed the record.
    /// </summary>
    public RestoreOutcome RunRestoreRecord(long recordId)
    {
        var record = _journal.Get(recordId)
            ?? throw new InvalidOperationException($"journal record {recordId} does not exist");
        if (!_strategies.TryGetValue(record.SourceKind, out _))
            return new RestoreOutcome { Status = RestoreStatus.ReadOnly, RecordId = recordId, Detail = "no strategy for record" };
        if (record.Result != QuarantineResult.Executed)
            return new RestoreOutcome { Status = RestoreStatus.ReadOnly, RecordId = recordId, Detail = "record is not in an executed state" };
        return PerformRestore(recordId);
    }

    private RestoreOutcome PerformRestore(long recordId)
    {
        var record = _journal.Get(recordId)!;
        var strategy = _strategies[record.SourceKind];
        MutationResult result;
        try
        {
            result = strategy.Restore(record.BeforeStateJson);
        }
        catch (Exception ex)
        {
            result = new MutationResult(MutationStatus.Failed, ex.Message);
        }
        return FinalizeRestore(recordId, result);
    }

    private RestoreOutcome FinalizeRestore(long recordId, MutationResult result)
    {
        switch (result.Status)
        {
            case MutationStatus.Ok:
                _journal.MarkRestored(recordId);
                return new RestoreOutcome { Status = RestoreStatus.Restored, RecordId = recordId };
            case MutationStatus.Denied:
                _journal.MarkRestoreFailed(recordId, $"elevation denied for restore: {result.Detail}");
                return new RestoreOutcome { Status = RestoreStatus.ElevationDeclined, RecordId = recordId, Detail = result.Detail };
            case MutationStatus.Failed:
            default:
                _journal.MarkRestoreFailed(recordId, result.Detail ?? "restore failed");
                return new RestoreOutcome { Status = RestoreStatus.Failed, RecordId = recordId, Detail = result.Detail };
        }
    }

    private RestoreOutcome FinalizeRestore(long recordId, ElevatedRunStatus run)
    {
        // After the child returns, trust the durable journal state — the
        // child (or its crash) is the source of truth, not the exit signal.
        var record = _journal.Get(recordId);
        if (record is { RestoreStatus: JournalRestoreStatus.Restored })
            return new RestoreOutcome { Status = RestoreStatus.Restored, RecordId = recordId };
        if (record is { RestoreStatus: JournalRestoreStatus.Failed })
            return new RestoreOutcome { Status = RestoreStatus.Failed, RecordId = recordId, Detail = record.RestoreErrorDetail };
        var detail = run switch
        {
            ElevatedRunStatus.Uncertain => "elevated restore did not complete cleanly and the journal shows no verified restore",
            _ => "elevated restore produced no durable result",
        };
        _journal.MarkRestoreFailed(recordId, detail);
        return new RestoreOutcome { Status = RestoreStatus.Failed, RecordId = recordId, Detail = detail };
    }

    private QuarantineOutcome ExecuteRecord(long recordId)
    {
        var record = _journal.Get(recordId)!;

        if (string.Equals(record.Scope, "machine", StringComparison.OrdinalIgnoreCase))
        {
            var run = _elevated.RunQuarantine(recordId);
            if (run is ElevatedRunStatus.Declined or ElevatedRunStatus.NotAvailable)
                return new QuarantineOutcome
                {
                    Status = QuarantineStatus.ElevationDeclined,
                    RecordId = recordId,
                    Detail = run == ElevatedRunStatus.Declined
                        ? "single-action elevation was declined; the entry was not mutated"
                        : "no elevation channel is available; the entry was not mutated",
                };
            return FinalizeQuarantine(recordId, run);
        }

        return PerformQuarantine(recordId);
    }

    private QuarantineOutcome PerformQuarantine(long recordId)
    {
        var record = _journal.Get(recordId)!;
        var strategy = _strategies[record.SourceKind];
        MutationResult result;
        try
        {
            result = strategy.Execute(record.BeforeStateJson);
        }
        catch (Exception ex)
        {
            result = new MutationResult(MutationStatus.Failed, ex.Message);
        }

        return FinalizeQuarantine(recordId, result);
    }

    private QuarantineOutcome FinalizeQuarantine(long recordId, ElevatedRunStatus run)
    {
        var record = _journal.Get(recordId);
        if (record?.Result == QuarantineResult.Executed)
            return new QuarantineOutcome { Status = QuarantineStatus.Quarantined, RecordId = recordId };
        if (record?.Result == QuarantineResult.Failed)
            return new QuarantineOutcome { Status = QuarantineStatus.Failed, RecordId = recordId, Detail = record.ErrorDetail };
        var detail = run == ElevatedRunStatus.Uncertain
            ? "elevated helper did not complete cleanly and the journal shows no verified result"
            : "elevated helper produced no durable result";
        _journal.MarkFailed(recordId, detail);
        return new QuarantineOutcome { Status = QuarantineStatus.Failed, RecordId = recordId, Detail = detail };
    }

    private QuarantineOutcome FinalizeQuarantine(long recordId, MutationResult result)
    {
        switch (result.Status)
        {
            case MutationStatus.Ok:
                _journal.MarkExecuted(recordId);
                return new QuarantineOutcome { Status = QuarantineStatus.Quarantined, RecordId = recordId };
            case MutationStatus.Denied:
                // Denial happens before any write for honest strategies. The
                // record is durably Failed with the reason — never Pending garbage.
                _journal.MarkFailed(recordId, $"access denied: {result.Detail}");
                return new QuarantineOutcome { Status = QuarantineStatus.Failed, RecordId = recordId, Detail = result.Detail };
            case MutationStatus.Failed:
            default:
                _journal.MarkFailed(recordId, result.Detail ?? "mutation failed");
                return new QuarantineOutcome { Status = QuarantineStatus.Failed, RecordId = recordId, Detail = result.Detail };
        }
    }

    private sealed class NoElevation : IElevatedRecordRunner
    {
        public ElevatedRunStatus RunQuarantine(long recordId) => ElevatedRunStatus.NotAvailable;

        public ElevatedRunStatus RunRestore(long recordId) => ElevatedRunStatus.NotAvailable;
    }
}
