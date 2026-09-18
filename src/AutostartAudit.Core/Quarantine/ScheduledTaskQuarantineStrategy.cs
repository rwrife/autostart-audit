using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Quarantine;

/// <summary>
/// Strategy for scheduled tasks with logon/boot triggers: the task is
/// disabled and its previous enabled state is journaled, so restore re-enables
/// exactly what was found. The task definition XML is never modified.
/// </summary>
public sealed class ScheduledTaskQuarantineStrategy : IQuarantineStrategy
{
    public const string Kind = AutostartAudit.Core.Scan.ScheduledTaskSource.Kind;

    private readonly ITaskWriteProbe _probe;

    public ScheduledTaskQuarantineStrategy(ITaskWriteProbe probe) => _probe = probe;

    public string SourceKind => Kind;

    internal sealed record State(string Path, bool WasEnabled);

    public QuarantineDecision Plan(AutoStartEntry entry)
    {
        var path = entry.NativeKey ?? entry.StableKey;
        // COM task paths keep their case; NativeKey is normalized lowercase,
        // so the verbatim path must come from the scan's SourceName.
        var verbatim = string.IsNullOrEmpty(entry.SourceName) ? path : entry.SourceName;
        if (!verbatim.StartsWith('\\') || verbatim.Length < 2)
            return QuarantineDecision.ReadOnly("task identity is not an absolute task path");
        if (entry.Enabled == true)
        {
            // Live re-read decides; scan state alone is not current evidence.
        }

        try
        {
            var live = _probe.GetTask(verbatim);
            if (live is null)
                return QuarantineDecision.ReadOnly("task is no longer registered");
            if (!string.Equals(StableKey.Normalize(live.Path), StableKey.Normalize(path), StringComparison.Ordinal))
                return QuarantineDecision.ReadOnly("task path resolved to a different identity");
            if (!live.Enabled)
                return QuarantineDecision.ReadOnly("task is already disabled");
            if (live.State is not ("Ready" or "Disabled"))
                return QuarantineDecision.ReadOnly($"task state '{live.State}' cannot host an exact enable/disable round-trip");
            return QuarantineDecision.Ready(BeforeState.Serialize(new State(verbatim, live.Enabled)));
        }
        catch (Exception ex)
        {
            // A denied read here is typical unelevated for machine tasks; the
            // elevated execution re-verifies before touching anything.
            return QuarantineDecision.ReadOnly($"current task state unreadable: {ex.Message}");
        }
    }

    public MutationResult Execute(string beforeStateJson)
    {
        var state = BeforeState.Deserialize<State>(beforeStateJson);
        if (state is null)
            return new MutationResult(MutationStatus.Failed, "journaled before-state failed to deserialize");
        try
        {
            var live = _probe.GetTask(state.Path);
            if (!live.Enabled && live.State is "Disabled")
                return new MutationResult(MutationStatus.Failed, "task is already disabled; refusing to journal a state we did not cause");
            if (live.State is not ("Ready" or "Disabled"))
                return new MutationResult(MutationStatus.Failed, $"task state '{live.State}' cannot be safely disabled right now");

            _probe.SetEnabled(state.Path, false);
            var after = _probe.GetTask(state.Path);
            if (after.Enabled)
                return new MutationResult(MutationStatus.Failed, "task remained enabled after disable; post-state verification failed");
            return new MutationResult(MutationStatus.Ok);
        }
        catch (QuarantineDeniedException ex)
        {
            return new MutationResult(MutationStatus.Denied, ex.Message);
        }
        catch (Exception ex)
        {
            return new MutationResult(MutationStatus.Failed, ex.Message);
        }
    }

    public MutationResult Restore(string beforeStateJson)
    {
        var state = BeforeState.Deserialize<State>(beforeStateJson);
        if (state is null || !state.WasEnabled)
            return new MutationResult(MutationStatus.Failed, "journaled before-state does not describe an enable restore");
        try
        {
            var live = _probe.GetTask(state.Path);
            if (live.Enabled)
                return new MutationResult(MutationStatus.Failed, "task is already enabled; the disable we journaled is not in effect (external change?)");

            _probe.SetEnabled(state.Path, true);
            var after = _probe.GetTask(state.Path);
            if (!after.Enabled)
                return new MutationResult(MutationStatus.Failed, "task remained disabled after enable; post-state verification failed");
            return new MutationResult(MutationStatus.Ok);
        }
        catch (QuarantineDeniedException ex)
        {
            return new MutationResult(MutationStatus.Denied, ex.Message);
        }
        catch (Exception ex)
        {
            return new MutationResult(MutationStatus.Failed, ex.Message);
        }
    }
}
