using Latch.Core.Contract;
using Latch.Core.Locking;
using Latch.Core.Model;
using Latch.Core.Security;
using Latch.Core.Storage;

namespace Latch.Core;

/// <summary>
/// The core of Latch: rules, the lock engine, unlocking, history and settings, behind the <see cref="ILatchApi"/>
/// contract. One lock guards all state; effects are handed to the host after the lock is released.
/// </summary>
public sealed class LatchService : ILatchApi
{
    private const int MaxDismissed = 100;
    private static readonly TimeSpan AttentionWindow = TimeSpan.FromDays(7);

    private readonly IPlatform _platform;
    private readonly TimeProvider _clock;
    private readonly SettingsStore _settingsStore;
    private readonly HistoryLog _history;
    private readonly Heartbeat _heartbeat;
    private readonly RuleMatcher _matcher;
    private readonly LockEngine _engine;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> _waiters = [];
    private readonly HashSet<string> _missing = [];
    private LatchSettings _settings;
    private bool _helloAvailable;
    private bool _passkeyAvailable;
    private bool _hotkeyTaken;

    public LatchService(DataPaths paths, IPlatform platform, TimeProvider clock, int pinIterations = PinHasher.DefaultIterations)
    {
        _platform = platform;
        _clock = clock;
        _settingsStore = new SettingsStore(paths.Settings);
        _history = new HistoryLog(paths.History);
        _heartbeat = new Heartbeat(paths.Heartbeat);
        _settings = _settingsStore.Load();
        Auth = new AuthService(new SecretStore(paths.Secrets), clock, pinIterations);
        _matcher = new RuleMatcher(platform.Inspector);
        _engine = new LockEngine(clock, () =>
        {
            var s = _settings;
            return new RelockPolicy(s.Grace, s.RelockOnClose, s.IdleTimeout);
        });
    }

    public AuthService Auth { get; }

    public LatchSettings Settings => _settings;

    public bool SetupDone => _settings.SetupDone;

    /// <summary>True when opening Latch or quitting needs an unlock.</summary>
    public bool Guarded => _settings.SetupDone && Auth.PinSet;

    public bool NeedsTimer { get { lock (_gate) return _engine.NeedsTimer; } }

    // ---- Lifecycle ------------------------------------------------------------------------------------------------

    /// <summary>Checks the heartbeat gap, logs the start and checks rules for uninstalled apps.</summary>
    public void Start(DateTimeOffset bootTime)
    {
        var now = _clock.GetUtcNow();
        if (Heartbeat.Gap(_heartbeat.Read(), bootTime, now) is { } gap)
            _history.Append(new HistoryEntry(gap.To, HistoryKind.LatchNotRunning, null, null, gap.From));
        _history.Append(new HistoryEntry(now, HistoryKind.LatchStarted, null, null, null));
        _heartbeat.Beat(now);
        _hotkeyTaken = !_platform.SetLockAllHotkey(_settings.LockAllHotkey);
        _ = Task.Run(async () =>
        {
            _helloAvailable = await _platform.IsHelloAvailableAsync();
            _passkeyAvailable = _platform.PasskeysAvailable;
            CheckMissing();
            Flush([new Effect(EffectKind.StateChanged)]);
        });
    }

    public void Beat() => _heartbeat.Beat(_clock.GetUtcNow());

    /// <summary>Windows is signing out or shutting down: not suspicious, so mark the exit clean.</summary>
    public void SessionEnding() => _heartbeat.Beat(_clock.GetUtcNow(), cleanExit: true);

    /// <summary>Asks for an unlock to quit. Returns true when Latch should exit now.</summary>
    public async Task<bool> RequestQuitAsync()
    {
        if (Guarded && !await RequestUnlock(new PurposeArgs(PromptPurpose.Quit))) return false;
        lock (_gate) _engine.ConsumeGrant(PromptPurpose.Quit);
        var now = _clock.GetUtcNow();
        _history.Append(new HistoryEntry(now, HistoryKind.LatchQuit, null, null, null));
        _heartbeat.Beat(now, cleanExit: true);
        return true;
    }

