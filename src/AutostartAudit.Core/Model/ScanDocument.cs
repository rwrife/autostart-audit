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
    /// A document with nothing observed. Only legitimately produced by the
    /// engine when no sources have been wired yet; a real scan must instead
    /// emit one <see cref="SourceReport"/> per source with its honest
    /// capability, so "empty" never silently means "clean".
    /// </summary>
    public static ScanDocument Empty { get; } = new()
    {
        Entries = Array.Empty<AutoStartEntry>(),
        Sources = Array.Empty<SourceReport>(),
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions.Default);

    public string ToTextSummary() =>
        $"autostart-audit scan: {Entries.Count} entr{(Entries.Count == 1 ? "y" : "ies")}, "
        + $"{Sources.Count} source{(Sources.Count == 1 ? "" : "s")} reported";
}
