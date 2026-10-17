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
using Lounge.Shell;

namespace Lounge.Screens;

/// <summary>Every controller Lounge has seen, with its name, ID and whether its buttons are
/// set up. Each controller has its own button layout; the Remote's action map applies to
/// all of them on top.</summary>
public sealed class ControllersScreen : BrowseScreen
{
    public override string Title => "Controllers";

    public ControllersScreen() : base("Controllers", "Choose")
    {
        Sub.Text = "Each controller has its own button layout. What the buttons do is set on the Remote.";
        L.Pad.ConnectedChanged += Rebuild;
        L.Pad.Profiles.Changed += Rebuild;
        Build();
    }

    private void Rebuild() => Dispatcher.UIThread.Post(Build);

    public override void OnClosed()
    {
        L.Pad.ConnectedChanged -= Rebuild;
        L.Pad.Profiles.Changed -= Rebuild;
    }

    public override void OnShown() => Build();

    private void Build()
    {
        var connected = L.Pad.Connected.Select(c => c.Id).ToHashSet();
        Options.Clear();
        var list = new FocusList(horizontal: false);
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(96, 10, 96, 80), MaxWidth = 1400, HorizontalAlignment = HorizontalAlignment.Left };
        var profiles = L.Pad.Profiles.All.OrderByDescending(p => connected.Contains(p.Id)).ThenByDescending(p => p.LastSeen).ToList();
        if (profiles.Count == 0)
            panel.Children.Add(Ui.Muted("No controller yet. Connect one (USB or Bluetooth) and it shows up here.", T.Body).Margin(24, 10, 0, 0));
        foreach (var p in profiles)
        {
            var on = connected.Contains(p.Id);
            var status = (on ? "Connected" : $"Last seen {p.LastSeen:d MMM HH:mm}") + " · " + (p.Configured ? "buttons set up" : "buttons guessed, set them up");
            var g = Ui.Grid("Auto,*,Auto", "Auto,Auto,Auto");
            g.Children.Add(Icons.Make("gamepad", 40, on ? T.Accent : T.TextFaint).Margin(0, 0, 24, 0).Col(0).Row(0, 3));
            g.Children.Add(Ui.Text(p.Name, T.Body, T.Text, FontWeight.SemiBold).Col(1).Row(0));
            g.Children.Add(Ui.Text(status, T.Caption, p.Configured || !on ? T.TextMuted : T.Accent, FontWeight.Medium).Col(1).Row(1));
            g.Children.Add(Ui.Muted($"{(p.Kind == ControllerKind.XInput ? "XInput" : "HID gamepad")} · {p.VendorId:X4}:{p.ProductId:X4} · ID {p.Id}", T.Caption).Col(1).Row(2));
            g.Children.Add(Icons.Make("chevron", 24, T.TextFaint).Col(2).Row(0, 3));
            var card = new Card(g.Margin(28, 18), 14) { FocusScale = 1.01, ShowRing = false };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f => card.Body.Background = f ? T.Surface : Brushes.Transparent;
            var profile = p;
            card.Item.Activate = () => ShowOptions(profile, on);
            Options[card.Item] = card.Item.Activate;
            panel.Children.Add(card);
            list.Add(card.Item);
        }
        Body.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        UseScope(list);
    }

    private static void ShowOptions(ControllerProfile p, bool connected) => L.Window.Menu(p.Name,
    [
        new MenuEntry("Set up buttons", "gamepad", () =>
        {
            if (!connected) { L.Window.Toast($"Connect {p.Name} first.", "warning"); return; }
            L.Window.Push(new ControllerSetupScreen(p));
        }, p.Configured ? "Set" : "Guessed"),
        new MenuEntry("Rename", "pencil", async () =>
        {
            var name = await L.Window.KeyboardAsync(new KeyboardRequest("Controller name", p.Name, Validate: s => string.IsNullOrWhiteSpace(s) ? "A name can't be empty." : null));
            if (name is not null) L.Pad.Profiles.Rename(p.Id, name);
        }),
        new MenuEntry("Reset to the default layout", "refresh", () => { L.Pad.Profiles.ResetLayout(p.Id); L.Window.Toast($"{p.Name} is back to its default buttons.", "refresh"); }),
        new MenuEntry("Forget this controller", "trash", () => L.Window.Ask($"Forget {p.Name}?", "Its name and button layout go. If it connects again it starts on the default layout.",
            ["Forget", "Cancel"], 1, i => { if (i == 0) L.Pad.Profiles.Forget(p.Id); }, "trash"), Danger: true),
    ]);
}