    // ---- Events from the host -------------------------------------------------------------------------------------

    /// <summary>
    /// A top-level window appeared or came to the front. Returns the locked app's rule id when the host must hide
    /// the window now, or null.
    /// </summary>
    public string? OnWindow(nint hwnd, bool fromScan)
    {
        var rules = _settings.Rules;
        if (rules.Count == 0) return null;
        var facts = _platform.Inspector.Inspect(hwnd);
        if (facts is null) return null;
        var match = _matcher.Match(facts, rules);
        if (match.Kind != MatchKind.Match || match.Rule is null) return null;
        var fx = new List<Effect>();
        bool hide;
        lock (_gate)
        {
            if (!_settings.Rules.Contains(match.Rule)) return null;
            hide = _engine.OnWindow(match.Rule.Id, hwnd, match.AppProcessId, facts.ProcessStarted, match.Hide, fromScan, fx);
        }
        Flush(fx);
        return hide ? match.Rule.Id : null;
    }

    /// <summary>The name and icon key the cover shows for a locked app.</summary>
    public (string Name, string? IconKey)? AppLabel(string ruleId) =>
        _settings.Rules.FirstOrDefault(r => r.Id == ruleId) is { } r ? (r.Name, r.IconKey) : null;

    public string? RuleOfPrompt(string promptId)
    {
        lock (_gate) return _engine.FindPrompt(promptId)?.RuleId;
    }

    /// <summary>Apps with an unlock prompt on screen or waiting in line.</summary>
    public IReadOnlySet<string> AppsAwaitingUnlock()
    {
        lock (_gate) return _engine.PromptRules();
    }

    public void OnWindowsHidden(string ruleId, IEnumerable<nint> windows)
    {
        lock (_gate) _engine.AddHidden(ruleId, windows);
    }

    public void OnProcessExited(int pid) => Run(fx => _engine.OnProcessExited(pid, fx));

    /// <summary>For a window that just went away: the unlocked app it belonged to, so the host can check whether
    /// that app still has any window on screen.</summary>
    public (string RuleId, IReadOnlyList<int> Pids)? UnlockedAppOf(int pid)
    {
        lock (_gate) return _engine.UnlockedAppOf(pid);
    }

    /// <summary>The app is still running but has no window on screen any more: treat it as closed.</summary>
    public void OnAppWindowsGone(string ruleId) => Run(fx => _engine.OnAppWindowsGone(ruleId, fx));

    public void Tick(TimeSpan idle) => Run(fx => _engine.Tick(idle, fx));

    public void RelockAll() => Run(_engine.RelockAll);

    /// <summary>The owner asked to open the main window. True when it may open right away.</summary>
    public bool RequestOpenLatch()
    {
        var fx = new List<Effect>();
        bool open;
        lock (_gate) open = _engine.RequestOpenLatch(Guarded, fx);
        Flush(fx);
        return open;
    }

    public void MainWindowClosed() => Run(_engine.MainWindowClosed);

    public bool TrayHintShown => _settings.TrayHintShown;

    public void MarkTrayHintShown() => ChangeSettings(s => s with { TrayHintShown = true }, push: false);

    // ---- Snapshots ------------------------------------------------------------------------------------------------

    /// <summary>The main-window state. Reads memory only.</summary>
    public AppSnapshot Snapshot()
    {
        lock (_gate)
        {
            var s = _settings;
            var rows = s.Rules
                .Select(r =>
                {
                    var status = _engine.StatusOf(r.Id);
                    var relocks = status == AppStatus.Grace ? _engine.Entry(r.Id).GraceUntil : null;
                    return new AppRow(r.Id, r.Name, _platform.IconUrl(r.IconKey), status, relocks);
                })
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return new AppSnapshot(
                s.SetupDone,
                rows,
                Attention(s),
                new SettingsView(s.GraceMinutes, s.RelockOnClose, s.IdleMinutes, s.StartWithWindows, s.Theme, s.LockAllHotkey, s.LockAllHotkey && _hotkeyTaken),
                new MethodsView(Auth.PinSet, _helloAvailable, Auth.HelloOn, _passkeyAvailable, Auth.PasskeyOn),
                _history.Revision,
                _platform.Version);
        }
    }

