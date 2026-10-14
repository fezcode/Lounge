using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Lounge.Platform;

/// <summary>Window and process helpers: who is in front, bringing Lounge back, and the
/// registry bits (startup, Cogas location).</summary>
public static class Win32
{
    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    public static int ForegroundProcessId()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(h, out var pid);
        return (int)pid;
    }

    public static bool IsProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Brings <paramref name="hwnd"/> to the front even when another process
    /// owns the foreground. Windows only lets the foreground process hand focus over;
    /// attaching to its input queue for the call is the documented way around that.</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var ours = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != ours && AttachThreadInput(ours, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            ShowWindow(hwnd, SW_SHOW);
        }
        finally
        {
            if (attached) AttachThreadInput(ours, fgThread, false);
        }
    }

    public static void Minimize(IntPtr hwnd) => ShowWindow(hwnd, SW_MINIMIZE);

    /// <summary>Asks a process's windows to close (WM_CLOSE), then gives up quietly.</summary>
    public static void CloseProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.CloseMainWindow();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
    }

    /// <summary>True while a full-screen exclusive app, a game or a presentation owns the
    /// screen (SHQueryUserNotificationState), so the agent stays quiet over it.</summary>
    public static bool FullscreenAppActive()
    {
        if (SHQueryUserNotificationState(out var state) != 0) return false;
        return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
    }

    // ---- registry

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetRunAtLogin(string name, string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (command is null) key.DeleteValue(name, false);
        else key.SetValue(name, command);
    }

    public static bool RunsAtLogin(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(name) is string;
    }

    /// <summary>Cogas records its install folder in HKCU\Software\Fezcode\Cogas.</summary>
    public static string? FindCogas(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var p = Directory.Exists(overridePath) ? Path.Combine(overridePath, "Cogas.exe") : overridePath;
            if (File.Exists(p)) return p;
        }
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Fezcode\Cogas");
        if (key?.GetValue("InstallDir") is string dir && File.Exists(Path.Combine(dir, "Cogas.exe")))
            return Path.Combine(dir, "Cogas.exe");
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Cogas", "Cogas.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>Cogas learned --big-picture in 0.20.0; older builds open their normal window.</summary>
    public static readonly Version CogasBigPictureSince = new(0, 20, 0);

    public static Version? FileVersion(string exe)
    {
        try
        {
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
        }
        catch { return null; }
    }

    public static bool SteamInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamExe") is string exe && File.Exists(exe.Replace('/', '\\'));
    }

    public static string? BrowserPath(string which)
    {
        string[] candidates = which switch
        {
            "chrome" =>
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
            ],
            "edge" =>
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
            ],
            _ => [],
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    // ---- interop

    private const int SW_SHOW = 5, SW_MINIMIZE = 6, SW_RESTORE = 9;
    private const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}
