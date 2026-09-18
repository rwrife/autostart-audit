using System.Runtime.InteropServices;
using AutostartAudit.Core.Model;
using AutostartAudit.Core.Quarantine;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// Task Scheduler write boundary for the quarantine layer only. Reads use the
/// same COM traversal as scanning; the single supported mutation is the
/// persistent Enabled flag, which Task Scheduler 2.0 stores without touching
/// the task definition XML.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsTaskWriteProbe : ITaskWriteProbe
{
    private const int AccessDeniedHResult = unchecked((int)0x80070005);

    public ScheduledTaskSnapshot GetTask(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Task Scheduler COM requires Windows");

        object? serviceObject = null;
        object? taskObject = null;
        try
        {
            (serviceObject, dynamic service) = Connect();
            taskObject = service.GetTask(path);
            dynamic task = taskObject;
            return new ScheduledTaskSnapshot(
                (string)task.Path,
                (string)task.Name,
                StateName((int)task.State),
                (bool)task.Enabled,
                (string)task.Xml);
        }
        catch (Exception ex)
        {
            throw Map(path, ex);
        }
        finally
        {
            Release(taskObject);
            Release(serviceObject);
        }
    }

    public void SetEnabled(string path, bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Task Scheduler COM requires Windows");

        object? serviceObject = null;
        object? taskObject = null;
        try
        {
            (serviceObject, dynamic service) = Connect();
            taskObject = service.GetTask(path);
            dynamic task = taskObject;
            task.Enabled = enabled; // IRegisteredTask::Enabled persists immediately.
        }
        catch (Exception ex)
        {
            throw Map(path, ex);
        }
        finally
        {
            Release(taskObject);
            Release(serviceObject);
        }
    }

    private static (object ServiceObject, dynamic Service) Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)
            ?? throw new PlatformNotSupportedException("Task Scheduler 2.0 COM is unavailable");
        var serviceObject = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("could not create Task Scheduler COM service");
        dynamic service = serviceObject;
        service.Connect();
        return (serviceObject, service);
    }

    private static Exception Map(string path, Exception exception)
    {
        var actual = exception is System.Reflection.TargetInvocationException { InnerException: not null } wrapped
            ? wrapped.InnerException!
            : exception;
        var denied = actual is UnauthorizedAccessException
            || actual is COMException { HResult: AccessDeniedHResult };
        return denied
            ? new QuarantineDeniedException(path, $"{actual.GetType().Name}: {actual.Message}")
            : new QuarantineUnknownException(path, $"{actual.GetType().Name}: {actual.Message}");
    }

    private static string StateName(int state) => state switch
    {
        1 => "Disabled",
        2 => "Queued",
        3 => "Ready",
        4 => "Running",
        _ => "Unknown",
    };

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}