    public PromptView? CurrentPrompt()
    {
        lock (_gate)
        {
            var req = _engine.ActivePrompt;
            if (req is null) return null;
            var rule = req.RuleId is null ? null : _settings.Rules.FirstOrDefault(r => r.Id == req.RuleId);
            return new PromptView(
                req.Id,
                req.Purpose,
                rule?.Name ?? Copy.Get("app.name"),
                rule is null ? null : _platform.IconDataUrl(rule.IconKey),
                Auth.HelloOn,
                Auth.PasskeyOn,
                Auth.LockoutUntil,
                _settings.Theme);
        }
    }

    private List<AttentionItem> Attention(LatchSettings s)
    {
        var items = new List<AttentionItem>();
        var since = _clock.GetUtcNow() - AttentionWindow;
        var recent = _history.Revision > 0 ? RecentProblems(since) : [];
        foreach (var e in recent)
        {
            var id = $"{e.Kind}:{e.At.ToUnixTimeSeconds()}";
            if (s.Dismissed.Contains(id)) continue;
            items.Add(e.Kind == HistoryKind.LatchNotRunning
                ? new AttentionItem(id, AttentionKind.NotRunning, null, null, e.At, e.From)
                : new AttentionItem(id, AttentionKind.Lockout, null, e.App, e.At, null));
        }
        foreach (var r in s.Rules.Where(r => _missing.Contains(r.Id)))
            items.Add(new AttentionItem("missing:" + r.Id, AttentionKind.AppMissing, r.Id, r.Name, _clock.GetUtcNow(), null));
        return items;
    }

    private List<HistoryEntry> _problemCache = [];
    private long _problemRevision = -1;

    /// <summary>The latest "wasn't running" and the latest lockout from the last week (history is cached in memory).</summary>
    private List<HistoryEntry> RecentProblems(DateTimeOffset since)
    {
        if (_problemRevision != _history.Revision)
        {
            var all = _history.ReadAll();
            _problemCache = new[] { HistoryKind.LatchNotRunning, HistoryKind.LockoutStarted }
                .Select(k => all.LastOrDefault(e => e.Kind == k))
                .OfType<HistoryEntry>()
                .ToList();
            _problemRevision = _history.Revision;
        }
        return _problemCache.Where(e => e.At >= since).ToList();
    }

    // ---- API: main window -----------------------------------------------------------------------------------------

    public Task<AppSnapshot> GetState(Empty args) => Task.FromResult(Snapshot());

    public Task<Empty> WindowReady(Empty args)
    {
        _platform.WindowReady();
        return Done;
    }

    public Task<Empty> SetPin(PinArgs args)
    {
        if (!PinHasher.IsValidFormat(args.Pin)) throw new ApiException(ErrorCode.PinFormat);
        RequireGrantIfGuarded(PromptPurpose.ChangePin);
        Auth.SetPin(args.Pin);
        Push();
        return Done;
    }

    public async Task<bool> TurnOnHello(Empty args)
    {
        if (!await _platform.IsHelloAvailableAsync()) throw new ApiException(ErrorCode.HelloUnavailable);
        if (!await _platform.VerifyHelloAsync(false, Copy.Get("settings.hello"))) return false;
        Auth.SetHello(true);
        Push();
        return true;
    }

    public Task<Empty> TurnOffHello(Empty args)
    {
        RequireGrantIfGuarded(PromptPurpose.TurnOffHello);
        Auth.SetHello(false);
        Push();
        return Done;
    }

