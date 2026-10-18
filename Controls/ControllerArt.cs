using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Lounge.Core.Input;
using Lounge.Shell;
using Path = Avalonia.Controls.Shapes.Path;

namespace Lounge.Controls;

/// <summary>An Xbox Series controller seen from the front, drawn on a 900 x 640 canvas:
/// the body with its shoulders and grips, recessed stick wells and D-pad, and Share.
/// Every button, triggers and bumpers included, is a card that screens place on the
/// <see cref="Hotspots"/> (Remote: what it does; Controllers: which one to press), so
/// nothing is drawn twice. Bumpers and triggers tilt with the shoulders (<see cref="Tilt"/>).</summary>
public static class ControllerArt
{
    public const double Width = 900, Height = 640;

    /// <summary>Where each button sits: top-left, size and corner radius.</summary>
    public static readonly (PadButton Button, double X, double Y, double W, double H, double R)[] Hotspots =
    [
        (PadButton.LT, 152, 72, 104, 46, 18), (PadButton.RT, 644, 72, 104, 46, 18),
        (PadButton.LB, 121, 131, 150, 30, 15), (PadButton.RB, 629, 131, 150, 30, 15),
        (PadButton.LeftStick, 180, 200, 104, 104, 52), (PadButton.RightStick, 514, 322, 104, 104, 52),
        (PadButton.DPadUp, 308, 310, 44, 44, 9), (PadButton.DPadDown, 308, 394, 44, 44, 9),
        (PadButton.DPadLeft, 266, 352, 44, 44, 9), (PadButton.DPadRight, 350, 352, 44, 44, 9),
        (PadButton.Y, 640, 166, 56, 56, 28), (PadButton.A, 640, 282, 56, 56, 28),
        (PadButton.X, 582, 224, 56, 56, 28), (PadButton.B, 698, 224, 56, 56, 28),
        (PadButton.View, 372, 236, 40, 40, 20), (PadButton.Menu, 488, 236, 40, 40, 20),
        (PadButton.Guide, 418, 158, 64, 64, 32),
    ];

    /// <summary>Bumpers and triggers follow the slope of the shoulders.</summary>
    public static void Tilt(Control spot, PadButton b)
    {
        var angle = b switch { PadButton.LB => -14, PadButton.RB => 14, PadButton.LT => -9, PadButton.RT => 9, _ => 0 };
        if (angle == 0) return;
        spot.RenderTransformOrigin = RelativePoint.Center;
        spot.RenderTransform = new RotateTransform(angle);
    }

    /// <summary>Symmetric about x = 450: shoulders rise high and round, the grips taper
    /// down and outwards, the underside arches between them.</summary>
    private const string Body =
        "M 450,150 C 380,150 320,146 268,150 C 196,156 132,176 104,226 C 76,276 56,360 44,440 " +
        "C 32,520 30,580 64,606 C 98,632 148,622 178,584 C 214,538 246,494 300,482 C 360,470 400,474 450,474 " +
        "C 500,474 540,470 600,482 C 654,494 686,538 722,584 C 752,622 802,632 836,606 " +
        "C 870,580 868,520 856,440 C 844,360 824,276 796,226 C 768,176 704,156 632,150 C 580,146 520,150 450,150 Z";

    private static IBrush Shade(byte a) => new SolidColorBrush(Color.FromArgb(a, 255, 255, 255));
    private static IBrush Dark(byte a) => new SolidColorBrush(Color.FromArgb(a, 0, 0, 0));

