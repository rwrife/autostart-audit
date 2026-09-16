using System.Runtime.InteropServices;

namespace AutostartAudit.Core.Signatures;

/// <summary>Native WinVerifyTrust call configured for cache-only, no-revocation-network evaluation.</summary>
public sealed class WindowsWinVerifyTrust : IWinVerifyTrust
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    private const uint CertNameSimpleDisplayType = 4;
    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public WinTrustResult Verify(SignatureFile file)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WinVerifyTrust requires Windows");

        var handleReference = false;
        IntPtr fileInfoPointer = IntPtr.Zero;
        var structureWritten = false;
        var data = new WinTrustData();
        try
        {
            file.Handle.DangerousAddRef(ref handleReference);
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = file.NormalizedPath,
                FileHandle = file.Handle.DangerousGetHandle(),
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            structureWritten = true;
            data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                FileInfoPointer = fileInfoPointer,
                StateAction = WtdStateActionVerify,
                ProviderFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
            };
            var action = ActionGenericVerifyV2;
            var code = unchecked((uint)WinVerifyTrust(IntPtr.Zero, ref action, ref data));
            var subject = code == 0 ? ReadSignerSubject(data.StateData) : null;
            return new WinTrustResult(code, subject);
        }
        finally
        {
            if (data.StateData != IntPtr.Zero)
            {
                data.StateAction = WtdStateActionClose;
                var action = ActionGenericVerifyV2;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
            if (structureWritten)
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            if (fileInfoPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(fileInfoPointer);
            if (handleReference)
                file.Handle.DangerousRelease();
        }
    }

    private static string? ReadSignerSubject(IntPtr stateData)
    {
        if (stateData == IntPtr.Zero)
            return null;
        var providerData = WTHelperProvDataFromStateData(stateData);
        if (providerData == IntPtr.Zero)
            return null;
        var signer = WTHelperGetProvSignerFromChain(providerData, 0, false, 0);
        if (signer == IntPtr.Zero)
            return null;
        var providerCert = WTHelperGetProvCertFromChain(signer, 0);
        if (providerCert == IntPtr.Zero)
            return null;
        var cert = Marshal.PtrToStructure<CryptProviderCert>(providerCert).Cert;
        if (cert == IntPtr.Zero)
            return null;
        var length = CertGetNameStringW(cert, CertNameSimpleDisplayType, 0, IntPtr.Zero, null, 0);
        if (length <= 1)
            return null;
        var buffer = new char[length];
        return CertGetNameStringW(cert, CertNameSimpleDisplayType, 0, IntPtr.Zero, buffer, length) <= 1
            ? null
            : new string(buffer, 0, (int)length - 1);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPointer;
        public uint StateAction;
        public IntPtr StateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCert
    {
        public uint StructSize;
        public IntPtr Cert;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid actionId, ref WinTrustData trustData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr providerData, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint CertGetNameStringW(IntPtr certContext, uint type, uint flags, IntPtr typeParameter,
        [Out] char[]? name, uint nameLength);
}
