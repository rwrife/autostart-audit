using System.Text.Json;
using AutostartAudit.Core;
using AutostartAudit.Core.Model;
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
        Func<string?, SnapshotStore> storeFactory)
    {
        if (args.Count == 1 && (args[0] is "--help" or "-h"))
        {
            PrintUsage(output);
            return 0;
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
    }
}
