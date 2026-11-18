using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Latch.Core;

namespace Latch.Host;

/// <summary>
/// The locked app's stand-in. A locked app's window can only be hidden by that app's own thread, which is often busy
/// starting up (31–171 ms measured), so the window would flash. Latch puts its own window over the exact spot at
/// once instead: a plain window surface with a Windows 11 title bar (the app's icon, its name, "Locked"). It stays as
/// long as that app's unlock prompt, which is centred on it, so the two read as one locked window.
/// Creating, painting and destroying happen on the window-watcher thread; positions and z-order may be read or
/// changed from any thread.
/// </summary>
internal sealed class Cover
{
    private const string ClassName = "LatchCover";

    private sealed record Entry(nint Hwnd, string RuleId, string Name, string? IconKey, Native.RECT Rect);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _byRule = [];
    private readonly Dictionary<nint, Entry> _byHwnd = [];
    private readonly List<nint> _pool = [];
    private readonly Native.WndProc _proc; // Kept alive for the class's lifetime.
    private readonly Func<bool> _dark;
    private readonly Func<string, (string Name, string? IconKey)?> _label;
    private readonly string _iconDir;
    private readonly Action _clicked;
    private bool _registered;
    private nint _below; // Non-zero while a system dialog is up: covers sit just under its group.

    private readonly Action<string> _close;
    private readonly Backdrop _backdrop;

    public Cover(Func<bool> dark, Func<string, (string Name, string? IconKey)?> label, string iconDir, Action clicked, Action<string> close, Backdrop backdrop)
    {
        _backdrop = backdrop;
        _close = close;
        _dark = dark;
        _label = label;
        _iconDir = iconDir;
        _clicked = clicked;
        _proc = WndProc;
    }

    /// <summary>Creates hidden cover windows ahead of time, so showing one costs a move and a show.</summary>
    public void Prepare()
    {
        Register();
        for (var i = 0; i < 2; i++) _pool.Add(Create());
    }

    /// <summary>Covers the app window's on-screen area, once per app. Skips windows that aren't on screen.</summary>
    public void Over(nint appWindow, string ruleId)
    {
        lock (_gate)
        {
            if (_byRule.ContainsKey(ruleId)) return;
        }
        if (Native.DwmGetWindowAttribute(appWindow, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return; // Store frames: never painted.
        if (Native.DwmGetWindowRect(appWindow, out var r) != 0 || r.Right - r.Left < 120 || r.Bottom - r.Top < 80) return;
        if (Native.MonitorFromPoint(new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 }, 0) == 0) return; // Off-screen.
        var label = _label(ruleId) ?? (Copy.Get("app.name"), null);
        var hwnd = _pool.Count > 0 ? _pool[^1] : Create();
        if (hwnd == 0) return;
        _pool.Remove(hwnd);
        var entry = new Entry(hwnd, ruleId, label.Name, label.IconKey, r);
        lock (_gate)
        {
            _byRule[ruleId] = entry;
            _byHwnd[hwnd] = entry;
        }
        // Always on top at first: the app's window is still being hidden right now.
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0040 | 0x0010); // SWP_SHOWWINDOW | SWP_NOACTIVATE
        Native.InvalidateRect(hwnd, 0, false);
        Native.UpdateWindow(hwnd); // Paint now, not at the next idle moment.
    }

    /// <summary>The covered area for an app, so its prompt can centre on it.</summary>
    public Native.RECT? AreaOf(string ruleId)
    {
        lock (_gate) return _byRule.TryGetValue(ruleId, out var e) ? e.Rect : null;
    }

    public nint WindowOf(string ruleId)
    {
        lock (_gate) return _byRule.TryGetValue(ruleId, out var e) ? e.Hwnd : 0;
    }

    public IReadOnlyList<string> Apps()
    {
        lock (_gate) return _byRule.Keys.ToList();
    }

    /// <summary>Watcher thread: the app's prompt is over.</summary>
    public void Remove(string ruleId)
    {
        Entry? e;
        lock (_gate)
        {
            if (!_byRule.Remove(ruleId, out e)) return;
            _byHwnd.Remove(e.Hwnd);
        }
        Native.ShowWindow(e.Hwnd, 0);
        if (_pool.Count < 2) _pool.Add(e.Hwnd);
        else Native.DestroyWindow(e.Hwnd);
    }

