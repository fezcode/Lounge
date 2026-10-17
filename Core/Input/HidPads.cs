using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lounge.Core.Input;

/// <summary>Gamepads that aren't XInput (8BitDo in D-input or Switch mode, Switch Pro,
/// PlayStation pads, generic USB pads), read through Raw Input on a thread of their own.
/// Input arrives while other apps are in front too (RIDEV_INPUTSINK), so the return
/// chord works from any controller. Each report is decoded with the HID parser
/// (HidP_*): buttons from the Button page, sticks, triggers and the hat switch from the
/// Generic Desktop page. XInput pads (device path with "IG_") are left to XInputPad.</summary>
public sealed class HidPads : IDisposable
{
    private sealed class Device
    {
        public required ControllerInfo Info;
        public required byte[] Preparsed;
        public required (ushort Page, ushort Usage, ushort Link, int Min, int Max, int Bits)[] Values;
        public required int MaxButtons;
        public RawPad Raw = new();
    }

    private readonly Dictionary<IntPtr, Device?> _devices = new();
    private readonly object _gate = new();
    private Thread? _thread;
    private IntPtr _hwnd;
    private WndProc? _proc;   // kept alive: the window calls it
    private volatile bool _stop;

    public bool Available { get; private set; }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "hid-pads" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Connected HID pads and a copy of their inputs now.</summary>
    public List<(ControllerInfo Info, RawPad Raw)> Snapshot()
    {
        var list = new List<(ControllerInfo, RawPad)>();
        lock (_gate)
            foreach (var d in _devices.Values)
                if (d is not null)
                {
                    var copy = new RawPad();
                    foreach (var (k, v) in d.Raw.Inputs) copy.Inputs[k] = v;
                    list.Add((d.Info, copy));
                }
        return list;
    }

    // ------------------------------------------------------------------ thread

