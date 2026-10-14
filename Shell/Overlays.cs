using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core.Audio;
using Lounge.Core.Focus;
using Lounge.Core.Input;

namespace Lounge.Shell;

/// <summary>Brief messages at the top of the screen. They stack and leave on their own.</summary>
public sealed class ToastHost : StackPanel
{
    public ToastHost()
    {
        Spacing = 12;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 40, 0, 0);
        IsHitTestVisible = false;
    }

    public void Show(string message, string icon = "info", double seconds = 3.2)
    {
        var body = Ui.H(16, Icons.Make(icon, 30, T.Text), Ui.Text(message, T.Body, T.Text, FontWeight.SemiBold, TextWrapping.Wrap));
        body.MaxWidth = 1100;
        var pill = new Border
        {
            Background = T.SurfaceStrong,
            BorderBrush = T.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(40),
            Padding = new Thickness(30, 18),
            Child = body,
            HorizontalAlignment = HorizontalAlignment.Center,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 12, Blur = 40, Color = Color.FromArgb(120, 0, 0, 0) }),
            RenderTransform = new TranslateTransform(0, -30),
            Opacity = 0,
        };
        var tr = (TranslateTransform)pill.RenderTransform!;
        var s = new Spring(0, v => { pill.Opacity = Math.Clamp(v, 0, 1); tr.Y = -30 * (1 - v); }, 260, 24);
        Children.Add(pill);
        while (Children.Count > 3) Children.RemoveAt(0);
        s.To(1);
        DispatcherTimer.RunOnce(() =>
        {
            s.To(0);
            DispatcherTimer.RunOnce(() => Children.Remove(pill), TimeSpan.FromMilliseconds(450));
        }, TimeSpan.FromSeconds(seconds));
    }
}

public sealed record MenuEntry(string Label, string Icon, Action? Run, string? Detail = null, bool Danger = false, bool Checked = false);

/// <summary>A centred modal card with a title, a message and buttons, or a slide-in list
/// of options. Both are entirely controller driven.</summary>
public sealed class ModalLayer : Grid, IInputLayer
{
    private readonly FocusScope _scope;
    private readonly Action<int> _done;
    private readonly int _cancelIndex;
    private readonly List<FocusItem> _items = new();
    private readonly Spring _in;
    private readonly Control _card;
    private bool _closed;

    public string Context => "Shell";

