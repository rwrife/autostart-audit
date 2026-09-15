using System.Xml;
using System.Xml.Linq;
using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Scan;

/// <summary>Read-only scheduled-task inventory for logon and boot triggers.</summary>
public sealed class ScheduledTaskSource : IAutoStartSource
{
    public const string Kind = "scheduled-task";

    private readonly IScheduledTaskProbe _probe;
    private readonly IElevationProbe _elevation;

    public ScheduledTaskSource(IScheduledTaskProbe probe, IElevationProbe elevation)
    {
        _probe = probe;
        _elevation = elevation;
    }

    public string SourceKind => Kind;

    public SourceScanResult Scan(ScanContext context)
    {
        var snapshot = _probe.Enumerate();
        var entries = new List<AutoStartEntry>();
        var elevation = ElevationObservation.Read(_elevation, "hidden or protected task folders");
        var observations = new List<SourceObservation>(snapshot.Observations) { elevation };

        foreach (var task in snapshot.Tasks)
        {
            try
            {
                var parsed = Parse(task.Xml);
                if (parsed.Triggers.Count == 0)
                    continue;

                if (parsed.Evidence.Count > 0)
                    observations.Add(new SourceObservation { Id = task.Path, Health = ObservationHealth.Unknown,
                        Detail = string.Join("; ", parsed.Evidence) });
                entries.Add(new AutoStartEntry
                {
                    SourceKind = Kind,
                    Scope = "machine",
                    StableKey = StableKey.BuildWithTargets(Kind, "machine", task.Path, parsed.TargetPaths),
                    DisplayName = task.Name,
                    SourceName = task.Path,
                    TargetPaths = parsed.TargetPaths,
                    RawValueSnapshot = task.Xml,
                    State = task.State.ToLowerInvariant(),
                    Enabled = task.Enabled,
                    TriggerSummary = string.Join(", ", parsed.Triggers),
                    Evidence = parsed.Evidence,
                    Signing = SigningStatus.Unverified,
                    Observation = parsed.Evidence.Count > 0 ? ObservationHealth.Unknown : ObservationHealth.Ok,
                });
            }
            catch (Exception ex) when (ex is XmlException or InvalidOperationException)
            {
                observations.Add(new SourceObservation
                {
                    Id = task.Path,
                    Health = ObservationHealth.Unknown,
                    Detail = $"malformed task XML: {ex.Message}",
                });
            }
        }

        var dataObservations = observations.Where(observation => observation.Id != "process-elevation").ToList();
        return new SourceScanResult
        {
            Entries = entries,
            Observations = observations,
            ForcedCapability = ElevationObservation.ApplyToCapability(elevation, dataObservations),
        };
    }

    private static ParsedTask Parse(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException("task document has no root element");
        if (root.Name.LocalName != "Task" || (root.Name.NamespaceName.Length > 0
            && root.Name.NamespaceName != "http://schemas.microsoft.com/windows/2004/02/mit/task"))
            throw new InvalidOperationException("document is not a Task Scheduler task");
        var ns = root.Name.Namespace;
        var triggerContainer = root.Elements(ns + "Triggers").SingleOrDefault();
        foreach (var trigger in root.Descendants().Where(e => e.Name.LocalName is "BootTrigger" or "LogonTrigger"))
            if (trigger.Parent != triggerContainer || trigger.Name.Namespace != ns)
                throw new InvalidOperationException("startup trigger is outside the task Triggers container");
        foreach (var container in root.Descendants().Where(e => e.Name.LocalName is "Triggers" or "Actions"))
            if (container.Parent != root || container.Name.Namespace != ns)
                throw new InvalidOperationException("task container is in an invalid schema position");
        var triggers = (triggerContainer?.Elements() ?? Enumerable.Empty<XElement>())
            .Where(e => e.Name.LocalName is "LogonTrigger" or "BootTrigger")
            .Select(e => e.Name.LocalName == "LogonTrigger" ? "logon" : "boot")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var targets = new List<string>();
        var evidence = new List<string>();
        var actions = root.Elements(ns + "Actions").SingleOrDefault();
        if (actions is not null)
        {
            foreach (var action in actions.Elements())
            {
                if (action.Name == ns + "Exec")
                {
                    var command = action.Elements(ns + "Command").SingleOrDefault()?.Value.Trim();
                    if (!string.IsNullOrEmpty(command))
                        targets.Add(command);
                    else
                        evidence.Add("exec action has no command");
                }
                else
                {
                    evidence.Add($"unsupported action: {action.Name.LocalName}");
                }
            }
        }

        if (actions is null || !actions.HasElements)
            evidence.Add("task has no readable actions");
        return new ParsedTask(triggers, targets, evidence);
    }

    private sealed record ParsedTask(
        IReadOnlyList<string> Triggers,
        IReadOnlyList<string> TargetPaths,
        IReadOnlyList<string> Evidence);
}
