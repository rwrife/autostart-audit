using AutostartAudit.Core.Model;
using AutostartAudit.Core.Signatures;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutostartAudit.Core.Tests;

public sealed class WindowsAuthenticodeIntegrationTests
{
    [WindowsFact]
    [Trait("Category", "WindowsAuthenticode")]
    public void Kernel32EmbeddedSignature_VerifiesWithNativeLocalOnlyPolicy()
    {
        var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var result = SignatureVerifierFactory.CreateForCurrentOS().Verify(kernel32);

        Assert.Equal(SigningStatus.Signed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Publisher));

        // This label is deliberately emitted only after the live native assertions pass.
        Console.WriteLine($"BENCH-VERIFIED Windows Authenticode: {kernel32} => signed ({result.Publisher})");
    }

    [WindowsFact]
    [Trait("Category", "WindowsAuthenticode")]
    public void ExclusivelyLockedFile_IsUnverified()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "locked.exe");
        File.WriteAllText(path, "not a PE file");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = SignatureVerifierFactory.CreateForCurrentOS().Verify(path);

        Assert.Equal(SigningStatus.Unverified, result.Status);
    }

    [WindowsFact]
    [Trait("Category", "WindowsAuthenticode")]
    public void FileHeldOpenForWriting_IsUnverified()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "writing.exe");
        File.WriteAllText(path, "not a PE file");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);

        var result = SignatureVerifierFactory.CreateForCurrentOS().Verify(path);

        Assert.Equal(SigningStatus.Unverified, result.Status);
    }

    [WindowsFact]
    [Trait("Category", "WindowsAuthenticode")]
    public void HeldDirectoryChain_DeniesWriteAndRename_UntilDisposed()
    {
        using var temporary = new TemporaryDirectory();
        var stableDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "stable")).FullName;
        var renamedDirectory = Path.Combine(temporary.Path, "renamed");
        var path = Path.Combine(stableDirectory, "app.exe");
        File.WriteAllText(path, "not a PE file");

        var opened = new WindowsSignatureFileAccess().OpenLocal(path);
        var held = Assert.IsType<SignatureFile>(opened.File);
        try
        {
            using var writer = OpenDirectoryForWrite(stableDirectory);
            var writeError = Marshal.GetLastWin32Error();
            Assert.True(writer.IsInvalid);
            Assert.Equal(32, writeError);
            Assert.ThrowsAny<IOException>(() => Directory.Move(stableDirectory, renamedDirectory));
        }
        finally
        {
            held.Dispose();
        }

        using (var writer = OpenDirectoryForWrite(stableDirectory))
            Assert.False(writer.IsInvalid);
        Directory.Move(stableDirectory, renamedDirectory);
        Assert.True(Directory.Exists(renamedDirectory));
    }

    [WindowsFact]
    [Trait("Category", "WindowsAuthenticode")]
    public void DirectoryHeldOpenForWriting_IsUnverified()
    {
        using var temporary = new TemporaryDirectory();
        var stableDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "stable")).FullName;
        var path = Path.Combine(stableDirectory, "app.exe");
        File.WriteAllText(path, "not a PE file");
        using var writer = OpenDirectoryForWrite(stableDirectory);
        Assert.False(writer.IsInvalid);

        var result = SignatureVerifierFactory.CreateForCurrentOS().Verify(path);

        Assert.Equal(SigningStatus.Unverified, result.Status);
    }

    [WindowsFact]
    [Trait("Category", "WindowsAuthenticode")]
    public void ReparsePointTraversal_IsUnverified()
    {
        using var temporary = new TemporaryDirectory();
        var target = Directory.CreateDirectory(Path.Combine(temporary.Path, "target"));
        File.WriteAllText(Path.Combine(target.FullName, "app.exe"), "not a PE file");
        var link = Path.Combine(temporary.Path, "link");
        CreateJunction(link, target.FullName);

        try
        {
            var result = SignatureVerifierFactory.CreateForCurrentOS().Verify(Path.Combine(link, "app.exe"));
            Assert.Equal(SigningStatus.Unverified, result.Status);
        }
        finally
        {
            // Remove only the junction itself. Recursive .NET cleanup can treat
            // a directory junction as a volume mount point and fail with ERROR_INVALID_PARAMETER.
            Directory.Delete(link, recursive: false);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start cmd.exe for junction creation.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /J failed: {stdout} {stderr}");
    }

    private static SafeFileHandle OpenDirectoryForWrite(string path) =>
        CreateFileW(path, 0x40000000, 0x00000001 | 0x00000002 | 0x00000004,
            IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"autostart-audit-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
