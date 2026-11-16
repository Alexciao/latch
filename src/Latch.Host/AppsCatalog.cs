using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Latch.Core;
using Latch.Core.Contract;
using Latch.Core.Locking;
using Latch.Core.Model;
using Latch.Core.Storage;

namespace Latch.Host;

/// <summary>
/// Installed apps from the shell's Apps folder (shell:AppsFolder), which lists desktop and Store apps with names,
/// icons and AppUserModelIDs. Icons are cached as PNGs in cache\icons (safe to delete).
/// </summary>
internal sealed class AppsCatalog(DataPaths paths)
{
    private const int IconPx = 96;
    private readonly ConcurrentDictionary<string, string?> _dataUrls = new();

    public string IconUrl(string key) => $"https://{WebUi.IconHost}/{key}.png";

    public Task<IReadOnlyList<InstalledApp>> ListAsync() => Sta.Run<IReadOnlyList<InstalledApp>>(() =>
    {
        var sw = Stopwatch.StartNew();
        var result = new Dictionary<string, InstalledApp>();
        var iid = typeof(Native.IShellItem).GUID;
        var folderId = Native.FOLDERID_AppsFolder;
        Marshal.ThrowExceptionForHR(Native.SHGetKnownFolderItem(ref folderId, 0, 0, ref iid, out var folderObj));
        var folder = (Native.IShellItem)folderObj;
        var bhid = Native.BHID_EnumItems;
        var enumIid = typeof(Native.IEnumShellItems).GUID;
        folder.BindToHandler(0, ref bhid, ref enumIid, out var enumObj);
        var items = (Native.IEnumShellItems)enumObj;
        while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1)
        {
            try
            {
                // Two shortcuts to one program ("VLC media player", "VLC media player skinned"): keep the shorter name.
                if (Describe(item) is { } app && (!result.TryGetValue(app.Key, out var seen) || app.Name.Length < seen.Name.Length)) result[app.Key] = app;
            }
            catch (Exception e)
            {
                Log.Error("apps folder item", e);
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        Marshal.ReleaseComObject(items);
        Marshal.ReleaseComObject(folder);
        Log.Info($"Listed {result.Count} apps in {sw.ElapsedMilliseconds} ms");
        return result.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    public Task<InstalledApp?> DescribeExeAsync(string path) => Sta.Run<InstalledApp?>(() =>
    {
        var iid = typeof(Native.IShellItem).GUID;
        if (Native.SHCreateItemFromParsingName(path, 0, ref iid, out var obj) != 0) return null;
        var item = (Native.IShellItem)obj;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var name = FirstNonEmpty(info.FileDescription, info.ProductName, Path.GetFileNameWithoutExtension(path))!;
            var key = RuleKeys.For(RuleKind.Exe, path);
            SaveIcon(item, key);
            return new InstalledApp(key, name, RuleKind.Exe, path, null, null, IconUrl(key), false);
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    });

    private InstalledApp? Describe(Native.IShellItem item)
    {
        item.GetDisplayName(Native.SIGDN_NORMALDISPLAY, out var name);
        item.GetDisplayName(Native.SIGDN_PARENTRELATIVEPARSING, out var parsing);
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) return null;

        var item2 = (Native.IShellItem2)item;
        var pfnKey = Native.PKEY_AppUserModel_PackageFamilyName;
        var family = item2.GetString(ref pfnKey, out var pfn) == 0 && !string.IsNullOrEmpty(pfn) ? pfn : null;
        if (family is null && parsing.Contains('!', StringComparison.Ordinal) && !parsing.Contains('\\', StringComparison.Ordinal))
            family = RuleMatcher.FamilyFromAumid(parsing);
        if (family is not null)
        {
            var key = RuleKeys.For(RuleKind.Package, family);
            SaveIcon(item, key);
            return new InstalledApp(key, name, RuleKind.Package, null, family, parsing, IconUrl(key), false);
        }

        var targetKey = Native.PKEY_Link_TargetParsingPath;
        var path = item2.GetString(ref targetKey, out var target) == 0 && !string.IsNullOrEmpty(target) ? target : ResolveKnownFolder(parsing);
        if (path is null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var file = Path.GetFileName(path);
        if (RuleMatcher.NeverLock.Contains(file) || file.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(path)) return null;
        var exeKey = RuleKeys.For(RuleKind.Exe, path);
        SaveIcon(item, exeKey);
        return new InstalledApp(exeKey, name, RuleKind.Exe, path, null, null, IconUrl(exeKey), false);
    }

    /// <summary>Desktop entries look like "{KNOWNFOLDERID}\VideoLAN\VLC\vlc.exe".</summary>
    private static string? ResolveKnownFolder(string parsing)
    {
        if (!parsing.StartsWith('{')) return File.Exists(parsing) ? parsing : null;
        var close = parsing.IndexOf('}', StringComparison.Ordinal);
        if (close < 0 || !Guid.TryParse(parsing[..(close + 1)], out var id)) return null;
        if (Native.SHGetKnownFolderPath(ref id, 0, 0, out var p) != 0) return null;
        var root = Marshal.PtrToStringUni(p);
        Native.CoTaskMemFree(p);
        return root is null ? null : Path.Combine(root, parsing[(close + 1)..].TrimStart('\\'));
    }

    private void SaveIcon(Native.IShellItem item, string key)
    {
        var file = Path.Combine(paths.Icons, key + ".png");
        if (File.Exists(file)) return;
        if (item is not Native.IShellItemImageFactory factory) return;
        if (factory.GetImage(new Native.SIZE { cx = IconPx, cy = IconPx }, 0x1, out var hbm) != 0 || hbm == 0) return; // SIIGBF_BIGGERSIZEOK
        try
        {
            var source = ToBitmapSource(hbm);
            if (source is null) return;
            Directory.CreateDirectory(paths.Icons);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            AtomicFile.Write(file, ms.ToArray());
        }
        finally
        {
            Native.DeleteObject(hbm);
        }
    }

    /// <summary>Reads a 32-bit HBITMAP with its alpha channel (CreateBitmapSourceFromHBitmap drops alpha).</summary>
    private static BitmapSource? ToBitmapSource(nint hbm)
    {
        if (Native.GetObjectW(hbm, Marshal.SizeOf<Native.BITMAP>(), out var bmp) == 0) return null;
        var w = bmp.bmWidth;
        var h = bmp.bmHeight;
        var header = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bits = new byte[w * h * 4];
        var dc = Native.CreateCompatibleDC(0);
        try
        {
            if (Native.GetDIBits(dc, hbm, 0, (uint)h, bits, ref header, 0) == 0) return null;
        }
        finally
        {
            Native.DeleteDC(dc);
        }
        var anyAlpha = false;
        for (var i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (var i = 3; i < bits.Length; i += 4) bits[i] = 255;
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bits, w * 4);
        src.Freeze();
        return src;
    }

    /// <summary>A data URL for the prompt, read once and kept in memory so the prompt never waits on disk.</summary>
    public string? IconDataUrl(string? key)
    {
        if (key is null) return null;
        return _dataUrls.GetOrAdd(key, k =>
        {
            var file = Path.Combine(paths.Icons, k + ".png");
            try
            {
                return File.Exists(file) ? "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(file)) : null;
            }
            catch (IOException)
            {
                return null;
            }
        });
    }

    /// <summary>Warms the in-memory icon cache for every rule, off the UI thread.</summary>
    public void Preload(IEnumerable<LockRule> rules)
    {
        foreach (var r in rules) IconDataUrl(r.IconKey);
    }

    public static bool TargetExists(LockRule rule)
    {
        if (rule.Kind == RuleKind.Exe) return rule.Path is not null && File.Exists(rule.Path);
        if (rule.PackageFamilyName is null) return false;
        var pm = new Windows.Management.Deployment.PackageManager();
        return pm.FindPackagesForUser(string.Empty, rule.PackageFamilyName).Any();
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}

/// <summary>Runs shell COM work on a short-lived STA thread.</summary>
internal static class Sta
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        })
        { IsBackground = true, Name = "Latch shell" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}
