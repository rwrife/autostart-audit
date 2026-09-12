using System.Text.Json;
using AutostartAudit.Cli;

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

    [Fact]
    public void ScanJson_PrintsValidEmptyEnvelope_AndSucceeds()
    {
        var (code, stdout, stderr) = Run("scan", "--json");
        Assert.Equal(0, code);
        Assert.Equal(string.Empty, stderr.Trim());
        var line = stdout.Trim();
        using var doc = JsonDocument.Parse(line); // invalid JSON would throw
        Assert.Equal("[]", doc.RootElement.GetProperty("entries").ToString());
        Assert.Equal("[]", doc.RootElement.GetProperty("sources").ToString());
    }

    [Fact]
    public void ScanWithoutJson_PrintsHumanSummary_AndSucceeds()
    {
        var (code, stdout, stderr) = Run("scan");
        Assert.Equal(0, code);
        Assert.Contains("0 entries", stdout);
        Assert.Equal(string.Empty, stderr.Trim());
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
