namespace AutostartAudit.Core.Scan;

/// <summary>
/// Write-capable SCM view used only by the quarantine layer. Implementations
/// throw <see cref="Quarantine.QuarantineDeniedException"/> or
/// <see cref="Quarantine.QuarantineUnknownException"/>.
/// </summary>
public interface IServiceWriteProbe
{
    /// <summary>Re-read one service configuration by name, throwing when it cannot be read.</summary>
    ServiceSnapshot GetService(string name);

    /// <summary>
    /// Persistently set the start type. Setting <see cref="ServiceStartType.AutomaticDelayed"/>
    /// must set automatic start plus the delayed-auto flag; <see cref="ServiceStartType.Automatic"/>
    /// must clear it.
    /// </summary>
    void SetStartType(string name, ServiceStartType startType);
}
