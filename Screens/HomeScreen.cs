using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core.Focus;
using Lounge.Core.Input;
using Lounge.Core.Launch;
using Lounge.Core.Themes;
using Lounge.Shell;

namespace Lounge.Screens;

/// <summary>The home screen. The same content (destinations, jump back in, videos,
/// music, apps, web) is laid out differently by each theme, which is what makes the
/// themes feel like different devices rather than different colours.</summary>
public sealed class HomeScreen : Screen
{
    private readonly Dictionary<FocusItem, HomeEntry> _entries = new();
    private TextBlock? _heroTitle, _heroSubtitle;
    private Control? _heroHints;
    private string? _restoreTitle;
    private IDisposable? _contentDebounce;

    public override string Title => "Home";

    public HomeScreen()
    {
        Build();
        LibraryService.Updated += OnLibraryUpdated;
    }

    private void OnLibraryUpdated() => Dispatcher.UIThread.Post(() => { if (IsVisible) Rebuild(); });

    public override void OnClosed() => LibraryService.Updated -= OnLibraryUpdated;

    // Nothing blinks or ticks behind another screen (coming back rebuilds the view and its timers).
    public override void OnHidden()
    {
        _blink?.Stop();
        _carTimer?.Stop();
        _barClock?.Stop();
    }

    private bool _shownOnce;

    public override void OnShown()
    {
        // The constructor already built the first view; later visits refresh it.
        if (_shownOnce) Rebuild();
        _shownOnce = true;
    }

    public void Rebuild()
    {
        _restoreTitle = Focused is { } f && _entries.TryGetValue(f, out var e) ? e.Title : null;
        Build();
    }

    private void Build()
    {
        Children.Clear();
        _entries.Clear();
        _blink?.Stop();
        _blink = null;
        // A line-up still due from the old layout must not land on the new one.
        _contentDebounce?.Dispose();
        _contentDebounce = null;
        _fallbackFocus = null;
        _building = true;
        _previewId = null;
        _kodiShown = null;
        _programmeShown = null;
        _heroTitle = _heroSubtitle = null;
        _sagaArt = null;
        _sagaBar = null;
        _carPage.Clear();
        _carTimer?.Stop();
        _carTimer = null;
        _barClock?.Stop();
        _barClock = null;
        var (content, root) = T.Def.Layout switch
        {
            HomeLayout.BottomRibbon => LivingRoom(),
            HomeLayout.SideMenu => Kodi(),
            HomeLayout.IconRow => ConsoleOne(),
            HomeLayout.TileGrid => Series(),
            HomeLayout.Programme => Programme(),
            HomeLayout.Cabinet => Cabinet(),
            HomeLayout.Dock => Dock(),
            HomeLayout.Glance => Glance(),
            HomeLayout.Sidebar => Sidebar(),
            HomeLayout.Hall => Hall(),
            _ => Aurora(),
        };
        _building = false;
        Children.Add(content);
        UseScope(root);
        if (_restoreTitle is not null && _entries.FirstOrDefault(kv => kv.Value.Title == _restoreTitle).Key is { } again)
            FocusItem(again);
        else if (_fallbackFocus is not null)
            FocusItem(_fallbackFocus);
        UpdateHero(Focused);
        ShowCarPage(Focused, snap: true);
        _carTimer?.Start();
        _barClock?.Start();
    }

    // ------------------------------------------------------------------ shared pieces

    private Card EntryCard(HomeEntry e, double w, double h, bool caption = true, bool inside = false, double radius = -1)
    {
        Card card;
        if (inside || !caption)
        {
            card = new PosterCard(w, h, e.Title, inside ? e.Subtitle : null, (cw, ch) => HomeModel.Art(e, cw, ch, corner: inside), e.Progress, e, captionInside: inside);
        }
        else
        {
            card = new PosterCard(w, h, e.Title, e.Subtitle, (cw, ch) => HomeModel.Art(e, cw, ch), e.Progress, e);
        }
        if (radius >= 0) card.Body.CornerRadius = new CornerRadius(radius);
        card.Item.Activate = e.Open;
        card.Item.Secondary = e.Options;
        _entries[card.Item] = e;
        return card;
    }

    /// <summary>A card with its caption underneath, which fades in on focus for posters.</summary>
    private Control Captioned(Card card, HomeEntry e, double width, bool alwaysShow = true)
    {
        var title = Ui.Text(e.Title, T.Caption, T.Text, FontWeight.SemiBold);
        title.Width = width;
        var sub = Ui.Text(e.Subtitle ?? "", T.Caption, T.TextMuted);
        sub.Width = width;
        var captions = Ui.V(2, title);
        if (!string.IsNullOrEmpty(e.Subtitle)) captions.Children.Add(sub);
        captions.Opacity = alwaysShow ? 1 : 0;
        if (!alwaysShow) card.Focused += f => captions.Opacity = f ? 1 : 0;
        return Ui.V(18, card, captions);
    }

