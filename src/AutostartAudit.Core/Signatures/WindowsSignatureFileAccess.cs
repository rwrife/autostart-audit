using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace AutostartAudit.Core.Signatures;

/// <summary>
/// Opens only absolute, local Windows files. UNC/device paths, mapped remote
/// drives, and every reparse point are rejected before trust evaluation.
/// </summary>
public sealed class WindowsSignatureFileAccess : ISignatureFileAccess
{
    private const uint FileReadData = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint DriveRemote = 4;
    private const uint DriveFixed = 3;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;

    public SignatureFileOpenResult OpenLocal(string path)
    {
        if (!OperatingSystem.IsWindows())
            return SignatureFileOpenResult.Failed(SignatureFileFailure.UnsupportedPlatform, "Authenticode verification requires Windows");
        if (string.IsNullOrWhiteSpace(path))
            return SignatureFileOpenResult.Failed(SignatureFileFailure.Missing, "target path is empty");
        if (IsNetworkOrDeviceSyntax(path))
            return SignatureFileOpenResult.Failed(SignatureFileFailure.Remote, "network and device paths are not verified");
        if (!Path.IsPathFullyQualified(path))
            return SignatureFileOpenResult.Failed(SignatureFileFailure.Unreadable, "target path is not absolute");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return SignatureFileOpenResult.Failed(SignatureFileFailure.Unreadable, "target path is malformed");
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            return SignatureFileOpenResult.Failed(SignatureFileFailure.Remote, "only absolute local drive paths are verified");
        var driveType = GetDriveTypeW(root);
        if (driveType == DriveRemote)
            return SignatureFileOpenResult.Failed(SignatureFileFailure.Remote, "mapped remote drives are not verified");
        if (driveType != DriveFixed)
            return SignatureFileOpenResult.Failed(SignatureFileFailure.Unreadable, $"drive type {driveType} is not a fixed local drive");

        var directoryHandles = new List<SafeFileHandle>();
        SafeFileHandle? handle = null;
        var transferred = false;
        try
        {
            var chainFailure = OpenDirectoryChain(fullPath, root, directoryHandles);
            if (chainFailure is not null)
                return chainFailure;

            handle = CreateFileW(fullPath, FileReadData | FileReadAttributes,
                FileShareRead, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                return OpenFailure(error);
            }

            if (!GetFileInformationByHandle(handle, out var info))
                return SignatureFileOpenResult.Failed(SignatureFileFailure.Unreadable,
                    $"file metadata read failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
                return SignatureFileOpenResult.Failed(SignatureFileFailure.UnsafeReparsePoint, "reparse-point targets are not verified");
            if ((info.FileAttributes & FileAttributeDirectory) != 0)
                return SignatureFileOpenResult.Failed(SignatureFileFailure.Unreadable, "target is not a file");

            var locationFailure = ValidateHandleIsLocal(handle);
            if (locationFailure is not null)
                return locationFailure;

            var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            var mtime = ((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow;
            var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            string contentHash;
            try
            {
                contentHash = ComputeContentHash(handle);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return SignatureFileOpenResult.Failed(SignatureFileFailure.Unreadable,
                    $"file content hash failed: {ex.GetType().Name}");
            }
            transferred = true;
            return SignatureFileOpenResult.Opened(new SignatureFile(fullPath, size, mtime, contentHash, handle,
                info.VolumeSerialNumber, fileIndex, directoryHandles));
        }
        finally
        {
            if (!transferred)
            {
                handle?.Dispose();
                DisposeHandles(directoryHandles);
            }
        }
    }

    private static SignatureFileOpenResult? OpenDirectoryChain(
        string fullPath, string root, List<SafeFileHandle> handles)
    {
        var failure = OpenStableDirectory(root, handles);
        if (failure is not null)
            return failure;

        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent) || parent.Length <= root.Length)
            return null;

        var relative = parent[root.Length..];
        var current = root.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current += Path.DirectorySeparatorChar + component;
            failure = OpenStableDirectory(current, handles);
            if (failure is not null)
                return failure;
        }
        return null;
    }

