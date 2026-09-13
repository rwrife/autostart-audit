using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// Orchestrates the registered auto-start sources into one <see cref="ScanDocument"/>.
/// A source that is not registered, fails unexpectedly, or attempts nothing is
/// classified honestly — never dropped and never shown as "clean".
/// </summary>
public static class ScanEngine
{
    /// <summary>Runs the given sources. Used by tests and by hosts that assemble their own source list.</summary>
    public static ScanDocument ScanAll(IReadOnlyList<IAutoStartSource> sources, ScanContext? context = null)
    {
        context ??= ScanContext.Default;
        var entries = new List<AutoStartEntry>();
        var reports = new List<SourceReport>();

        foreach (var source in sources)
        {
            SourceScanResult result;
            try
            {
                result = source.Scan(context);
            }
            catch (Exception ex)
            {
                // Any unexpected failure becomes an Unknown source report —
                // the scan stays honest instead of silently missing a source.
                reports.Add(new SourceReport
                {
                    SourceKind = source.SourceKind,
                    Capability = SourceCapability.Denied,
                    Detail = $"scan failed: {ex.GetType().Name}: {ex.Message}",
                });
                continue;
            }

            reports.Add(Report(source.SourceKind, result));
            entries.AddRange(result.Entries);
        }

        return new ScanDocument
        {
            Entries = entries,
            Sources = reports,
            ScanComplete = reports.Count > 0 && reports.All(r => r.Capability == SourceCapability.Scanned),
        };
    }

    /// <summary>Runs the default source set for the current OS.</summary>
    public static ScanDocument ScanAll() => ScanAll(DefaultSources.ForCurrentOS());

    internal static SourceReport Report(string sourceKind, SourceScanResult result)
    {
        var capability = result.ForcedCapability ?? DeriveCapability(result.Observations);
        return new SourceReport
        {
            SourceKind = sourceKind,
            Capability = capability,
            Detail = result.Detail,
            Observations = result.Observations,
        };
    }

    internal static SourceCapability DeriveCapability(IReadOnlyList<SourceObservation> observations)
    {
        // Nothing attempted at all is not a clean result — classify as Denied.
        if (observations.Count == 0)
            return SourceCapability.Denied;

        var ok = observations.Count(o => o.Health == ObservationHealth.Ok);
        if (ok == observations.Count)
            return SourceCapability.Scanned;
        if (ok == 0)
            return SourceCapability.Denied;
        return SourceCapability.Partial;
    }
}
