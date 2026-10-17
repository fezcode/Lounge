using System.Runtime.InteropServices;

namespace Lounge.Core.Input;

/// <summary>Reads Xbox controllers through XInput, each slot on its own with its model
/// (ControllerHub applies each pad's layout and merges them). The Xbox button comes from the
/// undocumented XInputGetStateEx (ordinal 100), which every xinput1_4 exports; it is
/// only reported when <see cref="ReadGuide"/> is on, because Game Bar owns it by default.
/// Empty slots are probed once a second: asking XInput about them is slow.</summary>
public sealed class XInputPad
{
    private const int Slots = 4;
    private const long ReprobeMs = 1000;

    private readonly bool[] _connected = new bool[Slots];
    // Not long.MinValue: now - MinValue overflows negative and empty slots would never be probed.
    private long? _lastProbe;
    private bool _unavailable;
    private bool _exUnavailable;

    public bool ReadGuide { get; set; }

    public int ConnectedCount => _connected.Count(c => c);

    private readonly (int Vid, int Pid)[] _model = new (int, int)[Slots];

    /// <summary>One connected pad: its slot, model (vendor and product ID) and raw inputs.</summary>
    public readonly record struct Slot(int Index, int VendorId, int ProductId, RawPad Raw);

    private static readonly PadButton[] Buttons =
    [
        PadButton.DPadUp, PadButton.DPadDown, PadButton.DPadLeft, PadButton.DPadRight, PadButton.Menu, PadButton.View,
        PadButton.LeftStick, PadButton.RightStick, PadButton.LB, PadButton.RB, PadButton.Guide,
        PadButton.A, PadButton.B, PadButton.X, PadButton.Y,
    ];

