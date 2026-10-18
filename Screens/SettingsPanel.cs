using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core;
using Lounge.Core.Audio;
using Lounge.Core.Focus;
using Lounge.Core.Input;
using Lounge.Core.Settings;
using Lounge.Media;
using Lounge.Shell;

namespace Lounge.Screens;

/// <summary>Settings, tivi style: a panel slides in from the right with letter-spaced
/// section headers and rows of toggles, steppers and choices. Every row is generated
/// from the settings schema, so nothing can exist without a way to change it. Changes
/// save and apply at once; View on a row resets it.</summary>
public sealed class SettingsPanel : Grid, IInputLayer
{
    private readonly FocusScope _scope;
    private readonly FocusList _sections = new(horizontal: false, rememberIndex: true);
    private readonly FocusList _rows = new(horizontal: false, rememberIndex: true);
    private readonly StackPanel _sectionPanel = new() { Spacing = 4, Margin = new Thickness(16, 8, 16, 24) };
    private readonly StackPanel _rowPanel = new() { Spacing = 6, Margin = new Thickness(16, 8, 16, 24) };
    private readonly ScrollViewer _rowScroll;
    private readonly TextBlock _sectionTitle = Ui.Text("", T.Title, T.Text, T.TitleWeight, letterSpacing: 4);
    private readonly TextBlock _help = new() { FontSize = T.Caption, Foreground = T.TextMuted, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
    private readonly Dictionary<FocusItem, SettingDescriptor> _rowSettings = new();
    private readonly Dictionary<string, Action> _refresh = new();
    private readonly Spring _in;
    private readonly Border _sheet;
    private IDisposable? _debounce;
    private bool _closed;

    public string? Section { get; private set; }
    public string Context => "Shell";

    public SettingsPanel(string? section = null)
    {
        Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
        var root = new FocusList(horizontal: true);
        root.Add(_sections);
        root.Add(_rows);

        foreach (var name in SettingsSchema.Sections)
        {
            var label = Ui.Text(name.ToUpperInvariant(), T.Caption, T.TextMuted, FontWeight.Bold, letterSpacing: 3);
            var card = new Card(label.Margin(28, 18), 12) { FocusScale = 1.03, ShowRing = false };
            card.Body.Background = Brushes.Transparent;
            var sec = name;
            card.Focused += f =>
            {
                card.Body.Background = f ? T.WithAlpha(T.TextColor, 0.12) : Brushes.Transparent;
                label.Foreground = f || Section == sec ? T.Text : T.TextMuted;
                if (f && Section != sec)
                {
                    _debounce?.Dispose();
                    _debounce = DispatcherTimer.RunOnce(() => ShowSection(sec), TimeSpan.FromMilliseconds(90));
                }
            };
            card.Item.Activate = () => { ShowSection(sec); EnterRows(); };
            _sectionPanel.Children.Add(card);
            _sections.Add(card.Item);
        }

        _rowScroll = new ScrollViewer
        {
            Content = _rowPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var right = Ui.Grid("*", "Auto,*,Auto");
        right.Children.Add(_sectionTitle.Margin(8, 0, 0, 20).Row(0));
        right.Children.Add(_rowScroll.Row(1));
        right.Children.Add(Ui.V(14,
            new Border { Height = 1, Background = T.Line },
            _help,
            Hints.Bar("Shell", (Act.Accept, "Change"), (Act.ContextMenu, "Reset"), (Act.Back, "Back"))).Margin(8, 16, 8, 0).Row(2));

        var layout = Ui.Grid("320,*", "*");
        layout.Children.Add(Ui.V(18,
            Ui.Text("SETTINGS", T.Caption, T.Accent, FontWeight.Bold, letterSpacing: 6).Margin(28, 0, 0, 10),
            new ScrollViewer { Content = _sectionPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }).Col(0));
        layout.Children.Add(right.Margin(36, 0, 0, 0).Col(1));

        _sheet = new Border
        {
            Width = 1320,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = T.SurfaceStrong,
            BorderBrush = T.Line,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(40, 64, 64, 48),
            Child = layout,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = -30, Blur = 90, Color = Color.FromArgb(170, 0, 0, 0) }),
        };
        var tr = new TranslateTransform();
        _sheet.RenderTransform = tr;
        Children.Add(_sheet);
        Opacity = 0;
        _in = new Spring(0, v => { Opacity = Math.Clamp(v, 0, 1); tr.X = 160 * (1 - v); }, 300, 28);
        _in.To(1);

        _scope = new FocusScope(root)
        {
            Locate = i => i.View is Visual v && v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), this) is { } p ? new Pt(p.X, p.Y) : null,
        };
        _scope.Moved += (_, now) =>
        {
            if (now?.View is Control c) SmoothScroll.BringIntoView(c, 40, 140);
            _help.Text = now is not null && _rowSettings.TryGetValue(now, out var d) ? d.Help : "";
        };

        var start = section is not null && SettingsSchema.Sections.Contains(section) ? section : SettingsSchema.Sections[0];
        _sections.Index = SettingsSchema.Sections.ToList().IndexOf(start);
        ShowSection(start);
        _scope.Refresh();
        if (section is not null) EnterRows();
        L.Settings.Changed += OnChanged;
    }

    private void OnChanged(string key)
    {
        if (_refresh.TryGetValue(key, out var r)) Dispatcher.UIThread.Post(r);
    }

    private void ShowSection(string name)
    {
        Section = name;
        _sectionTitle.Text = T.Header(name).ToUpperInvariant();
        _rowPanel.Children.Clear();
        _rows.Clear();
        _rowSettings.Clear();
        _refresh.Clear();
        foreach (var d in SettingsSchema.All.Where(d => d.Section == name))
            AddRow(d);
        if (name == "About") AddAbout();
        if (name == "Library") AddRescanStatus();
        SmoothScroll.Reset(_rowScroll);
        _scope.Refresh();
    }

    private void EnterRows()
    {
        if (!_rows.HasItems) return;
        var first = _rows.Items.First();
        _scope.Focus(_rows.Current ?? first);
        L.Play(UiSound.Move);
    }

    // ------------------------------------------------------------------ rows

    private void AddRow(SettingDescriptor d)
    {
        var label = Ui.Text(d.Label, T.Body, T.Text, FontWeight.Medium);
        var value = Ui.Text("", T.Body, T.TextMuted, FontWeight.SemiBold);
        var left = Icons.Make("back", 22, T.TextFaint);
        var right = Icons.Make("chevron", 22, T.TextFaint);
        Control trailing;
        Border? knobTrack = null;
        Border? knob = null;
        Spring? knobSpring = null;

        switch (d.Kind)
        {
            case SettingKind.Toggle:
                knob = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
                knobTrack = new Border { Width = 76, Height = 42, CornerRadius = new CornerRadius(21), Padding = new Thickness(4), Child = knob };
                var kt = new TranslateTransform();
                knob.RenderTransform = kt;
                var track = knobTrack;
                knobSpring = new Spring(L.Settings.Bool(d.Key) ? 1 : 0, v =>
                {
                    kt.X = 34 * v;
                    track.Background = new SolidColorBrush(Lerp(Color.FromArgb(70, 255, 255, 255), T.AccentColor, Math.Clamp(v, 0, 1)));
                }, 500, 32);
                trailing = knobTrack;
                break;
            case SettingKind.Integer or SettingKind.Number or SettingKind.Choice:
                left.Opacity = right.Opacity = 0;
                trailing = Ui.H(14, left, value, right);
                break;
            default:
                trailing = Ui.H(14, value, Icons.Make("chevron", 22, T.TextFaint));
                break;
        }

        var row = Ui.Grid("*,Auto", "Auto");
        row.Children.Add(label.Col(0).Align(HorizontalAlignment.Left, VerticalAlignment.Center));
        row.Children.Add(trailing.Col(1).Align(HorizontalAlignment.Right, VerticalAlignment.Center));
        var card = new Card(row.Margin(28, 20), 16) { FocusScale = 1.015, ShowRing = false };
        card.Body.Background = Brushes.Transparent;
        card.Focused += f =>
        {
            card.Body.Background = f ? T.WithAlpha(T.TextColor, 0.12) : Brushes.Transparent;
            left.Opacity = right.Opacity = f && d.Kind is SettingKind.Integer or SettingKind.Number or SettingKind.Choice ? 1 : 0;
        };

        void Refresh()
        {
            var v = L.Settings.Get(d.Key);
            value.Text = d.ShowValue(v);
            value.Foreground = L.Settings.IsDefault(d.Key) ? T.TextMuted : T.Text;
            knobSpring?.To(v is true ? 1 : 0);
        }
        Refresh();
        _refresh[d.Key] = Refresh;

        card.Item.Activate = () => Activate(d);
        _rowPanel.Children.Add(card);
        _rows.Add(card.Item);
        _rowSettings[card.Item] = d;
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private void Activate(SettingDescriptor d)
    {
        switch (d.Kind)
        {
            case SettingKind.Toggle:
                L.Settings.Step(d.Key, 1);
                L.Play(UiSound.Toggle);
                L.Rumble(0.15f, 30);
                break;
            case SettingKind.Choice:
                var current = L.Settings.String(d.Key);
                // Only the selected choice is ticked (on the right); a tick on every row read as all selected.
                L.Window.Menu(d.Label, d.Choices.Select(c => new MenuEntry(c.Label, "",
                    () => L.Settings.Set(d.Key, c.Value), Checked: c.Value == current)).ToList());
                break;
            case SettingKind.Integer or SettingKind.Number:
                L.Settings.Step(d.Key, 1);
                break;
            case SettingKind.Path:
                PickPath(d);
                break;
            case SettingKind.Folders:
                L.Window.Push(new FoldersScreen(d));
                Close();
                break;
            case SettingKind.Action:
                RunAction(d);
                break;
        }
    }

    private void PickPath(SettingDescriptor d)
    {
        var folder = d.Key is K.AmbientFolder or K.ScreenshotFolder;
        var current = L.Settings.String(d.Key);
        L.Window.Menu(d.Label,
        [
            new MenuEntry(folder ? "Choose a folder in Files" : "Choose in Files", "folder", () =>
            {
                Close();
                L.Window.Push(new FilesScreen(
                    start: string.IsNullOrEmpty(current) ? null : (folder ? current : Path.GetDirectoryName(current)),
                    pick: new FilePick(folder ? FilePickKind.Folder : d.Key == K.CogasPath ? FilePickKind.Program : FilePickKind.Image, d.Label,
                        path => { L.Settings.Set(d.Key, path); L.Window.Toast($"{d.Label}: {Path.GetFileName(path.TrimEnd('\\'))}", "check"); })));
            }),
            new MenuEntry("Use the default", "refresh", () => L.Settings.Reset(d.Key)),
        ]);
    }

    private void RunAction(SettingDescriptor d)
    {
        switch (d.Key)
        {
            case K.OpenRemote:
                Close();
                L.Window.Push(new RemoteScreen());
                break;
            case K.OpenControllers:
                Close();
                L.Window.Push(new ControllersScreen());
                break;
            case K.Rescan:
                _ = LibraryService.ScanAsync(quiet: false);
                L.Window.Toast("Scanning your library...", "refresh");
                break;
            case K.ResetAll:
                L.Window.Ask("Reset all settings?", "Every setting and button goes back to its default. Your library, history and tiles stay.",
                    ["Reset everything", "Cancel"], 1, i =>
                    {
                        if (i != 0) return;
                        L.Settings.ResetAll();
                        L.Keymap.ResetAll();
                        L.Window.Toast("Settings are back to their defaults.", "refresh");
                    }, "warning");
                break;
        }
    }

    private void AddAbout()
    {
        var lines = new (string, string)[]
        {
            ("Version", $"{Product.Name} {Product.Version}"),
            ("Data folder", AppPaths.Root),
            ("Video engine", Ff.Available ? $"FFmpeg 8 · {(L.Player.HardwareDecoder ?? "hardware decoding when available")}" : Ff.Error ?? "not loaded"),
            ("Subtitles", L.Player.Subtitles.Available ? "libass" : "unavailable"),
            ("Controllers", $"{L.Pad.ConnectedCount} connected"),
            ("Agent", File.Exists(L.AgentPath) ? (L.Settings.Bool(K.AgentEnabled) ? "starts with Windows" : "off") : "not installed"),
        };
        var info = new StackPanel { Spacing = 10, Margin = new Thickness(28, 30, 28, 0) };
        foreach (var (k, v) in lines)
        {
            var g = Ui.Grid("260,*", "Auto");
            g.Children.Add(Ui.Muted(k, T.Caption).Col(0));
            g.Children.Add(Ui.Text(v, T.Caption, T.Text, FontWeight.Medium, TextWrapping.Wrap).Col(1));
            info.Children.Add(g);
        }
        _rowPanel.Children.Add(info);
    }

    private void AddRescanStatus()
    {
        var counts = $"{L.Library.Count(Lounge.Core.Library.LibraryKind.Video)} videos · {L.Library.Count(Lounge.Core.Library.LibraryKind.Music)} songs";
        _rowPanel.Children.Add(Ui.Muted(counts, T.Caption).Margin(28, 20, 0, 0));
    }

    // ------------------------------------------------------------------ input

    private bool InRows => _scope.Focused is { } f && _rowSettings.ContainsKey(f);

    public bool Handle(ActionEvent e)
    {
        if (e.Phase is ActionPhase.Released or ActionPhase.Changed) return true;
        var repeat = e.Phase == ActionPhase.Repeated;
        switch (e.Action)
        {
            case Act.Up: if (_scope.Move(Dir.Up)) L.Play(UiSound.Move); return true;
            case Act.Down: if (_scope.Move(Dir.Down)) L.Play(UiSound.Move); return true;
            case Act.Left or Act.Right:
                var dir = e.Action == Act.Left ? -1 : 1;
                if (InRows && _rowSettings[_scope.Focused!] is { Kind: SettingKind.Integer or SettingKind.Number or SettingKind.Choice } d)
                {
                    L.Settings.Step(d.Key, dir);
                    L.Play(UiSound.Move);
                    return true;
                }
                if (InRows && _rowSettings[_scope.Focused!] is { Kind: SettingKind.Toggle } t && !repeat)
                {
                    if (L.Settings.Bool(t.Key) != (dir > 0)) { L.Settings.Set(t.Key, dir > 0); L.Play(UiSound.Toggle); return true; }
                }
                if (dir < 0 && InRows) { _scope.Focus(_sections.Current!); L.Play(UiSound.Move); return true; }
                if (dir > 0 && !InRows) { EnterRows(); return true; }
                return true;
            case Act.Accept:
                if (repeat) return true;
                (_scope.Focused?.View as Card)?.Pulse();
                if (!InRows) L.Play(UiSound.Select);
                _scope.Activate();
                return true;
            case Act.ContextMenu:
                if (InRows && _rowSettings[_scope.Focused!] is { Kind: not SettingKind.Action } r)
                    L.Window.Menu(r.Label,
                    [
                        new MenuEntry("Reset to default", "refresh", () => { L.Settings.Reset(r.Key); L.Window.Toast($"{r.Label} reset.", "refresh"); }),
                    ]);
                return true;
            case Act.Back:
                L.Play(UiSound.Back);
                if (InRows) _scope.Focus(_sections.Current!);
                else Close();
                return true;
            case Act.Settings:
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
        if (_closed) return;
        _closed = true;
        L.Settings.Changed -= OnChanged;
        L.Play(UiSound.Back);
        _in.To(0);
        L.Window.DismissOverlay(this, 260);
    }
}
