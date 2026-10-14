using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Lounge.Controls;
using Lounge.Core.Focus;
using Lounge.Core.Input;
using Lounge.Core.Library;
using Lounge.Core.Media;
using Lounge.Core.Settings;
using Lounge.Shell;

namespace Lounge.Screens;

/// <summary>A titled screen with a scrolling grid or list of cards.</summary>
public abstract class BrowseScreen : Screen
{
    protected readonly ContentControl Body = new();
    protected readonly TextBlock Heading;
    protected readonly TextBlock Sub = Ui.Text("", T.Body, T.TextMuted);
    protected readonly Dictionary<FocusItem, Action> Options = new();
    protected readonly Dictionary<FocusItem, string?> Art = new();

    protected BrowseScreen(string title)
    {
        Heading = Ui.Header(title, T.Title);
        var grid = Ui.Grid("*", "Auto,*,Auto");
        grid.Children.Add(Ui.V(6, Heading, Sub).Margin(96, 60, 96, 20).Row(0));
        grid.Children.Add(Body.Row(1));
        grid.Children.Add(Hints.Bar(Context, (Act.Accept, "Play"), (Act.ContextMenu, "Options"), (Act.Tertiary, "Search"), (Act.Back, "Back")).Margin(96, 10, 0, 40).Row(2));
        Children.Add(grid);
    }

    protected (ScrollViewer View, FocusGrid Focus) Grid(int columns, double itemW, double itemH, double spacing = 34)
    {
        var focus = new FocusGrid(columns);
        var wrap = new WrapPanel { ItemWidth = itemW + spacing, ItemHeight = itemH + spacing, Margin = new Thickness(96, 20, 60, 80) };
        var sv = new ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        return (sv, focus);
    }

    protected int Columns(double itemW, double spacing = 34) =>
        Math.Max(2, (int)((L.Window.DesignWidth - 156) / (itemW + spacing)));

    protected override void OnFocusMoved(FocusItem? old, FocusItem? now)
    {
        if (now is not null && Art.TryGetValue(now, out var art)) L.Window.Backdrop.SetArt(art, blur: true, strength: 0.5);
    }

    public override bool Handle(ActionEvent e)
    {
        if (e.Phase == ActionPhase.Pressed)
        {
            if (e.Action == Act.ContextMenu && Focused is { } f && Options.TryGetValue(f, out var o)) { o(); return true; }
            if (e.Action == Act.Tertiary) { L.Window.Push(new SearchScreen()); return true; }
        }
        return base.Handle(e);
    }

    public override void OnHidden() => L.Window.Backdrop.SetArt(null);
}

/// <summary>Videos: every folder of videos as a poster, newest first.</summary>
public sealed class VideosScreen : BrowseScreen
{
    public VideosScreen() : base("Videos") { Build(); LibraryService.Updated += Rebuild; }

    private void Rebuild() => Avalonia.Threading.Dispatcher.UIThread.Post(Build);
    public override void OnClosed() => LibraryService.Updated -= Rebuild;

    private void Build()
    {
        var collections = L.Library.VideoCollections();
        Sub.Text = collections.Count == 0 ? "" : $"{collections.Count} collections · {L.Library.Count(LibraryKind.Video)} videos";
        if (collections.Count == 0) { Empty(); return; }
        const double w = 250, h = 375;
        var (view, focus) = Grid(Columns(w), w, h + 70);
        var wrap = (WrapPanel)view.Content!;
        foreach (var c in collections)
        {
            var items = L.Library.InFolder(c.Folder);
            var first = items.FirstOrDefault();
            var card = new PosterCard(w, h, c.Name, null, (cw, ch) => first is not null && c.Art is null ? ImageCache.VideoArt(first, cw, ch, Placeholder(c.Name, cw, ch)) : ImageCache.Art(c.Art, cw, ch, Placeholder(c.Name, cw, ch)), tag: c);
            card.Item.Activate = () => { if (c.Count == 1 && first is not null) Playback.Open(first.Path); else L.Window.Push(new CollectionScreen(c)); };
            Options[card.Item] = () => L.Window.Menu(c.Name,
            [
                new MenuEntry("Open", "folder", () => L.Window.Push(new CollectionScreen(c))),
                new MenuEntry("Play all", "play", () => { var all = L.Library.InFolder(c.Folder).Select(i => i.Path).ToList(); if (all.Count > 0) Playback.Open(all[0], all, 0); }),
                new MenuEntry("Show in Files", "folder", () => L.Window.Push(new FilesScreen(c.Folder))),
            ]);
            Art[card.Item] = c.Art;
            var caption = Ui.V(2, Ui.Text(c.Name, T.Caption, T.Text, FontWeight.SemiBold).Also(t => t.Width = w), Ui.Muted(c.Count == 1 ? "1 video" : $"{c.Count} videos"));
            wrap.Children.Add(Ui.V(14, card, caption));
            focus.Add(card.Item);
        }
        Body.Content = view;
        UseScope(focus);
    }

