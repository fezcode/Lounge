using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core.Audio;
using Lounge.Core.Focus;
using Lounge.Core.Input;
using Lounge.Core.Settings;

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
        var body = ModalLayer.IconLine(Icons.Make(icon, 30, T.Text), Ui.Text(message, T.Body, T.Text, FontWeight.SemiBold, TextWrapping.Wrap), 16);
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

    /// <summary>An icon with text beside it that wraps within the width it is given. (A
    /// horizontal stack offers its children unlimited width, so text in one never wraps and
    /// a long file name ran out of the dialog.)</summary>
    public static Grid IconLine(Control icon, TextBlock text, double gap)
    {
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, Math.Max(0, (text.FontSize * 1.3 - icon.Height) / 2), 0, 0);
        text.Margin = new Thickness(gap, 0, 0, 0);
        line.Children.Add(icon);
        Grid.SetColumn(text, 1);
        line.Children.Add(text);
        return line;
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
            IconLine(Icons.Make(icon, 44, T.Accent), Ui.Text(title, T.Title, T.Text, T.TitleWeight, TextWrapping.Wrap), 18),
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

    /// <summary>An options list sliding in from the right (View button menus). With
    /// <paramref name="clock"/>, the time sits in its top-right corner (over a playing
    /// video, where the status bar is hidden).</summary>
    public static ModalLayer Menu(string title, IReadOnlyList<MenuEntry> items, Action<int> done, bool clock = false)
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
            if (it.Icon.Length > 0)   // choice lists have none: the selected one is ticked on the right
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
        var header = Ui.Header(title, T.Title);
        if (clock && (L.Settings?.Bool(K.ShowClock) ?? true))
        {
            header.TextTrimming = TextTrimming.CharacterEllipsis;
            var time = Ui.Text("", T.Body, T.TextMuted, FontWeight.SemiBold);
            void Tick() => time.Text = DateTime.Now.ToString(L.Settings?.Bool(K.Clock24h) ?? true ? "HH:mm" : "h:mm tt");
            Tick();
            // Normal priority: Background timers starve while a video is playing.
            var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick());
            time.AttachedToVisualTree += (_, _) => timer.Start();
            time.DetachedFromVisualTree += (_, _) => timer.Stop();
            var top = Ui.Grid("*,Auto", "Auto");
            top.Children.Add(header.Col(0).Align(HorizontalAlignment.Stretch, VerticalAlignment.Center));
            top.Children.Add(time.Margin(24, 0, 0, 0).Col(1).Align(HorizontalAlignment.Right, VerticalAlignment.Center));
            grid.Children.Add(top.Margin(36, 48, 36, 24).Row(0));
        }
        else grid.Children.Add(header.Margin(36, 48, 36, 24).Row(0));
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
