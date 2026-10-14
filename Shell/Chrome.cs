using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core.Settings;

namespace Lounge.Shell;

/// <summary>The volume level that appears on the right when RB/LB are pressed.</summary>
public sealed class VolumeHud : Border
{
    private readonly Border _fill;
    private readonly TextBlock _label = Ui.Text("", T.Body, T.Text, FontWeight.Bold);
    private readonly Control _icon;
    private readonly Spring _show;
    private readonly Spring _level;
    private IDisposable? _hide;
    private const double TrackHeight = 360;

    public VolumeHud()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(0, 0, 64, 0);
        Width = 96;
        Padding = new Thickness(0, 26);
        CornerRadius = new CornerRadius(48);
        Background = T.SurfaceStrong;
        BorderBrush = T.Line;
        BorderThickness = new Thickness(1);
        BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 16, Blur = 50, Color = Color.FromArgb(140, 0, 0, 0) });
        IsHitTestVisible = false;
        _fill = new Border { Background = T.Text, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Bottom };
        var track = new Border
        {
            Width = 24, Height = TrackHeight, CornerRadius = new CornerRadius(12),
            Background = T.WithAlpha(T.TextColor, 0.18), Child = _fill, ClipToBounds = true,
        };
        _icon = Icons.Make("audio", 34, T.Text);
        Child = Ui.V(18, _label.Align(HorizontalAlignment.Center, VerticalAlignment.Top), track.Align(HorizontalAlignment.Center, VerticalAlignment.Top), _icon.Align(HorizontalAlignment.Center, VerticalAlignment.Top));
        var tr = new TranslateTransform();
        RenderTransform = tr;
        Opacity = 0;
        _show = new Spring(0, v => { Opacity = Math.Clamp(v, 0, 1); tr.X = 60 * (1 - v); }, 320, 26);
        _level = new Spring(0, v => _fill.Height = Math.Clamp(v, 0, 1) * TrackHeight, 400, 32);
    }

    public void Show(int level, bool muted)
    {
        _label.Text = muted ? "0" : level.ToString();
        _level.To(muted ? 0 : level / 100.0);
        _show.To(1);
        _hide?.Dispose();
        _hide = DispatcherTimer.RunOnce(() => _show.To(0), TimeSpan.FromSeconds(1.6));
    }
}

/// <summary>Top-right status: what is playing, and the time.</summary>
public sealed class StatusBar : StackPanel
{
    private readonly TextBlock _clock = Ui.Text("", T.Body, T.Text, FontWeight.SemiBold);
    private readonly Border _nowPlaying;
    private readonly TextBlock _npText = Ui.Text("", T.Caption, T.Text, FontWeight.SemiBold);

    public StatusBar()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 24;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 44, 72, 0);
        IsHitTestVisible = false;
        _npText.MaxWidth = 460;
        _nowPlaying = new Border
        {
            Background = T.Surface,
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(18, 8),
            Child = Ui.H(10, Icons.Make("music", 24, T.Accent), _npText),
            IsVisible = false,
        };
        Children.Add(_nowPlaying);
        Children.Add(_clock);
        Tick();
        new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Tick()).Start();
    }

    public void Tick()
    {
        if (L.Settings is null) return;
        _clock.IsVisible = L.Settings.Bool(K.ShowClock);
        _clock.Text = DateTime.Now.ToString(L.Settings.Bool(K.Clock24h) ? "HH:mm" : "h:mm tt");
    }

    public void SetNowPlaying(string? text)
    {
        _nowPlaying.IsVisible = text is not null;
        _npText.Text = text ?? "";
    }
}

/// <summary>Shown when no controller is connected: Lounge needs one, and says so.</summary>
public sealed class ControllerOverlay : Grid
{
    private readonly Spring _show;

    public ControllerOverlay()
    {
        Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0));
        IsHitTestVisible = false;
        var card = Ui.V(26,
            Icons.Make("gamepad", 120, T.Text, 1.4).Align(HorizontalAlignment.Center, VerticalAlignment.Top),
            Ui.Text("Reconnect your controller", T.Title, T.Text, T.TitleWeight).Align(HorizontalAlignment.Center, VerticalAlignment.Top),
            Ui.Text("Turn it on with the Xbox button, or plug it in. Playback is paused until it is back.", T.Body, T.TextMuted, FontWeight.Medium, TextWrapping.Wrap)
                .Also(t => { t.TextAlignment = TextAlignment.Center; t.MaxWidth = 900; }).Align(HorizontalAlignment.Center, VerticalAlignment.Top));
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(card);
        Opacity = 0;
        IsVisible = false;
        _show = new Spring(0, v => { Opacity = Math.Clamp(v, 0, 1); IsVisible = v > 0.01; }, 200, 26);
    }

    public void Set(bool show) => _show.To(show ? 1 : 0);
}

/// <summary>After a while without input the screen fades to a quiet clock, like a TV's
/// ambient mode. Any button brings everything back (and is not passed on).</summary>
public sealed class IdleDim : Grid
{
    private readonly TextBlock _time = Ui.Text("", 160, Brushes.White, FontWeight.Light);
    private readonly TextBlock _date = Ui.Text("", T.Heading, new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), FontWeight.Medium);
    private readonly Spring _show;
    private readonly TranslateTransform _drift = new();
    public bool Active { get; private set; }

    public IdleDim()
    {
        Background = new SolidColorBrush(Color.FromArgb(235, 0, 0, 0));
        IsHitTestVisible = false;
        var stack = Ui.V(8, _time, _date);
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        stack.VerticalAlignment = VerticalAlignment.Center;
        stack.RenderTransform = _drift;
        Children.Add(stack);
        IsVisible = false;
        _show = new Spring(0, v => { Opacity = Math.Clamp(v, 0, 1); IsVisible = v > 0.01; }, 60, 16);
        new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) =>
        {
            Update();
            // Drift slowly so nothing burns into an OLED.
            var r = Random.Shared;
            _drift.X = r.Next(-300, 300);
            _drift.Y = r.Next(-200, 200);
        }).Start();
        Update();
    }

    private void Update()
    {
        var h24 = L.Settings?.Bool(K.Clock24h) ?? true;
        _time.Text = DateTime.Now.ToString(h24 ? "HH:mm" : "h:mm");
        _date.Text = DateTime.Now.ToString("dddd, d MMMM");
    }

    public void Set(bool on)
    {
        Active = on;
        Update();
        _show.To(on ? 1 : 0);
    }
}
