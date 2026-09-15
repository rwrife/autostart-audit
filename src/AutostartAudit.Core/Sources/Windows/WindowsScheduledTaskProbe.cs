using System.Runtime.InteropServices;
using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// Read-only Task Scheduler 2.0 COM traversal. Flags include hidden tasks and
/// folders; each folder operation is isolated so partial results survive.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsScheduledTaskProbe : IScheduledTaskProbe
{
    private const int IncludeHidden = 1;
    private const int AccessDeniedHResult = unchecked((int)0x80070005);

    public ScheduledTaskEnumeration Enumerate()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Task Scheduler COM requires Windows");

        var tasks = new List<ScheduledTaskSnapshot>();
        var observations = new List<SourceObservation>();
        object? serviceObject = null;
        object? rootObject = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)
                ?? throw new PlatformNotSupportedException("Task Scheduler 2.0 COM is unavailable");
            serviceObject = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("could not create Task Scheduler COM service");
            dynamic service = serviceObject;
            service.Connect();
            rootObject = service.GetFolder(@"\");
            Walk(rootObject, @"\", tasks, observations);
        }
        catch (Exception ex)
        {
            observations.Add(Failure("task-scheduler", ex));
        }
        finally
        {
            Release(rootObject);
            Release(serviceObject);
        }

        return new ScheduledTaskEnumeration(tasks, observations);
    }

    private static void Walk(
        object folderObject,
        string folderPath,
        List<ScheduledTaskSnapshot> snapshots,
        List<SourceObservation> observations)
    {
        dynamic folder = folderObject;
        object? taskCollectionObject = null;
        try
        {
            taskCollectionObject = folder.GetTasks(IncludeHidden);
            dynamic taskCollection = taskCollectionObject;
            var count = (int)taskCollection.Count;
            for (var index = 1; index <= count; index++)
            {
                object? taskObject = null;
                try
                {
                    taskObject = taskCollection[index];
                    dynamic task = taskObject;
                    var path = (string)task.Path;
                    snapshots.Add(new ScheduledTaskSnapshot(
                        path,
                        (string)task.Name,
                        StateName((int)task.State),
                        (bool)task.Enabled,
                        (string)task.Xml));
                }
                catch (Exception ex)
                {
                    observations.Add(Failure($"{folderPath} [task {index}]", ex));
                }
                finally
                {
                    Release(taskObject);
                }
            }
            observations.Add(new SourceObservation { Id = $"{folderPath} [tasks]", Health = ObservationHealth.Ok });
        }
        catch (Exception ex)
        {
            observations.Add(Failure($"{folderPath} [tasks]", ex));
        }
        finally
        {
            Release(taskCollectionObject);
        }

        object? folderCollectionObject = null;
        try
        {
            folderCollectionObject = folder.GetFolders(0); // Reserved flags; TASK_ENUM_HIDDEN applies only to GetTasks.
            dynamic folderCollection = folderCollectionObject;
            var count = (int)folderCollection.Count;
            for (var index = 1; index <= count; index++)
            {
                object? childObject = null;
                try
                {
                    childObject = folderCollection[index];
                    dynamic child = childObject;
                    var childPath = (string)child.Path;
                    Walk(childObject, childPath, snapshots, observations);
                }
                catch (Exception ex)
                {
                    observations.Add(Failure($"{folderPath} [folder {index}]", ex));
                }
                finally
                {
                    Release(childObject);
                }
            }
            observations.Add(new SourceObservation { Id = $"{folderPath} [folders]", Health = ObservationHealth.Ok });
        }
        catch (Exception ex)
        {
            observations.Add(Failure($"{folderPath} [folders]", ex));
        }
        finally
        {
            Release(folderCollectionObject);
        }
    }

    private static SourceObservation Failure(string id, Exception exception)
    {
        var actual = exception is System.Reflection.TargetInvocationException { InnerException: not null } wrapped
            ? wrapped.InnerException!
            : exception;
        var denied = actual is UnauthorizedAccessException
            || actual is COMException { HResult: AccessDeniedHResult };
        return new SourceObservation
        {
            Id = id,
            Health = denied ? ObservationHealth.Denied : ObservationHealth.Unknown,
            Detail = $"{actual.GetType().Name}: {actual.Message}",
        };
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
