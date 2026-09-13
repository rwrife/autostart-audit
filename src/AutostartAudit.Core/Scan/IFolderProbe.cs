namespace AutostartAudit.Core.Scan;

/// <summary>One directory entry as reported by the filesystem view.</summary>
public sealed record FsEntry(string Name, bool IsDirectory, long Length);

/// <summary>
/// Read-only directory view used by StartupFolderSource. Implementations
/// translate platform failures into the explicit exception types below.
/// </summary>
public interface IFolderProbe
{
    /// <summary>
    /// Lists the directory, or returns null when the folder provably does not
    /// exist (honest absence). Throws when contents could not be established.
    /// </summary>
    IReadOnlyList<FsEntry>? TryEnumerate(string folderPath);
}

/// <summary>Enumeration was attempted and refused (access denied).</summary>
public sealed class FolderAccessDeniedException(string folderPath, string detail)
    : Exception($"access denied enumerating {folderPath}: {detail}");

/// <summary>Enumeration failed and the reason is not established.</summary>
public sealed class FolderUnknownException(string folderPath, string detail)
    : Exception($"unknown failure enumerating {folderPath}: {detail}");
