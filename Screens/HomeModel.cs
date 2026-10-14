using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core;
using Lounge.Core.Launch;
using Lounge.Core.Library;
using Lounge.Core.Settings;
using Lounge.Platform;
using Lounge.Shell;

namespace Lounge.Screens;

public enum EntryShape { Poster, Wide, Square, Icon }

/// <summary>One thing on the home screen: a destination, a video to resume, an album, an
/// app or a web page.</summary>
public sealed record HomeEntry(
    string Title,
    string? Subtitle,
    string? Art,
    string Icon,
    string Color,
    Action Open,
    Action? Options = null,
    double Progress = -1,
    LibraryItem? Video = null,
    Tile? Tile = null,
    string? Backdrop = null);

public sealed record HomeSection(string Id, string Title, string Icon, IReadOnlyList<HomeEntry> Entries, EntryShape Shape);

/// <summary>What the home screen shows, independent of how a theme lays it out.</summary>
public static class HomeModel
{
    public static IReadOnlyList<HomeEntry> Destinations() =>
    [
        new("Videos", "Your films and shows", null, "video", "#0A84FF", () => L.Window.Push(new VideosScreen())),
        new("Music", "Albums, artists and songs", null, "music", "#FF375F", () => L.Window.Push(new MusicScreen())),
        new("Apps & Games", "Cogas, Steam and your apps", null, "gamepad", "#30D158", () => L.Window.Push(new AppsScreen())),
        new("Web", "Streaming in the browser", null, "web", "#FF9F0A", () => L.Window.Push(new AppsScreen(web: true))),
        new("Files", "Browse, play and rename", null, "folder", "#64D2FF", () => L.Window.Push(new FilesScreen())),
        new("Settings", "Make Lounge yours", null, "settings", "#8E8E93", () => L.Window.OpenSettings()),
        new("Power", "Leave, sleep or shut down", null, "power", "#636366", Launcher.ShowPowerMenu),
    ];

    public static IReadOnlyList<HomeSection> Sections()
    {
        var s = L.Settings;
        var sections = new List<HomeSection>();
        if (s.Bool(K.ShowJumpBackIn))
        {
            var jump = JumpBackIn();
            if (jump.Count > 0) sections.Add(new HomeSection("jump", "Jump back in", "clock", jump, EntryShape.Wide));
        }
        if (s.Bool(K.ShowVideos))
        {
            var vids = RecentVideos();
            sections.Add(new HomeSection("videos", "Videos", "video", vids.Count > 0 ? vids : [AddFolderEntry("video")], EntryShape.Poster));
        }
        if (s.Bool(K.ShowMusic))
        {
            var albums = Albums();
            sections.Add(new HomeSection("music", "Music", "music", albums.Count > 0 ? albums : [AddFolderEntry("music")], EntryShape.Square));
        }
        if (s.Bool(K.ShowApps)) sections.Add(new HomeSection("apps", "Apps & Games", "gamepad", Apps(web: false), EntryShape.Square));
        if (s.Bool(K.ShowWeb)) sections.Add(new HomeSection("web", "Web", "web", Apps(web: true), EntryShape.Wide));
        return sections;
    }

    private static HomeEntry AddFolderEntry(string kind) => new(
        kind == "video" ? "Add a video folder" : "Add a music folder",
        "Pick it in Files, then press View",
        null, "library-add", "#3A3A4A",
        () => L.Window.Push(new FilesScreen(pickFor: kind == "video" ? K.VideoRoots : K.MusicRoots)));

    public static List<HomeEntry> JumpBackIn()
    {
        var list = new List<HomeEntry>();
        foreach (var (path, e) in L.History.Recent(30))
        {
            if (list.Count >= 12) break;
            if (e.Position <= 0 || !File.Exists(path)) continue;
            var item = L.Library.Get(path);
            var title = item?.Title ?? LibraryScanner.CleanTitle(Path.GetFileNameWithoutExtension(path));
            var left = e.Duration > 0 ? $"{Ui.Time(e.Duration - e.Position)} left" : "Resume";
            var p = path;
            list.Add(new HomeEntry(title, left, item?.Art, "play", "#0A84FF",
                () => Playback.Open(p, resume: true),
                () => L.Window.Menu(title,
                [
                    new MenuEntry("Resume", "play", () => Playback.Open(p, resume: true)),
                    new MenuEntry("Play from the start", "refresh", () => Playback.Open(p, resume: false)),
                    new MenuEntry("Remove from Jump back in", "close", () => { L.History.Forget(p); L.History.Save(); L.Window.Find<HomeScreen>()?.Rebuild(); }),
                ]),
                e.Duration > 0 ? e.Position / e.Duration : 0,
                Video: item ?? new LibraryItem(p, LibraryKind.Video, "", Path.GetDirectoryName(p) ?? "", title, "", "", "", 0, 0, e.Duration, 0, 0, null, 0)));
        }
        return list;
    }

    private static List<HomeEntry> RecentVideos()
    {
        return L.Library.VideoCollections()
            .OrderByDescending(c => c.Added).Take(16)
            .Select(c =>
            {
                var items = L.Library.InFolder(c.Folder);
                var first = items.FirstOrDefault();
                return new HomeEntry(c.Name, c.Count == 1 ? first?.Title : $"{c.Count} videos", c.Art, "video", "#0A84FF",
                    () =>
                    {
                        if (c.Count == 1 && first is not null) Playback.Open(first.Path);
                        else L.Window.Push(new CollectionScreen(c));
                    }, Video: c.Art is null ? first : null);
            }).ToList();
    }

