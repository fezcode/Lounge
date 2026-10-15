using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core.Audio;
using Lounge.Core.Focus;
using Lounge.Core.Input;

namespace Lounge.Shell;

public sealed record KeyboardRequest(
    string Title,
    string Initial = "",
    string? LockedSuffix = null,
    bool SelectAll = true,
    IReadOnlyList<string>? Suggestions = null,
    Func<string, string?>? Validate = null,
    bool Secret = false);   // passwords: shown as dots, never suggested, never prefilled

/// <summary>The controller keyboard. Everything typed in Lounge goes through it: file
/// names, folder names, URLs, tile names, searches. X deletes, Y types a space, LB/RB
/// move the caret, LT switches shift and symbols, RT takes the first suggestion, Menu
/// finishes. When renaming, the extension is locked and shown beside the field, and the
/// name starts selected so one keypress replaces it.</summary>
public sealed class OnScreenKeyboard : Grid, IInputLayer
{
    private readonly KeyboardRequest _req;
    private readonly Action<string?> _done;
    private string _text;
    private int _caret;
    private bool _selectAll;
    private bool _shift;
    private bool _symbols;
    private readonly TextBlock _field = new() { FontSize = 40, FontWeight = FontWeight.Medium, Foreground = T.Text, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock _error = new() { FontSize = T.Caption, Foreground = T.Danger, FontWeight = FontWeight.SemiBold };
    private readonly StackPanel _suggestRow = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
    private readonly StackPanel _keys = new() { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
    private FocusScope _scope = null!;
    private FocusList _suggestFocus = null!;
    private readonly Spring _in;
    private bool _closed;
    private readonly DispatcherTimer _blink;
    private bool _caretOn = true;

    public string Context => "Keyboard";

    private static readonly string[][] Letters =
    [
        ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-"],
        ["q", "w", "e", "r", "t", "y", "u", "i", "o", "p", "'"],
        ["a", "s", "d", "f", "g", "h", "j", "k", "l", "(", ")"],
        ["⇧", "z", "x", "c", "v", "b", "n", "m", ",", ".", "⌫"],
        ["#+=", "←", "space", "→", "clear", "done"],
    ];

    private static readonly string[][] Symbols =
    [
        ["!", "@", "#", "$", "%", "&", "+", "=", "_", "~", "`"],
        ["[", "]", "{", "}", ";", ":", "/", "?", "<", ">", "|"],
        ["ç", "ğ", "ı", "ö", "ş", "ü", "é", "è", "à", "ñ", "ß"],
        ["⇧", "\\", "^", "*", "\"", "€", "£", "°", "·", "…", "⌫"],
        ["abc", "←", "space", "→", "clear", "done"],
    ];

    public OnScreenKeyboard(KeyboardRequest req, Action<string?> done)
    {
        _req = req;
        _done = done;
        _text = req.Initial;
        _caret = _text.Length;
        _selectAll = req.SelectAll && _text.Length > 0;
        Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));

        var fieldBox = new Border
        {
            Background = T.WithAlpha(Colors.Black, 0.35),
            BorderBrush = T.Accent,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(28, 18),
            Child = Ui.H(12, _field, LockedChip()),
            ClipToBounds = true,
        };

        var sheetContent = Ui.V(22,
            Ui.Grid("*,Auto", "Auto").Also(g =>
            {
                g.Children.Add(Ui.Text(req.Title, T.Heading, T.Text, T.TitleWeight).Col(0));
                g.Children.Add(Hints.Bar("Keyboard", (Act.KeyBackspace, "Delete"), (Act.KeySpace, "Space"), (Act.KeyShift, "Shift"), (Act.KeyDone, "Done"), (Act.Back, "Cancel")).Col(1));
            }),
            fieldBox,
            _error,
            _suggestRow,
            _keys);
        var sheet = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = T.SurfaceStrong,
            BorderBrush = T.Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(T.RadiusLarge, T.RadiusLarge, 0, 0),
            Padding = new Thickness(96, 40, 96, 48),
            Child = sheetContent,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = -20, Blur = 80, Color = Color.FromArgb(160, 0, 0, 0) }),
        };
        var tr = new TranslateTransform();
        sheet.RenderTransform = tr;
        Children.Add(sheet);
        Opacity = 0;
        _in = new Spring(0, v => { Opacity = Math.Clamp(v, 0, 1); tr.Y = 120 * (1 - v); }, 300, 26);
        _in.To(1);

        BuildKeys();
        Render();
        _blink = new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Background, (_, _) => { _caretOn = !_caretOn; Render(); });
        _blink.Start();
    }

    private Control LockedChip()
    {
        if (string.IsNullOrEmpty(_req.LockedSuffix)) return new Panel();
        return new Border
        {
            Background = T.WithAlpha(T.TextColor, 0.12),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.H(8, Icons.Make("eye-off", 22, T.TextMuted), Ui.Text(_req.LockedSuffix!, 30, T.TextMuted)),
        };
    }

    private void BuildKeys()
    {
        var focusedKey = _scope?.Focused?.Tag as string;
        _keys.Children.Clear();
        var rowsFocus = new FocusList(horizontal: false, rememberIndex: false);
        _suggestFocus = rowsFocus.Add(new FocusList(horizontal: true));
        BuildSuggestions();
        FocusItem? restore = null;
        foreach (var row in _symbols ? Symbols : Letters)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
            var rowFocus = rowsFocus.Add(new FocusList(horizontal: true, rememberIndex: false));
            foreach (var k in row)
            {
                var (label, width) = k switch
                {
                    "space" => ("space", 520.0), "clear" => ("Clear", 170.0), "done" => ("Done", 200.0),
                    "#+=" or "abc" => (k, 130.0), "←" or "→" => (k, 110.0), "⇧" => ("⇧", 110.0), "⌫" => ("⌫", 110.0),
                    _ => (_shift ? k.ToUpperInvariant() : k, 104.0),
                };
                var text = new TextBlock
                {
                    Text = label, FontSize = label.Length > 1 && label is not ("⇧" or "⌫" or "←" or "→") ? T.Body : 34,
                    FontWeight = FontWeight.SemiBold, Foreground = T.Text,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                var card = new Card(text, 14, k) { Width = width, Height = 84, FocusScale = 1.1 };
                var special = k is "space" or "clear" or "done" or "#+=" or "abc" or "←" or "→" or "⇧" or "⌫";
                var idle = k == "done" ? T.Accent : special ? T.WithAlpha(T.TextColor, 0.16) : T.WithAlpha(T.TextColor, 0.09);
                if (k == "⇧" && _shift) idle = T.WithAlpha(T.AccentColor, 0.6);
                card.Body.Background = idle;
                card.Focused += f => card.Body.Background = f && k != "done" ? T.WithAlpha(T.TextColor, 0.3) : idle;
                var key = k;
                card.Item.Activate = () => Press(key);
                panel.Children.Add(card);
                rowFocus.Add(card.Item);
                if (key == focusedKey) restore = card.Item;
            }
            _keys.Children.Add(panel);
        }
        _scope = new FocusScope(rowsFocus)
        {
            Locate = i => i.View is Visual v && v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), this) is { } p ? new Pt(p.X, p.Y) : null,
        };
        rowsFocus.Index = 1;
        _scope.Refresh();
        if (restore is not null) _scope.Focus(restore);
    }

    private void BuildSuggestions()
    {
        _suggestRow.Children.Clear();
        _suggestFocus.Clear();
        var list = (_req.Secret ? [] : _req.Suggestions ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s) && s != _text &&
                        (_text.Length == 0 || _selectAll || s.StartsWith(_text, StringComparison.OrdinalIgnoreCase)))
            .Distinct().Take(5).ToList();
        foreach (var s in list)
        {
            var card = new Card(Ui.Text(s, T.Caption, T.Text, FontWeight.SemiBold).Margin(22, 12), 30, s) { FocusScale = 1.06 };
            card.Body.Background = T.WithAlpha(T.TextColor, 0.1);
            var value = s;
            card.Item.Activate = () => { _text = value; _caret = _text.Length; _selectAll = false; Changed(); };
            _suggestRow.Children.Add(card);
            _suggestFocus.Add(card.Item);
        }
    }

    private void Press(string key)
    {
        switch (key)
        {
            case "⇧": _shift = !_shift; BuildKeys(); return;
            case "#+=": _symbols = true; BuildKeys(); return;
            case "abc": _symbols = false; BuildKeys(); return;
            case "⌫": Backspace(); return;
            case "←": MoveCaret(-1); return;
            case "→": MoveCaret(1); return;
            case "space": Insert(" "); return;
            case "clear": _text = ""; _caret = 0; _selectAll = false; Changed(); return;
            case "done": Finish(); return;
            default: Insert(_shift ? key.ToUpperInvariant() : key); return;
        }
    }

    private void Insert(string s)
    {
        if (_selectAll) { _text = ""; _caret = 0; _selectAll = false; }
        _text = _text.Insert(_caret, s);
        _caret += s.Length;
        if (_shift && !_symbols) { _shift = false; BuildKeys(); }
        Changed();
    }

    /// <summary>A real keyboard, when one is plugged in, types straight into the field.</summary>
    public void TypeFromKeyboard(string s) => Insert(s);

    private void Backspace()
    {
        if (_selectAll) { _text = ""; _caret = 0; _selectAll = false; Changed(); return; }
        if (_caret == 0) { L.Play(UiSound.Error); return; }
        _text = _text.Remove(_caret - 1, 1);
        _caret--;
        Changed();
    }

    private void MoveCaret(int d)
    {
        _selectAll = false;
        _caret = Math.Clamp(_caret + d, 0, _text.Length);
        Render();
    }

    private void Changed()
    {
        _error.Text = "";
        BuildSuggestions();
        _scope.Refresh();
        _caretOn = true;
        Render();
    }

    /// <summary>What the field shows: the text, or a dot per character for a password
    /// (a TV is a shared screen).</summary>
    private string Shown(string s) => _req.Secret ? new string('•', s.Length) : s;

    private void Render()
    {
        _field.Inlines ??= new InlineCollection();
        _field.Inlines.Clear();
        if (_selectAll)
        {
            _field.Inlines.Add(new Run(Shown(_text)) { Background = T.WithAlpha(T.AccentColor, 0.55) });
            return;
        }
        _field.Inlines.Add(new Run(Shown(_text[.._caret])));
        _field.Inlines.Add(new Run("|") { Foreground = _caretOn ? T.Accent : Brushes.Transparent, FontWeight = FontWeight.Light });
        _field.Inlines.Add(new Run(Shown(_text[_caret..])));
        if (_text.Length == 0) _field.Inlines.Add(new Run(" Type with the controller") { Foreground = T.TextFaint });
    }

    private void Finish()
    {
        var result = _text + (_req.LockedSuffix ?? "");
        if (_req.Validate?.Invoke(result) is { } problem)
        {
            _error.Text = problem;
            L.Play(UiSound.Error);
            L.Rumble(0.4f, 120);
            return;
        }
        Close(result);
    }

    private void Close(string? result)
    {
        if (_closed) return;
        _closed = true;
        _blink.Stop();
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
                (_scope.Focused?.View as Card)?.Pulse();
                L.Play(UiSound.Move);
                _scope.Activate();
                return true;
            case Act.KeyBackspace: Backspace(); return true;
            case Act.KeySpace: Insert(" "); return true;
            case Act.KeyCursorLeft: MoveCaret(-1); return true;
            case Act.KeyCursorRight: MoveCaret(1); return true;
            case Act.KeyShift:
                if (_shift) { _shift = false; _symbols = !_symbols; } else _shift = true;
                BuildKeys();
                return true;
            case Act.KeySuggest:
                if (_suggestFocus.Items.FirstOrDefault() is { } first) first.Activate?.Invoke();
                return true;
            case Act.KeyDone: Finish(); return true;
            case Act.Back: L.Play(UiSound.Back); Close(null); return true;
            case Act.Home: Close(null); return false;
            case Act.VolumeUp: case Act.VolumeDown: return false;
        }
        return true;
    }
}

public static class ControlExtensions
{
    public static TC Also<TC>(this TC c, Action<TC> a) { a(c); return c; }
}
