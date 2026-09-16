using Microsoft.Win32.SafeHandles;
using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Signatures;

public sealed record SignatureVerification(SigningStatus Status, string? Publisher, string? Detail = null);

public interface ISignatureVerifier
{
    SignatureVerification Verify(string path);
}

/// <summary>A local file handle and the metadata/content identity used as its cache identity.</summary>
public sealed class SignatureFile : IDisposable
{
    private readonly IReadOnlyList<SafeFileHandle> _directoryHandles;

    public SignatureFile(string normalizedPath, long size, long lastWriteTimeUtcTicks, string contentHash,
        SafeFileHandle handle,
        uint volumeSerialNumber = 0, ulong fileIndex = 0, IReadOnlyList<SafeFileHandle>? directoryHandles = null)
    {
        NormalizedPath = normalizedPath;
        Size = size;
        LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
        ContentHash = contentHash;
        Handle = handle;
        VolumeSerialNumber = volumeSerialNumber;
        FileIndex = fileIndex;
        _directoryHandles = directoryHandles ?? Array.Empty<SafeFileHandle>();
    }

    public string NormalizedPath { get; }
    public long Size { get; }
    public long LastWriteTimeUtcTicks { get; }
    public string ContentHash { get; }
    public SafeFileHandle Handle { get; }
    public uint VolumeSerialNumber { get; }
    public ulong FileIndex { get; }

    public void Dispose()
    {
        Handle.Dispose();
        foreach (var directoryHandle in _directoryHandles.Reverse())
            directoryHandle.Dispose();
    }
}

public enum SignatureFileFailure
{
    Missing,
    Locked,
    Remote,
    Unreadable,
    UnsafeReparsePoint,
    UnsupportedPlatform,
}

public sealed record SignatureFileOpenResult(SignatureFile? File, SignatureFileFailure? Failure, string? Detail)
{
    public static SignatureFileOpenResult Opened(SignatureFile file) => new(file, null, null);
    public static SignatureFileOpenResult Failed(SignatureFileFailure failure, string detail) => new(null, failure, detail);
}

public interface ISignatureFileAccess
{
    SignatureFileOpenResult OpenLocal(string path);
}

public sealed record WinTrustResult(uint ResultCode, string? SignerSubject);

/// <summary>Injectable boundary around the WinVerifyTrust native operation.</summary>
public interface IWinVerifyTrust
{
    WinTrustResult Verify(SignatureFile file);
}
