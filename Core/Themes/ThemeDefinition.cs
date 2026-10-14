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
}

/// <summary>A procedural default backdrop, so themes need no shipped wallpapers.</summary>
public enum BackdropStyle { Aurora, Sunset, Fanart, Waves, Glow }

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

    public static readonly IReadOnlyList<ThemeDefinition> All = [Aurora, LivingRoom, Kodi, ConsoleOne, Series];

    public static ThemeDefinition Get(string id) => All.FirstOrDefault(t => t.Id == id) ?? Aurora;
}
