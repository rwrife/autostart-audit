using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Quarantine;

/// <summary>
/// Strategy for Automatic/Automatic-delayed services: the start type is
/// captured to Disabled and the previous type journaled, so restore sets the
/// exact type back (including the delayed-auto flag). The running state is
/// deliberately untouched — quarantine prevents the next boot start, and the
/// tool never stops a live service.
/// </summary>
public sealed class ServiceQuarantineStrategy : IQuarantineStrategy
{
    public const string Kind = AutostartAudit.Core.Scan.ServiceSource.Kind;

    private readonly IServiceWriteProbe _probe;

    public ServiceQuarantineStrategy(IServiceWriteProbe probe) => _probe = probe;

    public string SourceKind => Kind;

    internal sealed record State(string Name, ServiceStartType PreviousType);

    public QuarantineDecision Plan(AutoStartEntry entry)
    {
        var name = string.IsNullOrEmpty(entry.SourceName)
            ? entry.NativeKey ?? entry.StableKey
            : entry.SourceName;

        ServiceStartType? scanType = entry.StartType switch
        {
            "automatic" => ServiceStartType.Automatic,
            "automatic-delayed" => ServiceStartType.AutomaticDelayed,
            _ => null,
        };
        if (entry.StartType == "automatic-delay-unknown")
            return QuarantineDecision.ReadOnly("automatic-delayed flag could not be read at scan time; exact restore cannot be described");
        if (scanType is null)
            return QuarantineDecision.ReadOnly("entry does not carry a quarantineable automatic start type");

        try
        {
            var live = _probe.GetService(name);
            if (live.StartType is ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed)
            {
                // Drift guard: the journaled type must be what we believe the
                // scan saw, or another tool changed it in between.
                if (scanType.Value != live.StartType)
                    return QuarantineDecision.ReadOnly(
                        $"service start type drifted since the scan (scan={entry.StartType}, now={live.StartType}); refusing to quarantine drifted state");
                return QuarantineDecision.Ready(BeforeState.Serialize(new State(name, live.StartType)));
            }
            return QuarantineDecision.ReadOnly($"service start type is {live.StartType}, not automatic; nothing to quarantine");
        }
        catch (Exception)
        {
            // Unelevated SCM write-open is expected to fail for machine
            // services; journal the scan-evidence candidate — the elevated
            // executor re-verifies live state before the first write.
            return QuarantineDecision.Ready(BeforeState.Serialize(new State(name, scanType.Value)));
        }
    }

    public MutationResult Execute(string beforeStateJson)
    {
        var state = BeforeState.Deserialize<State>(beforeStateJson);
        if (state is null)
            return new MutationResult(MutationStatus.Failed, "journaled before-state failed to deserialize");
        if (state.PreviousType is not (ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed))
            return new MutationResult(MutationStatus.Failed, "journaled previous type is not quarantineable");
        try
        {
            var live = _probe.GetService(state.Name);
            if (live.StartType != state.PreviousType)
                return new MutationResult(MutationStatus.Failed,
                    $"start type drifted since planning (journaled={state.PreviousType}, now={live.StartType}); nothing was changed");
            if (live.StartType is not (ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed))
                return new MutationResult(MutationStatus.Failed, "service is not automatic anymore; nothing was changed");

            _probe.SetStartType(state.Name, ServiceStartType.Disabled);
            var after = _probe.GetService(state.Name);
            if (after.StartType != ServiceStartType.Disabled)
                return new MutationResult(MutationStatus.Failed, "start type remained non-Disabled; post-state verification failed");
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
        if (state is null)
            return new MutationResult(MutationStatus.Failed, "journaled before-state failed to deserialize");
        if (state.PreviousType is not (ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed))
            return new MutationResult(MutationStatus.Failed, "journaled previous type is not restorable");
        try
        {
            var live = _probe.GetService(state.Name);
            if (live.StartType == state.PreviousType)
                return new MutationResult(MutationStatus.Failed,
                    "service already has the journaled start type; the disable we journaled is not in effect (external change?)");

            _probe.SetStartType(state.Name, state.PreviousType);
            var after = _probe.GetService(state.Name);
            if (after.StartType != state.PreviousType)
                return new MutationResult(MutationStatus.Failed, "start type did not return to the journaled value; post-state verification failed");
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
