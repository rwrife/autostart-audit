using System.Reflection;
using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

public class ScheduledTaskTraversalTests
{
    [Fact]
    public void TraversalIncludesHiddenTasksAndRecursesWithReservedZeroFlags()
    {
        var child = new TaskFolderFixture(@"\Microsoft\Windows", new[] { new TaskFixture() });
        var root = new TaskFolderFixture(@"\", [], child);
        var tasks = new List<ScheduledTaskSnapshot>();
        var observations = new List<SourceObservation>();
        // Exercise the production traversal without activating COM or touching the host.
        var walk = typeof(WindowsScheduledTaskProbe).GetMethod("Walk", BindingFlags.NonPublic | BindingFlags.Static)!;
        walk.Invoke(null, new object[] { root, root.Path, tasks, observations });

        Assert.Equal(@"\Microsoft\Windows\HiddenBoot", Assert.Single(tasks).Path);
        Assert.All(observations, o => Assert.Equal(ObservationHealth.Ok, o.Health));
        Assert.Equal(1, root.TaskFlags);
        Assert.Equal(1, child.TaskFlags);
        Assert.Equal(0, root.FolderFlags);
        Assert.Equal(0, child.FolderFlags);
    }
}

// Public types are required by the production dynamic COM dispatch binder.
public sealed class TaskFolderFixture(string path, TaskFixture[] tasks, params TaskFolderFixture[] children)
{
    public string Path => path;
    public int? TaskFlags { get; private set; }
    public int? FolderFlags { get; private set; }
    public OneBasedFixture<TaskFixture> GetTasks(int flags)
    {
        TaskFlags = flags;
        if (flags != 1) throw new ArgumentException("hidden tasks were not requested");
        return new(tasks);
    }
    public OneBasedFixture<TaskFolderFixture> GetFolders(int flags)
    {
        FolderFlags = flags;
        if (flags != 0) throw new ArgumentException("ITaskFolder.GetFolders reserves flags; must be zero");
        return new(children);
    }
}
public sealed class OneBasedFixture<T>(T[] items)
{
    public int Count => items.Length;
    public T this[int index] => items[index - 1];
}
public sealed class TaskFixture
{
    public string Path => @"\Microsoft\Windows\HiddenBoot";
    public string Name => "HiddenBoot";
    public int State => 3;
    public bool Enabled => true;
    public string Xml => "<Task><Triggers><BootTrigger/></Triggers><Actions><Exec><Command>agent.exe</Command></Exec></Actions></Task>";
}
