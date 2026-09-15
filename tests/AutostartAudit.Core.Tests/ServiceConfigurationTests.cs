using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

public class ServiceConfigurationTests
{
    private static ServiceSnapshot Read(uint start, Func<bool> delayed, List<SourceObservation> observations)
    {
        var method = typeof(WindowsServiceProbe).GetMethod("CreateSnapshot", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (ServiceSnapshot)method.Invoke(null, new object[] { "Svc", "Friendly", "Running", @"C:\svc.exe", start, delayed, observations })!;
    }

    [Fact]
    public void DeniedDelayRead_RetainsAutomaticEntryAndExactBinary_WithUnknownDelay()
    {
        var observations = new List<SourceObservation> { new() { Id = "service-control-manager", Health = ObservationHealth.Ok } };
        var snapshot = Read(2, () => throw new Win32Exception(5), observations);
        var document = ScanEngine.ScanAll(new IAutoStartSource[] {
            new ServiceSource(new FakeServiceProbe(new ServiceEnumeration(new[] { snapshot }, observations)), new FixedElevationProbe(true)) });
        var entry = Assert.Single(document.Entries);
        Assert.Equal("automatic-delay-unknown", entry.StartType);
        Assert.Equal(@"C:\svc.exe", entry.RawValueSnapshot);
        Assert.Equal(ObservationHealth.Unknown, entry.Observation);
        Assert.Contains(document.Sources.Single().Observations, o => o.Health == ObservationHealth.Denied);
        Assert.False(document.ScanComplete);
    }

    [Fact]
    public void NativeStartTypeMapping_MatchesSyntheticConfigurationFixture()
    {
        var rows = JsonSerializer.Deserialize<ConfigRow[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "services.json")))!;
        foreach (var row in rows)
        {
            var calls = 0;
            var snapshot = Read(row.NativeStart, () => { calls++; return row.Delayed; }, new());
            Assert.Equal(row.Expected, snapshot.StartType.ToString());
            Assert.Equal(row.NativeStart == 2 ? 1 : 0, calls);
        }
    }
    public sealed record ConfigRow(uint NativeStart, bool Delayed, string Expected);
}