    private static Control Placeholder(string name, double w, double h) => Ui.Monogram(name, "#2C3E50", Math.Min(w, h)).Also(b => { b.Width = w; b.Height = h; });

    private void Empty()
    {
        var add = new Card(Ui.H(16, Icons.Make("library-add", 34, T.OnAccent), Ui.Text("Add a video folder", T.Body, T.OnAccent, FontWeight.SemiBold)).Margin(36, 20), 40);
        add.Body.Background = T.Accent;
        add.Item.Activate = () => L.Window.Push(new FilesScreen(pickFor: K.VideoRoots));
        Body.Content = Ui.V(24, Icons.Make("video", 120, T.TextFaint, 1.2), Ui.Muted("No videos yet. Add a folder and Lounge will find your films and shows.", T.Body), add)
            .Margin(96, 120, 96, 0).Align(HorizontalAlignment.Left, VerticalAlignment.Top);
        var list = new FocusList(horizontal: false);
        list.Add(add.Item);
        UseScope(list);
    }
}

/// <summary>A folder of videos as an episode list, with resume progress.</summary>
public sealed class CollectionScreen : BrowseScreen
{
    private readonly VideoCollection _c;

    public CollectionScreen(VideoCollection c) : base(c.Name) { _c = c; Build(); }

    public override void OnShown() => Build();

    private void Build()
    {
        var items = L.Library.InFolder(_c.Folder).OrderBy(i => Path.GetFileName(i.Path), NaturalComparer.Instance).ToList();
        Sub.Text = $"{items.Count} videos · {Ui.Time(items.Sum(i => i.Duration))}";
        var list = new FocusList(horizontal: false);
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(96, 10, 96, 80) };
        var paths = items.Select(i => i.Path).ToList();
        for (int n = 0; n < items.Count; n++)
        {
            var item = items[n];
            var index = n;
            var progress = L.History.TryGet(item.Path, out var we) && we.Duration > 0 ? we.Position / we.Duration : -1;
            var thumb = ImageCache.VideoArt(item, 320, 180);
            var g = Ui.Grid("Auto,*,Auto", "Auto");
            var thumbBox = new Grid { Width = 320, Height = 180 };
            thumbBox.Children.Add(new Border { CornerRadius = new CornerRadius(T.Radius), ClipToBounds = true, Child = thumb });
            if (progress > 0)
                thumbBox.Children.Add(new Border
                {
                    Height = 6, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 10), CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                    Child = new Border { Background = T.Accent, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 296 * progress },
                });
            g.Children.Add(thumbBox.Col(0));
            var meta = new List<string>();
            if (item.Duration > 0) meta.Add(Ui.Time(item.Duration));
            if (item.Height > 0) meta.Add(item.Height >= 2000 ? "4K" : item.Height >= 1000 ? "HD" : $"{item.Height}p");
            if (progress > 0) meta.Add($"{Ui.Time(we.Duration - we.Position)} left");
            g.Children.Add(Ui.V(8, Ui.Text(item.Title, T.Body, T.Text, FontWeight.SemiBold), Ui.Muted(string.Join("  ·  ", meta))).Margin(30, 0, 0, 0).Col(1).Align(HorizontalAlignment.Left, VerticalAlignment.Center));
            var card = new Card(g.Margin(14), T.Radius) { FocusScale = 1.02 };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f => card.Body.Background = f ? T.Surface : Brushes.Transparent;
            card.Item.Activate = () => Playback.Open(item.Path, paths, index);
            Options[card.Item] = () => L.Window.Menu(item.Title,
            [
                new MenuEntry(progress > 0 ? "Resume" : "Play", "play", () => Playback.Open(item.Path, paths, index, resume: true)),
                new MenuEntry("Play from the start", "refresh", () => Playback.Open(item.Path, paths, index, resume: false)),
                new MenuEntry(progress > 0 ? "Mark as unwatched" : "Mark as watched", "check", () => { L.History.Forget(item.Path); L.History.Save(); Build(); }),
                new MenuEntry("Show in Files", "folder", () => L.Window.Push(new FilesScreen(Path.GetDirectoryName(item.Path)))),
            ]);
            Art[card.Item] = item.Art;
            panel.Children.Add(card);
            list.Add(card.Item);
        }
        Body.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        var keep = Focused?.Tag;
        UseScope(list);
    }
}

