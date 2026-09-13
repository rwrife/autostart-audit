using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

/// <summary>Registry probe driven by in-memory fixtures for source tests.</summary>
internal sealed class FakeRegistryProbe : IRegistryProbe
{
    private readonly Dictionary<string, RegistryKeySnapshot?> _keys;
    private readonly Dictionary<string, Exception> _failures;

    public FakeRegistryProbe(
        Dictionary<string, RegistryKeySnapshot?>? keys = null,
        Dictionary<string, Exception>? failures = null)
    {
        _keys = keys ?? new();
        _failures = failures ?? new();
    }

    public RegistryKeySnapshot? TryOpen(string hiveName, string subPath)
    {
        var full = $"{hiveName}\\{subPath}";
        if (_failures.TryGetValue(full, out var ex))
            throw ex;
        return _keys.TryGetValue(full, out var snap) ? snap : null;
    }

    public static RegistryKeySnapshot Snap(string path, params (string Name, string Data)[] values) =>
        new(path, values.Select(v => new RegistryValue(v.Name, ValueKind.ExpandString, v.Data)).ToList());
}

public class RunKeySourceTests
{
    private const string HklmRun = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string HklmRunOnce = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string HkcuRun = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string HkcuRunOnce = @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce";

    [Fact]
    public void ParsesValuesIntoEntries_WithCorrectScopeAndSnapshot()
    {
        var probe = new FakeRegistryProbe(keys: new()
        {
            [HklmRun] = FakeRegistryProbe.Snap(HklmRun, ("SecurityApp", @"""C:\Program Files\Sec\sec.exe"" /silent")),
            [HkcuRun] = FakeRegistryProbe.Snap(HkcuRun, ("MyTool", @"C:\Tools\mytool.exe --startup")),
        });

        var result = new RunKeySource(probe).Scan(ScanContext.Default);

        Assert.Equal(2, result.Entries.Count);
        var machine = result.Entries.Single(e => e.Scope == "machine");
        Assert.Equal("SecurityApp", machine.DisplayName);
        Assert.Equal(@"C:\Program Files\Sec\sec.exe", Assert.Single(machine.TargetPaths));
        Assert.Equal(@"""C:\Program Files\Sec\sec.exe"" /silent", machine.RawValueSnapshot);
        var user = result.Entries.Single(e => e.Scope == "user");
        Assert.Equal("MyTool", user.DisplayName);
        Assert.All(result.Entries, e =>
        {
            Assert.Equal(RunKeySource.Kind, e.SourceKind);
            Assert.Equal(ObservationHealth.Ok, e.Observation);
            Assert.Equal(SigningStatus.Unverified, e.Signing); // issue #4 fills this in
        });
    }

    [Fact]
    public void DeniedKey_YieldsDeniedObservation_NotSilentlyEmpty()
    {
        var probe = new FakeRegistryProbe(failures: new()
        {
            [HklmRun] = new RegistryAccessDeniedException(HklmRun, "requires elevation"),
        });

        var result = new RunKeySource(probe).Scan(ScanContext.Default);

        var denied = result.Observations.Single(o => o.Id == HklmRun);
        Assert.Equal(ObservationHealth.Denied, denied.Health);
        Assert.Contains("elevation", denied.Detail);
        // The other keys were still attempted and observed.
        Assert.Equal(4, result.Observations.Count);
    }

    [Fact]
    public void UnknownFailure_YieldsUnknownObservation()
    {
        var probe = new FakeRegistryProbe(failures: new()
        {
            [HkcuRunOnce] = new RegistryUnknownException(HkcuRunOnce, "boom"),
        });

        var result = new RunKeySource(probe).Scan(ScanContext.Default);

        Assert.Equal(ObservationHealth.Unknown, result.Observations.Single(o => o.Id == HkcuRunOnce).Health);
    }

    [Fact]
    public void AbsentKeys_AreHonestAbsence_AndAllObservationsOk()
    {
        var result = new RunKeySource(new FakeRegistryProbe()).Scan(ScanContext.Default);

        Assert.Empty(result.Entries);
        // Honest absence: four Ok observations with "key not present" detail.
        Assert.Equal(4, result.Observations.Count);
        Assert.All(result.Observations, o => Assert.Equal(ObservationHealth.Ok, o.Health));
    }

    [Fact]
    public void UnsupportedProbe_YieldsUnsupportedForcedCapability()
    {
        var probe = new FakeRegistryProbe(failures: new()
        {
            [HklmRun] = new RegistryUnsupportedException("not windows"),
        });

        var result = new RunKeySource(probe).Scan(ScanContext.Default);

        Assert.Equal(SourceCapability.Unsupported, result.ForcedCapability);
    }

    [Fact]
    public void DefaultNamedValue_IsNotAnEntry()
    {
        var probe = new FakeRegistryProbe(keys: new()
        {
            [HkcuRun] = FakeRegistryProbe.Snap(HkcuRun, ("", @"C:\not\an\entry.exe")),
        });

        var result = new RunKeySource(probe).Scan(ScanContext.Default);

        Assert.Empty(result.Entries);
    }

    [Fact]
    public void StableKeys_AreDeterministicAcrossScans_AndCaseInvariant()
    {
        Dictionary<string, RegistryKeySnapshot?> keys1 = new() { [HkcuRun] = FakeRegistryProbe.Snap(HkcuRun, ("MyApp", @"C:\A\app.exe")) };
        Dictionary<string, RegistryKeySnapshot?> keys2 = new() { [HkcuRun] = FakeRegistryProbe.Snap(HkcuRun.ToUpperInvariant(), ("MyAPP", @"C:\A\app.exe")) };

        var r1 = new RunKeySource(new FakeRegistryProbe(keys: keys1)).Scan(ScanContext.Default);
        var r2 = new RunKeySource(new FakeRegistryProbe(keys: keys2)).Scan(ScanContext.Default);

        Assert.Equal(r1.Entries.Single().StableKey, r2.Entries.Single().StableKey);
        // Identity embeds source kind + scope + full key path, not display name casing.
        Assert.Contains($"{RunKeySource.Kind}|user|", r1.Entries.Single().StableKey);
    }

    [Fact]
    public void RunOnceAndRunKeys_AreDistinctStableKeys()
    {
        var probe = new FakeRegistryProbe(keys: new()
        {
            [HkcuRun] = FakeRegistryProbe.Snap(HkcuRun, ("Same", "a.exe")),
            [HkcuRunOnce] = FakeRegistryProbe.Snap(HkcuRunOnce, ("Same", "a.exe")),
        });

        var result = new RunKeySource(probe).Scan(ScanContext.Default);

        Assert.Equal(2, result.Entries.Select(e => e.StableKey).Distinct().Count());
    }
}
