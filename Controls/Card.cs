using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lounge.Core.Focus;
using Lounge.Shell;

namespace Lounge.Controls;

/// <summary>Anything that can hold focus on a TV: it lifts toward the viewer with a
/// spring, grows a soft glow and a fine ring, and comes to the front. The body clips its
/// content to the theme's corner radius.</summary>
public class Card : Panel
{
    private readonly Border _glow;
    private readonly Border _ring;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Spring _focusSpring;
    private readonly Spring _pressSpring;
    private double _press = 1;
    private double _focusAmount;

    public Border Body { get; }
    public FocusItem Item { get; }
    private double _focusScale = T.Def.FocusScale;

    /// <summary>How far the card lifts. Rows that barely lift get a tight glow so it fits
    /// inside the list's edges instead of being cut off.</summary>
    public double FocusScale
    {
        get => _focusScale;
        set { _focusScale = value; ShapeGlow(); }
    }

    private void ShapeGlow()
    {
        if (_glow is null) return;
        var row = _focusScale <= 1.03;
        _glow.BoxShadow = new BoxShadows(new BoxShadow
        {
            OffsetY = row ? 6 : 18, Blur = row ? 18 : 48, Spread = 0,
            Color = Color.FromArgb(row ? (byte)110 : (byte)150, 0, 0, 0),
        }, [new BoxShadow { Blur = row ? 12 : 34, Spread = row ? 0 : 2, Color = Color.FromArgb(row ? (byte)60 : (byte)90, T.FocusColor.R, T.FocusColor.G, T.FocusColor.B) }]);
    }
    public bool ShowRing { get; set; } = true;

    public event Action<bool>? Focused;

    public Card(Control? content = null, double radius = -1, object? tag = null)
    {
        var r = radius < 0 ? T.Radius : radius;
        RenderTransform = _scale;
        RenderTransformOrigin = RelativePoint.Center;

        _glow = new Border
        {
            CornerRadius = new CornerRadius(r),
            Background = T.WithAlpha(T.Bg0Color, 0.01),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetY = 18, Blur = 48, Spread = 0,
                Color = Color.FromArgb(150, 0, 0, 0),
            }, [new BoxShadow { Blur = 34, Spread = 2, Color = Color.FromArgb(90, T.FocusColor.R, T.FocusColor.G, T.FocusColor.B) }]),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        Body = new Border
        {
            CornerRadius = new CornerRadius(r),
            ClipToBounds = true,
            Background = T.Surface,
            Child = content,
        };
        _ring = new Border
        {
            CornerRadius = new CornerRadius(r),
            BorderBrush = T.Focus,
            BorderThickness = new Thickness(3),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        ShapeGlow();
        Children.Add(_glow);
        Children.Add(Body);
        Children.Add(_ring);

        Item = new FocusItem(this, tag) { FocusChanged = SetFocused };
        _focusSpring = new Spring(0, v => { _focusAmount = v; Apply(); }, T.Def.SpringStiffness, T.Def.SpringDamping);
        _pressSpring = new Spring(1, v => { _press = v; Apply(); }, 600, 30);
    }

    public Control? Content
    {
        get => Body.Child;
        set => Body.Child = value;
    }

    private void Apply()
    {
        var s = (1 + (FocusScale - 1) * _focusAmount) * _press;
        _scale.ScaleX = _scale.ScaleY = s;
        _glow.Opacity = Math.Clamp(_focusAmount, 0, 1);
        _ring.Opacity = ShowRing ? Math.Clamp(_focusAmount, 0, 1) * 0.9 : 0;
    }

    public virtual void SetFocused(bool focused)
    {
        ZIndex = focused ? 10 : 0;
        _focusSpring.To(focused ? 1 : 0);
        Focused?.Invoke(focused);
    }

    /// <summary>A quick press-in on activation, like a physical button.</summary>
    public void Pulse()
    {
        _pressSpring.Snap(0.94);
        _pressSpring.To(1);
    }
}

/// <summary>A card that is a picture with a caption under it (posters, albums, apps).</summary>
public sealed class PosterCard : Card
{
    public PosterCard(double width, double height, string title, string? subtitle, Func<double, double, Control> art,
        double progress = -1, object? tag = null, bool captionInside = false)
        : base(null, -1, tag)
    {
        Width = width;
        Height = height;
        var grid = new Grid();
        grid.Children.Add(art(width, height));
        if (progress >= 0)
        {
            var track = new Border
            {
                Height = 6, CornerRadius = new CornerRadius(3),
                Background = T.WithAlpha(Colors.Black, 0.5),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(14, 0, 14, 14),
                Child = new Border
                {
                    Background = T.Accent,
                    CornerRadius = new CornerRadius(3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(6, (width - 28) * Math.Clamp(progress, 0, 1)),
                },
            };
            grid.Children.Add(track);
        }
        if (captionInside)
        {
            grid.Children.Add(new Border
            {
                Background = Ui.VerticalFade(Colors.Black, 0, 0.75, 0.45, 1),
                IsHitTestVisible = false,
            });
            var caption = Ui.V(2, Ui.Text(title, T.Body, Brushes.White, FontWeight.SemiBold));
            if (!string.IsNullOrEmpty(subtitle)) caption.Children.Add(Ui.Text(subtitle!, T.Caption, new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))));
            caption.VerticalAlignment = VerticalAlignment.Bottom;
            caption.Margin = new Thickness(18, 0, 18, progress >= 0 ? 30 : 16);
            grid.Children.Add(caption);
        }
        Content = grid;
        Title = title;
        Subtitle = subtitle;
    }

    public string Title { get; }
    public string? Subtitle { get; }
}
