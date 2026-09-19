using AutostartAudit.Core.Export;
using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Tests;

public class ReportExporterTests
{
    private static ScanDocument FixtureWithProfilePaths() => new()
    {
        Entries = new[]
        {
            new AutoStartEntry
            {
                SourceKind = "run-key",
                Scope = "user",
                StableKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Upd",
                NativeKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Upd",
                DisplayName = "Updater",
                TargetPaths = new[] { @"C:\Users\Alice\AppData\Local\Foo\updater.exe --quiet" },
                RawValueSnapshot = @"""C:\Users\Alice\AppData\Local\Foo\updater.exe"" --quiet",
                Signing = SigningStatus.Signed,
                SignerSubject = "Foo Corp",
                SigningAggregation = SigningAggregation.Single,
                TargetSignatures = new[]
                {
                    new TargetSignature
                    {
                        Path = @"C:\Users\Alice\AppData\Local\Foo\updater.exe",
                        Status = SigningStatus.Signed,
                        Publisher = "Foo Corp",
                    },
                },
                Observation = ObservationHealth.Ok,
            },
            new AutoStartEntry
            {
                SourceKind = "startup-folder",
                Scope = "user",
                StableKey = @"C:\Users\Bob\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\StartUp\Sync.lnk",
                NativeKey = @"C:\Users\Bob\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\StartUp\Sync.lnk",
                DisplayName = "Sync agent",
                TargetPaths = new[] { @"C:\Users\Bob\Tools\sync.exe" },
                RawValueSnapshot = @"C:\Users\Bob\Tools\sync.exe",
                Signing = SigningStatus.Unverified,
                Observation = ObservationHealth.Ok,
            },
        },
        Sources = new[]
        {
            new SourceReport { SourceKind = "run-key", Capability = SourceCapability.Scanned },
            new SourceReport
            {
                SourceKind = "startup-folder",
                Capability = SourceCapability.Partial,
                Detail = @"folder C:\Users\Bob\Start Menu unread",
                Observations = new[]
                {
                    new SourceObservation
                    {
                        Id = @"C:\Users\Bob\Start Menu",
                        Health = ObservationHealth.Denied,
                        Detail = "access denied",
                    },
                },
            },
        },
        ScanComplete = false,
    };

    [Theory]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.Markdown)]
    public void RedactedExports_RemoveEveryUserProfilePath(ExportFormat format)
    {
        var text = ReportExporter.Render(FixtureWithProfilePaths(), format, redactUserProfiles: true);

        // Doctrine test: no C:\Users\<name> identity survives redaction, in
        // either raw or JSON-escaped form, in either format.
        Assert.DoesNotContain(@"C:\Users\", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\\Users\\", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alice", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bob", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PathRedaction.Marker, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.Markdown)]
    public void UnredactedExports_KeepRawPaths(ExportFormat format)
    {
        var text = ReportExporter.Render(FixtureWithProfilePaths(), format, redactUserProfiles: false);
        // In JSON the separators are escaped, so match on the profile name
        // itself, which is present un-redacted in both formats.
        Assert.Contains("Alice", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bob", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonExport_RetainsAllEvidenceOutsidePaths()
    {
        var text = ReportExporter.Render(FixtureWithProfilePaths(), ExportFormat.Json, redactUserProfiles: true);
        Assert.Contains("signed", text, StringComparison.Ordinal);
        Assert.Contains("Foo Corp", text, StringComparison.Ordinal);
        Assert.Contains("unverified", text, StringComparison.Ordinal);
        Assert.Contains("local-only Authenticode", text, StringComparison.Ordinal);
        Assert.Contains("scanComplete\":false", text.Replace(" ", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownExport_DisclosesPolicyAndCompletenessAndKeepsNonColorStatusText()
    {
        var text = ReportExporter.Render(FixtureWithProfilePaths(), ExportFormat.Markdown, redactUserProfiles: true);
        Assert.Contains("Signature policy", text, StringComparison.Ordinal);
        Assert.Contains("Scan complete: no", text, StringComparison.Ordinal);
        // Status conveyed as words, never color codes.
        Assert.Contains("| signed |", text, StringComparison.Ordinal);
        Assert.Contains("| unverified |", text, StringComparison.Ordinal);
        Assert.Contains("denied", text, StringComparison.Ordinal);
        Assert.DoesNotContain("#FF", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownExport_EmptySourceListIsNotPresentedAsClean()
    {
        var text = ReportExporter.ToMarkdown(ScanDocument.Empty);
        Assert.Contains("Scan complete: no", text, StringComparison.Ordinal);
        Assert.Contains("not a clean result", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_LeavesNonProfilePathsAlone()
    {
        const string input = @"C:\Program Files\Foo\updater.exe \\server\share\x.exe D:\Tools\tool.exe";
        Assert.Equal(input, PathRedaction.Redact(input));
    }

    [Fact]
    public void Redaction_HandlesVerbatimAndUncProfileForms()
    {
        Assert.DoesNotContain(@"Alice",
            PathRedaction.Redact(@"\\?\C:\Users\Alice\x.exe"), StringComparison.Ordinal);
        Assert.DoesNotContain(@"Alice",
            PathRedaction.Redact(@"\\HOST\C$\Users\Alice\x.exe"), StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_IsConservative_WhenHomeDriveVariantAppears()
    {
        // Documents and Settings (legacy profile root) is also redacted.
        Assert.DoesNotContain("Alice",
            PathRedaction.Redact(@"C:\Documents and Settings\Alice\Start Menu"), StringComparison.Ordinal);
    }
}
