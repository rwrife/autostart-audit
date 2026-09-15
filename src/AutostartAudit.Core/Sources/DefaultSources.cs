using System.Runtime.InteropServices;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// Assembles the default source set for the current OS. On non-Windows hosts
/// each source is registered as an explicit <see cref="UnsupportedSource"/>
/// with a per-source Unsupported record — the engine still reports both
/// sources honestly rather than returning an empty document.
/// </summary>
public static class DefaultSources
{
    public static IReadOnlyList<IAutoStartSource> ForCurrentOS()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var elevation = new WindowsElevationProbe();
            return new IAutoStartSource[]
            {
                new RunKeySource(new WindowsRegistryProbe()),
                new StartupFolderSource(new SystemFolderProbe(), WindowsStartupFolders.Resolve()),
                new ScheduledTaskSource(new WindowsScheduledTaskProbe(), elevation),
                new ServiceSource(new WindowsServiceProbe(), elevation),
            };
        }

        return new IAutoStartSource[]
        {
            new UnsupportedSource(RunKeySource.Kind, "registry sources require Windows"),
            new UnsupportedSource(StartupFolderSource.Kind, "startup folders require Windows"),
            new UnsupportedSource(ScheduledTaskSource.Kind, "Task Scheduler COM requires Windows"),
            new UnsupportedSource(ServiceSource.Kind, "Service Control Manager requires Windows"),
        };
    }
}

/// <summary>Resolves the two Windows startup folder paths (user + common).</summary>
internal static class WindowsStartupFolders
{
    public static IReadOnlyList<(string Path, string Scope)> Resolve()
    {
        var folders = new List<(string, string)>();
        var user = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (!string.IsNullOrWhiteSpace(user))
            folders.Add((user, "user"));
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        if (!string.IsNullOrWhiteSpace(common))
            folders.Add((common, "machine"));
        return folders;
    }
}
