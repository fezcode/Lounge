namespace Lounge.Core.Input;

public enum ControllerKind { XInput, Hid }

/// <summary>A controller as Lounge knows it. <see cref="Id"/> is what its saved layout is
/// filed under: an HID pad's serial number or device path, or for an XInput pad (Windows
/// exposes no serial) its model and slot.</summary>
public sealed record ControllerInfo(string Id, string Name, ControllerKind Kind, int VendorId, int ProductId);

/// <summary>One controller's raw inputs, by source name: digital ones are 0 or 1, axes
/// -1..1. XInput pads use the Xbox names ("A", "DPadUp", "lx", "lt"); HID pads use
/// "b1".."b32" for buttons, "hat:up"/"hat:down"/"hat:left"/"hat:right" for the hat switch
/// and "x", "y", "z", "rx", "ry", "rz", "slider", "dial" for axes (in their raw direction).</summary>
public sealed class RawPad
{
    public Dictionary<string, float> Inputs { get; } = new();

    public float this[string source] => Inputs.TryGetValue(source, out var v) ? v : 0;

    public static bool IsAxis(string source) => source is "lx" or "ly" or "rx" or "ry" or "lt" or "rt" or "x" or "y" or "z" or "rz" or "slider" or "dial" or "accel" or "brake";
}

/// <summary>Where one Xbox-layout input comes from on a controller. For an axis source,
/// <see cref="Sign"/> is the direction that counts as "on" (or as up/right for a stick)
/// and <see cref="Rest"/> where the axis sits untouched (-1 for most HID triggers).</summary>
public sealed record SourceBinding(string Source, int Sign = 1, float Rest = 0)
{
    /// <summary>How far the input is pressed, 0..1.</summary>
    public float Amount(RawPad raw)
    {
        var v = raw[Source];
        if (!RawPad.IsAxis(Source)) return v > 0.5f ? 1 : 0;
        var extreme = Sign > 0 ? 1f : -1f;
        var range = Math.Abs(extreme - Rest);
        return range < 0.01f ? 0 : Math.Clamp((v - Rest) * Sign / range, 0, 1);
    }

    /// <summary>The input as a stick axis, -1..1 with the bound direction positive.</summary>
    public float Axis(RawPad raw) =>
        RawPad.IsAxis(Source) ? Math.Clamp((raw[Source] - Rest) * Sign, -1, 1) : (raw[Source] > 0.5f ? 1 : 0);
}

/// <summary>A controller's button layout: which of its inputs is A, B, the D-pad, the
/// sticks and triggers. Applying it turns any controller into the Xbox layout the action
/// map speaks, so every controller can drive Lounge with its own buttons at once.</summary>
public static class ControllerLayout
{
    /// <summary>What a layout can bind, in the order the set-up asks for them.</summary>
    public static readonly string[] Targets =
    [
        "A", "B", "X", "Y", "DPadUp", "DPadDown", "DPadLeft", "DPadRight",
        "LB", "RB", "LT", "RT", "View", "Menu", "Guide", "LeftStick", "RightStick",
        "LY", "LX", "RY", "RX",
    ];

    public static bool IsStickAxis(string target) => target is "LX" or "LY" or "RX" or "RY";

    /// <summary>What the set-up asks the viewer to do for a target.</summary>
    public static string Prompt(string target) => target switch
    {
        "LY" => "Push the left stick up",
        "LX" => "Push the left stick right",
        "RY" => "Push the right stick up",
        "RX" => "Push the right stick right",
        "LeftStick" => "Click the left stick in",
        "RightStick" => "Click the right stick in",
        "LT" => "Pull the left trigger (LT)",
        "RT" => "Pull the right trigger (RT)",
        _ => $"Press the button you want as {Label(target)}",
    };

    public static string Label(string target) => target switch
    {
        "DPadUp" => "D-pad Up", "DPadDown" => "D-pad Down", "DPadLeft" => "D-pad Left", "DPadRight" => "D-pad Right",
        "Guide" => "the Xbox / Home button", "View" => "View (Back / Select)", "Menu" => "Menu (Start)",
        "LeftStick" => "left stick click", "RightStick" => "right stick click",
        "LX" => "left stick, sideways", "LY" => "left stick, up and down", "RX" => "right stick, sideways", "RY" => "right stick, up and down",
        _ => target,
    };

