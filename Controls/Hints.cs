using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lounge.Core.Input;
using Lounge.Shell;

namespace Lounge.Controls;

/// <summary>Controller glyphs (Ⓐ in green, Ⓑ in red...) and the hint bar that tells you
/// what each button does on this screen. Hints follow the user's remapping.</summary>
public static class Hints
{
    private static readonly Dictionary<PadButton, Color> FaceColors = new()
    {
        [PadButton.A] = Color.Parse("#5DC21E"),
        [PadButton.B] = Color.Parse("#E63946"),
        [PadButton.X] = Color.Parse("#2E86DE"),
        [PadButton.Y] = Color.Parse("#F4B400"),
    };

    public static string ShortName(PadButton b) => b switch
    {
        PadButton.DPadUp => "↑", PadButton.DPadDown => "↓", PadButton.DPadLeft => "←", PadButton.DPadRight => "→",
        PadButton.LeftStick => "LS", PadButton.RightStick => "RS", PadButton.Guide => "Xbox",
        PadButton.LSUp or PadButton.LSDown or PadButton.LSLeft or PadButton.LSRight => "LS",
        PadButton.RSUp or PadButton.RSDown or PadButton.RSLeft or PadButton.RSRight => "RS",
        _ => b.ToString(),
    };

    public static Control Glyph(PadButton b, double size = 34)
    {
        if (FaceColors.TryGetValue(b, out var c))
        {
            return new Border
            {
                Width = size, Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = new SolidColorBrush(Color.FromArgb(48, c.R, c.G, c.B)),
                BorderBrush = new SolidColorBrush(c),
                BorderThickness = new Thickness(2),
                Child = new TextBlock
                {
                    Text = b.ToString(), FontSize = Math.Round(size * 0.55), FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(c),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }
        return new Border
        {
            Height = size, MinWidth = size,
            Padding = new Thickness(8, 0),
            CornerRadius = new CornerRadius(8),
            BorderBrush = T.TextMuted,
            BorderThickness = new Thickness(2),
            Child = new TextBlock
            {
                Text = ShortName(b), FontSize = Math.Max(16, Math.Round(size * 0.45)), FontWeight = FontWeight.Bold,
                Foreground = T.Text,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    public static Control Combo(InputBinding binding, double size = 34)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (binding.Gesture == Gesture.Hold) p.Children.Add(Ui.Text("hold", T.Caption, T.TextMuted));
        var first = true;
        foreach (var b in PadButtons.Split(binding.Buttons))
        {
            if (!first) p.Children.Add(Ui.Text("+", T.Caption, T.TextMuted));
            p.Children.Add(Glyph(b, size));
            first = false;
        }
        return p;
    }

    /// <summary>The binding currently used for <paramref name="action"/> in a context, if any.</summary>
    public static InputBinding? BindingFor(string context, string action)
    {
        foreach (var c in Keymap.Chain(context))
        {
            var b = L.Keymap.BindingsFor(c, action).Where(x => (x.Buttons & PadButton.Guide) == 0 || L.Settings.Bool(Lounge.Core.Settings.K.UseGuide)).OrderBy(x => x.Gesture == Gesture.Hold ? 1 : 0)
                .ThenBy(x => PadButtons.Count(x.Buttons)).Cast<InputBinding?>().FirstOrDefault();
            if (b is not null) return b;
        }
        return null;
    }

    /// <summary>A row of glyph + label pairs, e.g. Ⓐ Play  Ⓨ Options  Ⓑ Back.</summary>
    public static Control Bar(string context, params (string Action, string Label)[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 34 };
        foreach (var (action, label) in items)
        {
            if (BindingFor(context, action) is not { } b) continue;
            p.Children.Add(Ui.H(10, Combo(b, 32), Ui.Text(label, T.Caption, T.TextMuted)));
        }
        return p;
    }
}
