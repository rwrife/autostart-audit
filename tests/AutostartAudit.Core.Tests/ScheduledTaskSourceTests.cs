using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

internal sealed class FixedElevationProbe(bool elevated) : IElevationProbe
{
    public bool IsElevated() => elevated;
}

internal sealed class FakeScheduledTaskProbe(ScheduledTaskEnumeration snapshot) : IScheduledTaskProbe
{
    public ScheduledTaskEnumeration Enumerate() => snapshot;
}

public class ScheduledTaskSourceTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tasks", name));

    [Fact]
    public void LogonAndBootTask_ReportsAllExecActionsAndUnsupportedEvidence()
    {
        var enumeration = new ScheduledTaskEnumeration(
            new[] { new ScheduledTaskSnapshot(@"\Vendor\Agent", "Friendly Agent", "Ready", true, Fixture("mixed-actions.xml")) },
            new[] { new SourceObservation { Id = @"\Vendor", Health = ObservationHealth.Ok } });

        var result = new ScheduledTaskSource(new FakeScheduledTaskProbe(enumeration), new FixedElevationProbe(true)).Scan(ScanContext.Default);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("Friendly Agent", entry.DisplayName);
        Assert.Equal(@"\Vendor\Agent", entry.SourceName);
        Assert.Equal("ready", entry.State);
        Assert.True(entry.Enabled);
        Assert.Equal("logon, boot", entry.TriggerSummary);
        Assert.Equal(new[] { @"C:\Program Files\Contoso\agent.exe", @"C:\Tools\helper.exe" }, entry.TargetPaths);
        Assert.Contains(entry.Evidence, e => e.Contains("unsupported action: ComHandler", StringComparison.Ordinal));
        Assert.Equal(SigningStatus.Unverified, entry.Signing);
    }

    [Theory]
    [InlineData("<Task><Triggers><BootTrigger/></Triggers><Actions><ComHandler/></Actions></Task>")]
    [InlineData("<Task><Triggers><BootTrigger/></Triggers><Actions><Exec/></Actions></Task>")]
    [InlineData("<Task><Triggers><BootTrigger/></Triggers></Task>")]
    public void UnresolvedTaskActions_AreVisibleButUnknown(string xml)
    {
        var snapshot = new ScheduledTaskEnumeration(
            new[] { new ScheduledTaskSnapshot(@"\Odd", "Odd", "Ready", true, xml) },
            new[] { new SourceObservation { Id = @"\", Health = ObservationHealth.Ok } });
        var result = new ScheduledTaskSource(new FakeScheduledTaskProbe(snapshot), new FixedElevationProbe(true)).Scan(ScanContext.Default);
        Assert.Equal(ObservationHealth.Unknown, Assert.Single(result.Entries).Observation);
        Assert.Contains(result.Observations, o => o.Health == ObservationHealth.Unknown);
    }

    [Theory]
    [InlineData("<NotATask><BootTrigger/><Actions><Exec><Command>fake.exe</Command></Exec></Actions></NotATask>")]
    [InlineData("<Task><BootTrigger/><Actions><Exec><Command>fake.exe</Command></Exec></Actions></Task>")]
    [InlineData("<Task><Triggers><BootTrigger/></Triggers><Other><Actions><Exec><Command>fake.exe</Command></Exec></Actions></Other></Task>")]
    public void MalformedStructure_IsUnknownNotAHealthyTask(string xml)
    {
        var snapshot = new ScheduledTaskEnumeration(
            new[] { new ScheduledTaskSnapshot(@"\Malformed", "Malformed", "Ready", true, xml) },
            new[] { new SourceObservation { Id = @"\", Health = ObservationHealth.Ok } });
        var result = new ScheduledTaskSource(new FakeScheduledTaskProbe(snapshot), new FixedElevationProbe(true)).Scan(ScanContext.Default);
        Assert.DoesNotContain(result.Entries, e => e.Observation == ObservationHealth.Ok);
        Assert.Contains(result.Observations, o => o.Id == @"\Malformed" && o.Health == ObservationHealth.Unknown);
    }

    [Fact]
    public void NonStartupTask_IsNotAnEntry()
    {
        const string xml = "<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Triggers><TimeTrigger /></Triggers><Actions><Exec><Command>x.exe</Command></Exec></Actions></Task>";
        var snapshot = new ScheduledTaskEnumeration(
            new[] { new ScheduledTaskSnapshot(@"\Noon", "Noon", "Ready", true, xml) },
            new[] { new SourceObservation { Id = @"\", Health = ObservationHealth.Ok } });

        var result = new ScheduledTaskSource(new FakeScheduledTaskProbe(snapshot), new FixedElevationProbe(true)).Scan(ScanContext.Default);

        Assert.Empty(result.Entries);
    }

    [Fact]
    public void MalformedTaskAndDeniedFolder_PreserveValidEntryAndUnreadScopes()
    {
        var snapshot = new ScheduledTaskEnumeration(
            new[]
            {
                new ScheduledTaskSnapshot(@"\Good", "Good", "Running", true, Fixture("mixed-actions.xml")),
                new ScheduledTaskSnapshot(@"\Broken", "Broken", "Unknown", true, "<Task><Triggers>"),
            },
            new[]
            {
                new SourceObservation { Id = @"\", Health = ObservationHealth.Ok },
                new SourceObservation { Id = @"\Microsoft\Windows\Protected", Health = ObservationHealth.Denied, Detail = "access denied" },
            });

        var result = new ScheduledTaskSource(new FakeScheduledTaskProbe(snapshot), new FixedElevationProbe(true)).Scan(ScanContext.Default);

        Assert.Single(result.Entries);
        Assert.Contains(result.Observations, o => o.Id == @"\Microsoft\Windows\Protected" && o.Health == ObservationHealth.Denied);
        Assert.Contains(result.Observations, o => o.Id == @"\Broken" && o.Health == ObservationHealth.Unknown);
    }

    [Fact]
    public void UnelevatedIsExplicitlyIncomplete_ElevatedStillRetainsActualDenial()
    {
        var snapshot = new ScheduledTaskEnumeration(Array.Empty<ScheduledTaskSnapshot>(),
            new[] { new SourceObservation { Id = @"\Protected", Health = ObservationHealth.Denied, Detail = "ACL" } });

        var low = new ScheduledTaskSource(new FakeScheduledTaskProbe(snapshot), new FixedElevationProbe(false)).Scan(ScanContext.Default);
        var high = new ScheduledTaskSource(new FakeScheduledTaskProbe(snapshot), new FixedElevationProbe(true)).Scan(ScanContext.Default);

        Assert.Equal(ObservationHealth.Unknown, low.Observations.Single(o => o.Id == "process-elevation").Health);
        Assert.Equal(ObservationHealth.Ok, high.Observations.Single(o => o.Id == "process-elevation").Health);
        Assert.Contains(high.Observations, o => o.Id == @"\Protected" && o.Health == ObservationHealth.Denied);
    }

    [Fact]
    public void ElevationChangesVisibilityClaim_ButCannotTurnFailedReadsIntoSuccess()
    {
        var readable = new ScheduledTaskEnumeration(Array.Empty<ScheduledTaskSnapshot>(),
            new[] { new SourceObservation { Id = @"\", Health = ObservationHealth.Ok } });
        var denied = new ScheduledTaskEnumeration(Array.Empty<ScheduledTaskSnapshot>(),
            new[] { new SourceObservation { Id = @"\", Health = ObservationHealth.Denied } });

        SourceCapability Capability(ScheduledTaskEnumeration data, bool elevated) => ScanEngine.ScanAll(
            new IAutoStartSource[] { new ScheduledTaskSource(new FakeScheduledTaskProbe(data), new FixedElevationProbe(elevated)) })
            .Sources.Single().Capability;

        Assert.Equal(SourceCapability.Partial, Capability(readable, false));
        Assert.Equal(SourceCapability.Scanned, Capability(readable, true));
        Assert.Equal(SourceCapability.Denied, Capability(denied, true));
    }

    [Fact]
    public void IdentityUsesTaskKeyAndExecutablePaths_NotDisplayName()
    {
        var xml = Fixture("mixed-actions.xml");
        AutoStartEntry Scan(string display, string taskXml) => new ScheduledTaskSource(
            new FakeScheduledTaskProbe(new ScheduledTaskEnumeration(
                new[] { new ScheduledTaskSnapshot(@"\Vendor\Agent", display, "Ready", true, taskXml) },
                new[] { new SourceObservation { Id = @"\Vendor", Health = ObservationHealth.Ok } })),
            new FixedElevationProbe(true)).Scan(ScanContext.Default).Entries.Single();

        var original = Scan("First label", xml);
        var renamed = Scan("Second label", xml);
        var changedTarget = Scan("First label", xml.Replace("helper.exe", "other.exe", StringComparison.Ordinal));

        Assert.Equal(original.StableKey, renamed.StableKey);
        Assert.NotEqual(original.StableKey, changedTarget.StableKey);
    }
}
