namespace AutostartAudit.Core.Scan;

/// <summary>
/// Write-capable registry view used only by the quarantine layer. The
/// read-only <see cref="IRegistryProbe"/> contract stays untouched so scan
/// code can never mutate. Implementations translate platform failures into
/// the same exception vocabulary as the read path.
/// </summary>
public interface IRegistryWriteProbe
{
    /// <summary>
    /// Opens <paramref name="subPath"/> for write access under
    /// <paramref name="hiveName"/> and returns the write view, throwing the
    /// standard Registry* exceptions when the key cannot be opened for write.
    /// </summary>
    IRegistryKeyWriter OpenWritable(string hiveName, string subPath);
}

/// <summary>A registry key opened for write access.</summary>
public interface IRegistryKeyWriter : IDisposable
{
    /// <summary>
    /// Reads one value verbatim (no expansion), or throws
    /// <see cref="RegistryUnknownException"/> when the value cannot be read.
    /// A provably absent value returns null.
    /// </summary>
    RegistryValue? TryGetValue(string name);

    /// <summary>Whether the value provably does not exist on this key.</summary>
    bool ValueMissing(string name);

    /// <summary>Creates/overwrites one value verbatim. Throws on failure.</summary>
    void SetValue(string name, ValueKind kind, string data);

    /// <summary>Deletes one value. Throws on failure.</summary>
    void DeleteValue(string name);
}