    /// <summary>Draws everything but the buttons themselves.</summary>
    public static void DrawBody(Canvas canvas)
    {
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse(Body),
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(58, 255, 255, 255), 0), new GradientStop(Color.FromArgb(28, 255, 255, 255), 0.6), new GradientStop(Color.FromArgb(14, 255, 255, 255), 1) },
            },
            Stroke = T.Line, StrokeThickness = 2,
        });
        // Light catching the top edge.
        canvas.Children.Add(new Path { Data = Geometry.Parse("M 290,164 C 370,158 530,158 610,164"), Stroke = Shade(48), StrokeThickness = 2, StrokeLineCap = PenLineCap.Round });

        // Recesses: the two stick wells and the D-pad.
        Well(canvas, 232, 252, 66);
        Well(canvas, 566, 374, 66);
        Well(canvas, 330, 374, 70, 24);
        canvas.Children.Add(new Path { Data = Geometry.Parse("M 308,310 h 44 v 42 h 42 v 44 h -42 v 42 h -44 v -42 h -42 v -44 h 42 Z"), Fill = Shade(26), Stroke = T.Line, StrokeThickness = 1 });

        // Share sits below View and Menu; Lounge doesn't use it.
        canvas.Children.Add(new Border { Width = 30, Height = 16, CornerRadius = new CornerRadius(8), Background = Shade(28), BorderBrush = T.Line, BorderThickness = new Thickness(1), [Canvas.LeftProperty] = 435.0, [Canvas.TopProperty] = 300.0 });
    }

    private static void Well(Canvas canvas, double cx, double cy, double r, byte alpha = 34)
    {
        canvas.Children.Add(new Ellipse
        {
            Width = r * 2, Height = r * 2, Fill = Dark(alpha), Stroke = Shade(20), StrokeThickness = 1.5,
            [Canvas.LeftProperty] = cx - r, [Canvas.TopProperty] = cy - r,
        });
    }

    /// <summary>What a button shows: coloured letters for the face buttons, icons for View
    /// and Menu, the Xbox logo, arrows on the D-pad, names elsewhere.</summary>
    public static Control Face(PadButton b, double w, double h)
    {
        Control c = b switch
        {
            PadButton.A or PadButton.B or PadButton.X or PadButton.Y => Hints.Glyph(b, Math.Min(w, h) - 6),
            PadButton.View or PadButton.Menu => Icons.Make(b == PadButton.View ? "view" : "menu", 22, T.Text, 2),
            PadButton.Guide => XboxLogo(Math.Min(w, h) * 0.56),
            _ => Ui.Text(Hints.ShortName(b), 20, T.Text, FontWeight.Bold),
        };
        c.HorizontalAlignment = HorizontalAlignment.Center;
        c.VerticalAlignment = VerticalAlignment.Center;
        return c;
    }

    /// <summary>The Xbox mark (Simple Icons, CC0), used only to label the Xbox button.</summary>
    private const string Logo =
        "M4.102 21.033C6.211 22.881 8.977 24 12 24c3.026 0 5.789-1.119 7.902-2.967 1.877-1.912-4.316-8.709-7.902-11.417-3.582 2.708-9.779 9.505-7.898 11.417z" +
        "m11.16-14.406c2.5 2.961 7.484 10.313 6.076 12.912C23.002 17.48 24 14.861 24 12.004c0-3.34-1.365-6.362-3.57-8.536 0 0-.027-.022-.082-.042-.063-.022-.152-.045-.281-.045-.592 0-1.985.434-4.805 3.246z" +
        "M3.654 3.426c-.057.02-.082.041-.086.042C1.365 5.642 0 8.664 0 12.004c0 2.854.998 5.473 2.661 7.533-1.401-2.605 3.579-9.951 6.08-12.91-2.82-2.813-4.216-3.245-4.806-3.245-.131 0-.223.021-.281.046v-.002z" +
        "M12 3.551S9.055 1.828 6.755 1.746c-.903-.033-1.454.295-1.521.339C7.379.646 9.659 0 11.984 0H12c2.334 0 4.605.646 6.766 2.085-.068-.046-.615-.372-1.52-.339C14.946 1.828 12 3.545 12 3.545v.006z";

    private static Control XboxLogo(double size) => new Viewbox
    {
        Width = size, Height = size,
        Child = new Path { Data = Geometry.Parse(Logo), Fill = T.Text, Width = 24, Height = 24 },
    };
}
