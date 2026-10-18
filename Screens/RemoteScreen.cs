using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core.Audio;
using Lounge.Core.Focus;
using Lounge.Core.Input;
using Lounge.Shell;
using Path = Avalonia.Controls.Shapes.Path;

namespace Lounge.Screens;

/// <summary>The controller as a remote: a drawing of the pad where every button can be
/// focused to see what it does here, and remapped by pressing the new button. Bindings
/// are per context (menus, watching, keyboard...), switched with LT/RT.</summary>
public sealed class RemoteScreen : Screen
{
    private static readonly (PadButton Button, double X, double Y, double W, double H, double R)[] Hotspots = ControllerArt.Hotspots;

    private static readonly string[] ContextOrder = ["Global", "Shell", "Player", "Keyboard", "Files", "Web", "Cursor", "Dormant"];

    private int _context;
    private PadButton _button = PadButton.A;
    private readonly FocusSpatial _pad = new();
    private readonly FocusList _rows = new(horizontal: false);
    private readonly StackPanel _rowPanel = new() { Spacing = 6 };
    private readonly TextBlock _buttonTitle = Ui.Text("", T.Title, T.Text, T.TitleWeight);
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly Dictionary<PadButton, (Border Spot, Card Card)> _spots = new();
    private readonly Border _capture = new();
    private readonly TextBlock _captureText = Ui.Text("", T.Title, T.Text, T.TitleWeight, TextWrapping.Wrap);
    private bool _capturing;

    public override string Title => "Remote";
    public override string Context => "Shell";

    public RemoteScreen()
    {
        var grid = Ui.Grid("980,*", "Auto,*,Auto");
        grid.Children.Add(Ui.V(10, Ui.Header("Remote", T.Title), Ui.Muted("Pick a button on the controller to see and change what it does.", T.Body), _tabs).Margin(96, 56, 0, 10).Col(0, 2).Row(0));

        // ---- the controller drawing
        var canvas = new Canvas { Width = ControllerArt.Width, Height = ControllerArt.Height };
        ControllerArt.DrawBody(canvas);
        foreach (var (b, x, y, w, h, r) in Hotspots)
        {
            var spot = new Border
            {
                Width = w, Height = h, CornerRadius = new CornerRadius(r),
                Background = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
                BorderBrush = T.Line, BorderThickness = new Thickness(1.5),
                Child = ControllerArt.Face(b, w, h),
            };
            var card = new Card(spot, r) { FocusScale = 1.18 };
            card.Body.Background = Brushes.Transparent;
            // The card clips its content, so a tilted bumper or trigger tilts card and all.
            var holder = new Panel { Children = { card } };
            ControllerArt.Tilt(holder, b);
            Canvas.SetLeft(holder, x);
            Canvas.SetTop(holder, y);
            var button = b;
            card.Focused += f => { if (f) { _button = button; ShowBindings(); } };
            card.Item.Activate = () => { if (_rows.HasItems) FocusItem(_rows.Items.First()); };
            canvas.Children.Add(holder);
            _pad.Add(card.Item);
            _spots[b] = (spot, card);
        }
        var viewbox = new Viewbox { Child = canvas, Stretch = Stretch.Uniform, Margin = new Thickness(96, 30, 40, 30) };
        grid.Children.Add(viewbox.Col(0).Row(1));

        // ---- bindings for the focused button
        var right = Ui.V(18, _buttonTitle, new ScrollViewer { Content = _rowPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, MaxHeight = 640 });
        grid.Children.Add(right.Margin(20, 40, 96, 0).Col(1).Row(1));
        grid.Children.Add(Hints.Bar("Shell", (Act.PrevSection, "Context"), (Act.Accept, "Change"), (Act.ContextMenu, "Reset"), (Act.Back, "Back")).Margin(96, 0, 0, 40).Col(0, 2).Row(2));
        Children.Add(grid);

        // ---- capture overlay
        _capture.Background = new SolidColorBrush(Color.FromArgb(225, 0, 0, 0));
        _capture.IsVisible = false;
        _capture.Child = Ui.V(24, Icons.Make("gamepad", 120, T.Text, 1.3), _captureText,
            Ui.Muted("Press it now. Hold for a hold. Press two together for a combination. Press B twice to cancel.", T.Body))
            .Align(HorizontalAlignment.Center, VerticalAlignment.Center).Also(s => s.MaxWidth = 1100);
        Children.Add(_capture);

        var root = new FocusList(horizontal: true);
        root.Add(_pad);
        root.Add(_rows);
        _pad.Index = Array.FindIndex(Hotspots, h => h.Button == PadButton.A);
        BuildTabs();
        UseScope(root);
        ShowBindings();
    }

    private string Ctx => ContextOrder[_context];

