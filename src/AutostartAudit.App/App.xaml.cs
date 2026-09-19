using System.IO;
using System.Windows;
using AutostartAudit.App.Model;
using AutostartAudit.Core.Export;

namespace AutostartAudit.App;

public partial class Application : System.Windows.Application
{
    /// <summary>
    /// Normal launch shows the live window. <c>--smoke-test</c> runs the
    /// fixture-backed UI smoke (no real machine reads or mutations) and exits
    /// with 0/1 — CI invokes this on windows-latest as the WPF smoke gate.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--smoke-test", StringComparer.Ordinal))
        {
            // Fire-and-await: awaiting (not blocking) keeps the dispatcher
            // pumping so the view model's await continuations can run.
            _ = RunSmokeAsync();
            return;
        }

        var window = new MainWindow();
        window.Show();
    }

    private async Task RunSmokeAsync()
    {
        var report = new List<string>();
        var failures = new List<string>();
        var exportPath = Path.Combine(Path.GetTempPath(), $"aa-smoke-{Guid.NewGuid():N}.md");
        var reportPath = Environment.GetEnvironmentVariable("AA_SMOKE_REPORT") ?? "ui-smoke-report.txt";
        try
        {
            var fixture = new FixtureScanService();
            var window = new MainWindow(fixture);
            var vm = window.ViewModel;
            vm.ExportPathRequested = _ => Task.FromResult<string?>(exportPath);

            await vm.ScanAsync();
            report.Add($"scan: {vm.Entries.Count} rows, {vm.SourceStatuses.Count} sources");

            Check(vm.Entries.Count == FixtureScanService.EntryCount,
                $"inventory has {vm.Entries.Count} rows (expected {FixtureScanService.EntryCount})");
            Check(vm.ScanCompleteText!.Contains("INCOMPLETE", StringComparison.Ordinal),
                "incomplete scan is announced as incomplete, never clean");
            Check(vm.SourceStatuses.Any(s => s.CapabilityText == "denied"),
                "denied source is visible in the completeness footer");

            // Keyboard-only flow: filter → quarantine (refused on a read-only row).
            vm.SearchToken = "updater";
            Check(vm.Entries.Count(r => r.Visible) == FixtureScanService.MatchingUpdaterCount,
                $"text filter narrows visible rows to {FixtureScanService.MatchingUpdaterCount}");
            vm.SearchToken = "";

            vm.SelectedEntry = vm.Entries.FirstOrDefault(r => !r.QuarantineAvailable);
            Check(vm.SelectedEntry is not null, "at least one read-only row exists");
            await vm.QuarantineSelectedCommand.ExecuteAsync(null);
            Check(vm.StatusMessage.Contains("Read-only", StringComparison.Ordinal),
                $"read-only quarantine is refused honestly ({vm.StatusMessage})");
            Check(fixture.QuarantineCalls == 0, "no mutation attempted on a read-only row");

            // Journal tab: Pending is never restorable and never clean; executed is restorable.
            Check(vm.JournalRows.Any(r => r.CanRestore), "executed record offers per-row restore");
            Check(vm.JournalRows.Any(r => !r.CanRestore && r.ResultText.Contains("pending", StringComparison.OrdinalIgnoreCase)),
                "pending record renders as unresolved (never clean, never restorable)");
            var restorable = vm.JournalRows.First(r => r.CanRestore);
            vm.SelectedJournalRow = restorable;
            await vm.RestoreSelectedJournalRecordCommand.ExecuteAsync(null);
            Check(fixture.RestoreCalls.Contains(restorable.Id), "per-row restore routed to the service");
            report.Add($"journal: {vm.JournalRows.Count} rows, restore routed for #{restorable.Id}");

            // Export with redaction ON (default): no user-profile identity survives.
            vm.ExportFormat = ExportFormat.Markdown;
            Check(vm.RedactUserProfiles, "path redaction is ON by default in the export flow");
            await vm.ExportCommand.ExecuteAsync(null);
            var text = await File.ReadAllTextAsync(exportPath);
            Check(!text.Contains(@"C:\Users\", StringComparison.OrdinalIgnoreCase),
                @"export contains no C:\Users\<name> identity");
            Check(!text.Contains("Test", StringComparison.OrdinalIgnoreCase),
                "export contains no fixture profile name");
            Check(text.Contains(PathRedaction.Marker, StringComparison.Ordinal),
                "export carries the redaction marker");
            Check(text.Contains("Signature policy", StringComparison.Ordinal),
                "export discloses the local-only signature policy");
            report.Add("export: markdown written, redaction verified");

            foreach (var f in failures)
                report.Add($"SMOKE FAIL: {f}");
            report.Add(failures.Count == 0
                ? "UI SMOKE PASS: fixture-backed scan/filter/journal/export verified without real-machine mutation."
                : $"UI SMOKE FAIL: {failures.Count} assertion(s) failed.");
            Environment.ExitCode = failures.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.Add($"SMOKE FAIL (exception): {ex}");
            Environment.ExitCode = 1;
        }
        finally
        {
            try
            {
                if (File.Exists(exportPath))
                    File.Delete(exportPath);
                await File.WriteAllLinesAsync(reportPath, report);
                foreach (var line in report)
                    Console.WriteLine(line);
            }
            catch (IOException)
            {
                // Temp/report cleanup is best-effort; it does not affect the verdict.
            }
            Shutdown();
        }

        void Check(bool condition, string what)
        {
            if (!condition) failures.Add(what);
        }
    }
}
