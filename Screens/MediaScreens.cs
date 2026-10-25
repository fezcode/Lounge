using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core.Audio;
using Lounge.Core.Focus;
using Lounge.Core.Input;
using Lounge.Core.Media;
using Lounge.Core.Settings;
using Lounge.Media;
using Lounge.Shell;

namespace Lounge.Screens;

/// <summary>Now Playing for music: the cover large over its own blurred colours, the
/// transport, and the queue. Music keeps playing when you leave; a chip in the corner
/// shows what is on.</summary>
public sealed class NowPlayingScreen : Screen
{
    private readonly Border _cover = new() { Width = 560, Height = 560, CornerRadius = new CornerRadius(28), ClipToBounds = true };
    private readonly TextBlock _title = Ui.Text("", 54, T.Text, T.TitleWeight);
    private readonly TextBlock _artist = Ui.Text("", T.Heading, T.TextMuted);
    private readonly TextBlock _elapsed = Ui.Text("", T.Caption, T.TextMuted);
    private readonly TextBlock _remaining = Ui.Text("", T.Caption, T.TextMuted);
    private readonly Border _fill = new() { Background = T.Text, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Grid _track = new() { Height = 8 };
    private readonly ContentControl _playIcon = new();
    private readonly StackPanel _queue = new() { Spacing = 4 };
    private readonly DispatcherTimer _timer;
    private Card? _shuffle, _repeat;
    // Not null: the first refresh must fill the cover and title even when nothing plays.
    private string? _shownPath = "\0";

    public override string Context => "Player";
    public override string Title => "Now Playing";
    public override bool WantsAmbient => false;

    public NowPlayingScreen()
    {
        _cover.BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 40, Blur = 100, Color = Color.FromArgb(170, 0, 0, 0) });
        _track.Children.Add(new Border { Background = T.WithAlpha(T.TextColor, 0.2), CornerRadius = new CornerRadius(4) });
        _track.Children.Add(_fill);
        var times = Ui.Grid("*,*", "Auto");
        times.Children.Add(_elapsed.Col(0));
        times.Children.Add(_remaining.Col(1).Align(HorizontalAlignment.Right, VerticalAlignment.Top));

