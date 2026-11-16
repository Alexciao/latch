using System.Runtime.InteropServices;
using Latch.Core.Security;

namespace Latch.Host;

/// <summary>
/// Passkeys through the Windows WebAuthn API (webauthn.dll). Only cross-platform authenticators are allowed: a
/// phone (scan the QR code) or a security key. A passkey kept on this PC would be unlocked by the Windows PIN,
/// which is exactly what Latch's own PIN exists to avoid.
/// </summary>
internal static class WebAuthn
{
    private const uint AttachmentCrossPlatform = 2;
    private const uint UserVerificationPreferred = 2;
    private const uint AttestationNone = 1;
    private const int CancelledHr = unchecked((int)0x800704C7); // HRESULT_FROM_WIN32(ERROR_CANCELLED)
    private const int NotAllowedHr = unchecked((int)0x80090036); // NTE_USER_CANCELLED
    private const string PublicKey = "public-key";
    private const string Sha256 = "SHA-256";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RpEntity { public uint Version; public string Id; public string Name; public string? Icon; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserEntity { public uint Version; public uint IdLength; public nint Id; public string Name; public string? Icon; public string DisplayName; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CoseParameter { public uint Version; public string Type; public int Alg; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CoseParameters { public uint Count; public nint Items; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ClientData { public uint Version; public uint JsonLength; public nint Json; public string HashAlg; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credentials { public uint Count; public nint Items; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Extensions { public uint Count; public nint Items; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential { public uint Version; public uint IdLength; public nint Id; public string Type; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MakeOptions
    {
        public uint Version, TimeoutMs;
        public Credentials CredentialList;
        public Extensions Extensions;
        public uint Attachment;
        public int RequireResidentKey;
        public uint UserVerification, Attestation, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GetOptions
    {
        public uint Version, TimeoutMs;
        public Credentials CredentialList;
        public Extensions Extensions;
        public uint Attachment, UserVerification, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Attestation
    {
        public uint Version;
        public nint FormatType;
        public uint AuthDataLength;
        public nint AuthData;
        public uint AttestationLength;
        public nint AttestationData;
        public uint DecodeType;
        public nint Decoded;
        public uint ObjectLength;
        public nint Object;
        public uint CredentialIdLength;
        public nint CredentialId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Assertion
    {
        public uint Version;
        public uint AuthDataLength;
        public nint AuthData;
        public uint SignatureLength;
        public nint Signature;
    }

    [DllImport("webauthn.dll")] private static extern uint WebAuthNGetApiVersionNumber();

    [DllImport("webauthn.dll", CharSet = CharSet.Unicode)]
    private static extern int WebAuthNAuthenticatorMakeCredential(nint hwnd, ref RpEntity rp, ref UserEntity user, ref CoseParameters parameters, ref ClientData clientData, ref MakeOptions options, out nint attestation);

    [DllImport("webauthn.dll", CharSet = CharSet.Unicode)]
    private static extern int WebAuthNAuthenticatorGetAssertion(nint hwnd, string rpId, ref ClientData clientData, ref GetOptions options, out nint assertion);

    [DllImport("webauthn.dll")] private static extern void WebAuthNFreeCredentialAttestation(nint attestation);
    [DllImport("webauthn.dll")] private static extern void WebAuthNFreeAssertion(nint assertion);

    public static bool Available
    {
        get
        {
            try
            {
                return WebAuthNGetApiVersionNumber() >= 1;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>Blocking; run off the UI thread. Null when the owner cancels.</summary>
    public static PasskeyCreation? Create(nint hwnd, byte[] clientDataJson, string rpId, string rpName)
    {
        using var pins = new Pins();
        var userId = pins.Bytes(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var rp = new RpEntity { Version = 1, Id = rpId, Name = rpName };
        var user = new UserEntity { Version = 1, IdLength = 16, Id = userId, Name = Environment.UserName, DisplayName = Environment.UserName };
        var algs = new[] { new CoseParameter { Version = 1, Type = PublicKey, Alg = -7 }, new CoseParameter { Version = 1, Type = PublicKey, Alg = -257 } };
        var algHandle = GCHandle.Alloc(algs, GCHandleType.Pinned);
        try
        {
            var parameters = new CoseParameters { Count = (uint)algs.Length, Items = algHandle.AddrOfPinnedObject() };
            var client = new ClientData { Version = 1, JsonLength = (uint)clientDataJson.Length, Json = pins.Bytes(clientDataJson), HashAlg = Sha256 };
            var options = new MakeOptions
            {
                Version = 1,
                TimeoutMs = 120_000,
                Attachment = AttachmentCrossPlatform,
                UserVerification = UserVerificationPreferred,
                Attestation = AttestationNone,
            };
            var hr = WebAuthNAuthenticatorMakeCredential(hwnd, ref rp, ref user, ref parameters, ref client, ref options, out var result);
            if (hr is CancelledHr or NotAllowedHr) return null;
            if (hr != 0) throw Marshal.GetExceptionForHR(hr)!;
            try
            {
                var a = Marshal.PtrToStructure<Attestation>(result);
                return new PasskeyCreation(Copy(a.CredentialId, a.CredentialIdLength), Copy(a.AuthData, a.AuthDataLength));
            }
            finally
            {
                WebAuthNFreeCredentialAttestation(result);
            }
        }
        finally
        {
            algHandle.Free();
        }
    }

    /// <summary>Blocking; run off the UI thread. Null when the owner cancels.</summary>
    public static PasskeyAssertion? Get(nint hwnd, byte[] clientDataJson, string rpId, byte[] credentialId)
    {
        using var pins = new Pins();
        var cred = new[] { new Credential { Version = 1, IdLength = (uint)credentialId.Length, Id = pins.Bytes(credentialId), Type = PublicKey } };
        var credHandle = GCHandle.Alloc(cred, GCHandleType.Pinned);
        try
        {
            var client = new ClientData { Version = 1, JsonLength = (uint)clientDataJson.Length, Json = pins.Bytes(clientDataJson), HashAlg = Sha256 };
            var options = new GetOptions
            {
                Version = 1,
                TimeoutMs = 120_000,
                CredentialList = new Credentials { Count = 1, Items = credHandle.AddrOfPinnedObject() },
                Attachment = AttachmentCrossPlatform,
                UserVerification = UserVerificationPreferred,
            };
            var hr = WebAuthNAuthenticatorGetAssertion(hwnd, rpId, ref client, ref options, out var result);
            if (hr is CancelledHr or NotAllowedHr) return null;
            if (hr != 0)
            {
                Log.Info($"Passkey assertion failed: 0x{hr:X8}");
                return null;
            }
            try
            {
                var a = Marshal.PtrToStructure<Assertion>(result);
                return new PasskeyAssertion(Copy(a.AuthData, a.AuthDataLength), Copy(a.Signature, a.SignatureLength));
            }
            finally
            {
                WebAuthNFreeAssertion(result);
            }
        }
        finally
        {
            credHandle.Free();
        }
    }

    private static byte[] Copy(nint p, uint length)
    {
        var bytes = new byte[length];
        if (length > 0) Marshal.Copy(p, bytes, 0, (int)length);
        return bytes;
    }

    /// <summary>Pins byte arrays for the duration of a native call.</summary>
    private sealed class Pins : IDisposable
    {
        private readonly List<GCHandle> _handles = [];

        public nint Bytes(byte[] data)
        {
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            _handles.Add(h);
            return h.AddrOfPinnedObject();
        }

        public void Dispose()
        {
            foreach (var h in _handles) h.Free();
        }
    }
}
