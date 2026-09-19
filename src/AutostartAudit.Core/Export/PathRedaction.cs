using System.Text.RegularExpressions;

namespace AutostartAudit.Core.Export;

/// <summary>
/// User-profile path redaction for exported reports. This is applied to the
/// final serialized artifact (JSON or Markdown text) so every field a path
/// can hide in — targets, raw value snapshots, native keys, evidence,
/// details — is covered by one honest pass. The boundary is deliberately
/// conservative: whole <c>&lt;drive&gt;:\Users\&lt;profile&gt;</c> prefixes
/// (and their JSON-escaped <c>\\\\</c> variants, verbatim <c>\\?\</c> forms,
/// and forward-slash variants) are replaced with a fixed marker. Redaction
/// never rewrites statuses or removes entries; it only removes identity.
/// </summary>
public static partial class PathRedaction
{
    /// <summary>Replacement marker left in place of a user-profile prefix.</summary>
    public const string Marker = "[redacted-user-profile]";

    /// <summary>
    /// Matches an optional UNC/verbatim prefix plus a drive-letter Windows
    /// profile root (Users / Documents and Settings variants) followed by one
    /// profile-name segment. Separators may be single or doubled backslashes
    /// (JSON-escaped text) or forward slashes.
    /// </summary>
    [GeneratedRegex(
        @"(?:\\\\[^\\/:*?""<>|\r\n]+(?:\\\\|\\))*(?:\\\\\?\\)?[A-Za-z](?::|\$)(?:\\\\|\\|/)(?:Users|Documents and Settings|Dokumente und Einstellungen)(?:\\\\|\\|/)[^\\/:*?""<>|\r\n]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex UserProfilePrefix();

    /// <summary>
    /// Replaces every Windows user-profile path prefix found in
    /// <paramref name="text"/> (including the current process user's profile
    /// root, when resolvable) with <see cref="Marker"/>.
    /// </summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var redacted = UserProfilePrefix().Replace(text, Marker);

        var profileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profileRoot) && !string.Equals(profileRoot, "UserProfile", StringComparison.Ordinal))
            redacted = redacted.Replace(profileRoot, Marker, StringComparison.OrdinalIgnoreCase);

        return redacted;
    }
}
