namespace Lounge.Core.Themes;

/// <summary>How the home screen is arranged. Each theme picks one, which is why the
/// themes look different rather than just recoloured.</summary>
public enum HomeLayout
{
    /// <summary>Apple TV: top tab bar, full-bleed hero of the focused item, translucent rows.</summary>
    TopTabsHero,
    /// <summary>Smart TV: content backdrop with a launcher ribbon along the bottom.</summary>
    BottomRibbon,
    /// <summary>XBMC/Kodi: vertical main menu on the left over fanart.</summary>
    SideMenu,
    /// <summary>PS5: a row of large icons along the top, the focused item fills the backdrop.</summary>
    IconRow,
    /// <summary>Xbox Series: a dense tile grid with grouped rows.</summary>
    TileGrid,
    /// <summary>Screening Room: a printed programme. Destinations are a numbered index in
    /// large serif type; the focused one's line-up sits beside it.</summary>
    Programme,
    /// <summary>Arcade: a cabinet's game-select screen. A pixel marquee, the destinations as
    /// an uppercase menu with a blinking cursor, the focused one's line-up beside it.</summary>
    Cabinet,
    /// <summary>CarPlay: a status sidebar down the left (the time, recent apps, the Home
    /// button), app icons in pages of two rows with page dots, and a Dashboard of widgets
    /// one press away.</summary>
    Dock,
    /// <summary>A light TV launcher: centred pill tabs, the focused item's title large, an
    /// Up Next panel beside the weather, and a row of app tiles along the bottom.</summary>
    Glance,
    /// <summary>A floating glass sidebar (who's watching, Search, Home, the destinations, then
    /// the apps), the focused item's art filling the screen, its title over an Up Next row.</summary>
    Sidebar,
    /// <summary>A northern hall's inventory: the destinations as uppercase words with runes, the
    /// chosen one's contents as a plain list beside them, and the focused thing shown on the
    /// right, its picture fading into the dark over a framed card.</summary>
    Hall,
}

/// <summary>A procedural default backdrop, so themes need no shipped wallpapers. Projector:
/// a warm cone of light with flicker, film grain and a vignette. Pixel: a low-resolution
/// pixel-art scene (dithered sky, stars, a striped sun, parallax mountains, a scrolling grid)
/// scaled up without smoothing, under CRT scanlines. Drive: a car display's wallpaper,
/// black with broad bands of colour sweeping across it. Mist: a pale morning grey with soft
/// pastel light drifting through it. Liquid: near-black with warm and cool light pooling
/// under glass, and a slow sheen sliding across it. Snowfall: a cold night over dark mountains,
/// fog drifting and snow falling.</summary>
public enum BackdropStyle { Aurora, Sunset, Fanart, Waves, Glow, Projector, Pixel, Drive, Mist, Liquid, Snowfall }

/// <summary>Everything a theme decides. Colours are #RRGGBB or #AARRGGBB strings so Core
/// stays free of Avalonia; the Shell turns them into brushes.</summary>
public sealed record ThemeDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required HomeLayout Layout { get; init; }
    public required BackdropStyle Backdrop { get; init; }

    public required string Bg0 { get; init; }
    public required string Bg1 { get; init; }
    public required string BackdropA { get; init; }
    public required string BackdropB { get; init; }
    public required string BackdropC { get; init; }
    public required string Surface { get; init; }        // glass panel tint (with alpha)
    public required string SurfaceStrong { get; init; }  // dialogs, keyboard, settings panel
    public required string Line { get; init; }
    public required string Text { get; init; }
    public required string TextMuted { get; init; }
    public required string TextFaint { get; init; }
    public required string Accent { get; init; }
    public required string OnAccent { get; init; }
    public required string Focus { get; init; }          // focus ring / glow
    public string Danger { get; init; } = "#FF453A";
    public string Success { get; init; } = "#30D158";

    public double Radius { get; init; } = 18;
    public double RadiusLarge { get; init; } = 28;
    public double FocusScale { get; init; } = 1.08;
    public double SpringStiffness { get; init; } = 320;
    public double SpringDamping { get; init; } = 26;
    public bool UppercaseHeaders { get; init; }
    public double HeaderLetterSpacing { get; init; }
    public int TitleWeight { get; init; } = 700;
    /// <summary>Kodi-style themes show list views; others show poster grids.</summary>
    public bool PrefersLists { get; init; }
    public string AmbientPreset { get; init; } = "aurora";
    public string SoundSet { get; init; } = "aurora";

    /// <summary>Typefaces from Assets/Fonts by family name; null keeps Inter. The body font
    /// sets everything; the display font sets titles and headers.</summary>
    public string? BodyFont { get; init; }
    public string? DisplayFont { get; init; }

    /// <summary>Placeholder art (no poster yet, app monograms) is pulled toward this colour
    /// so it sits in the theme's palette; null keeps the entries' own colours.</summary>
    public string? ArtTone { get; init; }

    /// <summary>Panels are liquid glass: a bright rim catching light on the top edge and a
    /// sheen across the top, over a dark tint.</summary>
    public bool LiquidGlass { get; init; }

    /// <summary>The accent in force: the user's override, or the theme's own.</summary>
    public ThemeDefinition WithAccent(string? accent) =>
        string.IsNullOrWhiteSpace(accent) ? this : this with { Accent = accent!, Focus = accent! };
}

