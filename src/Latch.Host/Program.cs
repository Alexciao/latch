using System.Windows;
using Velopack;

namespace Latch.Host;

internal static class Program
{
    // A separate data folder (LATCH_DATA_DIR, for development) is a separate instance.
    private static readonly string Suffix = Environment.GetEnvironmentVariable("LATCH_DATA_DIR") is { Length: > 0 } d ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(d.ToUpperInvariant())))[..12] : "";
    private static readonly string InstanceMutex = @"Local\Latch.Instance" + Suffix;
    private static readonly string OpenEvent = @"Local\Latch.Open" + Suffix;

    [STAThread]
    private static int Main(string[] args)
    {
        // Velopack install/uninstall hooks run and exit here. On uninstall, stop starting with Windows.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Startup.Remove())
            .Run();

        using var mutex = new Mutex(true, InstanceMutex, out var first);
        if (!first)
        {
            // Latch is already running: ask it to open its window (it will ask for an unlock).
            if (EventWaitHandle.TryOpenExisting(OpenEvent, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var host = new HostApp(args.Contains("--background"));
        using var open = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEvent);
        ThreadPool.RegisterWaitForSingleObject(open, (_, _) => host.OnSecondInstance(), null, Timeout.Infinite, executeOnlyOnce: false);
        app.Startup += (_, _) => host.Start();
        app.SessionEnding += (_, _) => host.SessionEnding();
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("unhandled", e.Exception);
            e.Handled = true;
        };
        app.Run();
        return 0;
    }
}
