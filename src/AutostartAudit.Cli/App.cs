using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Cli;

/// <summary>
/// CLI entry logic, separated from <c>Program</c> so tests can drive it and
/// capture output without spawning a process.
/// </summary>
public static class App
{
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (args.Count == 1 && (args[0] is "--help" or "-h"))
        {
            PrintUsage(output);
            return 0;
        }

        if (args.Count >= 1 && args[0] == "scan")
        {
            var rest = args.Skip(1).ToList();
            if (rest.Count == 0)
            {
                var doc = ScanEngine.ScanAll();
                output.WriteLine(doc.ToTextSummary());
                return 0;
            }

            if (rest.Count == 1 && rest[0] == "--json")
            {
                var doc = ScanEngine.ScanAll();
                output.WriteLine(doc.ToJson());
                return 0;
            }

            error.WriteLine("error: unsupported arguments for 'scan' (expected: --json or none).");
            PrintUsage(error);
            return 2;
        }

        error.WriteLine(args.Count == 0 ? "error: no command given." : $"error: unknown command '{args[0]}'.");
        PrintUsage(error);
        return 2;
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("usage: autostart-audit <command> [options]");
        w.WriteLine();
        w.WriteLine("commands:");
        w.WriteLine("  scan           Scan auto-start sources and print a summary");
        w.WriteLine("  scan --json    Print the scan document as JSON");
    }
}