public static class Themes
{
    public static readonly ThemeDefinition Aurora = new()
    {
        Id = "aurora", Name = "Aurora", Layout = HomeLayout.TopTabsHero, Backdrop = BackdropStyle.Aurora,
        Bg0 = "#0B0D14", Bg1 = "#05060A",
        BackdropA = "#1E3A8A", BackdropB = "#0E7490", BackdropC = "#6D28D9",
        Surface = "#33FFFFFF", SurfaceStrong = "#E61C1F2A", Line = "#26FFFFFF",
        Text = "#F5F7FA", TextMuted = "#B3F5F7FA", TextFaint = "#66F5F7FA",
        Accent = "#0A84FF", OnAccent = "#FFFFFF", Focus = "#FFFFFF",
        Radius = 20, RadiusLarge = 30, FocusScale = 1.09, SpringStiffness = 340, SpringDamping = 24,
        AmbientPreset = "aurora", SoundSet = "aurora",
    };

    public static readonly ThemeDefinition LivingRoom = new()
    {
        Id = "livingroom", Name = "Living Room", Layout = HomeLayout.BottomRibbon, Backdrop = BackdropStyle.Sunset,
        Bg0 = "#140F14", Bg1 = "#0A070A",
        BackdropA = "#7C2D12", BackdropB = "#BE185D", BackdropC = "#1E1B4B",
        Surface = "#2EFFFFFF", SurfaceStrong = "#EB201820", Line = "#24FFFFFF",
        Text = "#FFF8F1", TextMuted = "#BFFFF8F1", TextFaint = "#66FFF8F1",
        Accent = "#FF5A5F", OnAccent = "#FFFFFF", Focus = "#FFFFFF",
        Radius = 14, RadiusLarge = 22, FocusScale = 1.12, SpringStiffness = 380, SpringDamping = 28,
        AmbientPreset = "livingroom", SoundSet = "livingroom",
    };

    public static readonly ThemeDefinition Kodi = new()
    {
        Id = "kodi", Name = "Kodi Classic", Layout = HomeLayout.SideMenu, Backdrop = BackdropStyle.Fanart,
        Bg0 = "#0D1621", Bg1 = "#070C12",
        BackdropA = "#12324A", BackdropB = "#0B1A27", BackdropC = "#1F5F8B",
        Surface = "#B30D1621", SurfaceStrong = "#F20F1B28", Line = "#2E8FB8DE",
        Text = "#ECF3FA", TextMuted = "#B3ECF3FA", TextFaint = "#66ECF3FA",
        Accent = "#12B2E7", OnAccent = "#04121C", Focus = "#12B2E7",
        Radius = 6, RadiusLarge = 10, FocusScale = 1.04, SpringStiffness = 420, SpringDamping = 32,
        UppercaseHeaders = true, HeaderLetterSpacing = 3, TitleWeight = 600, PrefersLists = true,
        AmbientPreset = "kodi", SoundSet = "kodi",
    };

    public static readonly ThemeDefinition ConsoleOne = new()
    {
        Id = "console", Name = "Console One", Layout = HomeLayout.IconRow, Backdrop = BackdropStyle.Waves,
        Bg0 = "#050B1F", Bg1 = "#02050F",
        BackdropA = "#0A1F5C", BackdropB = "#1240A8", BackdropC = "#03081A",
        Surface = "#2BFFFFFF", SurfaceStrong = "#EB0A1230", Line = "#22FFFFFF",
        Text = "#FFFFFF", TextMuted = "#B8FFFFFF", TextFaint = "#61FFFFFF",
        Accent = "#2E7BFF", OnAccent = "#FFFFFF", Focus = "#FFFFFF",
        Radius = 16, RadiusLarge = 24, FocusScale = 1.14, SpringStiffness = 300, SpringDamping = 24,
        TitleWeight = 300, AmbientPreset = "console", SoundSet = "console",
    };

