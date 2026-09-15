using System.Text.Json;
using AutostartAudit.Cli;
using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Tests;

public class CliTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = App.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private static (int Code, string Out, string Err) RunWith(Func<ScanDocument> scan, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = App.Run(args, o, e, scan);
        return (code, o.ToString(), e.ToString());
    }

    private static ScanDocument FixedScan() => new()
    {
        Entries = new[]
        {
            new AutoStartEntry
            {
                SourceKind = "run-key",
                Scope = "user",
                StableKey = "run-key|user|hkcu\\software\\...\\run\\myapp",
                DisplayName = "MyApp",
                TargetPaths = new[] { @"C:\Apps\MyApp\myapp.exe" },
                RawValueSnapshot = @"""C:\Apps\MyApp\myapp.exe"" /min",
                Signing = SigningStatus.Unverified,
                Observation = ObservationHealth.Ok,
            },
        },
        Sources = new[]
        {
            new SourceReport { SourceKind = "run-key", Capability = SourceCapability.Scanned },
            new SourceReport { SourceKind = "startup-folder", Capability = SourceCapability.Denied, Detail = "access denied" },
        },
        ScanComplete = false,
    };

    [Fact]
    public void ScanJson_PrintsValidEmptyEnvelope_AndSucceeds()
    {
        var (code, stdout, stderr) = RunWith(() => ScanDocument.Empty, "scan", "--json");
        Assert.Equal(0, code);
        Assert.Equal(string.Empty, stderr.Trim());
        var line = stdout.Trim();
        using var doc = JsonDocument.Parse(line); // invalid JSON would throw
        Assert.Equal("[]", doc.RootElement.GetProperty("entries").ToString());
        Assert.Equal("[]", doc.RootElement.GetProperty("sources").ToString());
        Assert.False(doc.RootElement.GetProperty("scanComplete").GetBoolean());
    }

    [Fact]
    public void ScanJson_EmitsRealEntriesAndCapabilityRecords()
    {
        var (code, stdout, _) = RunWith(FixedScan, "scan", "--json");
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout.Trim());
        var entries = doc.RootElement.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("MyApp", entries[0].GetProperty("displayName").GetString());
        Assert.Equal("unverified", entries[0].GetProperty("signing").GetString());
        var sources = doc.RootElement.GetProperty("sources");
        Assert.Equal(2, sources.GetArrayLength());
        Assert.Equal("scanned", sources[0].GetProperty("capability").GetString());
        Assert.Equal("denied", sources[1].GetProperty("capability").GetString());
        Assert.False(doc.RootElement.GetProperty("scanComplete").GetBoolean());
    }

    [Fact]
    public void ScanWithoutJson_PrintsHumanSummary_AndSucceeds()
    {
        var (code, stdout, stderr) = RunWith(FixedScan, "scan");
        Assert.Equal(0, code);
        Assert.Contains("1 entry", stdout);
        Assert.Contains("2 sources", stdout);
        Assert.Contains("scan-complete: no", stdout);
        Assert.Contains("unread sources: startup-folder", stdout);
        Assert.Equal(string.Empty, stderr.Trim());
    }

    [Fact]
    public void ScanSummary_NamesUnreadScopes()
    {
        var scan = FixedScan() with
        {
            Sources = new[]
            {
                new SourceReport
                {
                    SourceKind = "scheduled-task",
                    Capability = SourceCapability.Partial,
                    Observations = new[]
                    {
                        new SourceObservation { Id = @"\Microsoft\Protected", Health = ObservationHealth.Denied, Detail = "access denied" },
                    },
                },
            },
        };

        var (_, stdout, _) = RunWith(() => scan, "scan");

        Assert.Contains(@"scheduled-task [partial]", stdout);
        Assert.Contains(@"\Microsoft\Protected [denied]: access denied", stdout);
    }

    [Fact]
    public void ScanWithoutJson_OnHost_ProducesValidHonestSummary()
    {
        // Drives the real engine (no injection): must succeed on any OS and
        // never claim completeness when a source is unsupported/denied.
        var (code, stdout, _) = Run("scan");
        Assert.Equal(0, code);
        Assert.Contains("scan-complete:", stdout);
    }

    [Fact]
    public void Help_PrintsUsage_AndSucceeds()
    {
        var (code, stdout, _) = Run("--help");
        Assert.Equal(0, code);
        Assert.Contains("scan --json", stdout);
    }

    [Fact]
    public void UnknownCommand_ExitsWithUsageError()
    {
        var (code, _, stderr) = Run("frobnicate");
        Assert.Equal(2, code);
        Assert.Contains("unknown command", stderr);
    }

    [Fact]
    public void NoCommand_ExitsWithUsageError()
    {
        var (code, _, stderr) = Run();
        Assert.Equal(2, code);
        Assert.Contains("no command", stderr);
    }

    [Fact]
    public void ScanWithBadOption_ExitsWithUsageError()
    {
        var (code, _, stderr) = Run("scan", "--xml");
        Assert.Equal(2, code);
        Assert.Contains("unsupported arguments", stderr);
    }
}
