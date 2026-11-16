using Latch.Core.Contract;
using Latch.Core.Security;
using Latch.Core.Storage;

namespace Latch.Core.Tests;

public class PinAndLockoutTests
{
    [Fact]
    public void Hash_verifies_the_right_pin_only_and_uses_a_fresh_salt()
    {
        var (hash, salt) = PinHasher.Hash("482913", iterations: 1000);
        Assert.True(PinHasher.Verify("482913", hash, salt, 1000));
        Assert.False(PinHasher.Verify("482914", hash, salt, 1000));
        Assert.NotEqual(salt, PinHasher.Hash("482913", 1000).Salt);
        Assert.True(PinHasher.DefaultIterations >= 600_000);
    }

    [Theory]
    [InlineData("12345", false)]
    [InlineData("123456", true)]
    [InlineData("123456789012", true)]
    [InlineData("1234567890123", false)]
    [InlineData("12345a", false)]
    [InlineData("١٢٣٤٥٦", false)]
    public void Pin_format_is_6_to_12_ascii_digits(string pin, bool ok) => Assert.Equal(ok, PinHasher.IsValidFormat(pin));

    [Theory]
    [InlineData(4, 0)]
    [InlineData(5, 30)]
    [InlineData(6, 60)]
    [InlineData(7, 120)]
    [InlineData(9, 480)]
    [InlineData(10, 900)]
    [InlineData(40, 900)]
    public void Backoff_starts_at_the_fifth_miss_doubles_and_caps(int misses, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), Lockout.WaitAfter(misses));

    [Fact]
    public async Task Lockout_blocks_attempts_survives_restart_and_a_correct_pin_clears_it()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "secrets.json");
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-16T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var auth = new AuthService(new SecretStore(path), clock, iterations: 1000);
        auth.SetPin("246810");

        for (var i = 0; i < 4; i++) Assert.Equal(UnlockOutcome.Wrong, (await auth.TryPinAsync("111111")).Outcome);
        var fifth = await auth.TryPinAsync("111111");
        Assert.Equal(UnlockOutcome.LockedOut, fifth.Outcome);
        Assert.True(fifth.LockoutStarted);
        Assert.Equal(clock.Now.AddSeconds(30), fifth.LockoutUntil);

        // Even the right PIN is refused during the wait, and restarting Latch doesn't reset it.
        var restarted = new AuthService(new SecretStore(path), clock, iterations: 1000);
        var during = await restarted.TryPinAsync("246810");
        Assert.Equal(UnlockOutcome.LockedOut, during.Outcome);
        Assert.False(during.LockoutStarted);

        clock.Advance(TimeSpan.FromSeconds(31));
        var sixth = await restarted.TryPinAsync("111111");
        Assert.Equal(clock.Now.AddSeconds(60), sixth.LockoutUntil);

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(UnlockOutcome.Ok, (await restarted.TryPinAsync("246810")).Outcome);
        Assert.Equal(UnlockOutcome.Wrong, (await restarted.TryPinAsync("111111")).Outcome);
    }

    [Fact]
    public async Task Hello_success_clears_the_counter()
    {
        using var dir = new TempDir();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddYears(56));
        var auth = new AuthService(new SecretStore(Path.Combine(dir.Path, "s.json")), clock, iterations: 1000);
        auth.SetPin("246810");
        for (var i = 0; i < 4; i++) await auth.TryPinAsync("111111");
        auth.HelloSucceeded();
        Assert.Equal(UnlockOutcome.Wrong, (await auth.TryPinAsync("111111")).Outcome);
    }
}
