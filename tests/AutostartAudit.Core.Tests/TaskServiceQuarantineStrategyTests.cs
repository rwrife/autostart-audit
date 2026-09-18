using AutostartAudit.Core.Model;
using AutostartAudit.Core.Quarantine;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

public class ScheduledTaskQuarantineStrategyTests
{
    private static AutoStartEntry TaskEntry(string path, bool enabled, string state) => new()
    {
        SourceKind = ScheduledTaskSource.Kind,
        Scope = "machine",
        StableKey = StableKey.BuildWithTargets(ScheduledTaskSource.Kind, "machine", path, new[] { @"C:\tools\t.exe" }),
        NativeKey = path,
        DisplayName = System.IO.Path.GetFileName(path),
        SourceName = path,
        TargetPaths = new[] { @"C:\tools\t.exe" },
        RawValueSnapshot = "<xml/>",
        Enabled = enabled,
        State = state,
        Signing = SigningStatus.Unverified,
        Observation = ObservationHealth.Ok,
    };

    [Fact]
    public void RoundTrip_DisableThenEnable_RestoresPreviousEnabledState()
    {
        const string path = @"\Vendor\Updater";
        var probe = new FakeTaskWriteProbe();
        probe.Tasks[path] = new ScheduledTaskSnapshot(path, "Updater", "Ready", true, "<xml/>");
        var strategy = new ScheduledTaskQuarantineStrategy(probe);

        var plan = strategy.Plan(TaskEntry(path, enabled: true, "Ready"));
        Assert.True(plan.CanQuarantine, plan.Reason);

        Assert.Equal(MutationStatus.Ok, strategy.Execute(plan.BeforeStateJson!).Status);
        Assert.False(probe.Tasks[path].Enabled);

        Assert.Equal(MutationStatus.Ok, strategy.Restore(plan.BeforeStateJson!).Status);
        Assert.True(probe.Tasks[path].Enabled);
        Assert.Equal("Ready", probe.Tasks[path].State);
    }

    [Fact]
    public void AlreadyDisabledTask_IsReadOnly()
    {
        const string path = @"\Vendor\Updater";
        var probe = new FakeTaskWriteProbe();
        probe.Tasks[path] = new ScheduledTaskSnapshot(path, "Updater", "Disabled", false, "<xml/>");
        var strategy = new ScheduledTaskQuarantineStrategy(probe);

        var plan = strategy.Plan(TaskEntry(path, enabled: false, "Disabled"));
        Assert.False(plan.CanQuarantine);
        Assert.Contains("already disabled", plan.Reason);
        Assert.Equal(0, probe.MutationCount);
    }

    [Fact]
    public void RunningTaskState_IsReadOnly()
    {
        const string path = @"\Vendor\Churn";
        var probe = new FakeTaskWriteProbe();
        probe.Tasks[path] = new ScheduledTaskSnapshot(path, "Churn", "Running", true, "<xml/>");
        var strategy = new ScheduledTaskQuarantineStrategy(probe);

        var plan = strategy.Plan(TaskEntry(path, enabled: true, "Running"));
        Assert.False(plan.CanQuarantine);
        Assert.Contains("Running", plan.Reason);
    }

    [Fact]
    public void DeniedDisable_ReportsDenied_WithoutMutation()
    {
        const string path = @"\Vendor\Updater";
        var probe = new FakeTaskWriteProbe();
        probe.Tasks[path] = new ScheduledTaskSnapshot(path, "Updater", "Ready", true, "<xml/>");
        probe.DenyWrite = true;
        var strategy = new ScheduledTaskQuarantineStrategy(probe);

        var plan = strategy.Plan(TaskEntry(path, true, "Ready"));
        Assert.True(plan.CanQuarantine, plan.Reason);
        var executed = strategy.Execute(plan.BeforeStateJson!);
        Assert.Equal(MutationStatus.Denied, executed.Status);
        Assert.True(probe.Tasks[path].Enabled); // untouched
    }

    [Fact]
    public void Restore_WhenTaskReEnabledExternally_FailsHonestly()
    {
        const string path = @"\Vendor\Updater";
        var probe = new FakeTaskWriteProbe();
        probe.Tasks[path] = new ScheduledTaskSnapshot(path, "Updater", "Ready", true, "<xml/>");
        var strategy = new ScheduledTaskQuarantineStrategy(probe);
        var plan = strategy.Plan(TaskEntry(path, true, "Ready"));
        strategy.Execute(plan.BeforeStateJson!);

        // External tool re-enabled the task meanwhile.
        probe.Tasks[path] = probe.Tasks[path] with { Enabled = true, State = "Ready" };

        var restored = strategy.Restore(plan.BeforeStateJson!);
        Assert.Equal(MutationStatus.Failed, restored.Status);
        Assert.Contains("already enabled", restored.Detail);
    }
}

