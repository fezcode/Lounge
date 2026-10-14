using Lounge.Core.Audio;
using Lounge.Core.Files;
using Lounge.Core.Launch;
using Lounge.Core.Library;
using Lounge.Core.Media;
using Lounge.Core.Themes;
using Xunit;

namespace Lounge.Tests;

public class FileOpsTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("ok name.mkv", true)]
    [InlineData("bad:name", false)]
    [InlineData("trailing.", false)]
    [InlineData("CON", false)]
    [InlineData("con.txt", false)]
    [InlineData("console.txt", true)]
    public void Validates_windows_names(string name, bool ok) =>
        Assert.Equal(ok, FileOps.ValidateName(name) is null);

    [Fact]
    public void Locked_extension_is_restored()
    {
        Assert.Equal("New Title.mkv", FileOps.Compose("New Title ", ".mkv", lockExtension: true));
        Assert.Equal("New Title.mp4", FileOps.Compose("New Title.mp4", ".mkv", lockExtension: false));
    }

    [Fact]
    public void Rename_refuses_collision_but_allows_case_change()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a.txt"), "a");
        File.WriteAllText(dir.File("b.txt"), "b");
        var clash = FileOps.Rename(dir.File("a.txt"), "b.txt");
        Assert.False(clash.Ok);
        Assert.Contains("already exists", clash.Error);
        Assert.True(File.Exists(dir.File("a.txt")));

        var caseOnly = FileOps.Rename(dir.File("a.txt"), "A.txt");
        Assert.True(caseOnly.Ok);
        Assert.Contains("A.txt", Directory.GetFiles(dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Rename_folder_case_only_works()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("shows"));
        Assert.True(FileOps.Rename(dir.File("shows"), "Shows").Ok);
        Assert.Contains("Shows", Directory.GetDirectories(dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Unique_name_counts_up()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("New folder"));
        Directory.CreateDirectory(dir.File("New folder (2)"));
        Assert.Equal("New folder (3)", FileOps.UniqueName(dir.Path, "New folder"));
    }

    [Fact]
    public async Task Copy_with_keep_both_and_move()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("src/sub"));
        Directory.CreateDirectory(dir.File("dst"));
        File.WriteAllText(dir.File("src/sub/x.txt"), "x");
        File.WriteAllText(dir.File("src/y.txt"), "y");
        File.WriteAllText(dir.File("dst/y.txt"), "old");

        var r = await FileOps.TransferAsync([dir.File("src/y.txt"), dir.File("src/sub")], dir.File("dst"), move: false,
            _ => Task.FromResult(ConflictChoice.KeepBoth));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("old", File.ReadAllText(dir.File("dst/y.txt")));
        Assert.Equal("y", File.ReadAllText(dir.File("dst/y (2).txt")));
        Assert.True(File.Exists(dir.File("dst/sub/x.txt")));

        var m = await FileOps.TransferAsync([dir.File("src/sub")], dir.File("dst2"), move: true,
            _ => Task.FromResult(ConflictChoice.Replace));
        Assert.True(m.Ok, m.Error);
        Assert.False(Directory.Exists(dir.File("src/sub")));
        Assert.True(File.Exists(dir.File("dst2/sub/x.txt")));
    }

    [Fact]
    public async Task Folder_into_itself_is_refused()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("a/b"));
        var r = await FileOps.TransferAsync([dir.File("a")], dir.File("a/b"), move: true, _ => Task.FromResult(ConflictChoice.Skip));
        Assert.False(r.Ok);
    }

    [Fact]
    public void Cancel_on_conflict_stops()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("d"));
        File.WriteAllText(dir.File("f.txt"), "1");
        File.WriteAllText(dir.File("d/f.txt"), "2");
        var r = FileOps.TransferAsync([dir.File("f.txt")], dir.File("d"), false, _ => Task.FromResult(ConflictChoice.Cancel)).Result;
        Assert.False(r.Ok);
        Assert.Equal("2", File.ReadAllText(dir.File("d/f.txt")));
    }

    [Fact]
    public void Lists_folders_first_in_natural_order()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("ep10.mkv"), "");
        File.WriteAllText(dir.File("ep2.mkv"), "");
        Directory.CreateDirectory(dir.File("zeta"));
        var list = FileOps.List(dir.Path, showHidden: false);
        Assert.Equal(["zeta", "ep2.mkv", "ep10.mkv"], list.Select(e => e.Name));
        Assert.Equal(MediaKind.Video, list[1].Kind);
    }

    [Fact]
    public void Permanent_delete_removes()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("gone.txt"), "");
        Assert.True(FileOps.Delete([dir.File("gone.txt")], permanent: true).Ok);
        Assert.False(File.Exists(dir.File("gone.txt")));
    }
}

