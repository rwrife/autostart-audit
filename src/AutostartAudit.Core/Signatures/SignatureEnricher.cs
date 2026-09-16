using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Signatures;

public static class SignatureEnricher
{
    public static AutoStartEntry Enrich(AutoStartEntry entry, ISignatureVerifier verifier)
    {
        var targets = entry.TargetPaths.Select(path => ToTarget(path, verifier.Verify(path))).ToList();
        if (targets.Count == 0)
            return entry with
            {
                Signing = SigningStatus.Unverified,
                SignerSubject = null,
                SigningAggregation = SigningAggregation.NoTarget,
                TargetSignatures = targets,
            };

        if (targets.Count == 1)
            return entry with
            {
                Signing = targets[0].Status,
                SignerSubject = targets[0].Publisher,
                SigningAggregation = SigningAggregation.Single,
                TargetSignatures = targets,
            };

        var first = targets[0];
        var uniform = targets.All(target => target.Status == first.Status
            && StringComparer.Ordinal.Equals(target.Publisher, first.Publisher));
        return entry with
        {
            Signing = uniform ? first.Status : SigningStatus.Unverified,
            SignerSubject = uniform ? first.Publisher : null,
            SigningAggregation = uniform ? SigningAggregation.Uniform : SigningAggregation.Mixed,
            TargetSignatures = targets,
        };
    }

    private static TargetSignature ToTarget(string path, SignatureVerification result)
    {
        var status = result.Status == SigningStatus.Signed && string.IsNullOrWhiteSpace(result.Publisher)
            ? SigningStatus.Unverified
            : result.Status;
        return new TargetSignature
        {
            Path = path,
            Status = status,
            Publisher = status == SigningStatus.Signed ? result.Publisher : null,
            Detail = status == SigningStatus.Unverified && result.Status == SigningStatus.Signed
                ? "signature verifier returned no signer subject"
                : result.Detail,
        };
    }
}