    /// <summary>
    /// While Windows Hello is up, neither it nor the card is always-on-top, so the covers drop out of the topmost
    /// band and sit directly under that group (<paramref name="below"/>); zero puts them back on top.
    /// </summary>
    public void Restack(nint below)
    {
        _below = below;
        List<Entry> entries;
        lock (_gate) entries = _byHwnd.Values.ToList();
        foreach (var e in entries) Place(e);
    }

    private readonly Dictionary<string, int> _settling = [];

    /// <summary>
    /// A window of this app was just told to hide, but the app's own thread hasn't done it yet. Until it has, the
    /// cover stays always-on-top, even while Hello is up, so the app's window is never seen above it.
    /// </summary>
    public void Settling(string ruleId, bool on)
    {
        Entry? e;
        lock (_gate)
        {
            var n = _settling.GetValueOrDefault(ruleId) + (on ? 1 : -1);
            if (n > 0) _settling[ruleId] = n;
            else _settling.Remove(ruleId);
            _byRule.TryGetValue(ruleId, out e);
        }
        if (e is not null) Place(e);
    }

    private void Place(Entry e)
    {
        const uint keep = 0x0001 | 0x0002 | 0x0010; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
        bool settling;
        lock (_gate) settling = _settling.ContainsKey(e.RuleId);
        if (_below == 0 || settling)
        {
            Native.SetWindowPos(e.Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, keep);
        }
        else
        {
            Native.SetWindowPos(e.Hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, keep);
            Native.SetWindowPos(e.Hwnd, _below, 0, 0, 0, 0, keep);
        }
    }

    private nint Create()
    {
        Register();
        var hwnd = Native.CreateWindowExW(
            0x00000008 | 0x00000080 | 0x08000000, // WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            ClassName, "", unchecked((int)0x80000000), // WS_POPUP
            0, 0, 100, 100, 0, 0, Native.GetModuleHandleW(null), 0);
        if (hwnd == 0) return 0;
        var corner = 2; // Rounded like the app's own window on Windows 11.
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        return hwnd;
    }

