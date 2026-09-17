using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// Scans HKLM/HKCU <c>Run</c> and <c>RunOnce</c> keys. Observation-truth
/// boundary: a key that cannot be opened yields a <see cref="ObservationHealth.Denied"/>
/// or <see cref="ObservationHealth.Unknown"/> observation — never a silent
/// empty list. A key that provably does not exist is honest absence (Ok with
/// no entries).
/// </summary>
public sealed class RunKeySource : IAutoStartSource
{
    public const string Kind = "run-key";

    private static readonly (string Hive, string SubPath, string Scope)[] Keys =
    {
        ("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "machine"),
        ("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "machine"),
        ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "user"),
        ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "user"),
    };

    private readonly IRegistryProbe _probe;

    public RunKeySource(IRegistryProbe probe) => _probe = probe;

    public string SourceKind => Kind;

    public SourceScanResult Scan(ScanContext context)
    {
        var entries = new List<AutoStartEntry>();
        var observations = new List<SourceObservation>();

        try
        {
            foreach (var (hive, subPath, scope) in Keys)
            {
                var keyPath = $"{hive}\\{subPath}";
                RegistryKeySnapshot? snap;
                try
                {
                    snap = _probe.TryOpen(hive, subPath);
                }
                catch (RegistryAccessDeniedException ex)
                {
                    observations.Add(new SourceObservation { Id = keyPath, Health = ObservationHealth.Denied, Detail = ex.Message });
                    continue;
                }
                catch (RegistryUnknownException ex)
                {
                    observations.Add(new SourceObservation { Id = keyPath, Health = ObservationHealth.Unknown, Detail = ex.Message });
                    continue;
                }

                // Absent key: honest absence, recorded as an Ok observation.
                observations.Add(new SourceObservation { Id = keyPath, Health = ObservationHealth.Ok, Detail = snap is null ? "key not present" : null });
                if (snap is null)
                    continue;

                foreach (var value in snap.Values)
                {
                    // The unnamed (default) value is not an autostart entry.
                    if (string.IsNullOrWhiteSpace(value.Name))
                        continue;

                    entries.Add(EntryFrom(keyPath, scope, value));
                }
            }
        }
        catch (RegistryUnsupportedException ex)
        {
            return new SourceScanResult
            {
                ForcedCapability = SourceCapability.Unsupported,
                Detail = ex.Message,
            };
        }

        return new SourceScanResult { Entries = entries, Observations = observations };
    }

    private static AutoStartEntry EntryFrom(string keyPath, string scope, RegistryValue value)
    {
        var raw = value.Data;
        return new AutoStartEntry
        {
            SourceKind = Kind,
            Scope = scope,
            StableKey = StableKey.BuildWithTargets(Kind, scope, $@"{keyPath}\{value.Name}", TargetPathParser.Parse(raw)),
            NativeKey = StableKey.Normalize($@"{keyPath}\{value.Name}"),
            DisplayName = value.Name,
            TargetPaths = TargetPathParser.Parse(raw),
            RawValueSnapshot = raw,
            // Signing verification lands in issue #4; until then the honest
            // state is Unverified, never Unsigned.
            Signing = SigningStatus.Unverified,
            Observation = ObservationHealth.Ok,
        };
    }
}