        _queueFocus = new FocusList(horizontal: false);
        if (_car) BuildCar(times);
        else BuildClassic(times);
        Refresh();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        L.Player.StateChanged += OnState;
    }

    /// <summary>The cover large on the left with the transport under it, Up next beside it.</summary>
    private void BuildClassic(Grid times)
    {
        var root = new FocusList(horizontal: true);
        var transport = new FocusList(horizontal: true);
        root.Add(transport);
        var prev = Button("previous", () => Playback.PlayPrevious());
        var play = Button("pause", () => { L.Player.TogglePause(); Refresh(); }, big: true);
        _playIcon.Content = Icons.Make("pause", 44, T.Bg0);
        play.Content = _playIcon.Align(HorizontalAlignment.Center, VerticalAlignment.Center);
        play.Body.Background = T.Text;
        var next = Button("next", () => Playback.PlayNext());
        _shuffle = Button("shuffle", () => { L.Queue.Shuffle = !L.Queue.Shuffle; Refresh(); });
        _repeat = Button("repeat", () => { L.Queue.Repeat = (RepeatMode)(((int)L.Queue.Repeat + 1) % 3); Refresh(); });
        foreach (var b in new[] { _shuffle, prev, play, next, _repeat }) transport.Add(b.Item);
        transport.Index = 2;

        var left = Ui.V(30,
            _cover,
            Ui.V(6, _title, _artist),
            Ui.V(10, _track, times),
            Ui.H(26, _shuffle, prev, play, next, _repeat).Align(HorizontalAlignment.Center, VerticalAlignment.Top));
        left.Width = 640;

        root.Add(_queueFocus);
        var right = Ui.V(18, Ui.Header("Up next", T.Heading),
            new ScrollViewer { Content = _queue, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Height = 760 });
        var grid = Ui.Grid("Auto,*", "*");
        grid.Children.Add(left.Margin(130, 90, 70, 0).Col(0));
        grid.Children.Add(right.Margin(40, 110, 96, 0).Col(1));
        Children.Add(grid);
        UseScope(root);
    }

    private readonly FocusList _queueFocus;
    private readonly bool _car = CarPlay.On;

    /// <summary>CarPlay's Now Playing: a bar with Back and the title, the cover on the left,
    /// and beside it the song, the progress, plain transport glyphs, and a row of small
    /// buttons (shuffle, Up Next, repeat). Up Next opens as a list.</summary>
    private void BuildCar(Grid times)
    {
        _cover.Width = _cover.Height = 600;
        _cover.CornerRadius = new CornerRadius(18);
        _title.FontSize = 50;
        _title.FontWeight = FontWeight.Bold;
        _title.TextWrapping = TextWrapping.Wrap;
        _title.MaxHeight = 130;
        _artist.Foreground = T.TextMuted;

        Card Glyph(string icon, double size, double glyph, Action run)
        {
            var card = new Card(Icons.Make(icon, glyph, T.Text).Align(HorizontalAlignment.Center, VerticalAlignment.Center), 22)
            {
                Width = size, Height = size, FocusScale = 1.0, ShowGlow = false, VerticalAlignment = VerticalAlignment.Center,
            };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f => card.Body.Background = f ? T.Surface : Brushes.Transparent;
            card.Item.Activate = run;
            return card;
        }

        var transport = new FocusList(horizontal: true);
        var prev = Glyph("previous", 130, 58, () => Playback.PlayPrevious());
        var play = Glyph("pause", 150, 72, () => { L.Player.TogglePause(); Refresh(); });
        _playIcon.Content = Icons.Make("pause", 72, T.Text);
        play.Content = _playIcon.Align(HorizontalAlignment.Center, VerticalAlignment.Center);
        var next = Glyph("next", 130, 58, () => Playback.PlayNext());
        foreach (var b in new[] { prev, play, next }) transport.Add(b.Item);
        transport.Index = 1;

        var small = new FocusList(horizontal: true);
        _shuffle = Glyph("shuffle", 96, 40, () => { L.Queue.Shuffle = !L.Queue.Shuffle; Refresh(); });
        var upNext = Glyph("list", 96, 40, ShowUpNext);
        _repeat = Glyph("repeat", 96, 40, () => { L.Queue.Repeat = (RepeatMode)(((int)L.Queue.Repeat + 1) % 3); Refresh(); });
        foreach (var b in new[] { _shuffle, upNext, _repeat }) small.Add(b.Item);
        small.Index = 1;

        var root = new FocusList(horizontal: false);
        root.Add(transport);
        root.Add(small);

        var bar = Ui.Grid("*,Auto,*", "Auto");
        bar.Children.Add(Ui.H(6, Icons.Make("back", 30, T.Accent), Ui.Text("Back", T.Body, T.Accent, FontWeight.Medium)).Align(HorizontalAlignment.Left, VerticalAlignment.Center).Col(0));
        bar.Children.Add(Ui.Text("Now Playing", T.Heading, T.Text, FontWeight.Bold).Col(1));

        _track.Height = 10;
        var right = Ui.V(0,
            _title,
            _artist.Margin(0, 10, 0, 0),
            Ui.V(12, _track, times).Margin(0, 44, 0, 0),
            Ui.H(46, prev, play, next).Align(HorizontalAlignment.Center, VerticalAlignment.Top).Margin(0, 30, 0, 0),
            Ui.H(90, _shuffle, upNext, _repeat).Align(HorizontalAlignment.Center, VerticalAlignment.Top).Margin(0, 14, 0, 0));
        right.VerticalAlignment = VerticalAlignment.Center;

        var body = Ui.Grid("Auto,*", "*");
        body.Children.Add(_cover.Align(HorizontalAlignment.Left, VerticalAlignment.Center).Col(0));
        body.Children.Add(right.Margin(80, 0, 0, 0).Col(1));
        var grid = Ui.Grid("*", "Auto,*");
        grid.Children.Add(bar.Margin(48, 34, 48, 0).Row(0));
        grid.Children.Add(body.Margin(90, 10, 100, 60).Row(1));
        Children.Add(grid);
        UseScope(root);
    }

    /// <summary>Up Next as a list over Now Playing: choosing a song plays it.</summary>
    private void ShowUpNext()
    {
        var items = L.Queue.Items;
        if (items.Count == 0) { L.Window.Toast("Nothing is queued.", "list"); return; }
        var entries = items.Select((path, i) =>
        {
            var item = L.Library.Get(path);
            var current = i == L.Queue.Index;
            return new MenuEntry(item?.Title ?? Path.GetFileNameWithoutExtension(path), current ? "audio" : "music",
                () => { L.Queue.Jump(i); Playback.StartAudio(path); }, item?.Artist, Checked: current);
        }).ToList();
        L.Window.Menu("Up Next", entries);
    }

    private static Card Button(string icon, Action run, bool big = false)
    {
        // Fixed square size: in the row they would otherwise stretch to the big button's height.
        var size = big ? 100 : 78;
        var card = new Card(Icons.Make(icon, big ? 44 : 34, T.Text).Align(HorizontalAlignment.Center, VerticalAlignment.Center), 60)
        {
            FocusScale = 1.12, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
        };
        card.Body.Background = T.Surface;
        card.Body.CornerRadius = new CornerRadius(60);
        card.Item.Activate = run;
        return card;
    }

    private void OnState(PlayerState s) => Dispatcher.UIThread.Post(() =>
    {
        if (s == PlayerState.Ended && L.NowPlayingKind == MediaKind.Audio && !Playback.PlayNext()) Refresh();
        Refresh();
    });

    private void Refresh()
    {
        var p = L.Player;
        p.Tick();
        var path = p.Path;
        if (path != _shownPath)
        {
            _shownPath = path;
            var item = path is null ? null : L.Library.Get(path);
            _title.Text = item?.Title ?? (path is null ? "Nothing playing" : Path.GetFileNameWithoutExtension(path));
            _artist.Text = item is null ? "" : string.Join(" · ", new[] { item.Artist, item.Album }.Where(x => x.Length > 0));
            _cover.Child = ImageCache.Art(item?.Art, 560, 560, Ui.Monogram(_title.Text ?? "?", "#5E2750", 560));
            L.Window.Backdrop.SetArt(item?.Art, blur: true, strength: 0.9);
            BuildQueue();
        }
        var dur = p.Duration;
        var pos = p.Position;
        _elapsed.Text = Ui.Time(pos);
        _remaining.Text = dur > 0 ? "-" + Ui.Time(dur - pos) : "";
        _fill.Width = _track.Bounds.Width * (dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0);
        if (_car)
        {
            // CarPlay: glyphs on nothing; shuffle and repeat turn blue when on.
            _playIcon.Content = Icons.Make(p.IsPlaying ? "pause" : "play", 72, T.Text);
            if (_shuffle is not null) _shuffle.Content = Icons.Make("shuffle", 40, L.Queue.Shuffle ? T.Accent : T.Text).Align(HorizontalAlignment.Center, VerticalAlignment.Center);
            if (_repeat is not null) _repeat.Content = Icons.Make("repeat", 40, L.Queue.Repeat != RepeatMode.Off ? T.Accent : T.Text).Align(HorizontalAlignment.Center, VerticalAlignment.Center);
            return;
        }
        _playIcon.Content = Icons.Make(p.IsPlaying ? "pause" : "play", 44, T.Bg0);
        if (_shuffle is not null) _shuffle.Body.Background = L.Queue.Shuffle ? T.WithAlpha(T.AccentColor, 0.6) : T.Surface;
        if (_repeat is not null) _repeat.Body.Background = L.Queue.Repeat != RepeatMode.Off ? T.WithAlpha(T.AccentColor, 0.6) : T.Surface;
    }

    private void BuildQueue()
    {
        _queue.Children.Clear();
        _queueFocus.Clear();
        var items = L.Queue.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var path = items[i];
            var index = i;
            var item = L.Library.Get(path);
            var current = i == L.Queue.Index;
            var g = Ui.Grid("Auto,*,Auto", "Auto");
            g.Children.Add((current ? Icons.Make("audio", 28, T.Accent) : Ui.Muted((i + 1).ToString(), T.Body)).Also(c => c.Width = 44).Col(0));
            g.Children.Add(Ui.V(2, Ui.Text(item?.Title ?? Path.GetFileNameWithoutExtension(path), T.Body, current ? T.Accent : T.Text, FontWeight.SemiBold),
                Ui.Muted(item?.Artist ?? "")).Col(1));
            g.Children.Add(Ui.Muted(item is null ? "" : Ui.Time(item.Duration)).Col(2));
            var card = new Card(g.Margin(20, 12), 12) { FocusScale = 1.01, ShowRing = false };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f => card.Body.Background = f ? T.Surface : Brushes.Transparent;
            card.Item.Activate = () => { L.Queue.Jump(index); Playback.StartAudio(path); };
            _queue.Children.Add(card);
            _queueFocus.Add(card.Item);
        }
        RefreshFocus();
    }

    public override bool Handle(ActionEvent e)
    {
        var p = L.Player;
        switch (e.Action)
        {
            case Act.SeekForward or Act.SeekBackward:
                if (e.Phase is ActionPhase.Pressed or ActionPhase.Changed)
                {
                    var dir = e.Action == Act.SeekForward ? 1 : -1;
                    p.Seek(p.Position + dir * (2 + 18 * e.Value) * 0.25);
                }
                return true;
        }
        if (e.Phase is ActionPhase.Released or ActionPhase.Changed) return true;
        switch (e.Action)
        {
            // A presses whatever is focused (the transport buttons, a song in Up next);
            // focus starts on Play, so A alone still plays and pauses.
            case Act.PlayPause: return base.Handle(e with { Action = Act.Accept });
            case Act.NextItem: Playback.PlayNext(); return true;
            case Act.PrevItem: Playback.PlayPrevious(); return true;
            // B (Stop in the player's keymap) leaves: while music plays, it asks whether to
            // keep it playing in the background. Stop in the View menu always stops.
            case Act.Stop:
                return OnBack();
            case Act.ContextMenu:
                L.Window.Menu("Music",
                [
                    new MenuEntry(p.IsPlaying ? "Pause" : "Play", p.IsPlaying ? "pause" : "play", () => p.TogglePause()),
                    new MenuEntry("Shuffle", "shuffle", () => { L.Queue.Shuffle = !L.Queue.Shuffle; Refresh(); }, Checked: L.Queue.Shuffle),
                    new MenuEntry("Repeat", "repeat", () => { L.Queue.Repeat = (RepeatMode)(((int)L.Queue.Repeat + 1) % 3); Refresh(); }, L.Queue.Repeat.ToString()),
                    new MenuEntry("Stop", "stop", () => { L.Player.Close(); L.Window.Pop(); }, Danger: true),
                ]);
                return true;
        }
        // In Now Playing the D-pad moves focus like a normal screen.
        var mapped = e.Action switch
        {
            Act.SkipBack => Act.Left, Act.SkipForward => Act.Right, Act.BigSkipForward => Act.Up, Act.BigSkipBack => Act.Down, _ => e.Action,
        };
        return base.Handle(e with { Action = mapped == Act.Stop ? Act.Back : mapped });
    }

    /// <summary>B while music plays: keep it playing in the background, or stop it. The
    /// Audio setting can answer for good; B on the question keeps the music and leaves.</summary>
    protected override bool OnBack()
    {
        if (!L.Player.IsPlaying || L.NowPlayingKind != MediaKind.Audio) return base.OnBack();
        switch (L.Settings.String(K.MusicOnLeave))
        {
            case "keep":
                L.Window.Toast("The music keeps playing.", "music", 2);
                return base.OnBack();
            case "stop":
                L.Player.Close();
                return base.OnBack();
        }
        L.Window.Ask("Keep the music playing?",
            "It can play on in the background while you look around Lounge. Change what happens here in Settings > Audio.",
            ["Keep playing", "Stop the music"], 0, i =>
            {
                if (i < 0 || L.Window.Top != this) return;
                if (i == 1) L.Player.Close();
                else L.Window.Toast(CarPlay.On ? "The music keeps playing. It's in the sidebar." : "The music keeps playing. It shows in the corner.", "music", 2.5);
                L.Play(UiSound.Back);
                L.Window.Pop();
            }, "music");
        return true;
    }

    public override void OnHidden() => L.Window.Backdrop.SetArt(null);

    public override void OnClosed()
    {
        _timer.Stop();
        L.Player.StateChanged -= OnState;
    }
}