    private static List<HomeEntry> Albums()
    {
        return L.Library.Albums().Take(16)
            .Select(a => new HomeEntry(a.Name, a.Artist, a.Art, "album", "#FF375F", () => L.Window.Push(new AlbumScreen(a))))
            .ToList();
    }

    public static List<HomeEntry> Apps(bool web)
    {
        var tiles = web ? L.Tiles.Web : L.Tiles.Apps;
        var list = tiles.Select(TileEntry).ToList();
        list.Add(new HomeEntry(web ? "Add a website" : "Add an app", null, null, "plus", "#3A3A4A",
            () => L.Window.Push(web ? TileEditorScreen.NewWeb() : new AddAppScreen())));
        return list;
    }

    public static HomeEntry TileEntry(Tile t) => new(t.Name, t.IsWeb ? Host(t.Target) : t.Kind == TileKind.Cogas ? "Big Picture" : null,
        TileArt(t), t.IsWeb ? "web" : "gamepad", t.Color, () => Launcher.Launch(t),
        () => TileMenu(t), Tile: t);

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : url;

    public static void TileMenu(Tile t)
    {
        var items = new List<MenuEntry>
        {
            new("Open", "play", () => Launcher.Launch(t)),
        };
        if (t.IsWeb) items.Add(new MenuEntry("Open in full browser", "external", () => Launcher.Launch(t.Clone().Also(c => c.OpenExternally = true))));
        items.Add(new MenuEntry("Edit", "pencil", () => L.Window.Push(new TileEditorScreen(t))));
        items.Add(new MenuEntry("Move earlier", "back", () => { L.Tiles.Move(t.Id, -1); L.Window.Find<HomeScreen>()?.Rebuild(); }));
        items.Add(new MenuEntry("Move later", "chevron", () => { L.Tiles.Move(t.Id, +1); L.Window.Find<HomeScreen>()?.Rebuild(); }));
        items.Add(new MenuEntry("Hide from Home", "eye-off", () => { L.Tiles.SetHidden(t.Id, true); L.Window.Find<HomeScreen>()?.Rebuild(); }));
        L.Window.Menu(t.Name, items);
    }

    /// <summary>The picture for a tile: a chosen image, else the app's own icon (cached).</summary>
    public static string? TileArt(Tile t)
    {
        if (!string.IsNullOrEmpty(t.Icon) && File.Exists(t.Icon)) return t.Icon;
        string? source = t.Kind switch
        {
            TileKind.App => t.Target,
            TileKind.Cogas => Win32.FindCogas(L.Settings.String(K.CogasPath)),
            TileKind.StoreApp => $"shell:AppsFolder\\{t.Target}",
            _ => null,
        };
        if (source is null || L.Preview) return null;
        var file = Path.Combine(AppPaths.Thumbs, "icon-" + LibraryScanner.Hash(source) + ".png");
        if (File.Exists(file)) return file;
        _ = Task.Run(() =>
        {
            var icon = ShellIcons.Get(source);
            if (icon is null) return;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    using var wb = ImageCache.FromBgra(icon.Value.Bgra, icon.Value.Width, icon.Value.Height);
                    wb.Save(file);
                    L.Window.Find<HomeScreen>()?.Rebuild();
                }
                catch (IOException) { }
            });
        });
        return null;
    }

    /// <summary>The art area of an entry: picture, app icon on a colour wash, or glyph.</summary>
    public static Control Art(HomeEntry e, double w, double h, bool iconStyle = false, bool corner = false)
    {
        var placeholder = Glyph(e, w, h, corner);
        if (e.Tile is not null)
        {
            if (e.Art is null) return Ui.Monogram(e.Title, e.Color, Math.Min(w, h)).Also(b => { b.Width = w; b.Height = h; });
            // App icons are small square pictures: centre them on their brand colour.
            var bg = new Border { Width = w, Height = h, Background = Wash(e.Color) };
            var size = Math.Min(w, h) * 0.56;
            bg.Child = ImageCache.Art(e.Art, size, size, stretch: Stretch.Uniform).Also(a => { a.Background = Brushes.Transparent; a.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center; a.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center; });
            return bg;
        }
        if (e.Video is not null && e.Art is null) return ImageCache.VideoArt(e.Video, w, h, placeholder);
        if (e.Art is null || iconStyle) return placeholder;
        return ImageCache.Art(e.Art, w, h, placeholder);
    }

    public static IBrush Wash(string hex)
    {
        Color c;
        try { c = Color.Parse(hex); } catch (FormatException) { c = Colors.SlateGray; }
        var dark = Color.FromArgb(255, (byte)(c.R * 0.55), (byte)(c.G * 0.55), (byte)(c.B * 0.55));
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
            GradientStops = { new GradientStop(c, 0), new GradientStop(dark, 1) },
        };
    }

    public static Control Glyph(HomeEntry e, double w, double h, bool corner = false) => corner ? new Border
    {
        Width = w, Height = h,
        Background = Wash(e.Color),
        Child = Icons.Make(e.Icon, Math.Max(28, h * 0.3), Brushes.White, 1.6)
            .Also(i => { i.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right; i.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top; i.Margin = new Avalonia.Thickness(0, 16, 18, 0); }),
    } : new Border
    {
        Width = w, Height = h,
        Background = Wash(e.Color),
        Child = Icons.Make(e.Icon, Math.Min(w, h) * 0.34, Brushes.White, 1.6)
            .Also(i => { i.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center; i.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center; }),
    };
}
