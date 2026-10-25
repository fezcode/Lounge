using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core.Focus;
using Lounge.Core.Media;
using Lounge.Core.Settings;
using Lounge.Core.Themes;
using Lounge.Media;
using Lounge.Shell;

namespace Lounge.Screens;

/// <summary>The CarPlay theme's own pieces: rounded app icons, the status sidebar down the
/// left with the time and the recent apps, and the Home button that switches between the
/// app pages and the Dashboard.</summary>
public static class CarPlay
{
    public const double RailWidth = 172;
    public const double RailIcon = 100;
    private const double RailSpacing = 24;

    public static bool On => T.Def.Layout == HomeLayout.Dock;

    /// <summary>Home shows the Dashboard instead of the app pages.</summary>
    public static bool Dashboard { get; set; }

    private static readonly List<string> _recent = new();

    /// <summary>Notes an app opened from Home, so the sidebar shows it first.</summary>
    public static void Note(string title)
    {
        _recent.Remove(title);
        _recent.Insert(0, title);
        if (_recent.Count > 8) _recent.RemoveAt(_recent.Count - 1);
    }

    /// <summary>Wraps an entry so opening it counts as a recent app.</summary>
    public static HomeEntry Noted(HomeEntry e) => e with { Open = () => { Note(e.Title); e.Open(); } };

    /// <summary>Everything on the app pages, in CarPlay's order: the places, then your apps
    /// and sites, then Settings and Power last.</summary>
    public static List<HomeEntry> Apps()
    {
        var dest = HomeModel.Destinations();
        var list = new List<HomeEntry>();
        list.AddRange(dest.Where(d => d.Title is not ("Settings" or "Power")));
        list.AddRange(HomeModel.Apps(web: false).Where(a => a.Tile is not null));
        list.AddRange(HomeModel.Apps(web: true).Where(a => a.Tile is not null));
        list.AddRange(dest.Where(d => d.Title is "Settings" or "Power"));
        return list.Select(Noted).ToList();
    }

    public static bool MusicOn =>
        L.NowPlayingKind == MediaKind.Audio && L.Player?.State is PlayerState.Playing or PlayerState.Paused;

    /// <summary>The sidebar's four apps: Now Playing while music is on, then the most
    /// recent, filled up with Music, Videos, Apps &amp; Games and Web.</summary>
    public static List<HomeEntry> RailEntries()
    {
        var all = Apps();
        var picked = new List<HomeEntry>();
        if (MusicOn)
            picked.Add(new HomeEntry("Now Playing", Playback.NowPlayingLine(), null, "audio", "#FF375F", () =>
            {
                if (L.Window.Top is not NowPlayingScreen) L.Window.Push(new NowPlayingScreen());
            }));
        foreach (var title in _recent.Concat(["Music", "Videos", "Apps & Games", "Web"]))
        {
            if (picked.Count >= 4) break;
            if (picked.Any(p => p.Title == title) || (title == "Music" && MusicOn)) continue;
            if (all.FirstOrDefault(a => a.Title == title) is { } e) picked.Add(e);
        }
        return picked;
    }

    // ------------------------------------------------------------------ icons

