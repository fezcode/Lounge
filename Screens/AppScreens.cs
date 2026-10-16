using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Lounge.Controls;
using Lounge.Core.Focus;
using Lounge.Core.Input;
using Lounge.Core.Launch;
using Lounge.Platform;
using Lounge.Shell;

namespace Lounge.Screens;

/// <summary>All apps and games (or all web pages) as a grid of tiles.</summary>
public sealed class AppsScreen : BrowseScreen
{
    private readonly bool _web;
    private readonly bool _hidden;

    public AppsScreen(bool web = false, bool showHidden = false) : base(showHidden ? "Hidden tiles" : web ? "Web" : "Apps & Games", "Open")
    {
        _web = web;
        _hidden = showHidden;
        Build();
        L.Tiles.Changed += Rebuild;
        Lounge.Platform.SiteIcons.Updated += Rebuild;   // a site's icon arrived
    }

    private void Rebuild() => Avalonia.Threading.Dispatcher.UIThread.Post(Build);

    public override void OnClosed()
    {
        L.Tiles.Changed -= Rebuild;
        Lounge.Platform.SiteIcons.Updated -= Rebuild;
    }

    private void Build()
    {
        var entries = _hidden
            ? L.Tiles.HiddenTiles.Select(HomeModel.TileEntry).ToList()
            : HomeModel.Apps(_web);
        Sub.Text = _hidden ? "Ⓐ shows a tile on Home again" : _web ? "Streaming sites, played with the controller. Ⓧ shows a site's options." : "Ⓐ opens · hold View + Menu in any app to come back";
        double w = _web ? 380 : 250, h = _web ? 214 : 250;
        var (view, focus) = Grid(Columns(w), w, h + 60);
        var wrap = (WrapPanel)view.Content!;
        foreach (var e in entries)
        {
            var card = new PosterCard(w, h, e.Title, e.Subtitle, (cw, ch) => HomeModel.Art(e, cw, ch), tag: e);
            var entry = e;
            card.Item.Activate = _hidden && e.Tile is { } t
                ? () => { L.Tiles.SetHidden(t.Id, false); L.Window.Toast($"{t.Name} is back on Home.", "eye"); }
                : e.Open;
            if (e.Options is not null) { Options[card.Item] = e.Options; card.Item.Secondary = e.Options; }
            wrap.Children.Add(Ui.V(14, card, Ui.V(2, Ui.Text(e.Title, T.Caption, T.Text, FontWeight.SemiBold).Also(x => x.Width = w), Ui.Muted(e.Subtitle ?? ""))));
            focus.Add(card.Item);
        }
        if (_hidden && entries.Count == 0) wrap.Children.Add(Ui.Muted("Nothing is hidden.", T.Body));
        Body.Content = view;
        UseScope(focus);
    }
}

/// <summary>Pick an app to put on Home: everything in the Start Menu, or any program
/// found in Files.</summary>
public sealed class AddAppScreen : BrowseScreen
{
    public AddAppScreen() : base("Add an app")
    {
        Sub.Text = "Programs from your Start Menu";
        var list = new FocusList(horizontal: false);
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(96, 10, 96, 80) };

        var browse = new Card(Ui.H(18, Icons.Make("folder", 30, T.Accent), Ui.Text("Find a program in Files", T.Body, T.Accent, FontWeight.SemiBold)).Margin(24, 16), 14) { FocusScale = 1.01 };
        browse.Body.Background = T.WithAlpha(T.AccentColor, 0.12);
        browse.Item.Activate = () => L.Window.Push(new FilesScreen(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            new FilePick(FilePickKind.Program, "Choose a program", path => Added(new Tile { Kind = TileKind.App, Name = Path.GetFileNameWithoutExtension(path), Target = path, AllowMouse = true }))));
        panel.Children.Add(browse);
        list.Add(browse.Item);

