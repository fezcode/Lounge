using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core.Audio;
using Lounge.Core.Focus;
using Lounge.Core.Input;

namespace Lounge.Shell;

/// <summary>One row of a <see cref="SidePanel"/>: a label and a live value. Left/Right
/// call <see cref="Step"/>; A calls <see cref="Activate"/> (or steps forward).</summary>
public sealed class PanelRow
{
    public required string Label { get; init; }
    public Func<string> Value { get; init; } = () => "";
    public Action<int>? Step { get; init; }
    public Action? Activate { get; init; }
    public string Icon { get; init; } = "";
    public string? Header { get; init; }
}

/// <summary>A tivi-style panel sliding in from the right, used over the player for
/// audio, subtitles, speed and picture. Rows update live as values change.</summary>
public sealed class SidePanel : Grid, IInputLayer
{
    private readonly FocusScope _scope;
    private readonly List<(PanelRow Row, TextBlock Value)> _rows = new();
    private readonly Dictionary<FocusItem, PanelRow> _byItem = new();
    private readonly Spring _in;
    private readonly Action? _closed;
    private bool _isClosed;

    public string Context => "Shell";

    public SidePanel(string title, IReadOnlyList<PanelRow> rows, Action? closed = null)
    {
        _closed = closed;
        var list = new FocusList(horizontal: false);
        var panel = new StackPanel { Spacing = 4 };
        foreach (var r in rows)
        {
            if (r.Header is { } h) panel.Children.Add(Ui.Text(h.ToUpperInvariant(), T.Caption, T.TextMuted, FontWeight.Bold, letterSpacing: 3).Margin(24, 22, 0, 8));
            var value = Ui.Text(r.Value(), T.Body, T.Text, FontWeight.SemiBold);
            var left = Icons.Make("back", 20, T.TextFaint);
            var right = Icons.Make("chevron", 20, T.TextFaint);
            left.Opacity = right.Opacity = 0;
            var g = Ui.Grid("Auto,*,Auto", "Auto");
            if (r.Icon.Length > 0) g.Children.Add(Icons.Make(r.Icon, 28, T.TextMuted).Margin(0, 0, 18, 0).Col(0));
            g.Children.Add(Ui.Text(r.Label, T.Body, T.TextMuted, FontWeight.Medium).Col(1).Align(HorizontalAlignment.Left, VerticalAlignment.Center));
            g.Children.Add(Ui.H(10, left, value, right).Col(2).Align(HorizontalAlignment.Right, VerticalAlignment.Center));
            var card = new Card(g.Margin(24, 16), 14) { FocusScale = 1.015, ShowRing = false };
            card.Body.Background = Brushes.Transparent;
            var steps = r.Step is not null;
            card.Focused += f =>
            {
                card.Body.Background = f ? T.WithAlpha(T.TextColor, 0.12) : Brushes.Transparent;
                left.Opacity = right.Opacity = f && steps ? 1 : 0;
            };
            var row = r;
            card.Item.Activate = () => { if (row.Activate is { } a) a(); else row.Step?.Invoke(1); Refresh(); };
            panel.Children.Add(card);
            list.Add(card.Item);
            _rows.Add((r, value));
            _byItem[card.Item] = r;
        }

        var grid = Ui.Grid("*", "Auto,*,Auto");
        grid.Children.Add(Ui.Header(title, T.Title).Margin(24, 0, 0, 20).Row(0));
        grid.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }.Row(1));
        grid.Children.Add(Hints.Bar("Shell", (Act.Left, "Change"), (Act.Accept, "Choose"), (Act.Back, "Close")).Margin(24, 20, 0, 0).Row(2));
        var sheet = new Border
        {
            Width = 760,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = T.SurfaceStrong,
            BorderBrush = T.Line,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(28, 60, 40, 44),
            Child = grid,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = -20, Blur = 80, Color = Color.FromArgb(160, 0, 0, 0) }),
        };
        var tr = new TranslateTransform();
        sheet.RenderTransform = tr;
        Children.Add(sheet);
        Opacity = 0;
        _in = new Spring(0, v => { Opacity = Math.Clamp(v, 0, 1); tr.X = 120 * (1 - v); }, 300, 28);
        _in.To(1);
        _scope = new FocusScope(list)
        {
            Locate = i => i.View is Visual v && v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), this) is { } p ? new Pt(p.X, p.Y) : null,
        };
        _scope.Moved += (_, now) => { if (now?.View is Control c) SmoothScroll.BringIntoView(c, 40, 120); };
        _scope.Refresh();
    }

    public void Refresh()
    {
        foreach (var (row, value) in _rows) value.Text = row.Value();
    }

    public bool Handle(ActionEvent e)
    {
        if (_isClosed) return false;
        if (e.Phase is ActionPhase.Released or ActionPhase.Changed) return true;
        switch (e.Action)
        {
            case Act.Up: if (_scope.Move(Dir.Up)) L.Play(UiSound.Move); return true;
            case Act.Down: if (_scope.Move(Dir.Down)) L.Play(UiSound.Move); return true;
            case Act.Left or Act.Right or Act.SkipBack or Act.SkipForward:
                if (_scope.Focused is { } f && _byItem[f].Step is { } step)
                {
                    step(e.Action is Act.Left or Act.SkipBack ? -1 : 1);
                    L.Play(UiSound.Move);
                    Refresh();
                }
                return true;
            case Act.Accept or Act.PlayPause:
                if (e.Phase == ActionPhase.Repeated) return true;
                L.Play(UiSound.Select);
                _scope.Activate();
                return true;
            case Act.Back or Act.Tracks or Act.Stop or Act.ContextMenu:
                Close();
                return true;
            case Act.Home:
                Close();
                return false;
            case Act.VolumeUp or Act.VolumeDown:
                return false;
        }
        return true;
    }

    public void Close()
    {
        if (_isClosed) return;
        _isClosed = true;
        L.Play(UiSound.Back);
        _in.To(0);
        L.Window.DismissOverlay(this, 240);
        _closed?.Invoke();
    }
}
