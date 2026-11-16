using System.Text.Json;
using Latch.Core.Contract;
using Latch.Core.Model;

namespace Latch.Core.Storage;

/// <summary>
/// Detects that Latch was ended mid-session. Latch writes a heartbeat every minute and marks a clean exit when it
/// quits on purpose or Windows signs out. On start, a heartbeat newer than this boot with no clean exit means
/// Latch was ended while the session went on.
/// </summary>
public sealed class Heartbeat(string path)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>The gap to report, or null. Pure, so it can be tested with any boot time.</summary>
    public static (DateTimeOffset From, DateTimeOffset To)? Gap(HeartbeatFile? last, DateTimeOffset bootTime, DateTimeOffset now)
    {
        if (last is null || last.CleanExit) return null;
        if (last.Last <= bootTime) return null; // The gap spans a reboot: a normal shutdown isn't suspicious.
        if (last.Last > now) return null;       // Clock went backwards; nothing honest to say.
        return (last.Last, now);
    }

    public HeartbeatFile? Read() =>
        AtomicFile.Read(path, bytes => JsonSerializer.Deserialize(bytes, ContractJson.Default.HeartbeatFile));

    public void Beat(DateTimeOffset now, bool cleanExit = false) =>
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(new HeartbeatFile(now, cleanExit), ContractJson.Default.HeartbeatFile));
}
