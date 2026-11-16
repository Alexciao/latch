using Latch.Core.Contract;

namespace Latch.Core.Locking;

public enum LockState
{
    /// <summary>Not unlocked. The next window of this app is hidden and a prompt appears.</summary>
    Locked,
    /// <summary>Windows hidden, prompt queued or on screen.</summary>
    Prompting,
    /// <summary>Relocked while running (idle, sleep, lock all) or prompt dismissed: hidden, no prompt until it tries to show again.</summary>
    Hidden,
    /// <summary>Prompt cancelled on a fresh launch: windows were asked to close; new windows stay hidden until it exits.</summary>
    Closing,
    Unlocked,
    /// <summary>Closed after an unlock; relaunching before <see cref="AppEntry.GraceUntil"/> doesn't ask again.</summary>
    Grace,
}

public sealed class AppEntry(string ruleId)
{
    public string RuleId { get; } = ruleId;
    public LockState State { get; set; } = LockState.Locked;
    public bool FreshLaunch { get; set; }

    /// <summary>The prompt began with no known process of the app, so a young process seen later makes it a fresh launch.</summary>
    public bool LaunchCandidate { get; set; }
    public DateTimeOffset? GraceUntil { get; set; }
    public HashSet<int> Pids { get; } = [];
    public List<nint> Hidden { get; } = [];
}

public sealed record PromptRequest(string Id, PromptPurpose Purpose, string? RuleId);

public enum EffectKind
{
    /// <summary>Hide every visible top-level window of the app now, then report them with <see cref="LockEngine.AddHidden"/>.</summary>
    HideApp,
    RestoreWindows,
    CloseApp,
    PromptChanged,
    StateChanged,
    ScanWindows,
    TrackProcess,
    OpenMainWindow,
    CloseMainWindow,
}

public sealed record Effect(EffectKind Kind, string? RuleId = null, IReadOnlyList<nint>? Windows = null, IReadOnlyList<int>? Pids = null);

/// <summary>How the engine reads the relock preferences, so it never holds a stale copy.</summary>
public sealed record RelockPolicy(TimeSpan Grace, bool RelockOnClose, TimeSpan? IdleTimeout);

/// <summary>
/// The lock state machine for every rule plus Latch itself. Not thread-safe: the owner serializes calls. Every
/// method appends what the host must do to <c>fx</c>; the engine never touches the OS.
/// </summary>
public sealed class LockEngine(TimeProvider clock, Func<RelockPolicy> policy)
{
    /// <summary>A window counts as a fresh launch when its process started this recently.</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, AppEntry> _apps = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _pidToRule = [];
    private readonly List<PromptRequest> _queue = [];
    private readonly Dictionary<PromptPurpose, DateTimeOffset> _grants = [];
    private int _nextPrompt;

    public bool LatchOpen { get; private set; }
    public DateTimeOffset? LatchGraceUntil { get; private set; }

    public PromptRequest? ActivePrompt => _queue.Count > 0 ? _queue[0] : null;

    public IReadOnlySet<string> PromptRules() => _queue.Select(r => r.RuleId).OfType<string>().ToHashSet();

    public AppEntry Entry(string ruleId)
    {
        if (!_apps.TryGetValue(ruleId, out var e)) _apps[ruleId] = e = new AppEntry(ruleId);
        return e;
    }

    public IEnumerable<AppEntry> Entries => _apps.Values;

    public string? RuleForPid(int pid) => _pidToRule.GetValueOrDefault(pid);

    /// <summary>True while anything is unlocked or in grace, so the host runs its coarse relock timer.</summary>
    public bool NeedsTimer =>
        LatchOpen || LatchGraceUntil is not null || _apps.Values.Any(a => a.State is LockState.Unlocked or LockState.Grace);

