using AutostartAudit.Core.Model;
using AutostartAudit.Core.Quarantine;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

/// <summary>
/// Exercises the real Windows quarantine boundaries: the full run-key strategy
/// round-trip on an HKCU Run sandbox value (created by the test, removed in
/// finally — Windows CI runners are ephemeral and the value name is unique),
/// plus the native writable-open behavior. The startup-folder round-trip runs
/// against temp directories on every OS (see StartupFolderQuarantineStrategyTests).
/// Only the emitted per-step assertions make any claim; non-Windows hosts skip.
/// </summary>
public class WindowsQuarantineNativeTests
{
    private const string ProbeValueName = "AaIssue6Probe";

    private static AutoStartEntry ProbeEntry(string data) => new()
    {
        SourceKind = RunKeySource.Kind,
        Scope = "user",
        StableKey = StableKey.BuildWithTargets(RunKeySource.Kind, "user",
            $@"hkcu\software\microsoft\windows\currentversion\run\{ProbeValueName.ToLowerInvariant()}", new[] { data }),
        NativeKey = $@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\{ProbeValueName}".ToLowerInvariant(),
        DisplayName = ProbeValueName,
        TargetPaths = new[] { data },
        RawValueSnapshot = data,
        Signing = SigningStatus.Unverified,
        Observation = ObservationHealth.Ok,
    };

    [WindowsFact]
    [Trait("Category", "WindowsNativeSmoke")]
    public void NativeRunKeyStrategyRoundTrip_OnHkcuRunSandbox()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            return;

        const string subKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string data = @"%SystemRoot%\explorer.exe --aa-issue6-probe";
        DeleteProbeValues();
        using (var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey, writable: true)!)
            runKey.SetValue(ProbeValueName, data, Microsoft.Win32.RegistryValueKind.ExpandString);

        var strategy = new RunKeyQuarantineStrategy(new WindowsRegistryWriteProbe());
        try
        {
            var plan = strategy.Plan(ProbeEntry(data));
            Assert.True(plan.CanQuarantine, plan.Reason);

            Assert.Equal(MutationStatus.Ok, strategy.Execute(plan.BeforeStateJson!).Status);
            using (var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey)!)
            {
                Assert.Null(runKey.GetValue(ProbeValueName));
                Assert.Equal(data, runKey.GetValue(ProbeValueName + RunKeyQuarantineStrategy.QuarantinedSuffix,
                    null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames));
            }

            Assert.Equal(MutationStatus.Ok, strategy.Restore(plan.BeforeStateJson!).Status);
            using (var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey)!)
            {
                Assert.Equal(data, runKey.GetValue(ProbeValueName, null,
                    Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames));
                Assert.Equal(Microsoft.Win32.RegistryValueKind.ExpandString, runKey.GetValueKind(ProbeValueName));
                Assert.Null(runKey.GetValue(ProbeValueName + RunKeyQuarantineStrategy.QuarantinedSuffix));
            }
        }
        finally
        {
            DeleteProbeValues();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void DeleteProbeValues()
    {
        using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        runKey?.DeleteValue(ProbeValueName, throwOnMissingValue: false);
        runKey?.DeleteValue(ProbeValueName + ".aa-quarantined", throwOnMissingValue: false);
    }

    [WindowsFact]
    [Trait("Category", "WindowsNativeSmoke")]
    public void NativeWriteProbe_DeniesMissingKey_HonestFailure()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            return;

        var probe = new WindowsRegistryWriteProbe();
        // The write probe must never create keys implicitly — a missing key
        // is an honest unknown, not a silent mkdir. (Direct call under the
        // platform guard; analyzer cannot flow the guard into delegates.)
        Exception? error = null;
        try
        {
            probe.OpenWritable("HKCU", @"Software\AutostartAuditNoSuchKeyIssue6");
        }
        catch (Exception ex)
        {
            error = ex;
        }
        var ex2 = Assert.IsType<RegistryUnknownException>(error);
        Assert.Contains("does not exist", ex2.Message);
    }
}