public class ServiceQuarantineStrategyTests
{
    private static AutoStartEntry ServiceEntry(string name, string startType) => new()
    {
        SourceKind = ServiceSource.Kind,
        Scope = "machine",
        StableKey = StableKey.BuildWithTargets(ServiceSource.Kind, "machine", name, new[] { @"C:\svc\s.exe" }),
        NativeKey = name,
        DisplayName = name + " service",
        SourceName = name,
        TargetPaths = new[] { @"C:\svc\s.exe" },
        RawValueSnapshot = @"C:\svc\s.exe",
        StartType = startType,
        Signing = SigningStatus.Unverified,
        Observation = ObservationHealth.Ok,
    };

    [Theory]
    [InlineData(ServiceStartType.Automatic, "automatic")]
    [InlineData(ServiceStartType.AutomaticDelayed, "automatic-delayed")]
    public void RoundTrip_DisableThenRestorePreviousType(ServiceStartType previous, string scanType)
    {
        var probe = new FakeServiceWriteProbe();
        probe.Services["MySvc"] = new ServiceSnapshot("MySvc", "MySvc service", previous, @"C:\svc\s.exe", "Running");
        var strategy = new ServiceQuarantineStrategy(probe);

        var plan = strategy.Plan(ServiceEntry("MySvc", scanType));
        Assert.True(plan.CanQuarantine, plan.Reason);

        Assert.Equal(MutationStatus.Ok, strategy.Execute(plan.BeforeStateJson!).Status);
        Assert.Equal(ServiceStartType.Disabled, probe.Services["MySvc"].StartType);
        // Running state untouched.
        Assert.Equal("Running", probe.Services["MySvc"].State);

        Assert.Equal(MutationStatus.Ok, strategy.Restore(plan.BeforeStateJson!).Status);
        Assert.Equal(previous, probe.Services["MySvc"].StartType);
    }

    [Fact]
    public void StartTypeDriftSinceScan_RefusesToQuarantine()
    {
        var probe = new FakeServiceWriteProbe();
        probe.Services["MySvc"] = new ServiceSnapshot("MySvc", "s", ServiceStartType.Manual, @"C:\svc\s.exe", "Stopped");
        var strategy = new ServiceQuarantineStrategy(probe);

        var plan = strategy.Plan(ServiceEntry("MySvc", "automatic"));
        Assert.False(plan.CanQuarantine);
        Assert.Contains("not automatic", plan.Reason);
    }

    [Fact]
    public void AutomaticDelayUnknown_IsReadOnly()
    {
        var probe = new FakeServiceWriteProbe();
        probe.Services["MySvc"] = new ServiceSnapshot("MySvc", "s", ServiceStartType.Automatic, @"C:\svc\s.exe", "Stopped");
        var strategy = new ServiceQuarantineStrategy(probe);

        var plan = strategy.Plan(ServiceEntry("MySvc", "automatic-delay-unknown"));
        Assert.False(plan.CanQuarantine);
        Assert.Contains("delayed", plan.Reason);
    }

    [Fact]
    public void DeniedSetStartType_ReportsDenied_AndServiceUnchanged()
    {
        var probe = new FakeServiceWriteProbe();
        probe.Services["MySvc"] = new ServiceSnapshot("MySvc", "s", ServiceStartType.Automatic, @"C:\svc\s.exe", "Running");
        probe.DenyWrite = true;
        var strategy = new ServiceQuarantineStrategy(probe);

        var plan = strategy.Plan(ServiceEntry("MySvc", "automatic"));
        Assert.True(plan.CanQuarantine, plan.Reason);
        var executed = strategy.Execute(plan.BeforeStateJson!);
        Assert.Equal(MutationStatus.Denied, executed.Status);
        Assert.Equal(ServiceStartType.Automatic, probe.Services["MySvc"].StartType);
    }

    [Fact]
    public void PlanWithDeniedRead_JournalsScanCandidate()
    {
        // Unelevated plan for a machine service cannot open the SCM write
        // handle; we journal what the scan verifiably saw, and Execute (which
        // runs elevated) re-verifies before its first write.
        var probe = new FakeServiceWriteProbe();
        probe.DenyRead = true;
        var strategy = new ServiceQuarantineStrategy(probe);
        var plan = strategy.Plan(ServiceEntry("MySvc", "automatic-delayed"));
        Assert.True(plan.CanQuarantine, plan.Reason);
        Assert.Contains("automaticDelayed", plan.BeforeStateJson);
    }

    [Fact]
    public void Restore_WhenTypeChangedExternally_FailsHonestly()
    {
        var probe = new FakeServiceWriteProbe();
        probe.Services["MySvc"] = new ServiceSnapshot("MySvc", "s", ServiceStartType.Automatic, @"C:\svc\s.exe", "Stopped");
        var strategy = new ServiceQuarantineStrategy(probe);
        var plan = strategy.Plan(ServiceEntry("MySvc", "automatic"));
        strategy.Execute(plan.BeforeStateJson!);

        probe.Services["MySvc"] = probe.Services["MySvc"] with { StartType = ServiceStartType.Automatic };

        var restored = strategy.Restore(plan.BeforeStateJson!);
        Assert.Equal(MutationStatus.Failed, restored.Status);
    }
}
