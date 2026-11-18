using Velopack;
using Velopack.Sources;

namespace Latch.Host;

/// <summary>
/// Automatic updates from Latch's GitHub releases, through Velopack. A minute after start and every 6 hours it
/// checks for a newer release and downloads it quietly. A downloaded update installs the next time Latch starts
/// (Velopack applies it at startup, and Latch starts with Windows), or at once from the tray's "Restart to update".
/// Only for installed copies: a development build has nothing to update.
/// </summary>
internal sealed class Updater(Action<string> ready)
{
    private const string Repository = "https://github.com/Alexciao/latch";

    private readonly UpdateManager _manager = new(new GithubSource(Repository, null, false));
    private VelopackAsset? _pending;
    private Timer? _timer;
    private int _checking;

    /// <summary>The version waiting to be installed, if one has been downloaded.</summary>
    public string? Pending => _pending?.Version.ToString();

    public void Start()
    {
        if (!_manager.IsInstalled) return;
        _pending = _manager.UpdatePendingRestart; // Downloaded before, not installed yet.
        if (Pending is { } version) ready(version);
        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
    }

    private async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null || info.TargetFullRelease.Version == _pending?.Version) return;
            await _manager.DownloadUpdatesAsync(info);
            _pending = info.TargetFullRelease;
            Log.Info($"Update {Pending} downloaded; it installs the next time Latch starts");
            ready(Pending!);
        }
        catch (Exception e)
        {
            Log.Info("Update check failed: " + e.Message); // Offline, GitHub down: try again next time.
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>Installs the downloaded update and starts Latch again. Ends this process.</summary>
    public void InstallAndRestart()
    {
        if (_pending is { } update) _manager.ApplyUpdatesAndRestart(update, ["--background"]);
    }
}
