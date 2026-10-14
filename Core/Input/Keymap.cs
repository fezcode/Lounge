using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lounge.Core.Input;

public enum Gesture
{
    /// <summary>Fires when pressed (or on release, if the same button also has a hold or chord).</summary>
    Press,
    /// <summary>Fires once the buttons have been held for the hold time.</summary>
    Hold,
    /// <summary>Continuous: trigger pressure or stick deflection, reported every poll.</summary>
    Analog,
}

/// <summary>An input pattern: one or more buttons held together, with a gesture.
/// <see cref="HoldMs"/> overrides the global hold time (the return chord uses its own).</summary>
public readonly record struct InputBinding(PadButton Buttons, Gesture Gesture, int? HoldMs = null)
{
    public bool IsChord => PadButtons.Count(Buttons) > 1;

    public string Describe() => Gesture switch
    {
        Gesture.Hold => "Hold " + PadButtons.FormatLabel(Buttons),
        Gesture.Analog => PadButtons.FormatLabel(Buttons) + " (pressure)",
        _ => PadButtons.FormatLabel(Buttons),
    };

    /// <summary>Same buttons and gesture: two bindings that would fight.</summary>
    public bool SameInput(InputBinding other) => Buttons == other.Buttons && Gesture == other.Gesture;
}

public sealed record BoundAction(InputBinding Input, string Action);

public enum ConflictMode { Swap, Replace }

/// <summary>Bindings per input context. A context may inherit from a parent: Player
/// inherits Global, so RB keeps changing the volume during playback unless Player
/// binds RB itself.</summary>
public sealed class Keymap
{
    public const int Version = 1;

    public static readonly IReadOnlyDictionary<string, string?> Parents = new Dictionary<string, string?>
    {
        ["Global"] = null,
        ["Shell"] = "Global",
        ["Player"] = "Global",
        ["Keyboard"] = "Global",
        ["Files"] = "Global",
        ["Web"] = null,
        ["Cursor"] = null,
        ["Dormant"] = null,
    };

    public static readonly IReadOnlyDictionary<string, string> ContextLabels = new Dictionary<string, string>
    {
        ["Global"] = "Everywhere",
        ["Shell"] = "Menus",
        ["Player"] = "Watching",
        ["Keyboard"] = "On-screen keyboard",
        ["Files"] = "Files",
        ["Web"] = "Web pages",
        ["Cursor"] = "Controller mouse",
        ["Dormant"] = "While an app runs",
    };

    private readonly Dictionary<string, List<BoundAction>> _map = new(StringComparer.Ordinal);

    public event Action? Changed;

    public IReadOnlyList<BoundAction> In(string context) =>
        _map.TryGetValue(context, out var l) ? l : (IReadOnlyList<BoundAction>)[];

    public IEnumerable<string> Contexts => Parents.Keys;

    /// <summary>The context chain, most specific first: Player → Global.</summary>
    public static IEnumerable<string> Chain(string context)
    {
        for (string? c = context; c is not null; c = Parents.TryGetValue(c, out var p) ? p : null)
            yield return c;
    }

    /// <summary>The bindings in force for <paramref name="context"/>: its own, plus every
    /// inherited one whose input it does not bind itself.</summary>
    public List<BoundAction> Effective(string context)
    {
        var result = new List<BoundAction>();
        foreach (var c in Chain(context))
            foreach (var b in In(c))
                if (!result.Any(r => r.Input.SameInput(b.Input)))
                    result.Add(b);
        return result;
    }

    public void Add(string context, InputBinding input, string action)
    {
        if (!_map.TryGetValue(context, out var list)) _map[context] = list = new();
        list.RemoveAll(b => b.Input.SameInput(input));
        list.Add(new BoundAction(input, action));
    }

    public string? ActionFor(string context, InputBinding input) =>
        In(context).FirstOrDefault(b => b.Input.SameInput(input))?.Action;

    public IEnumerable<InputBinding> BindingsFor(string context, string action) =>
        In(context).Where(b => b.Action == action).Select(b => b.Input);

