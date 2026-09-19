using System.Text;
using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Export;

/// <summary>Export formats offered by the report dialog.</summary>
public enum ExportFormat
{
    Json,
    Markdown,
}

/// <summary>
/// Renders a <see cref="ScanDocument"/> as JSON or Markdown for sharing.
/// Redaction of user-profile paths is applied at the text level over the
/// finished artifact so no field can leak a profile identity, and it is ON by
/// default in every caller (the UI checkbox defaults to checked).
/// The signature-policy disclosure is included in both formats — an export
/// without it would misrepresent the evidence boundary.
/// </summary>
public static class ReportExporter
{
    public static string Render(ScanDocument document, ExportFormat format, bool redactUserProfiles = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = format switch
        {
            ExportFormat.Json => document.ToJson(),
            ExportFormat.Markdown => ToMarkdown(document),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        return redactUserProfiles ? PathRedaction.Redact(text) : text;
    }

    public static string ToMarkdown(ScanDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Autostart Audit report");
        sb.AppendLine();
        sb.AppendLine($"- Generated (UTC): {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"- Entries observed: {document.Entries.Count}");
        sb.AppendLine($"- Scan complete: {(document.ScanComplete ? "yes" : "no")}");
        sb.AppendLine();
        sb.Append("> Signature policy: ").Append(document.SignatureVerificationPolicy).AppendLine();
        sb.AppendLine("> \"unverified\" is never \"unsigned\", and an incomplete scan is never a clean machine.");
        sb.AppendLine();

        sb.AppendLine("## Entries");
        sb.AppendLine();
        if (document.Entries.Count == 0)
        {
            sb.AppendLine("_No entries were observed. This is only evidence of emptiness when every source below reports `scanned`._");
        }
        else
        {
            sb.AppendLine("| Entry | Source | Scope | Target(s) | Signing | Publisher | Health |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var entry in document.Entries)
            {
                sb.Append("| ").Append(Escape(entry.DisplayName))
                  .Append(" | ").Append(Escape(entry.SourceKind))
                  .Append(" | ").Append(Escape(entry.Scope))
                  .Append(" | ").Append(Escape(string.Join("; ", entry.TargetPaths)))
                  .Append(" | ").Append(Escape(SigningText(entry)))
                  .Append(" | ").Append(Escape(entry.SignerSubject ?? "-"))
                  .Append(" | ").Append(Escape(HealthText(entry.Observation)))
                  .AppendLine(" |");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Sources");
        sb.AppendLine();
        sb.AppendLine("| Source | Capability | Detail | Non-ok observations |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var source in document.Sources)
        {
            var nonOk = source.Observations
                .Where(o => o.Health != ObservationHealth.Ok)
                .Select(o => $"{o.Id} [{HealthText(o.Health)}]" + (string.IsNullOrWhiteSpace(o.Detail) ? "" : $": {o.Detail}"));
            sb.Append("| ").Append(Escape(source.SourceKind))
              .Append(" | ").Append(Escape(CapabilityText(source.Capability)))
              .Append(" | ").Append(Escape(source.Detail ?? "-"))
              .Append(" | ").Append(Escape(string.Join("<br>", nonOk.DefaultIfEmpty("-"))))
              .AppendLine(" |");
        }
        if (document.Sources.Count == 0)
            sb.AppendLine("| (none) | unsupported | no sources were wired; this is not a clean result | - |");

        return sb.ToString();
    }

    /// <summary>Plain-language signing label. Text-only; never color-only.</summary>
    public static string SigningText(AutoStartEntry entry)
    {
        var baseText = entry.Signing switch
        {
            SigningStatus.Signed => "signed",
            SigningStatus.Unsigned => "unsigned",
            SigningStatus.InvalidSignature => "invalid signature",
            SigningStatus.Unverified => "unverified",
            _ => "unverified",
        };
        return entry.SigningAggregation == SigningAggregation.Mixed
            ? $"{baseText} (mixed targets)"
            : baseText;
    }

    public static string HealthText(ObservationHealth health) => health switch
    {
        ObservationHealth.Ok => "ok",
        ObservationHealth.Denied => "denied",
        ObservationHealth.Unknown => "unknown",
        _ => "unknown",
    };

    public static string CapabilityText(SourceCapability capability) => capability switch
    {
        SourceCapability.Scanned => "scanned",
        SourceCapability.Partial => "partial",
        SourceCapability.Denied => "denied",
        SourceCapability.Unsupported => "unsupported",
        _ => capability.ToString().ToLowerInvariant(),
    };

    private static string Escape(string text) =>
        text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
