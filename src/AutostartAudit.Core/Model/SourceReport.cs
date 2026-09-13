namespace AutostartAudit.Core.Model;

/// <summary>
/// Per-source scan report. The presence of a source in this list with a
/// non-Scanned capability — and per-sub-source <see cref="Observations"/> —
/// is how a partial scan stays honest: the scan footer can state exactly
/// which sub-sources were not read and why.
/// </summary>
public sealed record SourceReport
{
    /// <summary>Source identifier, e.g. "run-key", "startup-folder".</summary>
    public required string SourceKind { get; init; }

    /// <summary>Whether the source was fully scanned, partial, denied, or unsupported.</summary>
    public required SourceCapability Capability { get; init; }

    /// <summary>Source-level detail (unsupported reason, error text). Null when nothing to note.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// One observation per sub-source the source attempted to read, including
    /// failed attempts. An empty list means nothing was even attempted, which
    /// the engine classifies as <see cref="SourceCapability.Denied"/>, never "clean".
    /// </summary>
    public IReadOnlyList<SourceObservation> Observations { get; init; } = Array.Empty<SourceObservation>();
}
