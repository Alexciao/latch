using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Latch.Core;

namespace Latch.Host;

/// <summary>
/// A heavily blurred picture of each locked app, for the background of its cover. A locked app's window is hidden
/// the moment it shows, before it has drawn anything worth showing, so the picture is taken earlier instead, while
/// the app is unlocked: a second after its windows come back or it comes to the front, every 10 s while it stays in
/// front and the owner is active, and as it loses the front. The full-size capture is reduced and blurred at once
/// and never kept. The blurred picture stays in memory (about 5 MB per app for a 3440 px wide window) and, reduced,
/// on disk (about 100 KB per app), so the first cover after Latch starts has one too; it shows nothing more than
/// the cover itself shows anyone. It all runs on a thread of its own, so the lock path never waits for it: the cover
/// just paints whatever picture is ready.
/// </summary>
internal sealed class Backdrop(LatchService service, Func<bool> dark, string dir)
{
    /// <summary>Reduced, blurred, untinted picture: BGR bytes, 3 per pixel.</summary>
    private sealed record Small(byte[] Pixels, int Width, int Height);

    /// <summary>The cover's background, tinted for one theme, at half the app window's size.</summary>
    public sealed record Picture(nint Bitmap, int Width, int Height, bool Dark);

    private readonly BlockingCollection<nint> _foreground = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Small> _small = [];
    private readonly Dictionary<string, Picture> _pictures = [];
    private readonly ConcurrentQueue<string> _retint = new();
    private Thread? _thread;

    private static readonly bool Trace = Environment.GetEnvironmentVariable("LATCH_TRACE") == "1";
    private const int FirstDelayMs = 1000, RepeatMs = 10_000;

    /// <summary>Starts the thread, which first loads the pictures saved by the last run.</summary>
    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Latch backdrop", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>A window came to the front, or an app's windows came back after an unlock. Costs a queue add.</summary>
    public void Foreground(nint hwnd) => _foreground.Add(hwnd);

    /// <summary>
    /// Paints the picture for an app's cover in the current theme, if there is one (false: the cover stays plain).
    /// Painting happens inside the lock, so the picture can't be replaced halfway.
    /// </summary>
    public bool Use(string ruleId, bool isDark, Action<Picture> paint)
    {
        lock (_gate)
        {
            if (!_pictures.TryGetValue(ruleId, out var p)) return false;
            if (p.Dark != isDark)
            {
                // The theme changed since the picture was tinted: tint it again, plain until then.
                _retint.Enqueue(ruleId);
                _foreground.Add(0);
                return false;
            }
            paint(p);
            return true;
        }
    }

