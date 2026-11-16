using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Latch.Core;
using Latch.Core.Contract;
using Latch.Core.Storage;
using Microsoft.Web.WebView2.Core;

namespace Latch.Host;

/// <summary>
/// The unlock prompt: one pre-created web view kept hidden and suspended, so it can paint within a few frames
/// (see DECISIONS.md, spike). The window stays DWM-cloaked until the page reports it has painted, so nothing
/// stale or half-laid-out is ever on screen.
/// </summary>
internal sealed class PromptHost(DataPaths paths, Rpc rpc, Func<ThemeChoice> theme, Func<nint> anchor, Func<long> anchorAt, Func<Native.RECT?> anchorArea, Func<(nint Hwnd, Native.RECT Area)?> coverOfPrompt)
{
    private const double WidthDip = 360;
    private readonly TaskCompletionSource _loaded = new();
    private Window? _win;
    private nint _hwnd;
    private CoreWebView2Controller? _ctl;
    private Task? _creating;
    private bool _suspended;
    private bool _visible;
    private string? _shownId;
    private int _heightDip = 300;
    private TaskCompletionSource? _painted;
    private string? _pendingId;
    private int _generation;

    public nint Hwnd => _hwnd;

    public bool IsVisible => _visible;

    /// <summary>Creates the web view and suspends it. Called once startup has settled.</summary>
    public async Task PrewarmAsync()
    {
        await EnsureAsync();
        await _loaded.Task;
        if (_visible) return;
        // WPF's first Show of a window costs a few hundred ms; pay it now, cloaked and off-screen.
        var cloak = 1;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_CLOAK, ref cloak, sizeof(int));
        _win!.ShowActivated = false;
        _win.Show();
        _win.Hide();
        _win.ShowActivated = true;
        await SuspendAsync();
    }

    public async void Update(PromptView? view)
    {
        try
        {
            var generation = ++_generation;
            if (view is null)
            {
                Hide();
                return;
            }
            var started = Stopwatch.GetTimestamp();
            var dispatchLag = Environment.TickCount64 - anchorAt();
            double Ms() => Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            await EnsureAsync();
            await _loaded.Task;
            if (generation != _generation) return;

            if (_visible && _shownId == view.PromptId)
            {
                Post(view);
                return;
            }

            var tReady = Ms();
            if (_suspended)
            {
                _ctl!.CoreWebView2.Resume();
                _suspended = false;
            }
            var tResumed = Ms();
            // Send the content first, so the page renders while the window work below happens.
            _painted = new TaskCompletionSource();
            _pendingId = view.PromptId;
            Post(view);
            WebUi.SetTheme(_ctl!.CoreWebView2, view.Theme);
            ApplySurface(view.Theme);

            // Reveal only after the page has painted this prompt: cloaked windows still render but aren't shown.
            var cloak = 1;
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_CLOAK, ref cloak, sizeof(int));
            PlaceOnAnchorMonitor();
            _win!.Show();
            _ctl.IsVisible = true;
            WebUi.FitToClient(_ctl, _hwnd);
            var tPosted = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            await Task.WhenAny(_painted.Task, Task.Delay(400));
            if (generation != _generation) return;
            PlaceOnAnchorMonitor();
            _visible = true;
            _shownId = view.PromptId;
            if (view.HelloOn && view.LockoutUntil is null)
            {
                // Hello starts by itself: stay cloaked until the Hello dialog exists and the card sits above it,
                // so the card appears once, in its final place, instead of jumping there.
                HoldUntilPlaced(generation, waitingForHello: true);
            }
            else
            {
                Reveal(focus: true);
            }
            var sinceWindow = Environment.TickCount64 - anchorAt();
            Log.Info($"Prompt shown {sinceWindow} ms after the app's window appeared: UI thread picked it up at +{dispatchLag} ms, ready {tReady:F0}, resumed {tResumed:F0}, shown+posted {tPosted:F0}, painted+revealed {Ms():F0} ms (painted: {_painted.Task.IsCompleted})");
        }
        catch (Exception e)
        {
            Log.Error("prompt update", e);
        }
    }

    private bool _restoreOnPaint;
    private int _fullHeightDip;

    public void Painted(string promptId, int heightDip)
    {
        if (promptId != _pendingId && promptId != _shownId) return;
        if (_dialogTimer is null && !_restoreOnPaint && heightDip > 120) _fullHeightDip = heightDip;
        if (_restoreOnPaint && _dialogTimer is null)
        {
            _restoreOnPaint = false;
            if (heightDip is > 40 and < 800) _heightDip = heightDip;
            PlaceOnAnchorMonitor();
            Reveal(focus: true);
            _painted?.TrySetResult();
            return;
        }
        if (heightDip is > 40 and < 800 && heightDip != _heightDip)
        {
            _heightDip = heightDip;
            if (_dialogTimer is not null) FollowSystemDialog(force: true);
            else PlaceOnAnchorMonitor();
        }
        _painted?.TrySetResult();
    }

    /// <summary>
    /// Runs Windows Hello or the passkey dialog. Meanwhile the page shows its compact card, and the prompt keeps
    /// itself centred just above the system dialog (whatever its size), so neither covers the other.
    /// </summary>
    public async Task<T> OverSystemDialog<T>(Func<nint, Task<T>> run)
    {
        _dialogTimer?.Stop();
        _dialogTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Normal, (_, _) => FollowSystemDialog(force: false), Dispatcher.CurrentDispatcher);
        _dialogHwnd = 0;
        // While Hello is up, neither Hello nor the card is always-on-top; they share an owner and stack as one.
        _win!.Topmost = false;
        Native.SetWindowPos(_hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _dialogRect = null;
        _candidate = null;
        _dialogGoneAt = null;
        _dialogStarted = DateTime.Now.AddSeconds(-1);
        var owner = MakeDialogOwner();
        _dialogOwner = owner.Handle;
        _win!.Owner = owner.Window; // Same owner as Hello: Windows stacks the card and Hello as one.
        // Hello must open in front, not behind whatever app was in use (it has no taskbar button to find it by).
        // Windows only lets the foreground process put a new window in front, so take the foreground first (the
        // card is still cloaked, so nothing flashes), let the Hello process take it from us, and once its window
        // exists bring it to the front once (see FollowSystemDialog).
        Foreground.Force(_hwnd);
        Native.AllowSetForegroundWindow(-1); // ASFW_ANY
        _dialogRaised = false;
        if (_visible && !_held) HoldUntilPlaced(_generation, waitingForHello: true); // The card changes size and place: hide the move.
        _dialogTimer.Start();
        try
        {
            var result = await run(owner.Handle);
            if (_dialogGoneAt is { } gone) Log.Info($"System dialog: result came {Environment.TickCount64 - gone} ms after its window closed");
            return result;
        }
        finally
        {
            _dialogTimer?.Stop();
            _dialogTimer = null;
            DialogStacking?.Invoke(0);
            _win!.Owner = null;
            owner.Window.Close();
            _dialogOwner = 0;
            _dialogHwnd = 0;
            _win!.Topmost = true; // The full prompt is always-on-top again.
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if (_visible)
            {
                // Back to the full prompt: stay cloaked until the page repaints at its new size (it only learns the
                // result after this returns), then centre and reveal in one step.
                _held = true;
                _restoreOnPaint = true;
                SetCloak(true);
                if (_fullHeightDip > 0)
                {
                    _heightDip = _fullHeightDip; // Place it at the full prompt's size now; the paint only confirms it.
                    PlaceOnAnchorMonitor();
                }
                var generation = _generation;
                _ = Task.Delay(150).ContinueWith(_ =>
                {
                    if (!_restoreOnPaint || generation != _generation || !_visible) return;
                    _restoreOnPaint = false;
                    PlaceOnAnchorMonitor();
                    Reveal(focus: true);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }
    }

    private DateTime _dialogStarted;
    private nint _dialogOwner;

    /// <summary>
    /// Windows places its Hello and passkey dialogs relative to their owner window. If the owner were the prompt,
    /// moving the prompt above the dialog would move the dialog too, and the two would walk down the screen. So the
    /// dialog gets its own invisible owner, sized and centred like the full prompt, which never moves: the dialog
    /// stays where it always was (centred), and only the compact card follows it.
    /// </summary>
    private (Window Window, nint Handle) MakeDialogOwner()
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Width = WidthDip,
            Height = 300,
            Left = -10000,
            Top = -10000,
        };
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        var ex = Native.GetWindowLongPtrW(hwnd, -20);
        Native.SetWindowLongPtrW(hwnd, -20, ex | 0x20 | 0x80 | 0x08000000); // WS_EX_TRANSPARENT | TOOLWINDOW | NOACTIVATE
        w.Show();
        if (CentrePoint() is { } centre)
        {
            Native.SetWindowPos(hwnd, 0, centre.X, centre.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
            var scale = Native.GetDpiForWindow(hwnd) / 96.0;
            var pw = (int)Math.Round(WidthDip * scale);
            var ph = (int)Math.Round(300 * scale);
            Native.SetWindowPos(hwnd, 0, centre.X - pw / 2, centre.Y - ph / 2, pw, ph, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        }
        return (w, hwnd);
    }

    private DispatcherTimer? _dialogTimer;
    private Native.RECT? _dialogRect;
    private Native.RECT? _candidate;

    private static bool Same(Native.RECT a, Native.RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private void FollowSystemDialog(bool force)
    {
        if (!_visible) return;
        if (FindSystemDialog() is not { } found) return;
        var d = found.Rect;
        if (!_dialogRaised)
        {
            _dialogRaised = true;
            Foreground.Force(found.Hwnd);
            // Only now do covers leave the always-on-top band (and only those whose app has finished hiding):
            // before Hello exists there's nothing to make room for, and the app may still be on screen.
            DialogStacking?.Invoke(_dialogOwner);
        }
        SyncWithDialog(found.Hwnd);
        var settled = _candidate is { } c && Same(c, d);
        _candidate = d;
        if (!settled) return; // Windows reports transitional rectangles while the dialog animates in; wait for it to hold still.
        if (!force && _dialogRect is { } last && Same(last, d)) return;
        _dialogRect = d;
        var monitor = Native.MonitorFromPoint(new Native.POINT { X = (d.Left + d.Right) / 2, Y = (d.Top + d.Bottom) / 2 }, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfoW(monitor, ref info);
        var work = info.rcWork;
        var scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        var w = (int)Math.Round(WidthDip * scale);
        var h = (int)Math.Round(_heightDip * scale);
        var pad = (int)Math.Round(16 * scale);
        var x = Math.Clamp((d.Left + d.Right - w) / 2, work.Left + pad, Math.Max(work.Left + pad, work.Right - w - pad));
        var y = d.Top - h - pad;
        if (y < work.Top + pad) y = Math.Min(d.Bottom + pad, work.Bottom - h - pad); // No room above: sit below it.
        Native.SetWindowPos(_hwnd, 0, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        SyncWithDialog(found.Hwnd);
        if (_ctl is not null) WebUi.FitToClient(_ctl, _hwnd);
        if (_held) Reveal(focus: false); // The system dialog has the keyboard; don't take it.
    }

    private nint _dialogHwnd;
    private long? _dialogGoneAt;
    private bool _dialogRaised;

    /// <summary>
    /// Keeps the card directly above the system dialog in the z-order, so whatever covers or uncovers one does the
    /// same to the other, and hands focus to the dialog if the card is clicked: the two behave as one window.
    /// </summary>
    private void SyncWithDialog(nint dialog)
    {
        // The card is owned by the same invisible window as Hello, so Windows already raises and covers the three
        // as one group. All that's left is focus: clicking the card hands the keyboard straight back to Hello.
        if (Native.GetForegroundWindow() == _hwnd) Native.SetForegroundWindow(dialog);
    }

    private bool _held;

    private void SetCloak(bool on)
    {
        var v = on ? 1 : 0;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_CLOAK, ref v, sizeof(int));
    }

    /// <summary>Keeps the window cloaked until it's placed; if no system dialog shows up, reveal it centred anyway.</summary>
    private readonly Spinner _spinner = new();

    private void HoldUntilPlaced(int generation, bool waitingForHello = false)
    {
        _held = true;
        SetCloak(true);
        // Windows Hello takes a moment to appear; meanwhile the locked app's cover shows a spinner.
        if (waitingForHello && coverOfPrompt() is { } cover) _spinner.Show(cover.Hwnd, cover.Area, Theme.IsDark(theme()));
        _ = Task.Delay(2500).ContinueWith(_ =>
        {
            if (!_held || generation != _generation || !_visible) return;
            PlaceOnAnchorMonitor();
            Reveal(focus: _dialogTimer is null);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Raised when a system dialog comes up (its owner window) and goes (zero), so covers stack under it.</summary>
    public event Action<nint>? DialogStacking;

    /// <summary>The locked app's cover was clicked: the keyboard belongs to the prompt (or to Hello, if it's up).</summary>
    public void FocusFromCover()
    {
        if (!_visible || _held) return;
        if (_dialogHwnd != 0) Foreground.Force(_dialogHwnd);
        else Focus();
    }

    private void Reveal(bool focus)
    {
        _held = false;
        _spinner.Hide();
        SetCloak(false);
        if (focus) Focus();
    }

    /// <summary>The Windows Hello or passkey dialog: a visible window owned by the prompt or by the credential broker.</summary>
    private static DateTime? StartedAt(uint pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
            return Native.GetProcessTimes(h, out var created, out _, out _, out _) ? DateTime.FromFileTime(created) : null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    private (nint Hwnd, Native.RECT Rect)? FindSystemDialog()
    {
        if (_dialogHwnd != 0 && Native.IsWindow(_dialogHwnd) && Native.IsWindowVisible(_dialogHwnd) && Native.DwmGetWindowRect(_dialogHwnd, out var cached) == 0)
            return (_dialogHwnd, cached);
        if (_dialogHwnd != 0) _dialogGoneAt ??= Environment.TickCount64; // It was there and now it's closed.
        _dialogHwnd = 0;
        Native.RECT? best = null;
        nint bestHwnd = 0;
        long bestArea = 0;
        var self = (uint)Environment.ProcessId;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self) return true;
            var owner = Native.GetWindow(hwnd, 4); // GW_OWNER
            var owned = owner != 0 && (owner == _hwnd || owner == _dialogOwner);
            if (!owned)
            {
                var path = WindowInspector.ImagePath(pid);
                if (path is null || !Path.GetFileName(path).Equals("CredentialUIBroker.exe", StringComparison.OrdinalIgnoreCase)) return true;
                if (StartedAt(pid) is not { } started || started < _dialogStarted) return true; // A stale dialog from earlier.
            }
            if (Native.DwmGetWindowRect(hwnd, out var r) != 0) return true;
            var mon = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfoW(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST), ref mon)
                && (r.Right - r.Left > (mon.rcWork.Right - mon.rcWork.Left) * 0.8 || r.Bottom - r.Top > (mon.rcWork.Bottom - mon.rcWork.Top) * 0.8)) return true;
            var area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea && r.Right - r.Left > 50)
            {
                bestArea = area;
                best = r;
                bestHwnd = hwnd;
            }
            return true;
        }, 0);
        if (best is not { } rect) return null;
        _dialogHwnd = bestHwnd;
        return (bestHwnd, rect);
    }

    private void Hide()
    {
        if (_win is null || _ctl is null) return;
        _win.Hide();
        _ctl.IsVisible = false;
        _visible = false;
        _held = false;
        _spinner.Hide();
        _restoreOnPaint = false;
        _shownId = null;
        Post(null);
        var generation = _generation;
        _ = Task.Delay(1500).ContinueWith(async _ =>
        {
            if (generation == _generation && !_visible) await SuspendAsync();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task SuspendAsync()
    {
        if (_ctl is null || _suspended || _ctl.IsVisible) return;
        try
        {
            _suspended = await _ctl.CoreWebView2.TrySuspendAsync();
        }
        catch (Exception e)
        {
            Log.Error("prompt suspend", e);
        }
    }

    private Task EnsureAsync() => _creating ??= CreateAsync();

    private async Task CreateAsync()
    {
        var w = WebUi.MakePopup();
        w.Title = Copy.Get("app.name");
        w.Width = WidthDip;
        w.Height = _heightDip;
        w.Topmost = true;
        w.ShowInTaskbar = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -10000;
        w.Top = -10000;
        _hwnd = WebUi.Prepare(w);
        _win = w;
        // The light popup shadow (CS_DROPSHADOW), the same one the locked app's cover uses.
        var style = Native.GetClassLongPtrW(_hwnd, -26); // GCL_STYLE
        Native.SetClassLongPtrW(_hwnd, -26, style | 0x00020000);
        ApplySurface(theme());
        w.SizeChanged += (_, _) =>
        {
            if (_ctl is not null) WebUi.FitToClient(_ctl, _hwnd);
        };
        _ctl = await WebUi.CreateAsync(paths, _hwnd, rpc, theme());
        _ctl.IsVisible = false;
        _ctl.CoreWebView2.NavigationCompleted += (_, _) => _loaded.TrySetResult();
        _ctl.CoreWebView2.Navigate(WebUi.PageUrl("prompt.html", mica: false));
    }

    private void Post(PromptView? view)
    {
        try
        {
            _ctl?.CoreWebView2.PostWebMessageAsJson(Rpc.PromptEvent(view));
        }
        catch (Exception e)
        {
            Log.Error("prompt post", e);
        }
    }

    /// <summary>Centres the prompt on the monitor where the locked app appeared.</summary>
    private void PlaceOnAnchorMonitor()
    {
        var a = anchor();
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        var found = a != 0 && Native.IsWindow(a) && Native.GetMonitorInfoW(Native.MonitorFromWindow(a, Native.MONITOR_DEFAULTTONEAREST), ref info);
        if (!found)
        {
            // No usable anchor (Latch's own prompts, or the app's window is gone): the monitor under the cursor.
            Native.GetCursorPos(out var pt);
            found = Native.GetMonitorInfoW(Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST), ref info);
        }
        if (!found || info.rcWork.Right - info.rcWork.Left < 200) return;
        var work = info.rcWork;
        var area = anchorArea();
        // Move first so the window takes the target monitor's DPI, then size in that monitor's pixels.
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, work.Left + 10, work.Top + 10, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        var scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        var w = (int)Math.Round(WidthDip * scale);
        var h = (int)Math.Round(_heightDip * scale);
        var x = work.Left + (work.Right - work.Left - w) / 2;
        var y = work.Top + (work.Bottom - work.Top - h) / 2;
        // Over the locked app's cover when it's big enough to hold the prompt, so the two read as one locked window.
        if (area is { } box && box.Right - box.Left >= w && box.Bottom - box.Top >= h)
        {
            x = Math.Clamp((box.Left + box.Right - w) / 2, work.Left, Math.Max(work.Left, work.Right - w));
            y = Math.Clamp((box.Top + box.Bottom - h) / 2, work.Top, Math.Max(work.Top, work.Bottom - h));
        }
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
        if (_ctl is not null) WebUi.FitToClient(_ctl, _hwnd);
    }

    /// <summary>The centre of the prompt's current spot (where Hello's owner goes).</summary>
    private Native.POINT? CentrePoint() =>
        Native.GetWindowRect(_hwnd, out var r) && r.Right > r.Left ? new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 } : null;

    private void ApplySurface(ThemeChoice choice)
    {
        var dark = Theme.IsDark(choice);
        _win!.Background = new SolidColorBrush(WebUi.PopupColor(dark));
        Theme.ApplyChrome(_hwnd, dark, mica: false, round: true);
    }

    private void Focus()
    {
        Foreground.Force(_hwnd);
        _win!.Activate();
        _ctl!.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
    }
}

/// <summary>Brings one of Latch's windows to the front even though Latch isn't the foreground process.</summary>
internal static class Foreground
{
    public static void Force(nint hwnd)
    {
        if (Native.SetForegroundWindow(hwnd) && Native.GetForegroundWindow() == hwnd) return;
        var fg = Native.GetForegroundWindow();
        var fgThread = fg == 0 ? 0 : Native.GetWindowThreadProcessId(fg, out _);
        var self = Native.GetCurrentThreadId();
        if (fgThread != 0 && fgThread != self)
        {
            Native.AttachThreadInput(self, fgThread, true);
            Native.SetForegroundWindow(hwnd);
            Native.BringWindowToTop(hwnd);
            Native.AttachThreadInput(self, fgThread, false);
        }
        if (Native.GetForegroundWindow() != hwnd)
        {
            // Last resort: a synthetic Alt press satisfies the foreground lock rules.
            const byte vkMenu = 0x12;
            Native.keybd_event(vkMenu, 0, 0, 0);
            Native.keybd_event(vkMenu, 0, 2, 0);
            Native.SetForegroundWindow(hwnd);
        }
    }
}