    public static readonly ThemeDefinition Series = new()
    {
        Id = "series", Name = "Series", Layout = HomeLayout.TileGrid, Backdrop = BackdropStyle.Glow,
        Bg0 = "#0E0F10", Bg1 = "#070808",
        BackdropA = "#0F3D1A", BackdropB = "#107C10", BackdropC = "#1A1C1E",
        Surface = "#26FFFFFF", SurfaceStrong = "#F0161819", Line = "#21FFFFFF",
        Text = "#FFFFFF", TextMuted = "#B3FFFFFF", TextFaint = "#5CFFFFFF",
        Accent = "#107C10", OnAccent = "#FFFFFF", Focus = "#FFFFFF",
        Radius = 8, RadiusLarge = 12, FocusScale = 1.05, SpringStiffness = 450, SpringDamping = 30,
        TitleWeight = 600, AmbientPreset = "series", SoundSet = "series",
    };

    /// <summary>A film-house programme: serif title cards over typewritten (screenplay)
    /// text, projector light on warm black, marquee amber, square corners, quiet motion.</summary>
    public static readonly ThemeDefinition ScreeningRoom = new()
    {
        Id = "screening", Name = "Screening Room", Layout = HomeLayout.Programme, Backdrop = BackdropStyle.Projector,
        Bg0 = "#0E0C0A", Bg1 = "#060504",
        BackdropA = "#F4D9A6", BackdropB = "#7A4A1C", BackdropC = "#120E0A",
        Surface = "#1AF2E6D2", SurfaceStrong = "#F5110F0D", Line = "#38F2E6D2",
        Text = "#F2E6D2", TextMuted = "#B0F2E6D2", TextFaint = "#5CF2E6D2",
        Accent = "#E8A33D", OnAccent = "#170F05", Focus = "#E8A33D",
        Danger = "#E5533D", Success = "#9DBF6A",
        Radius = 2, RadiusLarge = 3, FocusScale = 1.03, SpringStiffness = 240, SpringDamping = 26,
        TitleWeight = 400, HeaderLetterSpacing = 0,
        BodyFont = "IBM Plex Mono", DisplayFont = "Instrument Serif", ArtTone = "#6B4A2B",
        AmbientPreset = "screening", SoundSet = "screening",
    };

    /// <summary>An arcade cabinet: pixel type, a pixel-art sunset grid, arcade yellow on deep
    /// purple, square corners, snappy motion and chiptune sounds.</summary>
    public static readonly ThemeDefinition Arcade = new()
    {
        Id = "arcade", Name = "Arcade", Layout = HomeLayout.Cabinet, Backdrop = BackdropStyle.Pixel,
        Bg0 = "#0B0420", Bg1 = "#05020F",
        BackdropA = "#1A0B3D", BackdropB = "#FF2A6D", BackdropC = "#05D9E8",
        Surface = "#2EFFFFFF", SurfaceStrong = "#F2140A2E", Line = "#6605D9E8",
        Text = "#FFFFFF", TextMuted = "#CCD7DCFF", TextFaint = "#808C93C8",
        Accent = "#FFD319", OnAccent = "#1A0B3D", Focus = "#FFD319",
        Danger = "#FF2A6D", Success = "#39FF14",
        Radius = 0, RadiusLarge = 0, FocusScale = 1.04, SpringStiffness = 900, SpringDamping = 42,
        UppercaseHeaders = true, HeaderLetterSpacing = 1, TitleWeight = 400,
        BodyFont = "Pixelify Sans", DisplayFont = "Press Start 2P", ArtTone = "#3A1F7A",
        AmbientPreset = "arcade", SoundSet = "arcade",
    };

    /// <summary>A car's dashboard display: true black, the system's dark greys and blue,
    /// rounded icons, a status sidebar, crisp quiet clicks.</summary>
    public static readonly ThemeDefinition CarPlay = new()
    {
        Id = "carplay", Name = "Motorcar Recital", Layout = HomeLayout.Dock, Backdrop = BackdropStyle.Drive,
        Bg0 = "#000000", Bg1 = "#000000",
        BackdropA = "#0A3D91", BackdropB = "#5E2BB8", BackdropC = "#0C7FA8",
        Surface = "#2C2C2E", SurfaceStrong = "#F21C1C1E", Line = "#38545458",
        Text = "#FFFFFF", TextMuted = "#99EBEBF5", TextFaint = "#4DEBEBF5",
        Accent = "#0A84FF", OnAccent = "#FFFFFF", Focus = "#FFFFFF",
        Radius = 16, RadiusLarge = 26, FocusScale = 1.04, SpringStiffness = 520, SpringDamping = 34,
        TitleWeight = 700,
        AmbientPreset = "carplay", SoundSet = "carplay",
    };

