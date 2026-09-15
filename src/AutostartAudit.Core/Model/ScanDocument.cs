using System.Text.Json;

namespace AutostartAudit.Core.Model;

/// <summary>
/// The complete result of one scan run. Serialization of <see cref="Empty"/>
/// is exactly <c>{"entries":[],"sources":[]}</c>.
/// </summary>
public sealed record ScanDocument
{
    /// <summary>Every entry observed, across all sources.</summary>
    public required IReadOnlyList<AutoStartEntry> Entries { get; init; }

    /// <summary>One report per source consulted, including unreadable ones.</summary>
    public required IReadOnlyList<SourceReport> Sources { get; init; }

    /// <summary>
    /// Overall scan completeness: true only when at least one source ran and
    /// every source report is <see cref="SourceCapability.Scanned"/>. A false
    /// flag means the inventory may be incomplete — it is never presented as
    /// evidence of a clean machine.
    /// </summary>
    public required bool ScanComplete { get; init; }

    /// <summary>
    /// A document with nothing observed. Only legitimately produced by the
    /// engine when no sources have been wired yet; a real scan must instead
    /// emit one <see cref="SourceReport"/> per source with its honest
    /// capability, so "empty" never silently means "clean".
    /// </summary>
    public static ScanDocument Empty { get; } = new()
    {
        Entries = Array.Empty<AutoStartEntry>(),
        Sources = Array.Empty<SourceReport>(),
        ScanComplete = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions.Default);

    public string ToTextSummary()
    {
        var summary = new System.Text.StringBuilder(
            $"autostart-audit scan: {Entries.Count} entr{(Entries.Count == 1 ? "y" : "ies")}, "
            + $"{Sources.Count} source{(Sources.Count == 1 ? "" : "s")} reported, "
            + $"scan-complete: {(ScanComplete ? "yes" : "no")}");
        var unread = Sources.Where(source => source.Capability != SourceCapability.Scanned).ToList();
        if (unread.Count > 0)
        {
            summary.AppendLine();
            summary.Append("unread sources: ");
            summary.Append(string.Join(", ", unread.Select(source => source.SourceKind)));
            foreach (var source in unread)
            {
                summary.AppendLine();
                summary.Append($"  {source.SourceKind} [{source.Capability.ToString().ToLowerInvariant()}]");
                if (!string.IsNullOrWhiteSpace(source.Detail))
                    summary.Append($": {source.Detail}");
                foreach (var observation in source.Observations.Where(o => o.Health != ObservationHealth.Ok))
                {
                    summary.AppendLine();
                    summary.Append($"    {observation.Id} [{observation.Health.ToString().ToLowerInvariant()}]");
                    if (!string.IsNullOrWhiteSpace(observation.Detail))
                        summary.Append($": {observation.Detail}");
                }
            }
        }
        return summary.ToString();
    }
}