/// <summary>Music: albums, artists and songs, switched with LT/RT.</summary>
public sealed class MusicScreen : BrowseScreen
{
    private int _tab;
    private static readonly string[] Tabs = ["Albums", "Artists", "Songs"];
    private readonly StackPanel _tabBar = new() { Orientation = Orientation.Horizontal, Spacing = 12 };

    public MusicScreen() : base("Music")
    {
        Sub.IsVisible = false;
        ((StackPanel)((Grid)Children[0]).Children[0]).Children.Add(_tabBar.Margin(0, 10, 0, 0));
        Build();
    }

    public override bool Handle(ActionEvent e)
    {
        if (e.Phase == ActionPhase.Pressed && e.Action is Act.NextSection or Act.PrevSection)
        {
            _tab = (_tab + (e.Action == Act.NextSection ? 1 : Tabs.Length - 1)) % Tabs.Length;
            L.Play(Lounge.Core.Audio.UiSound.Move);
            Build();
            return true;
        }
        return base.Handle(e);
    }

    private void Build()
    {
        _tabBar.Children.Clear();
        for (int i = 0; i < Tabs.Length; i++)
        {
            var on = i == _tab;
            _tabBar.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(24), Padding = new Thickness(24, 10),
                Background = on ? T.Text : T.Surface,
                Child = Ui.Text(Tabs[i], T.Caption, on ? T.Bg0 : T.TextMuted, FontWeight.SemiBold),
            });
        }
        _tabBar.Children.Add(Ui.H(8, Hints.Glyph(PadButton.LT, 30), Hints.Glyph(PadButton.RT, 30), Ui.Muted("switch")).Margin(16, 0, 0, 0).Align(HorizontalAlignment.Left, VerticalAlignment.Center));
        Options.Clear();
        Art.Clear();
        switch (_tab)
        {
            case 0: Albums(); break;
            case 1: Artists(); break;
            default: Songs(); break;
        }
    }

    private void Albums()
    {
        var albums = L.Library.Albums();
        if (albums.Count == 0) { EmptyMusic(); return; }
        const double w = 260;
        var (view, focus) = Grid(Columns(w), w, w + 70);
        var wrap = (WrapPanel)view.Content!;
        foreach (var a in albums)
        {
            var card = new PosterCard(w, w, a.Name, a.Artist, (cw, ch) => ImageCache.Art(a.Art, cw, ch, Ui.Monogram(a.Name, "#5E2750", cw).Also(m => { m.Width = cw; m.Height = ch; })), tag: a);
            card.Item.Activate = () => L.Window.Push(new AlbumScreen(a));
            Options[card.Item] = () => L.Window.Menu(a.Name,
            [
                new MenuEntry("Play", "play", () => AlbumScreen.PlayAlbum(a, false)),
                new MenuEntry("Shuffle", "shuffle", () => AlbumScreen.PlayAlbum(a, true)),
                new MenuEntry("Open", "album", () => L.Window.Push(new AlbumScreen(a))),
            ]);
            Art[card.Item] = a.Art;
            wrap.Children.Add(Ui.V(14, card, Ui.V(2, Ui.Text(a.Name, T.Caption, T.Text, FontWeight.SemiBold).Also(t => t.Width = w), Ui.Muted(a.Artist))));
            focus.Add(card.Item);
        }
        Body.Content = view;
        UseScope(focus);
    }

    private void Artists()
    {
        var list = new FocusList(horizontal: false);
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(96, 10, 96, 80) };
        foreach (var artist in L.Library.Artists())
        {
            var albums = L.Library.Albums().Where(a => a.Artist == artist).ToList();
            var row = Ui.H(24, Icons.Make("person", 40, T.TextMuted), Ui.V(2, Ui.Text(artist, T.Body, T.Text, FontWeight.SemiBold), Ui.Muted($"{albums.Count} album{(albums.Count == 1 ? "" : "s")}")));
            var card = new Card(row.Margin(24, 16), 14) { FocusScale = 1.01, ShowRing = false };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f => card.Body.Background = f ? T.Surface : Brushes.Transparent;
            card.Item.Activate = () => L.Window.Menu(artist, albums.Select(a => new MenuEntry(a.Name, "album", () => L.Window.Push(new AlbumScreen(a)), a.Year > 0 ? a.Year.ToString() : null)).ToList());
            Art[card.Item] = albums.FirstOrDefault(a => a.Art is not null)?.Art;
            panel.Children.Add(card);
            list.Add(card.Item);
        }
        Body.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        UseScope(list);
        if (!list.HasItems) EmptyMusic();
    }

    private void Songs()
    {
        var songs = L.Library.All(LibraryKind.Music).OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
        if (songs.Count == 0) { EmptyMusic(); return; }
        var paths = songs.Select(s => s.Path).ToList();
        var list = new FocusList(horizontal: false);
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(96, 10, 96, 80) };
        for (int i = 0; i < songs.Count; i++)
        {
            var s = songs[i];
            var index = i;
            var g = Ui.Grid("Auto,*,Auto", "Auto");
            g.Children.Add(ImageCache.Art(s.Art, 56, 56, Icons.Make("music", 28, T.TextMuted)).Also(b => b.CornerRadius = new CornerRadius(8)).Col(0));
            g.Children.Add(Ui.V(2, Ui.Text(s.Title, T.Body, T.Text, FontWeight.SemiBold), Ui.Muted($"{s.Artist}{(s.Album.Length > 0 ? " · " + s.Album : "")}")).Margin(22, 0, 0, 0).Col(1));
            g.Children.Add(Ui.Muted(Ui.Time(s.Duration)).Col(2).Align(HorizontalAlignment.Right, VerticalAlignment.Center));
            var card = new Card(g.Margin(18, 10), 12) { FocusScale = 1.01, ShowRing = false };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f => card.Body.Background = f ? T.Surface : Brushes.Transparent;
            card.Item.Activate = () => Playback.Open(s.Path, paths, index);
            Art[card.Item] = s.Art;
            panel.Children.Add(card);
            list.Add(card.Item);
        }
        Body.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        UseScope(list);
    }

    private void EmptyMusic()
    {
        var add = new Card(Ui.H(16, Icons.Make("library-add", 34, T.OnAccent), Ui.Text("Add a music folder", T.Body, T.OnAccent, FontWeight.SemiBold)).Margin(36, 20), 40);
        add.Body.Background = T.Accent;
        add.Item.Activate = () => L.Window.Push(new FilesScreen(pickFor: K.MusicRoots));
        Body.Content = Ui.V(24, Icons.Make("music", 120, T.TextFaint, 1.2), Ui.Muted("No music yet. Add a folder and Lounge will sort it into albums.", T.Body), add)
            .Margin(96, 120, 96, 0).Align(HorizontalAlignment.Left, VerticalAlignment.Top);
        var list = new FocusList(horizontal: false);
        list.Add(add.Item);
        UseScope(list);
    }
}

