using AutostartAudit.App.Model;
using AutostartAudit.Core.Export;

namespace AutostartAudit.App.Tests;

/// <summary>
/// Presentation-logic tests over the same fixture-backed service the WPF
/// smoke test uses. These run on ANY host (including Linux CI steps) because
/// the view models reference no WPF types; the live WPF smoke additionally
/// runs on windows-latest against the real controls.
/// </summary>
public class MainViewModelTests
{
    private static async Task<(MainViewModel vm, FixtureScanService fixture)> ScannedAsync()
    {
        var fixture = new FixtureScanService();
        var vm = new MainViewModel(fixture);
        await vm.ScanAsync();
        return (vm, fixture);
    }

    [Fact]
    public async Task Scan_PopulatesRowsAndCompletenessFooter()
    {
        var (vm, _) = await ScannedAsync();
        Assert.Equal(FixtureScanService.EntryCount, vm.Entries.Count);
        Assert.Contains("INCOMPLETE", vm.ScanCompleteText);
        // Observation-truth boundary: the denied source is disclosed, not hidden.
        Assert.Contains(vm.SourceStatuses, s => s.CapabilityText == "denied");
    }

    [Fact]
    public async Task Scan_RowsCarryTextStatuses_NeverColorOnly()
    {
        var (vm, _) = await ScannedAsync();
        var signed = vm.Entries.Single(r => r.DisplayName == "Vendor Updater");
        Assert.Equal("signed", signed.SigningText);
        Assert.Equal("Vendor Inc", signed.Publisher);
        var unverified = vm.Entries.Single(r => r.DisplayName == "Telemetry task");
        Assert.Equal("unverified", unverified.SigningText);
        // Unverified is never collapsed into unsigned.
        Assert.NotEqual("unsigned", unverified.SigningText);
    }

    [Theory]
    [InlineData("updater", 1)]
    [InlineData("telemetry", 1)]
    [InlineData("svchost", 1)]
    [InlineData("zzz-nothing", 0)]
    public async Task SearchToken_FiltersVisibleRows(string token, int expectedVisible)
    {
        var (vm, _) = await ScannedAsync();
        vm.SearchToken = token;
        Assert.Equal(expectedVisible, vm.Entries.Count(r => r.Visible));
    }

    [Fact]
    public async Task SigningAndScopeFilters_Compose()
    {
        var (vm, _) = await ScannedAsync();
        vm.SigningFilter = "signed";
        vm.ScopeFilter = "user";
        Assert.Single(vm.Entries, r => r.Visible);
        vm.ScopeFilter = "machine";
        Assert.Single(vm.Entries, r => r.Visible);
        vm.SigningFilter = "unverified";
        vm.ScopeFilter = "machine";
        Assert.Single(vm.Entries, r => r.Visible);
    }

    [Fact]
    public async Task QuarantineReadOnlyRow_RefusesWithoutMutation()
    {
        var (vm, fixture) = await ScannedAsync();
        vm.SelectedEntry = vm.Entries.First(r => !r.QuarantineAvailable);
        await vm.QuarantineSelectedCommand.ExecuteAsync(null);
        Assert.Contains("Read-only", vm.StatusMessage);
        Assert.Equal(0, fixture.QuarantineCalls);
    }

    [Fact]
    public async Task QuarantineAvailableRow_GoesThroughService()
    {
        var (vm, fixture) = await ScannedAsync();
        vm.SelectedEntry = vm.Entries.Single(r => r.QuarantineAvailable);
        await vm.QuarantineSelectedCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.QuarantineCalls);
        Assert.Contains("Quarantined", vm.StatusMessage);
    }

    [Fact]
    public async Task Journal_PendingRecordIsNeverRestorable_ExecutedIs()
    {
        var (vm, _) = await ScannedAsync();
        var pending = vm.JournalRows.Single(r => r.Id == 2);
        var executed = vm.JournalRows.Single(r => r.Id == 1);
        Assert.Contains("pending", pending.ResultText, StringComparison.OrdinalIgnoreCase);
        Assert.False(pending.CanRestore);
        Assert.True(executed.CanRestore);
    }

    [Fact]
    public async Task RestorePendingRecord_IsRefused()
    {
        var (vm, fixture) = await ScannedAsync();
        vm.SelectedJournalRow = vm.JournalRows.Single(r => r.Id == 2);
        await vm.RestoreSelectedJournalRecordCommand.ExecuteAsync(null);
        Assert.Contains("not restorable", vm.StatusMessage);
        Assert.Empty(fixture.RestoreCalls);
    }

    [Fact]
    public async Task RestoreExecutedRecord_RoutesToService()
    {
        var (vm, fixture) = await ScannedAsync();
        vm.SelectedJournalRow = vm.JournalRows.Single(r => r.Id == 1);
        await vm.RestoreSelectedJournalRecordCommand.ExecuteAsync(null);
        Assert.Equal(new[] { 1L }, fixture.RestoreCalls);
        Assert.Contains("Restored", vm.StatusMessage);
    }

    [Fact]
    public async Task BaselineSnapshot_IsNotPresentedAsDiff()
    {
        var (vm, _) = await ScannedAsync();
        await vm.CaptureSnapshotCommand.ExecuteAsync(null);
        Assert.Contains("Baseline", vm.StatusMessage);
        Assert.Empty(vm.DiffRows);
    }

    [Fact]
    public async Task Export_RedactionOnByDefault_RemovesProfilePaths()
    {
        var (vm, _) = await ScannedAsync();
        Assert.True(vm.RedactUserProfiles, "redaction checkbox starts ON");

        var path = Path.Combine(Path.GetTempPath(), $"aa-export-{Guid.NewGuid():N}.json");
        try
        {
            vm.ExportFormat = ExportFormat.Json;
            await vm.ExportToFileAsync(path);
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(@"C:\Users\", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(PathRedaction.Marker, json);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportCommand_UsesInjectedDialogPath()
    {
        var (vm, _) = await ScannedAsync();
        var path = Path.Combine(Path.GetTempPath(), $"aa-export-{Guid.NewGuid():N}.md");
        vm.ExportPathRequested = format =>
        {
            Assert.Equal(ExportFormat.Markdown, format);
            return Task.FromResult<string?>(path);
        };
        vm.ExportFormat = ExportFormat.Markdown;
        try
        {
            await vm.ExportCommand.ExecuteAsync(null);
            Assert.Contains("written", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("Signature policy", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportCommand_Cancelled_WritesNothing()
    {
        var (vm, _) = await ScannedAsync();
        vm.ExportPathRequested = _ => Task.FromResult<string?>(null);
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Contains("cancelled", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportBeforeScan_ExplainsWhyItRefuses()
    {
        var vm = new MainViewModel(new FixtureScanService());
        Assert.Throws<InvalidOperationException>(() => vm.BuildExportText());
    }

    [Fact]
    public async Task RowIdentity_IsNormalizedKeyNotDisplayName()
    {
        var (vm, _) = await ScannedAsync();
        var row = vm.Entries.Single(r => r.DisplayName == "Vendor Updater");
        Assert.DoesNotContain("display", row.Identity, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("run-key|user|", row.Identity, StringComparison.Ordinal);
    }
}