    /// <summary>A window of a locked app appeared (or was found by a scan). Returns whether to hide it.</summary>
    public bool OnWindow(string ruleId, nint hwnd, int? pid, DateTimeOffset? processStarted, bool hideable, bool fromScan, List<Effect> fx)
    {
        var now = clock.GetUtcNow();
        var app = Entry(ruleId);
        var knownPids = app.Pids.Count;
        if (pid is { } p && app.Pids.Add(p))
        {
            _pidToRule[p] = ruleId;
            fx.Add(new Effect(EffectKind.TrackProcess, ruleId, Pids: [p]));
        }

        switch (app.State)
        {
            case LockState.Unlocked:
                return false;
            case LockState.Grace when app.GraceUntil > now:
                app.State = LockState.Unlocked;
                app.GraceUntil = null;
                fx.Add(new Effect(EffectKind.StateChanged));
                return false;
            case LockState.Grace:
            case LockState.Locked:
                app.State = LockState.Prompting;
                app.GraceUntil = null;
                app.LaunchCandidate = !fromScan && knownPids == 0;
                app.FreshLaunch = app.LaunchCandidate && IsYoung(processStarted, now);
                Enqueue(PromptPurpose.App, ruleId, fx);
                fx.Add(new Effect(EffectKind.StateChanged));
                break;
            case LockState.Hidden:
                app.State = LockState.Prompting;
                app.FreshLaunch = false;
                app.LaunchCandidate = false;
                Enqueue(PromptPurpose.App, ruleId, fx);
                fx.Add(new Effect(EffectKind.StateChanged));
                break;
            case LockState.Prompting:
                // A Store app's frame is identified before its own process is known; upgrade when it shows up.
                if (app.LaunchCandidate && !fromScan && IsYoung(processStarted, now)) app.FreshLaunch = true;
                break;
            case LockState.Closing:
                break;
        }

        if (!hideable) return false;
        if (!app.Hidden.Contains(hwnd)) app.Hidden.Add(hwnd);
        return true;
    }

    private static bool IsYoung(DateTimeOffset? started, DateTimeOffset now) => started is { } s && now - s < FreshWindow;

    /// <summary>The host hid these windows for a <see cref="EffectKind.HideApp"/>.</summary>
    public void AddHidden(string ruleId, IEnumerable<nint> windows)
    {
        var app = Entry(ruleId);
        foreach (var w in windows) if (!app.Hidden.Contains(w)) app.Hidden.Add(w);
    }

    public void OnProcessExited(int pid, List<Effect> fx)
    {
        if (!_pidToRule.Remove(pid, out var ruleId)) return;
        var app = Entry(ruleId);
        app.Pids.Remove(pid);
        if (app.Pids.Count > 0) return;
        var p = policy();
        switch (app.State)
        {
            case LockState.Prompting:
            case LockState.Hidden:
            case LockState.Closing:
                app.State = LockState.Locked;
                app.Hidden.Clear();
                Dequeue(r => r.RuleId == ruleId, fx);
                break;
            case LockState.Unlocked when p.RelockOnClose:
                StartGrace(app, p);
                break;
        }
        fx.Add(new Effect(EffectKind.StateChanged));
    }

    /// <summary>
    /// An unlocked app closed its last window but kept running (it lives on in the tray, like WhatsApp or Discord).
    /// That counts as closing it: the grace period starts, and showing a window afterwards asks again. The app is
    /// never treated as just launched, so `Not now` keeps it running and hidden.
    /// </summary>
    public void OnAppWindowsGone(string ruleId, List<Effect> fx)
    {
        var app = Entry(ruleId);
        var p = policy();
        if (app.State != LockState.Unlocked || !p.RelockOnClose) return;
        StartGrace(app, p);
        fx.Add(new Effect(EffectKind.StateChanged));
    }

    /// <summary>The rule and its known processes, if that process belongs to an unlocked app.</summary>
    public (string RuleId, IReadOnlyList<int> Pids)? UnlockedAppOf(int pid) =>
        _pidToRule.TryGetValue(pid, out var ruleId) && Entry(ruleId) is { State: LockState.Unlocked } app ? (ruleId, app.Pids.ToList()) : null;