    /// <summary>Changes the input of one binding. When the new input is already bound in
    /// the same context, <paramref name="mode"/> decides: Swap gives the other action the
    /// old input; Replace drops the other binding. Returns the displaced action, if any.</summary>
    public string? Rebind(string context, string action, InputBinding? oldInput, InputBinding newInput, ConflictMode mode)
    {
        if (!_map.TryGetValue(context, out var list)) _map[context] = list = new();
        var displaced = list.FirstOrDefault(b => b.Input.SameInput(newInput) && b.Action != action);
        if (oldInput is { } o) list.RemoveAll(b => b.Action == action && b.Input.SameInput(o));
        if (displaced is not null)
        {
            list.Remove(displaced);
            if (mode == ConflictMode.Swap && oldInput is { } old)
                list.Add(new BoundAction(old with { Gesture = Fit(displaced.Action, old.Gesture) }, displaced.Action));
        }
        list.Add(new BoundAction(newInput, action));
        Changed?.Invoke();
        return displaced?.Action;
    }

    private static Gesture Fit(string action, Gesture g) =>
        Act.Describe(action).Analog ? Gesture.Analog : g == Gesture.Analog ? Gesture.Press : g;

    public void Unbind(string context, string action, InputBinding input)
    {
        if (_map.TryGetValue(context, out var list) &&
            list.RemoveAll(b => b.Action == action && b.Input.SameInput(input)) > 0)
            Changed?.Invoke();
    }

    public void ResetContext(string context)
    {
        var d = Defaults();
        _map[context] = d.In(context).ToList();
        Changed?.Invoke();
    }

    public void ResetAll()
    {
        _map.Clear();
        foreach (var c in Parents.Keys) _map[c] = Defaults().In(c).ToList();
        Changed?.Invoke();
    }

    // ---- defaults

    public static Keymap Defaults()
    {
        var k = new Keymap();
        void P(string ctx, PadButton b, string a) => k.Add(ctx, new InputBinding(b, Gesture.Press), a);
        void H(string ctx, PadButton b, string a, int? ms = null) => k.Add(ctx, new InputBinding(b, Gesture.Hold, ms), a);
        void N(string ctx, PadButton b, string a) => k.Add(ctx, new InputBinding(b, Gesture.Analog), a);

        P("Global", PadButton.DPadUp, Act.Up); P("Global", PadButton.DPadDown, Act.Down);
        P("Global", PadButton.DPadLeft, Act.Left); P("Global", PadButton.DPadRight, Act.Right);
        P("Global", PadButton.LSUp, Act.Up); P("Global", PadButton.LSDown, Act.Down);
        P("Global", PadButton.LSLeft, Act.Left); P("Global", PadButton.LSRight, Act.Right);
        P("Global", PadButton.RSUp, Act.PageUp); P("Global", PadButton.RSDown, Act.PageDown);
        P("Global", PadButton.A, Act.Accept); P("Global", PadButton.B, Act.Back);
        P("Global", PadButton.X, Act.Secondary); P("Global", PadButton.Y, Act.Tertiary);
        P("Global", PadButton.Menu, Act.Settings);
        P("Global", PadButton.View, Act.ContextMenu);
        H("Global", PadButton.View, Act.Home);
        P("Global", PadButton.Guide, Act.Home);
        P("Global", PadButton.RB, Act.VolumeUp); P("Global", PadButton.LB, Act.VolumeDown);
        P("Global", PadButton.LT, Act.PrevSection); P("Global", PadButton.RT, Act.NextSection);
        H("Global", PadButton.View | PadButton.B, Act.CursorToggle);

        // On menus, holding A picks a tile up to move it (so A selects on release there).
        H("Shell", PadButton.A, Act.MoveMode);

        P("Player", PadButton.A, Act.PlayPause);
        P("Player", PadButton.B, Act.Stop);
        N("Player", PadButton.RT, Act.SeekForward);
        N("Player", PadButton.LT, Act.SeekBackward);
        P("Player", PadButton.DPadLeft, Act.SkipBack); P("Player", PadButton.DPadRight, Act.SkipForward);
        P("Player", PadButton.DPadUp, Act.BigSkipForward); P("Player", PadButton.DPadDown, Act.BigSkipBack);
        P("Player", PadButton.LSLeft, Act.SkipBack); P("Player", PadButton.LSRight, Act.SkipForward);
        P("Player", PadButton.Y, Act.Tracks); P("Player", PadButton.X, Act.Info);
        P("Player", PadButton.RightStick, Act.ToggleSubtitles);
        P("Player", PadButton.RSRight, Act.NextItem); P("Player", PadButton.RSLeft, Act.PrevItem);

        P("Keyboard", PadButton.X, Act.KeyBackspace); P("Keyboard", PadButton.Y, Act.KeySpace);
        P("Keyboard", PadButton.LB, Act.KeyCursorLeft); P("Keyboard", PadButton.RB, Act.KeyCursorRight);
        P("Keyboard", PadButton.LT, Act.KeyShift); P("Keyboard", PadButton.RT, Act.KeySuggest);
        P("Keyboard", PadButton.Menu, Act.KeyDone);

        P("Files", PadButton.X, Act.FileSelect); P("Files", PadButton.Y, Act.FileRename);

        H("Web", PadButton.View, Act.Home);
        H("Web", PadButton.View | PadButton.B, Act.CursorToggle);

        N("Cursor", PadButton.LSUp | PadButton.LSDown | PadButton.LSLeft | PadButton.LSRight, Act.CursorMove);
        N("Cursor", PadButton.RSUp | PadButton.RSDown | PadButton.RSLeft | PadButton.RSRight, Act.CursorScroll);
        P("Cursor", PadButton.A, Act.CursorClick); P("Cursor", PadButton.X, Act.CursorRightClick);
        P("Cursor", PadButton.Y, Act.CursorKeyboard); P("Cursor", PadButton.B, Act.CursorBack);
        H("Cursor", PadButton.View, Act.Home);
        H("Cursor", PadButton.View | PadButton.B, Act.CursorToggle);
        P("Cursor", PadButton.RB, Act.VolumeUp); P("Cursor", PadButton.LB, Act.VolumeDown);

        H("Dormant", PadButton.View | PadButton.Menu, Act.Return);
        return k;
    }

