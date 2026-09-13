using System.Text;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// Extracts target executable/script paths from a raw auto-start value.
/// Best-effort by design: a raw-value snapshot is always kept verbatim in the
/// entry, so a mis-parse here never loses evidence. Signature: a value that
/// cannot yield any path returns an empty list (not a fabricated path).
/// </summary>
public static class TargetPathParser
{
    public static IReadOnlyList<string> Parse(string rawValue)
    {
        var s = rawValue.Trim();
        if (s.Length == 0)
            return Array.Empty<string>();

        if (s[0] == '"')
        {
            // Scan to the closing quote, treating "" as an escaped literal
            // quote inside the path (Windows quoting convention).
            var sb = new StringBuilder();
            int i = 1;
            while (i < s.Length)
            {
                if (s[i] == '"')
                {
                    if (i + 1 < s.Length && s[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                        continue;
                    }

                    return sb.Length == 0 ? Array.Empty<string>() : new[] { sb.ToString() };
                }

                sb.Append(s[i]);
                i++;
            }

            // Unterminated quote: the quoted fragment runs to the end.
            return sb.Length == 0 ? Array.Empty<string>() : new[] { sb.ToString() };
        }

        // Unquoted: take tokens until one ends in ".exe" (the conventional
        // Windows boundary); otherwise the first token is the best candidate.
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return Array.Empty<string>();

        foreach (var t in tokens)
        {
            if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return new[] { t };
        }

        return new[] { tokens[0] };
    }
}