    /// <summary>A rounded app icon: a glossy gradient of the app's colour with its glyph in
    /// white, or the app's own picture.</summary>
    public static Control Icon(HomeEntry e, double size)
    {
        var radius = new CornerRadius(size * 0.225);
        var box = new Border { Width = size, Height = size, CornerRadius = radius, ClipToBounds = true };
        if (e.Tile is { } tile && e.Art is { } art)
        {
            if (tile.IsWeb && tile.Logo.Length == 0)
            {
                // A website keeps its own icon on white, like a web clip.
                box.Background = Brushes.White;
                box.Child = ImageCache.ArtNow(art, size * 0.64, size * 0.64, stretch: Stretch.Uniform, waiting: Brushes.Transparent)
                    .Align(HorizontalAlignment.Center, VerticalAlignment.Center);
                return box;
            }
            box.Background = tile.IsWeb ? new SolidColorBrush(Color.FromRgb(16, 16, 18)) : Gloss(e.Color);
            // A streaming site's wordmark is wide; Lounge's own games have square emblems.
            var wordmark = tile.Logo.Length > 0 && tile.Kind != Lounge.Core.Launch.TileKind.Builtin;
            var w = wordmark ? size * 0.78 : tile.Logo.Length > 0 ? size * 0.72 : size * 0.62;
            box.Child = ImageCache.ArtNow(art, w, wordmark ? size * 0.4 : w, stretch: Stretch.Uniform, waiting: Brushes.Transparent)
                .Align(HorizontalAlignment.Center, VerticalAlignment.Center);
            return box;
        }
        if (e.Tile is not null && e.Art is null && !e.Tile.IsBrowser)
        {
            box.Child = Ui.Monogram(e.Title, e.Color, size);
            return box;
        }
        box.Background = Gloss(e.Color);
        box.Child = Icons.Make(e.Icon, size * 0.5, Brushes.White, 1.5).Align(HorizontalAlignment.Center, VerticalAlignment.Center);
        return box;
    }

