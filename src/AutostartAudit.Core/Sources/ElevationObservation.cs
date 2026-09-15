using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Scan;

internal static class ElevationObservation
{
    public static SourceObservation Read(IElevationProbe probe, string protectedScope)
    {
        bool elevated;
        try
        {
            elevated = probe.IsElevated();
        }
        catch (Exception ex)
        {
            return new SourceObservation
            {
                Id = "process-elevation",
                Health = ObservationHealth.Unknown,
                Detail = $"could not determine process elevation: {ex.GetType().Name}: {ex.Message}",
            };
        }

        return elevated ? new SourceObservation
        {
            Id = "process-elevation",
            Health = ObservationHealth.Ok,
            Detail = "process token is elevated; individual read results still determine completeness",
        }
        : new SourceObservation
        {
            Id = "process-elevation",
            Health = ObservationHealth.Unknown,
            Detail = $"process is not elevated; some {protectedScope} may not be visible",
        };
    }

    public static SourceCapability ApplyToCapability(
        SourceObservation elevation,
        IReadOnlyList<SourceObservation> dataObservations)
    {
        var dataCapability = ScanEngine.DeriveCapability(dataObservations);
        if (elevation.Health == ObservationHealth.Ok)
            return dataCapability;
        return dataCapability == SourceCapability.Scanned
            ? SourceCapability.Partial
            : dataCapability;
    }
}
