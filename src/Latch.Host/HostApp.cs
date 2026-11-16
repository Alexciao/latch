using System.Windows;
using System.Windows.Threading;
using Latch.Core;
using Latch.Core.Contract;
using Latch.Core.Locking;
using Latch.Core.Model;
using Latch.Core.Security;
using Latch.Core.Storage;
using Microsoft.Win32;

namespace Latch.Host;

/// <summary>
/// Composition root and the host side of <see cref="IPlatform"/>: platform glue only. Every decision is the core's;
/// this class turns engine effects into window, tray and timer operations.
/// </summary>
internal sealed class HostApp : IPlatform
{
    private static readonly TimeSpan RelockTick = TimeSpan.FromSeconds(15);

    private readonly bool _background;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DataPaths _paths = DataPaths.Default();
    private readonly WindowInspector _inspector = new();
    private readonly AppsCatalog _catalog;
    private LatchService _service = null!;
    private Rpc _rpc = null!;
    private WindowWatcher _watcher = null!;
    private PromptHost _prompt = null!;
    private MainHost _main = null!;
    private TrayIcon? _tray;
    private Toast _toast = null!;
    private DispatcherTimer? _relockTimer;
    private DispatcherTimer? _heartbeatTimer;
    private bool _statePushQueued;
    private bool _quitting;

    public HostApp(bool background)
    {
        _background = background;
        _catalog = new AppsCatalog(_paths);
    }

    public IWindowInspector Inspector => _inspector;

    public string Version { get; } = typeof(HostApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public void Start()
    {
        Directory.CreateDirectory(_paths.Root);
        Log.Init(_paths.Log);
        Log.Info($"Latch {Version} starting (background: {_background}, Windows {Environment.OSVersion.Version})");

        _service = new LatchService(_paths, this, TimeProvider.System);
        _rpc = new Rpc(_service, (method, e) => Log.Error("api " + method, e));
        _watcher = new WindowWatcher(_service, () => Theme.IsDark(_service.Settings.Theme), _paths.Icons, () => _ui.BeginInvoke(() => _prompt.FocusFromCover()), ruleId => _ui.BeginInvoke(() => _service.DismissApp(ruleId)));
        _prompt = new PromptHost(_paths, _rpc, () => _service.Settings.Theme, PromptAnchor, () => _watcher.LastHiddenAt, PromptArea, PromptCover);
        _prompt.DialogStacking += _watcher.RestackCovers;
        _main = new MainHost(_paths, _rpc, () => _service.Settings.Theme, () =>
        {
            _service.MainWindowClosed();
            TrimSoon();
        });

        var boot = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        _toast = new Toast(() => Theme.IsDark(_service.Settings.Theme));
        _tray = new TrayIcon(OpenMain, LockAll, Quit);
        _tray.HotkeyPressed += LockAll;
        _service.Start(boot);
        _watcher.Start();
        _watcher.Scan();

        _heartbeatTimer = new DispatcherTimer(Heartbeat.Interval, DispatcherPriority.Background, (_, _) => _service.Beat(), _ui);
        _heartbeatTimer.Start();
        _relockTimer = new DispatcherTimer(RelockTick, DispatcherPriority.Background, (_, _) => OnRelockTick(), _ui) { IsEnabled = false };

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General) _ui.BeginInvoke(() => _main.ApplyTheme(_service.Settings.Theme));
        };

        // Starting Latch doesn't open its window (that needs an unlock); it says it's running. Only first run opens it.
        if (!_service.SetupDone) OpenMain();
        else if (!_background || !_service.TrayHintShown) ShowRunning();