    /// <summary>An icon's fill: lighter at the top, deeper at the bottom.</summary>
    public static IBrush Gloss(string hex)
    {
        Color c;
        try { c = Color.Parse(hex); } catch (FormatException) { c = Colors.SlateGray; }
        static byte Mix(byte v, double toward, double by) => (byte)Math.Clamp(v + (toward - v) * by, 0, 255);
        var top = Color.FromRgb(Mix(c.R, 255, 0.18), Mix(c.G, 255, 0.18), Mix(c.B, 255, 0.18));
        var bottom = Color.FromRgb(Mix(c.R, 0, 0.16), Mix(c.G, 0, 0.16), Mix(c.B, 0, 0.16));
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        };
    }

    /// <summary>A focusable icon: on focus a white rounded outline sits just around it.</summary>
    public static Card IconCard(Control icon, double size)
    {
        var card = new Card(icon, size * 0.225 + 8) { Width = size + 16, Height = size + 16, FocusScale = 1.0, ShowGlow = false };
        card.Body.Background = Brushes.Transparent;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return card;
    }

    // ------------------------------------------------------------------ the Home button

    /// <summary>The Home button's glyph: the app grid's dots on the Dashboard, the
    /// Dashboard's panels on the app pages.</summary>
    public static Control HomeGlyph(bool showDashboard, double size)
    {
        var g = new Grid { Width = size, Height = size };
        var fill = Brushes.White;
        if (showDashboard)
        {
            // One tall panel on the left, two stacked on the right.
            var unit = size / 10;
            g.Children.Add(new Border { Width = unit * 5.4, Height = size, CornerRadius = new CornerRadius(unit), Background = fill, HorizontalAlignment = HorizontalAlignment.Left });
            g.Children.Add(new Border { Width = unit * 3.6, Height = unit * 4.6, CornerRadius = new CornerRadius(unit), Background = fill, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top });
            g.Children.Add(new Border { Width = unit * 3.6, Height = unit * 4.6, CornerRadius = new CornerRadius(unit), Background = fill, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
            return g;
        }
        // Two rows of three rounded squares.
        var cell = size / 3.6;
        var u = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3, Rows = 2, Width = size, Height = cell * 2 + cell * 0.5, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 6; i++)
            u.Children.Add(new Border { Width = cell, Height = cell, CornerRadius = new CornerRadius(cell * 0.3), Background = fill });
        g.Children.Add(u);
        return g;
    }

    // ------------------------------------------------------------------ the sidebar

    /// <summary>Builds the sidebar. Home builds a focusable one (its icons open apps and the
    /// Home button switches views); everywhere else the window shows one to look at.</summary>
    public static Control Rail(bool onHome, FocusList? focus, Action? home, Dictionary<FocusItem, HomeEntry>? entries, Action<Card>? homeCard = null)
    {
        var time = Ui.Text("", 34, T.Text, FontWeight.SemiBold).Align(HorizontalAlignment.Center, VerticalAlignment.Top);
        void Tick() => time.Text = DateTime.Now.ToString(L.Settings?.Bool(K.Clock24h) ?? true ? "HH:mm" : "h:mm");
        Tick();
        // Where a phone shows its signal, the controller shows whether it is connected.
        var connected = L.Router?.Connected ?? true;
        var status = Icons.Make("gamepad", 26, connected ? T.Text : T.TextFaint).Align(HorizontalAlignment.Center, VerticalAlignment.Top);

        var apps = new StackPanel { Spacing = RailSpacing - 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var e in RailEntries())
        {
            var icon = Icon(e, RailIcon);
            if (e.Title == "Now Playing") icon = WithPlayingBadge(icon);
            var card = IconCard(icon, RailIcon);
            card.Item.Activate = e.Open;
            if (entries is not null) entries[card.Item] = e;
            focus?.Add(card.Item);
            apps.Children.Add(card);
        }

        var homeButton = new Border
        {
            Width = RailIcon, Height = RailIcon, CornerRadius = new CornerRadius(RailIcon * 0.225),
            Background = new SolidColorBrush(Color.FromRgb(58, 58, 60)),
            // On Home it shows the other view; elsewhere, the view Home returns to.
            Child = HomeGlyph(onHome ? !Dashboard : Dashboard, RailIcon * 0.42).Align(HorizontalAlignment.Center, VerticalAlignment.Center),
        };
        var homeCardView = IconCard(homeButton, RailIcon);
        homeCardView.Item.Activate = home;
        focus?.Add(homeCardView.Item);
        homeCard?.Invoke(homeCardView);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Width = RailWidth, HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(time.Margin(0, 30, 0, 0).Row(0));
        grid.Children.Add(status.Margin(0, 8, 0, 0).Row(1));
        grid.Children.Add(apps.Row(2));
        grid.Children.Add(homeCardView.Align(HorizontalAlignment.Center, VerticalAlignment.Bottom).Margin(0, 0, 0, 28).Row(3));

        var rail = new Border
        {
            Width = RailWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            // Opaque: Home draws its own focusable sidebar exactly over the window's one.
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = grid,
        };
        var clock = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Tick());
        rail.AttachedToVisualTree += (_, _) => { Tick(); clock.Start(); };
        rail.DetachedFromVisualTree += (_, _) => clock.Stop();
        return rail;
    }

    /// <summary>Now Playing's icon carries a small blue equaliser in its corner.</summary>
    private static Control WithPlayingBadge(Control icon)
    {
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Bottom };
        foreach (var h in new[] { 10.0, 18, 13 })
            bars.Children.Add(new Border { Width = 4, Height = h, CornerRadius = new CornerRadius(2), Background = Brushes.White, VerticalAlignment = VerticalAlignment.Bottom });
        var badge = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = new SolidColorBrush(Color.Parse("#0A84FF")),
            BorderBrush = Brushes.Black, BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -6, -6),
            Child = bars.Align(HorizontalAlignment.Center, VerticalAlignment.Center).Margin(0, 0, 0, 2),
        };
        var g = new Grid { Width = icon.Width, Height = icon.Height };
        g.Children.Add(icon);
        g.Children.Add(badge);
        return g;
    }

    /// <summary>The window's sidebar, beside every screen but the full-screen ones. Home
    /// draws its own, focusable, exactly on top of it, so it never flickers in transitions.</summary>
    public sealed class WindowRail : ContentControl
    {
        public WindowRail()
        {
            HorizontalAlignment = HorizontalAlignment.Left;
            Width = RailWidth;
        }

        public void Refresh(Screen? top)
        {
            IsVisible = On && top is { ShowsChrome: true };
            Content = IsVisible ? Rail(top is HomeScreen, null, null, null) : null;
        }
    }
}