public class SidecarAndQueueTests
{
    [Fact]
    public void Picks_preferred_language_then_plain_then_others_forced_last()
    {
        var names = new[] { "Movie.forced.en.srt", "Movie.tr.srt", "Movie.English.srt", "Movie.srt", "Other.srt", "Movie.mkv" };
        Assert.EndsWith("Movie.English.srt", SidecarSubs.Pick(@"C:\v\Movie.mkv", names, "en"));
        Assert.EndsWith("Movie.srt", SidecarSubs.Pick(@"C:\v\Movie.mkv", names, "de"));
        Assert.EndsWith("Movie.tr.srt", SidecarSubs.Pick(@"C:\v\Movie.mkv", ["Movie.tr.srt", "Movie.sdh.srt"], ""));
        Assert.Null(SidecarSubs.Pick(@"C:\v\Movie.mkv", ["Movies.srt"], "en"));
    }

    [Fact]
    public void Opening_an_episode_queues_the_later_ones()
    {
        var files = new[] { @"C:\s\E10.mkv", @"C:\s\E2.mkv", @"C:\s\E1.mkv", @"C:\s\notes.txt", @"C:\s\E3.mp3" };
        var q = new PlayQueue();
        q.OpenWithSiblings(@"C:\s\E2.mkv", autoQueue: true, _ => files);
        Assert.Equal([@"C:\s\E2.mkv", @"C:\s\E10.mkv"], q.Items);
        Assert.Equal(@"C:\s\E10.mkv", q.PeekNext());
        Assert.Equal(@"C:\s\E10.mkv", q.Next());
        Assert.Null(q.PeekNext());
        q.Repeat = RepeatMode.All;
        Assert.Equal(@"C:\s\E2.mkv", q.PeekNext());
    }

    [Fact]
    public void Shuffle_visits_everything_once()
    {
        var q = new PlayQueue(seed: 7);
        q.Set(Enumerable.Range(0, 10).Select(i => $"t{i}"), 0);
        q.Shuffle = true;
        var seen = new List<string> { q.Current! };
        while (q.Next() is { } n) seen.Add(n);
        Assert.Equal(10, seen.Distinct().Count());
    }
}

public class LibraryTests
{
    [Fact]
    public void Scanner_adds_updates_and_removes()
    {
        using var dir = new TempDir();
        var root = dir.File("lib");
        Directory.CreateDirectory(Path.Combine(root, "Show"));
        File.WriteAllText(Path.Combine(root, "Show", "Show.S01E01.1080p.WEB.mkv"), "a");
        File.WriteAllText(Path.Combine(root, "Show", "Show.S01E02.1080p.WEB.mkv"), "b");
        File.WriteAllText(Path.Combine(root, "Show", "poster.jpg"), "img");
        File.WriteAllText(Path.Combine(root, "readme.txt"), "x");

        using var db = LibraryDb.InMemory();
        int probes = 0;
        var scanner = new LibraryScanner(db, _ => { probes++; return new ProbeResult(1200, Width: 1920, Height: 1080); }, dir.File("thumbs"));
        var s1 = scanner.Scan(LibraryKind.Video, [root]);
        Assert.Equal(2, s1.Added);
        Assert.Equal(2, probes);
        var items = db.All(LibraryKind.Video);
        Assert.All(items, i => Assert.EndsWith("poster.jpg", i.Art));
        Assert.Contains(items, i => i.Title == "Show S01E01");

        var s2 = scanner.Scan(LibraryKind.Video, [root]);
        Assert.Equal(0, s2.Added + s2.Updated);
        Assert.Equal(2, probes);

        File.Delete(Path.Combine(root, "Show", "Show.S01E02.1080p.WEB.mkv"));
        var s3 = scanner.Scan(LibraryKind.Video, [root]);
        Assert.Equal(1, s3.Removed);
        Assert.Single(db.VideoCollections());
    }

    [Fact]
    public void Missing_root_keeps_its_items_and_is_reported()
    {
        using var dir = new TempDir();
        var root = dir.File("usb");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "song.flac"), "a");
        using var db = LibraryDb.InMemory();
        var scanner = new LibraryScanner(db, _ => new ProbeResult(200, "Song", "Artist", "Album"), dir.File("thumbs"));
        scanner.Scan(LibraryKind.Music, [root]);
        Directory.Delete(root, true);
        var s = scanner.Scan(LibraryKind.Music, [root]);
        Assert.Equal([root], s.MissingRoots);
        Assert.Equal(1, db.Count(LibraryKind.Music));
    }

    [Fact]
    public void Albums_group_tracks_and_embedded_art_is_cached()
    {
        using var dir = new TempDir();
        var root = dir.File("music");
        Directory.CreateDirectory(Path.Combine(root, "A"));
        File.WriteAllText(Path.Combine(root, "A", "2.mp3"), "a");
        File.WriteAllText(Path.Combine(root, "A", "1.mp3"), "b");
        using var db = LibraryDb.InMemory();
        var scanner = new LibraryScanner(db, p => new ProbeResult(180, Path.GetFileNameWithoutExtension(p) == "1" ? "One" : "Two",
            "Band", "Record", Track: int.Parse(Path.GetFileNameWithoutExtension(p)), Year: 2001, EmbeddedArt: [0xFF, 0xD8, 0xFF]), dir.File("thumbs"));
        scanner.Scan(LibraryKind.Music, [root]);
        var album = Assert.Single(db.Albums());
        Assert.Equal("Record", album.Name);
        Assert.NotNull(album.Art);
        Assert.True(File.Exists(album.Art));
        Assert.Equal(["One", "Two"], db.AlbumTracks(album).Select(t => t.Title));
    }

    [Theory]
    [InlineData("The.Expanse.S01E02.1080p.WEB-DL", "The Expanse S01E02")]
    [InlineData("Blade.Runner.1982.2160p.UHD", "Blade Runner (1982)")]
    [InlineData("Home Video", "Home Video")]
    public void Titles_are_cleaned(string stem, string expected) =>
        Assert.Equal(expected, LibraryScanner.CleanTitle(stem));
}

