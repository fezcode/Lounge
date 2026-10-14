using System.Runtime.InteropServices;

namespace Lounge.Platform;

/// <summary>Controller-as-mouse: moves the real pointer and sends real clicks and keys
/// with SendInput, so it works in web pages and in apps that ignore controllers.</summary>
public static class Mouse
{
    public static (int X, int Y) Position
    {
        get { GetCursorPos(out var p); return (p.X, p.Y); }
    }

    public static void MoveBy(double dx, double dy)
    {
        GetCursorPos(out var p);
        SetCursorPos(p.X + (int)Math.Round(dx), p.Y + (int)Math.Round(dy));
    }

    public static void MoveTo(int x, int y) => SetCursorPos(x, y);

    public static void Click(bool right = false)
    {
        var down = right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        var up = right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
        Send([MouseInput(down, 0), MouseInput(up, 0)]);
    }

    public static void Scroll(int delta, bool horizontal = false) =>
        Send([MouseInput(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, delta)]);

    /// <summary>Types text into whatever has keyboard focus (Unicode, so any language works).</summary>
    public static void Type(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text)
        {
            inputs.Add(KeyInput(0, ch, KEYEVENTF_UNICODE));
            inputs.Add(KeyInput(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        Send(inputs.ToArray());
    }

    public static void Key(ushort vk)
    {
        Send([KeyInput(vk, 0, 0), KeyInput(vk, 0, KEYEVENTF_KEYUP)]);
    }

    public static void KeyCombo(params ushort[] vks)
    {
        var inputs = vks.Select(v => KeyInput(v, 0, 0)).Concat(vks.Reverse().Select(v => KeyInput(v, 0, KEYEVENTF_KEYUP)));
        Send(inputs.ToArray());
    }

    public const ushort VK_BACK = 0x08, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_MENU = 0x12, VK_LEFT = 0x25, VK_F11 = 0x7A, VK_BROWSER_BACK = 0xA6;

    private static void Send(INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    private static INPUT MouseInput(uint flags, int data) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } },
    };

    private static INPUT KeyInput(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x02, MOUSEEVENTF_LEFTUP = 0x04, MOUSEEVENTF_RIGHTDOWN = 0x08, MOUSEEVENTF_RIGHTUP = 0x10,
        MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint KEYEVENTF_KEYUP = 0x02, KEYEVENTF_UNICODE = 0x04;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
}