    private static SignatureFileOpenResult? OpenStableDirectory(string path, List<SafeFileHandle> handles)
    {
        // Attribute-only opens do not participate in Windows sharing checks.
        // FILE_READ_DATA is FILE_LIST_DIRECTORY for a directory: request it so
        // the held handle actually excludes writers and rename/delete access.
        var handle = CreateFileW(path, FileReadData | FileReadAttributes, FileShareRead,
            IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return OpenFailure(error);
        }

        if (!GetFileInformationByHandle(handle, out var info))
        {
            var detail = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            handle.Dispose();
            return SignatureFileOpenResult.Failed(SignatureFileFailure.Unreadable,
                $"directory metadata read failed: {detail}");
        }
        if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            handle.Dispose();
            return SignatureFileOpenResult.Failed(SignatureFileFailure.UnsafeReparsePoint,
                "paths traversing reparse points are not verified");
        }
        if ((info.FileAttributes & FileAttributeDirectory) == 0)
        {
            handle.Dispose();
            return SignatureFileOpenResult.Failed(SignatureFileFailure.Unreadable,
                "path component is not a directory");
        }

        var locationFailure = ValidateHandleIsLocal(handle);
        if (locationFailure is not null)
        {
            handle.Dispose();
            return locationFailure;
        }

        handles.Add(handle);
        return null;
    }

    private static SignatureFileOpenResult? ValidateHandleIsLocal(SafeFileHandle handle)
    {
        var finalPath = GetFinalPath(handle);
        if (finalPath is null || finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || finalPath.StartsWith(@"\\", StringComparison.Ordinal) && !finalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return SignatureFileOpenResult.Failed(SignatureFileFailure.Remote, "resolved path is not demonstrably local");

        var finalDosPath = finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? finalPath[4..] : finalPath;
        var finalRoot = Path.GetPathRoot(finalDosPath);
        return string.IsNullOrEmpty(finalRoot) || GetDriveTypeW(finalRoot) != DriveFixed
            ? SignatureFileOpenResult.Failed(SignatureFileFailure.Remote, "resolved path is not on a fixed local drive")
            : null;
    }

    private static void DisposeHandles(IEnumerable<SafeFileHandle> handles)
    {
        foreach (var handle in handles.Reverse())
            handle.Dispose();
    }

    private static string ComputeContentHash(SafeFileHandle handle)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        int read;
        // Positional reads keep the handle's file pointer unchanged for WinVerifyTrust.
        while ((read = RandomAccess.Read(handle, buffer, offset)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            offset += read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static SignatureFileOpenResult OpenFailure(int error) => error switch
    {
        ErrorFileNotFound or ErrorPathNotFound => SignatureFileOpenResult.Failed(SignatureFileFailure.Missing, "target file does not exist"),
        ErrorSharingViolation => SignatureFileOpenResult.Failed(SignatureFileFailure.Locked, "target file is locked"),
        ErrorAccessDenied => SignatureFileOpenResult.Failed(SignatureFileFailure.Unreadable, "target file is not readable"),
        _ => SignatureFileOpenResult.Failed(SignatureFileFailure.Unreadable, $"target file cannot be opened (Win32 error {error})"),
    };

    internal static bool IsNetworkOrDeviceSyntax(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal)
        || path.StartsWith(@"\??\", StringComparison.Ordinal)
        || path.StartsWith(@"\\.\", StringComparison.Ordinal);

    private static string? GetFinalPath(SafeFileHandle handle)
    {
        var required = GetFinalPathNameByHandleW(handle, null, 0, 0);
        if (required == 0)
            return null;
        var buffer = new char[required + 1];
        var written = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        return written == 0 ? null : new string(buffer, 0, (int)written);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string rootPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[]? path, uint pathLength, uint flags);
}
