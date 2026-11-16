using Microsoft.Win32;

namespace Latch.Host;

/// <summary>Start at sign-in through the per-user Run key. No admin rights, no service.</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Latch";

    public static void Set(bool on)
    {
        if (Environment.GetEnvironmentVariable("LATCH_DATA_DIR") is { Length: > 0 }) return; // Dev instances never register.
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (on) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch (Exception e)
        {
            Log.Error("run key", e);
        }
    }

    public static void Remove() => Set(false);
}
