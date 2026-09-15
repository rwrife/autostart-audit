using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

internal sealed class FakeServiceProbe(ServiceEnumeration snapshot) : IServiceProbe
{
    public ServiceEnumeration Enumerate() => snapshot;
}

public class ServiceSourceTests
{
    [Fact]
    public void IncludesAutomaticAndDelayed_WithNameDisplayStartTypeAndBinary()
    {
        var snapshot = new ServiceEnumeration(new[]
        {
            new ServiceSnapshot("ImmediateSvc", "Immediate Service", ServiceStartType.Automatic, @"""C:\Program Files\Svc\svc.exe"" --service", "Running"),
            new ServiceSnapshot("DelayedSvc", "Delayed Service", ServiceStartType.AutomaticDelayed, @"C:\Tools\delayed.exe -k", "Stopped"),
            new ServiceSnapshot("ManualSvc", "Manual Service", ServiceStartType.Manual, @"C:\Tools\manual.exe", "Stopped"),
        }, new[] { new SourceObservation { Id = "service-control-manager", Health = ObservationHealth.Ok } });

        var result = new ServiceSource(new FakeServiceProbe(snapshot), new FixedElevationProbe(true)).Scan(ScanContext.Default);

        Assert.Equal(2, result.Entries.Count);
        var delayed = result.Entries.Single(e => e.SourceName == "DelayedSvc");
        Assert.Equal("Delayed Service", delayed.DisplayName);
        Assert.Equal("automatic-delayed", delayed.StartType);
        Assert.Equal("stopped", delayed.State);
        Assert.Equal(@"C:\Tools\delayed.exe", Assert.Single(delayed.TargetPaths));
        Assert.Equal(@"C:\Tools\delayed.exe -k", delayed.RawValueSnapshot);
        Assert.All(result.Entries, e => Assert.Equal(SigningStatus.Unverified, e.Signing));
    }

    [Fact]
    public void PartialEnumerationPreservesServicesAndDeniedScopes()
    {
        var snapshot = new ServiceEnumeration(
            new[] { new ServiceSnapshot("Good", "Good", ServiceStartType.Automatic, "good.exe", "Running") },
            new[]
            {
                new SourceObservation { Id = "service-control-manager", Health = ObservationHealth.Ok },
                new SourceObservation { Id = "service:Protected", Health = ObservationHealth.Denied, Detail = "access denied" },
                new SourceObservation { Id = "service:Odd", Health = ObservationHealth.Unknown, Detail = "malformed config" },
            });

        var result = new ServiceSource(new FakeServiceProbe(snapshot), new FixedElevationProbe(true)).Scan(ScanContext.Default);

        Assert.Single(result.Entries);
        Assert.Contains(result.Observations, o => o.Health == ObservationHealth.Denied);
        Assert.Contains(result.Observations, o => o.Health == ObservationHealth.Unknown);
    }

    [Fact]
    public void ElevationProbeUpgradesOnlyElevationObservation()
    {
        var snapshot = new ServiceEnumeration(Array.Empty<ServiceSnapshot>(),
            new[] { new SourceObservation { Id = "service:Protected", Health = ObservationHealth.Denied } });

        var low = new ServiceSource(new FakeServiceProbe(snapshot), new FixedElevationProbe(false)).Scan(ScanContext.Default);
        var high = new ServiceSource(new FakeServiceProbe(snapshot), new FixedElevationProbe(true)).Scan(ScanContext.Default);

        Assert.Equal(ObservationHealth.Unknown, low.Observations.Single(o => o.Id == "process-elevation").Health);
        Assert.Equal(ObservationHealth.Ok, high.Observations.Single(o => o.Id == "process-elevation").Health);
        Assert.Contains(high.Observations, o => o.Health == ObservationHealth.Denied);
    }

    [Fact]
    public void ElevatedTokenDoesNotClaimDeniedServiceReadsSucceeded()
    {
        var snapshot = new ServiceEnumeration(Array.Empty<ServiceSnapshot>(),
            new[] { new SourceObservation { Id = "service-control-manager", Health = ObservationHealth.Denied } });
        var document = ScanEngine.ScanAll(new IAutoStartSource[]
        {
            new ServiceSource(new FakeServiceProbe(snapshot), new FixedElevationProbe(true)),
        });

        Assert.Equal(SourceCapability.Denied, document.Sources.Single().Capability);
        Assert.False(document.ScanComplete);
    }

    [Fact]
    public void IdentityUsesServiceNameAndBinary_NotDisplayName()
    {
        AutoStartEntry Entry(string display, string binary) => new ServiceSource(
            new FakeServiceProbe(new ServiceEnumeration(
                new[] { new ServiceSnapshot("Svc", display, ServiceStartType.Automatic, binary, "Running") },
                new[] { new SourceObservation { Id = "service-control-manager", Health = ObservationHealth.Ok } })),
            new FixedElevationProbe(true)).Scan(ScanContext.Default).Entries.Single();

        Assert.Equal(Entry("One", "a.exe").StableKey, Entry("Two", "a.exe").StableKey);
        Assert.NotEqual(Entry("One", "a.exe").StableKey, Entry("One", "b.exe").StableKey);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Vendor\svc.exe -k")]
    [InlineData("\"C:\\broken\\svc.exe")]
    [InlineData("\"C:\\svc.exe\"junk")]
    public void AmbiguousOrMalformedCommand_RemainsRawAndUnknown(string binary)
    {
        var snapshot = new ServiceEnumeration(
            new[] { new ServiceSnapshot("Svc", "Service", ServiceStartType.Automatic, binary, "Running") },
            new[] { new SourceObservation { Id = "service-control-manager", Health = ObservationHealth.Ok } });
        var result = new ServiceSource(new FakeServiceProbe(snapshot), new FixedElevationProbe(true)).Scan(ScanContext.Default);
        var entry = Assert.Single(result.Entries);
        Assert.Empty(entry.TargetPaths);
        Assert.Equal(binary, entry.RawValueSnapshot);
        Assert.Equal(ObservationHealth.Unknown, entry.Observation);
        Assert.Contains(result.Observations, o => o.Health == ObservationHealth.Unknown);
    }

    [Fact]
    public void MalformedBinaryPath_RemainsVisibleAsUnknown_NotAbsentOrUnsigned()
    {
        var snapshot = new ServiceEnumeration(
            new[] { new ServiceSnapshot("Broken", "Broken Service", ServiceStartType.Automatic, "   ", "Stopped") },
            new[] { new SourceObservation { Id = "service-control-manager", Health = ObservationHealth.Ok } });

        var result = new ServiceSource(new FakeServiceProbe(snapshot), new FixedElevationProbe(true)).Scan(ScanContext.Default);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(ObservationHealth.Unknown, entry.Observation);
        Assert.Equal(SigningStatus.Unverified, entry.Signing);
        Assert.Contains(result.Observations, o => o.Id == "service:Broken:binary-path" && o.Health == ObservationHealth.Unknown);
    }
}