    /// <summary>The controller's state in the Xbox layout.</summary>
    public static PadState Apply(IReadOnlyDictionary<string, SourceBinding> layout, RawPad raw)
    {
        var buttons = PadButton.None;
        float lt = 0, rt = 0, lx = 0, ly = 0, rx = 0, ry = 0;
        foreach (var (target, b) in layout)
        {
            switch (target)
            {
                case "LT": lt = Math.Max(lt, b.Amount(raw)); break;
                case "RT": rt = Math.Max(rt, b.Amount(raw)); break;
                case "LX": lx = b.Axis(raw); break;
                case "LY": ly = b.Axis(raw); break;
                case "RX": rx = b.Axis(raw); break;
                case "RY": ry = b.Axis(raw); break;
                default:
                    if (Enum.TryParse<PadButton>(target, out var pb) && (uint)pb <= 0xFFFF && b.Amount(raw) >= 0.5f) buttons |= pb;
                    break;
            }
        }
        return new PadState(true, buttons, lt, rt, lx, ly, rx, ry);
    }

    // ------------------------------------------------------------------ defaults

    /// <summary>An XInput pad already speaks the Xbox layout.</summary>
    public static Dictionary<string, SourceBinding> XInputDefault()
    {
        var d = new Dictionary<string, SourceBinding>();
        foreach (var t in Targets)
            d[t] = t switch
            {
                "LX" => new("lx"), "LY" => new("ly"), "RX" => new("rx"), "RY" => new("ry"),
                "LT" => new("lt"), "RT" => new("rt"),
                _ => new(t),
            };
        return d;
    }

    /// <summary>A best guess for an HID pad until its buttons are set up: the common
    /// layouts by maker (positions, not letters: Xbox A is the bottom face button).</summary>
    public static Dictionary<string, SourceBinding> HidDefault(int vendorId)
    {
        string[] face; // bottom, right, left, top, LB, RB, LT, RT, View, Menu, LS, RS, Guide
        var d = new Dictionary<string, SourceBinding>
        {
            ["DPadUp"] = new("hat:up"), ["DPadDown"] = new("hat:down"), ["DPadLeft"] = new("hat:left"), ["DPadRight"] = new("hat:right"),
            ["LX"] = new("x"), ["LY"] = new("y", -1),   // HID Y grows downwards
        };
        switch (vendorId)
        {
            case 0x054C:   // Sony: Square, Cross, Circle, Triangle, L1, R1, L2, R2, Share, Options, L3, R3, PS
                face = ["b2", "b3", "b1", "b4", "b5", "b6", "b7", "b8", "b9", "b10", "b11", "b12", "b13"];
                d["RX"] = new("z"); d["RY"] = new("rz", -1);
                break;
            case 0x057E:   // Nintendo (Switch Pro): B, A, Y, X, L, R, ZL, ZR, Minus, Plus, LS, RS, Home
                face = ["b1", "b2", "b3", "b4", "b5", "b6", "b7", "b8", "b9", "b10", "b11", "b12", "b13"];
                d["RX"] = new("rx"); d["RY"] = new("ry", -1);
                break;
            case 0x2DC8:   // 8BitDo in D-input: A, B, -, X, Y, -, L, R, L2, R2, Select, Start, Home, LS, RS (Nintendo positions: B bottom, A right)
                face = ["b2", "b1", "b5", "b4", "b7", "b8", "b9", "b10", "b11", "b12", "b14", "b15", "b13"];
                d["RX"] = new("z"); d["RY"] = new("rz", -1);
                break;
            default:
                face = ["b1", "b2", "b3", "b4", "b5", "b6", "b7", "b8", "b9", "b10", "b11", "b12", "b13"];
                d["RX"] = new("z"); d["RY"] = new("rz", -1);
                break;
        }
        string[] targets = ["A", "B", "X", "Y", "LB", "RB", "LT", "RT", "View", "Menu", "LeftStick", "RightStick", "Guide"];
        for (var i = 0; i < targets.Length; i++) d[targets[i]] = new(face[i]);
        return d;
    }
}
