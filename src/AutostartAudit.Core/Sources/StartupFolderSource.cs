using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// Scans the user and common Startup folders. Observation-truth boundary: a
/// folder that cannot be enumerated yields a Denied/Unknown observation, never
/// a silent empty list. A folder that provably does not exist is honest
/// absence. The per-folder paths are injected so the source is testable and
/// so non-Windows hosts receive an explicit Unsupported result.
/// </summary>
public sealed class StartupFolderSource : IAutoStartSource
{
    public const string Kind = "startup-folder";

    private static readonly string[] AcceptedExtensions =
    {
        ".exe", ".lnk", ".bat", ".cmd", ".vbs", ".js", ".wsf", ".msc", ".com", ".pif", ".url",
    };

    private readonly IFolderProbe _probe;
    private readonly IReadOnlyList<(string Path, string Scope)> _folders;

    public StartupFolderSource(IFolderProbe probe, IReadOnlyList<(string Path, string Scope)> folders)
    {
        _probe = probe;
        _folders = folders;
    }

    public string SourceKind => Kind;

    public SourceScanResult Scan(ScanContext context)
    {
        if (_folders.Count == 0)
        {
            return new SourceScanResult
            {
                ForcedCapability = SourceCapability.Unsupported,
                Detail = "no startup folders resolvable on this host",
            };
        }

        var entries = new List<AutoStartEntry>();
        var observations = new List<SourceObservation>();

        foreach (var (folderPath, scope) in _folders)
        {
            IReadOnlyList<FsEntry>? items;
            try
            {
                items = _probe.TryEnumerate(folderPath);
            }
            catch (FolderAccessDeniedException ex)
            {
                observations.Add(new SourceObservation { Id = folderPath, Health = ObservationHealth.Denied, Detail = ex.Message });
                continue;
            }
            catch (FolderUnknownException ex)
            {
                observations.Add(new SourceObservation { Id = folderPath, Health = ObservationHealth.Unknown, Detail = ex.Message });
                continue;
            }

            observations.Add(new SourceObservation { Id = folderPath, Health = ObservationHealth.Ok, Detail = items is null ? "folder not present" : null });
            if (items is null)
                continue;

            foreach (var item in items)
            {
                if (item.IsDirectory)
                    continue;
                if (!AcceptedExtensions.Any(ext => item.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var fullPath = CombinePath(folderPath, item.Name);
                entries.Add(new AutoStartEntry
                {
                    SourceKind = Kind,
                    Scope = scope,
                    StableKey = StableKey.Build(Kind, scope, fullPath),
                    NativeKey = StableKey.Normalize(fullPath),
                    DisplayName = item.Name,
                    TargetPaths = new[] { fullPath },
                    // Verbatim filesystem facts serve as the raw snapshot.
                    RawValueSnapshot = $"{fullPath} (size={item.Length})",
                    Signing = SigningStatus.Unverified,
                    Observation = ObservationHealth.Ok,
                });
            }
        }

        return new SourceScanResult { Entries = entries, Observations = observations };
    }

    private static string CombinePath(string folder, string name)
    {
        var sep = folder.Contains('\\') && !folder.Contains('/') ? '\\' : '/';
        return folder.TrimEnd('/', '\\') + sep + name;
    }
}
