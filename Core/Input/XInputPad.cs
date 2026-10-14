using System.Runtime.InteropServices;

namespace Lounge.Core.Input;

/// <summary>Reads Xbox controllers through XInput and merges every connected pad, so
/// whichever one is in hand works (after Cogas's reader). The Xbox button comes from the
/// undocumented XInputGetStateEx (ordinal 100), which every xinput1_4 exports; it is
/// only reported when <see cref="ReadGuide"/> is on, because Game Bar owns it by default.
/// Empty slots are probed once a second: asking XInput about them is slow.</summary>
public sealed class XInputPad
{
    private const int Slots = 4;
    private const long ReprobeMs = 1000;

    private readonly bool[] _connected = new bool[Slots];
    private long _lastProbe = long.MinValue;
    private bool _unavailable;
    private bool _exUnavailable;

    public bool ReadGuide { get; set; }

    public int ConnectedCount => _connected.Count(c => c);

    /// <summary>The merged state of every connected pad at <paramref name="nowMs"/>.</summary>
    public PadState Read(long nowMs)
    {
        if (_unavailable) return PadState.Disconnected;
        var probe = nowMs - _lastProbe >= ReprobeMs;
        if (probe) _lastProbe = nowMs;
        var states = new List<PadState>(Slots);
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
                _connected[i] = rc == 0;
                if (rc != 0) continue;
                var g = s.Gamepad;
                var buttons = (PadButton)g.wButtons;
                if (!ReadGuide) buttons &= ~PadButton.Guide;
                states.Add(new PadState(true, buttons,
                    g.bLeftTrigger / 255f, g.bRightTrigger / 255f,
                    Norm(g.sThumbLX), Norm(g.sThumbLY), Norm(g.sThumbRX), Norm(g.sThumbRY)));
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            return PadState.Disconnected;
        }
        return states.Count == 0 ? PadState.Disconnected : PadState.Merge(states);
    }

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

    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int userIndex, out XInputState state);

    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern int XInputGetStateEx(int userIndex, out XInputState state);

    [DllImport("xinput1_4.dll")]
    private static extern int XInputSetState(int userIndex, ref XInputVibration vibration);
}