    private (Control View, FocusList Focus) Row(HomeSection section, double cardW, double cardH, bool captionsBelow = true, bool inside = false,
        double headerSize = T.Heading, double spacing = 34, double left = 96)
    {
        var focus = new FocusList(horizontal: true);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, Margin = new Thickness(left, 30, left, 40) };
        foreach (var e in section.Entries)
        {
            var card = EntryCard(e, cardW, cardH, captionsBelow, inside);
            panel.Children.Add(captionsBelow && !inside ? Captioned(card, e, cardW) : card);
            focus.Add(card.Item);
        }
        var scroll = new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ClipToBounds = false,
        };
        var header = Ui.Header(section.Title, headerSize).Margin(left, 0, 0, 0);
        return (Ui.V(0, header, scroll), focus);
    }

    private static (double W, double H) Size(EntryShape s, double scale = 1) => s switch
    {
        EntryShape.Poster => (250 * scale, 375 * scale),
        EntryShape.Wide => (420 * scale, 236 * scale),
        EntryShape.Square => (250 * scale, 250 * scale),
        _ => (200 * scale, 200 * scale),
    };

    private Control HintBar() => Hints.Bar("Shell", (Act.Accept, "Open"), (Act.Secondary, "Options"), (Act.Settings, "Settings"), (Act.Home, "Home"));

    protected override void OnFocusMoved(FocusItem? old, FocusItem? now)
    {
        UpdateHero(now);
        ShowCarPage(now, snap: false);
    }

    private void UpdateHero(FocusItem? now)
    {
        if (now is null || !_entries.TryGetValue(now, out var e)) return;
        if (_heroTitle is not null) _heroTitle.Text = T.Def.Layout == HomeLayout.Hall ? e.Title.ToUpperInvariant() : e.Title;
        if (_heroSubtitle is not null) _heroSubtitle.Text = e.Subtitle ?? "";
        // CarPlay's and Daylight's wallpapers stay put (dark art under Daylight's dark type
        // would be unreadable); focus only lights the card.
        if (T.Def.Layout is HomeLayout.Dock or HomeLayout.Glance) { L.Window.Backdrop.SetArt(null); return; }
        // Saga shows the picture in its own place, on the right.
        if (T.Def.Layout == HomeLayout.Hall) { L.Window.Backdrop.SetArt(null); ShowSagaItem(e); return; }
        var art = e.Tile is null ? e.Backdrop ?? e.Art ?? e.Video?.Art : null;
        // Apple TV-like layouts show the art sharp and full; the rest blur it into a wash.
        var full = T.Def.Layout is HomeLayout.TopTabsHero or HomeLayout.Sidebar;
        L.Window.Backdrop.SetArt(art, blur: !full, strength: full ? 0.95 : 0.6);
    }

    // ------------------------------------------------------------------ Aurora

    private (Control, FocusNode) Aurora()
    {
        var grid = Ui.Grid("*", "Auto,*,Auto");
        var root = new FocusList(horizontal: false);

        // Tabs
        var tabsFocus = root.Add(new FocusList(horizontal: true, rememberIndex: false));
        // The right margin keeps the tabs clear of the clock in the corner.
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(96, 44, 260, 0) };
        foreach (var d in HomeModel.Menu())
        {
            var label = Ui.Text(d.Title, T.Body, T.TextMuted, FontWeight.SemiBold);
            var card = new Card(label.Margin(26, 12), 30) { FocusScale = 1.06, ShowRing = false };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f => { card.Body.Background = f ? T.Text : Brushes.Transparent; label.Foreground = f ? T.Bg0 : T.TextMuted; };
            card.Item.Activate = d.Open;
            _entries[card.Item] = d;
            tabs.Children.Add(card);
            tabsFocus.Add(card.Item);
        }
        grid.Children.Add(tabs.Row(0));

        // Hero + rows share the rest: hero text sits over the backdrop, rows below.
        var body = Ui.Grid("*", "500,*");
        _heroTitle = Ui.Text("", 72, T.Text, FontWeight.Bold);
        _heroSubtitle = Ui.Text("", T.Heading, T.TextMuted);
        _heroHints = HintBar();
        var hero = Ui.V(14, _heroTitle, _heroSubtitle, _heroHints.Margin(0, 18, 0, 0));
        hero.VerticalAlignment = VerticalAlignment.Bottom;
        hero.Margin = new Thickness(96, 0, 96 + Clear(), 30);
        body.Children.Add(hero.Row(0));
        if (Widgets.Strip() is { } widgets) body.Children.Add(widgets.Align(HorizontalAlignment.Right, VerticalAlignment.Bottom).Margin(0, 0, 96, 30).Row(0));

        var rows = root.Add(new FocusList(horizontal: false));
        var rowsPanel = new StackPanel { Spacing = 6 };
        foreach (var section in HomeModel.Sections())
        {
            var (w, h) = Size(section.Shape, section.Shape == EntryShape.Poster ? 0.82 : 0.9);
            var (view, focus) = Row(section, w, h);
            rowsPanel.Children.Add(view);
            rows.Add(focus);
        }
        var scroll = new ScrollViewer { Content = rowsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        body.Children.Add(scroll.Row(1));
        grid.Children.Add(body.Row(1));
        root.Index = 1;
        return (grid, root);
    }

    // ------------------------------------------------------------------ Living Room (smart TV)

    private (Control, FocusNode) LivingRoom()
    {
        var grid = Ui.Grid("*", "*,Auto,Auto");
        var root = new FocusList(horizontal: false);
        var sections = HomeModel.Sections().ToDictionary(s => s.Id);

        _heroTitle = Ui.Text("", 80, T.Text, FontWeight.Bold);
        _heroSubtitle = Ui.Text("", T.Heading, T.TextMuted);
        var greeting = Ui.Text(Greeting(), T.Heading, T.TextMuted, FontWeight.SemiBold);
        var hero = Ui.V(10, greeting, _heroTitle, _heroSubtitle);
        hero.Margin = new Thickness(110, 90, 110 + Clear(), 0);
        grid.Children.Add(hero.Row(0));
        if (Widgets.Strip() is { } widgets) grid.Children.Add(widgets.Align(HorizontalAlignment.Right, VerticalAlignment.Top).Margin(0, 110, 110, 0).Row(0));

        // The preview row above the ribbon shows what the focused ribbon tile holds.
        // Lifted cards may reach past the host; the row inside does the clipping.
        var previewHost = new ContentControl { Height = 342, ClipToBounds = false };
        var previewFocus = root.Add(new FocusList(horizontal: true));
        grid.Children.Add(previewHost.Row(1));

        var ribbonFocus = root.Add(new FocusList(horizontal: true));
        var ribbon = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22, Margin = new Thickness(110, 26, 110, 26) };
        var ribbonEntries = new List<(HomeEntry Entry, string? Section)>();
        if (sections.TryGetValue("jump", out var jump)) ribbonEntries.Add((new HomeEntry("Continue", "Jump back in", null, "clock", "#5E5CE6", () => jump.Entries[0].Open()), "jump"));
        foreach (var d in HomeModel.Menu())
        {
            var sid = d.Title switch { "Videos" => "videos", "Music" => "music", "Apps & Games" => "apps", "Web" => "web", _ => null };
            ribbonEntries.Add((d, sid));
        }
        foreach (var (entry, sid) in ribbonEntries)
        {
            var card = EntryCard(entry, 170, 170, caption: false, radius: T.Radius);
            var label = Ui.Text(entry.Title, T.Caption, T.TextMuted, FontWeight.SemiBold);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            card.Focused += f =>
            {
                label.Foreground = f ? T.Text : T.TextMuted;
                if (!f) return;
                _contentDebounce?.Dispose();
                _contentDebounce = DispatcherTimer.RunOnce(() => FillPreview(previewHost, previewFocus, sid is null ? null : sections.GetValueOrDefault(sid)), TimeSpan.FromMilliseconds(120));
            };
            ribbon.Children.Add(Ui.V(12, card, label));
            ribbonFocus.Add(card.Item);
        }
        var bar = new Border
        {
            Background = T.SurfaceStrong,
            BorderBrush = T.Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new ScrollViewer { Content = ribbon, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        grid.Children.Add(bar.Row(2));
        root.Index = 1;
        FillPreview(previewHost, previewFocus, sections.Values.FirstOrDefault());
        return (grid, root);
    }

    private string? _previewId;

    private void FillPreview(ContentControl host, FocusList focus, HomeSection? section)
    {
        if (section is not null && section.Id == _previewId && host.Content is not null) return;
        _previewId = section?.Id;
        foreach (var item in focus.Items.ToList()) _entries.Remove(item);
        focus.Clear();
        if (section is null) { host.Content = null; RefreshFocus(); return; }
        // Room above for the focus lift: the row clips at its own edges.
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 26, Margin = new Thickness(110, 22, 110, 30) };
        foreach (var e in section.Entries.Take(14))
        {
            var (w, h) = section.Shape == EntryShape.Poster ? (190.0, 285.0) : section.Shape == EntryShape.Square ? (230.0, 230.0) : (400.0, 225.0);
            var card = EntryCard(e, w, h, caption: true, inside: section.Shape != EntryShape.Poster);
            panel.Children.Add(card);
            focus.Add(card.Item);
        }
        host.Content = Ui.V(4, Ui.Header(section.Title, T.Body).Margin(110, 0, 0, 0),
            new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, ClipToBounds = false });
        RefreshFocus();
    }

    /// <summary>The room text beside the corner widgets must leave them, and its long titles
    /// end in an ellipsis rather than running underneath.</summary>
    private double Clear(double w = Widgets.CompactW)
    {
        foreach (var t in new[] { _heroTitle, _heroSubtitle }) if (t is not null) t.TextTrimming = TextTrimming.CharacterEllipsis;
        var width = Widgets.StripWidth(w: w);
        return width > 0 ? width + 40 : 0;
    }

    /// <summary>Right margin that keeps a strip at the top clear of the clock in the corner.</summary>
    private static double ClockGap => L.Settings.Bool(Lounge.Core.Settings.K.ShowClock) ? 230 : 96;

    private static string Greeting()
    {
        var h = DateTime.Now.Hour;
        return h < 5 ? "Good night" : h < 12 ? "Good morning" : h < 18 ? "Good afternoon" : "Good evening";
    }

    // ------------------------------------------------------------------ Kodi Classic

    private (Control, FocusNode) Kodi()
    {
        var grid = Ui.Grid("560,*", "*,Auto");
        var root = new FocusList(horizontal: true);
        var sections = HomeModel.Sections().ToDictionary(s => s.Id);

        var menuFocus = root.Add(new FocusList(horizontal: false, rememberIndex: true));
        var menu = new StackPanel { Spacing = 4, Margin = new Thickness(0, 150, 0, 0) };
        var contentHost = new ContentControl();
        var contentFocus = root.Add(new FocusList(horizontal: false, rememberIndex: false));
        foreach (var d in HomeModel.Menu())
        {
            var sid = d.Title switch { "Videos" => "videos", "Music" => "music", "Apps & Games" => "apps", "Web" => "web", _ => null };
            var label = Ui.Text(T.Header(d.Title), 40, T.TextMuted, FontWeight.SemiBold, letterSpacing: 2);
            var icon = Icons.Make(d.Icon, 40, T.TextMuted);
            var bar = new Border { Width = 6, Background = T.Accent, CornerRadius = new CornerRadius(3), Opacity = 0, Margin = new Thickness(0, 6, 34, 6) };
            var row = Ui.H(26, bar, icon, label);
            var card = new Card(row.Margin(56, 18, 20, 18), 0) { FocusScale = 1.0, ShowRing = false, HorizontalAlignment = HorizontalAlignment.Stretch };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f =>
            {
                card.Body.Background = f ? Ui.HorizontalFade(T.AccentColor, 0.35, 0) : Brushes.Transparent;
                label.Foreground = f ? T.Text : T.TextMuted;
                bar.Opacity = f ? 1 : 0;
                if (!f) return;
                _contentDebounce?.Dispose();
                _contentDebounce = DispatcherTimer.RunOnce(() => FillKodiContent(contentHost, contentFocus, d, sid is null ? null : sections.GetValueOrDefault(sid), sections.GetValueOrDefault("jump")), TimeSpan.FromMilliseconds(140));
            };
            card.Item.Activate = d.Open;
            _entries[card.Item] = d;
            menu.Children.Add(card);
            menuFocus.Add(card.Item);
        }
        var menuPanel = new Border
        {
            Background = Ui.HorizontalFade(T.Bg0Color, 0.92, 0.55),
            BorderBrush = T.Line,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = Ui.V(0, Ui.Text("LOUNGE", T.Heading, T.Accent, FontWeight.Bold, letterSpacing: 8).Margin(62, 70, 0, 0), menu),
        };
        grid.Children.Add(menuPanel.Col(0).Row(0, 2));
        grid.Children.Add(contentHost.Col(1).Row(0));
        grid.Children.Add(HintBar().Margin(60, 0, 0, 40).Col(1).Row(1));
        FillKodiContent(contentHost, contentFocus, HomeModel.Destinations()[0], sections.GetValueOrDefault("videos"), sections.GetValueOrDefault("jump"));
        return (grid, root);
    }

    private string? _kodiShown;

    private void FillKodiContent(ContentControl host, FocusList focus, HomeEntry dest, HomeSection? section, HomeSection? jump)
    {
        if (_kodiShown == dest.Title && host.Content is not null) return;
        _kodiShown = dest.Title;
        foreach (var item in focus.Items.ToList()) _entries.Remove(item);
        focus.Clear();
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(0, Widgets.Any ? 110 : 160, 0, 0) };
        // The widgets head the content, above whatever the destination lines up.
        if (Widgets.Strip() is { } widgets) stack.Children.Add(widgets.Margin(60, 0, 0, 20));
        void AddRow(HomeSection s, double w, double h)
        {
            var (view, f) = Row(s, w, h, captionsBelow: true, headerSize: T.Body, left: 60, spacing: 22);
            stack.Children.Add(view);
            focus.Add(f);
        }
        if (dest.Title == "Videos" && jump is not null) AddRow(jump with { Title = "In progress" }, 380, 214);
        if (section is not null)
        {
            var (w, h) = section.Shape switch { EntryShape.Poster => (210.0, 315.0), EntryShape.Wide => (380.0, 214.0), _ => (230.0, 230.0) };
            AddRow(section with { Title = section.Id == "videos" ? "Recently added" : section.Title }, w, h);
        }
        if (section is null)
        {
            var info = Ui.V(16,
                Icons.Make(dest.Icon, 120, T.TextFaint, 1.2),
                Ui.Text(dest.Title, T.Title, T.Text, T.TitleWeight),
                Ui.Muted(dest.Subtitle ?? "", T.Body),
                Ui.Muted("Press Ⓐ to open", T.Caption));
            info.Margin = new Thickness(60, 220, 0, 0);
            stack.Children.Add(info);
        }
        host.Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        if (!_building) RefreshFocus();
    }

    // ------------------------------------------------------------------ Console One (PS5)

    private (Control, FocusNode) ConsoleOne()
    {
        var grid = Ui.Grid("*", "Auto,Auto,*");
        var root = new FocusList(horizontal: false);
        var sections = HomeModel.Sections().ToDictionary(s => s.Id);

        var iconFocus = root.Add(new FocusList(horizontal: true));
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22, Margin = new Thickness(110, 110, 110, 0) };
        var top = new List<HomeEntry>();
        if (HomeModel.NowPlaying() is { } playing) top.Add(playing);
        top.AddRange(sections.GetValueOrDefault("jump")?.Entries.Take(3) ?? []);
        top.AddRange(HomeModel.Destinations().Take(5));
        top.AddRange(HomeModel.Apps(web: false).Where(a => a.Tile is not null));
        top.AddRange(HomeModel.Apps(web: true).Where(a => a.Tile is not null));
        top.Add(HomeModel.Destinations()[5]);
        top.Add(HomeModel.Destinations()[6]);
        foreach (var e in top)
        {
            var card = EntryCard(e, 170, 170, caption: false, radius: 22);
            card.FocusScale = 1.32;
            card.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
            card.Margin = new Thickness(0, 0, 0, 0);
            card.VerticalAlignment = VerticalAlignment.Top;
            card.HorizontalAlignment = HorizontalAlignment.Left;
            var holder = new Panel { Width = 170, Height = 250, Children = { card } };
            var entry = e;
            card.Focused += f => { holder.Width = f ? 250 : 170; if (f) _lastIconEntry = entry; };
            icons.Children.Add(holder);
            iconFocus.Add(card.Item);
        }
        grid.Children.Add(new ScrollViewer
        {
            Content = icons, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, ClipToBounds = false,
        }.Row(0));

        _heroTitle = Ui.Text("", 76, T.Text, FontWeight.Light);
        _heroSubtitle = Ui.Text("", T.Heading, T.TextMuted, FontWeight.Light);
        var actionFocus = root.Add(new FocusList(horizontal: true));
        var openBtn = Pill("Open", "play", primary: true);
        var optsBtn = Pill("Options", "list", primary: false);
        openBtn.Item.Activate = () => (_lastIconEntry ?? top.First()).Open();
        optsBtn.Item.Activate = () => (_lastIconEntry?.Options ?? (() => L.Window.Toast("No options here.", "info")))();
        actionFocus.Add(openBtn.Item);
        actionFocus.Add(optsBtn.Item);
        var info = Ui.V(14, _heroTitle, _heroSubtitle, Ui.H(18, openBtn, optsBtn).Margin(0, 26, 0, 0));
        info.Margin = new Thickness(110, 40, 110 + Clear(), 0);
        grid.Children.Add(info.Row(1));
        if (Widgets.Strip() is { } widgets) grid.Children.Add(widgets.Align(HorizontalAlignment.Right, VerticalAlignment.Top).Margin(0, 40, 110, 0).Row(1));

        var lower = new StackPanel { Spacing = 0, Margin = new Thickness(0, 40, 0, 0) };
        var lowerFocus = root.Add(new FocusList(horizontal: false));
        foreach (var s in new[] { sections.GetValueOrDefault("videos"), sections.GetValueOrDefault("music") }.Where(s => s is not null))
        {
            var (view, f) = Row(s!, 300, s!.Shape == EntryShape.Poster ? 169 : 300, captionsBelow: false, inside: true, headerSize: T.Body, left: 110, spacing: 22);
            lower.Children.Add(view);
            lowerFocus.Add(f);
        }
        grid.Children.Add(new ScrollViewer { Content = lower, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }.Row(2));
        return (grid, root);
    }

    private HomeEntry? _lastIconEntry;

    private Card Pill(string text, string icon, bool primary)
    {
        var label = Ui.Text(text, T.Body, primary ? T.Bg0 : T.Text, FontWeight.SemiBold);
        var card = new Card(Ui.H(12, Icons.Make(icon, 26, primary ? T.Bg0 : T.Text), label).Margin(34, 14), 40) { FocusScale = 1.08 };
        card.Body.Background = primary ? T.Text : T.Surface;
        return card;
    }

    // ------------------------------------------------------------------ Series (Xbox)

    private (Control, FocusNode) Series()
    {
        var root = new FocusList(horizontal: false);
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 120, 0, 60) };
        var sections = HomeModel.Sections().ToList();

        // Jump back in: one big tile with smaller ones stacked beside it.
        var hero = new List<HomeEntry>();
        hero.AddRange(sections.FirstOrDefault(s => s.Id == "jump")?.Entries ?? []);
        hero.AddRange(HomeModel.Apps(web: false).Where(a => a.Tile is not null));
        if (hero.Count > 0)
        {
            var heroFocus = root.Add(new FocusList(horizontal: true, rememberIndex: true));
            var heroRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(96, 20, 96, 30) };
            // The widgets lead the row as tiles of their own, the height of the small tiles.
            if (Widgets.Strip(Orientation.Vertical, 14, 300, 173) is { } widgets) heroRow.Children.Add(widgets);
            var big = EntryCard(hero[0], 640, 360, caption: false, inside: true);
            heroRow.Children.Add(big);
            heroFocus.Add(big.Item);
            for (int i = 1; i < Math.Min(hero.Count, 9); i += 2)
            {
                var col = heroFocus.Add(new FocusList(horizontal: false, rememberIndex: false));
                var colPanel = new StackPanel { Spacing = 14 };
                foreach (var e in hero.Skip(i).Take(2))
                {
                    var c = EntryCard(e, 300, 173, caption: false, inside: true);
                    colPanel.Children.Add(c);
                    col.Add(c.Item);
                }
                heroRow.Children.Add(colPanel);
            }
            stack.Children.Add(Ui.V(0, Ui.Header("Jump back in", T.Heading).Margin(96, 0, 0, 0),
                new ScrollViewer { Content = heroRow, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, ClipToBounds = false }));
        }

        // Pins: the destinations as a row of wide tiles.
        var dest = new HomeSection("dest", "My stuff", "apps", HomeModel.Menu(), EntryShape.Wide);
        var (dview, dfocus) = Row(dest, 300, 120, captionsBelow: false, inside: true, headerSize: T.Body, spacing: 14);
        stack.Children.Add(dview);
        root.Add(dfocus);

        foreach (var s in sections.Where(s => s.Id != "jump"))
        {
            var (view, f) = Row(s, 220, 220, captionsBelow: false, inside: true, headerSize: T.Body, spacing: 14);
            stack.Children.Add(view);
            root.Add(f);
        }
        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        return (scroll, root);
    }

    // ------------------------------------------------------------------ Screening Room (programme)

    /// <summary>A printed film programme: a masthead, then the destinations as a numbered
    /// index in large serif type, and beside it the focused one's line-up under typewritten
    /// labels. Focus is quiet: the title turns italic and lights up, the reel number turns
    /// amber, and a rule draws in under it.</summary>
    private (Control, FocusNode) Programme()
    {
        var grid = Ui.Grid("780,*", "Auto,*,Auto");
        var root = new FocusList(horizontal: true);
        var sections = HomeModel.Sections().ToDictionary(s => s.Id);

        // Masthead: the house name in type, tonight's date as a title card, a fine rule.
        var now = DateTime.Now;
        var house = Ui.Text("LOUNGE  ·  PICTURE HOUSE", T.Caption, T.Accent, FontWeight.Medium, letterSpacing: 5);
        var tonight = Ui.Display(Ui.Text($"Tonight, {now:dddd d MMMM}", 40, T.TextMuted), italic: true);
        var rule = new Border { Height = 1, Background = T.Line, Margin = new Thickness(0, 22, 0, 0) };
        // The widgets share the masthead's height with the clock, so they stop short of it.
        grid.Children.Add(Ui.V(10, house, tonight, rule).Margin(96, 56, ClockGap + Clear(), 0).Col(0, 2).Row(0));
        if (Widgets.Strip() is { } widgets) grid.Children.Add(widgets.Align(HorizontalAlignment.Right, VerticalAlignment.Top).Margin(0, 56, ClockGap, 0).Col(0, 2).Row(0));

        var indexFocus = root.Add(new FocusList(horizontal: false, rememberIndex: true));
        var index = new StackPanel { Spacing = 2, Margin = new Thickness(96, 34, 0, 0) };
        var featureHost = new ContentControl { ClipToBounds = false };
        var featureFocus = root.Add(new FocusList(horizontal: false, rememberIndex: false));
        var number = 0;
        foreach (var d in HomeModel.Menu())
        {
            number++;
            var sid = d.Title switch { "Videos" => "videos", "Music" => "music", "Apps & Games" => "apps", "Web" => "web", _ => null };
            var reel = Ui.Text(number.ToString("00"), T.Caption, T.TextFaint, FontWeight.Medium, letterSpacing: 2);
            reel.Width = 64;
            reel.VerticalAlignment = VerticalAlignment.Center;
            var title = Ui.Display(Ui.Text(d.Title, 68, T.TextFaint));
            var underline = new Border { Height = 2, Width = 0, Background = T.Accent, HorizontalAlignment = HorizontalAlignment.Left };
            // A spring overshoots past its target; a width can't go below zero.
            var underlineWidth = new Spring(0, v => underline.Width = Math.Max(0, v), 260, 26);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(reel.Col(0));
            row.Children.Add(title.Col(1));
            var card = new Card(Ui.V(0, row, underline.Margin(64, -4, 0, 8)).Margin(0, 0, 40, 0), 0)
            {
                FocusScale = 1.0, ShowRing = false, ShowGlow = false, HorizontalAlignment = HorizontalAlignment.Left,
            };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f =>
            {
                title.Foreground = f ? T.Text : T.TextFaint;
                title.FontStyle = f ? FontStyle.Italic : FontStyle.Normal;
                reel.Foreground = f ? T.Accent : T.TextFaint;
                underlineWidth.To(f ? Math.Max(120, title.Bounds.Width * 0.55) : 0);
                if (!f) return;
                _contentDebounce?.Dispose();
                _contentDebounce = DispatcherTimer.RunOnce(() => FillLineup(featureHost, featureFocus, d, sid is null ? null : sections.GetValueOrDefault(sid), sections.GetValueOrDefault("jump"), ProgrammeStyle), TimeSpan.FromMilliseconds(130));
            };
            card.Item.Activate = d.Open;
            _entries[card.Item] = d;
            index.Children.Add(card);
            indexFocus.Add(card.Item);
        }
        grid.Children.Add(index.Col(0).Row(1));
        grid.Children.Add(featureHost.Col(1).Row(1));
        grid.Children.Add(HintBar().Margin(96, 0, 0, 40).Col(0, 2).Row(2));
        FillLineup(featureHost, featureFocus, HomeModel.Destinations()[0], sections.GetValueOrDefault("videos"), sections.GetValueOrDefault("jump"), ProgrammeStyle);
        return (grid, root);
    }

    private string? _programmeShown;
    /// <summary>True while a layout is being built: the old scope is still in place then, and
    /// refreshing it would fire focus changes on the discarded layout.</summary>
    private bool _building;
    /// <summary>Where focus goes when the remembered entry isn't on the new layout.</summary>
    private FocusItem? _fallbackFocus;

    /// <summary>How a line-up beside a destination list speaks: Screening Room's programme
    /// labels under a fine rule, or Arcade's banners.</summary>
    private sealed record LineupStyle(
        Func<string, Control> Header,
        Func<string, (string Label, double W, double H)> Shelf,
        string Continue,
        Func<HomeEntry, Control> Empty);

    private static readonly LineupStyle ProgrammeStyle = new(
        label => Ui.H(16,
            Ui.Text(label.ToUpperInvariant(), T.Caption, T.Accent, FontWeight.Medium, letterSpacing: 4),
            new Border { Height = 1, Width = 220, Background = T.Line, VerticalAlignment = VerticalAlignment.Center }),
        id => id switch
        {
            "videos" => ("Now showing", 168.0, 252.0),
            "music" => ("On the record", 230.0, 230.0),
            "apps" => ("The arcade", 230.0, 230.0),
            "web" => ("On air", 360.0, 203.0),
            _ => ("", 230.0, 230.0),
        },
        "Continue",
        dest => Ui.V(18,
            Ui.Text("FEATURE", T.Caption, T.Accent, FontWeight.Medium, letterSpacing: 4),
            Ui.Display(Ui.Text(dest.Subtitle ?? dest.Title, 64, T.Text, wrap: TextWrapping.Wrap), italic: true),
            Ui.Muted("Press A to open", T.Body)));

    /// <summary>The line-up beside the index: what this destination is showing.</summary>
    private void FillLineup(ContentControl host, FocusList focus, HomeEntry dest, HomeSection? section, HomeSection? jump, LineupStyle style)
    {
        if (_programmeShown == dest.Title && host.Content is not null) return;
        _programmeShown = dest.Title;
        foreach (var item in focus.Items.ToList()) _entries.Remove(item);
        focus.Clear();
        // The list reaches 40 px left of its column so the first card's ring and glow aren't
        // clipped at the column's edge; the content moves back by the same amount.
        const double bleed = 40;
        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(bleed, 40, 0, 0) };

        void AddRow(HomeSection s, string label, double w, double h)
        {
            var list = new FocusList(horizontal: true);
            // Each row clips at its own left edge as well: the first card starts a little in.
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Margin = new Thickness(10, 16, 96, 20) };
            foreach (var e in s.Entries.Take(12))
            {
                var card = EntryCard(e, w, h);
                panel.Children.Add(Captioned(card, e, w));
                list.Add(card.Item);
            }
            stack.Children.Add(Ui.V(0, style.Header(label),
                new ScrollViewer { Content = panel, Margin = new Thickness(-10, 0, 0, 0), HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, ClipToBounds = false }));
            focus.Add(list);
        }

        if (dest.Title == "Videos" && jump is not null) AddRow(jump, style.Continue, 320, 180);
        if (section is not null)
        {
            var (label, w, h) = style.Shelf(section.Id);
            AddRow(section, label.Length > 0 ? label : section.Title, w, h);
        }
        else
        {
            // Nothing to line up: a title card for the destination instead.
            var card = style.Empty(dest);
            card.MaxWidth = 860;
            card.Margin = new Thickness(0, 120, 96, 0);
            stack.Children.Add(card);
        }
        host.Content = new ScrollViewer
        {
            Content = stack, Margin = new Thickness(-bleed, 0, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        if (!_building) RefreshFocus();
    }

    // ------------------------------------------------------------------ Arcade (cabinet)

    private DispatcherTimer? _blink;
    /// <summary>Arcade: the destination last chosen, so coming back reopens it.</summary>
    private string? _cabinetDest;

    private static readonly LineupStyle ArcadeStyle = new(
        label => Ui.H(14, PixelArrow(18, T.Danger), Ui.Display(Ui.Text(label.ToUpperInvariant(), 22, T.Accent)),
            new Border { Height = 4, Width = 160, Background = T.Line, VerticalAlignment = VerticalAlignment.Center }),
        id => id switch
        {
            "videos" => ("Now playing", 168.0, 252.0),
            "music" => ("Sound test", 230.0, 230.0),
            "apps" => ("Select game", 230.0, 230.0),
            "web" => ("Bonus stage", 360.0, 203.0),
            _ => ("", 230.0, 230.0),
        },
        "Continue?",
        dest => Ui.V(22,
            Ui.Display(Ui.Text("STAGE SELECT", 22, T.Danger)),
            Ui.Display(Ui.Text((dest.Subtitle ?? dest.Title).ToUpperInvariant(), 36, T.Text, wrap: TextWrapping.Wrap)),
            Ui.Muted("Press A to enter", T.Body)));

    /// <summary>An arcade cabinet's select screen: the name as a pixel marquee with a hard
    /// drop shadow, the destinations as an uppercase menu where a blinking pixel arrow marks
    /// the choice, the chosen one's line-up beside it, and a blinking "press start".</summary>
    private (Control, FocusNode) Cabinet()
    {
        var grid = Ui.Grid("640,*", "Auto,*,Auto");
        var root = new FocusList(horizontal: true);
        var sections = HomeModel.Sections().ToDictionary(s => s.Id);

        var marquee = new Grid();
        marquee.Children.Add(Ui.Display(Ui.Text("LOUNGE", 72, T.Danger)).Margin(7, 7, 0, 0));
        marquee.Children.Add(Ui.Display(Ui.Text("LOUNGE", 72, T.Accent)));
        var subtitle = Ui.Display(Ui.Text("1UP  *  SELECT YOUR STAGE", 20, T.TextMuted));
        // Pixel type runs wide: the widgets get more room, and stop short of the clock.
        grid.Children.Add(Ui.V(18, marquee, subtitle).Margin(96, 56, ClockGap + Clear(w: 310), 0).Col(0, 2).Row(0));
        if (Widgets.Strip(w: 310) is { } widgets) grid.Children.Add(widgets.Align(HorizontalAlignment.Right, VerticalAlignment.Top).Margin(0, 56, ClockGap, 0).Col(0, 2).Row(0));

        var menuFocus = root.Add(new FocusList(horizontal: false, rememberIndex: true));
        var menu = new StackPanel { Spacing = 6, Margin = new Thickness(96, 44, 0, 0) };
        var lineupHost = new ContentControl { ClipToBounds = false };
        var lineupFocus = root.Add(new FocusList(horizontal: false, rememberIndex: false));
        Control? activeCursor = null;
        var destinations = HomeModel.Menu();
        var reopen = destinations.FirstOrDefault(x => x.Title == (_cabinetDest ?? "Videos")) ?? destinations[0];
        foreach (var d in destinations)
        {
            var sid = d.Title switch { "Videos" => "videos", "Music" => "music", "Apps & Games" => "apps", "Web" => "web", _ => null };
            var cursor = PixelArrow(30, T.Accent);
            cursor.Opacity = 0;
            var title = Ui.Display(Ui.Text(d.Title.ToUpperInvariant(), 34, T.TextFaint));
            title.VerticalAlignment = VerticalAlignment.Center;
            var card = new Card(Ui.H(22, cursor, title).Margin(0, 10, 30, 10), 0)
            {
                FocusScale = 1.0, ShowRing = false, ShowGlow = false, HorizontalAlignment = HorizontalAlignment.Left,
            };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f =>
            {
                title.Foreground = f ? T.Accent : T.TextFaint;
                cursor.Opacity = f ? 1 : 0;
                // The blink follows focus only: an entry focus has left keeps no arrow.
                if (!f) { if (activeCursor == cursor) activeCursor = null; return; }
                activeCursor = cursor;
                _cabinetDest = d.Title;
                _contentDebounce?.Dispose();
                _contentDebounce = DispatcherTimer.RunOnce(() => FillLineup(lineupHost, lineupFocus, d, sid is null ? null : sections.GetValueOrDefault(sid), sections.GetValueOrDefault("jump"), ArcadeStyle), TimeSpan.FromMilliseconds(130));
            };
            card.Item.Activate = d.Open;
            _entries[card.Item] = d;
            menu.Children.Add(card);
            menuFocus.Add(card.Item);
            if (d == reopen) _fallbackFocus = card.Item;
        }

        var pressStart = Ui.H(14, Ui.Display(Ui.Text("PRESS", 22, T.Text)), Hints.Glyph(PadButton.A, 36), Ui.Display(Ui.Text("TO START", 22, T.Text)));
        grid.Children.Add(menu.Col(0).Row(1));
        grid.Children.Add(lineupHost.Col(1).Row(1));
        grid.Children.Add(Ui.V(22, pressStart, HintBar()).Margin(96, 0, 0, 40).Col(0, 2).Row(2));
        var reopenId = reopen.Title switch { "Videos" => "videos", "Music" => "music", "Apps & Games" => "apps", "Web" => "web", _ => null };
        _programmeShown = null;
        FillLineup(lineupHost, lineupFocus, reopen, reopenId is null ? null : sections.GetValueOrDefault(reopenId), sections.GetValueOrDefault("jump"), ArcadeStyle);

        // Arcade blink: the cursor and "press start" flash on a steady beat.
        if (Motion.Enabled)
        {
            var on = true;
            _blink = new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Background, (_, _) =>
            {
                on = !on;
                pressStart.Opacity = on ? 1 : 0.15;
                if (activeCursor is not null) activeCursor.Opacity = on ? 1 : 0.35;
            });
        }
        return (grid, root);
    }

    /// <summary>A right-pointing arrow drawn in whole pixels, the cursor of every arcade menu.
    /// The path is stretched to size itself, not scaled inside a Viewbox: a Viewbox'd shape
    /// repainted only its tiny unscaled area when its opacity changed, so blinking cursors
    /// left stale arrows behind on entries focus had already left.</summary>
    private static Control PixelArrow(double height, IBrush brush) => new Avalonia.Controls.Shapes.Path
    {
        Data = Geometry.Parse("M0,0 H2 V1 H3 V2 H4 V3 H5 V5 H4 V6 H3 V7 H2 V8 H0 Z"), Fill = brush,
        Width = height * 5 / 8, Height = height, Stretch = Stretch.Fill, VerticalAlignment = VerticalAlignment.Center,
    };

    // ------------------------------------------------------------------ CarPlay (dock)

    private const string RailKey = "rail:";
    private readonly Dictionary<FocusItem, int> _carPage = new();
    private TranslateTransform? _pageShift;
    private Spring? _pageSpring;
    private StackPanel? _dots;
    private double _pageWidth;
    private int _page = -1;
    private FocusItem? _carFirst;
    /// <summary>Refreshes the Dashboard's Now Playing while Home is up.</summary>
    private DispatcherTimer? _carTimer;

    /// <summary>CarPlay: the status sidebar on the left (focusable here: its recent apps,
    /// and the Home button that switches between the app pages and the Dashboard), and
    /// beside it either the app icons in pages of two rows, or the Dashboard.</summary>
    private (Control, FocusNode) Dock()
    {
        var grid = Ui.Grid($"{CarPlay.RailWidth},*", "*");
        var root = new FocusList(horizontal: true);
        var railFocus = root.Add(new FocusList(horizontal: false, rememberIndex: true));
        var railEntries = new Dictionary<FocusItem, HomeEntry>();
        Card? homeButton = null;
        grid.Children.Add(CarPlay.Rail(onHome: true, railFocus, ToggleDashboard, railEntries, c => homeButton = c).Col(0));
        // Sidebar entries restore focus by their own key: "Music" is in the sidebar and on the pages.
        foreach (var (item, e) in railEntries) _entries[item] = e with { Title = RailKey + e.Title };
        _entries[homeButton!.Item] = new HomeEntry(RailKey + "home", null, null, "home", "#3A3A3C", ToggleDashboard);

        _carFirst = null;
        var content = CarPlay.Dashboard ? DockDashboard(root) : DockPages(root);
        grid.Children.Add(content.Col(1));
        root.Index = 1;
        return (grid, root);
    }

    private void ToggleDashboard()
    {
        CarPlay.Dashboard = !CarPlay.Dashboard;
        L.Play(Lounge.Core.Audio.UiSound.Open);
        Rebuild();
    }

    /// <summary>The app pages: icons with their names under them, five across and two
    /// down, page dots beneath. Moving past the last column slides to the next page.</summary>
    private Control DockPages(FocusList root)
    {
        var apps = CarPlay.Apps();
        _pageWidth = Math.Max(800, L.Window.DesignWidth - CarPlay.RailWidth);
        var height = L.Window.DesignHeight - 110;
        var cols = _pageWidth >= 1500 ? 5 : 4;
        const int rows = 2;
        const double icon = 188;
        var perPage = cols * rows;
        var pageCount = Math.Max(1, (apps.Count + perPage - 1) / perPage);
        var focus = root.Add(new FocusSpatial());
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        _pageShift = new TranslateTransform();
        strip.RenderTransform = _pageShift;
        _pageSpring = new Spring(0, v => { if (_pageShift is not null) _pageShift.X = v; }, 300, 30);
        _page = -1;
        for (int p = 0; p < pageCount; p++)
        {
            var page = new Grid { Width = _pageWidth, Height = height, RowDefinitions = new RowDefinitions("*,*") };
            for (int c = 0; c < cols; c++) page.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (int i = 0; i < perPage && p * perPage + i < apps.Count; i++)
            {
                var e = apps[p * perPage + i];
                var card = CarPlay.IconCard(CarPlay.Icon(e, icon), icon);
                card.Item.Activate = e.Open;
                card.Item.Secondary = e.Options;
                _entries[card.Item] = e;
                _carPage[card.Item] = p;
                _carFirst ??= card.Item;
                var label = Ui.Text(e.Title, 26, T.Text, FontWeight.Medium);
                label.TextAlignment = TextAlignment.Center;
                label.Width = _pageWidth / cols - 24;
                var cell = Ui.V(10, card.Align(HorizontalAlignment.Center, VerticalAlignment.Top), label);
                var row = i / cols;
                cell.HorizontalAlignment = HorizontalAlignment.Center;
                cell.VerticalAlignment = row == 0 ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                cell.Margin = new Thickness(0, row == 0 ? 0 : 26, 0, row == 0 ? 26 : 0);
                page.Children.Add(cell.Col(i % cols).Row(row));
                focus.Add(card.Item);
            }
            strip.Children.Add(page);
        }
        // A canvas lets the strip run past the edge; only the current page shows.
        var window = new Canvas { ClipToBounds = true, Children = { strip } };
        _dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int p = 0; p < pageCount; p++) _dots.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7) });
        _dots.IsVisible = pageCount > 1;
        var layout = Ui.Grid("*", "*,110");
        layout.Children.Add(window.Row(0));
        layout.Children.Add(_dots.Row(1));
        return layout;
    }

    /// <summary>Slides to the page holding the focused icon and lights its dot.</summary>
    private void ShowCarPage(FocusItem? now, bool snap)
    {
        if (now is null || !_carPage.TryGetValue(now, out var page) || page == _page) return;
        _page = page;
        var x = -page * _pageWidth;
        if (snap) _pageSpring?.Snap(x); else _pageSpring?.To(x);
        if (_dots is null) return;
        for (int i = 0; i < _dots.Children.Count; i++)
            ((Border)_dots.Children[i]).Background = i == page ? Brushes.White : new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
    }

    /// <summary>The Dashboard: a large card for what to watch, and beside it Now Playing
    /// and today's card, all one press from the app pages.</summary>
    private Control DockDashboard(FocusList root)
    {
        var focus = root.Add(new FocusSpatial());
        const double pad = 26, gap = 22;
        var w = Math.Max(800, L.Window.DesignWidth - CarPlay.RailWidth) - pad * 2;
        var h = L.Window.DesignHeight - pad * 2;
        var bigW = Math.Round((w - gap) * 0.6);
        var smallW = w - gap - bigW;
        var smallH = (h - gap) / 2;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{bigW},{gap},{smallW}"),
            RowDefinitions = new RowDefinitions($"{smallH},{gap},{smallH}"),
            Margin = new Thickness(pad),
        };

        // What to watch: the newest thing to resume, else the newest video, else Videos.
        var sections = HomeModel.Sections().ToDictionary(s => s.Id);
        var (label, watch) = sections.GetValueOrDefault("jump")?.Entries.FirstOrDefault() is { } j ? ("JUMP BACK IN", j)
            : sections.GetValueOrDefault("videos")?.Entries.FirstOrDefault(e => e.Video is not null || e.Art is not null) is { } v ? ("RECENTLY ADDED", v)
            : ("VIDEOS", HomeModel.Destinations()[0]);
        watch = CarPlay.Noted(watch);
        var big = new Grid();
        big.Children.Add(HomeModel.Art(watch, bigW, h));
        big.Children.Add(new Border { Background = Ui.VerticalFade(Colors.Black, 0, 0.85, 0.35, 1), IsHitTestVisible = false });
        var caption = Ui.V(6,
            Ui.Text(label, 22, T.TextMuted, FontWeight.SemiBold, letterSpacing: 1),
            Ui.Text(watch.Title, 52, Brushes.White, FontWeight.Bold),
            Ui.Text(watch.Subtitle ?? "", T.Body, new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))));
        if (watch.Progress >= 0)
            caption.Children.Add(new Border
            {
                Height = 8, Width = 420, CornerRadius = new CornerRadius(4), Background = T.WithAlpha(Colors.White, 0.25), HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 12, 0, 0),
                Child = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(8, 420 * Math.Clamp(watch.Progress, 0, 1)) },
            });
        caption.VerticalAlignment = VerticalAlignment.Bottom;
        caption.Margin = new Thickness(40, 0, 40, 38);
        big.Children.Add(caption);
        var bigCard = new Card(big, 30) { Width = bigW, Height = h, FocusScale = 1.0, ShowGlow = false };
        bigCard.Item.Activate = watch.Open;
        bigCard.Item.Secondary = watch.Options;
        _entries[bigCard.Item] = watch;
        focus.Add(bigCard.Item);
        _carFirst = bigCard.Item;
        grid.Children.Add(bigCard.Col(0).Row(0, 3));

        grid.Children.Add(NowPlayingWidget(focus, smallW, smallH).Col(2).Row(0));
        // Below it the weather and the month, side by side when both are on; with neither,
        // today's card.
        var glass = new SolidColorBrush(Color.FromArgb(235, 28, 28, 30));
        if (Widgets.WeatherOn && Widgets.CalendarOn)
        {
            var half = Math.Floor((smallW - gap) / 2);
            grid.Children.Add(Ui.H(gap, Widgets.Weather(half, smallH, glass), Widgets.Calendar(smallW - gap - half, smallH, glass)).Col(2).Row(2));
        }
        else if (Widgets.WeatherOn) grid.Children.Add(Widgets.Weather(smallW, smallH, glass).Col(2).Row(2));
        else if (Widgets.CalendarOn) grid.Children.Add(Widgets.Calendar(smallW, smallH, glass).Col(2).Row(2));
        else grid.Children.Add(TodayWidget(focus, smallW, smallH, sections).Col(2).Row(2));
        return grid;
    }

    private static Border Widget(double w, double h) => new()
    {
        Width = w, Height = h, CornerRadius = new CornerRadius(30), ClipToBounds = true,
        Background = new SolidColorBrush(Color.FromArgb(235, 28, 28, 30)),
    };

    /// <summary>Now Playing: the cover, the song and the buttons; while nothing plays, the
    /// newest album with a button to open it.</summary>
    private Control NowPlayingWidget(FocusSpatial focus, double w, double h)
    {
        var box = Widget(w, h);
        var artSize = Math.Min(h - 80, 200);
        var art = new Border { Width = artSize, Height = artSize, CornerRadius = new CornerRadius(14), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top };
        var title = Ui.Text("", 32, T.Text, FontWeight.Bold);
        var artist = Ui.Text("", 24, T.TextMuted);
        var head = Ui.Text("NOW PLAYING", 20, T.Accent, FontWeight.SemiBold, letterSpacing: 1);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new Thickness(-14, 8, 0, 0) };
        var info = Ui.V(6, head, title, artist, buttons);
        info.VerticalAlignment = VerticalAlignment.Center;
        var layout = Ui.Grid("Auto,*", "*");
        layout.Children.Add(art.Col(0).Align(HorizontalAlignment.Left, VerticalAlignment.Center));
        layout.Children.Add(info.Margin(30, 0, 10, 0).Col(1));
        box.Child = layout.Margin(30, 26);

        Card Button(string glyph, double size, Action run)
        {
            var card = CarPlay.IconCard(Icons.Make(glyph, size * 0.5, T.Text), size);
            card.Item.Activate = run;
            buttons.Children.Add(card);
            focus.Add(card.Item);
            _entries[card.Item] = new HomeEntry(RailKey + "music-" + glyph, null, null, glyph, "#FF375F", run);
            return card;
        }

        string? shown = "\0";
        if (CarPlay.MusicOn)
        {
            Button("previous", 72, () => Playback.PlayPrevious());
            var play = Button("pause", 84, () => L.Player.TogglePause());
            Button("next", 72, () => Playback.PlayNext());
            void Refresh()
            {
                var path = L.Player.Path;
                if (path != shown)
                {
                    shown = path;
                    var item = path is null ? null : L.Library.Get(path);
                    title.Text = item?.Title ?? (path is null ? "Nothing playing" : Path.GetFileNameWithoutExtension(path));
                    artist.Text = item?.Artist ?? "";
                    art.Child = ImageCache.Art(item?.Art, artSize, artSize, Ui.Monogram(title.Text ?? "?", "#FF375F", artSize));
                }
                play.Content = Icons.Make(L.Player.IsPlaying ? "pause" : "play", 42, T.Text).Align(HorizontalAlignment.Center, VerticalAlignment.Center);
            }
            Refresh();
            _carTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Refresh());
            _carTimer.Stop();
        }
        else
        {
            head.Text = "MUSIC";
            head.Foreground = new SolidColorBrush(Color.Parse("#FF375F"));
            var album = L.Library.Albums().FirstOrDefault();
            title.Text = album?.Name ?? "Music";
            artist.Text = album?.Artist ?? "Add a music folder to play here";
            art.Child = ImageCache.Art(album?.Art, artSize, artSize, CarPlay.Icon(HomeModel.Destinations()[1], artSize));
            Button("play", 84, () =>
            {
                CarPlay.Note("Music");
                if (album is not null) L.Window.Push(new AlbumScreen(album)); else HomeModel.Destinations()[1].Open();
            });
        }
        return box;
    }

    /// <summary>Today: the weekday in red over the date, like a calendar, beside what was
    /// added to the library most recently.</summary>
    private Control TodayWidget(FocusSpatial focus, double w, double h, Dictionary<string, HomeSection> sections)
    {
        var now = DateTime.Now;
        var date = Ui.V(0,
            Ui.Text(now.ToString("dddd").ToUpperInvariant(), 22, new SolidColorBrush(Color.Parse("#FF453A")), FontWeight.SemiBold, letterSpacing: 1),
            Ui.Text(now.Day.ToString(), 104, T.Text, FontWeight.Light));
        var newest = sections.GetValueOrDefault("videos")?.Entries.FirstOrDefault(e => e.Tile is null && e.Video is not null || e.Art is not null);
        var album = L.Library.Albums().FirstOrDefault();
        var lines = Ui.V(8, Ui.Text("RECENTLY ADDED", 20, T.TextMuted, FontWeight.SemiBold, letterSpacing: 1));
        void Line(string icon, string text, string? sub)
        {
            var row = Ui.H(14, Icons.Make(icon, 28, T.Accent), Ui.V(0, Ui.Text(text, 28, T.Text, FontWeight.SemiBold), Ui.Text(sub ?? "", 22, T.TextMuted)));
            lines.Children.Add(row.Margin(0, 6, 0, 0));
        }
        if (newest is not null) Line("video", newest.Title, newest.Subtitle);
        if (album is not null) Line("album", album.Name, album.Artist);
        if (newest is null && album is null) Line("library-add", "Nothing yet", "Add folders in Files");
        var layout = Ui.Grid("Auto,*", "*");
        layout.Children.Add(date.Col(0).Align(HorizontalAlignment.Left, VerticalAlignment.Center));
        layout.Children.Add(new Border { Width = 1, Background = T.Line, Margin = new Thickness(34, 10, 0, 10), HorizontalAlignment = HorizontalAlignment.Left }.Col(1));
        layout.Children.Add(lines.Margin(64, 0, 10, 0).Col(1).Align(HorizontalAlignment.Stretch, VerticalAlignment.Center));
        var box = Widget(w, h);
        box.Child = layout.Margin(36, 24);
        var open = newest is not null ? CarPlay.Noted(newest) : CarPlay.Noted(HomeModel.Destinations()[0]);
        var card = new Card(box, 30) { Width = w, Height = h, FocusScale = 1.0, ShowGlow = false };
        card.Body.Background = Brushes.Transparent;
        card.Item.Activate = open.Open;
        _entries[card.Item] = open with { Title = RailKey + "today" };
        focus.Add(card.Item);
        return card;
    }

    // ------------------------------------------------------------------ Daylight (glance)

    private StackPanel? _upNextDots;

    /// <summary>A light TV launcher: the destinations as pills in a frosted capsule at the
    /// top, the focused item's title large under them with today's date beside it, an Up
    /// Next panel of things to resume beside the weather, and the app tiles along the bottom.</summary>
    private (Control, FocusNode) Glance()
    {
        var grid = Ui.Grid("*", "Auto,Auto,Auto,*");
        var root = new FocusList(horizontal: false);
        var sections = HomeModel.Sections().ToDictionary(s => s.Id);
        const double side = 96;
        var width = Math.Max(1200, L.Window.DesignWidth) - side * 2;

        // Tabs: "Home" lit as where you are, then a pill for each destination.
        var tabsFocus = root.Add(new FocusList(horizontal: true, rememberIndex: false));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        tabs.Children.Add(new Border
        {
            Background = T.SurfaceStrong, CornerRadius = new CornerRadius(24),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 2, Blur = 8, Color = Color.FromArgb(30, 0, 0, 0) }),
            Child = Ui.Text("Home", T.Caption, T.Text, FontWeight.SemiBold).Margin(24, 9),
        });
        foreach (var d in HomeModel.Menu())
        {
            var label = Ui.Text(d.Title, T.Caption, T.TextMuted, FontWeight.SemiBold);
            var card = new Card(label.Margin(22, 9), 24) { FocusScale = 1.04, ShowRing = false, ShowGlow = false };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f => { card.Body.Background = f ? T.Text : Brushes.Transparent; label.Foreground = f ? T.SurfaceStrong : T.TextMuted; };
            card.Item.Activate = d.Open;
            _entries[card.Item] = d;
            tabs.Children.Add(card);
            tabsFocus.Add(card.Item);
        }
        var capsule = new Border
        {
            Background = T.Surface, CornerRadius = new CornerRadius(30), Padding = new Thickness(6),
            BorderBrush = T.Line, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0),
            Child = tabs,
        };
        grid.Children.Add(capsule.Row(0));

        // The focused item's title, and today beside it.
        var calendar = Widgets.CalendarOn ? Widgets.Calendar(Widgets.CompactW, 130) : null;
        _heroTitle = Ui.Text("", 68, T.Text, T.TitleWeight);
        _heroSubtitle = Ui.Text("", T.Body, T.TextMuted);
        foreach (var t in new[] { _heroTitle, _heroSubtitle }) { t.TextTrimming = TextTrimming.CharacterEllipsis; t.MaxWidth = width - (calendar is null ? 0 : Widgets.CompactW + 40); }
        var heroRow = new Grid { Margin = new Thickness(side, 50, side, 0) };
        heroRow.Children.Add(Ui.V(8, _heroTitle, _heroSubtitle).Align(HorizontalAlignment.Left, VerticalAlignment.Bottom));
        if (calendar is not null) heroRow.Children.Add(calendar.Align(HorizontalAlignment.Right, VerticalAlignment.Bottom));
        grid.Children.Add(heroRow.Row(1));

        // Up Next beside the weather.
        const double panelH = 410, gap = 24;
        var weatherW = Widgets.WeatherOn ? Math.Round(width * 0.32) : 0;
        var upNextW = width - (weatherW > 0 ? weatherW + gap : 0);
        var middle = new Grid { Margin = new Thickness(side, 34, side, 0), Height = panelH };
        middle.ColumnDefinitions = new ColumnDefinitions(weatherW > 0 ? $"{upNextW},{gap},{weatherW}" : "*");
        middle.Children.Add(UpNextPanel(root, sections, upNextW, panelH).Col(0));
        if (weatherW > 0) middle.Children.Add(Widgets.Weather(weatherW, panelH).Col(2));
        grid.Children.Add(middle.Row(2));

        // The apps along the bottom.
        var appsFocus = root.Add(new FocusList(horizontal: true));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22, Margin = new Thickness(side, 40, side, 30) };
        foreach (var e in HomeModel.Apps(web: false).Concat(HomeModel.Apps(web: true)).Where(a => a.Tile is not null))
        {
            var card = EntryCard(e, 272, 153, caption: false);
            row.Children.Add(card);
            appsFocus.Add(card.Item);
        }
        grid.Children.Add(new ScrollViewer
        {
            Content = row, ClipToBounds = false, VerticalAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.Row(3));
        root.Index = 1;
        return (grid, root);
    }

    /// <summary>Up Next: what to resume, else what's new, three cards to a page with dots
    /// under them that follow focus.</summary>
    private Control UpNextPanel(FocusList root, Dictionary<string, HomeSection> sections, double w, double h)
    {
        var source = new[] { "jump", "videos", "music", "apps" }
            .Select(sections.GetValueOrDefault)
            .FirstOrDefault(s => s is not null && s.Entries.Any(e => e.Tile is not null || e.Video is not null || e.Art is not null || e.Progress >= 0))
            ?? sections.Values.FirstOrDefault();
        var entries = source?.Entries.Take(12).ToList() ?? HomeModel.Destinations().Take(3).ToList();
        const double pad = 28, lift = 14, spacing = 20;
        var cardW = Math.Floor((w - pad * 2 - spacing * 2) / 3);
        // 16:9, or flatter when the captions under the cards would leave the panel.
        var cardH = Math.Min(Math.Round(cardW * 9 / 16), h - 180);
        var focus = root.Add(new FocusList(horizontal: true));
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, Margin = new Thickness(lift, lift, lift, 0) };
        _upNextDots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        var pages = Math.Max(1, (entries.Count + 2) / 3);
        for (int p = 0; p < pages; p++) _upNextDots.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = T.TextFaint });
        _upNextDots.IsVisible = pages > 1;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            // Ring and lift only: the panel clips, and a clipped shadow shows its edges.
            var card = EntryCard(e, cardW, cardH);
            card.ShowGlow = false;
            var page = i / 3;
            card.Focused += f => { if (f) LightUpNext(page); };
            strip.Children.Add(Captioned(card, e, cardW));
            focus.Add(card.Item);
        }
        LightUpNext(0);
        // The cards scroll inside the panel; the margin leaves room for the focus lift.
        var scroll = new ScrollViewer
        {
            Content = strip, ClipToBounds = true, Margin = new Thickness(-lift, -lift, -lift, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var label = Ui.Text(source?.Id == "jump" ? "Up Next" : source?.Title ?? "Up Next", T.Caption, T.TextMuted, FontWeight.SemiBold);
        var layout = Ui.Grid("*", "Auto,*,Auto");
        layout.Children.Add(label.Margin(0, 0, 0, 14).Row(0));
        layout.Children.Add(scroll.Row(1));
        layout.Children.Add(_upNextDots.Row(2));
        return new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(T.RadiusLarge),
            Background = T.Surface, BorderBrush = T.Line, BorderThickness = new Thickness(1),
            Padding = new Thickness(pad, 22, pad, 18),
            Child = layout,
        };
    }

    private void LightUpNext(int page)
    {
        if (_upNextDots is null) return;
        for (int i = 0; i < _upNextDots.Children.Count; i++)
            ((Border)_upNextDots.Children[i]).Background = i == page ? T.Text : T.TextFaint;
    }

    // ------------------------------------------------------------------ Liquid Glass (sidebar)

    /// <summary>Liquid Glass: the sidebar's clock, while Home is up.</summary>
    private DispatcherTimer? _barClock;

    /// <summary>A floating liquid glass sidebar down the left (who's watching and the time,
    /// Search, Home, the destinations, then the apps with their icons in circles), and the
    /// focused item's art filling the screen behind its title and an Up Next row.</summary>
    private (Control, FocusNode) Sidebar()
    {
        var grid = new Grid();
        var root = new FocusList(horizontal: true);
        var sections = HomeModel.Sections().ToDictionary(s => s.Id);
        const double barX = 40, barW = 440, contentX = barX + barW + 64;

        // The art behind stays under readable type: dark to the left and along the bottom.
        grid.Children.Add(new Border { Background = Ui.HorizontalFade(Colors.Black, 0.75, 0), Width = 1200, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false });
        grid.Children.Add(new Border { Background = Ui.VerticalFade(Colors.Black, 0, 0.9, 0.4, 1), IsHitTestVisible = false });

        // ---- the bar
        var barFocus = root.Add(new FocusList(horizontal: false, rememberIndex: true));
        var list = new StackPanel { Spacing = 2 };
        Card Row(HomeEntry e, Func<bool, Control> icon, bool here = false)
        {
            var label = Ui.Text(e.Title, T.Body, T.Text, FontWeight.Medium);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.VerticalAlignment = VerticalAlignment.Center;
            var iconHost = new ContentControl { Content = icon(false), VerticalAlignment = VerticalAlignment.Center };
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            line.Children.Add(iconHost);
            line.Children.Add(label.Margin(18, 0, 0, 0).Col(1));
            // The list scrolls inside a clip, so the pill lights up without growing past its edges.
            var card = new Card(line.Margin(14, 10), 26) { FocusScale = 1.0, ShowRing = false, ShowGlow = false, HorizontalAlignment = HorizontalAlignment.Stretch };
            // Where you are keeps a faint pill; focus is a white one with dark type.
            var resting = here ? T.WithAlpha(Colors.White, 0.12) : Brushes.Transparent;
            card.Body.Background = resting;
            card.Focused += f =>
            {
                card.Body.Background = f ? Brushes.White : resting;
                label.Foreground = f ? Brushes.Black : T.Text;
                iconHost.Content = icon(f);
            };
            card.Item.Activate = e.Open;
            card.Item.Secondary = e.Options;
            _entries[card.Item] = e;
            list.Children.Add(card);
            barFocus.Add(card.Item);
            return card;
        }
        // A glyph in a small round well, dark on the white pill.
        Func<bool, Control> Glyph(string glyph) => f => new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(22),
            Background = f ? T.WithAlpha(Colors.Black, 0.08) : T.WithAlpha(Colors.White, 0.12),
            Child = Icons.Make(glyph, 24, f ? Brushes.Black : T.Text).Align(HorizontalAlignment.Center, VerticalAlignment.Center),
        };
        // An app's own icon, in a circle.
        Func<bool, Control> Badge(HomeEntry e) => _ => new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(22), ClipToBounds = true,
            Child = HomeModel.Art(e, 44, 44),
        };

        Row(new HomeEntry("Search", "Your films, music and apps", null, "search", "#8E8E93", () => L.Window.Push(new SearchScreen())), Glyph("search"));
        Row(new HomeEntry("Home", Greeting(), null, "home", "#8E8E93", () => L.Play(Lounge.Core.Audio.UiSound.Select)), Glyph("home"), here: true);
        foreach (var d in HomeModel.Menu()) Row(d, d.Art is not null ? Badge(d) : Glyph(d.Icon));
        var apps = HomeModel.Apps(web: false).Concat(HomeModel.Apps(web: true)).Where(a => a.Tile is not null).ToList();
        if (apps.Count > 0)
        {
            list.Children.Add(Ui.Text("Apps & Websites", T.Caption, T.TextFaint, FontWeight.SemiBold).Margin(16, 22, 0, 8));
            foreach (var a in apps) Row(a, Badge(a));
        }

        // Who's watching, and the time.
        var name = Environment.UserName is { Length: > 0 } u ? char.ToUpper(u[0]) + u[1..] : "You";
        var avatar = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(24), ClipToBounds = true, Child = Ui.Monogram(name, "#5E5CE6", 48) };
        var who = Ui.Text(name, T.Body, T.Text, FontWeight.SemiBold);
        who.TextTrimming = TextTrimming.CharacterEllipsis;
        who.VerticalAlignment = VerticalAlignment.Center;
        var time = Ui.Text("", T.Body, T.TextMuted, FontWeight.Medium);
        time.VerticalAlignment = VerticalAlignment.Center;
        void Tick()
        {
            time.IsVisible = L.Settings.Bool(Lounge.Core.Settings.K.ShowClock);
            time.Text = DateTime.Now.ToString(L.Settings.Bool(Lounge.Core.Settings.K.Clock24h) ? "HH:mm" : "h:mm tt");
        }
        Tick();
        _barClock = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Tick());
        _barClock.Stop();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(16, 4, 16, 18) };
        header.Children.Add(avatar);
        header.Children.Add(who.Margin(16, 0, 12, 0).Col(1));
        header.Children.Add(time.Col(2));

        var scroll = new ScrollViewer { Content = list.Margin(4, 2, 4, 10), VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var inside = Ui.Grid("*", "Auto,*");
        inside.Children.Add(header.Row(0));
        inside.Children.Add(scroll.Row(1));
        var glass = new Grid();
        glass.Children.Add(new Border { Background = Ui.Sheen(), CornerRadius = new CornerRadius(40), IsHitTestVisible = false });
        glass.Children.Add(inside.Margin(14, 22, 14, 14));
        var bar = new Border
        {
            Width = barW, Margin = new Thickness(barX, 40, 0, 40), HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(40), ClipToBounds = true,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(120, 48, 48, 54), 0), new GradientStop(Color.FromArgb(165, 18, 18, 22), 1) },
            },
            BorderBrush = Ui.Rim(), BorderThickness = new Thickness(1.5),
            Child = glass,
        };
        grid.Children.Add(new Border
        {
            // The shadow sits on its own layer: on the clipping bar it would be cut off.
            Width = barW, Margin = bar.Margin, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(40),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 20, Blur = 60, Color = Color.FromArgb(140, 0, 0, 0) }),
            Background = T.WithAlpha(Colors.Black, 0.01), IsHitTestVisible = false,
        });
        grid.Children.Add(bar);

        // ---- the row, and the focused item's title above it
        var source = new[] { "jump", "videos", "music", "apps" }
            .Select(sections.GetValueOrDefault)
            .FirstOrDefault(s => s is not null && s.Entries.Any(e => e.Tile is not null || e.Video is not null || e.Art is not null || e.Progress >= 0))
            ?? sections.Values.FirstOrDefault();
        var rowFocus = root.Add(new FocusList(horizontal: true));
        var cards = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 26, Margin = new Thickness(contentX, 18, 96, 56) };
        foreach (var e in (source?.Entries ?? HomeModel.Destinations()).Take(16))
        {
            var card = EntryCard(e, 330, 186);
            cards.Children.Add(Captioned(card, e, 330));
            rowFocus.Add(card.Item);
            _fallbackFocus ??= card.Item;
        }
        var rowLabel = Ui.Text(source?.Id == "jump" ? "Up Next" : source?.Title ?? "Up Next", T.Body, T.Text, FontWeight.SemiBold).Margin(contentX, 0, 0, 0);
        var row = Ui.V(0, rowLabel, new ScrollViewer
        {
            Content = cards, ClipToBounds = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        row.VerticalAlignment = VerticalAlignment.Bottom;
        grid.Children.Add(row);

        _heroTitle = Ui.Text("", 76, T.Text, T.TitleWeight);
        _heroSubtitle = Ui.Text("", T.Heading, T.TextMuted);
        foreach (var t in new[] { _heroTitle, _heroSubtitle }) t.TextTrimming = TextTrimming.CharacterEllipsis;
        var hero = Ui.V(10, _heroTitle, _heroSubtitle);
        hero.VerticalAlignment = VerticalAlignment.Bottom;
        hero.Margin = new Thickness(contentX, 0, 96, 400);
        grid.Children.Add(hero);

        if (Widgets.Strip() is { } widgets) grid.Children.Add(widgets.Align(HorizontalAlignment.Right, VerticalAlignment.Top).Margin(0, 40, 56, 0));
        root.Index = 1;
        return (grid, root);
    }

    // ------------------------------------------------------------------ Saga (hall)

    private ContentControl? _sagaArt;
    private Border? _sagaBar;
    private string? _sagaList;
    /// <summary>Saga: the destination last chosen, so coming back reopens it.</summary>
    private string? _sagaDest;

    /// <summary>A northern hall's inventory: a rune and a knotted rule over the name, the
    /// destinations as light uppercase words each with its rune, the chosen one's contents
    /// as a plain list, and on the right the focused thing's picture fading into the dark
    /// above a smoked-glass card with its name. Focus never lifts: the choice brightens, a
    /// band of light falls behind it and a marker appears beside it.</summary>
    private (Control, FocusNode) Hall()
    {
        var grid = Ui.Grid("560,560,*", "Auto,*,Auto");
        var root = new FocusList(horizontal: true);
        var sections = HomeModel.Sections().ToDictionary(s => s.Id);
        var silver = new SolidColorBrush(Nordic.Silver);

        var title = Ui.H(18, Nordic.Rune("ingwaz", 34, silver), Ui.Text("LOUNGE", 36, T.Text, FontWeight.Light, letterSpacing: 14).Align(HorizontalAlignment.Left, VerticalAlignment.Center));
        var masthead = Ui.V(14, title, Nordic.Divider().Also(x => x.Width = 420).Align(HorizontalAlignment.Left, VerticalAlignment.Top));
        grid.Children.Add(masthead.Margin(96, 56, 0, 0).Col(0, 2).Row(0));

        // The destinations.
        var menuFocus = root.Add(new FocusList(horizontal: false, rememberIndex: true));
        var menu = new StackPanel { Spacing = 4, Margin = new Thickness(96, 40, 30, 0) };
        var listHost = new ContentControl();
        var listFocus = root.Add(new FocusList(horizontal: false, rememberIndex: false));
        var lines = new List<(string Title, TextBlock Label, ContentControl Rune, Control Marker)>();
        void Paint(string chosen, bool focused)
        {
            foreach (var (t, label, rune, marker) in lines)
            {
                var on = t == chosen;
                label.Foreground = on ? (focused ? T.Text : T.TextMuted) : T.TextFaint;
                rune.Content = Nordic.Rune(Nordic.RuneFor(t), 28, on ? (focused ? T.Text : T.TextMuted) : T.TextFaint);
                marker.Opacity = on ? (focused ? 1 : 0.45) : 0;
            }
        }
        var destinations = HomeModel.Menu();
        var reopen = destinations.FirstOrDefault(x => x.Title == (_sagaDest ?? "Videos")) ?? destinations[0];
        foreach (var d in destinations)
        {
            var sid = d.Title switch { "Videos" => "videos", "Music" => "music", "Apps & Games" => "apps", "Web" => "web", _ => null };
            var marker = Nordic.Marker(26);
            var rune = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
            var label = Ui.Text(d.Title.ToUpperInvariant(), 38, T.TextFaint, FontWeight.Light, letterSpacing: 4);
            label.VerticalAlignment = VerticalAlignment.Center;
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
            line.Children.Add(marker.Margin(0, 0, 14, 0));
            line.Children.Add(rune.Margin(0, 0, 18, 0).Col(1));
            line.Children.Add(label.Col(2));
            lines.Add((d.Title, label, rune, marker));
            var card = new Card(line.Margin(0, 6), 0) { FocusScale = 1.0, ShowRing = false, ShowGlow = false, HorizontalAlignment = HorizontalAlignment.Stretch };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f =>
            {
                if (!f) { Paint(_sagaDest ?? d.Title, focused: false); return; }
                _sagaDest = d.Title;
                Paint(d.Title, focused: true);
                _contentDebounce?.Dispose();
                _contentDebounce = DispatcherTimer.RunOnce(() => FillSaga(listHost, listFocus, d, sid is null ? null : sections.GetValueOrDefault(sid), sections.GetValueOrDefault("jump")), TimeSpan.FromMilliseconds(130));
            };
            card.Item.Activate = d.Open;
            _entries[card.Item] = d;
            menu.Children.Add(card);
            menuFocus.Add(card.Item);
            if (d == reopen) _fallbackFocus = card.Item;
        }
        Paint(reopen.Title, focused: false);
        grid.Children.Add(menu.Col(0).Row(1));
        grid.Children.Add(Nordic.Upright().Align(HorizontalAlignment.Left, VerticalAlignment.Stretch).Margin(0, 20, 0, 20).Col(1).Row(1));
        grid.Children.Add(listHost.Margin(30, 30, 40, 0).Col(1).Row(1));

        // The focused thing: weather and the date above, its picture fading into the dark,
        // and its card.
        var right = Ui.Grid("*", "Auto,*,Auto");
        // Level with the masthead, stopping short of the clock in the corner.
        if (Widgets.Strip() is { } widgets) right.Children.Add(widgets.Align(HorizontalAlignment.Right, VerticalAlignment.Top).Margin(0, 56, ClockGap - 96, 0).Row(0));
        _sagaArt = new ContentControl { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_sagaArt.Row(1));
        _heroTitle = Ui.Text("", 44, T.Text, FontWeight.Light, letterSpacing: 3);
        _heroSubtitle = Ui.Text("", T.Body, T.TextMuted);
        foreach (var t in new[] { _heroTitle, _heroSubtitle }) { t.TextTrimming = TextTrimming.CharacterEllipsis; t.HorizontalAlignment = HorizontalAlignment.Center; }
        // Progress, as a bar drawn like a stamina meter: a fine frame with a fill.
        _sagaBar = new Border { Height = 6, Background = new SolidColorBrush(Nordic.Silver), HorizontalAlignment = HorizontalAlignment.Left };
        var meter = new Border
        {
            Width = 360, Height = 12, Padding = new Thickness(2), BorderBrush = silver, BorderThickness = new Thickness(1),
            Background = T.WithAlpha(Colors.Black, 0.5), HorizontalAlignment = HorizontalAlignment.Center, Child = _sagaBar,
        };
        var card2 = Nordic.Frame(Ui.V(10, _heroTitle, _heroSubtitle, meter.Margin(0, 8, 0, 0)));
        right.Children.Add(card2.Margin(0, 16, 0, 0).Row(2));
        grid.Children.Add(right.Margin(20, 0, 96, 24).Col(2).Row(0, 2));

        // The hints, on a dark band under a knotted rule.
        var band = Ui.V(18, Nordic.Divider().Margin(96, 0), HintBar().Margin(96, 0, 0, 0));
        band.Margin = new Thickness(0, 0, 0, 36);
        grid.Children.Add(new Border { Background = Ui.VerticalFade(Colors.Black, 0, 0.7), IsHitTestVisible = false }.Col(0, 3).Row(2));
        grid.Children.Add(band.Col(0, 3).Row(2));

        _sagaList = null;
        FillSaga(listHost, listFocus, reopen, reopen.Title switch { "Videos" => sections.GetValueOrDefault("videos"), "Music" => sections.GetValueOrDefault("music"), "Apps & Games" => sections.GetValueOrDefault("apps"), "Web" => sections.GetValueOrDefault("web"), _ => null }, sections.GetValueOrDefault("jump"));
        return (grid, root);
    }

    /// <summary>The chosen destination's contents as an inventory list: names on the left,
    /// what they hold or how much is left on the right.</summary>
    private void FillSaga(ContentControl host, FocusList focus, HomeEntry dest, HomeSection? section, HomeSection? jump)
    {
        if (_sagaList == dest.Title && host.Content is not null) return;
        _sagaList = dest.Title;
        foreach (var item in focus.Items.ToList()) _entries.Remove(item);
        focus.Clear();
        var items = new List<HomeEntry>();
        if (dest.Title == "Videos" && jump is not null) items.AddRange(jump.Entries.Take(6));
        if (section is not null) items.AddRange(section.Entries);
        if (items.Count == 0) items.Add(dest);
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(Ui.Text(dest.Title.ToUpperInvariant(), T.Caption, T.TextMuted, FontWeight.SemiBold, letterSpacing: 4).Margin(48, 0, 0, 14));
        foreach (var e in items.Take(40))
        {
            var name = Ui.Text(e.Title, 30, T.TextMuted, FontWeight.Normal);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            var side = Ui.Text(e.Subtitle ?? "", T.Caption, T.TextFaint);
            side.TextTrimming = TextTrimming.CharacterEllipsis;
            side.MaxWidth = 150;
            side.VerticalAlignment = VerticalAlignment.Center;
            var marker = Nordic.Marker(20);
            marker.Opacity = 0;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(marker.Margin(0, 0, 10, 0));
            row.Children.Add(name.Col(1));
            row.Children.Add(side.Margin(16, 0, 0, 0).Col(2));
            var card = new Card(row.Margin(8, 8, 16, 8), 0) { FocusScale = 1.0, ShowRing = false, ShowGlow = false, HorizontalAlignment = HorizontalAlignment.Stretch };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f =>
            {
                card.Body.Background = f ? Ui.HorizontalFade(Colors.White, 0.14, 0) : Brushes.Transparent;
                name.Foreground = f ? T.Text : T.TextMuted;
                side.Foreground = f ? T.TextMuted : T.TextFaint;
                marker.Opacity = f ? 1 : 0;
            };
            card.Item.Activate = e.Open;
            card.Item.Secondary = e.Options;
            _entries[card.Item] = e;
            stack.Children.Add(card);
            focus.Add(card.Item);
        }
        host.Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        if (!_building) RefreshFocus();
    }

    /// <summary>The focused thing on the right: its picture, fading out at its edges into the
    /// night like a model in an inventory, and its progress on the meter.</summary>
    private void ShowSagaItem(HomeEntry e)
    {
        if (_sagaArt is null) return;
        const double w = 640, h = 360;
        var art = new Border
        {
            Width = w, Height = h, Child = HomeModel.Art(e, w, h),
            OpacityMask = new RadialGradientBrush
            {
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.Black, 0.55), new GradientStop(Colors.Transparent, 1) },
            },
        };
        _sagaArt.Content = art;
        if (_sagaBar?.Parent is Border meter)
        {
            meter.IsVisible = e.Progress >= 0;
            _sagaBar.Width = Math.Max(2, (meter.Width - 6) * Math.Clamp(e.Progress, 0, 1));
        }
    }

    // ------------------------------------------------------------------ input

    private Tile? _moving;

    /// <summary>Picks a tile up: the D-pad carries it along its row, A or B puts it down.</summary>
    public void StartMove(Tile tile)
    {
        _moving = tile;
        L.Rumble(0.3f, 60);
        L.Window.Toast($"Moving {tile.Name}. Use the D-pad, then A to put it down.", "move", 3);
    }

    private bool HandleMove(ActionEvent e)
    {
        if (_moving is not { } t || e.Phase is ActionPhase.Released or ActionPhase.Changed) return _moving is not null;
        switch (e.Action)
        {
            case Act.Left or Act.Up:
                L.Tiles.Move(t.Id, -1);
                Rebuild();
                L.Play(Lounge.Core.Audio.UiSound.Move);
                return true;
            case Act.Right or Act.Down:
                L.Tiles.Move(t.Id, +1);
                Rebuild();
                L.Play(Lounge.Core.Audio.UiSound.Move);
                return true;
            case Act.Accept or Act.Back or Act.MoveMode:
                _moving = null;
                L.Play(Lounge.Core.Audio.UiSound.Select);
                L.Rumble(0.2f, 40);
                return true;
        }
        return true;
    }

    public override bool Handle(ActionEvent e)
    {
        if (_moving is not null) return HandleMove(e);
        if (e.Phase == ActionPhase.Pressed)
        {
            switch (e.Action)
            {
                case Act.MoveMode:
                    if (Focused is { } mf && _entries.TryGetValue(mf, out var me) && me.Tile is { } mt) StartMove(mt);
                    else L.Play(Lounge.Core.Audio.UiSound.Error);
                    return true;
                case Act.ContextMenu:
                    if (Focused is { } ff && _entries.TryGetValue(ff, out var en) && en.Options is { } opts) opts();
                    else HomeMenu();
                    return true;
                case Act.Tertiary:
                    L.Window.Push(new SearchScreen());
                    return true;
                case Act.NextSection: return MoveFocus(Lounge.Core.Focus.Dir.Down);
                case Act.PrevSection: return MoveFocus(Lounge.Core.Focus.Dir.Up);
            }
        }
        return base.Handle(e);
    }

    protected override bool OnBack()
    {
        // At the root, B moves focus back to the start before offering to leave (in CarPlay,
        // the first app rather than the sidebar).
        if ((T.Def.Layout == HomeLayout.Dock ? _carFirst : Scope?.Root.Items.FirstOrDefault()) is { } first && Focused != first)
        {
            FocusItem(first);
            L.Play(Lounge.Core.Audio.UiSound.Back);
            return true;
        }
        return false;
    }

    private void HomeMenu() => L.Window.Menu("Home",
    [
        new MenuEntry("Search", "search", () => L.Window.Push(new SearchScreen())),
        new MenuEntry("Rescan library", "refresh", () => _ = LibraryService.ScanAsync(quiet: false)),
        new MenuEntry("Change theme", "sun", () => L.Window.OpenSettings("Appearance")),
        new MenuEntry("Hidden tiles", "eye", () => L.Window.Push(new AppsScreen(showHidden: true))),
        new MenuEntry("Power", "power", Launcher.ShowPowerMenu),
    ]);
}
