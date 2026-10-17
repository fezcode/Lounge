using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Lounge.Core.Input;
using Lounge.Shell;
using Path = Avalonia.Controls.Shapes.Path;

namespace Lounge.Controls;

/// <summary>An Xbox Series controller, drawn on a 900 x 640 canvas: triggers and bumpers
/// behind the body, recessed stick wells, the D-pad plate, coloured face buttons, View,
/// Share and Menu, and the Xbox button. Screens put their own button cards on the
/// <see cref="Hotspots"/> (Remote: what each does; Controllers: which one to press).</summary>
public static class ControllerArt
{
    public const double Width = 900, Height = 640;

    /// <summary>Where each button sits: top-left, size and corner radius.</summary>
    public static readonly (PadButton Button, double X, double Y, double W, double H, double R)[] Hotspots =
    [
        (PadButton.LT, 196, 44, 112, 50, 20), (PadButton.RT, 592, 44, 112, 50, 20),
        (PadButton.LB, 168, 104, 164, 34, 17), (PadButton.RB, 568, 104, 164, 34, 17),
        (PadButton.LeftStick, 180, 186, 104, 104, 52), (PadButton.RightStick, 514, 318, 104, 104, 52),
        (PadButton.DPadUp, 308, 306, 44, 44, 9), (PadButton.DPadDown, 308, 390, 44, 44, 9),
        (PadButton.DPadLeft, 266, 348, 44, 44, 9), (PadButton.DPadRight, 350, 348, 44, 44, 9),
        (PadButton.Y, 640, 150, 56, 56, 28), (PadButton.A, 640, 266, 56, 56, 28),
        (PadButton.X, 582, 208, 56, 56, 28), (PadButton.B, 698, 208, 56, 56, 28),
        (PadButton.View, 372, 222, 40, 40, 20), (PadButton.Menu, 488, 222, 40, 40, 20),
        (PadButton.Guide, 416, 140, 68, 68, 34),
    ];

    private const string Body =
        "M 300,130 C 360,116 540,116 600,130 C 690,122 770,146 814,206 C 860,276 886,402 890,484 " +
        "C 896,566 856,614 804,614 C 760,614 736,584 710,548 C 678,502 648,472 598,468 L 302,468 " +
        "C 252,472 222,502 190,548 C 164,584 140,614 96,614 C 44,614 4,566 10,484 " +
        "C 14,402 40,276 86,206 C 130,146 210,122 300,130 Z";

    private const string LeftTrigger = "M 192,112 C 194,66 216,42 252,42 L 276,42 C 300,42 314,62 312,112 Z";
    private const string LeftBumper = "M 152,168 C 170,130 218,108 280,104 L 336,104 C 344,116 344,130 334,140 C 276,138 216,150 164,180 Z";

    /// <summary>The same shape on the right: x mirrored about the middle.</summary>
    private static string Mirror(string path) => System.Text.RegularExpressions.Regex.Replace(path, @"(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)",
        m => $"{Width - double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture):0.##},{m.Groups[2].Value}");

    private static IBrush Shade(byte a) => new SolidColorBrush(Color.FromArgb(a, 255, 255, 255));
    private static IBrush Dark(byte a) => new SolidColorBrush(Color.FromArgb(a, 0, 0, 0));

