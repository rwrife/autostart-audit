namespace AutostartAudit.Core.Scan;

/// <summary>Injectable, read-only query of the current process token.</summary>
public interface IElevationProbe
{
    bool IsElevated();
}

/// <summary>Conservative fallback used only when no native token probe is available.</summary>
public sealed class UnelevatedProbe : IElevationProbe
{
    public bool IsElevated() => false;
}