/// <summary>Pictures from Files, full screen. Left/right to flip through.</summary>
public sealed class ImageViewerScreen : Screen
{
    private readonly IReadOnlyList<string> _images;
    private int _index;
    private readonly Border _view = new();
    private readonly TextBlock _caption = Ui.Text("", T.Body, Brushes.White, FontWeight.SemiBold);

    public override string Title => "Pictures";
    public override bool ShowsChrome => false;
    public override bool ShowsBackdrop => false;
    public override bool WantsAmbient => true;

    public ImageViewerScreen(IReadOnlyList<string> images, int index)
    {
        _images = images;
        _index = Math.Max(0, index);
        Background = Brushes.Black;
        Children.Add(_view);
        Children.Add(new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom, Height = 180, Background = Ui.VerticalFade(Colors.Black, 0, 0.8),
            Child = Ui.Grid("*,Auto", "Auto").Also(g =>
            {
                g.Children.Add(_caption.Col(0));
                g.Children.Add(Hints.Bar("Shell", (Act.Left, "Previous"), (Act.ContextMenu, "Options"), (Act.Back, "Back")).Col(1));
            }).Margin(96, 0, 96, 50).Align(HorizontalAlignment.Stretch, VerticalAlignment.Bottom),
        });
        Show();
    }

    private void Show()
    {
        if (_images.Count == 0) return;
        var path = _images[_index];
        _caption.Text = $"{Path.GetFileName(path)}   ·   {_index + 1} of {_images.Count}";
        _view.Background = null;
        _ = ImageCache.LoadAsync(path, 3840).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (t.Result is { } bmp && _images[_index] == path) _view.Background = new ImageBrush(bmp) { Stretch = Stretch.Uniform };
        }));
    }

    public override bool Handle(ActionEvent e)
    {
        if (e.Phase is not (ActionPhase.Pressed or ActionPhase.Repeated)) return true;
        switch (e.Action)
        {
            case Act.Left or Act.SkipBack: if (_index > 0) { _index--; Show(); L.Play(UiSound.Move); } return true;
            case Act.Right or Act.SkipForward or Act.Accept: if (_index < _images.Count - 1) { _index++; Show(); L.Play(UiSound.Move); } return true;
            case Act.ContextMenu:
                var path = _images[_index];
                L.Window.Menu(Path.GetFileName(path),
                [
                    new MenuEntry("Use as wallpaper", "image", () => { L.Settings.Set(K.WallpaperFor(T.Def.Id), path); L.Window.Toast($"Wallpaper set for {T.Def.Name}.", "image"); }),
                    new MenuEntry("Show in Files", "folder", () => L.Window.Push(new FilesScreen(Path.GetDirectoryName(path)))),
                ]);
                return true;
        }
        return base.Handle(e);
    }
}