    /// <summary>Draws everything but the buttons themselves.</summary>
    public static void DrawBody(Canvas canvas)
    {
        foreach (var shape in new[] { LeftTrigger, Mirror(LeftTrigger) })
            canvas.Children.Add(new Path { Data = Geometry.Parse(shape), Fill = Shade(26), Stroke = T.Line, StrokeThickness = 1.5 });
        foreach (var shape in new[] { LeftBumper, Mirror(LeftBumper) })
            canvas.Children.Add(new Path { Data = Geometry.Parse(shape), Fill = Shade(34), Stroke = T.Line, StrokeThickness = 1.5 });

        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse(Body),
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(64, 255, 255, 255), 0), new GradientStop(Color.FromArgb(30, 255, 255, 255), 0.55), new GradientStop(Color.FromArgb(16, 255, 255, 255), 1) },
            },
            Stroke = T.Line, StrokeThickness = 2,
        });
        // A soft highlight along the top edge, and the seam where the grips begin.
        canvas.Children.Add(new Path { Data = Geometry.Parse("M 304,144 C 380,132 520,132 596,144"), Stroke = Shade(60), StrokeThickness = 2, StrokeLineCap = PenLineCap.Round });
        canvas.Children.Add(new Path { Data = Geometry.Parse("M 120,330 C 150,420 170,470 190,520 M 780,330 C 750,420 730,470 710,520"), Stroke = Shade(18), StrokeThickness = 2, StrokeLineCap = PenLineCap.Round });

        // Recesses: stick wells, the D-pad plate, the face-button plate.
        Well(canvas, 232, 238, 68);
        Well(canvas, 566, 370, 68);
        Well(canvas, 330, 370, 72, 26);
        canvas.Children.Add(new Ellipse { Width = 196, Height = 196, Fill = Dark(18), [Canvas.LeftProperty] = 570.0, [Canvas.TopProperty] = 138.0 });
        canvas.Children.Add(new Path { Data = Geometry.Parse("M 308,306 h 44 v 42 h 42 v 44 h -42 v 42 h -44 v -42 h -42 v -44 h 42 Z"), Fill = Shade(30), Stroke = T.Line, StrokeThickness = 1 });

        // Share sits between View and Menu, lower down; Lounge doesn't use it.
        canvas.Children.Add(new Border { Width = 30, Height = 18, CornerRadius = new CornerRadius(9), Background = Shade(30), BorderBrush = T.Line, BorderThickness = new Thickness(1), [Canvas.LeftProperty] = 435.0, [Canvas.TopProperty] = 286.0 });
    }

    private static void Well(Canvas canvas, double cx, double cy, double r, byte alpha = 34)
    {
        canvas.Children.Add(new Ellipse
        {
            Width = r * 2, Height = r * 2, Fill = Dark(alpha), Stroke = Shade(22), StrokeThickness = 1.5,
            [Canvas.LeftProperty] = cx - r, [Canvas.TopProperty] = cy - r,
        });
    }

    /// <summary>What a button shows: coloured letters for the face buttons, icons for View
    /// and Menu, the Xbox mark, arrows on the D-pad, names elsewhere.</summary>
    public static Control Face(PadButton b, double w, double h)
    {
        Control c = b switch
        {
            PadButton.A or PadButton.B or PadButton.X or PadButton.Y => Hints.Glyph(b, Math.Min(w, h) - 6),
            PadButton.View or PadButton.Menu => Icons.Make(b == PadButton.View ? "view" : "menu", 22, T.Text, 2),
            PadButton.Guide => XboxMark(Math.Min(w, h) * 0.55),
            _ => Ui.Text(Hints.ShortName(b), b is PadButton.DPadUp or PadButton.DPadDown or PadButton.DPadLeft or PadButton.DPadRight ? 20 : 22, T.Text, FontWeight.Bold),
        };
        c.HorizontalAlignment = HorizontalAlignment.Center;
        c.VerticalAlignment = VerticalAlignment.Center;
        return c;
    }

    /// <summary>A sphere with two crossing swooshes, like the Xbox button's mark.</summary>
    private static Control XboxMark(double size) => new Viewbox
    {
        Width = size, Height = size,
        Child = new Path
        {
            Data = Geometry.Parse("M 12,2 A 10,10 0 1 1 11.99,2 Z M 5.2,5.6 C 9,8 12,11.5 18.8,18.4 M 18.8,5.6 C 15,8 12,11.5 5.2,18.4"),
            Stroke = T.Text, StrokeThickness = 2.2, StrokeLineCap = PenLineCap.Round, Width = 24, Height = 24,
        },
    };
}
