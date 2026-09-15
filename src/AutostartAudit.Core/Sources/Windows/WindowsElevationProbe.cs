using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AutostartAudit.Core.Scan;

/// <summary>Reads TokenElevation from the current process token.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsElevationProbe : IElevationProbe
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    public bool IsElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var elevation = 0;
            if (!GetTokenInformation(token, TokenElevation, ref elevation, sizeof(int), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return elevation != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        ref int tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
