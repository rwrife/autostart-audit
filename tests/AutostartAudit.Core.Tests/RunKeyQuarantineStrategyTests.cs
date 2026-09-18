using AutostartAudit.Core.Quarantine;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

public class RunKeyQuarantineStrategyTests
{
    private const string UserRun = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string MachineRun = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    [Fact]
    public void RoundTrip_QuarantineThenRestore_RestoresNameAndDataVerbatim()
    {
        var probe = new FakeRegistryWriteProbe();
        const string data = @"""C:\Program Files\Sec\sec.exe"" /silent %LOGONDOMAIN%";
        probe.Seed(UserRun, "MyApp", ValueKind.ExpandString, data);
        var strategy = new RunKeyQuarantineStrategy(probe);
        var entry = QuarantineTestEntries.RunKey("user", "MyApp", data);

        var plan = strategy.Plan(entry);
        Assert.True(plan.CanQuarantine, plan.Reason);

        var executed = strategy.Execute(plan.BeforeStateJson!);
        Assert.Equal(MutationStatus.Ok, executed.Status);
        // Original gone; quarantined name present with verbatim data + kind.
        Assert.Null(probe.Peek(UserRun, "MyApp"));
        var copy = probe.Peek(UserRun, "MyApp" + RunKeyQuarantineStrategy.QuarantinedSuffix);
        Assert.NotNull(copy);
        Assert.Equal(data, copy!.Data);
        Assert.Equal(ValueKind.ExpandString, copy.Kind);

        var restored = strategy.Restore(plan.BeforeStateJson!);
        Assert.Equal(MutationStatus.Ok, restored.Status);
        var original = probe.Peek(UserRun, "MyApp");
        Assert.NotNull(original);
        Assert.Equal(data, original!.Data); // byte-equal content
        Assert.Equal(ValueKind.ExpandString, original.Kind);
        Assert.Null(probe.Peek(UserRun, "MyApp" + RunKeyQuarantineStrategy.QuarantinedSuffix));
    }

    [Fact]
    public void DriftedDataSinceScan_RefusesToQuarantine()
    {
        var probe = new FakeRegistryWriteProbe();
        probe.Seed(UserRun, "MyApp", ValueKind.String, @"C:\now\sneaky.exe");
        var strategy = new RunKeyQuarantineStrategy(probe);
        var entry = QuarantineTestEntries.RunKey("user", "MyApp", @"C:\tools\myapp.exe");

        var plan = strategy.Plan(entry);
        Assert.False(plan.CanQuarantine);
        Assert.Contains("drifted", plan.Reason);
        Assert.Equal(0, probe.MutationCount);
    }

    [Fact]
    public void MissingValue_RefusesToQuarantine()
    {
        var probe = new FakeRegistryWriteProbe();
        var strategy = new RunKeyQuarantineStrategy(probe);
        probe.Keys[UserRun] = new();
        var plan = strategy.Plan(QuarantineTestEntries.RunKey("user", "Gone", @"C:\gone.exe"));
        Assert.False(plan.CanQuarantine);
        Assert.Contains("no longer present", plan.Reason);
    }

    [Fact]
    public void OccupiedQuarantineName_ExecutionRefuses_AndMutatesNothing()
    {
        var probe = new FakeRegistryWriteProbe();
        const string data = @"C:\Tools\myapp.exe --startup";
        probe.Seed(UserRun, "MyApp", ValueKind.String, data);
        probe.Seed(UserRun, "MyApp.aa-quarantined", ValueKind.String, "pre-existing");
        var strategy = new RunKeyQuarantineStrategy(probe);
        var plan = strategy.Plan(QuarantineTestEntries.RunKey("user", "MyApp", data));
        Assert.True(plan.CanQuarantine, plan.Reason);

        var executed = strategy.Execute(plan.BeforeStateJson!);
        Assert.Equal(MutationStatus.Failed, executed.Status);
        Assert.Contains("occupied", executed.Detail);
        // Original untouched, pre-existing copy untouched.
        Assert.Equal(data, probe.Peek(UserRun, "MyApp")!.Data);
        Assert.Equal("pre-existing", probe.Peek(UserRun, "MyApp.aa-quarantined")!.Data);
    }

    [Fact]
    public void MachineKeyDenied_ExecutionReportsDenied_WithoutWrites()
    {
        var probe = new FakeRegistryWriteProbe();
        probe.Seed(MachineRun, "SecSvc", ValueKind.String, @"C:\sec\svc.exe");
        probe.WriteDenied.Add(MachineRun);
        var strategy = new RunKeyQuarantineStrategy(probe);

        // Plan on machine key without read access: journals a scan-evidence
        // candidate (the elevated child re-verifies before touching).
        var entry = QuarantineTestEntries.RunKey("machine", "SecSvc", @"C:\sec\svc.exe");
        var plan = strategy.Plan(entry);
        Assert.True(plan.CanQuarantine, plan.Reason);

        var executed = strategy.Execute(plan.BeforeStateJson!);
        Assert.Equal(MutationStatus.Denied, executed.Status);
        Assert.Equal(0, probe.MutationCount);
    }

    [Fact]
    public void RestoreAfterExternalTakeoverOfOriginalName_RefusesToClobber()
    {
        var probe = new FakeRegistryWriteProbe();
        const string data = @"C:\Tools\myapp.exe";
        probe.Seed(UserRun, "MyApp", ValueKind.String, data);
        var strategy = new RunKeyQuarantineStrategy(probe);
        var plan = strategy.Plan(QuarantineTestEntries.RunKey("user", "MyApp", data));
        strategy.Execute(plan.BeforeStateJson!);

        // Something else created a value under the original name meanwhile.
        probe.Seed(UserRun, "MyApp", ValueKind.String, @"C:\someone\else.exe");

        var restored = strategy.Restore(plan.BeforeStateJson!);
        Assert.Equal(MutationStatus.Failed, restored.Status);
        Assert.Contains("occupied", restored.Detail);
        Assert.Equal(@"C:\someone\else.exe", probe.Peek(UserRun, "MyApp")!.Data); // external state preserved
    }

    [Fact]
    public void NonStringKindValue_IsReadOnly()
    {
        var probe = new FakeRegistryWriteProbe();
        probe.Seed(UserRun, "Weird", ValueKind.Other, "010203");
        var strategy = new RunKeyQuarantineStrategy(probe);
        var plan = strategy.Plan(QuarantineTestEntries.RunKey("user", "Weird", "010203"));
        Assert.False(plan.CanQuarantine);
        Assert.Contains("not string", plan.Reason);
    }

    [Fact]
    public void AlreadyQuarantinedValue_IsReadOnly()
    {
        var probe = new FakeRegistryWriteProbe();
        probe.Seed(UserRun, "Old.aa-quarantined", ValueKind.String, "copy");
        var strategy = new RunKeyQuarantineStrategy(probe);
        var plan = strategy.Plan(QuarantineTestEntries.RunKey("user", "Old.aa-quarantined", "copy"));
        Assert.False(plan.CanQuarantine);
        Assert.Contains("suffix", plan.Reason);
    }
}
