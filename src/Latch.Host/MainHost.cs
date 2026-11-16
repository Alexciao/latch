using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Latch.Core;
using Latch.Core.Contract;
using Latch.Core.Storage;
using Microsoft.Web.WebView2.Core;

namespace Latch.Host;

/// <summary>
/// The main window. Its web view is created only when the owner opens it and disposed when it closes, so a
/// hidden Latch costs nothing here. The window stays hidden until the page reports its first paint.
/// </summary>
internal sealed class MainHost(DataPaths paths, Rpc rpc, Func<ThemeChoice> theme, Action closed)
{
    private Window? _win;
    private nint _hwnd;
    private CoreWebView2Controller? _ctl;
    private bool _shown;

    public bool IsOpen => _win is not null;

    public nint Hwnd => _hwnd;

    public async void Open()
    {
        try
        {
            if (_win is not null)
            {
                if (_shown)
                {
                    if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
                    _win.Activate();
                    Foreground.Force(_hwnd);
                }
                return;
            }
            var mica = Theme.MicaSupported;
            var dark = Theme.IsDark(theme());
            var w = new Window
            {
                Title = Copy.Get("app.name"),
                Width = 960,
                Height = 660,
                MinWidth = 720,
                MinHeight = 520,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = mica ? Brushes.Transparent : new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF7, 0xF7, 0xF7)),
            };
            var icon = Path.Combine(AppContext.BaseDirectory, "Latch.ico");
            if (File.Exists(icon)) w.Icon = BitmapFrame.Create(new Uri(icon));
            _win = w;
            _hwnd = WebUi.Prepare(w);
            Theme.ApplyChrome(_hwnd, dark, mica, round: false);
            if (mica)
            {
                // Native title bar kept; the client area shows the Mica backdrop through the transparent web view.
                var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                Native.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            }
            w.SizeChanged += (_, _) =>
            {
                if (_ctl is not null) WebUi.FitToClient(_ctl, _hwnd);
            };
            w.Closed += (_, _) => OnClosed();
            // Shown but DWM-cloaked: Chromium renders (a hidden window gets no animation frames) yet nothing is on
            // screen until the page reports its first paint.
            var cloak = 1;
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_CLOAK, ref cloak, sizeof(int));
            w.ShowActivated = false;
            w.Show();
            _ctl = await WebUi.CreateAsync(paths, _hwnd, rpc, theme());
            WebUi.FitToClient(_ctl, _hwnd);
            _ctl.CoreWebView2.Navigate(WebUi.PageUrl("index.html"));
            // Fallback in case the page never reports ready (it always should).
            _ = Task.Delay(3000).ContinueWith(_ => Ready(), TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception e)
        {
            Log.Error("main window", e);
        }
    }

    /// <summary>The page painted: show the window now, so it never flashes white or half-built.</summary>
    public void Ready()
    {
        if (_win is null || _shown) return;
        _shown = true;
        WebUi.FitToClient(_ctl!, _hwnd);
        var cloak = 0;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_CLOAK, ref cloak, sizeof(int));
        Foreground.Force(_hwnd);
        _win.Activate();
        _ctl!.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
    }

    public void Close() => _win?.Close();

    public void Post(string json)
    {
        try
        {
            _ctl?.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception e)
        {
            Log.Error("main post", e);
        }
    }

    public void ApplyTheme(ThemeChoice choice)
    {
        if (_win is null || _ctl is null) return;
        WebUi.SetTheme(_ctl.CoreWebView2, choice);
        Theme.ApplyChrome(_hwnd, Theme.IsDark(choice), Theme.MicaSupported, round: false);
    }

    private void OnClosed()
    {
        _ctl?.Close();
        _ctl = null;
        _win = null;
        _hwnd = 0;
        _shown = false;
        closed();
    }
}
