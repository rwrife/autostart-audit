using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Scan;

/// <summary>Read-only snapshot of one registered task.</summary>
public sealed record ScheduledTaskSnapshot(string Path, string Name, string State, bool Enabled, string Xml);

/// <summary>Tasks successfully read plus every folder/task scope attempted.</summary>
public sealed record ScheduledTaskEnumeration(
    IReadOnlyList<ScheduledTaskSnapshot> Tasks,
    IReadOnlyList<SourceObservation> Observations);

public interface IScheduledTaskProbe
{
    ScheduledTaskEnumeration Enumerate();
}
