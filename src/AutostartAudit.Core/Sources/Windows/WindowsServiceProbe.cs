using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// Strictly read-only Service Control Manager enumeration. It requests only
/// enumerate/query-config rights and records per-service failures.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsServiceProbe : IServiceProbe
{
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceWin32 = 0x0030;
    private const uint ServiceStateAll = 0x0003;
    private const int ErrorMoreData = 234;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorAccessDenied = 5;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;

    public ServiceEnumeration Enumerate()
    {
        var services = new List<ServiceSnapshot>();
        var observations = new List<SourceObservation>();
        using var manager = OpenSCManager(null, null, ScManagerEnumerateService);
        if (manager.IsInvalid)
        {
            observations.Add(Win32Failure("service-control-manager", Marshal.GetLastWin32Error()));
            return new ServiceEnumeration(services, observations);
        }

        uint initialResume = 0;
        var initialSucceeded = EnumServicesStatusEx(manager, 0, ServiceWin32, ServiceStateAll, nint.Zero, 0,
            out var bytesNeeded, out _, ref initialResume, null);
        var firstError = initialSucceeded ? 0 : Marshal.GetLastWin32Error();
        if (initialSucceeded && bytesNeeded == 0)
        {
            observations.Add(new SourceObservation { Id = "service-control-manager", Health = ObservationHealth.Ok, Detail = "no Win32 services present" });
            return new ServiceEnumeration(services, observations);
        }
        if (bytesNeeded == 0 || firstError != ErrorMoreData)
        {
            observations.Add(Win32Failure("service-control-manager", firstError));
            return new ServiceEnumeration(services, observations);
        }

        var buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
        try
        {
            uint resume = 0;
            if (!EnumServicesStatusEx(manager, 0, ServiceWin32, ServiceStateAll, buffer, bytesNeeded,
                    out _, out var count, ref resume, null))
            {
                observations.Add(Win32Failure("service-control-manager", Marshal.GetLastWin32Error()));
                return new ServiceEnumeration(services, observations);
            }

            var size = Marshal.SizeOf<EnumServiceStatusProcess>();
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + checked(index * size));
                var name = Marshal.PtrToStringUni(item.ServiceName) ?? $"unknown-{index}";
                ReadService(manager, name, Marshal.PtrToStringUni(item.DisplayName) ?? name,
                    StateName(item.Status.CurrentState), services, observations);
            }
            observations.Add(new SourceObservation { Id = "service-control-manager", Health = ObservationHealth.Ok });
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new ServiceEnumeration(services, observations);
    }

    private static void ReadService(
        SafeServiceHandle manager,
        string name,
        string displayName,
        string state,
        List<ServiceSnapshot> services,
        List<SourceObservation> observations)
    {
        using var service = OpenService(manager, name, ServiceQueryConfig);
        if (service.IsInvalid)
        {
            observations.Add(Win32Failure($"service:{name}", Marshal.GetLastWin32Error()));
            return;
        }

        _ = QueryServiceConfig(service, nint.Zero, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (required == 0 || error != ErrorInsufficientBuffer)
        {
            observations.Add(Win32Failure($"service:{name}", error));
            return;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!QueryServiceConfig(service, buffer, required, out _))
            {
                observations.Add(Win32Failure($"service:{name}", Marshal.GetLastWin32Error()));
                return;
            }
            var config = Marshal.PtrToStructure<QueryServiceConfigData>(buffer);
            services.Add(CreateSnapshot(name, displayName, state,
                Marshal.PtrToStringUni(config.BinaryPathName) ?? string.Empty, config.StartType, () =>
            {
                var delayed = 0;
                if (!QueryServiceConfig2(service, ServiceConfigDelayedAutoStartInfo, ref delayed, sizeof(int), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return delayed != 0;
            }, observations));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ServiceSnapshot CreateSnapshot(string name, string displayName, string state,
        string binaryPath, uint nativeStartType, Func<bool> readDelayedStart, List<SourceObservation> observations)
    {
        var startType = MapStartType(nativeStartType);
        if (startType == ServiceStartType.Automatic)
        {
            try
            {
                if (readDelayedStart()) startType = ServiceStartType.AutomaticDelayed;
            }
            catch (Win32Exception ex)
            {
                startType = ServiceStartType.AutomaticDelayUnknown;
                observations.Add(Win32Failure($"service:{name}:delayed-auto-start", ex.NativeErrorCode));
            }
        }
        if (startType == ServiceStartType.Unknown)
            observations.Add(new SourceObservation { Id = $"service:{name}:start-type", Health = ObservationHealth.Unknown,
                Detail = $"unsupported native start type: {nativeStartType}" });
        return new ServiceSnapshot(name, displayName, startType, binaryPath, state);
    }

    private static SourceObservation Win32Failure(string id, int error)
    {
        var exception = new Win32Exception(error);
        return new SourceObservation
        {
            Id = id,
            Health = error == ErrorAccessDenied ? ObservationHealth.Denied : ObservationHealth.Unknown,
            Detail = $"Win32 error {error}: {exception.Message}",
        };
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

    private static string StateName(uint state) => state switch
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess
    {
        public nint ServiceName;
        public nint DisplayName;
        public ServiceStatusProcess Status;
    }

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

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(SafeServiceHandle manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(
        SafeServiceHandle manager,
        int infoLevel,
        uint serviceType,
        uint serviceState,
        nint services,
        uint bufferSize,
        out uint bytesNeeded,
        out uint servicesReturned,
        ref uint resumeHandle,
        string? groupName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(SafeServiceHandle service, nint config, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2(
        SafeServiceHandle service,
        uint infoLevel,
        ref int buffer,
        int bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint serviceHandle);
}