    private void StartGrace(AppEntry app, RelockPolicy p)
    {
        if (p.Grace > TimeSpan.Zero)
        {
            app.State = LockState.Grace;
            app.GraceUntil = clock.GetUtcNow() + p.Grace;
        }
        else
        {
            app.State = LockState.Locked;
            app.GraceUntil = null;
        }
    }

    /// <summary>Queues a prompt for an app whose windows are hidden (the owner asked to see it).</summary>
    public void RequestShow(string ruleId, List<Effect> fx)
    {
        var app = Entry(ruleId);
        if (app.State != LockState.Hidden) return;
        app.State = LockState.Prompting;
        app.FreshLaunch = false;
        app.LaunchCandidate = false;
        Enqueue(PromptPurpose.App, ruleId, fx);
        fx.Add(new Effect(EffectKind.StateChanged));
    }

    /// <summary>Opening the main window: returns true when it can open without a prompt.</summary>
    public bool RequestOpenLatch(bool needsUnlock, List<Effect> fx)
    {
        var now = clock.GetUtcNow();
        if (!needsUnlock || LatchOpen || LatchGraceUntil > now)
        {
            LatchOpen = true;
            LatchGraceUntil = null;
            fx.Add(new Effect(EffectKind.StateChanged));
            return true;
        }
        Enqueue(PromptPurpose.OpenLatch, null, fx);
        return false;
    }

    public void MainWindowClosed(List<Effect> fx)
    {
        if (!LatchOpen) return;
        LatchOpen = false;
        var p = policy();
        LatchGraceUntil = p.Grace > TimeSpan.Zero ? clock.GetUtcNow() + p.Grace : null;
        fx.Add(new Effect(EffectKind.StateChanged));
    }

    /// <summary>Asks for an unlock before a sensitive action. Returns the prompt id.</summary>
    public string RequestSensitive(PromptPurpose purpose, List<Effect> fx) => Enqueue(purpose, null, fx).Id;

    /// <summary>Consumes a recent unlock for <paramref name="purpose"/>; sensitive actions call this before acting.</summary>
    public bool ConsumeGrant(PromptPurpose purpose)
    {
        var ok = _grants.Remove(purpose, out var until) && until > clock.GetUtcNow();
        return ok;
    }

    public PromptRequest? FindPrompt(string promptId) => _queue.FirstOrDefault(r => r.Id == promptId);

    public PromptRequest? PromptForApp(string ruleId) => _queue.FirstOrDefault(r => r.RuleId == ruleId);

    public void OnUnlocked(string promptId, List<Effect> fx)
    {
        var req = FindPrompt(promptId);
        if (req is null) return;
        _queue.Remove(req);
        if (req.Purpose == PromptPurpose.App && req.RuleId is { } ruleId)
        {
            var app = Entry(ruleId);
            app.State = LockState.Unlocked;
            app.GraceUntil = null;
            fx.Add(new Effect(EffectKind.RestoreWindows, ruleId, app.Hidden.ToList()));
            app.Hidden.Clear();
        }
        else if (req.Purpose == PromptPurpose.OpenLatch)
        {
            LatchOpen = true;
            LatchGraceUntil = null;
            fx.Add(new Effect(EffectKind.OpenMainWindow));
        }
        else
        {
            _grants[req.Purpose] = clock.GetUtcNow() + TimeSpan.FromMinutes(2);
        }
        fx.Add(new Effect(EffectKind.PromptChanged));
        fx.Add(new Effect(EffectKind.StateChanged));
    }

    /// <summary>`Esc` or `Not now`. A just-launched app is closed; a running one stays hidden. Unsaved work is never lost.</summary>
    public void OnCancelled(string promptId, List<Effect> fx)
    {
        var req = FindPrompt(promptId);
        if (req is null) return;
        _queue.Remove(req);
        if (req.Purpose == PromptPurpose.App && req.RuleId is { } ruleId)
        {
            var app = Entry(ruleId);
            if (app.FreshLaunch)
            {
                app.State = LockState.Closing;
                fx.Add(new Effect(EffectKind.CloseApp, ruleId, app.Hidden.ToList(), app.Pids.ToList()));
            }
            else
            {
                app.State = LockState.Hidden;
            }
        }
        fx.Add(new Effect(EffectKind.PromptChanged));
        fx.Add(new Effect(EffectKind.StateChanged));
    }