    /// <summary>Every connected pad at <paramref name="nowMs"/>, each on its own.</summary>
    public List<Slot> ReadSlots(long nowMs)
    {
        var slots = new List<Slot>(Slots);
        if (_unavailable) return slots;
        var probe = ShouldProbe(_lastProbe, nowMs);
        if (probe) _lastProbe = nowMs;
        try
        {
            for (int i = 0; i < Slots; i++)
            {
                if (!_connected[i] && !probe) continue;
                XInputState s;
                int rc;
                if (ReadGuide && !_exUnavailable)
                {
                    try { rc = XInputGetStateEx(i, out s); }
                    catch (EntryPointNotFoundException) { _exUnavailable = true; rc = XInputGetState(i, out s); }
                }
                else rc = XInputGetState(i, out s);
                var was = _connected[i];
                _connected[i] = rc == 0;
                if (rc != 0) continue;
                if (!was) _model[i] = Model(i);
                var g = s.Gamepad;
                var raw = new RawPad();
                foreach (var b in Buttons)
                    raw.Inputs[b.ToString()] = (g.wButtons & (ushort)b) != 0 && (b != PadButton.Guide || ReadGuide) ? 1 : 0;
                raw.Inputs["lx"] = Norm(g.sThumbLX); raw.Inputs["ly"] = Norm(g.sThumbLY);
                raw.Inputs["rx"] = Norm(g.sThumbRX); raw.Inputs["ry"] = Norm(g.sThumbRY);
                raw.Inputs["lt"] = g.bLeftTrigger / 255f; raw.Inputs["rt"] = g.bRightTrigger / 255f;
                slots.Add(new Slot(i, _model[i].Vid, _model[i].Pid, raw));
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            slots.Clear();
        }
        return slots;
    }

    private static readonly Dictionary<string, SourceBinding> DefaultLayout = ControllerLayout.XInputDefault();

    /// <summary>The merged state of every connected pad at <paramref name="nowMs"/>.</summary>
    public PadState Read(long nowMs)
    {
        var slots = ReadSlots(nowMs);
        return slots.Count == 0 ? PadState.Disconnected : PadState.Merge(slots.Select(s => ControllerLayout.Apply(DefaultLayout, s.Raw)));
    }

    private bool _capsUnavailable;

    /// <summary>The pad's vendor and product ID from the undocumented XInputGetCapabilitiesEx
    /// (ordinal 108); a plain Microsoft pad when it isn't there.</summary>
    private (int, int) Model(int slot)
    {
        if (_capsUnavailable) return (0x045E, 0);
        try
        {
            if (XInputGetCapabilitiesEx(1, slot, 0, out var caps) == 0) return (caps.VendorId, caps.ProductId);
        }
        catch (EntryPointNotFoundException) { _capsUnavailable = true; }
        return (0x045E, 0);
    }

    /// <summary>A readable name for an XInput pad's model.</summary>
    public static string ModelName(int vendorId, int productId) => (vendorId, productId) switch
    {
        (0x045E, 0x028E) => "Xbox 360 Controller",
        (0x045E, 0x02D1 or 0x02DD or 0x02EA or 0x02E0 or 0x02FD or 0x0B20) => "Xbox One Controller",
        (0x045E, 0x0B00 or 0x0B05 or 0x0B22) => "Xbox Elite Controller Series 2",
        (0x045E, 0x0B12 or 0x0B13) => "Xbox Wireless Controller",
        (0x045E, _) => "Xbox Controller",
        (0x2DC8, _) => "8BitDo Controller (XInput)",
        (0x054C, _) => "PlayStation Controller (XInput)",
        (0x046D, _) => "Logitech Gamepad (XInput)",
        (0x0E6F or 0x24C6 or 0x0F0D or 0x1532 or 0x20D6, _) => "Xbox-compatible Controller",
        _ => "XInput Controller",
    };

    /// <summary>Turns a feedback request into a pulse a hand actually notices. Motors need
    /// about 150 ms and a good share of power to be felt (tried on an Xbox Wireless
    /// Controller: half power for 300 ms went unnoticed). The setting (0-100, 50 =
    /// normal) scales it; 0 is off.</summary>
    public static (float Power, int Ms) Feel(float strength, int ms, int setting)
    {
        if (setting <= 0 || strength <= 0) return (0, 0);
        var scale = setting / 50f;
        var power = Math.Clamp((0.45f + strength) * scale, 0.3f, 1f);
        var duration = Math.Clamp((int)(Math.Max(ms * 2.5, 150) * Math.Max(scale, 0.75f)), 120, 600);
        return (power, duration);
    }

    /// <summary>Empty slots are asked about on the first read and then once a second.</summary>
    public static bool ShouldProbe(long? lastProbe, long nowMs) => lastProbe is not { } last || nowMs - last >= ReprobeMs;

    private static float Norm(short v) => Math.Clamp(v / 32767f, -1f, 1f);

    /// <summary>Rumbles every connected pad. Strength 0..1 for each motor.</summary>
    public void Vibrate(float low, float high)
    {
        if (_unavailable) return;
        var v = new XInputVibration
        {
            wLeftMotorSpeed = (ushort)(Math.Clamp(low, 0, 1) * 65535),
            wRightMotorSpeed = (ushort)(Math.Clamp(high, 0, 1) * 65535),
        };
        try
        {
            for (int i = 0; i < Slots; i++)
                if (_connected[i]) XInputSetState(i, ref v);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { _unavailable = true; }
    }

    /// <summary>A short buzz: rumble for <paramref name="ms"/> then stop.</summary>
    public async Task Pulse(float strength, int ms)
    {
        if (strength <= 0) return;
        Vibrate(strength, strength * 0.6f);
        await Task.Delay(ms);
        Vibrate(0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort wLeftMotorSpeed, wRightMotorSpeed;
    }

    /// <summary>XINPUT_CAPABILITIES followed by the Ex fields.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputCapabilitiesEx
    {
        public byte Type, SubType;
        public ushort Flags;
        public XInputGamepad Gamepad;
        public XInputVibration Vibration;
        public ushort VendorId, ProductId, ProductVersion, Unknown1;
        public uint Unknown2;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "#108")]
    private static extern int XInputGetCapabilitiesEx(int a1, int userIndex, int flags, out XInputCapabilitiesEx caps);

    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int userIndex, out XInputState state);

    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern int XInputGetStateEx(int userIndex, out XInputState state);

    [DllImport("xinput1_4.dll")]
    private static extern int XInputSetState(int userIndex, ref XInputVibration vibration);
}
