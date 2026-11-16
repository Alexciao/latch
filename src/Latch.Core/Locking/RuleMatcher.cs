using Latch.Core.Contract;
using Latch.Core.Model;

namespace Latch.Core.Locking;

/// <summary>What the host can tell about a top-level window. Gathered cheaply, once per event.</summary>
public sealed record WindowFacts(
    nint Hwnd,
    int ProcessId,
    string? ImagePath,
    string? ProcessPackageFamily,
    string? Aumid,
    string ClassName,
    DateTimeOffset? ProcessStarted,
    int? HostedProcessId,
    string? HostedPackageFamily)
{
    public bool IsFrameHost => ImageName.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
    public string ImageName => ImagePath is null ? "" : System.IO.Path.GetFileName(ImagePath);
}

/// <summary>Implemented by the host over Win32; faked in tests.</summary>
public interface IWindowInspector
{
    WindowFacts? Inspect(nint hwnd);

    /// <summary>The file's product name (version resource). Cheap; cached by the host.</summary>
    string? ProductName(string path);

    /// <summary>The embedded Authenticode signer subject, or null. Costlier; cached by the host.</summary>
    string? Publisher(string path);
}

public enum MatchKind { None, Pending, Match }

/// <param name="AppProcessId">The process that belongs to the app (for a UWP frame, the hosted app, never the frame host).</param>
/// <param name="Hide">False for windows that must be tracked but not hidden (a UWP CoreWindow before it is re-parented).</param>
public sealed record MatchResult(MatchKind Kind, LockRule? Rule, int? AppProcessId, bool Hide)
{
    public static readonly MatchResult None = new(MatchKind.None, null, null, false);
    public static readonly MatchResult Pending = new(MatchKind.Pending, null, null, false);
}

/// <summary>Decides which locked app, if any, a window belongs to.</summary>
public sealed class RuleMatcher(IWindowInspector inspector)
{
    /// <summary>
    /// Shell processes Latch never locks, even if a rule names them: hiding these would hide the taskbar or the
    /// frames of every Store app. This is the one place such quirks live.
    /// </summary>
    public static readonly HashSet<string> NeverLock = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "ApplicationFrameHost.exe", "dwm.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "SearchHost.exe", "SearchApp.exe", "LockApp.exe", "CredentialUIBroker.exe", "ShellHost.exe", "Taskmgr.exe",
        "msedgewebview2.exe", "TextInputHost.exe", "SystemSettings.exe", "consent.exe", "Latch.exe",
    };

    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    public MatchResult Match(WindowFacts w, IReadOnlyList<LockRule> rules)
    {
        if (rules.Count == 0 || w.ImagePath is null) return MatchResult.None;

        if (w.IsFrameHost)
        {
            // A Store app's visible frame belongs to ApplicationFrameHost; the real app is named by the frame's
            // AppUserModelID or by the process of the hosted child window. Early on, neither may be known yet.
            var family = FamilyFromAumid(w.Aumid) ?? w.HostedPackageFamily;
            if (family is null) return MatchResult.Pending;
            var rule = rules.FirstOrDefault(r => r.Kind == RuleKind.Package && Same(r.PackageFamilyName, family));
            return rule is null ? MatchResult.None : new MatchResult(MatchKind.Match, rule, w.HostedProcessId, true);
        }

        if (NeverLock.Contains(w.ImageName)) return MatchResult.None;

        if (w.ProcessPackageFamily is { } pfn)
        {
            var rule = rules.FirstOrDefault(r => r.Kind == RuleKind.Package && Same(r.PackageFamilyName, pfn));
            if (rule is not null) return new MatchResult(MatchKind.Match, rule, w.ProcessId, w.ClassName != CoreWindowClass);
        }

        var path = NormalizePath(w.ImagePath);
        var byPath = rules.FirstOrDefault(r => r.Kind == RuleKind.Exe && r.Path is not null && NormalizePath(r.Path) == path);
        if (byPath is not null) return new MatchResult(MatchKind.Match, byPath, w.ProcessId, true);

        // The same program copied or renamed: same product name and same signer. The signer is read only when the
        // product name already matches, because it costs a few milliseconds.
        var candidates = rules.Where(r => r.Kind == RuleKind.Exe && r.Publisher is not null && r.ProductName is not null).ToList();
        if (candidates.Count == 0) return MatchResult.None;
        var product = inspector.ProductName(w.ImagePath);
        if (string.IsNullOrWhiteSpace(product)) return MatchResult.None;
        var sameProduct = candidates.Where(r => Same(r.ProductName, product)).ToList();
        if (sameProduct.Count == 0) return MatchResult.None;
        var publisher = inspector.Publisher(w.ImagePath);
        if (publisher is null) return MatchResult.None;
        var bySigner = sameProduct.FirstOrDefault(r => Same(r.Publisher, publisher));
        return bySigner is null ? MatchResult.None : new MatchResult(MatchKind.Match, bySigner, w.ProcessId, true);
    }

    public static string? FamilyFromAumid(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        var bang = aumid.IndexOf('!', StringComparison.Ordinal);
        return bang > 0 ? aumid[..bang] : null;
    }

    /// <summary>Full path, backslashes, no `\\?\` prefix, no quotes, upper-case: the form exe rules compare in.</summary>
    public static string NormalizePath(string path)
    {
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        while (p.Contains(@"\\", StringComparison.Ordinal) && !p.StartsWith(@"\\", StringComparison.Ordinal)) p = p.Replace(@"\\", @"\", StringComparison.Ordinal);
        var parts = new List<string>();
        foreach (var part in p.Split('\\'))
        {
            if (part == ".") continue;
            if (part == ".." && parts.Count > 1) { parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return string.Join('\\', parts).TrimEnd('\\').ToUpperInvariant();
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