public class LaunchTests
{
    [Fact]
    public void Tiles_round_trip_and_reorder()
    {
        using var dir = new TempDir();
        var store = new TileStore(dir.File("tiles.json"));
        var first = store.Apps.First().Id;
        store.Move(first, +1);
        var again = new TileStore(dir.File("tiles.json"));
        Assert.Equal(first, again.Apps.ElementAt(1).Id);
        store.SetHidden(first, true);
        Assert.DoesNotContain(new TileStore(dir.File("tiles.json")).Apps, t => t.Id == first);
    }

    [Fact]
    public void Corrupt_tiles_fall_back()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("tiles.json"), "{");
        var store = new TileStore(dir.File("tiles.json"));
        Assert.NotNull(store.LoadNotice);
        Assert.NotEmpty(store.Web);
    }

    [Fact]
    public void Dormant_follows_foreground_and_launched_app()
    {
        var d = new DormantState();
        var exits = 0;
        d.LaunchedAppExited += () => exits++;
        d.Observe(true, false);
        Assert.Equal(ShellMode.Active, d.Mode);
        d.Launched("cogas", 1234);
        d.Observe(false, true);
        Assert.Equal(ShellMode.Dormant, d.Mode);
        d.Observe(false, false);
        Assert.Equal(1, exits);
        Assert.Equal(ShellMode.Background, d.Mode);
        Assert.Equal("Dormant", DormantState.ContextFor(ShellMode.Dormant, "Shell"));
        Assert.Equal("Player", DormantState.ContextFor(ShellMode.Active, "Player"));
    }

    [Fact]
    public void Untracked_launch_stays_dormant_until_lounge_returns()
    {
        var d = new DormantState();
        d.Launched("steam", null);
        d.Observe(false, false);
        Assert.Equal(ShellMode.Dormant, d.Mode);
        d.Observe(true, false);
        Assert.Equal(ShellMode.Active, d.Mode);
        Assert.False(d.HasLaunchedApp);
    }
}

public class AudioAndThemeTests
{
    [Theory]
    [InlineData("aurora")]
    [InlineData("livingroom")]
    [InlineData("kodi")]
    [InlineData("console")]
    [InlineData("series")]
    public void Ambient_is_audible_bounded_and_deterministic(string preset)
    {
        var a = new float[AmbientSynth.SampleRate * 2 * 4];
        var b = new float[a.Length];
        new AmbientSynth(preset, 3).Read(a);
        new AmbientSynth(preset, 3).Read(b);
        Assert.Equal(a, b);
        Assert.All(a, s => Assert.InRange(s, -1f, 1f));
        var rms = Math.Sqrt(a.Skip(a.Length / 2).Average(s => (double)s * s));
        Assert.InRange(rms, 0.005, 0.5);
    }

    [Fact]
    public void Ui_sounds_exist_for_every_theme()
    {
        foreach (var t in Lounge.Core.Themes.Themes.All)
            foreach (var s in Enum.GetValues<UiSound>())
            {
                var data = UiSounds.Render(s, t.SoundSet);
                Assert.NotEmpty(data);
                Assert.All(data, x => Assert.InRange(x, -1f, 1f));
            }
    }

    [Fact]
    public void Five_themes_with_distinct_layouts_and_ambient()
    {
        var all = Lounge.Core.Themes.Themes.All;
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Select(t => t.Layout).Distinct().Count());
        Assert.All(all, t => Assert.True(AmbientSynth.Presets.ContainsKey(t.AmbientPreset)));
        Assert.Equal("#FF375F", Lounge.Core.Themes.Themes.Aurora.WithAccent("#FF375F").Accent);
        foreach (var t in all)
            foreach (var c in new[] { t.Bg0, t.Bg1, t.Surface, t.Text, t.Accent, t.Focus })
                Assert.Matches("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", c);
    }

    [Fact]
    public void Core_does_not_reference_avalonia()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        foreach (var folder in new[] { "Core", "Media" })
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                Assert.DoesNotMatch(@"(using\s+Avalonia|Avalonia\.\w+\.)", File.ReadAllText(f));
        }
    }
}
