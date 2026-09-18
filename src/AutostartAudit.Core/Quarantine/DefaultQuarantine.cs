using System.Runtime.InteropServices;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Quarantine;

/// <summary>
/// Assembles the default quarantine machinery for the current OS. On
/// non-Windows hosts zero strategies are registered, so every entry honestly
/// reports read-only ("no quarantine strategy is registered") instead of
/// pretending mutation is possible.
/// </summary>
public static class DefaultQuarantine
{
    /// <summary>Default managed quarantine folder for startup-file moves.</summary>
    public static string DefaultQuarantineRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "autostart-audit", "quarantine");

    public static IEnumerable<IQuarantineStrategy> StrategiesForCurrentOS(string? quarantineRoot = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Array.Empty<IQuarantineStrategy>();

        return new IQuarantineStrategy[]
        {
            new RunKeyQuarantineStrategy(new WindowsRegistryWriteProbe()),
            new StartupFolderQuarantineStrategy(new SystemFileMutator(),
                quarantineRoot ?? DefaultQuarantineRoot),
            new ScheduledTaskQuarantineStrategy(new WindowsTaskWriteProbe()),
            new ServiceQuarantineStrategy(new WindowsServiceWriteProbe()),
        };
    }

    public static QuarantineCoordinator CoordinatorForCurrentOS(
        ChangeJournal journal,
        IElevatedRecordRunner? elevated = null,
        string? quarantineRoot = null) =>
        new(journal, StrategiesForCurrentOS(quarantineRoot), elevated
            ?? new ElevatedProcessRunner(
                () => Environment.ProcessPath
                    ?? throw new InvalidOperationException("cannot resolve the executable path for elevation"),
                ChangeJournal.DefaultDatabasePath));
}
