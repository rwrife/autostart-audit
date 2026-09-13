using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// OS-boundary registry probe. Failures are translated into the explicit
/// exception vocabulary of <see cref="IRegistryProbe"/> so the scan layer can
/// distinguish denied / unknown / absent — never conflating them with "no
/// entries". Returns null only when the key provably does not exist.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsRegistryProbe : IRegistryProbe
{
    public RegistryKeySnapshot? TryOpen(string hiveName, string subPath)
    {
        RegistryHive hive = hiveName switch
        {
            "HKLM" => RegistryHive.LocalMachine,
            "HKCU" => RegistryHive.CurrentUser,
            _ => throw new RegistryUnknownException($"{hiveName}\\{subPath}", $"unknown hive '{hiveName}'"),
        };

        RegistryKey key;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            var opened = baseKey.OpenSubKey(subPath);
            if (opened is null)
                return null; // provably absent
            key = opened;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new RegistryAccessDeniedException($"{hiveName}\\{subPath}", ex.Message);
        }
        catch (Exception ex) when (ex is not RegistryUnknownException and not RegistryAccessDeniedException and not RegistryUnsupportedException)
        {
            throw new RegistryUnknownException($"{hiveName}\\{subPath}", $"{ex.GetType().Name}: {ex.Message}");
        }

        using (key)
        {
            var values = new List<RegistryValue>();
            try
            {
                foreach (var name in key.GetValueNames())
                {
                    var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    var (kind, data) = raw switch
                    {
                        string s => (ValueKind.String, s),
                        string[] arr => (ValueKind.String, string.Join("\0", arr)),
                        null => (ValueKind.String, string.Empty),
                        byte[] bytes => (ValueKind.Other, Convert.ToHexString(bytes)),
                        _ => (ValueKind.Other, Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
                    };
                    // Map the actual registry kind more precisely when available.
                    kind = key.GetValueKind(name) switch
                    {
                        RegistryValueKind.ExpandString => ValueKind.ExpandString,
                        RegistryValueKind.String => ValueKind.String,
                        RegistryValueKind.MultiString => kind,
                        _ => ValueKind.Other,
                    };
                    values.Add(new RegistryValue(name, kind, data));
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new RegistryAccessDeniedException($"{hiveName}\\{subPath}", ex.Message);
            }
            catch (Exception ex) when (ex is not RegistryUnknownException and not RegistryAccessDeniedException and not RegistryUnsupportedException)
            {
                throw new RegistryUnknownException($"{hiveName}\\{subPath}", $"{ex.GetType().Name}: {ex.Message}");
            }

            return new RegistryKeySnapshot($"{hiveName}\\{subPath}", values);
        }
    }
}
