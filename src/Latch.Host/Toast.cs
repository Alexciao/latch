using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Latch.Host;

/// <summary>
/// Latch's notification card: the same shape as the compact unlock card (icon, title, one caption), shown at the
/// bottom centre of the monitor under the cursor. It slides up and fades in, steps aside the moment the pointer
/// touches it, and leaves on its own after a few seconds. Native WPF, so it costs nothing while idle.
/// </summary>
internal sealed class Toast
{
    private static readonly TimeSpan Visible = TimeSpan.FromSeconds(3.5);
    private const double WidthDip = 360;
    private const double ShadowMargin = 16; // Room for the drop shadow inside the transparent window.
    private const double ScreenPadding = 24;

    private readonly Func<bool> _dark;
    private Window? _win;
    private Border? _card;
    private TextBlock? _title, _caption;
    private TranslateTransform? _slide;
    private DispatcherTimer? _timer;
    private bool _leaving;

    public Toast(Func<bool> dark) => _dark = dark;

    public void Show(string title, string? caption = null)
    {
        Ensure();
        ApplyTheme();
        _title!.Text = title;
        _caption!.Text = caption ?? "";
        _caption.Visibility = caption is null ? Visibility.Collapsed : Visibility.Visible;
        _leaving = false;
        _timer!.Stop();

        _win!.Show();
        _win.UpdateLayout();
        PlaceBottomCentre();
        Animate(fromY: 12, toY: 0, fromOpacity: _card!.Opacity, toOpacity: 1, ms: 200, ease: new CubicEase { EasingMode = EasingMode.EaseOut });
        _timer.Start();
    }

    private void Leave(double ms)
    {
        if (_win is null || _leaving || !_win.IsVisible) return;
        _leaving = true;
        _timer!.Stop();
        Animate(fromY: _slide!.Y, toY: 6, fromOpacity: _card!.Opacity, toOpacity: 0, ms: ms, ease: new QuadraticEase { EasingMode = EasingMode.EaseIn }, done: () =>
        {
            if (_leaving) _win.Hide();
        });
    }

    private void Animate(double fromY, double toY, double fromOpacity, double toOpacity, double ms, IEasingFunction ease, Action? done = null)
    {
        // Honour "Show animations in Windows" being off: jump straight to the end state.
        if (!SystemParameters.ClientAreaAnimation) ms = 0;
        var duration = new Duration(TimeSpan.FromMilliseconds(ms));
        var fade = new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = ease };
        if (done is not null) fade.Completed += (_, _) => done();
        _card!.BeginAnimation(UIElement.OpacityProperty, fade);
        _slide!.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, toY, duration) { EasingFunction = ease });
    }

    private void Ensure()
    {
        if (_win is not null) return;
        _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _caption = new TextBlock { FontSize = 13, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _caption } };
        var icon = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var ico = Path.Combine(AppContext.BaseDirectory, "Latch.ico");
        if (File.Exists(ico))
        {
            var decoder = BitmapDecoder.Create(new Uri(ico), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            icon.Source = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 64)).First();
        }
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var row = new DockPanel { Children = { icon, text } };
        DockPanel.SetDock(icon, Dock.Left);
        _slide = new TranslateTransform();
        _card = new Border
        {
            Width = WidthDip,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(ShadowMargin),
            Child = row,
            Opacity = 0,
            RenderTransform = _slide,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.28, Color = Colors.Black },
        };
        _card.MouseEnter += (_, _) => Leave(110); // Steps aside on hover so it never sits on what you're reaching for.
        _win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            FontFamily = Theme.UiFont(),
            Content = _card,
            Left = -10000,
            Top = -10000,
        };
        _win.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_win).Handle;
            var ex = Native.GetWindowLongPtrW(hwnd, -20);
            Native.SetWindowLongPtrW(hwnd, -20, ex | 0x08000000 | 0x80); // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
        };
        _timer = new DispatcherTimer { Interval = Visible };
        _timer.Tick += (_, _) => Leave(160);
    }

    private void ApplyTheme()
    {
        var dark = _dark();
        _card!.Background = new SolidColorBrush(dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xFB, 0xFB, 0xFB));
        _card.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x17, 0, 0, 0));
        _title!.Foreground = new SolidColorBrush(dark ? Color.FromRgb(0xF1, 0xF1, 0xF1) : Color.FromRgb(0x1B, 0x1B, 0x1B));
        _caption!.Foreground = new SolidColorBrush(dark ? Color.FromRgb(0xB3, 0xB3, 0xB3) : Color.FromRgb(0x61, 0x61, 0x61));
    }

    /// <summary>Bottom centre of the work area (above the taskbar) of the monitor under the cursor.</summary>
    private void PlaceBottomCentre()
    {
        var hwnd = new WindowInteropHelper(_win!).Handle;
        Native.GetCursorPos(out var pt);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfoW(Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST), ref info)) return;
        var work = info.rcWork;
        // Move onto the monitor first so the window takes its DPI, then measure in its pixels.
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, work.Left, work.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _win!.UpdateLayout();
        Native.GetWindowRect(hwnd, out var r);
        var scale = Native.GetDpiForWindow(hwnd) / 96.0;
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        var x = work.Left + (work.Right - work.Left - w) / 2;
        var y = work.Bottom - h - (int)Math.Round((ScreenPadding - ShadowMargin) * scale);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
