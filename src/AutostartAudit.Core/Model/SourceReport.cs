namespace AutostartAudit.Core.Model;

/// <summary>
/// Per-source scan report. The presence of a source in this list with a
/// non-Ok capability is how a partial scan stays honest: the scan footer can
/// state exactly which sources were not read and why.
/// </summary>
public sealed record SourceReport
{
    /// <summary>Source identifier, e.g. "run-key", "startup-folder".</summary>
    public required string SourceKind { get; init; }

    /// <summary>Whether the source was fully readable, denied, unknown, or unsupported.</summary>
    public required Capability Capability { get; init; }

    /// <summary>Human-readable detail (error text, elevation note). Null when capability is Ok.</summary>
    public string? Detail { get; init; }
}
