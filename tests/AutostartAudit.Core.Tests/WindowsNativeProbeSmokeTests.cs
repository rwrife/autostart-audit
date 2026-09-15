using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

public class WindowsNativeProbeSmokeTests
{
    [WindowsFact]
    [Trait("Category", "WindowsNativeSmoke")]
    public void ReadOnlyNativeProbes_ReturnExplicitObservationsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var tasks = new WindowsScheduledTaskProbe().Enumerate();
        var services = new WindowsServiceProbe().Enumerate();
        _ = new WindowsElevationProbe().IsElevated();

        Assert.Contains(tasks.Observations, o => o.Id == @"\ [folders]" && o.Health == AutostartAudit.Core.Model.ObservationHealth.Ok);
        Assert.Contains(services.Observations, o => o.Id == "service-control-manager" && o.Health == AutostartAudit.Core.Model.ObservationHealth.Ok);
        Assert.NotEmpty(services.Services); // A total SCM failure must not pass this hosted-Windows smoke gate.
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires live Windows native APIs; not exercised on this host.";
    }
}