    /// <summary>Registers a phone or security key. False when the owner cancelled the system dialog.</summary>
    public async Task<bool> AddPasskey(Empty args)
    {
        if (!_platform.PasskeysAvailable) throw new ApiException(ErrorCode.PasskeyUnavailable);
        var clientData = Passkeys.ClientData("webauthn.create", Passkeys.NewChallenge());
        var creation = await _platform.CreatePasskeyAsync(clientData, Passkeys.RpId, Copy.Get("app.name"));
        if (creation is null) return false;
        var credential = Passkeys.FromRegistration(creation) ?? throw new ApiException(ErrorCode.PasskeyFailed);
        Auth.SavePasskey(credential);
        Push();
        return true;
    }

    public Task<Empty> RemovePasskey(Empty args)
    {
        RequireGrantIfGuarded(PromptPurpose.RemovePasskey);
        Auth.RemovePasskey();
        Push();
        return Done;
    }

    public Task<Empty> FinishSetup(Empty args)
    {
        if (!Auth.PinSet) throw new ApiException(ErrorCode.PinFormat);
        ChangeSettings(s => s with { SetupDone = true });
        _platform.SetRunAtStartup(_settings.StartWithWindows);
        Run(fx => _engine.RequestOpenLatch(false, fx));
        Flush([new Effect(EffectKind.ScanWindows)]);
        return Done;
    }

    public async Task<IReadOnlyList<InstalledApp>> ListInstalledApps(Empty args)
    {
        var apps = await _platform.ListInstalledAppsAsync();
        var locked = _settings.Rules.Select(r => r.Id).ToHashSet();
        return apps.Select(a => a with { Locked = locked.Contains(a.Key) }).ToList();
    }

    public Task<InstalledApp?> BrowseForProgram(Empty args) => _platform.BrowseForProgramAsync();

    public Task<Empty> SetLockedApps(SetLockedAppsArgs args)
    {
        var selected = args.Apps.GroupBy(a => a.Key).Select(g => g.First()).ToList();
        var current = _settings.Rules;
        var added = selected.Where(a => current.All(r => r.Id != a.Key)).Select(ToRule).OfType<LockRule>().ToList();
        var keep = current.Where(r => selected.Any(a => a.Key == r.Id)).ToList();
        var removed = current.Where(r => !keep.Contains(r)).ToList();
        ChangeSettings(s => s with { Rules = keep.Concat(added).ToList() });
        Run(fx => removed.ForEach(r => _engine.RuleRemoved(r.Id, fx)));
        CheckMissingLater();
        return Done;
    }

    public Task<Empty> RemoveApp(IdArgs args)
    {
        if (_settings.Rules.All(r => r.Id != args.Id)) throw new ApiException(ErrorCode.NotFound);
        ChangeSettings(s => s with { Rules = s.Rules.Where(r => r.Id != args.Id).ToList() });
        Run(fx => _engine.RuleRemoved(args.Id, fx));
        lock (_gate) _missing.Remove(args.Id);
        Push();
        return Done;
    }

    public Task<Empty> ShowApp(IdArgs args)
    {
        Run(fx => _engine.RequestShow(args.Id, fx));
        return Done;
    }

    public Task<IReadOnlyList<HistoryEntry>> GetHistory(Empty args) =>
        Task.Run<IReadOnlyList<HistoryEntry>>(() => _history.ReadAll().Reverse().ToList());

    public Task<Empty> DismissAttention(IdArgs args)
    {
        ChangeSettings(s => s with { Dismissed = s.Dismissed.Append(args.Id).TakeLast(MaxDismissed).ToList() });
        return Done;
    }

    public Task<Empty> UpdateSettings(SettingsPatch args)
    {
        if (args.StartWithWindows == false && _settings.StartWithWindows) RequireGrantIfGuarded(PromptPurpose.TurnOffStartup);
        ChangeSettings(s => s with
        {
            GraceMinutes = args.GraceMinutes is { } g && LatchSettings.GraceChoices.Contains(g) ? g : s.GraceMinutes,
            RelockOnClose = args.RelockOnClose ?? s.RelockOnClose,
            IdleMinutes = args.IdleMinutes is { } i && LatchSettings.IdleChoices.Contains(i) ? i : s.IdleMinutes,
            StartWithWindows = args.StartWithWindows ?? s.StartWithWindows,
            Theme = args.Theme ?? s.Theme,
            LockAllHotkey = args.LockAllHotkey ?? s.LockAllHotkey,
        });
        if (args.LockAllHotkey is { } hotkey)
        {
            _hotkeyTaken = !_platform.SetLockAllHotkey(hotkey);
            Push();
        }
        if (args.StartWithWindows is { } on && _settings.SetupDone) _platform.SetRunAtStartup(on);
        return Done;
    }