/// <summary>The folders of the video or music library: remove one, rescan, or add more
/// from Files.</summary>
public sealed class FoldersScreen : BrowseScreen
{
    private readonly SettingDescriptor _setting;

    public FoldersScreen(SettingDescriptor setting) : base(setting.Label)
    {
        _setting = setting;
        Sub.Text = setting.Help;
        Build();
    }

    public override void OnShown() => Build();

    private void Build()
    {
        var list = new FocusList(horizontal: false);
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(96, 10, 96, 80) };
        var add = new Card(Ui.H(18, Icons.Make("plus", 30, T.Accent), Ui.Text("Add a folder from Files", T.Body, T.Accent, FontWeight.SemiBold)).Margin(26, 18), 14) { FocusScale = 1.01 };
        add.Body.Background = T.WithAlpha(T.AccentColor, 0.12);
        add.Item.Activate = () => L.Window.Push(new FilesScreen(pickFor: _setting.Key));
        panel.Children.Add(add);
        list.Add(add.Item);
        foreach (var folder in L.Settings.List(_setting.Key))
        {
            var exists = Directory.Exists(folder);
            var g = Ui.Grid("Auto,*,Auto", "Auto");
            g.Children.Add(Icons.Make(exists ? "folder" : "warning", 32, exists ? T.TextMuted : T.Danger).Margin(0, 0, 20, 0).Col(0));
            g.Children.Add(Ui.V(2, Ui.Text(Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder, T.Body, T.Text, FontWeight.SemiBold),
                Ui.Muted(exists ? folder : folder + " · can't be reached")).Col(1));
            g.Children.Add(Ui.Muted("Ⓐ remove").Col(2));
            var card = new Card(g.Margin(26, 16), 14) { FocusScale = 1.01, ShowRing = false };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f => card.Body.Background = f ? T.Surface : Brushes.Transparent;
            var f = folder;
            card.Item.Activate = () => L.Window.Ask("Remove this folder?", $"{f}\nIts videos or songs leave the library. Nothing is deleted from disk.", ["Remove", "Cancel"], 1, i =>
            {
                if (i != 0) return;
                L.Settings.RemoveFolder(_setting.Key, f);
                Build();
            }, "folder");
            Options[card.Item] = () => L.Window.Menu(Path.GetFileName(f.TrimEnd('\\')),
            [
                new MenuEntry("Open in Files", "folder", () => L.Window.Push(new FilesScreen(f))),
                new MenuEntry("Rescan", "refresh", () => _ = LibraryService.ScanAsync(quiet: false)),
                new MenuEntry("Remove from library", "trash", () => { L.Settings.RemoveFolder(_setting.Key, f); Build(); }, Danger: true),
            ]);
            panel.Children.Add(card);
            list.Add(card.Item);
        }
        Body.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        UseScope(list);
    }
}
