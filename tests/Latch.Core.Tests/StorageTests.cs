using System.Text;
using Latch.Core.Contract;
using Latch.Core.Model;
using Latch.Core.Storage;

namespace Latch.Core.Tests;

public class StorageTests
{
    private static readonly DateTimeOffset Boot = DateTimeOffset.Parse("2026-11-16T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Heartbeat_after_boot_without_a_clean_exit_is_a_gap()
    {
        var last = new HeartbeatFile(Boot.AddHours(2), CleanExit: false);
        var now = Boot.AddHours(4);
        Assert.Equal((last.Last, now), Heartbeat.Gap(last, Boot, now));
    }

    [Fact]
    public void Heartbeat_before_this_boot_or_with_a_clean_exit_is_not_reported()
    {
        Assert.Null(Heartbeat.Gap(new HeartbeatFile(Boot.AddMinutes(-5), false), Boot, Boot.AddHours(1)));
        Assert.Null(Heartbeat.Gap(new HeartbeatFile(Boot.AddHours(2), true), Boot, Boot.AddHours(3)));
        Assert.Null(Heartbeat.Gap(null, Boot, Boot.AddHours(3)));
    }

    [Fact]
    public void Heartbeat_file_round_trips()
    {
        using var dir = new TempDir();
        var hb = new Heartbeat(Path.Combine(dir.Path, "heartbeat"));
        hb.Beat(Boot, cleanExit: true);
        Assert.Equal(new HeartbeatFile(Boot, true), hb.Read());
    }

    [Fact]
    public void Settings_drop_only_the_bad_fields_and_rules()
    {
        var json = """
            {
              "schema": 1,
              "graceMinutes": 7,
              "relockOnClose": "yes",
              "idleMinutes": 30,
              "theme": "dark",
              "futureField": 42,
              "rules": [
                { "id": "a", "kind": "exe", "name": "VLC", "path": "C:\\vlc.exe" },
                { "id": "b", "kind": "teleport", "name": "Bad" },
                { "id": "c", "kind": "package", "name": "No family" }
              ]
            }
            """;
        var s = SettingsStore.Parse(Encoding.UTF8.GetBytes(json))!;
        Assert.Equal(5, s.GraceMinutes);     // 7 isn't a choice: default.
        Assert.True(s.RelockOnClose);        // Wrong type: default.
        Assert.Equal(30, s.IdleMinutes);     // Valid: kept.
        Assert.Equal(ThemeChoice.Dark, s.Theme);
        Assert.Equal("a", Assert.Single(s.Rules).Id);
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_the_backup()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var store = new SettingsStore(path);
        store.Save(new LatchSettings { IdleMinutes = 15 });
        store.Save(new LatchSettings { IdleMinutes = 30 }); // The first version is now settings.json.bak.
        File.WriteAllText(path, "{ not json");
        Assert.Equal(15, store.Load().IdleMinutes);
    }

    [Fact]
    public void Settings_round_trip_with_rules()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        var rule = new LockRule("k", RuleKind.Package, "Calculator", null, "Microsoft.WindowsCalculator_8wekyb3d8bbwe", null, "k", null, null);
        store.Save(new LatchSettings { Rules = [rule], SetupDone = true, Theme = ThemeChoice.Light, Dismissed = ["x"] });
        var loaded = store.Load();
        Assert.Equal(rule, Assert.Single(loaded.Rules));
        Assert.True(loaded.SetupDone);
        Assert.Equal(ThemeChoice.Light, loaded.Theme);
        Assert.Equal(["x"], loaded.Dismissed);
    }

    [Fact]
    public void History_rotates_and_keeps_five_files()
    {
        using var dir = new TempDir();
        var log = new HistoryLog(dir.Path);
        var padding = new string('x', 300_000);
        for (var i = 0; i < 30; i++) log.Append(new HistoryEntry(Boot.AddMinutes(i), HistoryKind.Failed, padding, UnlockMethod.Pin, null));
        Assert.Equal(5, Directory.GetFiles(dir.Path, "history*.jsonl").Length);
        var fresh = new HistoryLog(dir.Path).ReadAll();
        Assert.Equal(Boot.AddMinutes(29), fresh[^1].At);
        Assert.True(fresh.Count < 30);
    }
}
