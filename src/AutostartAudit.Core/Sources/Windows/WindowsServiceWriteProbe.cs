using System.ComponentModel;
using System.Runtime.InteropServices;
using AutostartAudit.Core.Quarantine;
using Microsoft.Win32.SafeHandles;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// SCM write boundary for the quarantine layer only. Opens services with
/// CHANGE_CONFIG rights and changes nothing but the start type (plus the
/// delayed-auto flag, which is inseparable from "automatic" for an exact
/// round-trip). Running service state is never touched.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsServiceWriteProbe : IServiceWriteProbe
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;
    private const int ErrorAccessDenied = 5;

    public ServiceSnapshot GetService(string name)
    {
        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
            throw Win32Failure($"OpenSCManager for {name}");
        using var service = OpenService(manager, name, ServiceQueryConfig | ServiceChangeConfig);
        if (service.IsInvalid)
            throw Win32Failure($"OpenService for {name}");

        _ = QueryServiceConfig(service, nint.Zero, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (required == 0 || error != 122 /* ERROR_INSUFFICIENT_BUFFER */)
            throw new QuarantineUnknownException(name, $"QueryServiceConfig sizing failed with Win32 error {error}");

        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!QueryServiceConfig(service, buffer, required, out _))
                throw Win32Failure($"QueryServiceConfig for {name}");
            var config = Marshal.PtrToStructure<QueryServiceConfigData>(buffer);
            var binaryPath = Marshal.PtrToStringUni(config.BinaryPathName) ?? string.Empty;
            var startType = MapStartType(config.StartType);
            if (startType == ServiceStartType.Automatic)
            {
                // Exact round-trip needs the delayed flag; a read failure here
                // makes the state unknown rather than assumed.
                var delayed = 0;
                if (!QueryServiceConfig2(service, ServiceConfigDelayedAutoStartInfo, ref delayed, sizeof(int), out _))
                    throw Win32Failure($"QueryServiceConfig2 (delayed-auto) for {name}");
                if (delayed != 0)
                    startType = ServiceStartType.AutomaticDelayed;
            }
            var state = ReadState(service);
            var displayName = Marshal.PtrToStringUni(config.DisplayName) ?? name;
            return new ServiceSnapshot(name, displayName, startType, binaryPath, state);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void SetStartType(string name, ServiceStartType startType)
    {
        uint nativeStart = startType switch
        {
            ServiceStartType.Disabled => 4,
            ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed => 2,
            ServiceStartType.Manual => 3,
            _ => throw new QuarantineUnknownException(name, $"refusing to write start type {startType}"),
        };

        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
            throw Win32Failure($"OpenSCManager for {name}");
        using var service = OpenService(manager, name, ServiceQueryConfig | ServiceChangeConfig);
        if (service.IsInvalid)
            throw Win32Failure($"OpenService for {name}");

        if (!ChangeServiceConfig(service, ServiceNoChange, nativeStart, ServiceNoChange,
                null, null, null, null, null, null, null))
            throw Win32Failure($"ChangeServiceConfig for {name}");

        if (startType is ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed)
        {
            var delayed = startType == ServiceStartType.AutomaticDelayed ? 1 : 0;
            if (!ChangeServiceConfig2(service, ServiceConfigDelayedAutoStartInfo, ref delayed, sizeof(int), out _))
                throw Win32Failure($"ChangeServiceConfig2 (delayed-auto) for {name}");
        }
    }

    private static string ReadState(SafeServiceHandle service)
    {
        if (!QueryServiceStatus(service, out var status))
            throw Win32Failure($"QueryServiceStatus for {service}");
        return status.CurrentState switch
        {
            1 => "Stopped",
            2 => "StartPending",
            3 => "StopPending",
            4 => "Running",
            5 => "ContinuePending",
            6 => "PausePending",
            7 => "Paused",
            _ => "Unknown",
        };
    }

    private static Exception Win32Failure(string what)
    {
        var error = Marshal.GetLastWin32Error();
        var message = new Win32Exception(error).Message;
        return error == ErrorAccessDenied
            ? new QuarantineDeniedException(what, $"Win32 error {error}: {message}")
            : new QuarantineUnknownException(what, $"Win32 error {error}: {message}");
    }

    private static ServiceStartType MapStartType(uint value) => value switch
    {
        0 => ServiceStartType.Boot,
        1 => ServiceStartType.System,
        2 => ServiceStartType.Automatic,
        3 => ServiceStartType.Manual,
        4 => ServiceStartType.Disabled,
        _ => ServiceStartType.Unknown,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfigData
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public nint BinaryPathName;
        public nint LoadOrderGroup;
        public uint TagId;
        public nint Dependencies;
        public nint ServiceStartName;
        public nint DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(SafeServiceHandle manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(SafeServiceHandle service, nint config, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2(
        SafeServiceHandle service, uint infoLevel, ref int buffer, int bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(
        SafeServiceHandle service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        uint[]? tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(
        SafeServiceHandle service, uint infoLevel, ref int delayedAutostart, int bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(SafeServiceHandle service, out ServiceStatus serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint serviceHandle);
}