/// <summary>An album: big cover, play and shuffle, and the track list.</summary>
public sealed class AlbumScreen : Screen
{
    private readonly Album _album;
    public override string Title => _album.Name;

    public AlbumScreen(Album album)
    {
        _album = album;
        var tracks = L.Library.AlbumTracks(album);
        var paths = tracks.Select(t => t.Path).ToList();
        var grid = Ui.Grid("620,*", "*");
        var cover = new Border
        {
            Width = 460, Height = 460, CornerRadius = new CornerRadius(T.RadiusLarge), ClipToBounds = true,
            Child = ImageCache.Art(album.Art, 460, 460, Ui.Monogram(album.Name, "#5E2750", 460)),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 30, Blur = 80, Color = Color.FromArgb(160, 0, 0, 0) }),
        };
        var root = new FocusList(horizontal: true);
        var buttons = root.Add(new FocusList(horizontal: false));
        var play = Button("Play", "play", true, () => PlayAlbum(album, false));
        var shuffle = Button("Shuffle", "shuffle", false, () => PlayAlbum(album, true));
        buttons.Add(play.Item);
        buttons.Add(shuffle.Item);
        var left = Ui.V(26, cover,
            Ui.V(6, Ui.Text(album.Name, T.Title, T.Text, T.TitleWeight, TextWrapping.Wrap), Ui.Muted($"{album.Artist}{(album.Year > 0 ? " · " + album.Year : "")} · {tracks.Count} songs", T.Body)),
            Ui.H(16, play, shuffle));
        grid.Children.Add(left.Margin(110, 90, 30, 0).Col(0));

        var list = root.Add(new FocusList(horizontal: false));
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 90, 96, 80) };
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            var index = i;
            var g = Ui.Grid("70,*,Auto", "Auto");
            g.Children.Add(Ui.Muted(t.Track > 0 ? t.Track.ToString() : (i + 1).ToString(), T.Body).Col(0));
            g.Children.Add(Ui.Text(t.Title, T.Body, T.Text, FontWeight.Medium).Col(1));
            g.Children.Add(Ui.Muted(Ui.Time(t.Duration), T.Body).Col(2));
            var card = new Card(g.Margin(22, 14), 12) { FocusScale = 1.01, ShowRing = false };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f => card.Body.Background = f ? T.Surface : Brushes.Transparent;
            card.Item.Activate = () => Playback.Open(t.Path, paths, index);
            panel.Children.Add(card);
            list.Add(card.Item);
        }
        grid.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }.Col(1));
        Children.Add(grid);
        UseScope(root);
        L.Window.Backdrop.SetArt(album.Art, blur: true, strength: 0.55);
    }

    public override void OnHidden() => L.Window.Backdrop.SetArt(null);

    public static void PlayAlbum(Album album, bool shuffle)
    {
        var paths = L.Library.AlbumTracks(album).Select(t => t.Path).ToList();
        if (paths.Count == 0) return;
        L.Queue.Shuffle = false;
        Playback.Open(paths[0], paths, 0);
        if (shuffle) { L.Queue.Shuffle = true; Playback.PlayNext(); }
    }

    private static Card Button(string text, string icon, bool primary, Action run)
    {
        var fg = primary ? T.OnAccent : T.Text;
        var card = new Card(Ui.H(12, Icons.Make(icon, 26, fg), Ui.Text(text, T.Body, fg, FontWeight.SemiBold)).Margin(34, 16), 40) { FocusScale = 1.06 };
        card.Body.Background = primary ? T.Accent : T.Surface;
        card.Item.Activate = run;
        return card;
    }
}

