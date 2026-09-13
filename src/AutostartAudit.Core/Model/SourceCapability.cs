namespace AutostartAudit.Core.Model;

/// <summary>
/// Coarse capability of one scan source over a scan run. A source that could
/// not be read is <see cref="Denied"/> or <see cref="Unsupported"/>, never
/// <see cref="Scanned"/> with an empty entry list pretending to be clean.
/// Coarse-grained unknowns are reported as <see cref="Denied"/> at this level;
/// the per-sub-source detail (including genuinely unknown causes) is always
/// preserved verbatim in <see cref="SourceReport.Observations"/>.
/// </summary>
public enum SourceCapability
{
    /// <summary>Every sub-source of this source was fully readable.</summary>
    Scanned,

    /// <summary>Some sub-sources were readable, others were refused or unresolved.</summary>
    Partial,

    /// <summary>No sub-source could be read (refused, or cause not established).</summary>
    Denied,

    /// <summary>This build or OS does not implement the source (or a path it needs does not exist here).</summary>
    Unsupported,
}
