using Lounge.Core.Input;
using Lounge.Core.Media;
using Lounge.Core.Settings;
using Xunit;

namespace Lounge.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lounge-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class NaturalSortTests
{
    [Fact]
    public void Episodes_sort_numerically_and_case_insensitively()
    {
        var names = new[] { "Show E10.mkv", "show e2.mkv", "Show E1.mkv", "Show E02b.mkv", "Show E002.mkv" };
        var sorted = names.OrderBy(n => n, NaturalComparer.Instance).ToArray();
        Assert.Equal("Show E1.mkv", sorted[0]);
        Assert.Equal("Show E10.mkv", sorted[^1]);
        Assert.True(NaturalComparer.Instance.Compare("a2", "a10") < 0);
        Assert.True(NaturalComparer.Instance.Compare("a", "a1") < 0);
        Assert.Equal(0, NaturalComparer.Instance.Compare("ABC", "abc"));
    }
}

public class WatchHistoryTests
{
    [Fact]
    public void Round_trips_in_tivi_format_with_pipes_in_path()
    {
        using var dir = new TempDir();
        var path = dir.File("history.ini");
        var h = new WatchHistory(path, () => 1700000000);
        h.Put(@"D:\Movies\A|B\film.mkv", new WatchEntry(120.5, 5400, -250, 0));
        h.Save();
        var line = File.ReadAllLines(path)[1];
        Assert.Equal(@"120.500|5400.000|-250|1700000000|D:\Movies\A|B\film.mkv", line);

        var again = new WatchHistory(path);
        Assert.True(again.TryGet(@"d:/movies/a|b/FILM.mkv", out var e));
        Assert.Equal(120.5, e.Position);
        Assert.Equal(-250, e.SubDelayMs);
    }

    [Fact]
    public void Malformed_lines_are_skipped()
    {
        using var dir = new TempDir();
        var path = dir.File("history.ini");
        File.WriteAllText(path, "# header\ngarbage\n1|2|x|4|C:\\a.mkv\n10.0|20.0|0|5|\n30.000|100.000|0|7|C:\\ok.mkv\n");
        var h = new WatchHistory(path);
        Assert.Equal(1, h.Count);
        Assert.True(h.TryGet(@"C:\ok.mkv", out _));
    }

    [Fact]
    public void Caps_at_400_newest_kept()
    {
        using var dir = new TempDir();
        var path = dir.File("history.ini");
        long t = 0;
        var h = new WatchHistory(path, () => ++t);
        for (int i = 0; i < 450; i++) h.Put($@"C:\v{i}.mkv", new WatchEntry(100, 1000, 0, 0));
        h.Save();
        var again = new WatchHistory(path);
        Assert.Equal(400, again.Count);
        Assert.True(again.TryGet(@"C:\v449.mkv", out _));
        Assert.False(again.TryGet(@"C:\v0.mkv", out _));
    }

    [Fact]
    public void Resume_rules_match_tivi()
    {
        using var dir = new TempDir();
        var h = new WatchHistory(dir.File("history.ini"));
        h.Put("a", new WatchEntry(20, 1000, 0, 0));
        h.Put("b", new WatchEntry(500, 1000, 0, 0));
        h.Put("c", new WatchEntry(960, 1000, 0, 0));
        Assert.Null(h.ResumePoint("a"));
        Assert.Equal(500, h.ResumePoint("b"));
        Assert.Null(h.ResumePoint("c"));
        Assert.Null(h.ResumePoint("missing"));
    }

    [Fact]
    public void Remember_forgets_finished_and_barely_started_files()
    {
        using var dir = new TempDir();
        var h = new WatchHistory(dir.File("history.ini"));
        h.Remember("x", 995, 1000, 0, reachedEnd: false);
        Assert.False(h.TryGet("x", out _));
        h.Remember("y", 3, 1000, 0, reachedEnd: false);
        Assert.False(h.TryGet("y", out _));
        h.Remember("z", 3, 1000, 400, reachedEnd: false);
        Assert.True(h.TryGet("z", out _));
    }
}