/// <summary>Search across the library, typed with the controller keyboard.</summary>
public sealed class SearchScreen : BrowseScreen
{
    private string _query = "";

    public SearchScreen() : base("Search")
    {
        Sub.Text = "Press Ⓨ to type";
        Ask();
    }

    private async void Ask()
    {
        var q = await L.Window.KeyboardAsync(new KeyboardRequest("Search your library", _query, SelectAll: true,
            Suggestions: L.Library.Artists().Take(5).ToList()));
        if (q is null) { if (_query.Length == 0) L.Window.Pop(); return; }
        _query = q.Trim();
        Run();
    }

    private void Run()
    {
        var hits = _query.Length == 0 ? [] : L.Library.Search(_query);
        Sub.Text = hits.Count == 0 ? $"Nothing found for \"{_query}\". Press Ⓨ to search again." : $"{hits.Count} results for \"{_query}\"";
        var list = new FocusList(horizontal: false);
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(96, 10, 96, 80) };
        foreach (var h in hits)
        {
            var g = Ui.Grid("Auto,*", "Auto");
            g.Children.Add((h.Kind == LibraryKind.Video ? ImageCache.VideoArt(h, 160, 90) : ImageCache.Art(h.Art, 90, 90, Icons.Make("music", 34, T.TextMuted))).Also(b => b.CornerRadius = new CornerRadius(10)).Col(0));
            g.Children.Add(Ui.V(4, Ui.Text(h.Title, T.Body, T.Text, FontWeight.SemiBold), Ui.Muted(h.Kind == LibraryKind.Video ? h.FolderName : $"{h.Artist} · {h.Album}")).Margin(24, 0, 0, 0).Col(1).Align(HorizontalAlignment.Left, VerticalAlignment.Center));
            var card = new Card(g.Margin(14), 14) { FocusScale = 1.01, ShowRing = false };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f => card.Body.Background = f ? T.Surface : Brushes.Transparent;
            var path = h.Path;
            card.Item.Activate = () => Playback.Open(path);
            Art[card.Item] = h.Art;
            panel.Children.Add(card);
            list.Add(card.Item);
        }
        Body.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        UseScope(list);
    }

    public override bool Handle(ActionEvent e)
    {
        if (e.Phase == ActionPhase.Pressed && e.Action == Act.Tertiary) { Ask(); return true; }
        return base.Handle(e);
    }
}
