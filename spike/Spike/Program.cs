using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace Spike;

static class Program
{
    static readonly StreamWriter Log = new(Path.Combine(AppContext.BaseDirectory, "spike.log"), false) { AutoFlush = true };
    static void L(string s) { var line = $"{Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000)} {s}"; Log.WriteLine(line); Console.WriteLine(line); }

    static HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
    static HashSet<string> targetPfns = new(StringComparer.OrdinalIgnoreCase);
    static bool hide;
    static Native.WinEventProc? proc;

    [STAThread]
    static void Main(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "hook";
        if (mode == "hook") RunHook(args.Skip(1).ToArray());
        else if (mode == "webview") RunWebView();
        else if (mode == "sign") RunSign(args.Skip(1).ToArray());
        else if (mode == "mica") RunMica(args.Skip(1).ToArray());
        else if (mode == "tray") RunTray(int.Parse(args[1]), int.Parse(args[2]));
        else if (mode == "shadows") RunShadows();
    }

    static void RunSign(string[] paths)
    {
        foreach (var p in paths)
        {
            var sw = Stopwatch.StartNew();
            var fvi = FileVersionInfo.GetVersionInfo(p);
            var t1 = sw.Elapsed.TotalMilliseconds;
            string? subj = null;
            try {
#pragma warning disable SYSLIB0057
                subj = X509Certificate.CreateFromSignedFile(p).Subject;
#pragma warning restore SYSLIB0057
            } catch (Exception e) { subj = "ERR " + e.GetType().Name; }
            var t2 = sw.Elapsed.TotalMilliseconds;
            L($"SIGN {p} product='{fvi.ProductName}' company='{fvi.CompanyName}' fvi={t1:F2}ms signer='{subj}' total={t2:F2}ms");
        }
    }

    static void RunHook(string[] a)
    {
        hide = a.Contains("--hide");
        targets.UnionWith(["notepad.exe", "vlc.exe", "CalculatorApp.exe", "ApplicationFrameHost.exe", "Photos.exe"]);
        targetPfns.UnionWith(["Microsoft.WindowsNotepad_8wekyb3d8bbwe", "Microsoft.WindowsCalculator_8wekyb3d8bbwe", "Microsoft.Windows.Photos_8wekyb3d8bbwe"]);
        proc = Callback;
        var h1 = Native.SetWinEventHook(Native.EVENT_OBJECT_CREATE, Native.EVENT_OBJECT_SHOW, 0, proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        var h2 = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, 0, proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        var h3 = Native.SetWinEventHook(Native.EVENT_OBJECT_CLOAKED, Native.EVENT_OBJECT_UNCLOAKED, 0, proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        L($"hooks {h1} {h2} {h3} hide={hide}");
        var end = Environment.TickCount64 + 40_000;
        Native.MSG msg;
        Native.SetTimer(0, 0, 500, 0);
        while (Environment.TickCount64 < end && Native.GetMessageW(out msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg); Native.DispatchMessageW(ref msg);
        }
        L("done");
    }

    static readonly Dictionary<nint, bool> seen = new();

    static void Callback(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == 0) return;
        if (Native.GetAncestor(hwnd, 2) != hwnd) return; // GA_ROOT
        var now = Native.GetTickCount();
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var path = ImagePath(pid);
        var name = Path.GetFileName(path ?? "");
        if (!targets.Contains(name)) return;
        var pfn = ProcPfn(pid);
        var aumid = WindowAumid(hwnd);
        Native.DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4);
        var vis = Native.IsWindowVisible(hwnd);
        var cls = new StringBuilder(256); Native.GetClassNameW(hwnd, cls, 256);
        var hostedPfn = "";
        if (name.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            Native.EnumChildWindows(hwnd, (c, _) => { Native.GetWindowThreadProcessId(c, out var cp); if (cp != pid) { hostedPfn = ProcPfn(cp) + "/" + Path.GetFileName(ImagePath(cp)); return false; } return true; }, 0);
        }
        var evName = ev switch { Native.EVENT_OBJECT_CREATE => "CREATE", Native.EVENT_OBJECT_SHOW => "SHOW", Native.EVENT_SYSTEM_FOREGROUND => "FG", Native.EVENT_OBJECT_CLOAKED => "CLOAK", Native.EVENT_OBJECT_UNCLOAKED => "UNCLOAK", _ => ev.ToString() };
        var procStart = ProcStartAgeMs(pid);
        double hideMs = -1;
        bool matched = (pfn != null && targetPfns.Contains(pfn)) || (aumid != null && targetPfns.Contains(aumid.Split('!')[0])) || hostedPfn.Length > 1 || name is "vlc.exe" or "notepad.exe";
        if (hide && matched && vis && (ev == Native.EVENT_OBJECT_SHOW || ev == Native.EVENT_SYSTEM_FOREGROUND || ev == Native.EVENT_OBJECT_UNCLOAKED))
        {
            var sw = Stopwatch.StartNew();
            Native.ShowWindow(hwnd, 0);
            hideMs = sw.Elapsed.TotalMilliseconds;
        }
        L($"{evName,-7} lag={now - time}ms hwnd={hwnd:X} pid={pid} {name} cls={cls} vis={vis} cloaked={cloaked} pfn={pfn} aumid={aumid} hosted={hostedPfn} procAge={procStart}ms hide={hideMs:F2}ms");
    }

