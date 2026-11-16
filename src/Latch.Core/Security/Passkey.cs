using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;

namespace Latch.Core.Security;

/// <summary>A registered passkey: its id, its COSE public key and the last signature counter seen.</summary>
public sealed record PasskeyCredential(byte[] Id, byte[] PublicKey, uint SignCount);

/// <summary>What the platform's passkey dialog returns when a passkey is created.</summary>
public sealed record PasskeyCreation(byte[] CredentialId, byte[] AuthenticatorData);

/// <summary>What the platform's passkey dialog returns when a passkey signs a challenge.</summary>
public sealed record PasskeyAssertion(byte[] AuthenticatorData, byte[] Signature);

/// <summary>
/// Local WebAuthn for Latch: Latch is both client and relying party, so it builds the client data itself and
/// verifies the authenticator's signature with the public key stored at registration. A valid signature over a
/// fresh random challenge proves the owner has the phone or security key.
/// </summary>
public static class Passkeys
{
    public const string RpId = "latch.local";
    public const string Origin = "https://latch.local";

    private const byte FlagUserPresent = 0x01;
    private const byte FlagAttested = 0x40;

    /// <summary>The client data JSON for a new ceremony; its SHA-256 is what the authenticator signs.</summary>
    public static byte[] ClientData(string type, byte[] challenge) =>
        Encoding.UTF8.GetBytes($"{{\"type\":\"{type}\",\"challenge\":\"{Base64Url(challenge)}\",\"origin\":\"{Origin}\",\"crossOrigin\":false}}");

    public static byte[] NewChallenge() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Reads the credential out of a registration, or null when it isn't one for Latch.</summary>
    public static PasskeyCredential? FromRegistration(PasskeyCreation creation)
    {
        var data = creation.AuthenticatorData;
        if (data.Length < 37 + 18 || !RpMatches(data)) return null;
        var flags = data[32];
        if ((flags & FlagUserPresent) == 0 || (flags & FlagAttested) == 0) return null;
        var signCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(33, 4));
        var idLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(53, 2));
        var keyStart = 55 + idLength;
        if (keyStart >= data.Length) return null;
        var id = data.AsSpan(55, idLength).ToArray();
        if (!id.AsSpan().SequenceEqual(creation.CredentialId)) return null;
        try
        {
            var reader = new CborReader(data.AsMemory(keyStart), CborConformanceMode.Lax);
            reader.SkipValue();
            var key = data.AsSpan(keyStart, data.Length - keyStart - reader.BytesRemaining).ToArray();
            using var _ = ImportKey(key); // Rejects key types Latch can't verify.
            return new PasskeyCredential(id, key, signCount);
        }
        catch (Exception e) when (e is CborContentException or InvalidOperationException or CryptographicException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Checks an assertion. Returns the new signature counter, or null when it doesn't verify.</summary>
    public static uint? Verify(PasskeyCredential credential, byte[] clientData, PasskeyAssertion assertion)
    {
        var data = assertion.AuthenticatorData;
        if (data.Length < 37 || !RpMatches(data) || (data[32] & FlagUserPresent) == 0) return null;
        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(33, 4));
        // A counter that doesn't move forward means a cloned authenticator; 0/0 means the authenticator has none.
        if ((count != 0 || credential.SignCount != 0) && count <= credential.SignCount) return null;
        var signed = new byte[data.Length + 32];
        data.CopyTo(signed, 0);
        SHA256.HashData(clientData, signed.AsSpan(data.Length));
        try
        {
            using var key = ImportKey(credential.PublicKey);
            var ok = key switch
            {
                ECDsa ec => ec.VerifyData(signed, assertion.Signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence),
                RSA rsa => rsa.VerifyData(signed, assertion.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                _ => false,
            };
            return ok ? count : null;
        }
        catch (Exception e) when (e is CborContentException or InvalidOperationException or CryptographicException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool RpMatches(byte[] authData) =>
        CryptographicOperations.FixedTimeEquals(authData.AsSpan(0, 32), SHA256.HashData(Encoding.UTF8.GetBytes(RpId)));

    /// <summary>COSE keys Latch accepts: EC2 P-256 with ES256 (-7) and RSA with RS256 (-257).</summary>
    internal static AsymmetricAlgorithm ImportKey(byte[] cose)
    {
        var reader = new CborReader(cose, CborConformanceMode.Lax);
        var count = reader.ReadStartMap() ?? throw new NotSupportedException("Indefinite COSE map");
        long kty = 0, alg = 0, crv = 0;
        byte[]? x = null, y = null, n = null, e = null;
        for (var i = 0; i < count; i++)
        {
            var label = reader.ReadInt64();
            switch (label)
            {
                case 1: kty = reader.ReadInt64(); break;
                case 3: alg = reader.ReadInt64(); break;
                case -1 when reader.PeekState() == CborReaderState.UnsignedInteger: crv = reader.ReadInt64(); break;
                case -1: n = reader.ReadByteString(); break;
                case -2 when kty == 2: x = reader.ReadByteString(); break;
                case -2: e = reader.ReadByteString(); break;
                case -3: y = reader.ReadByteString(); break;
                default: reader.SkipValue(); break;
            }
        }
        if (kty == 2 && alg == -7 && crv == 1 && x is { Length: 32 } && y is { Length: 32 })
            return ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
        if (kty == 3 && alg == -257 && n is not null && e is not null)
            return RSA.Create(new RSAParameters { Modulus = n, Exponent = e });
        throw new NotSupportedException($"COSE key kty={kty} alg={alg}");
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