    // ---- persistence

    public string ToJson()
    {
        var contexts = new JsonObject();
        foreach (var c in Parents.Keys)
        {
            var arr = new JsonArray();
            foreach (var b in In(c))
            {
                var o = new JsonObject
                {
                    ["input"] = PadButtons.Format(b.Input.Buttons),
                    ["gesture"] = b.Input.Gesture.ToString().ToLowerInvariant(),
                    ["action"] = b.Action,
                };
                if (b.Input.HoldMs is { } ms) o["holdMs"] = ms;
                arr.Add(o);
            }
            contexts[c] = arr;
        }
        return new JsonObject { ["version"] = Version, ["contexts"] = contexts }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Parses keymap.json. Unknown contexts and malformed bindings are skipped;
    /// a context missing from the file keeps its defaults.</summary>
    public static Keymap FromJson(string json)
    {
        var k = Defaults();
        var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("keymap.json is not an object");
        if (root["contexts"] is not JsonObject contexts) return k;
        foreach (var (ctx, node) in contexts)
        {
            if (!Parents.ContainsKey(ctx) || node is not JsonArray arr) continue;
            var list = new List<BoundAction>();
            foreach (var item in arr.OfType<JsonObject>())
            {
                var input = item["input"]?.GetValue<string>();
                var action = item["action"]?.GetValue<string>();
                var gesture = item["gesture"]?.GetValue<string>() ?? "press";
                if (input is null || action is null || !PadButtons.TryParse(input, out var buttons)) continue;
                if (!Enum.TryParse<Gesture>(gesture, true, out var g)) continue;
                int? ms = item["holdMs"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
                list.Add(new BoundAction(new InputBinding(buttons, g, ms), action));
            }
            k._map[ctx] = list;
        }
        return k;
    }

    public static Keymap Load(string path, out string? notice)
    {
        notice = null;
        if (!File.Exists(path)) return Defaults();
        try { return FromJson(File.ReadAllText(path)); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            var moved = AtomicFile.SetAside(path);
            notice = "Your button mapping could not be read, so the defaults are back." +
                     (moved is null ? "" : $" The old file was kept as {Path.GetFileName(moved)}.");
            return Defaults();
        }
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, ToJson());
}