    static string? ImagePath(uint pid)
    {
        var h = Native.OpenProcess(0x1000, false, pid);
        if (h == 0) return null;
        try { var sb = new StringBuilder(1024); int n = 1024; return Native.QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString() : null; }
        finally { Native.CloseHandle(h); }
    }
    static long ProcStartAgeMs(uint pid)
    {
        var h = Native.OpenProcess(0x1000, false, pid);
        if (h == 0) return -1;
        try { Native.GetProcessTimes(h, out var c, out _, out _, out _); Native.GetSystemTimeAsFileTime(out var n); return (n - c) / 10_000; }
        finally { Native.CloseHandle(h); }
    }
    static string? ProcPfn(uint pid)
    {
        var h = Native.OpenProcess(0x1000, false, pid);
        if (h == 0) return null;
        try { int len = 256; var sb = new StringBuilder(256); return Native.GetPackageFamilyName(h, ref len, sb) == 0 ? sb.ToString() : null; }
        finally { Native.CloseHandle(h); }
    }
    static string? WindowAumid(nint hwnd)
    {
        var iid = typeof(Native.IPropertyStore).GUID;
        if (Native.SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return null;
        try
        {
            var key = new Native.PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            store.GetValue(ref key, out var pv);
            string? s = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null;
            Native.PropVariantClear(ref pv);
            return s;
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    // ---------------- WebView ----------------
    static void RunWebView()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var html = "<html><body style='margin:0;background:#202020;color:#eee;font:14px Segoe UI Variable'><div id=a style='padding:24px'>x</div><script>chrome.webview.addEventListener('message',e=>{document.getElementById('a').textContent=e.data.t;requestAnimationFrame(()=>requestAnimationFrame(()=>chrome.webview.postMessage({painted:e.data.id})));});chrome.webview.postMessage({ready:1})</script></body></html>";
            var dataDir = Path.Combine(Path.GetTempPath(), "latch-spike-wv");
            var swAll = Stopwatch.StartNew();
            for (int run = 0; run < 2; run++)
            {
                // COLD: new window + controller + navigate + paint
                var sw = Stopwatch.StartNew();
                var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                var tEnv = sw.Elapsed.TotalMilliseconds;
                var (win, ctl) = await MakeWindow(env, true);
                var tCtl = sw.Elapsed.TotalMilliseconds;
                var ready = new TaskCompletionSource();
                var painted = new TaskCompletionSource<int>();
                ctl.CoreWebView2.WebMessageReceived += (_, e) => { var j = e.WebMessageAsJson; if (j.Contains("ready")) ready.TrySetResult(); else painted.TrySetResult(1); };
                ctl.CoreWebView2.NavigateToString(html);
                await ready.Task;
                var tReady = sw.Elapsed.TotalMilliseconds;
                ctl.CoreWebView2.PostWebMessageAsJson("{\"t\":\"Notepad is locked\",\"id\":1}");
                await painted.Task;
                L($"COLD run{run}: env={tEnv:F0} controller={tCtl:F0} ready={tReady:F0} painted={sw.Elapsed.TotalMilliseconds:F0}ms");
                await Task.Delay(1500);
                // WARM: hide + suspend, then resume + post + show + paint
                win.Hide(); ctl.IsVisible = false;
                var sus = await ctl.CoreWebView2.TrySuspendAsync();
                await Task.Delay(3000);
                L($"suspended={sus} mem(self)={Process.GetCurrentProcess().WorkingSet64 / 1048576}MB webview procs={WebViewMem()}");
                for (int k = 0; k < 3; k++)
                {
                    var p2 = new TaskCompletionSource<int>();
                    void H(object? s, CoreWebView2WebMessageReceivedEventArgs e) => p2.TrySetResult(1);
                    ctl.CoreWebView2.WebMessageReceived += H;
                    sw.Restart();
                    ctl.CoreWebView2.Resume();
                    ctl.IsVisible = true;
                    win.Show();
                    var tShow = sw.Elapsed.TotalMilliseconds;
                    ctl.CoreWebView2.PostWebMessageAsJson($"{{\"t\":\"VLC is locked {k}\",\"id\":{k + 2}}}");
                    await p2.Task;
                    L($"WARM run{run}.{k}: show={tShow:F1} painted={sw.Elapsed.TotalMilliseconds:F1}ms");
                    ctl.CoreWebView2.WebMessageReceived -= H;
                    await Task.Delay(800);
                    win.Hide(); ctl.IsVisible = false;
                    await ctl.CoreWebView2.TrySuspendAsync();
                    await Task.Delay(1500);
                }
                ctl.Close(); win.Close();
                await Task.Delay(2500);
            }
            L("webview done");
            app.Shutdown();
        };
        app.Run();
    }

    static void RunMica(string[] a)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var opts = new CoreWebView2EnvironmentOptions(a.Contains("--lean") ? "--renderer-process-limit=1 --disable-features=msSmartScreenProtection,Translate --no-first-run" : null);
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Path.GetTempPath(), "latch-spike-wv"), opts);
            var (win, ctl) = await MakeWindow(env, true);
            var done = new TaskCompletionSource();
            ctl.CoreWebView2.WebMessageReceived += (_, e) => done.TrySetResult();
            ctl.CoreWebView2.NavigateToString("<html><body style='margin:0;background:transparent;color:#fff;font:20px Segoe UI Variable'><div style='padding:24px'>Mica check: text over transparent body</div><script>chrome.webview.postMessage(1)</script></body></html>");
            await done.Task; await Task.Delay(1500);
            L("MICA shown at " + win.Left + "," + win.Top);
            await Task.Delay(a.Contains("--long") ? 12000 : 3000);
            win.Hide(); ctl.IsVisible = false;
            if (a.Contains("--low")) ctl.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            await ctl.CoreWebView2.TrySuspendAsync();
            await Task.Delay(8000);
            L($"MEM low={a.Contains("--low")} lean={a.Contains("--lean")} self={Process.GetCurrentProcess().WorkingSet64 / 1048576}MB/{Process.GetCurrentProcess().PrivateMemorySize64 / 1048576}MB webview {WebViewMem()}");
            var sw = Stopwatch.StartNew();
            ctl.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            ctl.CoreWebView2.Resume(); ctl.IsVisible = true; win.Show();
            var p2 = new TaskCompletionSource();
            ctl.CoreWebView2.WebMessageReceived += (_, e) => p2.TrySetResult();
            ctl.CoreWebView2.ExecuteScriptAsync("requestAnimationFrame(()=>requestAnimationFrame(()=>chrome.webview.postMessage(2)))");
            await p2.Task;
            L($"RESUME after low: painted={sw.Elapsed.TotalMilliseconds:F1}ms");
            app.Shutdown();
        };
        app.Run();
    }

    /// <summary>Acts like a tray app: shows a window, hides it after hideAfter s (process lives on), re-shows it after showAfter s more.</summary>
    static void RunTray(int hideAfter, int showAfter)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var w = new Window { Title = "Tray test", Width = 400, Height = 200, Content = "Tray test window" };
            w.Show();
            Thread.Sleep(400); // Like a busy app starting up: can't act on Latch's hide for a while.
            await Task.Delay(hideAfter * 1000);
            w.Hide();
            L("TRAY hidden");
            await Task.Delay(showAfter * 1000);
            w.Show();
            w.Activate();
            L("TRAY shown again, visible=" + w.IsVisible);
            Thread.Sleep(400); // Like a busy app: our UI thread can't act on Latch's hide for a while.
            await Task.Delay(30000);
            app.Shutdown();
        };
        app.Run();
    }

    /// <summary>Four borderless windows side by side over a grey backdrop, to compare DWM shadows.</summary>
    static void RunShadows()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var back = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = 100, Top = 100, Width = 1240, Height = 360, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)), Topmost = true, ShowActivated = false };
            back.Show();
            var labels = new[] { "A chrome -1", "B popup round", "C popup roundsmall", "D chrome 1px" };
            for (int i = 0; i < 4; i++)
            {
                var w = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = 140 + i * 300, Top = 180, Width = 240, Height = 120, Topmost = true, ShowActivated = false, Content = labels[i], Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2B, 0x2B, 0x2B)), Foreground = System.Windows.Media.Brushes.White };
                if (i == 0) System.Windows.Shell.WindowChrome.SetWindowChrome(w, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false });
                if (i == 3) System.Windows.Shell.WindowChrome.SetWindowChrome(w, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(0, 1, 0, 0), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false });
                var h = new WindowInteropHelper(w).EnsureHandle();
                int corner = i == 2 ? 3 : 2; Native.DwmSetWindowAttribute(h, 33, ref corner, 4);
                w.Show();
            }
            await Task.Delay(8000);
            app.Shutdown();
        };
        app.Run();
    }

    static string WebViewMem()
    {
        long ws = 0, priv = 0; int n = 0;
        foreach (var p in Process.GetProcessesByName("msedgewebview2"))
        {
            try { if (ParentPid(p.Id) is int pp && (pp == Environment.ProcessId || IsChildOfSelfWebView(pp))) { ws += p.WorkingSet64; priv += p.PrivateMemorySize64; n++; } } catch { }
        }
        return $"{n} procs ws={ws / 1048576}MB private={priv / 1048576}MB";
    }
    static bool IsChildOfSelfWebView(int pid) { try { return ParentPid(pid) == Environment.ProcessId; } catch { return false; } }
    static int? ParentPid(int pid)
    {
        var h = Native.OpenProcess(0x1000, false, (uint)pid);
        if (h == 0) return null;
        try { var pbi = new Native.PROCESS_BASIC_INFORMATION(); return Native.NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(pbi), out _) == 0 ? (int)pbi.InheritedFromUniqueProcessId : null; }
        finally { Native.CloseHandle(h); }
    }

    static async Task<(Window, CoreWebView2Controller)> MakeWindow(CoreWebView2Environment env, bool show)
    {
        var win = new Window { Width = 360, Height = 280, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, Background = System.Windows.Media.Brushes.Transparent, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        System.Windows.Shell.WindowChrome.SetWindowChrome(win, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false });
        var hwnd = new WindowInteropHelper(win).EnsureHandle();
        win.SourceInitialized += (_, _) => { };
        int mica = 2; Native.DwmSetWindowAttribute(hwnd, 38, ref mica, 4);
        int dark = 1; Native.DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
        HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        if (show) win.Show();
        var ctl = await env.CreateCoreWebView2ControllerAsync(hwnd);
        ctl.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        Native.GetClientRect(hwnd, out var rc);
        ctl.Bounds = new System.Drawing.Rectangle(0, 0, rc.right, rc.bottom);
        return (win, ctl);
    }
}

