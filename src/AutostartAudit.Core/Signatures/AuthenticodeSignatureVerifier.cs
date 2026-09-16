using System.Collections.Concurrent;
using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Signatures;

public sealed class AuthenticodeSignatureVerifier : ISignatureVerifier
{
    internal const uint ErrorSuccess = 0;
    internal const uint TrustENoSignature = 0x800B0100;
    private readonly ISignatureFileAccess _files;
    private readonly IWinVerifyTrust _trust;
    private readonly ConcurrentDictionary<CacheKey, SignatureVerification> _cache = new();

    public AuthenticodeSignatureVerifier(ISignatureFileAccess files, IWinVerifyTrust trust)
    {
        _files = files;
        _trust = trust;
    }

    public SignatureVerification Verify(string path)
    {
        SignatureFileOpenResult opened;
        try
        {
            opened = _files.OpenLocal(path);
        }
        catch (Exception ex)
        {
            return Unverified($"file preflight failed: {ex.GetType().Name}");
        }

        if (opened.File is null)
            return Unverified(opened.Detail ?? opened.Failure?.ToString() ?? "file unavailable");

        using var file = opened.File;
        var key = new CacheKey(file.NormalizedPath, file.Size, file.LastWriteTimeUtcTicks, file.ContentHash,
            file.VolumeSerialNumber, file.FileIndex);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        SignatureVerification result;
        try
        {
            result = Map(_trust.Verify(file));
        }
        catch (Exception ex)
        {
            result = Unverified($"WinVerifyTrust failed: {ex.GetType().Name}");
        }

        _cache.TryAdd(key, result);
        return result;
    }

    internal static SignatureVerification Map(WinTrustResult result)
    {
        if (result.ResultCode == ErrorSuccess)
        {
            if (string.IsNullOrWhiteSpace(result.SignerSubject))
                return Unverified("trust succeeded but signer subject was unavailable");
            return new SignatureVerification(SigningStatus.Signed, result.SignerSubject);
        }

        if (result.ResultCode == TrustENoSignature)
            return new SignatureVerification(SigningStatus.Unsigned, null);

        if (InvalidSignatureCodes.Contains(result.ResultCode))
            return new SignatureVerification(SigningStatus.InvalidSignature, null,
                $"WinVerifyTrust result 0x{result.ResultCode:X8}");

        // Unknown/provider/subject-form/file errors are not proof of no signature.
        return Unverified($"unrecognized WinVerifyTrust result 0x{result.ResultCode:X8}");
    }

    private static SignatureVerification Unverified(string detail) =>
        new(SigningStatus.Unverified, null, detail);

    private static readonly HashSet<uint> InvalidSignatureCodes =
    [
        0x80096004, // TRUST_E_CERT_SIGNATURE
        0x80096010, // TRUST_E_BAD_DIGEST
        0x800B0101, // CERT_E_EXPIRED
        0x800B0102, // CERT_E_VALIDITYPERIODNESTING
        0x800B0103, // CERT_E_ROLE
        0x800B0104, // CERT_E_PATHLENCONST
        0x800B0105, // CERT_E_CRITICAL
        0x800B0106, // CERT_E_PURPOSE
        0x800B0107, // CERT_E_ISSUERCHAINING
        0x800B0108, // CERT_E_MALFORMED
        0x800B0109, // CERT_E_UNTRUSTEDROOT
        0x800B010A, // CERT_E_CHAINING
        0x800B010C, // CERT_E_REVOKED
        0x800B010D, // CERT_E_UNTRUSTEDTESTROOT
        0x800B0110, // CERT_E_WRONG_USAGE
        0x800B0111, // TRUST_E_EXPLICIT_DISTRUST
        0x800B0114, // CERT_E_CN_NO_MATCH
    ];

    private sealed record CacheKey(string Path, long Size, long LastWriteTimeUtcTicks, string ContentHash,
        uint VolumeSerialNumber, ulong FileIndex)
    {
        public bool Equals(CacheKey? other) => other is not null
            && StringComparer.OrdinalIgnoreCase.Equals(Path, other.Path)
            && Size == other.Size
            && LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks
            && StringComparer.Ordinal.Equals(ContentHash, other.ContentHash)
            && VolumeSerialNumber == other.VolumeSerialNumber
            && FileIndex == other.FileIndex;

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Path), Size, LastWriteTimeUtcTicks,
            StringComparer.Ordinal.GetHashCode(ContentHash), VolumeSerialNumber, FileIndex);
    }
}
