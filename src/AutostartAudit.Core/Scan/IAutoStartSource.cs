namespace AutostartAudit.Core.Scan;

/// <summary>
/// Ambient facts a source may consult while scanning. Elevation state is the
/// key one: sources that cannot read their data unelevated must report a
/// capability, never an empty list.
/// </summary>
public sealed record ScanContext
{
    public static ScanContext Default { get; } = new();

    /// <summary>Whether the process is running elevated (admin). Conservative default: false.</summary>
    public bool IsElevated { get; init; }
}

/// <summary>
/// What one source produced in one scan pass: entries plus one observation
/// per sub-source it attempted (including failed attempts), and an optional
/// forced capability for sources that know they cannot run at all.
/// </summary>
public sealed record SourceScanResult
{
    public static SourceScanResult Nothing { get; } = new();

    public IReadOnlyList<Model.AutoStartEntry> Entries { get; init; } = Array.Empty<Model.AutoStartEntry>();

    public IReadOnlyList<Model.SourceObservation> Observations { get; init; } = Array.Empty<Model.SourceObservation>();

    /// <summary>When set, the engine uses this capability verbatim instead of deriving it.</summary>
    public Model.SourceCapability? ForcedCapability { get; init; }

    /// <summary>Source-level note (unsupported reason, etc.). Null when nothing to note.</summary>
    public string? Detail { get; init; }
}

/// <summary>A registered auto-start source that the scan engine can run.</summary>
public interface IAutoStartSource
{
    /// <summary>Stable source identifier, e.g. "run-key".</summary>
    string SourceKind { get; }

    SourceScanResult Scan(ScanContext context);
}

/// <summary>
/// Placeholder source for environments/builds where a real source cannot run.
/// Reports <see cref="Model.SourceCapability.Unsupported"/> explicitly so a
/// missing capability is visible in the scan document.
/// </summary>
public sealed class UnsupportedSource : IAutoStartSource
{
    private readonly string _reason;

    public UnsupportedSource(string sourceKind, string reason)
    {
        SourceKind = sourceKind;
        _reason = reason;
    }

    public string SourceKind { get; }

    public SourceScanResult Scan(ScanContext context) => new()
    {
        ForcedCapability = Model.SourceCapability.Unsupported,
        Detail = _reason,
    };
}
