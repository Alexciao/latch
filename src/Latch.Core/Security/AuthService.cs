using Latch.Core.Contract;
using Latch.Core.Model;
using Latch.Core.Storage;

namespace Latch.Core.Security;

public sealed record AttemptResult(UnlockOutcome Outcome, DateTimeOffset? LockoutUntil, bool LockoutStarted)
{
    public bool Ok => Outcome == UnlockOutcome.Ok;
}

/// <summary>
/// Owns the unlock secrets and the wrong-guess counter. PIN attempts run one at a time, so parallel guesses can't
/// skip the backoff. The counter and lockout end time are persisted on every change.
/// </summary>
public sealed class AuthService
{
    private readonly SecretStore _store;
    private readonly TimeProvider _clock;
    private readonly int _iterations;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Lock _gate = new();
    private SecretsFile _s;

    public AuthService(SecretStore store, TimeProvider clock, int iterations = PinHasher.DefaultIterations)
    {
        _store = store;
        _clock = clock;
        _iterations = iterations;
        _s = store.Load();
    }

    public bool PinSet { get { lock (_gate) return _s.PinHash is not null; } }

    /// <summary>How many digits the PIN has, once known; the prompt submits as soon as that many are typed.</summary>
    public int? PinLength { get { lock (_gate) return _s.PinHash is not null && _s.PinLength > 0 ? _s.PinLength : null; } }
    public bool HelloOn { get { lock (_gate) return _s.HelloOn; } }
    public bool PasskeyOn { get { lock (_gate) return Passkey is not null; } }

    public DateTimeOffset? LockoutUntil
    {
        get
        {
            lock (_gate) return _s.LockoutUntil is { } u && u > _clock.GetUtcNow() ? u : null;
        }
    }

    /// <summary>The registered passkey, if any.</summary>
    public PasskeyCredential? Passkey
    {
        get
        {
            lock (_gate)
            {
                if (_s.PasskeyId is null || _s.PasskeyKey is null) return null;
                try
                {
                    return new PasskeyCredential(Convert.FromBase64String(_s.PasskeyId), Convert.FromBase64String(_s.PasskeyKey), _s.PasskeyCount);
                }
                catch (FormatException)
                {
                    return null;
                }
            }
        }
    }

    public void SetPin(string pin)
    {
        if (!PinHasher.IsValidFormat(pin)) throw new ApiException(ErrorCode.PinFormat);
        var (hash, salt) = PinHasher.Hash(pin, _iterations);
        Update(s => s with { PinHash = hash, PinSalt = salt, PinIterations = _iterations, PinLength = pin.Length });
    }

    public void SetHello(bool on) => Update(s => s with { HelloOn = on });

    public void SavePasskey(PasskeyCredential credential) => Update(s => s with
    {
        PasskeyId = Convert.ToBase64String(credential.Id),
        PasskeyKey = Convert.ToBase64String(credential.PublicKey),
        PasskeyCount = credential.SignCount,
    });

    public void RemovePasskey() => Update(s => s with { PasskeyId = null, PasskeyKey = null, PasskeyCount = 0 });

    public async Task<AttemptResult> TryPinAsync(string pin)
    {
        await _serial.WaitAsync();
        try
        {
            SecretsFile s;
            lock (_gate) s = _s;
            if (LockoutUntil is { } until) return new(UnlockOutcome.LockedOut, until, false);
            var ok = s.PinHash is not null && s.PinSalt is not null && PinHasher.IsValidFormat(pin)
                && await Task.Run(() => PinHasher.Verify(pin, s.PinHash, s.PinSalt, s.PinIterations));
            if (!ok) return Failed();
            if (s.PinLength != pin.Length) Update(x => x with { PinLength = pin.Length }); // Learned for PINs set before it was stored.
            return Succeeded();
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>
    /// Checks a passkey signature. A passkey can't be guessed, so it works during a PIN lockout (it is the way back
    /// in after a forgotten PIN) and a correct one clears the counter. A bad signature counts as a wrong guess.
    /// </summary>
    public AttemptResult TryPasskey(byte[] clientData, PasskeyAssertion assertion)
    {
        var credential = Passkey;
        if (credential is null) return new(UnlockOutcome.NotVerified, LockoutUntil, false);
        var count = Passkeys.Verify(credential, clientData, assertion);
        if (count is null) return Failed();
        Update(s => s with { PasskeyCount = count.Value });
        return Succeeded();
    }

    /// <summary>A correct Windows Hello result clears the counter.</summary>
    public AttemptResult HelloSucceeded() => Succeeded();

    private AttemptResult Succeeded()
    {
        Update(s => s with { FailedCount = 0, LockoutUntil = null });
        return new(UnlockOutcome.Ok, null, false);
    }

    private AttemptResult Failed()
    {
        var now = _clock.GetUtcNow();
        DateTimeOffset? until = null;
        Update(s =>
        {
            var count = s.FailedCount + 1;
            var wait = Lockout.WaitAfter(count);
            until = wait > TimeSpan.Zero ? now + wait : null;
            return s with { FailedCount = count, LockoutUntil = until };
        });
        return until is { } u ? new(UnlockOutcome.LockedOut, u, true) : new(UnlockOutcome.Wrong, null, false);
    }

    private void Update(Func<SecretsFile, SecretsFile> change)
    {
        lock (_gate)
        {
            _s = change(_s);
            _store.Save(_s);
        }
    }
}
