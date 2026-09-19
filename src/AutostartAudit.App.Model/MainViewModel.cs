using System.Collections.ObjectModel;
using AutostartAudit.Core.Export;
using AutostartAudit.Core.Model;
using AutostartAudit.Core.Snapshot;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutostartAudit.App.Model;

/// <summary>
/// The single-window UI state: scan → filter → quarantine → restore, the
/// snapshot-diff tab, the change-journal tab, and the export flow. All
/// machine access goes through <see cref="IScanService"/>, so this class is
/// fully exercisable with a fixture service on any host.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IScanService _service;
    private ScanDocument? _lastScan;

    public MainViewModel(IScanService service)
    {
        _service = service;
        StatusMessage = "Ready. Run a scan to inventory auto-start entries.";
    }

    public ObservableCollection<EntryRow> Entries { get; } = new();
    public ObservableCollection<SourceStatusRow> SourceStatuses { get; } = new();
    public ObservableCollection<DiffRow> DiffRows { get; } = new();
    public ObservableCollection<JournalRow> JournalRows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isScanning;

    public bool IsBusy => IsScanning;
    public bool IsIdle => !IsScanning;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string? _scanCompleteText;

    /// <summary>Free-text filter token (matches name/source/target/publisher/key).</summary>
    [ObservableProperty]
    private string _searchToken = "";

    /// <summary>"" = all source kinds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableSourceKinds))]
    private string _sourceFilter = "";

    /// <summary>"" = all signing statuses; otherwise signed/unsigned/invalidSignature/unverified.</summary>
    [ObservableProperty]
    private string _signingFilter = "";

    /// <summary>"" = all scopes; otherwise machine/user.</summary>
    [ObservableProperty]
    private string _scopeFilter = "";

    [ObservableProperty]
    private EntryRow? _selectedEntry;

    [ObservableProperty]
    private JournalRow? _selectedJournalRow;

    /// <summary>Export dialog default: redaction ON. Turning it off is an explicit user action.</summary>
    [ObservableProperty]
    private bool _redactUserProfiles = true;

    [ObservableProperty]
    private ExportFormat _exportFormat = ExportFormat.Json;

    public IReadOnlyList<string> AvailableSourceKinds { get; private set; } = Array.Empty<string>();

    public static IReadOnlyList<string> SigningFilterOptions { get; } =
        new[] { "", "signed", "unsigned", "invalidSignature", "unverified" };

    public static IReadOnlyList<string> ScopeFilterOptions { get; } = new[] { "", "machine", "user" };

    /// <summary>Text form of the current effective filter for status/automation reporting.</summary>
    public string ActiveFilterSummary =>
        $"source={Display(SourceFilter)} signing={Display(SigningFilter)} scope={Display(ScopeFilter)} text='{SearchToken}'";

    private static string Display(string v) => string.IsNullOrEmpty(v) ? "all" : v;

    partial void OnSearchTokenChanged(string value) => ApplyFilters();
    partial void OnSourceFilterChanged(string value) => ApplyFilters();
    partial void OnSigningFilterChanged(string value) => ApplyFilters();
    partial void OnScopeFilterChanged(string value) => ApplyFilters();

    [RelayCommand]
    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var document = await _service.ScanAsync(cancellationToken);
            _lastScan = document;

            // Assess performs live re-reads per entry — keep it off the UI
            // thread; rows are materialized on the pool, then bound here.
            var rows = await Task.Run(
                () => document.Entries
                    .OrderBy(e => e.SourceKind, StringComparer.Ordinal)
                    .ThenBy(e => e.DisplayName, StringComparer.Ordinal)
                    .Select(e => new EntryRow(e, _service.AssessQuarantine(e)))
                    .ToList(),
                cancellationToken);

            Entries.Clear();
            foreach (var row in rows)
                Entries.Add(row);

            SourceStatuses.Clear();
            foreach (var source in document.Sources)
                SourceStatuses.Add(new SourceStatusRow(
                    source.SourceKind, ReportExporter.CapabilityText(source.Capability), source.Detail));

            AvailableSourceKinds = document.Sources.Select(s => s.SourceKind).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            OnPropertyChanged(nameof(AvailableSourceKinds));

            ScanCompleteText = document.ScanComplete
                ? "Scan complete: every source reported fully readable."
                : "Scan INCOMPLETE: at least one source could not be fully read — the inventory may be missing entries. This is not a clean machine.";

            StatusMessage = $"Scan finished: {document.Entries.Count} entries, {document.Sources.Count} sources.";
            ApplyFilters();
            await RefreshJournalAsync();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled. No completeness claim is made.";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    public async Task CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (_lastScan is null)
        {
            StatusMessage = "Run a scan first — there is nothing to save as a snapshot yet.";
            return;
        }

        var outcome = await _service.CaptureAndDiffAsync(_lastScan, cancellationToken);
        DiffRows.Clear();
        if (outcome.IsBaseline)
        {
            StatusMessage = $"Baseline snapshot #{outcome.SnapshotId} established. No prior snapshot exists, so no diff is shown — that is a first capture, not an unchanged machine.";
            return;
        }

        foreach (var row in DiffRow.FromScanDiff(outcome.Diff ?? ScanDiff.Empty))
            DiffRows.Add(row);
        StatusMessage = $"Snapshot #{outcome.SnapshotId} saved; diff vs #{outcome.PreviousSnapshotId}: "
            + $"{outcome.Diff?.Added.Count ?? 0} added, {outcome.Diff?.Removed.Count ?? 0} removed, "
            + $"{outcome.Diff?.Changed.Count ?? 0} changed.";
    }

    [RelayCommand]
    public async Task QuarantineSelectedAsync(CancellationToken cancellationToken = default)
    {
        var row = SelectedEntry;
        if (row is null)
        {
            StatusMessage = "Select an entry row first.";
            return;
        }
        if (!row.QuarantineAvailable)
        {
            StatusMessage = $"Read-only: {row.QuarantineReason} Nothing was changed.";
            return;
        }

        var outcome = await _service.QuarantineAsync(row.Entry, cancellationToken);
        StatusMessage = outcome.Status switch
        {
            Core.Quarantine.QuarantineStatus.Quarantined => $"Quarantined '{row.DisplayName}' (journal #{outcome.RecordId}).",
            Core.Quarantine.QuarantineStatus.ElevationDeclined => $"Elevation declined for '{row.DisplayName}'. The entry is untouched; the journal states the outcome honestly.",
            Core.Quarantine.QuarantineStatus.ReadOnly => $"Read-only: {outcome.Detail} Nothing was changed.",
            _ => $"Quarantine failed: {outcome.Detail}",
        };
        await RefreshJournalAsync();
    }

    [RelayCommand]
    public async Task RestoreSelectedJournalRecordAsync(CancellationToken cancellationToken = default)
    {
        var row = SelectedJournalRow;
        if (row is null)
        {
            StatusMessage = "Select a journal record first.";
            return;
        }
        if (!row.CanRestore)
        {
            StatusMessage = $"Record #{row.Id} is not restorable ({row.RestoreText}). Restore is offered only for verified quarantines.";
            return;
        }

        var outcome = await _service.RestoreAsync(row.Id, cancellationToken);
        row.StatusText = outcome.Detail ?? "";
        StatusMessage = outcome.Status switch
        {
            Core.Quarantine.RestoreStatus.Restored => $"Restored record #{row.Id} ('{row.DisplayName}').",
            Core.Quarantine.RestoreStatus.ElevationDeclined => $"Elevation for restore of #{row.Id} was declined; nothing else changed.",
            Core.Quarantine.RestoreStatus.ReadOnly => $"Record #{row.Id} cannot be restored: {outcome.Detail}",
            _ => $"Restore of #{row.Id} failed: {outcome.Detail}",
        };
        await RefreshJournalAsync();
    }

    [RelayCommand]
    public async Task RefreshJournalAsync(CancellationToken cancellationToken = default)
    {
        var records = await _service.ListJournalAsync(cancellationToken);
        JournalRows.Clear();
        foreach (var record in records)
            JournalRows.Add(new JournalRow(record));
    }

    /// <summary>
    /// Builds export text for the current scan. Redaction follows
    /// <see cref="RedactUserProfiles"/>, which starts checked.
    /// </summary>
    public string BuildExportText()
    {
        if (_lastScan is null)
            throw new InvalidOperationException("Run a scan before exporting.");
        return ReportExporter.Render(_lastScan, ExportFormat, RedactUserProfiles);
    }

    /// <summary>
    /// Writes the export to disk; the WPF layer passes a SaveFileDialog-backed path.
    /// </summary>
    public async Task<string> ExportToFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = BuildExportText();
        await File.WriteAllTextAsync(path, text, cancellationToken);
        StatusMessage = $"Export written to {path} ({(RedactUserProfiles ? "user-profile paths redacted" : "REDACTION OFF — contains raw paths")}).";
        return path;
    }

    /// <summary>
    /// Supplied by the WPF view to present a SaveFileDialog and return the
    /// chosen path (or null when cancelled). Tests inject a stub — the view
    /// model never references WPF types.
    /// </summary>
    public Func<ExportFormat, Task<string?>>? ExportPathRequested { get; set; }

    [RelayCommand]
    public async Task ExportAsync(CancellationToken cancellationToken = default)
    {
        if (_lastScan is null)
        {
            StatusMessage = "Run a scan before exporting.";
            return;
        }
        if (ExportPathRequested is null)
        {
            StatusMessage = "No save dialog is available in this host.";
            return;
        }

        var path = await ExportPathRequested(ExportFormat);
        if (path is null)
        {
            StatusMessage = "Export cancelled.";
            return;
        }
        await ExportToFileAsync(path, cancellationToken);
    }

    private void ApplyFilters()
    {
        foreach (var row in Entries)
        {
            row.Visible =
                (string.IsNullOrEmpty(SourceFilter) || row.SourceKind == SourceFilter)
                && (string.IsNullOrEmpty(SigningFilter)
                    || string.Equals(row.Signing.ToString(), SigningFilter, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrEmpty(ScopeFilter) || row.Scope == ScopeFilter)
                && (string.IsNullOrEmpty(SearchToken) || row.Matches(SearchToken.Trim()));
        }
        FilterVersion++;
        OnPropertyChanged(nameof(FilterVersion));
    }

    /// <summary>
    /// Bumped whenever filters change; the view calls ICollectionView.Refresh
    /// on this so per-row <c>Visible</c> flags are re-evaluated. The
    /// ObservableCollection itself stays stable (virtualization-friendly —
    /// rows are never removed/re-added by filtering).
    /// </summary>
    public int FilterVersion { get; private set; }
}
