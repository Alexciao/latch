using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Latch.Core.Locking;

namespace Latch.Host;

/// <summary>Reads facts about windows and files from Win32. Process facts are cached per (pid, start time).</summary>
internal sealed class WindowInspector : IWindowInspector
{
    private sealed record ProcFacts(string? Path, string? Family, DateTimeOffset? Started);

    private readonly ConcurrentDictionary<(uint Pid, long Created), ProcFacts> _procs = new();
    private readonly ConcurrentDictionary<string, string?> _products = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _publishers = new(StringComparer.OrdinalIgnoreCase);

    public WindowFacts? Inspect(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        var proc = Process(pid);
        if (proc?.Path is null) return null;
        var cls = ClassName(hwnd);
        string? aumid = null;
        int? hostedPid = null;
        string? hostedFamily = null;
        var started = proc.Started;
        if (System.IO.Path.GetFileName(proc.Path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            aumid = WindowAumid(hwnd);
            started = null; // The frame host is long-lived; only the hosted app's start time says "just launched".
            var child = HostedProcess(hwnd, pid);
            if (child is { } c && Process(c) is { } hosted)
            {
                hostedPid = (int)c;
                hostedFamily = hosted.Family;
                started = hosted.Started;
            }
        }
        return new WindowFacts(hwnd, (int)pid, proc.Path, proc.Family, aumid, cls, started, hostedPid, hostedFamily);
    }

    public string? ProductName(string path) => _products.GetOrAdd(path, p =>
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(p);
            return string.IsNullOrWhiteSpace(info.ProductName) ? null : info.ProductName.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    });

    public string? Publisher(string path) => _publishers.GetOrAdd(path, p =>
    {
        try
        {
#pragma warning disable SYSLIB0057 // Reading the embedded signer subject only; no trust decision rests on it.
            return X509Certificate.CreateFromSignedFile(p).Subject;
#pragma warning restore SYSLIB0057
        }
        catch (Exception)
        {
            return null; // Unsigned or catalog-signed: matched by path only.
        }
    });

    public static string ClassName(nint hwnd)
    {
        var sb = new StringBuilder(256);
        Native.GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string? ImagePath(uint pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return Native.QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    private ProcFacts? Process(uint pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
            Native.GetProcessTimes(h, out var created, out _, out _, out _);
            return _procs.GetOrAdd((pid, created), _ =>
            {
                var sb = new StringBuilder(1024);
                var size = sb.Capacity;
                var path = Native.QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
                var len = 256;
                var fam = new StringBuilder(len);
                var family = Native.GetPackageFamilyName(h, ref len, fam) == 0 ? fam.ToString() : null;
                return new ProcFacts(path, family, created > 0 ? DateTimeOffset.FromFileTime(created) : null);
            });
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    private static uint? HostedProcess(nint frame, uint framePid)
    {
        uint? found = null;
        Native.EnumChildWindows(frame, (child, _) =>
        {
            Native.GetWindowThreadProcessId(child, out var p);
            if (p != 0 && p != framePid)
            {
                found = p;
                return false;
            }
            return true;
        }, 0);
        return found;
    }

    public static string? WindowAumid(nint hwnd)
    {
        var iid = typeof(Native.IPropertyStore).GUID;
        if (Native.SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return null;
        try
        {
            var key = Native.PKEY_AppUserModel_ID;
            if (store.GetValue(ref key, out var pv) != 0) return null;
            var value = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null;
            Native.PropVariantClear(ref pv);
            return value;
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }
}