        if (Win32.SteamInstalled() && L.Tiles.All.All(t => t.Target != "steam://open/bigpicture"))
        {
            var steam = Row("Steam Big Picture", "steam://open/bigpicture", null);
            steam.Item.Activate = () => Added(new Tile { Kind = TileKind.Protocol, Name = "Steam Big Picture", Target = "steam://open/bigpicture", Color = "#1B2838", IsGame = true });
            panel.Children.Add(steam);
            list.Add(steam.Item);
        }

        foreach (var (name, path) in ShellIcons.StartMenuApps())
        {
            var card = Row(name, path, path);
            card.Item.Activate = () => Added(new Tile { Kind = TileKind.App, Name = name, Target = path, AllowMouse = true });
            panel.Children.Add(card);
            list.Add(card.Item);
        }

        var store = ShellIcons.StoreApps();
        if (store.Count > 0)
        {
            panel.Children.Add(Ui.Text("MICROSOFT STORE APPS", T.Caption, T.TextMuted, FontWeight.Bold, letterSpacing: 3).Margin(24, 26, 0, 8));
            foreach (var (name, aumid) in store)
            {
                var card = Row(name, "Microsoft Store", null);
                card.Item.Activate = () => Added(new Tile { Kind = TileKind.StoreApp, Name = name, Target = aumid, AllowMouse = true });
                panel.Children.Add(card);
                list.Add(card.Item);
            }
        }
        Body.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        UseScope(list);
    }

    private static Card Row(string name, string detail, string? iconSource)
    {
        var g = Ui.Grid("Auto,*", "Auto");
        g.Children.Add(Icons.Make("apps", 30, T.TextMuted).Margin(0, 0, 20, 0).Col(0));
        g.Children.Add(Ui.V(2, Ui.Text(name, T.Body, T.Text, FontWeight.SemiBold), Ui.Muted(detail)).Col(1));
        var card = new Card(g.Margin(24, 12), 12) { FocusScale = 1.01, ShowRing = false };
        card.Body.Background = Brushes.Transparent;
        card.Focused += f => card.Body.Background = f ? T.Surface : Brushes.Transparent;
        return card;
    }

    private static void Added(Tile t)
    {
        L.Tiles.Add(t);
        L.Window.Toast($"{t.Name} is on Home now.", "plus");
        L.Window.Pop();
    }
}

/// <summary>Edit a tile: name, address or program, picture, colour, and how Lounge
/// behaves around it. Everything is chosen with the controller.</summary>
public sealed class TileEditorScreen : BrowseScreen
{
    private readonly Tile _tile;
    private readonly bool _isNew;
    private readonly Dictionary<string, TextBlock> _values = new();

    private static readonly (string Hex, string Name)[] Colours =
    [
        ("#3A3A4A", "Graphite"), ("#0A84FF", "Blue"), ("#30D158", "Green"), ("#FF9F0A", "Orange"), ("#FF375F", "Pink"),
        ("#BF5AF2", "Purple"), ("#E50914", "Red"), ("#1B2838", "Navy"), ("#64D2FF", "Teal"),
    ];

    public static TileEditorScreen NewWeb() =>
        new(new Tile { Kind = TileKind.Web, Name = "New site", Target = "https://", Color = "#0A84FF", AllowMouse = true }, isNew: true);

    public TileEditorScreen(Tile tile, bool isNew = false) : base(isNew ? "Add a website" : $"Edit {tile.Name}")
    {
        _tile = tile.Clone();
        _isNew = isNew;
        _brand = Colours.Any(c => c.Hex == tile.Color) ? null : tile.Color;
        Sub.Text = "Changes are saved when you choose Save";
        Build();
    }

