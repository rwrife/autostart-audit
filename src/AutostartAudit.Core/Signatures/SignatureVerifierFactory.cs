namespace AutostartAudit.Core.Signatures;

public static class SignatureVerifierFactory
{
    public static ISignatureVerifier CreateForCurrentOS() => OperatingSystem.IsWindows()
        ? new AuthenticodeSignatureVerifier(new WindowsSignatureFileAccess(), new WindowsWinVerifyTrust())
        : new UnavailableSignatureVerifier("Authenticode verification requires Windows");

    private sealed class UnavailableSignatureVerifier(string detail) : ISignatureVerifier
    {
        public SignatureVerification Verify(string path) =>
            new(Model.SigningStatus.Unverified, null, detail);
    }
}
