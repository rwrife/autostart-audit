namespace AutostartAudit.Core.Scan;

/// <summary>Registry value kind, mapped from the platform enum at the OS boundary.</summary>
public enum ValueKind
{
    String,
    ExpandString,
    Other,
}

/// <summary>One raw registry value as read, verbatim (no variable expansion applied).</summary>
public sealed record RegistryValue(string Name, ValueKind Kind, string Data);

/// <summary>A successfully opened registry key with all its values.</summary>
public sealed record RegistryKeySnapshot(string Path, IReadOnlyList<RegistryValue> Values);

/// <summary>Reading was attempted and refused (access denied / requires elevation).</summary>
public sealed class RegistryAccessDeniedException(string keyPath, string detail)
    : Exception($"access denied reading {keyPath}: {detail}");

/// <summary>Reading failed and the reason is not established.</summary>
public sealed class RegistryUnknownException(string keyPath, string detail)
    : Exception($"unknown failure reading {keyPath}: {detail}");

/// <summary>Registry access is not available on this OS/build at all.</summary>
public sealed class RegistryUnsupportedException(string detail)
    : Exception($"registry not available: {detail}");

/// <summary>
/// Read-only registry probe. Implementations translate platform failures into
/// the three explicit exception types above — a failed read is never reported
/// as "key absent with no values".
/// </summary>
public interface IRegistryProbe
{
    /// <summary>
    /// Returns the key snapshot, or null when the key provably does not exist
    /// (which is honest absence). Throws <see cref="RegistryAccessDeniedException"/>,
    /// <see cref="RegistryUnknownException"/>, or <see cref="RegistryUnsupportedException"/>
    /// when the key's contents could not be established.
    /// </summary>
    RegistryKeySnapshot? TryOpen(string hiveName, string subPath);
}