    private void Build()
    {
        _values.Clear();
        var list = new FocusList(horizontal: false);
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(96, 10, 96, 80), MaxWidth = 1300, HorizontalAlignment = HorizontalAlignment.Left };
        void Row(string key, string label, Func<string> value, Action run)
        {
            var v = Ui.Text(value(), T.Body, T.TextMuted, FontWeight.SemiBold);
            _values[key] = v;
            var g = Ui.Grid("*,Auto", "Auto");
            g.Children.Add(Ui.Text(label, T.Body, T.Text, FontWeight.Medium).Col(0));
            g.Children.Add(Ui.H(12, v, Icons.Make("chevron", 22, T.TextFaint)).Col(1));
            var card = new Card(g.Margin(28, 20), 14) { FocusScale = 1.01, ShowRing = false };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f => card.Body.Background = f ? T.Surface : Brushes.Transparent;
            card.Item.Activate = () => { run(); Refresh(); };
            panel.Children.Add(card);
            list.Add(card.Item);
            _refreshers[key] = () => v.Text = value();
        }

        Row("name", "Name", () => _tile.Name, async () =>
        {
            var n = await L.Window.KeyboardAsync(new KeyboardRequest("Tile name", _tile.Name, Validate: s => string.IsNullOrWhiteSpace(s) ? "A name can't be empty." : null));
            if (n is not null) { _tile.Name = n.Trim(); Refresh(); }
        });
        if (_tile.Kind is TileKind.Web or TileKind.Protocol)
            Row("target", _tile.IsWeb ? "Address" : "Link", () => _tile.Target, async () =>
            {
                var u = await L.Window.KeyboardAsync(new KeyboardRequest(_tile.IsWeb ? "Web address" : "Link", _tile.Target, SelectAll: false,
                    Suggestions: ["https://", "https://www.", ".com", ".tv"],
                    Validate: s => _tile.IsWeb && !Uri.TryCreate(s, UriKind.Absolute, out _) ? "That isn't a full web address (https://...)." : null));
                if (u is null) return;
                _tile.Target = u.Trim();
                // A known streaming site gets its logo, whether the tile is new or edited.
                if (_tile.IsWeb && _tile.Icon.Length == 0) _tile.Logo = TileStore.LogoFor(_tile.Target) ?? "";
                Refresh();
            });
        if (_tile.Kind == TileKind.App)
        {
            Row("target", "Program", () => Path.GetFileName(_tile.Target), () =>
                L.Window.Push(new FilesScreen(Path.GetDirectoryName(_tile.Target), new FilePick(FilePickKind.Program, "Choose a program", p => { _tile.Target = p; Refresh(); }))));
            Row("args", "Arguments", () => string.IsNullOrEmpty(_tile.Args) ? "None" : _tile.Args, async () =>
            {
                var a = await L.Window.KeyboardAsync(new KeyboardRequest("Arguments", _tile.Args, SelectAll: false));
                if (a is not null) { _tile.Args = a; Refresh(); }
            });
        }
        Row("icon", "Picture", PictureName, () =>
        {
            var automatic = _tile.IsWeb ? "Site icon" : "Automatic";
            var items = new List<MenuEntry>
            {
                new("Choose an image in Files", "image", () => L.Window.Push(new FilesScreen(null, new FilePick(FilePickKind.Image, "Choose a picture", p => { _tile.Icon = p; Refresh(); })))),
            };
            if (_tile.IsWeb && TileStore.LogoFor(_tile.Target) is { } logo)
                items.Add(new MenuEntry("Logo", "star", () => { _tile.Icon = ""; _tile.Logo = logo; Refresh(); }, Checked: PictureName() == "Logo"));
            items.Add(new MenuEntry(automatic, "refresh", () => { _tile.Icon = ""; _tile.Logo = ""; Refresh(); }, Checked: PictureName() == automatic));
            L.Window.Menu("Picture", items);
        });
        Row("color", "Colour", () => Colours.FirstOrDefault(c => c.Hex == _tile.Color).Name ?? "Brand colour", () =>
        {
            var choices = Colours.ToList();
            if (_brand is not null) choices.Insert(0, (_brand, "Brand colour"));
            L.Window.Menu("Colour", choices.Select(c => new MenuEntry(c.Name, "sun", () => { _tile.Color = c.Hex; Refresh(); }, Checked: c.Hex == _tile.Color)).ToList());
        });
        if (_tile.IsWeb)
        {
            Row("external", "Open in", () => _tile.OpenExternally ? "Full browser" : "Lounge", () => _tile.OpenExternally = !_tile.OpenExternally);
            Row("agent", "Site version", () => _tile.UserAgent.Length == 0 ? "Standard" : _tile.UserAgent == TileStore.TvUserAgent ? "TV (arrow keys)" : "Custom", () =>
            {
                // TV sites are driven with the arrow keys; standard ones with the pointer.
                var tv = _tile.UserAgent.Length == 0;
                _tile.UserAgent = tv ? TileStore.TvUserAgent : "";
                _tile.AllowMouse = !tv;
            });
        }
        if (_tile.Kind != TileKind.Cogas)
            Row("mouse", _tile.IsWeb ? "Start with the pointer" : "Start with the controller mouse",
                () => _tile.AllowMouse ? "Yes" : _tile.IsWeb ? "No, arrow keys" : "No (hold View + B for it)",
                () => _tile.AllowMouse = !_tile.AllowMouse);
        if (!_tile.IsWeb)
        {
            Row("game", "This is a game", () => _tile.IsGame ? "Yes" : "No", () => _tile.IsGame = !_tile.IsGame);
            Row("return", "When you come back", () => _tile.OnReturn switch
            {
                ReturnBehaviour.Switch => "Leave it running", ReturnBehaviour.Close => "Close it", ReturnBehaviour.Ask => "Ask", _ => "Use the default",
            }, () => _tile.OnReturn = (ReturnBehaviour)(((int)_tile.OnReturn + 1) % 4));
        }
        Row("hide", "Show on Home", () => _tile.Hidden ? "Hidden" : "Shown", () => _tile.Hidden = !_tile.Hidden);

