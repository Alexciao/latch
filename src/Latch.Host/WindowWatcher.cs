using System.Collections.Concurrent;
using Latch.Core;
using Latch.Core.Locking;
using Microsoft.Win32.SafeHandles;

namespace Latch.Host;

/// <summary>
/// Watches top-level windows with out-of-context WinEvent hooks on a dedicated thread, so hiding a locked window
/// never waits behind WPF or WebView2 work. Also hides, restores and closes app windows for the engine.
/// </summary>
internal sealed class WindowWatcher(LatchService service, Func<bool> dark, string iconDir, Action coverClicked, Action<string> coverClosed)
{
    private readonly ConcurrentDictionary<nint, byte> _hiddenByUs = new();
    private readonly ConcurrentDictionary<int, RegisteredWaitHandle> _tracked = new();
    private Native.WinEventProc? _proc; // Kept alive for the hooks' lifetime.
    private Cover? _cover;
    private const uint RunMessage = Native.WM_APP + 7;
    private readonly ConcurrentQueue<Action> _work = new();

    /// <summary>Runs work on the watcher thread, which owns the cover windows.</summary>
    private void Run(Action work)
    {
        _work.Enqueue(work);
        if (_threadId != 0) Native.PostThreadMessageW(_threadId, RunMessage, 0, 0);
    }

    public IReadOnlyList<string> CoveredApps() => _cover?.Apps() ?? [];

    public Native.RECT? CoverArea(string ruleId) => _cover?.AreaOf(ruleId);

    public nint CoverWindow(string ruleId) => _cover?.WindowOf(ruleId) ?? 0;

    /// <summary>The app's prompt is over: its cover goes, after <paramref name="delayMs"/> (so restored windows are back first).</summary>
    public void RemoveCover(string ruleId, int delayMs)
    {
        if (delayMs <= 0) Run(() => _cover!.Remove(ruleId));
        else _ = Task.Delay(delayMs).ContinueWith(_ => Run(() => _cover!.Remove(ruleId)), TaskScheduler.Default);
    }

    /// <summary>Covers go under a system dialog's group while it is up (zero: back to always-on-top).</summary>
    public void RestackCovers(nint below) => _cover?.Restack(below);
    private Thread? _thread;
    private uint _threadId;

    /// <summary>The window most recently hidden; the prompt centres on its monitor.</summary>
    public nint LastHidden { get; private set; }

    /// <summary>When <see cref="LastHidden"/> was hidden (Environment.TickCount64), to measure prompt latency.</summary>
    public long LastHiddenAt { get; private set; }

    private static readonly bool Trace = Environment.GetEnvironmentVariable("LATCH_TRACE") == "1";