    /// <summary>Idle, Windows lock, sleep, sign-out or `Lock all now`: everything relocks and running apps hide.</summary>
    public void RelockAll(List<Effect> fx)
    {
        foreach (var app in _apps.Values)
        {
            switch (app.State)
            {
                case LockState.Unlocked when app.Pids.Count > 0:
                    app.State = LockState.Hidden;
                    fx.Add(new Effect(EffectKind.HideApp, app.RuleId, Pids: app.Pids.ToList()));
                    break;
                case LockState.Unlocked:
                case LockState.Grace:
                    app.State = LockState.Locked;
                    app.GraceUntil = null;
                    break;
            }
        }
        if (LatchOpen) fx.Add(new Effect(EffectKind.CloseMainWindow));
        LatchOpen = false;
        LatchGraceUntil = null;
        _grants.Clear();
        fx.Add(new Effect(EffectKind.StateChanged));
    }

    /// <summary>The coarse timer: grace periods end, and idle time past the limit relocks everything.</summary>
    public void Tick(TimeSpan idle, List<Effect> fx)
    {
        var now = clock.GetUtcNow();
        var p = policy();
        var ended = false;
        foreach (var app in _apps.Values.Where(a => a.State == LockState.Grace && a.GraceUntil <= now))
        {
            app.State = LockState.Locked;
            app.GraceUntil = null;
            ended = true;
        }
        if (LatchGraceUntil <= now)
        {
            LatchGraceUntil = null;
            ended = true;
        }
        if (ended)
        {
            fx.Add(new Effect(EffectKind.ScanWindows));
            fx.Add(new Effect(EffectKind.StateChanged));
        }
        if (p.IdleTimeout is { } limit && idle >= limit && NeedsTimer) RelockAll(fx);
    }

    /// <summary>A rule was removed: give its windows back and drop its prompt.</summary>
    public void RuleRemoved(string ruleId, List<Effect> fx)
    {
        if (!_apps.Remove(ruleId, out var app)) return;
        foreach (var pid in app.Pids) _pidToRule.Remove(pid);
        if (app.Hidden.Count > 0) fx.Add(new Effect(EffectKind.RestoreWindows, ruleId, app.Hidden.ToList()));
        Dequeue(r => r.RuleId == ruleId, fx);
        fx.Add(new Effect(EffectKind.StateChanged));
    }

    public AppStatus StatusOf(string ruleId) => !_apps.TryGetValue(ruleId, out var a) ? AppStatus.Locked : a.State switch
    {
        LockState.Unlocked => AppStatus.Unlocked,
        LockState.Grace => AppStatus.Grace,
        LockState.Hidden => AppStatus.Hidden,
        _ => AppStatus.Locked,
    };

    private PromptRequest Enqueue(PromptPurpose purpose, string? ruleId, List<Effect> fx)
    {
        var existing = _queue.FirstOrDefault(r => r.Purpose == purpose && r.RuleId == ruleId);
        if (existing is not null) return existing;
        var req = new PromptRequest($"p{++_nextPrompt}", purpose, ruleId);
        _queue.Add(req);
        if (_queue.Count == 1) fx.Add(new Effect(EffectKind.PromptChanged));
        return req;
    }

    private void Dequeue(Func<PromptRequest, bool> match, List<Effect> fx)
    {
        var wasActive = ActivePrompt;
        _queue.RemoveAll(r => match(r));
        if (wasActive != ActivePrompt) fx.Add(new Effect(EffectKind.PromptChanged));
    }

    /// <summary>Removes a sensitive prompt without granting anything (the UI went away).</summary>
    public void Drop(string promptId, List<Effect> fx) => Dequeue(r => r.Id == promptId, fx);
}
