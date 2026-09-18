using Microsoft.Win32;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// OS-boundary writable registry view for the quarantine layer only. Failure
/// vocabulary matches the read probe so the strategy layer can classify
/// denied vs unknown consistently.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsRegistryWriteProbe : IRegistryWriteProbe
{
    public IRegistryKeyWriter OpenWritable(string hiveName, string subPath)
    {
        RegistryHive hive = hiveName switch
        {
            "HKLM" => RegistryHive.LocalMachine,
            "HKCU" => RegistryHive.CurrentUser,
            _ => throw new RegistryUnknownException($"{hiveName}\\{subPath}", $"unknown hive '{hiveName}'"),
        };

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            var key = baseKey.OpenSubKey(subPath, writable: true)
                ?? throw new RegistryUnknownException($"{hiveName}\\{subPath}", "key does not exist; refusing to create it");
            return new Writer(key, $"{hiveName}\\{subPath}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new RegistryAccessDeniedException($"{hiveName}\\{subPath}", ex.Message);
        }
        catch (Exception ex) when (ex is not RegistryUnknownException and not RegistryAccessDeniedException and not RegistryUnsupportedException)
        {
            throw new RegistryUnknownException($"{hiveName}\\{subPath}", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class Writer : IRegistryKeyWriter
    {
        private readonly RegistryKey _key;
        private readonly string _path;

        public Writer(RegistryKey key, string path)
        {
            _key = key;
            _path = path;
        }

        public RegistryValue? TryGetValue(string name)
        {
            try
            {
                if (!GetValueNames().Contains(name, StringComparer.Ordinal))
                    return null; // provably absent
                var raw = _key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var (kind, data) = raw switch
                {
                    string s => (ValueKind.String, s),
                    string[] arr => (ValueKind.String, string.Join("\0", arr)),
                    null => (ValueKind.String, string.Empty),
                    _ => (ValueKind.Other, Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
                };
                kind = _key.GetValueKind(name) switch
                {
                    RegistryValueKind.ExpandString => ValueKind.ExpandString,
                    RegistryValueKind.String => ValueKind.String,
                    _ => kind,
                };
                return new RegistryValue(name, kind, data);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new RegistryAccessDeniedException(_path, ex.Message);
            }
            catch (Exception ex)
            {
                throw new RegistryUnknownException(_path, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        public bool ValueMissing(string name)
        {
            try
            {
                return !GetValueNames().Contains(name, StringComparer.Ordinal);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new RegistryAccessDeniedException(_path, ex.Message);
            }
            catch (Exception ex)
            {
                throw new RegistryUnknownException(_path, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        public void SetValue(string name, ValueKind kind, string data)
        {
            try
            {
                var native = kind switch
                {
                    ValueKind.ExpandString => RegistryValueKind.ExpandString,
                    ValueKind.String => RegistryValueKind.String,
                    _ => throw new RegistryUnknownException(_path, $"cannot write value kind {kind} verbatim"),
                };
                _key.SetValue(name, data, native);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new RegistryAccessDeniedException(_path, ex.Message);
            }
            catch (RegistryUnknownException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RegistryUnknownException(_path, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        public void DeleteValue(string name)
        {
            try
            {
                _key.DeleteValue(name, throwOnMissingValue: true);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new RegistryAccessDeniedException(_path, ex.Message);
            }
            catch (Exception ex)
            {
                throw new RegistryUnknownException(_path, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private string[] GetValueNames() => _key.GetValueNames();

        public void Dispose() => _key.Dispose();
    }
}
