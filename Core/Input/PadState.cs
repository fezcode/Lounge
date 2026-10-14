namespace Lounge.Core.Input;

/// <summary>Every input the router understands as a button. The low 16 bits match
/// XInput's wButtons (Guide is bit 0x400 via XInputGetStateEx); triggers and stick
/// directions become buttons once they pass their thresholds.</summary>
[Flags]
public enum PadButton : uint
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Menu = 0x0010,
    View = 0x0020,
    LeftStick = 0x0040,
    RightStick = 0x0080,
    LB = 0x0100,
    RB = 0x0200,
    Guide = 0x0400,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
    LT = 1u << 16,
    RT = 1u << 17,
    LSUp = 1u << 18,
    LSDown = 1u << 19,
    LSLeft = 1u << 20,
    LSRight = 1u << 21,
    RSUp = 1u << 22,
    RSDown = 1u << 23,
    RSLeft = 1u << 24,
    RSRight = 1u << 25,
}

public static class PadButtons
{
    /// <summary>Order used for display and for parsing "View+Menu".</summary>
    public static readonly PadButton[] All = Enum.GetValues<PadButton>().Where(b => b != PadButton.None).ToArray();

    public static IEnumerable<PadButton> Split(PadButton combo) => All.Where(b => (combo & b) != 0);

    public static int Count(PadButton combo) => System.Numerics.BitOperations.PopCount((uint)combo);

    public static string Label(PadButton b) => b switch
    {
        PadButton.DPadUp => "D-pad Up",
        PadButton.DPadDown => "D-pad Down",
        PadButton.DPadLeft => "D-pad Left",
        PadButton.DPadRight => "D-pad Right",
        PadButton.LeftStick => "Left stick click",
        PadButton.RightStick => "Right stick click",
        PadButton.Guide => "Xbox button",
        PadButton.LSUp => "Left stick Up",
        PadButton.LSDown => "Left stick Down",
        PadButton.LSLeft => "Left stick Left",
        PadButton.LSRight => "Left stick Right",
        PadButton.RSUp => "Right stick Up",
        PadButton.RSDown => "Right stick Down",
        PadButton.RSLeft => "Right stick Left",
        PadButton.RSRight => "Right stick Right",
        _ => b.ToString(),
    };

    public static string Format(PadButton combo) => string.Join("+", Split(combo).Select(b => b.ToString()));

    public static string FormatLabel(PadButton combo) => string.Join(" + ", Split(combo).Select(Label));

    public static bool TryParse(string text, out PadButton combo)
    {
        combo = PadButton.None;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<PadButton>(part, ignoreCase: true, out var b) || b == PadButton.None) return false;
            combo |= b;
        }
        return combo != PadButton.None;
    }
}

/// <summary>One controller snapshot: digital buttons plus normalised analog values.
/// Triggers are 0..1, sticks -1..1 with +Y up.</summary>
public readonly record struct PadState(
    bool Connected,
    PadButton Buttons,
    float LeftTrigger = 0,
    float RightTrigger = 0,
    float LX = 0, float LY = 0,
    float RX = 0, float RY = 0)
{
    public static readonly PadState Disconnected = new(false, PadButton.None);

    public bool IsDown(PadButton b) => (Buttons & b) == b;

    /// <summary>Adds the derived buttons: triggers past <paramref name="triggerThreshold"/>,
    /// stick directions past <paramref name="deadzone"/> (dominant axis only, so a
    /// diagonal never moves focus twice).</summary>
    public PadState WithDerived(float deadzone, float triggerThreshold)
    {
        var b = Buttons & (PadButton)0xFFFF;
        if (LeftTrigger >= triggerThreshold) b |= PadButton.LT;
        if (RightTrigger >= triggerThreshold) b |= PadButton.RT;
        b |= StickButtons(LX, LY, deadzone, PadButton.LSUp, PadButton.LSDown, PadButton.LSLeft, PadButton.LSRight);
        b |= StickButtons(RX, RY, deadzone, PadButton.RSUp, PadButton.RSDown, PadButton.RSLeft, PadButton.RSRight);
        return this with { Buttons = b };
    }

    private static PadButton StickButtons(float x, float y, float dz, PadButton up, PadButton down, PadButton left, PadButton right)
    {
        if (Math.Sqrt(x * x + y * y) < dz) return PadButton.None;
        if (Math.Abs(x) > Math.Abs(y)) return x > 0 ? right : left;
        return y > 0 ? up : down;
    }

    /// <summary>Combines several controllers into one, like Cogas: any pad can drive.</summary>
    public static PadState Merge(IEnumerable<PadState> pads)
    {
        var connected = false; var buttons = PadButton.None;
        float lt = 0, rt = 0, lx = 0, ly = 0, rx = 0, ry = 0;
        foreach (var p in pads)
        {
            if (!p.Connected) continue;
            connected = true;
            buttons |= p.Buttons;
            lt = Math.Max(lt, p.LeftTrigger); rt = Math.Max(rt, p.RightTrigger);
            if (Math.Abs(p.LX) > Math.Abs(lx)) lx = p.LX;
            if (Math.Abs(p.LY) > Math.Abs(ly)) ly = p.LY;
            if (Math.Abs(p.RX) > Math.Abs(rx)) rx = p.RX;
            if (Math.Abs(p.RY) > Math.Abs(ry)) ry = p.RY;
        }
        return new PadState(connected, buttons, lt, rt, lx, ly, rx, ry);
    }
}
