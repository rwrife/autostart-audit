using AutostartAudit.Core.Export;
using AutostartAudit.Core.Model;
using AutostartAudit.Core.Quarantine;
using AutostartAudit.Core.Scan;
using AutostartAudit.Core.Snapshot;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutostartAudit.App.Model;

/// <summary>
/// One inventory row. Every status is carried as plain text as well as an
/// enum — the UI must never convey state by color alone, and screen readers
/// read these strings. Row identity is the normalized StableKey identity,
/// never the display name.
/// </summary>
public sealed partial class EntryRow : ObservableObject
{
    public EntryRow(AutoStartEntry entry, QuarantineDecision quarantine)
    {
        Entry = entry;
        DisplayName = entry.DisplayName;
        SourceKind = entry.SourceKind;
        Scope = entry.Scope;
        Targets = entry.TargetPaths.Count == 0
            ? "(no target path observed)"
            : string.Join("; ", entry.TargetPaths);
        SigningText = ReportExporter.SigningText(entry);
        Publisher = entry.SignerSubject ?? "(none)";
        HealthText = ReportExporter.HealthText(entry.Observation);
        State = entry.State ?? "-";
        NativeKey = entry.NativeKey ?? entry.StableKey;
        Identity = StableKey.Build(entry.SourceKind, entry.Scope, entry.NativeKey ?? entry.StableKey);
        QuarantineAvailable = quarantine.CanQuarantine;
        QuarantineReason = quarantine.CanQuarantine ? "can be quarantined (exact restore plan exists)" : quarantine.Reason ?? "read-only";
    }

    public AutoStartEntry Entry { get; }

    /// <summary>Normalized identity (source+key), used for selection/matching — never the display name.</summary>
    public string Identity { get; }

    public string NativeKey { get; }

    public string DisplayName { get; }
    public string SourceKind { get; }
    public string Scope { get; }
    public string Targets { get; }
    public string SigningText { get; }
    public string Publisher { get; }
    public string HealthText { get; }
    public string State { get; }

    /// <summary>True when a strategy can describe an exact restore for this entry right now.</summary>
    public bool QuarantineAvailable { get; }

    /// <summary>Why the row is quarantining or read-only, as readable text.</summary>
    public string QuarantineReason { get; }

    public SigningStatus Signing => Entry.Signing;

    /// <summary>Filter token matching (case-insensitive contains on the visible row text).</summary>
    public bool Matches(string token) =>
        DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase)
        || SourceKind.Contains(token, StringComparison.OrdinalIgnoreCase)
        || Targets.Contains(token, StringComparison.OrdinalIgnoreCase)
        || Publisher.Contains(token, StringComparison.OrdinalIgnoreCase)
        || NativeKey.Contains(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>Current filter visibility; the view's ICollectionView reads this. Rows are never removed from the collection by filtering (virtualization-friendly).</summary>
    [ObservableProperty]
    private bool _visible = true;
}

/// <summary>Per-source capability line shown in the persistent scan-completeness footer.</summary>
public sealed record SourceStatusRow(string SourceKind, string CapabilityText, string? Detail)
{
    public string Display => Detail is null ? $"{SourceKind}: {CapabilityText}" : $"{SourceKind}: {CapabilityText} — {Detail}";
}

/// <summary>One journal row for the change-journal tab with per-row restore.</summary>
public sealed partial class JournalRow : ObservableObject
{
    public JournalRow(JournalRecord record)
    {
        Record = record;
        Id = record.Id;
        CreatedAtUtc = record.CreatedAtUtc;
        DisplayName = record.DisplayName;
        SourceKind = record.SourceKind;
        Scope = record.Scope;
        NativeKey = record.NativeKey;
        ResultText = record.Result switch
        {
            QuarantineResult.Pending => "pending — mutation outcome NOT established (state unknown)",
            QuarantineResult.Executed => "executed — quarantine verified",
            QuarantineResult.Failed => "failed — see detail",
            _ => record.Result.ToString(),
        };
        RestoreText = record.RestoreStatus switch
        {
            JournalRestoreStatus.None when record.Result == QuarantineResult.Executed => "not restored",
            JournalRestoreStatus.None => "n/a (never executed)",
            JournalRestoreStatus.Restored => "restored",
            JournalRestoreStatus.Failed => "restore FAILED — retry available",
            _ => record.RestoreStatus.ToString(),
        };
        DetailText = record.ErrorDetail ?? record.RestoreErrorDetail ?? "";
    }

    public JournalRecord Record { get; }

    public long Id { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string DisplayName { get; }
    public string SourceKind { get; }
    public string Scope { get; }
    public string NativeKey { get; }
    public string ResultText { get; }
    public string RestoreText { get; }
    public string DetailText { get; }

    /// <summary>
    /// Restore is offered only for verified-executed records that are not yet
    /// restored. A Pending record has NO trustworthy inverse — offering
    /// restore there would misrepresent an unknown state as fixable.
    /// </summary>
    public bool CanRestore =>
        Record.Result == QuarantineResult.Executed
        && Record.RestoreStatus != JournalRestoreStatus.Restored;

    [ObservableProperty]
    private string _statusText = "";
}

/// <summary>One row of the snapshot-diff tab.</summary>
public sealed record DiffRow(string Bucket, string DisplayName, string SourceKind, string Scope, string Details)
{
    public static IEnumerable<DiffRow> FromScanDiff(ScanDiff diff)
    {
        foreach (var r in diff.Added)
            yield return new DiffRow("added", r.DisplayNameAfter ?? r.DisplayNameBefore ?? "(unknown)", r.SourceKind, r.Scope,
                r.TargetPathsAfter is null ? "" : "targets: " + string.Join("; ", r.TargetPathsAfter));
        foreach (var r in diff.Removed)
            yield return new DiffRow("removed", r.DisplayNameBefore ?? r.DisplayNameAfter ?? "(unknown)", r.SourceKind, r.Scope,
                r.TargetPathsBefore is null ? "" : "was: " + string.Join("; ", r.TargetPathsBefore));
        foreach (var r in diff.Changed)
            yield return new DiffRow("changed", r.DisplayNameAfter ?? r.DisplayNameBefore ?? "(unknown)", r.SourceKind, r.Scope,
                "reasons: " + string.Join(", ", r.Reasons.DefaultIfEmpty("(unspecified)")));
    }
}
