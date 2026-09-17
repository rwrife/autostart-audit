using System.Text;
using System.Text.Json;

namespace AutostartAudit.Core.Snapshot;

/// <summary>
/// Classification of two scan snapshots: Added / Removed / Changed / Unchanged.
/// The lists are ordered deterministically by identity key so exports and
/// golden-file tests are stable.
/// </summary>
public sealed record ScanDiff
{
    public static ScanDiff Empty { get; } = new()
    {
        Added = Array.Empty<DiffRecord>(),
        Removed = Array.Empty<DiffRecord>(),
        Changed = Array.Empty<DiffRecord>(),
        Unchanged = Array.Empty<DiffRecord>(),
    };

    public required IReadOnlyList<DiffRecord> Added { get; init; }
    public required IReadOnlyList<DiffRecord> Removed { get; init; }
    public required IReadOnlyList<DiffRecord> Changed { get; init; }
    public required IReadOnlyList<DiffRecord> Unchanged { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions.Default);

    public string ToTextSummary()
    {
        var sb = new StringBuilder();
        sb.Append($"snapshot diff: {Added.Count} added, {Removed.Count} removed, "
            + $"{Changed.Count} changed, {Unchanged.Count} unchanged");
        AppendBucket(sb, "added", Added, after: true);
        AppendBucket(sb, "removed", Removed, after: false);
        AppendBucket(sb, "changed", Changed, after: true);
        AppendBucket(sb, "unchanged", Unchanged, after: true);
        return sb.ToString();
    }

    private static void AppendBucket(StringBuilder sb, string label, IReadOnlyList<DiffRecord> records, bool after)
    {
        foreach (var r in records)
        {
            sb.AppendLine();
            var name = after ? r.DisplayNameAfter ?? r.DisplayNameBefore : r.DisplayNameBefore ?? r.DisplayNameAfter;
            sb.Append($"{label}: {name} [{r.SourceKind}/{r.Scope}]");
            if (r.Reasons.Count > 0)
                sb.Append($" reasons: {string.Join(", ", r.Reasons)}");
            if (label == "changed")
            {
                if (r.TargetPathsBefore is not null && r.TargetPathsAfter is not null
                    && !r.TargetPathsBefore.SequenceEqual(r.TargetPathsAfter, StringComparer.Ordinal))
                    sb.Append($" | paths: {string.Join(", ", r.TargetPathsBefore)} -> {string.Join(", ", r.TargetPathsAfter)}");
                if (r.SigningBefore is not null && r.SigningAfter is not null && r.SigningBefore != r.SigningAfter)
                    sb.Append($" | signing: {r.SigningBefore} -> {r.SigningAfter}");
                if (r.EnabledBefore is not null && r.EnabledAfter is not null && r.EnabledBefore != r.EnabledAfter)
                    sb.Append($" | enabled: {r.EnabledBefore} -> {r.EnabledAfter}");
                if (!string.Equals(r.DisplayNameBefore, r.DisplayNameAfter, StringComparison.Ordinal))
                    sb.Append($" | display: {r.DisplayNameBefore} -> {r.DisplayNameAfter}");
            }
        }
    }
}
