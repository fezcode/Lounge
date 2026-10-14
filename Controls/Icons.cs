using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Lounge.Controls;

/// <summary>Lounge's line icons: drawn on a 24 grid with round 1.8 strokes, in the spirit
/// of SF Symbols. A few transport glyphs are filled.</summary>
public static class Icons
{
    private static readonly Dictionary<string, (string Path, bool Filled)> Paths = new()
    {
        ["home"] = ("M3 10.5 12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-6h-6v6H4a1 1 0 0 1-1-1z", false),
        ["video"] = ("M3 5h18v14H3z M7 5v14 M17 5v14 M3 9.5h4 M3 14.5h4 M17 9.5h4 M17 14.5h4", false),
        ["music"] = ("M9 18V5l12-2v13 M9 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0z M21 16a3 3 0 1 1-6 0 3 3 0 0 1 6 0z", false),
        ["apps"] = ("M4 4h6v6H4z M14 4h6v6h-6z M4 14h6v6H4z M14 14h6v6h-6z", false),
        ["gamepad"] = ("M6.5 11h4 M8.5 9v4 M15 12h.01 M18 10h.01 M17.3 5H6.7a4 4 0 0 0-4 3.6l-.9 7.7A2.5 2.5 0 0 0 4.3 19c.9 0 1.8-.5 2.2-1.4L8 15h8l1.5 2.6c.4.9 1.3 1.4 2.2 1.4a2.5 2.5 0 0 0 2.5-2.8l-.9-7.6A4 4 0 0 0 17.3 5z", false),
        ["web"] = ("M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z M3 12h18 M12 3c2.5 2.5 3.8 5.6 3.8 9s-1.3 6.5-3.8 9c-2.5-2.5-3.8-5.6-3.8-9S9.5 5.5 12 3z", false),
        ["folder"] = ("M3 6.5A1.5 1.5 0 0 1 4.5 5H9l2 2.5h8.5A1.5 1.5 0 0 1 21 9v9.5a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5z", false),
        ["settings"] = ("M4 6h9 M17 6h3 M4 12h3 M11 12h9 M4 18h11 M19 18h1 M15 4v4 M9 10v4 M17 16v4", false),
        ["play"] = ("M7 4.5v15l13-7.5z", true),
        ["pause"] = ("M6.5 4h4v16h-4z M13.5 4h4v16h-4z", true),
        ["stop"] = ("M6 6h12v12H6z", true),
        ["next"] = ("M5 5v14l10-7z M17 5h2.5v14H17z", true),
        ["previous"] = ("M19 5v14L9 12z M4.5 5H7v14H4.5z", true),
        ["forward"] = ("M3 6v12l8.5-6z M12.5 6v12l8.5-6z", true),
        ["rewind"] = ("M21 6v12l-8.5-6z M11.5 6v12L3 12z", true),
        ["back"] = ("M15 5l-7 7 7 7", false),
        ["chevron"] = ("M9 5l7 7-7 7", false),
        ["chevron-down"] = ("M5 9l7 7 7-7", false),
        ["search"] = ("M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14z M21 21l-5-5", false),
        ["plus"] = ("M12 5v14 M5 12h14", false),
        ["check"] = ("M5 12.5l4.5 4.5L19 7", false),
        ["close"] = ("M6 6l12 12 M18 6L6 18", false),
        ["trash"] = ("M4 7h16 M9 7V4h6v3 M6 7l1 13h10l1-13 M10 11v5 M14 11v5", false),
        ["pencil"] = ("M4 20h4L19 9l-4-4L4 16z M14 6l4 4", false),
        ["copy"] = ("M8 8h12v12H8z M16 8V4H4v12h4", false),
        ["cut"] = ("M6 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M6 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M8.5 7.5 20 19 M8.5 16.5 20 5", false),
        ["paste"] = ("M9 3h6v3H9z M8 4.5H5v16.5h14V4.5h-3", false),
        ["info"] = ("M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z M12 11v6 M12 7.5v.01", false),
        ["subtitles"] = ("M3 5h18v14H3z M7 13h4 M13 13h4 M7 16h10", false),
        ["audio"] = ("M4 9h4l5-4v14l-5-4H4z M16 9a4 4 0 0 1 0 6 M18.5 6.5a8 8 0 0 1 0 11", false),
        ["mute"] = ("M4 9h4l5-4v14l-5-4H4z M17 9.5l5 5 M22 9.5l-5 5", false),
        ["drive"] = ("M3 13h18v6H3z M5 13l2.5-8h9L19 13 M7 16h.01", false),
        ["usb"] = ("M8 2h8v6H8z M6 8h12v8a6 6 0 0 1-12 0z M10 4.5v1 M14 4.5v1", false),
        ["network"] = ("M3 13h18v6H3z M7 16h.01 M12 13V5 M8 5h8", false),
        ["image"] = ("M3 5h18v14H3z M3 16l5-5 5 5 3-3 5 5 M15.5 8.5h.01", false),
        ["document"] = ("M6 3h8l4 4v14H6z M14 3v4h4", false),
        ["file"] = ("M6 3h8l4 4v14H6z M14 3v4h4", false),
        ["download"] = ("M12 4v11 M7 10l5 5 5-5 M5 20h14", false),
        ["desktop"] = ("M3 4h18v12H3z M8 20h8 M12 16v4", false),
        ["power"] = ("M12 3v9 M6.3 6.3a8 8 0 1 0 11.4 0", false),
        ["shuffle"] = ("M3 7h4l10 10h4 M3 17h4l3-3 M14 10l3-3h4 M18 4l3 3-3 3 M18 14l3 3-3 3", false),
        ["repeat"] = ("M4 12V9a3 3 0 0 1 3-3h12 M16 3l3 3-3 3 M20 12v3a3 3 0 0 1-3 3H5 M8 21l-3-3 3-3", false),
        ["clock"] = ("M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z M12 7v5l3 2", false),
        ["tv"] = ("M3 6h18v12H3z M8 21h8", false),
        ["sun"] = ("M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8z M12 2v2 M12 20v2 M4.9 4.9l1.4 1.4 M17.7 17.7l1.4 1.4 M2 12h2 M20 12h2 M4.9 19.1l1.4-1.4 M17.7 6.3l1.4-1.4", false),
        ["speed"] = ("M12 14l4-4 M4.5 18a9 9 0 1 1 15 0", false),
        ["eye"] = ("M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z", false),
        ["eye-off"] = ("M3 3l18 18 M10.6 5.1A10 10 0 0 1 12 5c6.5 0 10 7 10 7a17 17 0 0 1-3 3.8 M6.6 6.6C3.7 8.4 2 12 2 12s3.5 7 10 7a9.6 9.6 0 0 0 5.4-1.6", false),
        ["move"] = ("M12 3v18 M3 12h18 M9 6l3-3 3 3 M9 18l3 3 3-3 M6 9l-3 3 3 3 M18 9l3 3-3 3", false),
        ["refresh"] = ("M20 11a8 8 0 1 0-2.3 5.7 M20 4v7h-7", false),
        ["keyboard"] = ("M3 6h18v12H3z M7 10h.01 M11 10h.01 M15 10h.01 M7 14h10", false),
        ["star"] = ("M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1-4.4-4.3 6.1-.9z", false),
        ["list"] = ("M8 6h13 M8 12h13 M8 18h13 M3.5 6h.01 M3.5 12h.01 M3.5 18h.01", false),
        ["grid"] = ("M4 4h6v6H4z M14 4h6v6h-6z M4 14h6v6H4z M14 14h6v6h-6z", false),
        ["new-folder"] = ("M3 6.5A1.5 1.5 0 0 1 4.5 5H9l2 2.5h8.5A1.5 1.5 0 0 1 21 9v9.5a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5z M12 11v6 M9 14h6", false),
        ["library-add"] = ("M4 6h12 M4 11h12 M4 16h7 M18 14v6 M15 17h6", false),
        ["external"] = ("M14 4h6v6 M20 4l-9 9 M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5", false),
        ["mouse"] = ("M12 3a6 6 0 0 0-6 6v6a6 6 0 0 0 12 0V9a6 6 0 0 0-6-6z M12 7v4", false),
        ["chapters"] = ("M4 5h16 M4 12h16 M4 19h16 M8 3v4 M14 10v4 M10 17v4", false),
        ["album"] = ("M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z", false),
        ["person"] = ("M12 4a4 4 0 1 0 0 8 4 4 0 0 0 0-8z M4 21a8 8 0 0 1 16 0", false),
        ["view"] = ("M9 9h10v10H9z M5 15V5h10", false),
        ["menu"] = ("M5 7h14 M5 12h14 M5 17h14", false),
        ["warning"] = ("M12 3l10 18H2z M12 10v5 M12 18v.01", false),
    };

    public static Geometry Geometry(string name) =>
        Avalonia.Media.Geometry.Parse(Paths.TryGetValue(name, out var p) ? p.Path : Paths["info"].Path);

    public static bool IsFilled(string name) => Paths.TryGetValue(name, out var p) && p.Filled;

    public static bool Exists(string name) => Paths.ContainsKey(name);

    public static IEnumerable<string> Names => Paths.Keys;

    /// <summary>An icon sized to <paramref name="size"/> px in <paramref name="brush"/>.</summary>
    public static Control Make(string name, double size, IBrush brush, double stroke = 1.8)
    {
        var filled = IsFilled(name);
        var path = new Path
        {
            Data = Geometry(name),
            Stretch = Stretch.None,
            StrokeThickness = filled ? 0 : stroke,
            Stroke = filled ? null : brush,
            Fill = filled ? brush : null,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = new Canvas { Width = 24, Height = 24, Children = { path } },
        };
    }
}