    private void Run()
    {
        try
        {
            _proc = Proc;
            var cls = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), lpszClassName = "LoungeHidPads", hInstance = GetModuleHandle(null) };
            if (RegisterClassEx(ref cls) == 0) return;
            _hwnd = CreateWindowEx(0, "LoungeHidPads", "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return;
            var devices = new[]
            {
                new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 4, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = _hwnd },   // joystick
                new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 5, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = _hwnd },   // gamepad
                new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 8, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = _hwnd },   // multi-axis
            };
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) return;
            Available = true;
            EnumerateExisting();
            while (!_stop && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or Win32Exception) { Available = false; }
    }

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_INPUT) OnInput(lParam);
            else if (msg == WM_INPUT_DEVICE_CHANGE)
            {
                if ((long)wParam == GIDC_ARRIVAL) Describe(lParam);
                else if ((long)wParam == GIDC_REMOVAL) lock (_gate) _devices.Remove(lParam);
            }
        }
        catch (Exception) { }   // one bad report or device never stops the reader
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void EnumerateExisting()
    {
        uint count = 0;
        var size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0) return;
        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, size) == unchecked((uint)-1)) return;
        foreach (var d in list.Take((int)count))
            if (d.dwType == RIM_TYPEHID) Describe(d.hDevice);
    }

    /// <summary>Learns a device once: whether it is a gamepad we read, its name, ID and report layout.</summary>
    private Device? Describe(IntPtr handle)
    {
        lock (_gate) if (_devices.TryGetValue(handle, out var known)) return known;
        var device = Inspect(handle);
        lock (_gate) _devices[handle] = device;
        return device;
    }

    private static Device? Inspect(IntPtr handle)
    {
        var info = new byte[32];
        BitConverter.GetBytes(32).CopyTo(info, 0);
        uint size = 32;
        if (GetRawInputDeviceInfo(handle, RIDI_DEVICEINFO, info, ref size) == unchecked((uint)-1)) return null;
        if (BitConverter.ToInt32(info, 4) != RIM_TYPEHID) return null;
        var vid = BitConverter.ToInt32(info, 8);
        var pid = BitConverter.ToInt32(info, 12);
        var page = BitConverter.ToUInt16(info, 20);
        var usage = BitConverter.ToUInt16(info, 22);
        if (page != 1 || usage is not (4 or 5 or 8)) return null;

        var path = DeviceName(handle);
        if (path is null || path.Contains("IG_", StringComparison.OrdinalIgnoreCase)) return null;   // XInput reads it

        size = 0;
        GetRawInputDeviceInfo(handle, RIDI_PREPARSEDDATA, null, ref size);
        if (size == 0) return null;
        var preparsed = new byte[size];
        if (GetRawInputDeviceInfo(handle, RIDI_PREPARSEDDATA, preparsed, ref size) == unchecked((uint)-1)) return null;

        var caps = new byte[64];
        if (HidP_GetCaps(preparsed, caps) != HIDP_STATUS_SUCCESS) return null;
        var valueCount = BitConverter.ToUInt16(caps, 48);
        var values = new List<(ushort, ushort, ushort, int, int, int)>();
        if (valueCount > 0)
        {
            var buf = new byte[72 * valueCount];
            var len = valueCount;
            if (HidP_GetValueCaps(0, buf, ref len, preparsed) == HIDP_STATUS_SUCCESS)
                for (var i = 0; i < len; i++)
                {
                    var o = i * 72;
                    var vPage = BitConverter.ToUInt16(buf, o);
                    var link = BitConverter.ToUInt16(buf, o + 6);
                    var isRange = buf[o + 12] != 0;
                    var bits = BitConverter.ToUInt16(buf, o + 18);
                    var min = BitConverter.ToInt32(buf, o + 40);
                    var max = BitConverter.ToInt32(buf, o + 44);
                    var u0 = BitConverter.ToUInt16(buf, o + 56);
                    var u1 = isRange ? BitConverter.ToUInt16(buf, o + 58) : u0;
                    for (var u = u0; u <= u1 && u - u0 < 16; u++) values.Add((vPage, u, link, min, max, bits));
                }
        }
        var maxButtons = (int)HidP_MaxUsageListLength(0, 9, preparsed);

        var (name, serial) = Strings(path);
        name = string.IsNullOrWhiteSpace(name) ? $"Gamepad {vid:X4}:{pid:X4}" : name.Trim();
        var id = !string.IsNullOrWhiteSpace(serial) && serial.Trim().Length >= 4
            ? $"hid-{vid:x4}-{pid:x4}-{Clean(serial)}"
            : $"hid-{vid:x4}-{pid:x4}-{Hash(path)}";
        return new Device
        {
            Info = new ControllerInfo(id, name, ControllerKind.Hid, vid, pid),
            Preparsed = preparsed,
            Values = values.ToArray(),
            MaxButtons = Math.Max(maxButtons, 1),
        };
    }

    private void OnInput(IntPtr hRawInput)
    {
        uint size = 0;
        var header = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(hRawInput, RID_INPUT, null, ref size, header);
        if (size == 0) return;
        var data = new byte[size];
        if (GetRawInputData(hRawInput, RID_INPUT, data, ref size, header) == unchecked((uint)-1)) return;
        if (BitConverter.ToInt32(data, 0) != RIM_TYPEHID) return;
        var handle = IntPtr.Size == 8 ? (IntPtr)BitConverter.ToInt64(data, 8) : (IntPtr)BitConverter.ToInt32(data, 8);
        var device = Describe(handle);
        if (device is null) return;
        var off = (int)header;
        var reportSize = BitConverter.ToInt32(data, off);
        var count = BitConverter.ToInt32(data, off + 4);
        if (reportSize <= 0 || count <= 0) return;
        var report = new byte[reportSize];
        // The newest report is the state now.
        Buffer.BlockCopy(data, off + 8 + reportSize * (count - 1), report, 0, reportSize);
        var raw = Decode(device, report);
        lock (_gate) device.Raw = raw;
    }

    private static RawPad Decode(Device d, byte[] report)
    {
        var raw = new RawPad();
        for (var b = 1; b <= Math.Min(d.MaxButtons, 32); b++) raw.Inputs["b" + b] = 0;
        var usages = new ushort[d.MaxButtons];
        var n = (uint)usages.Length;
        if (HidP_GetUsages(0, 9, 0, usages, ref n, d.Preparsed, report, (uint)report.Length) == HIDP_STATUS_SUCCESS)
            for (var i = 0; i < n; i++)
                if (usages[i] is >= 1 and <= 32) raw.Inputs["b" + usages[i]] = 1;

        foreach (var hat in new[] { "hat:up", "hat:down", "hat:left", "hat:right" }) raw.Inputs[hat] = 0;
        foreach (var (page, usage, link, min, max, bits) in d.Values)
        {
            if (HidP_GetUsageValue(0, page, link, usage, out var value, d.Preparsed, report, (uint)report.Length) != HIDP_STATUS_SUCCESS) continue;
            long v = value, lo = min, hi = max;
            if (hi < lo || (lo == 0 && hi == -1)) { lo = 0; hi = bits is > 0 and < 32 ? (1L << bits) - 1 : uint.MaxValue; }
            else if (lo < 0 && bits is > 0 and < 32 && (v & (1L << (bits - 1))) != 0) v -= 1L << bits;   // sign-extend
            if (page == 1 && usage == 0x39)
            {
                if (v < lo || v > hi) continue;   // the hat's "nothing pressed"
                var steps = hi - lo + 1;
                var dir = (int)(steps == 4 ? (v - lo) * 2 : v - lo);   // 0 = up, clockwise in eighths
                raw.Inputs["hat:up"] = dir is 7 or 0 or 1 ? 1 : 0;
                raw.Inputs["hat:right"] = dir is 1 or 2 or 3 ? 1 : 0;
                raw.Inputs["hat:down"] = dir is 3 or 4 or 5 ? 1 : 0;
                raw.Inputs["hat:left"] = dir is 5 or 6 or 7 ? 1 : 0;
                continue;
            }
            var name = (page, usage) switch
            {
                (1, 0x30) => "x", (1, 0x31) => "y", (1, 0x32) => "z", (1, 0x33) => "rx", (1, 0x34) => "ry", (1, 0x35) => "rz",
                (1, 0x36) => "slider", (1, 0x37) => "dial", (2, 0xC4) => "accel", (2, 0xC5) => "brake",
                _ => null,
            };
            if (name is null || hi <= lo) continue;
            raw.Inputs[name] = (float)Math.Clamp((v - lo) / (double)(hi - lo) * 2 - 1, -1, 1);
        }
        return raw;
    }

    // ------------------------------------------------------------------ names and IDs

    private static string? DeviceName(IntPtr handle)
    {
        uint chars = 0;
        GetRawInputDeviceInfoW(handle, RIDI_DEVICENAME, null, ref chars);
        if (chars == 0) return null;
        var buf = new char[chars];
        return GetRawInputDeviceInfoW(handle, RIDI_DEVICENAME, buf, ref chars) == unchecked((uint)-1) ? null : new string(buf).TrimEnd('\0');
    }

    private static (string? Name, string? Serial) Strings(string path)
    {
        using var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return (null, null);
        string? Read(Func<SafeFileHandle, byte[], int, bool> f)
        {
            var buf = new byte[512];
            return f(h, buf, buf.Length) ? System.Text.Encoding.Unicode.GetString(buf).Split('\0')[0] : null;
        }
        return (Read(HidD_GetProductString), Read(HidD_GetSerialNumberString));
    }

    private static string Clean(string s) => new(s.Where(char.IsLetterOrDigit).Take(40).ToArray());

    /// <summary>A short stable hash of the device path (it carries the USB port or Bluetooth pairing).</summary>
    public static string Hash(string path)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }

    public void Dispose()
    {
        _stop = true;
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    // ------------------------------------------------------------------ interop

    private const uint WM_INPUT = 0x00FF, WM_INPUT_DEVICE_CHANGE = 0x00FE, WM_QUIT = 0x0012;   // ends GetMessage
    private const long GIDC_ARRIVAL = 1, GIDC_REMOVAL = 2;
    private const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_DEVNOTIFY = 0x00002000;
    private const uint RID_INPUT = 0x10000003, RIDI_PREPARSEDDATA = 0x20000005, RIDI_DEVICENAME = 0x20000007, RIDI_DEVICEINFO = 0x2000000b;
    private const int RIM_TYPEHID = 2;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public int dwType; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTHEADER { public int dwType, dwSize; public IntPtr hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")] private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, byte[]? data, ref uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, char[]? data, ref uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr raw, uint command, byte[]? data, ref uint size, uint headerSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll")] private static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] private static extern bool HidD_GetSerialNumberString(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(byte[] preparsed, byte[] caps);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int reportType, byte[] caps, ref ushort length, byte[] preparsed);
    [DllImport("hid.dll")] private static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, byte[] preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort link, [In, Out] ushort[] usages, ref uint length, byte[] preparsed, byte[] report, uint reportLength);
    [DllImport("hid.dll")] private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort link, ushort usage, out uint value, byte[] preparsed, byte[] report, uint reportLength);
}
