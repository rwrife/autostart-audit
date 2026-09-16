using Microsoft.Win32.SafeHandles;
using AutostartAudit.Core.Model;
using AutostartAudit.Core.Signatures;

namespace AutostartAudit.Core.Tests;

public sealed class SignatureVerifierTests
{
    private const uint TrustENoSignature = 0x800B0100;
    private const uint TrustEBadDigest = 0x80096010;
    private const uint TrustEFail = 0x800B010B;
    private const uint TrustESubjectFormUnknown = 0x800B0003;

    [Theory]
    [InlineData(0u, SigningStatus.Signed)]
    [InlineData(TrustENoSignature, SigningStatus.Unsigned)]
    [InlineData(TrustEBadDigest, SigningStatus.InvalidSignature)]
    [InlineData(TrustEFail, SigningStatus.Unverified)]
    [InlineData(TrustESubjectFormUnknown, SigningStatus.Unverified)]
    [InlineData(0xDEADBEEFu, SigningStatus.Unverified)]
    public void TrustResults_MapConservatively_AndPublisherOnlySurvivesSigned(uint code, SigningStatus expected)
    {
        using var files = new FakeFileAccess(File("C:\\local\\app.exe", 10, 20));
        var native = new FakeWinVerifyTrust(code, "CN=Publisher");
        var result = new AuthenticodeSignatureVerifier(files, native).Verify(@"C:\local\app.exe");

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == SigningStatus.Signed ? "CN=Publisher" : null, result.Publisher);
    }

    [Theory]
    [InlineData(SignatureFileFailure.Missing)]
    [InlineData(SignatureFileFailure.Locked)]
    [InlineData(SignatureFileFailure.Remote)]
    [InlineData(SignatureFileFailure.Unreadable)]
    [InlineData(SignatureFileFailure.UnsafeReparsePoint)]
    public void FilesThatCannotBeLocallyObserved_AreUnverified_WithoutCallingTrust(SignatureFileFailure failure)
    {
        using var files = new FakeFileAccess(failure);
        var native = new FakeWinVerifyTrust(0, "must not run");

        var result = new AuthenticodeSignatureVerifier(files, native).Verify(@"C:\target.exe");

        Assert.Equal(SigningStatus.Unverified, result.Status);
        Assert.Null(result.Publisher);
        Assert.Equal(0, native.Calls);
    }

    [Fact]
    public void Cache_ReusesExactPathMetadataAndContentHash_AndInvalidatesEachComponent()
    {
        using var files = new FakeFileAccess(
            File(@"C:\a.exe", 10, 20),
            File(@"C:\a.exe", 10, 20),
            File(@"C:\a.exe", 11, 20),
            File(@"C:\a.exe", 11, 21),
            File(@"C:\b.exe", 11, 21));
        var native = new FakeWinVerifyTrust(0, "CN=Publisher");
        var verifier = new AuthenticodeSignatureVerifier(files, native);

        verifier.Verify("ignored");
        verifier.Verify("ignored");
        verifier.Verify("ignored");
        verifier.Verify("ignored");
        verifier.Verify("ignored");

        Assert.Equal(4, native.Calls);
    }

    [Fact]
    public void Cache_InvalidatesWhenContentHashChangesWithAllMetadataUnchanged()
    {
        using var files = new FakeFileAccess(
            File(@"C:\a.exe", 10, 20, fileIndex: 1, contentHash: "hash-a"),
            File(@"C:\a.exe", 10, 20, fileIndex: 1, contentHash: "hash-b"));
        var native = new FakeWinVerifyTrust(0, "CN=Publisher");
        var verifier = new AuthenticodeSignatureVerifier(files, native);

        verifier.Verify("ignored");
        verifier.Verify("ignored");

        Assert.Equal(2, native.Calls);
    }

    [Fact]
    public void Cache_InvalidatesWhenFileIdentityChangesWithSamePathSizeAndMtime()
    {
        using var files = new FakeFileAccess(
            File(@"C:\a.exe", 10, 20, fileIndex: 1),
            File(@"C:\a.exe", 10, 20, fileIndex: 2));
        var native = new FakeWinVerifyTrust(0, "CN=Publisher");
        var verifier = new AuthenticodeSignatureVerifier(files, native);

        verifier.Verify("ignored");
        verifier.Verify("ignored");

        Assert.Equal(2, native.Calls);
    }

    [Fact]
    public void Cache_DoesNotPersistFailuresThatHaveNoFileIdentity()
    {
        using var files = new FakeFileAccess(SignatureFileFailure.Missing, SignatureFileFailure.Missing);
        var native = new FakeWinVerifyTrust(0, null);
        var verifier = new AuthenticodeSignatureVerifier(files, native);

        verifier.Verify("missing");
        verifier.Verify("missing");

        Assert.Equal(2, files.Calls);
        Assert.Equal(0, native.Calls);
    }

    [Fact]
    public void TrustException_FailsClosedAsUnverified()
    {
        using var files = new FakeFileAccess(File(@"C:\a.exe", 1, 1));
        var verifier = new AuthenticodeSignatureVerifier(files, new ThrowingWinVerifyTrust());

        var result = verifier.Verify(@"C:\a.exe");

        Assert.Equal(SigningStatus.Unverified, result.Status);
        Assert.Null(result.Publisher);
    }

    [Fact]
    public void TrustSuccessWithoutReadableSigner_FailsClosedAsUnverified()
    {
        using var files = new FakeFileAccess(File(@"C:\a.exe", 1, 1));
        var result = new AuthenticodeSignatureVerifier(files, new FakeWinVerifyTrust(0, null)).Verify(@"C:\a.exe");

        Assert.Equal(SigningStatus.Unverified, result.Status);
        Assert.Null(result.Publisher);
    }

    [Theory]
    [InlineData(@"\\server\share\app.exe")]
    [InlineData(@"\\?\UNC\server\share\app.exe")]
    [InlineData(@"\??\UNC\server\share\app.exe")]
    [InlineData(@"\\.\GLOBALROOT\Device\app.exe")]
    public void RemoteAndDeviceSyntax_IsRejectedLexically(string path)
    {
        Assert.True(WindowsSignatureFileAccess.IsNetworkOrDeviceSyntax(path));
    }

    private static SignatureFile File(
        string path, long size, long mtime, ulong fileIndex = 0, string contentHash = "same-hash") =>
        new(path, size, mtime, contentHash,
            new SafeFileHandle(new IntPtr(123), ownsHandle: false), 0, fileIndex);

    private sealed class FakeFileAccess : ISignatureFileAccess, IDisposable
    {
        private readonly Queue<SignatureFileOpenResult> _results;
        public int Calls { get; private set; }

        public FakeFileAccess(params SignatureFile[] files) : this(files.Select(SignatureFileOpenResult.Opened).ToArray()) { }
        public FakeFileAccess(params SignatureFileFailure[] failures) : this(failures.Select(f => SignatureFileOpenResult.Failed(f, f.ToString())).ToArray()) { }
        private FakeFileAccess(params SignatureFileOpenResult[] results) => _results = new(results);

        public SignatureFileOpenResult OpenLocal(string path)
        {
            Calls++;
            return _results.Dequeue();
        }

        public void Dispose()
        {
            foreach (var result in _results)
                result.File?.Dispose();
        }
    }

    private sealed class FakeWinVerifyTrust(uint resultCode, string? subject) : IWinVerifyTrust
    {
        public int Calls { get; private set; }
        public WinTrustResult Verify(SignatureFile file)
        {
            Calls++;
            return new WinTrustResult(resultCode, subject);
        }
    }

    private sealed class ThrowingWinVerifyTrust : IWinVerifyTrust
    {
        public WinTrustResult Verify(SignatureFile file) => throw new InvalidOperationException("native failure");
    }
}
