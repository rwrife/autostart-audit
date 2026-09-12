namespace AutostartAudit.Core.Model;

/// <summary>
/// Observation-truth boundary: a scan source that could not be read is reported
/// as <see cref="Denied"/> or <see cref="Unknown"/> (or <see cref="Unsupported"/>
/// on this OS/build), never as "nothing found". An empty result set is only
/// honest when the capability is <see cref="Ok"/>.
/// </summary>
public enum Capability
{
    /// <summary>The source was fully readable.</summary>
    Ok,

    /// <summary>This build or OS does not implement the source.</summary>
    Unsupported,

    /// <summary>Reading was attempted and refused (access denied, requires elevation).</summary>
    Denied,

    /// <summary>Reading could not be completed and the reason is not established.</summary>
    Unknown,
}