    /// <summary>A light TV launcher: pale grey glass and near-black type, white cards, a
    /// blue accent, soft round corners and calm motion. The only light theme.</summary>
    public static readonly ThemeDefinition Daylight = new()
    {
        Id = "daylight", Name = "Daylight", Layout = HomeLayout.Glance, Backdrop = BackdropStyle.Mist,
        Bg0 = "#ECEDF1", Bg1 = "#E3E4EA",
        BackdropA = "#C7D7F5", BackdropB = "#F3D3E2", BackdropC = "#CFEDE3",
        Surface = "#B8FFFFFF", SurfaceStrong = "#FFF6F7FA", Line = "#1A202124",
        Text = "#202124", TextMuted = "#B3202124", TextFaint = "#73202124",
        Accent = "#1A73E8", OnAccent = "#FFFFFF", Focus = "#202124",
        Danger = "#D93025", Success = "#188038",
        Radius = 18, RadiusLarge = 26, FocusScale = 1.06, SpringStiffness = 360, SpringDamping = 28,
        TitleWeight = 600,
        AmbientPreset = "daylight", SoundSet = "daylight",
    };

    /// <summary>A dark TV home under liquid glass: near-black, white type, panels with bright
    /// rims and a sheen, very round corners, soft springy motion.</summary>
    public static readonly ThemeDefinition Liquid = new()
    {
        Id = "liquid", Name = "Liquid Glass", Layout = HomeLayout.Sidebar, Backdrop = BackdropStyle.Liquid,
        Bg0 = "#0A0A0C", Bg1 = "#050506",
        BackdropA = "#C2661F", BackdropB = "#3B1E78", BackdropC = "#0E5566",
        Surface = "#26FFFFFF", SurfaceStrong = "#EB1A1A1F", Line = "#2EFFFFFF",
        Text = "#FFFFFF", TextMuted = "#B8FFFFFF", TextFaint = "#6BFFFFFF",
        Accent = "#0A84FF", OnAccent = "#FFFFFF", Focus = "#FFFFFF",
        Radius = 22, RadiusLarge = 34, FocusScale = 1.08, SpringStiffness = 300, SpringDamping = 22,
        TitleWeight = 700, LiquidGlass = true,
        AmbientPreset = "liquid", SoundSet = "liquid",
    };

    /// <summary>A northern saga: smoked-glass panels on a snowy night, light condensed
    /// uppercase type, silver knotwork and runes, square corners, no lift on focus (the
    /// choice brightens and a marker appears beside it), deep wooden sounds.</summary>
    public static readonly ThemeDefinition Saga = new()
    {
        Id = "saga", Name = "Saga", Layout = HomeLayout.Hall, Backdrop = BackdropStyle.Snowfall,
        Bg0 = "#0B0C0E", Bg1 = "#060708",
        BackdropA = "#1E2630", BackdropB = "#8996A5", BackdropC = "#12161C",
        Surface = "#8C000000", SurfaceStrong = "#F8080A0C", Line = "#4DC9CED4",
        Text = "#EDEDEA", TextMuted = "#A8EDEDEA", TextFaint = "#5CEDEDEA",
        Accent = "#56697C", OnAccent = "#FFFFFF", Focus = "#E6E6E2",
        Danger = "#B8483A", Success = "#7FA06A",
        Radius = 0, RadiusLarge = 0, FocusScale = 1.03, SpringStiffness = 420, SpringDamping = 34,
        UppercaseHeaders = true, HeaderLetterSpacing = 3, TitleWeight = 300,
        BodyFont = "Barlow Condensed", DisplayFont = "Barlow Condensed", ArtTone = "#2B3138",
        AmbientPreset = "saga", SoundSet = "saga",
    };

    public static readonly IReadOnlyList<ThemeDefinition> All = [Aurora, LivingRoom, Kodi, ConsoleOne, Series, ScreeningRoom, Arcade, CarPlay, Daylight, Liquid, Saga];

    public static ThemeDefinition Get(string id) => All.FirstOrDefault(t => t.Id == id) ?? Aurora;
}
