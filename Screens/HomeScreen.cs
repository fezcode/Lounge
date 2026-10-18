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

    // Nothing blinks behind another screen (coming back rebuilds the view and its timer).
    public override void OnHidden() => _blink?.Stop();

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
        var (content, root) = T.Def.Layout switch
        {
            HomeLayout.BottomRibbon => LivingRoom(),
            HomeLayout.SideMenu => Kodi(),
            HomeLayout.IconRow => ConsoleOne(),
            HomeLayout.TileGrid => Series(),
            HomeLayout.Programme => Programme(),
            HomeLayout.Cabinet => Cabinet(),
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

    protected override void OnFocusMoved(FocusItem? old, FocusItem? now) => UpdateHero(now);

    private void UpdateHero(FocusItem? now)
    {
        if (now is null || !_entries.TryGetValue(now, out var e)) return;
        if (_heroTitle is not null) _heroTitle.Text = e.Title;
        if (_heroSubtitle is not null) _heroSubtitle.Text = e.Subtitle ?? "";
        var art = e.Tile is null ? e.Backdrop ?? e.Art ?? e.Video?.Art : null;
        L.Window.Backdrop.SetArt(art, blur: T.Def.Layout != HomeLayout.TopTabsHero, strength: T.Def.Layout == HomeLayout.TopTabsHero ? 0.95 : 0.6);
    }

    // ------------------------------------------------------------------ Aurora

    private (Control, FocusNode) Aurora()
    {
        var grid = Ui.Grid("*", "Auto,*,Auto");
        var root = new FocusList(horizontal: false);

        // Tabs
        var tabsFocus = root.Add(new FocusList(horizontal: true, rememberIndex: false));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(96, 44, 0, 0) };
        foreach (var d in HomeModel.Destinations())
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
        hero.Margin = new Thickness(96, 0, 96, 30);
        body.Children.Add(hero.Row(0));

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
        hero.Margin = new Thickness(110, 90, 110, 0);
        grid.Children.Add(hero.Row(0));

        // The preview row above the ribbon shows what the focused ribbon tile holds.
        var previewHost = new ContentControl { Height = 330 };
        var previewFocus = root.Add(new FocusList(horizontal: true));
        grid.Children.Add(previewHost.Row(1));

        var ribbonFocus = root.Add(new FocusList(horizontal: true));
        var ribbon = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22, Margin = new Thickness(110, 26, 110, 26) };
        var ribbonEntries = new List<(HomeEntry Entry, string? Section)>();
        if (sections.TryGetValue("jump", out var jump)) ribbonEntries.Add((new HomeEntry("Continue", "Jump back in", null, "clock", "#5E5CE6", () => jump.Entries[0].Open()), "jump"));
        foreach (var d in HomeModel.Destinations())
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
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 26, Margin = new Thickness(110, 10, 110, 30) };
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
        foreach (var d in HomeModel.Destinations())
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
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(0, 160, 0, 0) };
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
        info.Margin = new Thickness(110, 40, 110, 0);
        grid.Children.Add(info.Row(1));

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
        var dest = new HomeSection("dest", "My stuff", "apps", HomeModel.Destinations(), EntryShape.Wide);
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
        grid.Children.Add(Ui.V(10, house, tonight, rule).Margin(96, 56, 96, 0).Col(0, 2).Row(0));

        var indexFocus = root.Add(new FocusList(horizontal: false, rememberIndex: true));
        var index = new StackPanel { Spacing = 2, Margin = new Thickness(96, 34, 0, 0) };
        var featureHost = new ContentControl();
        var featureFocus = root.Add(new FocusList(horizontal: false, rememberIndex: false));
        var number = 0;
        foreach (var d in HomeModel.Destinations())
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
        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 40, 0, 0) };

        void AddRow(HomeSection s, string label, double w, double h)
        {
            var list = new FocusList(horizontal: true);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Margin = new Thickness(0, 16, 96, 20) };
            foreach (var e in s.Entries.Take(12))
            {
                var card = EntryCard(e, w, h);
                panel.Children.Add(Captioned(card, e, w));
                list.Add(card.Item);
            }
            stack.Children.Add(Ui.V(0, style.Header(label),
                new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, ClipToBounds = false }));
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
        host.Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
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
        grid.Children.Add(Ui.V(18, marquee, subtitle).Margin(96, 56, 96, 0).Col(0, 2).Row(0));

        var menuFocus = root.Add(new FocusList(horizontal: false, rememberIndex: true));
        var menu = new StackPanel { Spacing = 6, Margin = new Thickness(96, 44, 0, 0) };
        var lineupHost = new ContentControl();
        var lineupFocus = root.Add(new FocusList(horizontal: false, rememberIndex: false));
        Control? activeCursor = null;
        var destinations = HomeModel.Destinations();
        var reopen = destinations.FirstOrDefault(x => x.Title == _cabinetDest) ?? destinations[0];
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
        // At the root, B moves focus back to the start before offering to leave.
        if (Scope?.Root.Items.FirstOrDefault() is { } first && Focused != first)
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
