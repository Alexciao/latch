using Latch.Core.Contract;
using Latch.Core.Locking;

namespace Latch.Core.Tests;

public class LockEngineTests
{
    private readonly ManualClock _clock = new(DateTimeOffset.Parse("2026-11-16T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private RelockPolicy _policy = new(TimeSpan.FromMinutes(5), RelockOnClose: true, IdleTimeout: TimeSpan.FromMinutes(10));
    private readonly LockEngine _engine;
    private readonly List<Effect> _fx = [];

    public LockEngineTests() => _engine = new LockEngine(_clock, () => _policy);

    private bool Launch(string rule, nint hwnd, int pid, bool fromScan = false) =>
        _engine.OnWindow(rule, hwnd, pid, _clock.Now.AddSeconds(-1), hideable: true, fromScan, _fx);

    private void UnlockActive() => _engine.OnUnlocked(_engine.ActivePrompt!.Id, _fx);

    [Fact]
    public void A_fresh_launch_hides_every_window_and_asks_once()
    {
        Assert.True(Launch("notepad", 1, 10));
        Assert.True(Launch("notepad", 2, 10));
        Assert.Equal(LockState.Prompting, _engine.Entry("notepad").State);
        Assert.Equal(new nint[] { 1, 2 }, _engine.Entry("notepad").Hidden);
        Assert.Single(_fx, e => e.Kind == EffectKind.PromptChanged);
    }

    [Fact]
    public void Unlock_restores_the_hidden_windows_in_order()
    {
        Launch("notepad", 1, 10);
        Launch("notepad", 2, 10);
        UnlockActive();
        var restore = Assert.Single(_fx, e => e.Kind == EffectKind.RestoreWindows);
        Assert.Equal(new nint[] { 1, 2 }, restore.Windows);
        Assert.False(Launch("notepad", 3, 10)); // Unlocked: new windows show.
    }

    [Fact]
    public void Closing_starts_the_grace_period_and_relaunching_inside_it_does_not_ask()
    {
        Launch("vlc", 1, 10);
        UnlockActive();
        _engine.OnProcessExited(10, _fx);
        Assert.Equal(AppStatus.Grace, _engine.StatusOf("vlc"));
        Assert.Equal(_clock.Now.AddMinutes(5), _engine.Entry("vlc").GraceUntil);

        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(Launch("vlc", 2, 11));
        Assert.Equal(AppStatus.Unlocked, _engine.StatusOf("vlc"));
    }

    [Fact]
    public void A_tray_app_closing_its_last_window_starts_grace_and_asks_again_after_it()
    {
        Launch("whatsapp", 1, 10);
        UnlockActive();
        _engine.OnAppWindowsGone("whatsapp", _fx); // Window closed, process still running in the tray.
        Assert.Equal(AppStatus.Grace, _engine.StatusOf("whatsapp"));
        Assert.False(Launch("whatsapp", 1, 10)); // Reopened within grace: no prompt.

        _engine.OnAppWindowsGone("whatsapp", _fx);
        _clock.Advance(TimeSpan.FromMinutes(6));
        _engine.Tick(TimeSpan.Zero, _fx);
        Assert.True(Launch("whatsapp", 1, 10)); // After grace: hidden and asked again...
        Assert.False(_engine.Entry("whatsapp").FreshLaunch); // ...and `Not now` will never close it.
    }

    [Fact]
    public void Grace_ends_on_the_timer_and_the_next_launch_asks_again()
    {
        Launch("vlc", 1, 10);
        UnlockActive();
        _engine.OnProcessExited(10, _fx);
        _clock.Advance(TimeSpan.FromMinutes(5));
        _engine.Tick(TimeSpan.Zero, _fx);
        Assert.Equal(AppStatus.Locked, _engine.StatusOf("vlc"));
        Assert.Contains(_fx, e => e.Kind == EffectKind.ScanWindows);
        Assert.True(Launch("vlc", 2, 11));
    }

    [Fact]
    public void Grace_off_relocks_right_after_close_and_relock_on_close_off_stays_unlocked()
    {
        _policy = _policy with { Grace = TimeSpan.Zero };
        Launch("vlc", 1, 10);
        UnlockActive();
        _engine.OnProcessExited(10, _fx);
        Assert.Equal(AppStatus.Locked, _engine.StatusOf("vlc"));

        _policy = _policy with { RelockOnClose = false };
        Launch("vlc", 2, 11);
        UnlockActive();
        _engine.OnProcessExited(11, _fx);
        Assert.Equal(AppStatus.Unlocked, _engine.StatusOf("vlc"));
    }

    [Fact]
    public void Idle_relocks_everything_and_hides_running_apps()
    {
        Launch("vlc", 1, 10);
        UnlockActive();
        _engine.Tick(TimeSpan.FromMinutes(9), _fx);
        Assert.Equal(AppStatus.Unlocked, _engine.StatusOf("vlc"));
        _engine.Tick(TimeSpan.FromMinutes(10), _fx);
        Assert.Equal(AppStatus.Hidden, _engine.StatusOf("vlc"));
        Assert.Contains(_fx, e => e is { Kind: EffectKind.HideApp, RuleId: "vlc" });
        Assert.False(_engine.NeedsTimer);
    }

    [Fact]
    public void Cancel_on_a_fresh_launch_closes_the_app()
    {
        Launch("notepad", 1, 10);
        _engine.OnCancelled(_engine.ActivePrompt!.Id, _fx);
        var close = Assert.Single(_fx, e => e.Kind == EffectKind.CloseApp);
        Assert.Equal(new[] { 10 }, close.Pids);
        Assert.True(Launch("notepad", 2, 10)); // Anything it shows while closing stays hidden...
        Assert.Null(_engine.ActivePrompt);      // ...without asking again.
    }

    [Fact]
    public void Cancel_after_an_idle_relock_never_closes_and_keeps_the_app_hidden()
    {
        Launch("notepad", 1, 10);
        UnlockActive();
        _engine.RelockAll(_fx);
        _engine.AddHidden("notepad", [1]);
        Assert.True(Launch("notepad", 1, 10)); // It tries to show itself again: prompt.
        Assert.False(_engine.Entry("notepad").FreshLaunch);
        _engine.OnCancelled(_engine.ActivePrompt!.Id, _fx);
        Assert.DoesNotContain(_fx, e => e.Kind == EffectKind.CloseApp);
        Assert.Equal(AppStatus.Hidden, _engine.StatusOf("notepad"));
    }

    [Fact]
    public void A_store_frame_seen_before_its_young_process_still_counts_as_a_fresh_launch()
    {
        _engine.OnWindow("calc", 1, pid: null, processStarted: null, hideable: true, fromScan: false, _fx); // Frame, host unknown.
        Assert.False(_engine.Entry("calc").FreshLaunch);
        _engine.OnWindow("calc", 2, pid: 30, processStarted: _clock.Now.AddSeconds(-1), hideable: false, fromScan: false, _fx);
        Assert.True(_engine.Entry("calc").FreshLaunch);
    }

    [Fact]
    public void An_app_found_by_the_startup_scan_is_never_treated_as_just_launched()
    {
        Launch("notepad", 1, 10, fromScan: true);
        Assert.False(_engine.Entry("notepad").FreshLaunch);
    }

    [Fact]
    public void Two_apps_queue_their_prompts_and_an_exit_dismisses_its_own()
    {
        Launch("vlc", 1, 10);
        Launch("calc", 2, 20);
        Assert.Equal("vlc", _engine.ActivePrompt!.RuleId);
        _engine.OnProcessExited(10, _fx); // VLC quits while its prompt is open.
        Assert.Equal("calc", _engine.ActivePrompt!.RuleId);
        UnlockActive();
        Assert.Null(_engine.ActivePrompt);
    }

    [Fact]
    public void Latch_itself_needs_an_unlock_to_open_and_relocks_with_everything_else()
    {
        Assert.False(_engine.RequestOpenLatch(needsUnlock: true, _fx));
        Assert.Equal(PromptPurpose.OpenLatch, _engine.ActivePrompt!.Purpose);
        UnlockActive();
        Assert.True(_engine.LatchOpen);
        _engine.RelockAll(_fx);
        Assert.Contains(_fx, e => e.Kind == EffectKind.CloseMainWindow);
        Assert.False(_engine.RequestOpenLatch(needsUnlock: true, _fx));
    }

    [Fact]
    public void Sensitive_unlocks_grant_one_use_for_a_short_time()
    {
        _engine.RequestSensitive(PromptPurpose.ChangePin, _fx);
        UnlockActive();
        Assert.True(_engine.ConsumeGrant(PromptPurpose.ChangePin));
        Assert.False(_engine.ConsumeGrant(PromptPurpose.ChangePin));
    }
}