static class Native
{
    public const uint EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_SHOW = 0x8002, EVENT_SYSTEM_FOREGROUND = 3, EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
    public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    public delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);
    public delegate bool EnumProc(nint hwnd, nint lp);
    [DllImport("user32")] public static extern nint SetWinEventHook(uint min, uint max, nint mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }
    [DllImport("user32")] public static extern int GetMessageW(out MSG m, nint h, uint a, uint b);
    [DllImport("user32")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32")] public static extern nint DispatchMessageW(ref MSG m);
    [DllImport("user32")] public static extern nint SetTimer(nint h, nint id, uint ms, nint p);
    [DllImport("user32")] public static extern nint GetAncestor(nint h, uint f);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32")] public static extern bool IsWindowVisible(nint h);
    [DllImport("user32")] public static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint h, StringBuilder sb, int n);
    [DllImport("user32")] public static extern bool EnumChildWindows(nint h, EnumProc p, nint lp);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
    [DllImport("user32")] public static extern bool GetClientRect(nint h, out RECT r);
    [DllImport("kernel32")] public static extern uint GetTickCount();
    [DllImport("kernel32")] public static extern nint OpenProcess(uint a, bool i, uint pid);
    [DllImport("kernel32")] public static extern bool CloseHandle(nint h);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageNameW(nint h, int f, StringBuilder sb, ref int n);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern int GetPackageFamilyName(nint h, ref int len, StringBuilder sb);
    [DllImport("kernel32")] public static extern bool GetProcessTimes(nint h, out long c, out long e, out long k, out long u);
    [DllImport("kernel32")] public static extern void GetSystemTimeAsFileTime(out long ft);
    [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(nint h, int a, out int v, int s);
    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(nint h, int a, ref int v, int s);
    [StructLayout(LayoutKind.Sequential)] public struct PROCESS_BASIC_INFORMATION { public nint ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId; }
    [DllImport("ntdll")] public static extern int NtQueryInformationProcess(nint h, int c, ref PROCESS_BASIC_INFORMATION p, int l, out int r);
    [StructLayout(LayoutKind.Sequential)] public struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] public struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public nint p; }
    [DllImport("ole32")] public static extern int PropVariantClear(ref PROPVARIANT pv);
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint c); void GetAt(uint i, out PROPERTYKEY k); void GetValue(ref PROPERTYKEY k, out PROPVARIANT v); void SetValue(ref PROPERTYKEY k, ref PROPVARIANT v); void Commit();
    }
    [DllImport("shell32")] public static extern int SHGetPropertyStoreForWindow(nint h, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
}
