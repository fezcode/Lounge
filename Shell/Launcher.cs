using System.Diagnostics;
using System.Runtime.InteropServices;
using Lounge.Core.Launch;
using Lounge.Core.Settings;
using Lounge.Platform;
using Lounge.Screens;

namespace Lounge.Shell;

/// <summary>Starts what a tile points at and hands the controller over: Lounge goes
/// dormant until the app ends or the user holds the return chord.</summary>
public static class Launcher
{
    public static void Launch(Tile tile)
    {
        if (tile.Kind == TileKind.Web && !tile.OpenExternally)
        {
            L.Window.Push(new WebScreen(tile));
            return;
        }
        if (tile.Kind == TileKind.Builtin)
        {
            // Lounge's own games play inside Lounge: nothing to hand over.
            if (tile.Target == "starfall") L.Window.Push(new StarfallScreen());
            else if (tile.IsBrowser) Browser.Choose();
            else L.Window.Toast($"{tile.Name} isn't part of this version of Lounge.", "warning");
            return;
        }
        try
        {
            int? pid = null;
            switch (tile.Kind)
            {
                case TileKind.Web:
                    pid = OpenBrowser(tile.Target);
                    pid = null;   // browsers hand off to a running instance; wait for the return chord
                    break;
                case TileKind.Protocol:
                    Process.Start(new ProcessStartInfo(tile.Target) { UseShellExecute = true });
                    break;
                case TileKind.StoreApp:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{tile.Target}") { UseShellExecute = true });
                    break;
                case TileKind.Cogas:
                    var cogas = Win32.FindCogas(L.Settings.String(K.CogasPath));
                    if (cogas is null)
                    {
                        L.Window.Toast("Cogas isn't installed. Set its location in Settings > Launcher.", "warning");
                        return;
                    }
                    if (Win32.FileVersion(cogas) is { } cv && cv < Win32.CogasBigPictureSince)
                        L.Window.Toast($"Cogas {cv.ToString(3)} has no Big Picture mode. Update it to {Win32.CogasBigPictureSince.ToString(3)} or newer (Airlift can do it).", "warning", 6);
                    pid = Start(cogas, "--big-picture", Path.GetDirectoryName(cogas));
                    break;
                default:
                    if (!File.Exists(tile.Target) && !tile.Target.Contains("://"))
                    {
                        L.Window.Toast($"{tile.Name} can't be found at {tile.Target}.", "warning");
                        return;
                    }
                    pid = Start(tile.Target, tile.Args, string.IsNullOrEmpty(tile.WorkingDir) ? Path.GetDirectoryName(tile.Target) : tile.WorkingDir);
                    break;
            }
            L.Dormant.Launched(tile.Id, pid);
            if (pid is { } p) WatchHandoff(p);
            L.Window.OnAppLaunched(tile);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            L.Window.Toast($"{tile.Name} didn't start: {e.Message}", "warning");
        }
    }

    private static int? Start(string file, string args, string? dir)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
        using var p = Process.Start(psi);
        return p?.Id;
    }

    /// <summary>A launcher stub that exits within seconds (many games do this) is not the
    /// app closing: stop tracking it and wait for the return chord instead.</summary>
    private static void WatchHandoff(int pid)
    {
        var started = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(8))
            {
                await Task.Delay(500);
                if (!Win32.IsProcessAlive(pid))
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (L.Dormant.LaunchedPid == pid) L.Dormant.Untrack(); });
                    return;
                }
            }
        });
    }

    public static int? OpenBrowser(string url)
    {
        var which = L.Settings.String(K.ExternalBrowser);
        var exe = which == "default" ? null : Win32.BrowserPath(which);
        if (exe is null)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return null;
        }
        using var p = Process.Start(new ProcessStartInfo(exe, $"--new-window --start-fullscreen \"{url}\"") { UseShellExecute = true });
        return p?.Id;
    }

    // ---- power

    public static void ShowPowerMenu()
    {
        L.Window.Menu("Power",
        [
            new MenuEntry("Leave Lounge", "close", () =>
            {
                if (L.Settings.Bool(K.ConfirmExit))
                    L.Window.Ask("Leave Lounge?", "Lounge closes until you open it again.", ["Leave", "Stay"], 1, i => { if (i == 0) L.Window.Exit(); }, "close");
                else L.Window.Exit();
            }),
            new MenuEntry("Sleep", "clock", () => Confirm("Put the PC to sleep?", () => SetSuspendState(false, false, false))),
            new MenuEntry("Restart", "refresh", () => Confirm("Restart the PC?", () => Shutdown("/r"))),
            new MenuEntry("Shut down", "power", () => Confirm("Shut down the PC?", () => Shutdown("/s")), Danger: true),
        ]);
    }

    private static void Confirm(string question, Action yes) =>
        L.Window.Ask(question, "Anything playing will stop.", ["Yes", "Cancel"], 1, i => { if (i == 0) yes(); }, "power");

    private static void Shutdown(string flag) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", $"{flag} /t 0") { UseShellExecute = false, CreateNoWindow = true });

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
