using System.Text;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// Canonical identity normalization. Identity across scans and snapshots is
/// (SourceKind, Scope, StableKey) — never display names, which are unstable
/// and not unique. Keys are lower-cased, trimmed, with internal whitespace
/// collapsed, so cosmetic differences do not create phantom diff entries.
/// </summary>
public static class StableKey
{
    /// <summary>Builds the normalized stable key triple-prefix for an entry.</summary>
    public static string Build(string sourceKind, string scope, string rawId) =>
        Normalize($"{sourceKind}|{scope}|{rawId}");

    /// <summary>
    /// Builds an identity from the source key and normalized target paths.
    /// Friendly/display names are deliberately excluded.
    /// </summary>
    public static string BuildWithTargets(
        string sourceKind,
        string scope,
        string rawId,
        IEnumerable<string> targetPaths)
    {
        var paths = targetPaths
            .Select(Normalize)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal);
        return Build(sourceKind, scope, $"{rawId}|{string.Join("|", paths)}");
    }

    /// <summary>Lower-cases, trims, and collapses runs of whitespace.</summary>
    public static string Normalize(string value)
    {
        var s = value.Trim().ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && sb.Length > 0)
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }

        return sb.ToString();
    }
}