        var save = new Card(Ui.H(14, Icons.Make("check", 28, T.OnAccent), Ui.Text("Save", T.Body, T.OnAccent, FontWeight.SemiBold)).Margin(36, 18), 40) { FocusScale = 1.06 };
        save.Body.Background = T.Accent;
        save.Item.Activate = Save;
        var buttons = Ui.H(18, save);
        var buttonFocus = new FocusList(horizontal: true);
        buttonFocus.Add(save.Item);
        if (!_isNew)
        {
            var del = new Card(Ui.H(14, Icons.Make("trash", 28, T.Danger), Ui.Text("Remove tile", T.Body, T.Danger, FontWeight.SemiBold)).Margin(36, 18), 40) { FocusScale = 1.06 };
            del.Body.Background = T.Surface;
            del.Item.Activate = () => L.Window.Ask($"Remove {_tile.Name}?", "Only the tile goes. Nothing is uninstalled.", ["Remove", "Cancel"], 1, i =>
            {
                if (i != 0) return;
                L.Tiles.Remove(_tile.Id);
                L.Window.Pop();
            }, "trash");
            buttons.Children.Add(del);
            buttonFocus.Add(del.Item);
        }
        panel.Children.Add(buttons.Margin(0, 26, 0, 0));
        list.Add(buttonFocus);
        Body.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        UseScope(list);
    }

    private readonly Dictionary<string, Action> _refreshers = new();
    /// <summary>The tile's own colour when it is not one of the palette's (Prime's blue).</summary>
    private readonly string? _brand;

    private string PictureName() =>
        _tile.Icon.Length > 0 ? Path.GetFileName(_tile.Icon) : _tile.Logo.Length > 0 ? "Logo" : _tile.IsWeb ? "Site icon" : "Automatic";

    private void Refresh()
    {
        foreach (var r in _refreshers.Values) r();
    }

    private void Save()
    {
        if (_tile.IsWeb && !Uri.TryCreate(_tile.Target, UriKind.Absolute, out _))
        {
            L.Window.Toast("Add a full web address first (https://...).", "warning");
            return;
        }
        L.Tiles.Update(_tile);
        L.Window.Toast($"{_tile.Name} saved.", "check");
        L.Window.Pop();
    }
}
