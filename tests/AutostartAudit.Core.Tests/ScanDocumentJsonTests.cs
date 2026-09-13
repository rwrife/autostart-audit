using System.Text.Json;
using AutostartAudit.Core;
using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

public class ScanDocumentJsonTests
{
    [Fact]
    public void EmptyDocument_SerializesToExactEnvelope()
    {
        Assert.Equal("{\"entries\":[],\"sources\":[],\"scanComplete\":false}", ScanDocument.Empty.ToJson());
    }

    [Fact]
    public void EmptyDocument_RoundTripsAsValidJson()
    {
        var parsed = JsonSerializer.Deserialize<ScanDocument>(ScanDocument.Empty.ToJson(), JsonOptions.Default);
        Assert.NotNull(parsed);
        Assert.Empty(parsed.Entries);
        Assert.Empty(parsed.Sources);
        Assert.False(parsed.ScanComplete);
    }

    [Fact]
    public void Engine_DefaultScan_AlwaysReportsBothDefaultSources()
    {
        // Host-independent: whatever the OS, the default scan must list both
        // sources with an honest capability (never silently missing).
        var doc = ScanEngine.ScanAll();
        var kinds = doc.Sources.Select(s => s.SourceKind).ToList();
        Assert.Contains(RunKeySource.Kind, kinds);
        Assert.Contains(StartupFolderSource.Kind, kinds);
        using var _ = JsonDocument.Parse(doc.ToJson()); // throws if invalid JSON
    }

    [Fact]
    public void Document_WithDeniedSource_KeepsCapabilityVisible()
    {
        // Doctrine check: a source that was not readable must still appear in
        // the sources list with an explicit capability, never vanish.
        var doc = new ScanDocument
        {
            Entries = Array.Empty<AutoStartEntry>(),
            Sources = new[] { new SourceReport { SourceKind = "scheduled-task", Capability = SourceCapability.Denied, Detail = "requires elevation" } },
            ScanComplete = false,
        };
        var json = doc.ToJson();
        using var parsed = JsonDocument.Parse(json);
        var sources = parsed.RootElement.GetProperty("sources");
        Assert.Equal(1, sources.GetArrayLength());
        Assert.Equal("denied", sources[0].GetProperty("capability").GetString());
    }
}
