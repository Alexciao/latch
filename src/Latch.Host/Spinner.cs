using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Path = System.Windows.Shapes.Path;
using Windows.UI.ViewManagement;

namespace Latch.Host;

/// <summary>
/// A recreation of the Windows 11 indeterminate ProgressRing (WinUI draws the real one; Win32 and WPF apps can't host
/// it without XAML Islands): 32 px, 4 px stroke with round caps, the accent fill colour WinUI uses, and an arc that
/// grows, shrinks and rotates. Shown on a locked app's cover while Windows Hello is starting. It is owned by the
/// cover, so it always sits on it, and it animates only while visible.
/// </summary>
internal sealed class Spinner
{
    private const double Size = 32, Stroke = 4, Box = 48;
    private Window? _win;
    private Path? _arc;
    private PathFigure? _figure;
    private ArcSegment? _segment;
    private DateTime _started;
    private bool _animating;
    private int _generation;

    /// <summary>Shows the ring centred on <paramref name="area"/> (device pixels), on top of <paramref name="owner"/>, after a short delay.</summary>
    public async void Show(nint owner, Native.RECT area, bool dark)
    {
        var generation = ++_generation;
        await Task.Delay(100); // If Hello is quick, never blink a spinner.
        if (generation != _generation || !Native.IsWindow(owner)) return;
        Ensure();
        _arc!.Stroke = new SolidColorBrush(Accent(dark));
        new WindowInteropHelper(_win!).Owner = owner; // Owned by the cover: always directly on it.
        _win!.Show();
        var hwnd = new WindowInteropHelper(_win).Handle;
        // Move onto the cover's monitor first so the window takes its DPI, then centre it in that monitor's pixels.
        Native.SetWindowPos(hwnd, 0, area.Left, area.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        var px = (int)Math.Round(Box * Native.GetDpiForWindow(hwnd) / 96.0);
        Native.SetWindowPos(hwnd, 0, (area.Left + area.Right - px) / 2, (area.Top + area.Bottom - px) / 2, px, px, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        _started = DateTime.UtcNow;
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += Tick;
        }
        Fade(1, 150);
    }

    public void Hide()
    {
        _generation++;
        if (_win is null || !_win.IsVisible) return;
        Fade(0, 120, () =>
        {
            _win.Hide();
            if (_animating)
            {
                _animating = false;
                CompositionTarget.Rendering -= Tick;
            }
        });
    }

    private void Fade(double to, double ms, Action? done = null)
    {
        if (!SystemParameters.ClientAreaAnimation) ms = 0;
        var animation = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(ms)));
        if (done is not null) animation.Completed += (_, _) => done();
        _arc!.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>
    /// One cycle (1.6 s): the arc's end sweeps ahead 270° while its start holds, then the start catches up, and the
    /// whole ring turns, so the arc grows and shrinks as it spins, like WinUI's ring.
    /// </summary>
    private void Tick(object? sender, EventArgs e)
    {
        const double period = 1.6;
        var t = (DateTime.UtcNow - _started).TotalSeconds;
        var cycle = Math.Floor(t / period);
        var p = t / period - cycle;
        static double Ease(double x) => x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
        double start, end;
        if (p < 0.5)
        {
            start = 0;
            end = 10 + Ease(p * 2) * 260;
        }
        else
        {
            start = Ease((p - 0.5) * 2) * 260;
            end = 270;
        }
        var rotation = cycle * 260 + t * 200;
        SetArc(rotation + start, rotation + end);
    }

    private void SetArc(double fromDeg, double toDeg)
    {
        const double r = (Size - Stroke) / 2, c = Box / 2;
        static Point At(double deg) => new(c + r * Math.Cos((deg - 90) * Math.PI / 180), c + r * Math.Sin((deg - 90) * Math.PI / 180));
        _figure!.StartPoint = At(fromDeg);
        _segment!.Point = At(toDeg);
        _segment.IsLargeArc = toDeg - fromDeg > 180;
    }

    private void Ensure()
    {
        if (_win is not null) return;
        _segment = new ArcSegment { Size = new Size((Size - Stroke) / 2, (Size - Stroke) / 2), SweepDirection = SweepDirection.Clockwise };
        _figure = new PathFigure { IsClosed = false, Segments = { _segment } };
        _arc = new Path
        {
            Data = new PathGeometry { Figures = { _figure } },
            StrokeThickness = Stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        SetArc(0, 10);
        _win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Width = Box,
            Height = Box,
            Left = -10000,
            Top = -10000,
            Content = new System.Windows.Controls.Canvas { Width = Box, Height = Box, Children = { _arc } },
        };
        var hwnd = new WindowInteropHelper(_win).EnsureHandle();
        var ex = Native.GetWindowLongPtrW(hwnd, -20);
        Native.SetWindowLongPtrW(hwnd, -20, ex | 0x20 | 0x80 | 0x08000000); // WS_EX_TRANSPARENT | TOOLWINDOW | NOACTIVATE
    }

    /// <summary>WinUI's AccentFillColorDefault: the accent's Light 2 shade on dark, Dark 1 on light.</summary>
    private static Color Accent(bool dark)
    {
        try
        {
            var c = new UISettings().GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch (Exception)
        {
            return dark ? Color.FromRgb(0x99, 0xEB, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8);
        }
    }
}
