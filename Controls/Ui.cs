using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lounge.Shell;

namespace Lounge.Controls;

/// <summary>Small builders so screens read as layout rather than property soup.</summary>
public static class Ui
{
    public static TextBlock Text(string text, double size = T.Body, IBrush? brush = null, FontWeight weight = FontWeight.Medium,
        TextWrapping wrap = TextWrapping.NoWrap, double letterSpacing = 0)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush ?? T.Text,
            FontWeight = weight,
            TextWrapping = wrap,
            TextTrimming = wrap == TextWrapping.NoWrap ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            LetterSpacing = letterSpacing,
        };
        // Titles (heading size and up, in the theme's title weight) take the display face.
        if (T.HasDisplayFont && size >= T.Heading && weight == T.TitleWeight) Display(block);
        return block;
    }

    /// <summary>Sets a text in the theme's display typeface. Display faces often come in a
    /// single weight, so the weight goes back to normal rather than being faked.</summary>
    public static TextBlock Display(TextBlock block, bool italic = false)
    {
        if (!T.HasDisplayFont) return block;
        block.FontFamily = T.DisplayFont;
        block.FontWeight = FontWeight.Normal;
        block.LetterSpacing = 0;
        if (italic) block.FontStyle = FontStyle.Italic;
        return block;
    }

    public static TextBlock Header(string text, double size = T.Heading) =>
        Display(Text(T.Header(text), size, T.Text, T.TitleWeight, letterSpacing: T.Def.HeaderLetterSpacing));

    public static TextBlock Muted(string text, double size = T.Caption, TextWrapping wrap = TextWrapping.NoWrap) =>
        Text(text, size, T.TextMuted, FontWeight.Medium, wrap);

    public static StackPanel H(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static StackPanel V(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static T Margin<T>(this T c, double l, double t, double r, double b) where T : Control { c.Margin = new Thickness(l, t, r, b); return c; }
    public static T Margin<T>(this T c, double all) where T : Control { c.Margin = new Thickness(all); return c; }
    public static T Margin<T>(this T c, double horizontal, double vertical) where T : Control { c.Margin = new Thickness(horizontal, vertical); return c; }
    public static T Align<T>(this T c, HorizontalAlignment h, VerticalAlignment v) where T : Control { c.HorizontalAlignment = h; c.VerticalAlignment = v; return c; }
    public static T Col<T>(this T c, int col, int span = 1) where T : Control { Avalonia.Controls.Grid.SetColumn(c, col); Avalonia.Controls.Grid.SetColumnSpan(c, span); return c; }
    public static T Row<T>(this T c, int row, int span = 1) where T : Control { Avalonia.Controls.Grid.SetRow(c, row); Avalonia.Controls.Grid.SetRowSpan(c, span); return c; }

    public static Grid Grid(string cols, string rows)
    {
        return new Grid { ColumnDefinitions = new ColumnDefinitions(cols), RowDefinitions = new RowDefinitions(rows) };
    }

    /// <summary>A frosted panel: the theme's glass tint over a backdrop blur.</summary>
    public static Border Glass(Control? child = null, double radius = -1, bool strong = false)
    {
        return new Border
        {
            Background = strong ? T.SurfaceStrong : T.Surface,
            BorderBrush = Rim(),
            BorderThickness = new Thickness(T.Def.LiquidGlass ? 1.5 : 1),
            CornerRadius = new CornerRadius(radius < 0 ? T.RadiusLarge : radius),
            Child = child,
        };
    }

    /// <summary>A panel's edge: the theme's line, or under liquid glass a rim that catches the
    /// light bright along the top-left, fades down the sides and glints again bottom-right.</summary>
    public static IBrush Rim() => !T.Def.LiquidGlass ? T.Line : new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(150, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(28, 255, 255, 255), 0.35),
            new GradientStop(Color.FromArgb(18, 255, 255, 255), 0.7),
            new GradientStop(Color.FromArgb(90, 255, 255, 255), 1),
        },
    };

    /// <summary>The sheen across the top of a liquid glass panel (null for other themes).</summary>
    public static IBrush? Sheen() => !T.Def.LiquidGlass ? null : VerticalFade(Colors.White, 0.13, 0, 0, 0.45);

    public static IBrush VerticalFade(Color c, double topAlpha, double bottomAlpha, double start = 0, double end = 1) =>
        new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, start, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, end, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(topAlpha * 255), c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb((byte)(bottomAlpha * 255), c.R, c.G, c.B), 1),
            },
        };

    public static IBrush HorizontalFade(Color c, double leftAlpha, double rightAlpha) =>
        new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(leftAlpha * 255), c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb((byte)(rightAlpha * 255), c.R, c.G, c.B), 1),
            },
        };

    public static string Time(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>A two-letter monogram for tiles without art, in a brand colour.</summary>
    public static Border Monogram(string name, string color, double size)
    {
        var letters = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
        Color c;
        try { c = Color.Parse(color); } catch (FormatException) { c = Colors.SlateGray; }
        c = T.Tone(c);
        var light = Color.FromArgb(255, (byte)Math.Min(255, c.R + 40), (byte)Math.Min(255, c.G + 40), (byte)Math.Min(255, c.B + 40));
        return new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(light, 0), new GradientStop(c, 1) },
            },
            Child = new TextBlock
            {
                Text = letters.Length == 0 ? "?" : letters,
                FontSize = Math.Max(22, Math.Round(size * 0.32)),
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }
}