    private void Loop()
    {
        nint current = 0;
        string? currentRule = null;
        long next = long.MaxValue;
        uint lastInput = 0;
        Load();
        while (true)
        {
            var wait = current == 0 ? Timeout.Infinite : (int)Math.Clamp(next - Environment.TickCount64, 0, int.MaxValue);
            try
            {
                if (_foreground.TryTake(out var hwnd, wait))
                {
                    while (_retint.TryDequeue(out var r)) Retint(r);
                    if (hwnd == 0 || hwnd == current) continue;
                    if (current != 0) Capture(current, currentRule!); // Its last look before something else came to the front.
                    (current, currentRule) = Resolve(hwnd);
                    next = Environment.TickCount64 + FirstDelayMs;
                    continue;
                }
                if (current == 0) continue;
                next = Environment.TickCount64 + RepeatMs;
                // Nothing changes on screen while nobody's there: skip the capture if there's been no input since.
                var info = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>() };
                bool have;
                lock (_gate) have = _small.ContainsKey(currentRule!);
                if (Native.GetLastInputInfo(ref info) && info.dwTime == lastInput && have) continue;
                lastInput = info.dwTime;
                Capture(current, currentRule!);
            }
            catch (Exception e)
            {
                Log.Error("backdrop", e);
                current = 0;
            }
        }
    }

    /// <summary>The window and its app, if it's a window of a locked app that is unlocked right now.</summary>
    private (nint, string?) Resolve(nint hwnd)
    {
        hwnd = Native.GetAncestor(hwnd, Native.GA_ROOT);
        return Rule(hwnd) is { } rule ? (hwnd, rule) : (0, null);
    }

    private string? Rule(nint hwnd)
    {
        if (hwnd == 0) return null;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != 0 && service.UnlockedAppOf((int)pid) is { } app) return app.RuleId;
        // A Store app's frame belongs to ApplicationFrameHost and hosts the app's window as a child.
        string? found = null;
        Native.EnumChildWindows(hwnd, (child, _) =>
        {
            Native.GetWindowThreadProcessId(child, out var p);
            if (p != pid && service.UnlockedAppOf((int)p) is { } a) found = a.RuleId;
            return found is null;
        }, 0);
        return found;
    }

    private readonly Dictionary<string, long> _lastCapture = [];

    private void Capture(nint hwnd, string ruleId)
    {
        // Switching back and forth quickly doesn't need a picture each time.
        if (_lastCapture.TryGetValue(ruleId, out var last) && Environment.TickCount64 - last < 2000) return;
        // Only while it's on screen and still unlocked: never a locked, hidden or minimised window.
        if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || Rule(hwnd) != ruleId) return;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return;
        if (!Native.GetWindowRect(hwnd, out var wr) || Native.DwmGetWindowRect(hwnd, out var dr) != 0) return;
        int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
        if (w < 120 || h < 80 || w > 16384 || h > 16384) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        var info = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dc = Native.CreateCompatibleDC(0);
        var bmp = Native.CreateDIBSection(0, ref info, 0, out var bits, 0, 0);
        Small? small = null;
        var printed = 0.0;
        try
        {
            if (dc == 0 || bmp == 0) return;
            var old = Native.SelectObject(dc, bmp);
            // PW_RENDERFULLCONTENT: the window as DWM composes it, GPU-drawn content (Chromium, Electron) included.
            var ok = Native.PrintWindow(hwnd, dc, 2);
            Native.GdiFlush();
            Native.SelectObject(dc, old);
            if (!ok) return;
            printed = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            var scale = Native.GetDpiForWindow(hwnd) / 96.0;
            // Only the visible window (the invisible resize borders around it are left out).
            int x0 = dr.Left - wr.Left, y0 = dr.Top - wr.Top;
            small = Reduce(bits, w, x0, y0, Math.Min(dr.Right - dr.Left, w - x0), Math.Min(dr.Bottom - dr.Top, h - y0), scale);
        }
        finally
        {
            if (bmp != 0) Native.DeleteObject(bmp); // The sharp capture goes at once.
            if (dc != 0) Native.DeleteDC(dc);
        }
        if (small is null) return;
        _lastCapture[ruleId] = Environment.TickCount64;
        int pw = (dr.Right - dr.Left + 1) / 2, ph = (dr.Bottom - dr.Top + 1) / 2;
        var picture = Tint(small, dark(), pw, ph);
        lock (_gate)
        {
            _small[ruleId] = small;
            if (_pictures.Remove(ruleId, out var p)) Native.DeleteObject(p.Bitmap);
            if (picture is not null) _pictures[ruleId] = picture;
        }
        Save(ruleId, small, pw, ph);
        if (Trace) Log.Info($"Backdrop captured ({w}x{h}) in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1} ms (PrintWindow {printed:F1} ms)");
    }

    // ---- Saved pictures ---------------------------------------------------------------------------------------------

    private const int SaveEveryMs = 60_000;
    private static ReadOnlySpan<byte> Magic => "LBD1"u8;
    private readonly Dictionary<string, long> _lastSave = [];

    private string? FileOf(string ruleId) =>
        ruleId.Length is > 0 and <= 64 && ruleId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? Path.Combine(dir, ruleId + ".blur") : null;

    /// <summary>At most once a minute per app: the picture barely changes, and Latch may be ended at any time.</summary>
    private void Save(string ruleId, Small small, int width, int height)
    {
        if (_lastSave.TryGetValue(ruleId, out var last) && Environment.TickCount64 - last < SaveEveryMs) return;
        if (FileOf(ruleId) is not { } file) return;
        try
        {
            Directory.CreateDirectory(dir);
            var temp = file + ".tmp";
            using (var w = new BinaryWriter(File.Create(temp)))
            {
                w.Write(Magic);
                w.Write(small.Width);
                w.Write(small.Height);
                w.Write(width);
                w.Write(height);
                w.Write(small.Pixels);
            }
            File.Move(temp, file, overwrite: true);
            _lastSave[ruleId] = Environment.TickCount64;
        }
        catch (Exception e)
        {
            Log.Info("Couldn't save a cover picture: " + e.Message);
        }
    }

    /// <summary>Loads the pictures of the apps still locked, and deletes those of apps that no longer are.</summary>
    private void Load()
    {
        if (!Directory.Exists(dir)) return;
        var rules = service.Settings.Rules.Select(r => r.Id).ToHashSet();
        var isDark = dark();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            try
            {
                var ruleId = Path.GetFileNameWithoutExtension(file);
                if (!file.EndsWith(".blur", StringComparison.Ordinal) || !rules.Contains(ruleId))
                {
                    File.Delete(file);
                    continue;
                }
                using var r = new BinaryReader(File.OpenRead(file));
                if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) continue;
                int sw = r.ReadInt32(), sh = r.ReadInt32(), width = r.ReadInt32(), height = r.ReadInt32();
                if (sw is <= 0 or > 4096 || sh is <= 0 or > 4096 || width is <= 0 or > 16384 || height is <= 0 or > 16384) continue;
                var pixels = r.ReadBytes(sw * sh * 3);
                if (pixels.Length != sw * sh * 3) continue;
                var small = new Small(pixels, sw, sh);
                var picture = Tint(small, isDark, width, height);
                lock (_gate)
                {
                    if (_small.ContainsKey(ruleId)) continue; // A fresh capture got there first.
                    _small[ruleId] = small;
                    if (picture is not null) _pictures[ruleId] = picture;
                }
            }
            catch (Exception e)
            {
                Log.Info("Couldn't load a cover picture: " + e.Message);
            }
        }
    }

    private void Retint(string ruleId)
    {
        Small? small;
        Picture? old;
        lock (_gate)
        {
            if (!_small.TryGetValue(ruleId, out small) || !_pictures.TryGetValue(ruleId, out old)) return;
        }
        var isDark = dark();
        if (old.Dark == isDark) return;
        var picture = Tint(small, isDark, old.Width, old.Height);
        lock (_gate)
        {
            if (_pictures.Remove(ruleId, out var p)) Native.DeleteObject(p.Bitmap);
            if (picture is not null) _pictures[ruleId] = picture;
        }
    }

    // ---- Image work ------------------------------------------------------------------------------------------------

    /// <summary>Block size of the reduction, in DIPs; with the blur below it gives a blur of about 40 DIPs.</summary>
    private const double Block = 12;

    private const int Radius = 3, Passes = 3;

    /// <summary>
    /// Averages the capture down to one pixel per 12 DIP block, then box-blurs that three times (close to a
    /// Gaussian). Returns null for a capture that came back black (some apps can't be captured).
    /// </summary>
    private static unsafe Small? Reduce(nint bits, int stride, int x0, int y0, int w, int h, double scale)
    {
        if (x0 < 0 || y0 < 0 || w <= 0 || h <= 0) return null;
        var k = Math.Max(2, (int)Math.Round(Block * scale));
        int sw = (w + k - 1) / k, sh = (h + k - 1) / k;
        var sums = new int[sw * sh * 3];
        var counts = new int[sw * sh];
        var src = (byte*)bits;
        long bright = 0;
        // Every other pixel of every other row is plenty for a picture that ends up this blurred, at a quarter of
        // the work. Block columns advance by counting, not dividing.
        for (var y = 0; y < h; y += 2)
        {
            var row = src + ((long)(y0 + y) * stride + x0) * 4;
            var line = y / k * sw;
            int block = line, left = k;
            for (var x = 0; x < w; x += 2)
            {
                var px = row + x * 4;
                var i = block * 3;
                sums[i] += px[0];
                sums[i + 1] += px[1];
                sums[i + 2] += px[2];
                counts[block]++;
                if ((left -= 2) <= 0)
                {
                    left += k;
                    block++;
                }
            }
        }
        var img = new float[sw * sh * 3];
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) continue;
            for (var c = 0; c < 3; c++)
            {
                img[i * 3 + c] = (float)sums[i * 3 + c] / counts[i];
                bright += sums[i * 3 + c];
            }
        }
        if (bright / Math.Max(1L, 3L * counts.Sum()) < 2) return null; // All black: the capture failed.

        var tmp = new float[img.Length];
        for (var p = 0; p < Passes; p++)
        {
            BoxBlur(img, tmp, sw, sh, horizontal: true);
            BoxBlur(tmp, img, sw, sh, horizontal: false);
        }
        var bytes = new byte[img.Length];
        for (var i = 0; i < img.Length; i++) bytes[i] = (byte)Math.Clamp(img[i] + 0.5f, 0, 255);
        return new Small(bytes, sw, sh);
    }

    /// <summary>A running-sum box blur along one axis; edges repeat the border pixel.</summary>
    private static void BoxBlur(float[] src, float[] dst, int w, int h, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        var step = horizontal ? 3 : w * 3;
        var norm = 1f / (2 * Radius + 1);
        for (var line = 0; line < lines; line++)
        {
            var start = horizontal ? line * w * 3 : line * 3;
            for (var c = 0; c < 3; c++)
            {
                var b = start + c;
                float At(int i) => src[b + Math.Clamp(i, 0, len - 1) * step];
                var sum = 0f;
                for (var i = -Radius; i <= Radius; i++) sum += At(i);
                for (var i = 0; i < len; i++)
                {
                    dst[b + i * step] = sum * norm;
                    sum += At(i + Radius + 1) - At(i - Radius);
                }
            }
        }
    }

    /// <summary>
    /// Scales the small picture up smoothly (bilinear) and mixes in the window colour, so the prompt and title bar
    /// stand out against it and it reads as Latch's surface rather than the app. A little noise keeps the smooth
    /// gradients from banding.
    /// </summary>
    private static unsafe Picture? Tint(Small s, bool isDark, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        var info = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bmp = Native.CreateDIBSection(0, ref info, 0, out var bits, 0, 0);
        if (bmp == 0) return null;
        var surface = isDark ? 0x20 : 0xF3;
        var mix = isDark ? 0.62f : 0.72f; // Share of the window colour.
        // Fixed point (8 fraction bits for the weights, 8 for the tint): no floating point per pixel.
        var keep = (int)Math.Round((1 - mix) * 256);
        var add = (int)Math.Round(surface * mix * 256 * 256);
        var dst = (uint*)bits;
        var px = s.Pixels;
        int sw = s.Width, sh = s.Height;
        var xa = new int[w];
        var xb = new int[w];
        var xt = new int[w];
        for (var x = 0; x < w; x++)
        {
            var sx = Math.Clamp((x + 0.5f) * sw / w - 0.5f, 0, sw - 1);
            xa[x] = (int)sx * 3;
            xb[x] = Math.Min((int)sx + 1, sw - 1) * 3;
            xt[x] = (int)((sx - (int)sx) * 256);
        }
        var noise = (uint)Environment.TickCount | 1;
        for (var y = 0; y < h; y++)
        {
            var sy = Math.Clamp((y + 0.5f) * sh / h - 0.5f, 0, sh - 1);
            var top = (int)sy * sw * 3;
            var bottom = Math.Min((int)sy + 1, sh - 1) * sw * 3;
            var ty = (int)((sy - (int)sy) * 256);
            var row = dst + (long)y * w;
            for (var x = 0; x < w; x++)
            {
                int a = xa[x], b = xb[x], tx = xt[x];
                noise ^= noise << 13;
                noise ^= noise >> 17;
                noise ^= noise << 5;
                var n = (int)(noise & 0xFFFF) - 0x8000; // ±half a level, in 1/65536ths
                uint v = 0xFF000000;
                for (var ch = 0; ch < 3; ch++)
                {
                    var t = (px[top + a + ch] << 8) + (px[top + b + ch] - px[top + a + ch]) * tx;
                    var u = (px[bottom + a + ch] << 8) + (px[bottom + b + ch] - px[bottom + a + ch]) * tx;
                    var value = ((t << 8) + (u - t) * ty) >> 8; // 8.8 fixed point
                    var c = (value * keep + add + n + 0x8000) >> 16;
                    v |= (uint)Math.Clamp(c, 0, 255) << (ch * 8);
                }
                row[x] = v;
            }
        }
        return new Picture(bmp, w, h, isDark);
    }
}
