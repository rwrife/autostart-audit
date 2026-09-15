using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Scan;

public enum ServiceStartType
{
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
    Boot,
    System,
    Unknown,
    AutomaticDelayUnknown,
}

/// <summary>Read-only SCM configuration snapshot.</summary>
public sealed record ServiceSnapshot(
    string Name,
    string DisplayName,
    ServiceStartType StartType,
    string BinaryPath,
    string State);

public sealed record ServiceEnumeration(
    IReadOnlyList<ServiceSnapshot> Services,
    IReadOnlyList<SourceObservation> Observations);

public interface IServiceProbe
{
    ServiceEnumeration Enumerate();
}