/// <summary>Teaches Lounge one controller's buttons: it asks for each Xbox-layout input in
/// turn on a drawing of the pad, and learns whatever that controller sends. Hold any
/// button to skip a step; hold longer to stop. Only this controller is listened to for the
/// answers; any other controller or the keyboard still works (B cancels).</summary>
public sealed class ControllerSetupScreen : Screen
{
    private readonly ControllerProfile _profile;
    private readonly Dictionary<string, SourceBinding> _layout;
    private readonly Dictionary<PadButton, Border> _spots = new();
    private readonly TextBlock _step = Ui.Text("", T.Caption, T.TextMuted, FontWeight.Bold, letterSpacing: 2);
    private readonly TextBlock _prompt = Ui.Text("", T.Title, T.Text, T.TitleWeight, TextWrapping.Wrap);
    private readonly TextBlock _was = Ui.Muted("", T.Body);
    private readonly Border _holdFill = new() { Height = 6, Width = 0, CornerRadius = new CornerRadius(3), Background = T.Accent, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly DispatcherTimer _tick;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private LayoutCapture? _capture;
    private int _index;
    private bool _done;

    public override string Title => $"Set up {_profile.Name}";

    public ControllerSetupScreen(ControllerProfile profile)
    {
        _profile = profile;
        _layout = new Dictionary<string, SourceBinding>(profile.Layout);
        var grid = Ui.Grid("1000,*", "Auto,*");
        grid.Children.Add(Ui.V(10, Ui.Header($"Set up {profile.Name}", T.Title),
            Ui.Muted("Hold any button for 1.5 s to skip a step, or 4 s to stop. B on another controller or Esc cancels.", T.Body)).Margin(96, 56, 0, 10).Col(0, 2).Row(0));

        var canvas = new Canvas { Width = ControllerArt.Width, Height = ControllerArt.Height };
        ControllerArt.DrawBody(canvas);
        foreach (var (b, x, y, w, h, r) in ControllerArt.Hotspots)
        {
            var spot = new Border
            {
                Width = w, Height = h, CornerRadius = new CornerRadius(r),
                Background = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), BorderBrush = T.Line, BorderThickness = new Thickness(1.5),
                Child = ControllerArt.Face(b, w, h), [Canvas.LeftProperty] = x, [Canvas.TopProperty] = y,
            };
            canvas.Children.Add(spot);
            _spots[b] = spot;
        }
        grid.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform, Margin = new Thickness(96, 30, 40, 30) }.Col(0).Row(1));

        var track = new Border { Height = 6, Width = 420, CornerRadius = new CornerRadius(3), Background = T.Surface, HorizontalAlignment = HorizontalAlignment.Left, Child = _holdFill };
        grid.Children.Add(Ui.V(18, _step, _prompt, _was, track).Margin(20, 120, 96, 0).Col(1).Row(1));
        Children.Add(grid);

        _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
        _tick.Tick += (_, _) => Tick();
    }

    public override void OnShown()
    {
        L.Pad.CaptureId = _profile.Id;
        _prompt.Text = "Let go of every button...";
        _tick.Start();
        ShowStep();
    }

    public override void OnClosed() => Release();

    private void Release()
    {
        _tick.Stop();
        if (L.Pad.CaptureId == _profile.Id) L.Pad.CaptureId = null;
    }

    private string Target => ControllerLayout.Targets[_index];

    private void ShowStep()
    {
        _step.Text = $"STEP {_index + 1} OF {ControllerLayout.Targets.Length}";
        if (_capture is not null) _prompt.Text = ControllerLayout.Prompt(Target);
        _was.Text = _layout.TryGetValue(Target, out var b) ? $"Now: {SourceLabel(b)}" : "Not set";
        var highlight = Target switch { "LX" or "LY" => PadButton.LeftStick, "RX" or "RY" => PadButton.RightStick, var t => Enum.Parse<PadButton>(t) };
        foreach (var (button, spot) in _spots)
        {
            var on = button == highlight;
            spot.BorderBrush = on ? T.Accent : T.Line;
            spot.BorderThickness = new Thickness(on ? 4 : 1.5);
            spot.Background = on ? T.WithAlpha(T.AccentColor, 0.35) : new SolidColorBrush(Color.FromArgb(55, 255, 255, 255));
        }
    }

    private void Tick()
    {
        if (_done) return;
        var raw = L.Pad.RawOf(_profile.Id);
        if (raw is null) { Finish("disconnected"); return; }
        var now = _clock.ElapsedMilliseconds;
        if (_capture is null)
        {
            // Wait for hands off (the button that opened this screen may still be down).
            if (raw.Inputs.Any(kv => !RawPad.IsAxis(kv.Key) && kv.Value > 0.5f)) return;
            _capture = new LayoutCapture(raw);
            ShowStep();
            return;
        }
        var result = _capture.Update(raw, now);
        _holdFill.Width = 420 * Math.Clamp(_capture.HeldMs(now) / (double)LayoutCapture.SkipMs, 0, 1);
        switch (result.Outcome)
        {
            case LearnOutcome.Assigned:
                _layout[Target] = result.Binding!;
                L.Play(UiSound.Select);
                L.Rumble(0.2f, 40);
                Next(raw);
                break;
            case LearnOutcome.Skipped:
                L.Play(UiSound.Move);
                Next(raw);
                break;
            case LearnOutcome.Stopped:
                Finish("stopped");
                break;
        }
    }

    private void Next(RawPad raw)
    {
        if (++_index >= ControllerLayout.Targets.Length) { Finish("complete"); return; }
        _capture!.NextStep(raw);
        ShowStep();
    }

    private void Finish(string why)
    {
        _done = true;
        Release();
        switch (why)
        {
            case "complete":
                L.Pad.Profiles.SetLayout(_profile.Id, _layout);
                L.Play(UiSound.Notify);
                L.Window.Toast($"{_profile.Name}'s buttons are set.", "check");
                L.Window.Remove(this);
                break;
            case "disconnected":
                L.Window.Toast($"{_profile.Name} disconnected. Nothing was changed.", "warning");
                L.Window.Remove(this);
                break;
            default:
                L.Window.Ask("Keep these buttons?", $"You stopped at step {_index + 1}. The steps you answered can be kept; the rest stay as they were.",
                    ["Keep them", "Discard"], 1, i =>
                    {
                        if (i == 0) { L.Pad.Profiles.SetLayout(_profile.Id, _layout); L.Window.Toast($"{_profile.Name}'s buttons are saved.", "check"); }
                        L.Window.Remove(this);
                    }, "gamepad");
                break;
        }
    }

    protected override bool OnBack()
    {
        if (!_done) { _done = true; Release(); L.Window.Toast("Set-up cancelled. Nothing was changed.", "info"); }
        return base.OnBack();
    }

    public override bool Handle(ActionEvent e) => e.Action == Act.Back && e.Phase == ActionPhase.Pressed ? OnBack() : true;

    /// <summary>A source as the viewer would say it: "Button 4", "Hat up", "Y axis -".</summary>
    public static string SourceLabel(SourceBinding b) => b.Source switch
    {
        var s when s.StartsWith('b') && int.TryParse(s[1..], out var n) => $"Button {n}",
        var s when s.StartsWith("hat:") => "Hat " + s[4..],
        "lx" => "Left stick X", "ly" => "Left stick Y", "lt" => "Left trigger", "rt" => "Right trigger",
        var s when RawPad.IsAxis(s) => $"{s.ToUpperInvariant()} axis {(b.Sign > 0 ? "+" : "-")}",
        var s when Enum.TryParse<PadButton>(s, out var pb) => PadButtons.Label(pb),
        var s => s,
    };
}