    private void Register()
    {
        if (_registered) return;
        var wc = new Native.WNDCLASSEXW
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = Native.GetModuleHandleW(null),
            style = 0x00020000, // CS_DROPSHADOW: a window shadow without extending the DWM frame (which lets the background bleed through).
            hCursor = Native.LoadCursorW(0, 32512), // IDC_ARROW
            lpszClassName = ClassName,
        };
        _registered = Native.RegisterClassExW(ref wc) != 0;
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case 0x0014: // WM_ERASEBKGND: everything is painted in WM_PAINT.
                return 1;
            case 0x000F: // WM_PAINT
                Paint(hwnd);
                return 0;
            case 0x0021: // WM_MOUSEACTIVATE: never take focus; outside the close button, hand it to the prompt.
                if (!OverClose(hwnd)) _clicked();
                return 3; // MA_NOACTIVATE
            case 0x0200: // WM_MOUSEMOVE
                SetHover(hwnd, OverClose(hwnd));
                var track = new Native.TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<Native.TRACKMOUSEEVENT>(), dwFlags = 0x2, hwndTrack = hwnd }; // TME_LEAVE
                Native.TrackMouseEvent(ref track);
                return 0;
            case 0x02A3: // WM_MOUSELEAVE
                SetHover(hwnd, false);
                return 0;
            case 0x0201: // WM_LBUTTONDOWN
                if (OverClose(hwnd))
                {
                    _pressed = hwnd;
                    Native.SetCapture(hwnd);
                    Native.InvalidateRect(hwnd, 0, false);
                }
                return 0;
            case 0x0202: // WM_LBUTTONUP: like a real close button, it acts on release over the button.
                if (_pressed == hwnd)
                {
                    _pressed = 0;
                    Native.ReleaseCapture();
                    Native.InvalidateRect(hwnd, 0, false);
                    Entry? e;
                    lock (_gate) _byHwnd.TryGetValue(hwnd, out e);
                    if (OverClose(hwnd) && e is not null) _close(e.RuleId);
                }
                return 0;
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private nint _hover, _pressed;

    private void SetHover(nint hwnd, bool over)
    {
        var next = over ? hwnd : (_hover == hwnd ? 0 : _hover);
        if (next == _hover) return;
        var old = _hover;
        _hover = next;
        if (old != 0) Native.InvalidateRect(old, 0, false);
        if (next != 0) Native.InvalidateRect(next, 0, false);
    }

    /// <summary>The caption close button: 46 × 32 at the top-right corner, like Windows 11's.</summary>
    private static Native.RECT CloseRect(nint hwnd)
    {
        Native.GetClientRect(hwnd, out var rc);
        var scale = Native.GetDpiForWindow(hwnd) / 96.0;
        return new Native.RECT { Left = rc.Right - (int)Math.Round(46 * scale), Top = 0, Right = rc.Right, Bottom = (int)Math.Round(32 * scale) };
    }

    private static bool OverClose(nint hwnd)
    {
        Native.GetCursorPos(out var pt);
        Native.ScreenToClient(hwnd, ref pt);
        var r = CloseRect(hwnd);
        return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
    }

    private void Paint(nint hwnd)
    {
        var hdc = Native.BeginPaint(hwnd, out var ps);
        try
        {
            Entry? e;
            lock (_gate) _byHwnd.TryGetValue(hwnd, out e);
            Native.GetClientRect(hwnd, out var rc);
            var dark = _dark();
            var surface = dark ? 0x202020u : 0xF3F3F3u;
            var scale = Native.GetDpiForWindow(hwnd) / 96.0;
            // The title bar in the Windows 11 window colour (what Mica falls back to); below it, a blurred picture of
            // the app when there is one, else the same colour.
            var bar = (int)Math.Round(32 * scale);
            if (e is not null && rc.Bottom > bar && _backdrop.Use(e.RuleId, dark, p => Stretch(hdc, p, rc, bar)))
                Fill(hdc, new Native.RECT { Right = rc.Right, Bottom = bar }, surface);
            else
                Fill(hdc, rc, surface);
            if (e is null) return;
            // Never wait on text layout here: if a piece isn't rendered yet, ask for it and repaint when it's ready.
            var left = Piece(PieceKey.Left(e, scale, dark), hwnd);
            var right = Piece(PieceKey.Right(scale, dark), hwnd);
            var state = _pressed == hwnd ? CloseState.Pressed : _hover == hwnd ? CloseState.Hover : CloseState.Normal;
            var close = Piece(PieceKey.CloseButton(state, scale, dark), hwnd);
            var closeRect = CloseRect(hwnd);
            if (close is { } c) Blit(hdc, c, closeRect.Left, 0, c.Width);
            var rightX = closeRect.Left - (right?.Width ?? 0);
            if (right is { } r) Blit(hdc, r, rightX, 0, r.Width);
            if (left is { } l) Blit(hdc, l, 0, 0, Math.Min(l.Width, Math.Max(0, rightX - (int)Math.Round(16 * scale))));
        }
        finally
        {
            Native.EndPaint(hwnd, ref ps);
        }
    }

    private sealed record Bitmap(nint Handle, int Width, int Height);

    /// <summary>What a title bar piece depends on. Left: this app's icon and name. Right: the lock and "Locked".</summary>
    private enum CloseState { None, Normal, Hover, Pressed }

    private sealed record PieceKey(string? Name, string? IconKey, double Scale, bool Dark, CloseState Close = CloseState.None)
    {
        public static PieceKey Left(Entry e, double scale, bool dark) => new(e.Name, e.IconKey, scale, dark);

        public static PieceKey Right(double scale, bool dark) => new(null, null, scale, dark);

        public static PieceKey CloseButton(CloseState state, double scale, bool dark) => new(null, null, scale, dark, state);
    }

    private readonly Dictionary<PieceKey, Bitmap?> _pieces = [];
    private readonly HashSet<nint> _waiting = [];
    private Thread? _renderer;
    private readonly System.Collections.Concurrent.BlockingCollection<PieceKey> _queue = [];

    private Bitmap? Piece(PieceKey key, nint repaint)
    {
        lock (_gate)
        {
            if (_pieces.TryGetValue(key, out var done)) return done;
            _waiting.Add(repaint);
        }
        Request(key);
        return null;
    }

    /// <summary>Renders every locked app's title bar ahead of time, off the event thread.</summary>
    public void Prerender(IEnumerable<(string Name, string? IconKey)> apps)
    {
        var scale = Native.GetDpiForSystem() / 96.0;
        foreach (var dark in new[] { _dark() })
        {
            Request(PieceKey.Right(scale, dark));
            foreach (var state in new[] { CloseState.Normal, CloseState.Hover, CloseState.Pressed }) Request(PieceKey.CloseButton(state, scale, dark));
            foreach (var (name, icon) in apps) Request(new PieceKey(name, icon, scale, dark));
        }
    }

    private void Request(PieceKey key)
    {
        lock (_gate)
        {
            if (_pieces.ContainsKey(key)) return;
            if (_renderer is null)
            {
                // WPF text layout can stall for seconds the first time in a process; it must never run on the
                // thread that receives window events, so it gets a thread of its own.
                _renderer = new Thread(RenderLoop) { IsBackground = true, Name = "Latch cover renderer", Priority = ThreadPriority.BelowNormal };
                _renderer.SetApartmentState(ApartmentState.STA);
                _renderer.Start();
            }
        }
        _queue.Add(key);
    }

    private void RenderLoop()
    {
        foreach (var key in _queue.GetConsumingEnumerable())
        {
            lock (_gate)
            {
                if (_pieces.ContainsKey(key)) continue;
            }
            var bitmap = Render(key);
            List<nint> repaint;
            lock (_gate)
            {
                _pieces[key] = bitmap;
                repaint = _waiting.ToList();
                _waiting.Clear();
            }
            foreach (var w in repaint) Native.InvalidateRect(w, 0, false); // Safe from any thread; painted by the owner.
        }
    }

    /// <summary>
    /// A piece of a Windows 11 caption, 32 px high: the app's 16 px icon (rounded like on the Hello card) and its
    /// name in 12 px Inter, or a lock and "Locked" in the muted colour. Laid out by WPF (GDI can't load a private
    /// font file as cleanly, nor render it as well) into an opaque premultiplied bitmap.
    /// </summary>
    private Bitmap? Render(PieceKey key)
    {
        try
        {
            const double height = 32, pad = 12, icon = 16, gap = 10, size = 12, maxName = 420;
            var scale = key.Scale;
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            var ltr = System.Windows.FlowDirection.LeftToRight;
            var text = new SolidColorBrush(key.Dark ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1A, 0x1A, 0x1A));
            var muted = new SolidColorBrush(key.Dark ? Color.FromRgb(0xA8, 0xA8, 0xA8) : Color.FromRgb(0x5F, 0x5F, 0x5F));
            var background = new SolidColorBrush(key.Dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
            var face = new Typeface(Theme.UiFont(), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);

            var visual = new DrawingVisual();
            TextOptions.SetTextRenderingMode(visual, System.Windows.Media.TextRenderingMode.Grayscale);
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            double width;
            using (var dc = visual.RenderOpen())
            {
                if (key.Close != CloseState.None)
                {
                    // Windows 11's close button: the ChromeClose glyph at 10 px, red on hover, a deeper red pressed.
                    width = 46;
                    var fill = key.Close switch
                    {
                        CloseState.Hover => Color.FromRgb(0xC4, 0x2B, 0x1C),
                        CloseState.Pressed => Color.FromRgb(0xA3, 0x26, 0x19),
                        _ => key.Dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3),
                    };
                    var glyphColor = key.Close == CloseState.Normal ? text : Brushes.White;
                    var closeFace = new Typeface(new FontFamily(Environment.OSVersion.Version.Build >= 22000 ? "Segoe Fluent Icons" : "Segoe MDL2 Assets"), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
                    var x = new FormattedText("\uE8BB", culture, ltr, closeFace, 10, glyphColor, scale);
                    dc.DrawRectangle(new SolidColorBrush(fill), null, new System.Windows.Rect(0, 0, width, height));
                    dc.DrawText(x, new System.Windows.Point((width - x.Width) / 2, (height - x.Height) / 2));
                }
                else if (key.Name is { } name)
                {
                    var title = new FormattedText(name, culture, ltr, face, size, text, scale)
                    {
                        MaxTextWidth = maxName, MaxLineCount = 1, Trimming = System.Windows.TextTrimming.CharacterEllipsis,
                    };
                    var file = key.IconKey is null ? null : Path.Combine(_iconDir, key.IconKey + ".png");
                    var hasIcon = file is not null && File.Exists(file);
                    var nameX = hasIcon ? pad + icon + gap : pad;
                    width = WholePixels(nameX + title.Width + pad, scale);
                    dc.DrawRectangle(background, null, new System.Windows.Rect(0, 0, width, height)); // Opaque, like the surface.
                    if (hasIcon)
                    {
                        var image = BitmapDecoder.Create(new Uri(file!), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                        // Rounded like the icon on the card above Windows Hello (6 px at 32 px, so 3 px at 16 px).
                        var iconRect = new System.Windows.Rect(pad, (height - icon) / 2, icon, icon);
                        dc.PushClip(new RectangleGeometry(iconRect, 3, 3));
                        dc.DrawImage(image, iconRect);
                        dc.Pop();
                    }
                    dc.DrawText(title, new System.Windows.Point(nameX, (height - title.Height) / 2));
                }
                else
                {
                    var glyphFace = new Typeface(new FontFamily(Environment.OSVersion.Version.Build >= 22000 ? "Segoe Fluent Icons" : "Segoe MDL2 Assets"), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
                    var locked = new FormattedText(Copy.Get("cover.locked"), culture, ltr, face, size, muted, scale);
                    var glyph = new FormattedText("\uE72E", culture, ltr, glyphFace, size, muted, scale);
                    width = WholePixels(glyph.Width + 6 + locked.Width + 8, scale);
                    dc.DrawRectangle(background, null, new System.Windows.Rect(0, 0, width, height));
                    dc.DrawText(glyph, new System.Windows.Point(0, (height - glyph.Height) / 2 + 1));
                    dc.DrawText(locked, new System.Windows.Point(glyph.Width + 6, (height - locked.Height) / 2));
                }
            }
            var pw = (int)Math.Ceiling(width * scale);
            var ph = (int)Math.Ceiling(height * scale);
            var target = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            target.Render(visual);
            var bytes = new byte[pw * ph * 4];
            target.CopyPixels(bytes, pw * 4, 0);
            var info = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = pw, biHeight = -ph, biPlanes = 1, biBitCount = 32 };
            var bmp = Native.CreateDIBSection(0, ref info, 0, out var bits, 0, 0);
            if (bmp == 0) return null;
            Marshal.Copy(bytes, 0, bits, bytes.Length);
            return new Bitmap(bmp, pw, ph);
        }
        catch (Exception ex)
        {
            Log.Error("cover title bar", ex);
            return null;
        }
    }

    /// <summary>Rounds a width up to whole device pixels, so the opaque background covers the last pixel column too.</summary>
    private static double WholePixels(double dip, double scale) => Math.Ceiling(dip * scale) / scale;

    private static void Blit(nint hdc, Bitmap b, int x, int y, int width)
    {
        if (width <= 0) return;
        var mem = Native.CreateCompatibleDC(hdc);
        var old = Native.SelectObject(mem, b.Handle);
        Native.BitBlt(hdc, x, y, width, b.Height, mem, 0, 0, 0x00CC0020); // SRCCOPY: the pieces are opaque.
        Native.SelectObject(mem, old);
        Native.DeleteDC(mem);
    }

    /// <summary>The picture spans the whole window; the part under the title bar is left out.</summary>
    private static void Stretch(nint hdc, Backdrop.Picture p, Native.RECT rc, int bar)
    {
        var mem = Native.CreateCompatibleDC(hdc);
        var old = Native.SelectObject(mem, p.Bitmap);
        var sy = (int)Math.Round((double)bar * p.Height / rc.Bottom);
        Native.SetStretchBltMode(hdc, 3); // COLORONCOLOR: the picture is already smooth, so plain scaling is enough (and fast).
        Native.StretchBlt(hdc, 0, bar, rc.Right, rc.Bottom - bar, mem, 0, sy, p.Width, p.Height - sy, 0x00CC0020);
        Native.SelectObject(mem, old);
        Native.DeleteDC(mem);
    }

    /// <summary>0xRRGGBB to a GDI COLORREF (0x00BBGGRR).</summary>
    private static uint Rgb(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private static void Fill(nint hdc, Native.RECT r, uint rgb)
    {
        var brush = Native.CreateSolidBrush(Rgb(rgb));
        Native.FillRect(hdc, ref r, brush);
        Native.DeleteObject(brush);
    }

}
