using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lounge.Shell;
using Path = Avalonia.Controls.Shapes.Path;

namespace Lounge.Controls;

/// <summary>The Saga theme's northern ornaments, drawn as lines: Elder Futhark runes, a
/// divider that fades out both ways from a knot, the diamond that marks a choice, and the
/// dark box framed above and below by those dividers.</summary>
public static class Nordic
{
    // On a 24 grid, straight strokes like runes cut in wood or stone.
    private static readonly Dictionary<string, string> Runes = new()
    {
        ["fehu"] = "M9 2v20 M9 8l7-5 M9 14l7-5",
        ["ansuz"] = "M9 2v20 M9 3l7 5 M9 9l7 5",
        ["raidho"] = "M8 2v20 M8 2l8 5-8 5 M8 12l9 10",
        ["kenaz"] = "M16 4l-8 8 8 8",
        ["algiz"] = "M12 4v18 M12 12L6 4 M12 12l6-8",
        ["tiwaz"] = "M12 3v19 M12 3L6 9 M12 3l6 6",
        ["othala"] = "M12 2l6 6L6 20 M12 2L6 8l12 12",
        ["dagaz"] = "M4 5v14L20 5v14z",
        ["mannaz"] = "M6 2v20 M18 2v20 M6 3l12 9 M18 3L6 12",
        ["ingwaz"] = "M12 3l6 9-6 9-6-9z",
        ["sowilo"] = "M15 3L8 10l8 4-7 7",
    };

    /// <summary>Each destination's rune, chosen for its old meaning: day (dagaz) for videos,
    /// the god's voice (ansuz) for music, the torch (kenaz) for games, the journey (raidho)
    /// for the web, the homestead (othala) for files, Tyr's order (tiwaz) for settings, the
    /// guard (algiz) for power, the sun (sowilo) for what is playing.</summary>
    public static string RuneFor(string destination) => destination switch
    {
        "Now Playing" => "sowilo",
        "Videos" => "dagaz",
        "Music" => "ansuz",
        "Apps & Games" => "kenaz",
        "Web" => "raidho",
        "Files" => "othala",
        "Settings" => "tiwaz",
        "Power" => "algiz",
        _ => "ingwaz",
    };

    /// <summary>The ornaments' silver: the theme's text, dimmed.</summary>
    public static Color Silver => Color.FromArgb(200, T.TextColor.R, T.TextColor.G, T.TextColor.B);

    private static Control Lines(string data, double size, IBrush brush, double stroke) => new Viewbox
    {
        Width = size, Height = size, Stretch = Stretch.Uniform,
        Child = new Canvas
        {
            Width = 24, Height = 24,
            Children =
            {
                new Path
                {
                    Data = Geometry.Parse(data), Stroke = brush, StrokeThickness = stroke,
                    StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                },
            },
        },
    };

    public static Control Rune(string name, double size, IBrush brush, double stroke = 1.7) =>
        Lines(Runes.GetValueOrDefault(name) ?? Runes["ingwaz"], size, brush, stroke);

    /// <summary>A knot: a diamond within a diamond, with a tick each side.</summary>
    public static Control Knot(double size, IBrush? brush = null) =>
        Lines("M12 5l7 7-7 7-7-7z M12 9l3 3-3 3-3-3z M1 12h3 M20 12h3", size, brush ?? new SolidColorBrush(Silver), 1.3);

    /// <summary>The marker beside a chosen line: a small diamond trailing a short stroke,
    /// pointing right, or left when flipped.</summary>
    public static Control Marker(double size, bool flip = false)
    {
        var mark = Lines("M3 12l4-4 4 4-4 4z M11 12h10", size, new SolidColorBrush(Silver), 1.6);
        if (flip) mark.RenderTransform = new ScaleTransform(-1, 1);
        mark.VerticalAlignment = VerticalAlignment.Center;
        return mark;
    }

    /// <summary>A fine rule that fades out both ways from a knot in the middle.</summary>
    public static Control Divider(double knot = 18)
    {
        var s = Silver;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), IsHitTestVisible = false };
        grid.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Background = Ui.HorizontalFade(s, 0, 0.75) });
        grid.Children.Add(Knot(knot).Margin(6, 0).Col(1));
        grid.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Background = Ui.HorizontalFade(s, 0.75, 0) }.Col(2));
        return grid;
    }

    /// <summary>A rule fading in from the top and out at the bottom, between columns.</summary>
    public static Control Upright() => new Border
    {
        Width = 1, IsHitTestVisible = false,
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, Silver.R, Silver.G, Silver.B), 0),
                new GradientStop(Color.FromArgb(110, Silver.R, Silver.G, Silver.B), 0.5),
                new GradientStop(Color.FromArgb(0, Silver.R, Silver.G, Silver.B), 1),
            },
        },
    };

    /// <summary>A box of smoked glass, darkest in the middle, with a knotted rule above and below.</summary>
    public static Control Frame(Control child, double padX = 34, double padY = 26)
    {
        var grid = Ui.Grid("*", "Auto,*,Auto");
        grid.Children.Add(new Border
        {
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(70, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(170, 0, 0, 0), 0.5),
                    new GradientStop(Color.FromArgb(70, 0, 0, 0), 1),
                },
            },
        }.Row(0, 3));
        grid.Children.Add(Divider().Row(0));
        grid.Children.Add(child.Margin(padX, padY).Row(1));
        grid.Children.Add(Divider().Row(2));
        return grid;
    }
}
