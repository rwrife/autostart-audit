using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Scan;

/// <summary>Read-only inventory of automatic and automatic-delayed services.</summary>
public sealed class ServiceSource : IAutoStartSource
{
    public const string Kind = "service";

    private readonly IServiceProbe _probe;
    private readonly IElevationProbe _elevation;

    public ServiceSource(IServiceProbe probe, IElevationProbe elevation)
    {
        _probe = probe;
        _elevation = elevation;
    }

    public string SourceKind => Kind;

    public SourceScanResult Scan(ScanContext context)
    {
        var snapshot = _probe.Enumerate();
        var elevation = ElevationObservation.Read(_elevation, "protected service configurations");
        var observations = new List<SourceObservation>(snapshot.Observations) { elevation };
        var entries = new List<AutoStartEntry>();
        foreach (var service in snapshot.Services.Where(service =>
                     service.StartType is ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed or ServiceStartType.AutomaticDelayUnknown))
        {
            var targets = ParseBinaryPath(service.BinaryPath);
            var health = targets.Count == 0 || service.StartType == ServiceStartType.AutomaticDelayUnknown
                ? ObservationHealth.Unknown : ObservationHealth.Ok;
            if (targets.Count == 0)
            {
                observations.Add(new SourceObservation
                {
                    Id = $"service:{service.Name}:binary-path",
                    Health = ObservationHealth.Unknown,
                    Detail = "automatic service has an empty, ambiguous, or malformed binary path; inspect the raw command",
                });
            }
            entries.Add(ToEntry(service, targets, health));
        }
        var dataObservations = observations.Where(observation => observation.Id != "process-elevation").ToList();
        return new SourceScanResult
        {
            Entries = entries,
            Observations = observations,
            ForcedCapability = ElevationObservation.ApplyToCapability(elevation, dataObservations),
        };
    }

    // SCM command lines are not argv arrays: an unquoted path with spaces is
    // ambiguous (e.g. C:\Program.exe can win). Preserve raw evidence, never guess.
    private static IReadOnlyList<string> ParseBinaryPath(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0 || value.Any(char.IsControl))
            return Array.Empty<string>();
        string path;
        if (value[0] == '"')
        {
            var end = value.IndexOf('"', 1);
            if (end <= 1 || (end + 1 < value.Length && !char.IsWhiteSpace(value[end + 1])))
                return Array.Empty<string>();
            path = value[1..end];
        }
        else
        {
            path = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            if (path.Contains('"') || (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".com", StringComparison.OrdinalIgnoreCase)))
                return Array.Empty<string>();
        }
        return string.IsNullOrWhiteSpace(path) ? Array.Empty<string>() : new[] { path };
    }

    private static AutoStartEntry ToEntry(
        ServiceSnapshot service,
        IReadOnlyList<string> targets,
        ObservationHealth health)
    {
        var startType = service.StartType switch
        {
            ServiceStartType.AutomaticDelayed => "automatic-delayed",
            ServiceStartType.AutomaticDelayUnknown => "automatic-delay-unknown",
            _ => "automatic",
        };
        return new AutoStartEntry
        {
            SourceKind = Kind,
            Scope = "machine",
            StableKey = StableKey.BuildWithTargets(Kind, "machine", service.Name, targets),
            NativeKey = StableKey.Normalize(service.Name),
            DisplayName = service.DisplayName,
            SourceName = service.Name,
            TargetPaths = targets,
            RawValueSnapshot = service.BinaryPath,
            State = service.State.ToLowerInvariant(),
            StartType = startType,
            Evidence = new[] { $"service name: {service.Name}", $"binary path: {service.BinaryPath}" },
            Signing = SigningStatus.Unverified,
            Observation = health,
        };
    }
}