    private void BuildTabs()
    {
        _tabs.Children.Clear();
        for (int i = 0; i < ContextOrder.Length; i++)
        {
            var on = i == _context;
            _tabs.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(22), Padding = new Thickness(20, 8),
                Background = on ? T.Text : T.Surface,
                Child = Ui.Text(Keymap.ContextLabels[ContextOrder[i]], T.Caption, on ? T.Bg0 : T.TextMuted, FontWeight.SemiBold),
            });
        }
        _tabs.Children.Add(Ui.H(8, Hints.Glyph(PadButton.LT, 30), Hints.Glyph(PadButton.RT, 30)).Margin(10, 0, 0, 0));
    }

    /// <summary>Bindings in force for the focused button in this context, own first,
    /// then inherited ones (marked).</summary>
    private void ShowBindings()
    {
        _buttonTitle.Text = PadButtons.Label(_button);
        _rowPanel.Children.Clear();
        _rows.Clear();
        foreach (var (b, (spot, _)) in _spots)
        {
            var used = L.Keymap.Effective(Ctx).Any(x => (x.Input.Buttons & b) != 0);
            spot.Opacity = used ? 1 : 0.45;
        }

        var own = L.Keymap.In(Ctx).Where(x => (x.Input.Buttons & _button) != 0 || StickMatches(x.Input.Buttons)).ToList();
        var inherited = L.Keymap.Effective(Ctx).Where(x => !own.Contains(x) && ((x.Input.Buttons & _button) != 0 || StickMatches(x.Input.Buttons))).ToList();
        foreach (var b in own) AddRow(b, inherited: false);
        foreach (var b in inherited) AddRow(b, inherited: true);
        if (own.Count + inherited.Count == 0)
            _rowPanel.Children.Add(Ui.Muted("Does nothing here.", T.Body).Margin(24, 10, 0, 10));

        AddAction("plus", "Add an action to this button", AddBinding);
        AddAction("refresh", $"Reset \"{Keymap.ContextLabels[Ctx]}\"", () => L.Window.Ask("Reset this context?", "Its buttons go back to how Lounge ships.", ["Reset", "Cancel"], 1,
            i => { if (i == 0) { L.Keymap.ResetContext(Ctx); ShowBindings(); } }, "refresh"));
        RefreshFocus();
    }

    private bool StickMatches(PadButton combo)
    {
        // Sticks are drawn as one hotspot: the click and the four directions.
        var ls = PadButton.LSUp | PadButton.LSDown | PadButton.LSLeft | PadButton.LSRight;
        var rs = PadButton.RSUp | PadButton.RSDown | PadButton.RSLeft | PadButton.RSRight;
        return (_button == PadButton.LeftStick && (combo & ls) != 0) || (_button == PadButton.RightStick && (combo & rs) != 0);
    }

    private void AddRow(BoundAction b, bool inherited)
    {
        var info = Act.Describe(b.Action);
        var g = Ui.Grid("Auto,*", "Auto,Auto");
        g.Children.Add(Hints.Combo(b.Input, 36).Margin(0, 0, 22, 0).Col(0).Row(0, 2));
        g.Children.Add(Ui.Text(info.Label, T.Body, T.Text, FontWeight.SemiBold).Col(1).Row(0));
        var detail = b.Input.Gesture switch { Gesture.Hold => "Hold", Gesture.Analog => "Pressure", _ => "Press" };
        if (inherited) detail += $" · from {Keymap.ContextLabels[OwnerOf(b)]}";
        g.Children.Add(Ui.Muted(detail).Col(1).Row(1));
        var card = new Card(g.Margin(24, 16), 14) { FocusScale = 1.015, ShowRing = false };
        card.Body.Background = Brushes.Transparent;
        card.Focused += f => card.Body.Background = f ? T.Surface : Brushes.Transparent;
        card.Item.Activate = () => EditBinding(b, inherited);
        _rowPanel.Children.Add(card);
        _rows.Add(card.Item);
    }

    private string OwnerOf(BoundAction b) => Keymap.Chain(Ctx).FirstOrDefault(c => L.Keymap.In(c).Contains(b)) ?? Ctx;

    private void AddAction(string icon, string text, Action run)
    {
        var card = new Card(Ui.H(16, Icons.Make(icon, 28, T.Accent), Ui.Text(text, T.Body, T.Accent, FontWeight.SemiBold)).Margin(24, 16), 14) { FocusScale = 1.015, ShowRing = false };
        card.Body.Background = Brushes.Transparent;
        card.Focused += f => card.Body.Background = f ? T.Surface : Brushes.Transparent;
        card.Item.Activate = run;
        _rowPanel.Children.Add(card);
        _rows.Add(card.Item);
    }

    private void EditBinding(BoundAction b, bool inherited)
    {
        var ctx = inherited ? OwnerOf(b) : Ctx;
        L.Window.Menu(Act.Describe(b.Action).Label,
        [
            new MenuEntry("Change the button", "gamepad", () => Capture(b.Action, b.Input, ctx), "Press the new button or combination"),
            new MenuEntry("Change what it does", "list", () => PickAction(b.Input.Gesture == Gesture.Analog, a =>
            {
                L.Keymap.Rebind(ctx, a, null, b.Input, ConflictMode.Replace);
                ShowBindings();
            }), Act.Describe(b.Action).Label),
            new MenuEntry("Remove", "trash", () => { L.Keymap.Unbind(ctx, b.Action, b.Input); ShowBindings(); }, Danger: true),
        ]);
    }

    private void AddBinding()
    {
        var analog = _button is PadButton.LT or PadButton.RT or PadButton.LeftStick or PadButton.RightStick;
        var gestures = new List<MenuEntry>
        {
            new("When pressed", "gamepad", () => PickAction(false, a => Bind(new InputBinding(_button, Gesture.Press), a))),
            new("When held", "clock", () => PickAction(false, a => Bind(new InputBinding(_button, Gesture.Hold), a))),
        };
        if (analog)
            gestures.Add(new MenuEntry("By pressure or direction", "speed", () => PickAction(true, a =>
            {
                var buttons = _button switch
                {
                    PadButton.LeftStick => PadButton.LSUp | PadButton.LSDown | PadButton.LSLeft | PadButton.LSRight,
                    PadButton.RightStick => PadButton.RSUp | PadButton.RSDown | PadButton.RSLeft | PadButton.RSRight,
                    _ => _button,
                };
                Bind(new InputBinding(buttons, Gesture.Analog), a);
            })));
        L.Window.Menu($"{PadButtons.Label(_button)}: when?", gestures);
    }

    private void Bind(InputBinding input, string action)
    {
        var existing = L.Keymap.ActionFor(Ctx, input);
        if (existing is null || existing == action)
        {
            L.Keymap.Rebind(Ctx, action, null, input, ConflictMode.Replace);
            ShowBindings();
            return;
        }
        L.Window.Ask("That's already in use", $"{input.Describe()} does \"{Act.Describe(existing).Label}\" here.", ["Replace it", "Cancel"], 1, i =>
        {
            if (i != 0) return;
            L.Keymap.Rebind(Ctx, action, null, input, ConflictMode.Replace);
            ShowBindings();
        }, "gamepad");
    }

    private static void PickAction(bool analog, Action<string> pick) =>
        L.Window.Menu("Choose an action", Act.Catalog.Where(a => a.Analog == analog).Select(a => new MenuEntry(a.Label, "chevron", () => pick(a.Id))).ToList());

    private void Capture(string action, InputBinding old, string ctx)
    {
        _capturing = true;
        _captureText.Text = $"Press the new button for \"{Act.Describe(action).Label}\"";
        _capture.IsVisible = true;
        L.Play(UiSound.Open);
        L.Router.BeginCapture(result => Dispatcher.UIThread.Post(() =>
        {
            _capturing = false;
            _capture.IsVisible = false;
            if (result.Cancelled) { L.Play(UiSound.Back); return; }
            var input = result.Binding;
            if (Act.Describe(action).Analog)
            {
                var analogButtons = input.Buttons & (PadButton.LT | PadButton.RT);
                if (analogButtons == 0) { L.Window.Toast("That action needs a trigger.", "warning"); return; }
                input = new InputBinding(analogButtons, Gesture.Analog);
            }
            var conflict = L.Keymap.ActionFor(ctx, input);
            if (conflict is not null && conflict != action)
            {
                L.Window.Ask("That's already in use", $"{input.Describe()} does \"{Act.Describe(conflict).Label}\" here. Swap them, or replace it?",
                    ["Swap", "Replace", "Cancel"], 2, i =>
                    {
                        if (i == 2) return;
                        L.Keymap.Rebind(ctx, action, old, input, i == 0 ? ConflictMode.Swap : ConflictMode.Replace);
                        Done(input);
                    }, "gamepad");
                return;
            }
            L.Keymap.Rebind(ctx, action, old, input, ConflictMode.Replace);
            Done(input);
        }));
    }

    private void Done(InputBinding input)
    {
        L.Play(UiSound.Select);
        L.Rumble(0.3f, 70);
        L.Window.Toast($"Now {input.Describe()}", "check");
        ShowBindings();
    }

    public override bool Handle(ActionEvent e)
    {
        if (_capturing) return true;
        if (e.Phase == ActionPhase.Pressed)
        {
            switch (e.Action)
            {
                case Act.NextSection or Act.PrevSection:
                    _context = (_context + (e.Action == Act.NextSection ? 1 : ContextOrder.Length - 1)) % ContextOrder.Length;
                    L.Play(UiSound.Move);
                    BuildTabs();
                    ShowBindings();
                    return true;
                case Act.ContextMenu:
                    L.Window.Menu("Remote",
                    [
                        new MenuEntry($"Reset \"{Keymap.ContextLabels[Ctx]}\"", "refresh", () => { L.Keymap.ResetContext(Ctx); ShowBindings(); }),
                        new MenuEntry("Controllers and their button layouts", "gamepad", () => L.Window.Push(new ControllersScreen())),
                        new MenuEntry("Reset every button", "refresh", () => { L.Keymap.ResetAll(); ShowBindings(); }, Danger: true),
                    ]);
                    return true;
            }
        }
        return base.Handle(e);
    }
}