    private ModalLayer(Control card, FocusNode root, Action<int> done, int cancelIndex, bool side)
    {
        _done = done;
        _cancelIndex = cancelIndex;
        _card = card;
        Background = new SolidColorBrush(Color.FromArgb(side ? (byte)110 : (byte)170, 0, 0, 0));
        Children.Add(card);
        _scope = new FocusScope(root)
        {
            Locate = i => i.View is Visual v && v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), this) is { } p ? new Pt(p.X, p.Y) : null,
        };
        _scope.Moved += (_, now) => { if (now?.View is Control c) SmoothScroll.BringIntoView(c, 40, 80); };
        var tr = new TranslateTransform();
        card.RenderTransform = tr;
        Opacity = 0;
        _in = new Spring(0, v =>
        {
            Opacity = Math.Clamp(v, 0, 1);
            if (side) tr.X = 80 * (1 - v); else tr.Y = 40 * (1 - v);
        }, 300, 26);
        _in.To(1);
        _scope.Refresh();
    }

    /// <summary>A dialog. Returns the chosen button index, or <paramref name="cancelIndex"/> on B.</summary>
    public static ModalLayer Dialog(string title, string message, string[] buttons, int cancelIndex, Action<int> done, string icon = "info")
    {
        var row = new FocusList(horizontal: true);
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center };
        ModalLayer? layer = null;
        for (int i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var card = new Card(new TextBlock
            {
                Text = buttons[i], FontSize = T.Body, FontWeight = FontWeight.SemiBold, Foreground = T.Text,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            }, 16) { MinWidth = 220, Height = 72, FocusScale = 1.06 };
            card.Body.Background = T.Surface;
            card.Item.Activate = () => layer!.Close(index);
            card.Focused += f => card.Body.Background = f ? T.Accent : T.Surface;
            buttonPanel.Children.Add(card);
            row.Add(card.Item);
        }
        var body = Ui.V(28,
            Ui.H(18, Icons.Make(icon, 44, T.Accent), Ui.Text(title, T.Title, T.Text, T.TitleWeight, TextWrapping.Wrap)),
            Ui.Text(message, T.Body, T.TextMuted, FontWeight.Medium, TextWrapping.Wrap),
            buttonPanel.Margin(0, 16, 0, 0));
        var card2 = Ui.Glass(body.Margin(56, 48), strong: true);
        var c0 = ((ISolidColorBrush)T.SurfaceStrong).Color;
        card2.Background = new SolidColorBrush(Color.FromArgb(255, c0.R, c0.G, c0.B));
        card2.MaxWidth = 1000;
        card2.HorizontalAlignment = HorizontalAlignment.Center;
        card2.VerticalAlignment = VerticalAlignment.Center;
        card2.BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 30, Blur = 80, Color = Color.FromArgb(160, 0, 0, 0) });
        layer = new ModalLayer(card2, row, done, cancelIndex, side: false);
        return layer;
    }

    /// <summary>An options list sliding in from the right (View button menus).</summary>
    public static ModalLayer Menu(string title, IReadOnlyList<MenuEntry> items, Action<int> done)
    {
        var list = new FocusList(horizontal: false, rememberIndex: true);
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(16, 8, 16, 24) };
        ModalLayer? layer = null;
        for (int i = 0; i < items.Count; i++)
        {
            var index = i;
            var it = items[i];
            var label = Ui.Text(it.Label, T.Body, it.Danger ? T.Danger : T.Text, FontWeight.SemiBold);
            var row = Ui.Grid("Auto,*,Auto", "Auto");
            row.Children.Add(Icons.Make(it.Icon, 30, it.Danger ? T.Danger : T.Text).Margin(0, 0, 20, 0).Col(0));
            var texts = Ui.V(2, label);
            if (it.Detail is { } d) texts.Children.Add(Ui.Muted(d));
            row.Children.Add(texts.Col(1).Align(HorizontalAlignment.Stretch, VerticalAlignment.Center));
            if (it.Checked) row.Children.Add(Icons.Make("check", 28, T.Accent).Col(2));
            var card = new Card(row.Margin(24, 16), 14) { FocusScale = 1.02, ShowRing = false };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f => card.Body.Background = f ? T.WithAlpha(T.TextColor, 0.14) : Brushes.Transparent;
            card.Item.Activate = () => layer!.Close(index);
            panel.Children.Add(card);
            list.Add(card.Item);
        }
        var scroll = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var grid = Ui.Grid("*", "Auto,*");
        grid.Children.Add(Ui.Header(title, T.Title).Margin(36, 48, 36, 24).Row(0));
        grid.Children.Add(scroll.Margin(12, 0, 12, 36).Row(1));
        var sheet = new Border
        {
            Width = 640,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = T.SurfaceStrong,
            BorderBrush = T.Line,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = grid,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = -20, Blur = 80, Color = Color.FromArgb(150, 0, 0, 0) }),
        };
        layer = new ModalLayer(sheet, list, done, -1, side: true);
        return layer;
    }

    public void Close(int result)
    {
        if (_closed) return;
        _closed = true;
        _in.To(0);
        L.Window.DismissOverlay(this, 220);
        _done(result);
    }

    public bool Handle(ActionEvent e)
    {
        if (e.Phase is ActionPhase.Released or ActionPhase.Changed) return true;
        switch (e.Action)
        {
            case Act.Up: if (_scope.Move(Dir.Up)) L.Play(UiSound.Move); return true;
            case Act.Down: if (_scope.Move(Dir.Down)) L.Play(UiSound.Move); return true;
            case Act.Left: if (_scope.Move(Dir.Left)) L.Play(UiSound.Move); return true;
            case Act.Right: if (_scope.Move(Dir.Right)) L.Play(UiSound.Move); return true;
            case Act.Accept:
                L.Play(UiSound.Select);
                (_scope.Focused?.View as Card)?.Pulse();
                _scope.Activate();
                return true;
            case Act.Back:
            case Act.ContextMenu:
                L.Play(UiSound.Back);
                Close(_cancelIndex);
                return true;
            case Act.Home:
                Close(_cancelIndex);
                return false;   // let Home go through
        }
        return true;   // modal: nothing behind reacts
    }
}
