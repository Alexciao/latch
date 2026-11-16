using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using Latch.Core.Contract;
using Latch.Core.Security;
using Latch.Core.Storage;

namespace Latch.Core.Tests;

/// <summary>A software authenticator: a real P-256 key producing real WebAuthn authenticator data and signatures.</summary>
internal sealed class SoftKey : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public byte[] Id { get; } = RandomNumberGenerator.GetBytes(16);
    public uint Counter { get; set; }

    public PasskeyCreation Create(string rpId = Passkeys.RpId)
    {
        var p = _key.ExportParameters(false);
        var cose = new CborWriter();
        cose.WriteStartMap(5);
        cose.WriteInt32(1); cose.WriteInt32(2);   // kty: EC2
        cose.WriteInt32(3); cose.WriteInt32(-7);  // alg: ES256
        cose.WriteInt32(-1); cose.WriteInt32(1);  // crv: P-256
        cose.WriteInt32(-2); cose.WriteByteString(p.Q.X!);
        cose.WriteInt32(-3); cose.WriteByteString(p.Q.Y!);
        cose.WriteEndMap();
        var attested = new byte[16 + 2 + Id.Length];
        BinaryPrimitives.WriteUInt16BigEndian(attested.AsSpan(16), (ushort)Id.Length);
        Id.CopyTo(attested, 18);
        return new PasskeyCreation(Id, [.. Header(rpId, 0x41), .. attested, .. cose.Encode()]);
    }

    public PasskeyAssertion Sign(byte[] clientData, string rpId = Passkeys.RpId, byte flags = 0x05)
    {
        Counter++;
        var authData = Header(rpId, flags);
        byte[] signed = [.. authData, .. SHA256.HashData(clientData)];
        return new PasskeyAssertion(authData, _key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    private byte[] Header(string rpId, byte flags)
    {
        var h = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(rpId)).CopyTo(h, 0);
        h[32] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(33), Counter);
        return h;
    }

    public void Dispose() => _key.Dispose();
}

public class PasskeyTests
{
    [Fact]
    public void Registration_and_a_fresh_signature_verify()
    {
        using var key = new SoftKey();
        var credential = Passkeys.FromRegistration(key.Create());
        Assert.NotNull(credential);
        var clientData = Passkeys.ClientData("webauthn.get", Passkeys.NewChallenge());
        Assert.Equal(1u, Passkeys.Verify(credential, clientData, key.Sign(clientData)));
    }

    [Fact]
    public void Signature_over_other_client_data_or_from_another_key_fails()
    {
        using var key = new SoftKey();
        using var other = new SoftKey();
        var credential = Passkeys.FromRegistration(key.Create())!;
        var clientData = Passkeys.ClientData("webauthn.get", Passkeys.NewChallenge());
        var replayed = key.Sign(Passkeys.ClientData("webauthn.get", Passkeys.NewChallenge()));
        Assert.Null(Passkeys.Verify(credential, clientData, replayed));
        Assert.Null(Passkeys.Verify(credential, clientData, other.Sign(clientData)));
    }

    [Fact]
    public void Another_relying_party_missing_presence_or_a_stale_counter_fails()
    {
        using var key = new SoftKey();
        Assert.Null(Passkeys.FromRegistration(key.Create("evil.example")));
        var credential = Passkeys.FromRegistration(key.Create())! with { SignCount = 5 };
        var clientData = Passkeys.ClientData("webauthn.get", Passkeys.NewChallenge());
        Assert.Null(Passkeys.Verify(credential, clientData, key.Sign(clientData, rpId: "evil.example")));
        Assert.Null(Passkeys.Verify(credential, clientData, key.Sign(clientData, flags: 0x04)));
        key.Counter = 2; // Counter goes backwards: a cloned key.
        Assert.Null(Passkeys.Verify(credential, clientData, key.Sign(clientData)));
    }

    [Fact]
    public async Task Passkey_unlocks_during_a_pin_lockout_and_clears_it()
    {
        using var dir = new TempDir();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddYears(56));
        var auth = new AuthService(new SecretStore(Path.Combine(dir.Path, "s.json")), clock, iterations: 1000);
        auth.SetPin("246810");
        using var key = new SoftKey();
        auth.SavePasskey(Passkeys.FromRegistration(key.Create())!);
        for (var i = 0; i < 5; i++) await auth.TryPinAsync("111111");
        Assert.NotNull(auth.LockoutUntil);

        var clientData = Passkeys.ClientData("webauthn.get", Passkeys.NewChallenge());
        Assert.Equal(UnlockOutcome.Ok, auth.TryPasskey(clientData, key.Sign(clientData)).Outcome);
        Assert.Null(auth.LockoutUntil);
        Assert.Equal(1u, auth.Passkey!.SignCount); // Persisted, so a replayed assertion is refused next time.
    }
}
