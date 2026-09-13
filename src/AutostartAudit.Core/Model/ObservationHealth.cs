namespace AutostartAudit.Core.Model;

/// <summary>
/// Observation-truth boundary for a single observed entry or sub-source read.
/// A read that failed or could not be established is <see cref="Denied"/> or
/// <see cref="Unknown"/> — never silently "absent" or "clean". An empty result
/// is only honest when the corresponding capability is <see cref="SourceCapability.Scanned"/>.
/// </summary>
public enum ObservationHealth
{
    /// <summary>The observation was fully readable.</summary>
    Ok,

    /// <summary>Reading was attempted and refused (access denied, requires elevation).</summary>
    Denied,

    /// <summary>The observation could not be completed and the reason is not established.</summary>
    Unknown,
}
