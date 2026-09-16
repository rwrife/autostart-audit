using AutostartAudit.Core.Model;
using AutostartAudit.Core.Signatures;
using System.Text.Json;

namespace AutostartAudit.Core.Tests;

public sealed class SignatureEnrichmentTests
{
    [Fact]
    public void SingleTarget_EnrichesEntryAndTarget_WithPublisher()
    {
        var entry = Entry(@"C:\a.exe");
        var enriched = SignatureEnricher.Enrich(entry, new StubVerifier(
            new SignatureVerification(SigningStatus.Signed, "CN=A")));

        Assert.Equal(SigningStatus.Signed, enriched.Signing);
        Assert.Equal("CN=A", enriched.SignerSubject);
        Assert.Equal(SigningAggregation.Single, enriched.SigningAggregation);
        var target = Assert.Single(enriched.TargetSignatures);
        Assert.Equal(@"C:\a.exe", target.Path);
        Assert.Equal(SigningStatus.Signed, target.Status);
        Assert.Equal("CN=A", target.Publisher);
    }

    [Fact]
    public void MultipleDifferentResults_AreExplicitlyMixed_NotMisleadinglyAggregated()
    {
        var entry = Entry(@"C:\a.exe", @"C:\b.exe");
        var verifier = new StubVerifier(
            new(SigningStatus.Signed, "CN=A"),
            new(SigningStatus.Unsigned, null));

        var enriched = SignatureEnricher.Enrich(entry, verifier);

        Assert.Equal(SigningAggregation.Mixed, enriched.SigningAggregation);
        Assert.Equal(SigningStatus.Unverified, enriched.Signing);
        Assert.Null(enriched.SignerSubject);
        Assert.Equal(new[] { SigningStatus.Signed, SigningStatus.Unsigned }, enriched.TargetSignatures.Select(x => x.Status));
    }

    [Fact]
    public void MultipleIdenticalResults_AreUniform()
    {
        var enriched = SignatureEnricher.Enrich(Entry("a", "b"), new StubVerifier(
            new(SigningStatus.InvalidSignature, null), new(SigningStatus.InvalidSignature, null)));

        Assert.Equal(SigningAggregation.Uniform, enriched.SigningAggregation);
        Assert.Equal(SigningStatus.InvalidSignature, enriched.Signing);
        Assert.Null(enriched.SignerSubject);
    }

    [Fact]
    public void SignedTargetsWithDifferentPublishers_AreMixed()
    {
        var enriched = SignatureEnricher.Enrich(Entry("a", "b"), new StubVerifier(
            new(SigningStatus.Signed, "CN=A"), new(SigningStatus.Signed, "CN=B")));

        Assert.Equal(SigningAggregation.Mixed, enriched.SigningAggregation);
        Assert.Equal(SigningStatus.Unverified, enriched.Signing);
        Assert.Null(enriched.SignerSubject);
    }

    [Fact]
    public void NoTarget_IsExplicitAndDoesNotCallVerifier()
    {
        var verifier = new StubVerifier(new SignatureVerification(SigningStatus.Signed, "unused"));
        var enriched = SignatureEnricher.Enrich(Entry(), verifier);

        Assert.Equal(SigningAggregation.NoTarget, enriched.SigningAggregation);
        Assert.Equal(SigningStatus.Unverified, enriched.Signing);
        Assert.Empty(enriched.TargetSignatures);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public void NonSignedModelValues_CannotExposeAStalePublisher()
    {
        var entry = Entry("a") with
        {
            Signing = SigningStatus.InvalidSignature,
            SignerSubject = "must not leak",
            TargetSignatures = new[]
            {
                new TargetSignature
                {
                    Path = "a", Status = SigningStatus.Unsigned, Publisher = "must not leak",
                },
            },
        };

        Assert.Null(entry.SignerSubject);
        Assert.Null(entry.TargetSignatures[0].Publisher);
        Assert.DoesNotContain("must not leak", JsonSerializer.Serialize(entry, JsonOptions.Default));
    }

    private static AutoStartEntry Entry(params string[] targets) => new()
    {
        SourceKind = "test", Scope = "user", StableKey = "test|user|key", DisplayName = "entry",
        TargetPaths = targets, RawValueSnapshot = "raw", Signing = SigningStatus.Unverified,
        Observation = ObservationHealth.Ok,
    };

    private sealed class StubVerifier(params SignatureVerification[] results) : ISignatureVerifier
    {
        private readonly Queue<SignatureVerification> _results = new(results);
        public int Calls { get; private set; }
        public SignatureVerification Verify(string path)
        {
            Calls++;
            return _results.Dequeue();
        }
    }
}
