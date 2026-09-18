using System.Text.Json;
using AutostartAudit.Core;
using AutostartAudit.Core.Model;
using AutostartAudit.Core.Quarantine;
using AutostartAudit.Core.Scan;
using AutostartAudit.Core.Snapshot;
using AutostartAudit.Core.Store;

namespace AutostartAudit.Cli;

/// <summary>
/// CLI entry logic, separated from <c>Program</c> so tests can drive it and
/// capture output without spawning a process. The scan delegate and snapshot
/// store factory are injectable so tests never depend on the host machine's
/// real autostart state or the user profile directory.
/// </summary>
public static class App
{
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) =>
        Run(args, output, error, ScanEngine.ScanAll);

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, Func<ScanDocument> scan) =>
        Run(args, output, error, scan, path => new SnapshotStore(path));

    public static int Run(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        Func<ScanDocument> scan,
        Func<string?, SnapshotStore> storeFactory) =>
        Run(args, output, error, scan, storeFactory, path => new ChangeJournal(path));

    public static int Run(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        Func<ScanDocument> scan,
        Func<string?, SnapshotStore> storeFactory,
        Func<string?, ChangeJournal> journalFactory)
    {
        if (args.Count == 1 && (args[0] is "--help" or "-h"))
        {
            PrintUsage(output);
            return 0;
        }

        // Elevated-child entry point: re-launches perform exactly one
        // already-journaled record. Exit 0 only on a verified durable result.
        if (args.Count >= 3 && args[0] == "run-record" && long.TryParse(args[1], out var recordId)
            && args[2] is "quarantine" or "restore")
        {
            string? journalPath = null;
            for (var i = 3; i < args.Count; i++)
                if (args[i] == "--journal" && i + 1 < args.Count)
                    journalPath = args[++i];
            using var childJournal = journalFactory(journalPath);
            var child = new QuarantineCoordinator(childJournal, DefaultQuarantine.StrategiesForCurrentOS());
            try
            {
                if (args[2] == "quarantine")
                {
                    var outcome = child.RunQuarantineRecord(recordId);
                    return outcome.Status == QuarantineStatus.Quarantined ? 0 : 1;
                }
                var restore = child.RunRestoreRecord(recordId);
                return restore.Status == RestoreStatus.Restored ? 0 : 1;
            }
            catch (Exception ex)
            {
                error.WriteLine($"error: run-record {recordId} {args[2]}: {ex.Message}");
                return 1;
            }
        }

        if (args.Count >= 1 && args[0] == "journal")
        {
            string? journalPath = null;
            for (var i = 1; i < args.Count; i++)
                if (args[i] == "--journal" && i + 1 < args.Count)
                    journalPath = args[++i];
            using var journal = journalFactory(journalPath);
            foreach (var record in journal.ListAll())
            {
                output.WriteLine($"#{record.Id} {record.CreatedAtUtc:O} {record.SourceKind}/{record.Scope} "
                    + $"'{record.DisplayName}' result={record.Result} restore={record.RestoreStatus}"
                    + (record.ErrorDetail is null ? "" : $" error={record.ErrorDetail}")
                    + (record.RestoreErrorDetail is null ? "" : $" restore-error={record.RestoreErrorDetail}"));
            }
            return 0;
        }

        if (args.Count >= 2 && args[0] == "restore" && long.TryParse(args[1], out var restoreId))
        {
            string? journalPath = null;
            for (var i = 2; i < args.Count; i++)
                if (args[i] == "--journal" && i + 1 < args.Count)
                    journalPath = args[++i];
            using var journal = journalFactory(journalPath);
            var coordinator = DefaultQuarantine.CoordinatorForCurrentOS(journal);
            try
            {
                var outcome = coordinator.Restore(restoreId);
                output.WriteLine($"restore #{restoreId}: {outcome.Status}"
                    + (outcome.Detail is null ? "" : $" — {outcome.Detail}"));
                return outcome.Status == RestoreStatus.Restored ? 0 : 1;
            }
            catch (Exception ex)
            {
                error.WriteLine($"error: restore {restoreId}: {ex.Message}");
                return 1;
            }
        }

        if (args.Count >= 2 && args[0] == "quarantine" && !string.IsNullOrWhiteSpace(args[1]))
        {
            string? journalPath = null;
            string match = args[1];
            for (var i = 2; i < args.Count; i++)
                if (args[i] == "--journal" && i + 1 < args.Count)
                    journalPath = args[++i];
            using var journal = journalFactory(journalPath);
            var coordinator = DefaultQuarantine.CoordinatorForCurrentOS(journal);

            // Match by identity (normalized native key), never by display
            // name alone — display names are not identities.
            var document = scan();
            var normalizedMatch = AutostartAudit.Core.Scan.StableKey.Normalize(match);
            var candidates = document.Entries
                .Where(entry => (entry.NativeKey ?? entry.StableKey).Contains(normalizedMatch, StringComparison.Ordinal))
                .ToList();
            if (candidates.Count == 0)
            {
                error.WriteLine($"error: no scan entry matches identity '{match}'.");
                return 2;
            }
            if (candidates.Count > 1)
            {
                error.WriteLine($"error: identity '{match}' is ambiguous across {candidates.Count} entries; "
                    + "use the entry's full native key.");
                return 2;
            }

            var outcome = coordinator.Quarantine(candidates[0]);
            output.WriteLine($"quarantine: {outcome.Status}"
                + (outcome.RecordId is null ? "" : $" (journal #{outcome.RecordId})")
                + (outcome.Detail is null ? "" : $" — {outcome.Detail}"));
            return outcome.Status == QuarantineStatus.Quarantined ? 0 : 1;
        }

        if (args.Count >= 1 && args[0] == "scan")
        {
            var rest = args.Skip(1).ToList();
            bool save = false;
            bool json = false;
            string? storePath = null;
            for (var i = 0; i < rest.Count; i++)
            {
                switch (rest[i])
                {
                    case "--json":
                        json = true;
                        break;
                    case "--save":
                        save = true;
                        break;
                    case "--store" when i + 1 < rest.Count:
                        storePath = rest[++i];
                        break;
                    default:
                        error.WriteLine($"error: unsupported arguments for 'scan' ({rest[i]}).");
                        PrintUsage(error);
                        return 2;
                }
            }

            if (!save)
            {
                var doc = scan();
                output.WriteLine(json ? doc.ToJson() : doc.ToTextSummary());
                return 0;
            }

            var document = scan();
            using var store = storeFactory(storePath);
            var outcome = SnapshotCapture.CaptureAndCompare(store, document);
            if (json)
            {
                output.WriteLine(JsonSerializer.Serialize(new CaptureResult
                {
                    SnapshotId = outcome.SnapshotId,
                    PreviousSnapshotId = outcome.PreviousSnapshotId,
                    IsBaseline = outcome.IsBaseline,
                    ScanComplete = document.ScanComplete,
                    Diff = outcome.Diff,
                }, JsonOptions.Default));
            }
            else
            {
                if (outcome.IsBaseline)
                {
                    output.WriteLine($"baseline snapshot #{outcome.SnapshotId} established "
                        + $"({document.Entries.Count} entries). No prior snapshot exists, so no changes "
                        + "are reported — this is not evidence of an unchanged machine, just a first capture.");
                }
                else
                {
                    output.WriteLine($"snapshot #{outcome.SnapshotId} saved (previous #{outcome.PreviousSnapshotId}).");
                    output.WriteLine(outcome.Diff!.ToTextSummary());
                }
            }

            return 0;
        }

        error.WriteLine(args.Count == 0 ? "error: no command given." : $"error: unknown command '{args[0]}'.");
        PrintUsage(error);
        return 2;
    }

    /// <summary>JSON envelope for a capture-and-compare run.</summary>
    public sealed record CaptureResult
    {
        public required long SnapshotId { get; init; }
        public long? PreviousSnapshotId { get; init; }
        public required bool IsBaseline { get; init; }
        public required bool ScanComplete { get; init; }
        public ScanDiff? Diff { get; init; }
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("usage: autostart-audit <command> [options]");
        w.WriteLine();
        w.WriteLine("commands:");
        w.WriteLine("  scan                    Scan auto-start sources and print a summary");
        w.WriteLine("  scan --json             Print the scan document as JSON");
        w.WriteLine("  scan --save             Scan, store a snapshot, and print the diff vs the previous one");
        w.WriteLine("  scan --save --json      Same, as a JSON capture envelope");
        w.WriteLine("  scan --save --store P   Use snapshot database at path P (default: %LOCALAPPDATA%\\autostart-audit)");
        w.WriteLine("  journal                 List the durable change journal (quarantine/restore history)");
        w.WriteLine("  quarantine <native-key> Journal-first disable of one entry matched by native key");
        w.WriteLine("  restore <id>            Exact inverse of journal record <id> (elevates for machine scope)");
        w.WriteLine("  --journal P             Journal database path for journal/restore/run-record");
    }
}