public class IniFileTests
{
    [Fact]
    public void Parses_tivi_config_and_keeps_unknown_keys()
    {
        var ini = IniFile.Parse("# tivi settings\nvolume=0.850\nsub_color=ffe066\nwin_x=10\nsub_font=Segoe UI\nnonsense\n");
        Assert.Equal(0.85, ini.GetDouble("volume", 0));
        Assert.Equal(0xffe066u, ini.GetHex("sub_color", 0));
        Assert.Equal("Segoe UI", ini.Get("sub_font"));
        Assert.Contains("win_x=10", ini.Serialize());
    }
}

public class SettingsTests
{
    [Fact]
    public void Every_descriptor_is_complete()
    {
        foreach (var d in SettingsSchema.All)
        {
            Assert.Contains(d.Section, SettingsSchema.Sections);
            Assert.False(string.IsNullOrWhiteSpace(d.Label), d.Key);
            if (d.Kind is SettingKind.Integer or SettingKind.Number)
            {
                Assert.True(d.Max > d.Min, d.Key);
                Assert.InRange(Convert.ToDouble(d.Default), d.Min, d.Max);
            }
            if (d.Kind == SettingKind.Choice) Assert.Contains(d.Choices, c => c.Value == (string)d.Default!);
        }
        foreach (var theme in SettingsSchema.Themes)
            Assert.True(SettingsSchema.TryGet(K.WallpaperFor(theme.Value), out _), $"no wallpaper setting for {theme.Value}");
        Assert.Equal(SettingsSchema.All.Count, SettingsSchema.All.Select(d => d.Key).Distinct().Count());
        foreach (var section in SettingsSchema.Sections)
            Assert.Contains(SettingsSchema.All, d => d.Section == section);
    }

    [Fact]
    public void Values_round_trip_and_are_clamped()
    {
        using var dir = new TempDir();
        var s = new SettingsStore(dir.File("settings.json"), dir.File("config.ini"));
        Assert.True(s.Set(K.Deadzone, 500));
        Assert.Equal(50, s.Int(K.Deadzone));
        Assert.False(s.Set(K.Theme, "nope"));
        Assert.True(s.Set(K.Theme, "kodi"));
        Assert.True(s.Set(K.PitchCorrect, false));
        Assert.True(s.Set(K.SubColor, "ffe066"));
        s.AddFolder(K.VideoRoots, @"D:\Movies");
        s.AddFolder(K.VideoRoots, @"d:\movies");

        var again = new SettingsStore(dir.File("settings.json"), dir.File("config.ini"));
        Assert.Equal("kodi", again.String(K.Theme));
        Assert.False(again.Bool(K.PitchCorrect));
        Assert.Single(again.List(K.VideoRoots));
        var ini = File.ReadAllText(dir.File("config.ini"));
        Assert.Contains("pitch_correct=0", ini);
        Assert.Contains("sub_color=ffe066", ini);
    }

    [Fact]
    public void Corrupt_file_is_set_aside_and_defaults_load()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{ not json");
        var s = new SettingsStore(dir.File("settings.json"), dir.File("config.ini"));
        Assert.NotNull(s.LoadNotice);
        Assert.Equal("aurora", s.String(K.Theme));
        Assert.Contains(Directory.GetFiles(dir.Path), f => f.Contains(".corrupt-"));
    }