        // Pre-warm the prompt once startup has settled; a cold web view can't paint in 250 ms.
        _ = Task.Delay(2000).ContinueWith(async _ =>
        {
            await _prompt.PrewarmAsync();
            TrimSoon();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        _ = Task.Run(() => _catalog.Preload(_service.Settings.Rules));
        UpdateRelockTimer();
    }

    /// <summary>Cost nothing when hidden: once the window is gone, compact the heap and hand pages back to Windows.</summary>
    private void TrimSoon() => _ = Task.Delay(3000).ContinueWith(_ =>
    {
        if (_main.IsOpen) return;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), -1, -1);
    }, TaskScheduler.Default);

    /// <summary>A second launch of Latch asks this instance to open its window.</summary>
    /// <summary>Launching Latch again while it runs: say so instead of asking for the PIN.</summary>
    public void OnSecondInstance() => _ui.BeginInvoke(() =>
    {
        if (!_service.SetupDone || _main.IsOpen) OpenMain();
        else ShowRunning();
    });

    private void ShowRunning()
    {
        _toast.Show(Copy.Get("toast.runningTitle"), Copy.Get("toast.runningCaption"));
        if (!_service.TrayHintShown) _service.MarkTrayHintShown();
    }

    private void LockAll()
    {
        _service.RelockAll();
        _toast.Show(Copy.Get("toast.lockedAll"));
    }

    public void SessionEnding()
    {
        _service.SessionEnding();
        Log.Info("Session ending");
    }

    private void OpenMain()
    {
        if (_main.IsOpen || _service.RequestOpenLatch()) _main.Open();
    }

    private async void Quit()
    {
        if (_quitting) return;
        if (!await _service.RequestQuitAsync()) return;
        _quitting = true;
        Log.Info("Quit from the tray");
        _watcher.RestoreAll();
        _watcher.Stop();
        _tray?.Dispose();
        Application.Current.Shutdown();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
        {
            Log.Info("Relock: " + e.Reason);
            _service.RelockAll();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            Log.Info("Relock: sleep");
            _service.RelockAll();
        }
    }

    private void OnRelockTick()
    {
        var info = new Native.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.LASTINPUTINFO>() };
        var idle = Native.GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds((uint)Environment.TickCount - info.dwTime) : TimeSpan.Zero;
        _service.Tick(idle);
        UpdateRelockTimer();
    }

    /// <summary>The idle and grace timer runs only while something is unlocked; otherwise Latch is fully idle.</summary>
    private void UpdateRelockTimer()
    {
        if (_relockTimer is null) return;
        var need = _service.NeedsTimer;
        if (need != _relockTimer.IsEnabled) _relockTimer.IsEnabled = need;
    }

    // ---- IPlatform ------------------------------------------------------------------------------------------------

    public void Apply(IReadOnlyList<Effect> effects)
    {
        // Engine effects can come from the hook thread or the thread pool; the UI thread carries them out.
        _ui.BeginInvoke(() =>
        {
            foreach (var e in effects)
            {
                try
                {
                    Do(e);
                }
                catch (Exception ex)
                {
                    Log.Error("effect " + e.Kind, ex);
                }
            }
            UpdateRelockTimer();
            DropFinishedCovers(effects);
        });
    }

    /// <summary>
    /// A cover lasts exactly as long as its app's prompt (on screen or waiting in line). After an unlock it waits a
    /// moment so the app's own windows are back before it goes; otherwise it goes at once.
    /// </summary>
    private void DropFinishedCovers(IReadOnlyList<Effect> effects)
    {
        if (!effects.Any(e => e.Kind is EffectKind.PromptChanged or EffectKind.StateChanged)) return;
        var waiting = _service.AppsAwaitingUnlock();
        foreach (var ruleId in _watcher.CoveredApps())
        {
            if (waiting.Contains(ruleId)) continue;
            var restored = effects.Any(e => e.Kind == EffectKind.RestoreWindows && e.RuleId == ruleId);
            _watcher.RemoveCover(ruleId, restored ? 150 : 0);
        }
    }

    /// <summary>The cover of the app whose prompt is active (for the spinner shown while Hello starts).</summary>
    private (nint Hwnd, Native.RECT Area)? PromptCover() =>
        _service.CurrentPrompt() is { Purpose: PromptPurpose.App } p && _service.RuleOfPrompt(p.PromptId) is { } ruleId
            && _watcher.CoverWindow(ruleId) is var hwnd && hwnd != 0 && _watcher.CoverArea(ruleId) is { } area
            ? (hwnd, area)
            : null;

    /// <summary>The covered area of the app whose prompt is active, so the prompt centres on it.</summary>
    private Native.RECT? PromptArea() =>
        _service.CurrentPrompt() is { Purpose: PromptPurpose.App } p && _service.RuleOfPrompt(p.PromptId) is { } ruleId
            ? _watcher.CoverArea(ruleId)
            : null;

    private void Do(Effect e)
    {
        switch (e.Kind)
        {
            case EffectKind.HideApp:
                _watcher.HideApp(e.RuleId!, e.Pids ?? []);
                break;
            case EffectKind.RestoreWindows:
                _watcher.Restore(e.Windows ?? []);
                break;
            case EffectKind.CloseApp:
                _watcher.CloseApp(e.Windows ?? [], e.Pids ?? []);
                break;
            case EffectKind.PromptChanged:
                _prompt.Update(_service.CurrentPrompt());
                break;
            case EffectKind.StateChanged:
                QueueStatePush();
                break;
            case EffectKind.ScanWindows:
                _watcher.Scan();
                break;
            case EffectKind.TrackProcess:
                _watcher.Track(e.Pids ?? []);
                break;
            case EffectKind.OpenMainWindow:
                _main.Open();
                break;
            case EffectKind.CloseMainWindow:
                _main.Close();
                break;
        }
    }

    /// <summary>Coalesces snapshot pushes to at most about 30 per second.</summary>
    private void QueueStatePush()
    {
        if (_statePushQueued || !_main.IsOpen) return;
        _statePushQueued = true;
        _ = Task.Delay(33).ContinueWith(_ =>
        {
            _statePushQueued = false;
            var snapshot = _service.Snapshot();
            _main.Post(Rpc.StateEvent(snapshot));
            _main.ApplyTheme(snapshot.Settings.Theme);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public Task<IReadOnlyList<InstalledApp>> ListInstalledAppsAsync() => _catalog.ListAsync();

    public async Task<InstalledApp?> BrowseForProgramAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Programs (*.exe)|*.exe",
            CheckFileExists = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.Topmost == false);
        if (dialog.ShowDialog(owner) != true) return null;
        if (!dialog.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ApiException(ErrorCode.NotAProgram);
        return await _catalog.DescribeExeAsync(dialog.FileName);
    }

    public Task<bool> IsHelloAvailableAsync() => Hello.IsAvailableAsync();

    public Task<bool> VerifyHelloAsync(bool overPrompt, string reason) => OnUi(async () =>
    {
        if (overPrompt && _prompt.Hwnd != 0) return await _prompt.OverSystemDialog(owner => Hello.VerifyAsync(owner, reason));
        return await Hello.VerifyAsync(_main.Hwnd, reason);
    });

    public bool SetLockAllHotkey(bool on) => _tray?.SetHotkey(on) ?? true;

    public bool PasskeysAvailable { get; } = WebAuthn.Available;

    public Task<PasskeyCreation?> CreatePasskeyAsync(byte[] clientData, string rpId, string rpName) => OnUi(() =>
    {
        var hwnd = _main.Hwnd;
        return Task.Run(() => WebAuthn.Create(hwnd, clientData, rpId, rpName));
    });

    public Task<PasskeyAssertion?> GetPasskeyAssertionAsync(byte[] clientData, string rpId, byte[] credentialId) => OnUi(async () =>
    {
        return await _prompt.OverSystemDialog(owner => Task.Run(() => WebAuthn.Get(owner, clientData, rpId, credentialId)));
    });

    private Task<T> OnUi<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.BeginInvoke(async () =>
        {
            try
            {
                tcs.TrySetResult(await work());
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        });
        return tcs.Task;
    }

    /// <summary>
    /// The window the prompt centres on: the locked app's window, or Latch's own window for Latch's prompts.
    /// Zero means the monitor under the cursor.
    /// </summary>
    private nint PromptAnchor()
    {
        var purpose = _service.CurrentPrompt()?.Purpose;
        if (purpose == PromptPurpose.App && _watcher.LastHidden != 0 && Native.IsWindow(_watcher.LastHidden)) return _watcher.LastHidden;
        if (_main.IsOpen && _main.Hwnd != 0) return _main.Hwnd;
        return 0;
    }

    public void SetRunAtStartup(bool on) => Startup.Set(on);

    public bool TargetExists(LockRule rule) => AppsCatalog.TargetExists(rule);

    public string? IconUrl(string? iconKey) => iconKey is null ? null : _catalog.IconUrl(iconKey);

    public string? IconDataUrl(string? iconKey) => _catalog.IconDataUrl(iconKey);

    public void WindowReady() => _ui.BeginInvoke(() =>
    {
        _main.Ready();
        QueueStatePush();
    });

    public void PromptPainted(string promptId, int height) => _ui.BeginInvoke(() => _prompt.Painted(promptId, height));
}
