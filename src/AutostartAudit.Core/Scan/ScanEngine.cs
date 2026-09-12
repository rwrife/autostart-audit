using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// Orchestrates the registered auto-start sources into one <see cref="ScanDocument"/>.
/// Issue #2 registers RunKeySource and StartupFolderSource here; until sources
/// are registered the engine returns an explicitly empty document.
/// </summary>
public static class ScanEngine
{
    public static ScanDocument ScanAll()
    {
        // No sources registered yet (issue #2 wires them in). Returning
        // ScanDocument.Empty here is honest only because the scan has not
        // been performed against any source; it is never presented as
        // evidence of a clean machine.
        return ScanDocument.Empty;
    }
}
