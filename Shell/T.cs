using Avalonia;
using Avalonia.Media;
using Lounge.Core.Themes;

namespace Lounge.Shell;

/// <summary>The theme in force, as brushes and metrics. Screens read these when they
/// are built; a theme change rebuilds the visible screens.</summary>
public static class T
{
    public static ThemeDefinition Def { get; private set; } = Themes.Aurora;

    public static IBrush Bg0 { get; private set; } = Brushes.Black;
    public static IBrush Bg1 { get; private set; } = Brushes.Black;
    public static IBrush Surface { get; private set; } = Brushes.Transparent;
    public static IBrush SurfaceStrong { get; private set; } = Brushes.Black;
    public static IBrush Line { get; private set; } = Brushes.Gray;
    public static IBrush Text { get; private set; } = Brushes.White;
    public static IBrush TextMuted { get; private set; } = Brushes.White;
    public static IBrush TextFaint { get; private set; } = Brushes.White;
    public static IBrush Accent { get; private set; } = Brushes.DodgerBlue;
    public static IBrush OnAccent { get; private set; } = Brushes.White;
    public static IBrush Focus { get; private set; } = Brushes.White;
    public static IBrush Danger { get; private set; } = Brushes.Red;
    public static IBrush Success { get; private set; } = Brushes.Green;

    public static Color AccentColor { get; private set; }
    public static Color FocusColor { get; private set; }
    public static Color TextColor { get; private set; }
    public static Color Bg0Color { get; private set; }

    public static double Radius => Def.Radius;
    public static double RadiusLarge => Def.RadiusLarge;

    private const string Fallback = "fonts:Inter#Inter, Segoe UI";

    /// <summary>The theme's body typeface (Inter unless the theme ships its own).</summary>
    public static FontFamily Font { get; private set; } = new(Fallback);

    /// <summary>The theme's title typeface; the body font when the theme has none.</summary>
    public static FontFamily DisplayFont { get; private set; } = new(Fallback);

    public static bool HasDisplayFont => Def.DisplayFont is not null;

    /// <summary>Fonts ship in Assets/Fonts of whichever assembly is running (Lounge, or the
    /// preview and check tools that compile the same sources), with Inter behind them for
    /// any symbol the theme's font lacks.</summary>
    private static FontFamily Family(string? name) => name is null
        ? new FontFamily(Fallback)
        : new FontFamily($"avares://{typeof(T).Assembly.GetName().Name}/Assets/Fonts#{name}, {Fallback}");

    // Type scale on the 1920x1080 grid. Whole pixels only: fractional metrics are what
    // made Cogas's text look soft. Nothing below 22.
    public const double Display = 64, Title = 44, Heading = 32, Body = 26, Caption = 22;

    public static event Action? Changed;

    public static Color Parse(string hex) => Color.Parse(hex);

    public static void Apply(ThemeDefinition def, string? accentOverride)
    {
        Def = def.WithAccent(accentOverride);
        Bg0 = B(Def.Bg0); Bg1 = B(Def.Bg1);
        Surface = B(Def.Surface); SurfaceStrong = B(Def.SurfaceStrong);
        Line = B(Def.Line);
        Text = B(Def.Text); TextMuted = B(Def.TextMuted); TextFaint = B(Def.TextFaint);
        Accent = B(Def.Accent); OnAccent = B(Def.OnAccent); Focus = B(Def.Focus);
        Danger = B(Def.Danger); Success = B(Def.Success);
        AccentColor = Parse(Def.Accent);
        FocusColor = Parse(Def.Focus);
        TextColor = Parse(Def.Text);
        Bg0Color = Parse(Def.Bg0);
        Font = Family(Def.BodyFont);
        DisplayFont = Def.DisplayFont is null ? Font : Family(Def.DisplayFont);
        Changed?.Invoke();
    }

    public static IBrush B(string hex) => new SolidColorBrush(Parse(hex)).ToImmutable();

    /// <summary>A placeholder colour in the theme's palette: mixed toward the theme's art
    /// tone, keeping a trace of the original hue so entries still tell apart.</summary>
    public static Color Tone(Color c)
    {
        if (Def.ArtTone is not { } hex) return c;
        var t = Parse(hex);
        const double keep = 0.28;
        byte Mix(byte from, byte to) => (byte)Math.Round(from * keep + to * (1 - keep));
        return Color.FromArgb(c.A, Mix(c.R, t.R), Mix(c.G, t.G), Mix(c.B, t.B));
    }

    public static IBrush WithAlpha(Color c, double a) => new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B)).ToImmutable();

    public static FontWeight TitleWeight => (FontWeight)Def.TitleWeight;

    public static CornerRadius R(double r) => new(r);

    public static string Header(string text) => Def.UppercaseHeaders ? text.ToUpperInvariant() : text;
}
