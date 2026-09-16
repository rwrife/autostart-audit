using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;
using AutostartAudit.Core.Signatures;

namespace AutostartAudit.Core.Tests;

/// <summary>Programmable source for engine-level tests.</summary>
internal sealed class StubSource : IAutoStartSource
{
    private readonly Func<ScanContext, SourceScanResult> _scan;

    public StubSource(string kind, Func<ScanContext, SourceScanResult> scan)
    {
        SourceKind = kind;
        _scan = scan;
    }

    public string SourceKind { get; }

    public SourceScanResult Scan(ScanContext context) => _scan(context);
}

public class ScanEngineTests
{
    [Fact]
    public void SignatureEnrichment_PreservesIdentityObservationAndSourceCapability()
    {
        var original = new AutoStartEntry
        {
            SourceKind = "a", Scope = "machine", StableKey = "a|machine|stable", DisplayName = "Friendly",
            TargetPaths = new[] { @"C:\target.exe" }, RawValueSnapshot = "raw",
            Signing = SigningStatus.Unverified, Observation = ObservationHealth.Unknown,
        };
        var doc = ScanEngine.ScanAll(new IAutoStartSource[]
        {
            new StubSource("a", _ => new SourceScanResult
            {
                Entries = new[] { original },
                Observations = new[] { new SourceObservation { Id = "scope", Health = ObservationHealth.Ok } },
            }),
        }, signatureVerifier: new FixedSignatureVerifier());

        var entry = Assert.Single(doc.Entries);
        Assert.Equal(original.StableKey, entry.StableKey);
        Assert.Equal(original.Observation, entry.Observation);
        Assert.Equal(SourceCapability.Scanned, Assert.Single(doc.Sources).Capability);
        Assert.Equal(SigningStatus.Signed, entry.Signing);
        Assert.Equal("CN=Test", entry.SignerSubject);
    }

    [Fact]
    public void AllSourcesScanned_CompletenessTrue_EntriesAggregated()
    {
        var doc = ScanEngine.ScanAll(new IAutoStartSource[]
        {
            new StubSource("a", _ => new SourceScanResult
            {
                Observations = new[] { new SourceObservation { Id = "a1", Health = ObservationHealth.Ok } },
            }),
            new StubSource("b", _ => new SourceScanResult
            {
                Entries = new[]
                {
                    new AutoStartEntry
                    {
                        SourceKind = "b", Scope = "user", StableKey = "b|user|x", DisplayName = "X",
                        TargetPaths = Array.Empty<string>(), RawValueSnapshot = "x",
                        Signing = SigningStatus.Unverified, Observation = ObservationHealth.Ok,
                    },
                },
                Observations = new[] { new SourceObservation { Id = "b1", Health = ObservationHealth.Ok } },
            }),
        });

        Assert.True(doc.ScanComplete);
        Assert.Single(doc.Entries);
        Assert.Equal(new[] { "a", "b" }, doc.Sources.Select(s => s.SourceKind));
    }

    [Fact]
    public void PartialSource_CompletenessFalse_CapabilityPartial()
    {
        var doc = ScanEngine.ScanAll(new IAutoStartSource[]
        {
            new StubSource("a", _ => new SourceScanResult
            {
                Observations = new[]
                {
                    new SourceObservation { Id = "ok", Health = ObservationHealth.Ok },
                    new SourceObservation { Id = "denied", Health = ObservationHealth.Denied },
                },
            }),
        });

        Assert.Equal(SourceCapability.Partial, doc.Sources.Single().Capability);
        Assert.False(doc.ScanComplete);
    }

    [Fact]
    public void SourceWithNoObservationsAtAll_IsDenied_NotClean()
    {
        // Doctrine: attempting nothing is never "scanned clean".
        var doc = ScanEngine.ScanAll(new IAutoStartSource[]
        {
            new StubSource("a", _ => SourceScanResult.Nothing),
        });

        Assert.Equal(SourceCapability.Denied, doc.Sources.Single().Capability);
        Assert.False(doc.ScanComplete);
    }

    [Fact]
    public void SourceThatThrows_BecomesDeniedReport_ScanNeverCrashes()
    {
        var doc = ScanEngine.ScanAll(new IAutoStartSource[]
        {
            new StubSource("boom", _ => throw new InvalidOperationException("exploded")),
        });

        var report = doc.Sources.Single();
        Assert.Equal("boom", report.SourceKind);
        Assert.Equal(SourceCapability.Denied, report.Capability);
        Assert.Contains("exploded", report.Detail);
        Assert.False(doc.ScanComplete);
    }

    [Fact]
    public void ForcedUnsupported_IsHonouredVerbatim()
    {
        var doc = ScanEngine.ScanAll(new IAutoStartSource[]
        {
            new UnsupportedSource("run-key", "registry sources require Windows"),
        });

        var report = doc.Sources.Single();
        Assert.Equal(SourceCapability.Unsupported, report.Capability);
        Assert.Contains("Windows", report.Detail);
        Assert.False(doc.ScanComplete);
    }

    [Fact]
    public void DeniedObservations_RoundTripThroughJson_Visible()
    {
        var doc = ScanEngine.ScanAll(new IAutoStartSource[]
        {
            new StubSource("a", _ => new SourceScanResult
            {
                Observations = new[]
                {
                    new SourceObservation { Id = "k1", Health = ObservationHealth.Denied, Detail = "elevate" },
                },
            }),
        });

        var json = doc.ToJson();
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        var obs = parsed.RootElement.GetProperty("sources")[0].GetProperty("observations");
        Assert.Equal("denied", obs[0].GetProperty("health").GetString());
        Assert.Equal("elevate", obs[0].GetProperty("detail").GetString());
    }
}

internal sealed class FixedSignatureVerifier : ISignatureVerifier
{
    public SignatureVerification Verify(string path) => new(SigningStatus.Signed, "CN=Test");
}
