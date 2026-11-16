namespace Latch.Core.Security;

/// <summary>
/// Wrong-guess backoff: the first 4 misses in a row are free, the 5th starts a 30 s wait, and each further miss
/// doubles it, capped at 15 minutes.
/// </summary>
public static class Lockout
{
    public const int FreeAttempts = 4;
    public static readonly TimeSpan First = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Cap = TimeSpan.FromMinutes(15);

    /// <summary>The wait after the <paramref name="failedCount"/>-th miss in a row, or zero.</summary>
    public static TimeSpan WaitAfter(int failedCount)
    {
        if (failedCount <= FreeAttempts) return TimeSpan.Zero;
        var doublings = Math.Min(failedCount - FreeAttempts - 1, 10);
        var wait = First * Math.Pow(2, doublings);
        return wait > Cap ? Cap : wait;
    }
}
