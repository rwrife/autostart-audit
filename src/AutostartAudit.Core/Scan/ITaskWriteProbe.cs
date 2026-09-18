using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// Write-capable Task Scheduler view used only by the quarantine layer.
/// Implementations throw <see cref="Quarantine.QuarantineDeniedException"/> or
/// <see cref="Quarantine.QuarantineUnknownException"/> — a refused or
/// unresolved operation is never reported as success.
/// </summary>
public interface ITaskWriteProbe
{
    /// <summary>Re-read one task by path, throwing when it cannot be read.</summary>
    ScheduledTaskSnapshot GetTask(string path);

    /// <summary>Set the persistent enabled state of the task at <paramref name="path"/>.</summary>
    void SetEnabled(string path, bool enabled);
}