    public Task<bool> RequestUnlock(PurposeArgs args)
    {
        if (args.Purpose is PromptPurpose.App or PromptPurpose.OpenLatch) throw new ApiException(ErrorCode.Unexpected);
        if (!Guarded) return Task.FromResult(true);
        var fx = new List<Effect>();
        TaskCompletionSource<bool> tcs;
        lock (_gate)
        {
            var id = _engine.RequestSensitive(args.Purpose, fx);
            if (!_waiters.TryGetValue(id, out tcs!))
                _waiters[id] = tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Flush(fx);
        return tcs.Task;
    }

    // ---- API: prompt ----------------------------------------------------------------------------------------------

    public async Task<UnlockResult> SubmitPin(PromptPinArgs args)
    {
        if (Prompt(args.PromptId) is not { } req) return new UnlockResult(UnlockOutcome.Gone, null);
        var result = await Auth.TryPinAsync(args.Pin);
        return Resolve(req, result, UnlockMethod.Pin);
    }

    public async Task<UnlockResult> UsePasskey(PromptIdArgs args)
    {
        if (Prompt(args.PromptId) is not { } req) return new UnlockResult(UnlockOutcome.Gone, null);
        if (Auth.Passkey is not { } credential) return new UnlockResult(UnlockOutcome.NotVerified, Auth.LockoutUntil);
        var clientData = Passkeys.ClientData("webauthn.get", Passkeys.NewChallenge());
        var assertion = await _platform.GetPasskeyAssertionAsync(clientData, Passkeys.RpId, credential.Id);
        if (assertion is null) return new UnlockResult(UnlockOutcome.NotVerified, Auth.LockoutUntil);
        if (Prompt(args.PromptId) is null) return new UnlockResult(UnlockOutcome.Gone, null);
        return Resolve(req, Auth.TryPasskey(clientData, assertion), UnlockMethod.Passkey);
    }

    public async Task<UnlockResult> UseHello(PromptIdArgs args)
    {
        if (Prompt(args.PromptId) is not { } req) return new UnlockResult(UnlockOutcome.Gone, null);
        if (!Auth.HelloOn) return new UnlockResult(UnlockOutcome.NotVerified, null);
        var name = CurrentPrompt()?.AppName ?? Copy.Get("app.name");
        var ok = await _platform.VerifyHelloAsync(true, Copy.Format("prompt.helloReason", ("app", name)));
        if (!ok) return new UnlockResult(UnlockOutcome.NotVerified, Auth.LockoutUntil);
        return Resolve(req, Auth.HelloSucceeded(), UnlockMethod.Hello);
    }

    /// <summary>The cover's close button: the same as `Not now` on that app's prompt, whether it's on screen or waiting.</summary>
    public void DismissApp(string ruleId)
    {
        PromptRequest? req;
        lock (_gate) req = _engine.PromptForApp(ruleId);
        if (req is not null) Dismiss(req);
    }

    public Task<Empty> CancelPrompt(PromptIdArgs args)
    {
        if (Prompt(args.PromptId) is not { } req) return Done;
        Dismiss(req);
        return Done;
    }

    private void Dismiss(PromptRequest req)
    {
        _history.Append(new HistoryEntry(_clock.GetUtcNow(), HistoryKind.PromptCancelled, AppName(req), null, null));
        Run(fx => _engine.OnCancelled(req.Id, fx));
        Complete(req.Id, false);
    }

    public Task<Empty> PromptPainted(PromptPaintedArgs args)
    {
        _platform.PromptPainted(args.PromptId, args.Height);
        return Done;
    }

    private UnlockResult Resolve(PromptRequest req, AttemptResult result, UnlockMethod method)
    {
        var now = _clock.GetUtcNow();
        var app = AppName(req);
        switch (result.Outcome)
        {
            case UnlockOutcome.Ok:
                _history.Append(new HistoryEntry(now, HistoryKind.Unlocked, app, method, null));
                Run(fx => _engine.OnUnlocked(req.Id, fx));
                Complete(req.Id, true);
                break;
            case UnlockOutcome.Wrong:
                _history.Append(new HistoryEntry(now, HistoryKind.Failed, app, method, null));
                Push();
                break;
            case UnlockOutcome.LockedOut when result.LockoutStarted:
                _history.Append(new HistoryEntry(now, HistoryKind.Failed, app, method, null));
                _history.Append(new HistoryEntry(now, HistoryKind.LockoutStarted, app, method, null));
                Push();
                break;
        }
        return new UnlockResult(result.Outcome, result.LockoutUntil);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private static Task<Empty> Done => Task.FromResult(Empty.Value);

    private PromptRequest? Prompt(string promptId)
    {
        lock (_gate) return _engine.ActivePrompt?.Id == promptId ? _engine.ActivePrompt : null;
    }

    private string AppName(PromptRequest req)
    {
        lock (_gate) return _settings.Rules.FirstOrDefault(r => r.Id == req.RuleId)?.Name ?? Copy.Get("app.name");
    }

    private void Complete(string promptId, bool ok)
    {
        TaskCompletionSource<bool>? tcs;
        lock (_gate) _waiters.Remove(promptId, out tcs);
        tcs?.TrySetResult(ok);
    }

    private void RequireGrantIfGuarded(PromptPurpose purpose)
    {
        if (!Guarded) return;
        bool ok;
        lock (_gate) ok = _engine.ConsumeGrant(purpose);
        if (!ok) throw new ApiException(ErrorCode.NeedsUnlock);
    }

    private LockRule? ToRule(InstalledApp app)
    {
        if (app.Kind == RuleKind.Package && app.PackageFamilyName is { } pfn)
            return new LockRule(app.Key, RuleKind.Package, app.Name, null, pfn, app.Aumid, app.Key, null, null);
        if (app.Kind == RuleKind.Exe && app.Path is { } path)
        {
            // Store the signer and product name so a copied or renamed exe still matches.
            string? publisher = null, product = null;
            try
            {
                product = _platform.Inspector.ProductName(path);
                publisher = product is null ? null : _platform.Inspector.Publisher(path);
            }
            catch (Exception)
            {
                // Path-only matching still works.
            }
            return new LockRule(app.Key, RuleKind.Exe, app.Name, path, null, null, app.Key, publisher, product);
        }
        return null;
    }

    private void ChangeSettings(Func<LatchSettings, LatchSettings> change, bool push = true)
    {
        LatchSettings next;
        lock (_gate)
        {
            next = change(_settings);
            _settings = next;
        }
        _settingsStore.Save(next);
        if (push) Push();
    }

    private void CheckMissingLater() => Task.Run(() =>
    {
        CheckMissing();
        Push();
    });

    private void CheckMissing()
    {
        foreach (var rule in _settings.Rules)
        {
            bool exists;
            try
            {
                exists = _platform.TargetExists(rule);
            }
            catch (Exception)
            {
                exists = true;
            }
            lock (_gate)
            {
                if (exists) _missing.Remove(rule.Id);
                else _missing.Add(rule.Id);
            }
        }
    }

    private void Run(Action<List<Effect>> action)
    {
        var fx = new List<Effect>();
        lock (_gate) action(fx);
        Flush(fx);
    }

    private void Push() => Flush([new Effect(EffectKind.StateChanged)]);

    private void Flush(List<Effect> fx)
    {
        if (fx.Count > 0) _platform.Apply(fx);
    }
}