    [Fact]
    public void Future_file_is_backed_up_and_known_values_kept()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"),
            "{\"schemaVersion\":99,\"values\":{\"appearance.theme\":\"series\",\"future.thing\":42}}");
        var s = new SettingsStore(dir.File("settings.json"), dir.File("config.ini"));
        Assert.NotNull(s.LoadNotice);
        Assert.Equal("series", s.String(K.Theme));
        s.Set(K.Sounds, false);
        Assert.Contains("future.thing", File.ReadAllText(dir.File("settings.json")));
    }

    [Fact]
    public void Step_cycles_choices_and_moves_numbers()
    {
        using var dir = new TempDir();
        var s = new SettingsStore(dir.File("settings.json"), dir.File("config.ini"));
        s.Step(K.Theme, -1);
        Assert.Equal("carplay", s.String(K.Theme));   // wraps to the last theme
        s.Step(K.UiScale, 1);
        Assert.Equal(1.05, s.Double(K.UiScale), 6);
        Assert.Equal("105%", SettingsSchema.Get(K.UiScale).ShowValue(s.Get(K.UiScale)));
    }

    [Fact]
    public void Reset_all_keeps_library_folders()
    {
        using var dir = new TempDir();
        var s = new SettingsStore(dir.File("settings.json"), dir.File("config.ini"));
        s.Set(K.Theme, "console");
        s.AddFolder(K.MusicRoots, @"E:\Music");
        s.ResetAll();
        Assert.Equal("aurora", s.String(K.Theme));
        Assert.Single(s.List(K.MusicRoots));
    }

    [Fact]
    public void Changed_fires_once_per_real_change()
    {
        using var dir = new TempDir();
        var s = new SettingsStore(dir.File("settings.json"), dir.File("config.ini"));
        var keys = new List<string>();
        s.Changed += keys.Add;
        s.Set(K.Blur, false);
        s.Set(K.Blur, false);
        Assert.Equal([K.Blur], keys);
    }
}

public class KeymapTests
{
    [Fact]
    public void Json_round_trip_preserves_bindings()
    {
        var k = Keymap.Defaults();
        k.Rebind("Player", Act.Tracks, new InputBinding(PadButton.Y, Gesture.Press), new InputBinding(PadButton.LB | PadButton.Y, Gesture.Hold, 800), ConflictMode.Replace);
        var again = Keymap.FromJson(k.ToJson());
        Assert.Equal(Act.Tracks, again.ActionFor("Player", new InputBinding(PadButton.LB | PadButton.Y, Gesture.Hold)));
        Assert.Equal(800, again.BindingsFor("Player", Act.Tracks).Single().HoldMs);
    }

    [Fact]
    public void Swap_gives_the_displaced_action_the_old_input()
    {
        var k = Keymap.Defaults();
        var displaced = k.Rebind("Player", Act.Tracks, new InputBinding(PadButton.Y, Gesture.Press),
            new InputBinding(PadButton.X, Gesture.Press), ConflictMode.Swap);
        Assert.Equal(Act.Info, displaced);
        Assert.Equal(Act.Info, k.ActionFor("Player", new InputBinding(PadButton.Y, Gesture.Press)));
        Assert.Equal(Act.Tracks, k.ActionFor("Player", new InputBinding(PadButton.X, Gesture.Press)));
    }

    [Fact]
    public void Replace_drops_the_displaced_binding()
    {
        var k = Keymap.Defaults();
        k.Rebind("Player", Act.Tracks, new InputBinding(PadButton.Y, Gesture.Press),
            new InputBinding(PadButton.X, Gesture.Press), ConflictMode.Replace);
        Assert.Empty(k.BindingsFor("Player", Act.Info));
        Assert.Null(k.ActionFor("Player", new InputBinding(PadButton.Y, Gesture.Press)));
    }

    [Fact]
    public void Corrupt_keymap_falls_back_to_defaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("keymap.json"), "[[[");
        var k = Keymap.Load(dir.File("keymap.json"), out var notice);
        Assert.NotNull(notice);
        Assert.Equal(Act.Accept, k.ActionFor("Global", new InputBinding(PadButton.A, Gesture.Press)));
    }

    [Fact]
    public void Every_default_action_is_in_the_catalog()
    {
        var k = Keymap.Defaults();
        foreach (var c in k.Contexts)
            foreach (var b in k.In(c))
                Assert.Contains(Act.Catalog, a => a.Id == b.Action);
    }
}