    public void Start()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            _proc = OnEvent;
            _cover = new Cover(dark, service.AppLabel, iconDir, coverClicked, coverClosed);
            _cover.Prepare();
            _cover.Prerender(service.Settings.Rules.Select(r => (r.Name, r.IconKey)));
            const uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
            // SHOW catches new windows; FOREGROUND and UNCLOAKED catch Store frames once their AUMID is set and any
            // window that slipped past (see DECISIONS.md, spike). CREATE doesn't help: windows are invisible then.
            var hooks = new[]
            {
                Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, 0, _proc, 0, 0, flags),
                Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, 0, _proc, 0, 0, flags),
                Native.SetWinEventHook(Native.EVENT_OBJECT_UNCLOAKED, Native.EVENT_OBJECT_UNCLOAKED, 0, _proc, 0, 0, flags),
                // A tray app (WhatsApp, Discord) "closes" by hiding its window while the process lives on.
                Native.SetWinEventHook(Native.EVENT_OBJECT_HIDE, Native.EVENT_OBJECT_HIDE, 0, _proc, 0, 0, flags),
                Native.SetWinEventHook(Native.EVENT_OBJECT_CLOAKED, Native.EVENT_OBJECT_CLOAKED, 0, _proc, 0, 0, flags),
            };
            ready.Set();
            while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                if (msg.hwnd == 0 && msg.message == RunMessage)
                {
                    while (_work.TryDequeue(out var work)) work();
                    continue;
                }
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
            foreach (var h in hooks) Native.UnhookWinEvent(h);
        })
        {
            IsBackground = true,
            Name = "Latch window watcher",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
    }

    public void Stop()
    {
        if (_threadId != 0) Native.PostThreadMessageW(_threadId, Native.WM_QUIT, 0, 0);
    }

    private void OnEvent(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || idChild != 0 || hwnd == 0) return;
        if (ev is Native.EVENT_OBJECT_HIDE or Native.EVENT_OBJECT_CLOAKED)
        {
            OnWindowGone(hwnd);
            return;
        }
        if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd || !Native.IsWindowVisible(hwnd)) return;
        try
        {
            if (service.OnWindow(hwnd, fromScan: false) is { } ruleId)
            {
                Hide(hwnd); // Asynchronous: the app's own thread carries it out, often tens of milliseconds later...
                var t = System.Diagnostics.Stopwatch.GetTimestamp();
                _cover!.Over(hwnd, ruleId); // ...so cover the spot with the app's locked stand-in right now...
                _cover.Settling(ruleId, true); // ...and keep it on top until the app has really hidden the window.
                var cover = _cover;
                _ = Task.Run(async () =>
                {
                    var start = Environment.TickCount64;
                    while (Native.IsWindow(hwnd) && Native.IsWindowVisible(hwnd) && Environment.TickCount64 - start < 2000) await Task.Delay(10);
                    cover.Settling(ruleId, false);
                    // Some apps show their window again on their own once their startup work is done, and Windows
                    // doesn't always report it. Keep an eye on it for a few seconds and hide it again if so.
                    while (Native.IsWindow(hwnd) && Environment.TickCount64 - start < 3000)
                    {
                        await Task.Delay(25);
                        if (!Native.IsWindowVisible(hwnd) || !_hiddenByUs.ContainsKey(hwnd)) continue;
                        cover.Settling(ruleId, true);
                        Native.ShowWindowAsync(hwnd, Native.SW_HIDE);
                        if (Trace) Log.Info("Window showed itself again without an event; hidden again");
                        var again = Environment.TickCount64;
                        while (Native.IsWindow(hwnd) && Native.IsWindowVisible(hwnd) && Environment.TickCount64 - again < 2000) await Task.Delay(10);
                        cover.Settling(ruleId, false);
                    }
                });
                if (Trace) Log.Info($"Cover shown {System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds:F1} ms after deciding to lock, {Native.GetTickCount() - time} ms after the event");
            }
        }
        catch (Exception e)
        {
            Log.Error("window event", e);
        }
    }

    private readonly ConcurrentDictionary<string, Timer> _goneChecks = new();

    /// <summary>
    /// A window of an unlocked app went away. If, a moment later, the app has no window on screen at all, it was
    /// closed to the tray: tell the core, which starts the grace period. The delay rides over splash screens.
    /// </summary>
    private void OnWindowGone(nint hwnd)
    {
        if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd || _hiddenByUs.ContainsKey(hwnd)) return;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || service.UnlockedAppOf((int)pid) is not { } app) return;
        var timer = new Timer(_ =>
        {
            _goneChecks.TryRemove(app.RuleId, out var t);
            t?.Dispose();
            if (service.UnlockedAppOf((int)pid) is not { } now) return;
            if (!HasWindowOnScreen(now.Pids)) service.OnAppWindowsGone(now.RuleId);
        });
        if (_goneChecks.TryRemove(app.RuleId, out var old)) old.Dispose();
        _goneChecks[app.RuleId] = timer;
        timer.Change(1500, Timeout.Infinite);
    }

    private static bool HasWindowOnScreen(IReadOnlyList<int> pids)
    {
        var set = pids.ToHashSet();
        foreach (var hwnd in TopLevelWindows())
        {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (!set.Contains((int)pid) && !InspectorHostsAny(hwnd, set)) continue;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (!Native.GetWindowRect(hwnd, out var r) || r.Right - r.Left < 2 || r.Bottom - r.Top < 2) continue;
            if ((Native.GetWindowLongPtrW(hwnd, -20) & 0x80) != 0) continue; // Tool windows (tray flyouts) don't count.
            return true;
        }
        return false;
    }

    private void Hide(nint hwnd)
    {
        // Async: a cross-process ShowWindow waits for the target thread, and a hung app must not block the hook.
        var t0 = Environment.TickCount64;
        Native.ShowWindowAsync(hwnd, Native.SW_HIDE);
        if (Trace) _ = Task.Run(() =>
        {
            // How long the app's own thread took to act on the hide: the length of the flash.
            while (Native.IsWindowVisible(hwnd) && Environment.TickCount64 - t0 < 1000) Thread.Sleep(1);
            Log.Info($"Window hidden {Environment.TickCount64 - t0} ms after the hide request");
        });
        _hiddenByUs[hwnd] = 1;
        LastHidden = hwnd;
        LastHiddenAt = Environment.TickCount64;
    }

    /// <summary>Checks every visible top-level window, e.g. apps already open when Latch started.</summary>
    public void Scan() => ThreadPool.QueueUserWorkItem(_ =>
    {
        foreach (var hwnd in TopLevelWindows())
        {
            try
            {
                if (service.OnWindow(hwnd, fromScan: true) is not null) Hide(hwnd);
            }
            catch (Exception e)
            {
                Log.Error("scan", e);
            }
        }
    });

    /// <summary>Hides every visible window of an app that was relocked while running.</summary>
    public void HideApp(string ruleId, IReadOnlyList<int> pids)
    {
        var set = pids.ToHashSet();
        var hidden = new List<nint>();
        foreach (var hwnd in TopLevelWindows())
        {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            var owner = (int)pid;
            if (!set.Contains(owner))
            {
                // A Store app's frame belongs to ApplicationFrameHost; it hosts the app's window as a child.
                if (!InspectorHostsAny(hwnd, set)) continue;
            }
            if (WindowInspector.ClassName(hwnd) == "Windows.UI.Core.CoreWindow") continue;
            Hide(hwnd);
            hidden.Add(hwnd);
        }
        service.OnWindowsHidden(ruleId, hidden);
    }

    /// <summary>Shows the windows again in their previous state, topmost first, and brings the app to the front.</summary>
    public void Restore(IReadOnlyList<nint> windows)
    {
        var alive = windows.Where(Native.IsWindow).ToList();
        for (var i = alive.Count - 1; i >= 0; i--)
        {
            Native.ShowWindowAsync(alive[i], Native.SW_SHOWNA);
            _hiddenByUs.TryRemove(alive[i], out _);
        }
        var front = alive.FirstOrDefault(w => !Native.IsIconic(w));
        if (front != 0)
        {
            Native.SetForegroundWindow(front);
            Native.BringWindowToTop(front);
        }
    }

    /// <summary>`Not now` on a fresh launch: ask the app to close, and end it only if it is still there after 3 s.</summary>
    public void CloseApp(IReadOnlyList<nint> windows, IReadOnlyList<int> pids)
    {
        foreach (var w in windows) Native.PostMessageW(w, Native.WM_CLOSE, 0, 0);
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            foreach (var pid in pids)
            {
                var path = WindowInspector.ImagePath((uint)pid);
                if (path is null || RuleMatcher.NeverLock.Contains(Path.GetFileName(path))) continue;
                var h = Native.OpenProcess(Native.PROCESS_TERMINATE, false, (uint)pid);
                if (h == 0) continue;
                Native.TerminateProcess(h, 1);
                Native.CloseHandle(h);
                Log.Info($"Ended pid {pid} after Not now (still running after 3 s)");
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Gives every window Latch hid back. Used when the owner quits Latch after an unlock.</summary>
    public void RestoreAll()
    {
        foreach (var hwnd in _hiddenByUs.Keys)
            if (Native.IsWindow(hwnd)) Native.ShowWindowAsync(hwnd, Native.SW_SHOWNA);
        _hiddenByUs.Clear();
    }

    /// <summary>Reports when a process exits, without polling.</summary>
    public void Track(IReadOnlyList<int> pids)
    {
        foreach (var pid in pids)
        {
            if (_tracked.ContainsKey(pid)) continue;
            var h = Native.OpenProcess(Native.SYNCHRONIZE, false, (uint)pid);
            if (h == 0)
            {
                service.OnProcessExited(pid);
                continue;
            }
            var wait = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(h, ownsHandle: true) };
            var reg = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) =>
            {
                if (_tracked.TryRemove(pid, out var r)) r.Unregister(null);
                wait.Dispose();
                service.OnProcessExited(pid);
            }, null, Timeout.Infinite, executeOnlyOnce: true);
            _tracked[pid] = reg;
        }
    }

    private static bool InspectorHostsAny(nint frame, HashSet<int> pids)
    {
        var found = false;
        Native.EnumChildWindows(frame, (child, _) =>
        {
            Native.GetWindowThreadProcessId(child, out var p);
            found = pids.Contains((int)p);
            return !found;
        }, 0);
        return found;
    }

    private static List<nint> TopLevelWindows()
    {
        var list = new List<nint>();
        var self = (uint)Environment.ProcessId;
        Native.EnumWindows((hwnd, _) =>
        {
            if (Native.IsWindowVisible(hwnd))
            {
                Native.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != self) list.Add(hwnd);
            }
            return true;
        }, 0);
        return list;
    }
}
