using Latch.Core.Contract;
using Latch.Core.Locking;
using Latch.Core.Model;

namespace Latch.Core.Tests;

public class RuleMatcherTests
{
    private const string Vlc = @"C:\Program Files\VideoLAN\VLC\vlc.exe";
    private const string VideoLan = "CN=VideoLAN, O=VideoLAN, L=Paris, C=FR";
    private const string CalcFamily = "Microsoft.WindowsCalculator_8wekyb3d8bbwe";

    private static readonly LockRule VlcRule = new("vlc", RuleKind.Exe, "VLC media player", Vlc, null, null, null, VideoLan, "VLC media player");
    private static readonly LockRule CalcRule = new("calc", RuleKind.Package, "Calculator", null, CalcFamily, CalcFamily + "!App", null, null, null);

    private static WindowFacts Win(string path, string? pfn = null, string? aumid = null, string cls = "Qt5QWindowIcon", int pid = 100, int? hostedPid = null, string? hostedPfn = null) =>
        new(1, pid, path, pfn, aumid, cls, null, hostedPid, hostedPfn);

    [Theory]
    [InlineData(@"c:/program files/videolan/vlc/VLC.EXE")]
    [InlineData(@"\\?\C:\Program Files\VideoLAN\VLC\vlc.exe")]
    [InlineData(@"""C:\Program Files\VideoLAN\VLC\..\VLC\vlc.exe""")]
    public void Exe_paths_normalize_to_the_same_form(string variant) =>
        Assert.Equal(RuleMatcher.NormalizePath(Vlc), RuleMatcher.NormalizePath(variant));

    [Fact]
    public void Exe_matches_by_full_path_without_reading_the_signer()
    {
        var inspector = new FakeInspector();
        var result = new RuleMatcher(inspector).Match(Win(@"C:\PROGRAM FILES\VideoLAN\VLC\vlc.exe"), [VlcRule]);
        Assert.Equal(MatchKind.Match, result.Kind);
        Assert.Same(VlcRule, result.Rule);
        Assert.Equal(0, inspector.PublisherReads);
    }

    [Fact]
    public void A_renamed_copy_with_the_same_signer_and_product_matches()
    {
        var inspector = new FakeInspector();
        inspector.Files[@"D:\stuff\player.exe"] = ("VLC media player", VideoLan);
        var result = new RuleMatcher(inspector).Match(Win(@"D:\stuff\player.exe"), [VlcRule]);
        Assert.Equal(VlcRule, result.Rule);
    }

    [Fact]
    public void Same_product_from_another_signer_or_an_unrelated_exe_does_not_match()
    {
        var inspector = new FakeInspector();
        inspector.Files[@"D:\fake\vlc.exe"] = ("VLC media player", "CN=Someone Else");
        inspector.Files[@"D:\notes.exe"] = ("Notes", VideoLan);
        var matcher = new RuleMatcher(inspector);
        Assert.Equal(MatchKind.None, matcher.Match(Win(@"D:\fake\vlc.exe"), [VlcRule]).Kind);
        Assert.Equal(MatchKind.None, matcher.Match(Win(@"D:\notes.exe"), [VlcRule]).Kind);
        Assert.Equal(1, inspector.PublisherReads); // Only the same-product file paid for a signer read.
    }

    [Fact]
    public void Store_app_frame_is_matched_through_its_aumid_and_tracks_the_hosted_process()
    {
        var frame = Win(@"C:\Windows\System32\ApplicationFrameHost.exe", aumid: CalcFamily + "!App", cls: "ApplicationFrameWindow", pid: 10892, hostedPid: 27816);
        var result = new RuleMatcher(new FakeInspector()).Match(frame, [VlcRule, CalcRule]);
        Assert.Equal(CalcRule, result.Rule);
        Assert.Equal(27816, result.AppProcessId);
        Assert.True(result.Hide);
    }

    [Fact]
    public void Store_app_frame_falls_back_to_the_hosted_child_and_waits_when_neither_is_known()
    {
        var matcher = new RuleMatcher(new FakeInspector());
        var hosted = Win(@"C:\Windows\System32\ApplicationFrameHost.exe", cls: "ApplicationFrameWindow", hostedPid: 5, hostedPfn: CalcFamily);
        Assert.Equal(CalcRule, matcher.Match(hosted, [CalcRule]).Rule);
        var early = Win(@"C:\Windows\System32\ApplicationFrameHost.exe", cls: "ApplicationFrameWindow");
        Assert.Equal(MatchKind.Pending, matcher.Match(early, [CalcRule]).Kind);
    }

    [Fact]
    public void Packaged_process_matches_by_family_but_its_core_window_is_never_hidden()
    {
        var matcher = new RuleMatcher(new FakeInspector());
        var core = Win(@"C:\Program Files\WindowsApps\Calc\CalculatorApp.exe", pfn: CalcFamily, cls: "Windows.UI.Core.CoreWindow");
        var result = matcher.Match(core, [CalcRule]);
        Assert.Equal(CalcRule, result.Rule);
        Assert.False(result.Hide);
    }

    [Fact]
    public void Shell_processes_are_never_locked()
    {
        var explorerRule = new LockRule("x", RuleKind.Exe, "Explorer", @"C:\Windows\explorer.exe", null, null, null, null, null);
        Assert.Equal(MatchKind.None, new RuleMatcher(new FakeInspector()).Match(Win(@"C:\Windows\explorer.exe"), [explorerRule]).Kind);
    }

    [Fact]
    public void Rule_keys_are_stable_across_path_spellings()
    {
        Assert.Equal(RuleKeys.For(RuleKind.Exe, Vlc), RuleKeys.For(RuleKind.Exe, "c:/program files/videolan/vlc/vlc.exe"));
        Assert.NotEqual(RuleKeys.For(RuleKind.Exe, Vlc), RuleKeys.For(RuleKind.Package, Vlc));
    }
}
