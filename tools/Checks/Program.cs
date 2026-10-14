using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lounge.Controls;
using Lounge.Core;
using Lounge.Core.Input;
using Lounge.Core.Launch;
using Lounge.Core.Library;
using Lounge.Core.Media;
using Lounge.Core.Settings;
using Lounge.Media;
using Lounge.Platform;
using Lounge.Screens;
using Lounge.Shell;

namespace Lounge.Checks;

/// <summary>Controller-only flows through the real screens, headlessly. A virtual pad
/// feeds button presses into the real router, exactly as the XInput pump would, and
/// each check asserts what the user would see happen. Nothing touches the desktop.</summary>
internal static class Program
{
    private static MainWindow _w = null!;
    private static long _now = 1_000;
    private static string _data = "";
    private static int _failed;

    [STAThread]
    public static int Main()
    {
        _data = Path.Combine(Path.GetTempPath(), "lounge-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_data);
        Environment.SetEnvironmentVariable("LOUNGE_DATA", _data);
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();
        Motion.Enabled = false;
        L.Preview = true;

        try
        {
            L.Settings = SettingsStore.OpenDefault();
            foreach (var (k, v) in new (string, object)[] { (K.PlayerVolume, 0.0), (K.Ambient, false), (K.Sounds, false), (K.ResumePrompt, false), (K.ConfirmDelete, false), (K.GpuConvert, false) })
                L.Settings.Set(k, v);
            L.Keymap = Keymap.Defaults();
            L.Pad = new XInputPad();
            L.Router = new ActionRouter(L.Keymap);
            L.Tiles = new TileStore(AppPaths.Tiles);
            L.History = new WatchHistory(AppPaths.History);
            L.Library = new LibraryDb(AppPaths.LibraryDb);
            L.Player = new MediaPlayer();
            L.Volume = new SystemVolume();
            _w = new MainWindow { Width = 1920, Height = 1080 };
            L.Window = _w;
            _w.ApplyTheme(rebuild: false);
            _w.Push(new HomeScreen());
            _w.Show();
            _w.Wire();
            Pad(PadButton.None);
            if (Environment.GetEnvironmentVariable("LOUNGE_CHECKS_TRACE") is not null) L.Router.Fired += e => Console.WriteLine($"    action {e.Action} {e.Phase} ctx={L.Router.Context} top={_w.Top?.GetType().Name}");

            Check("Home takes focus and the D-pad moves it", HomeNavigation);
            Check("Menu opens settings; the theme changes from the controller", ChangeTheme);
            Check("A library folder is added from Files", AddLibraryFolder);
            Check("A file is renamed with the controller keyboard", RenameFile);
            Check("New folder, copy and paste from the options menu", NewFolderCopyPaste);
            Check("A button is remapped on the Remote screen", RemapButton);
            Check("Every theme renders its own home", ThemesDiffer);
            Check("The keyboard fits on screen in every layer", KeyboardFits);
            Check("While an app runs, only the return chord is heard", DormantChord);
            Check("A video plays, the D-pad skips and the trigger scrubs", PlayAndSeek);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Checks crashed: {e}");
            _failed++;
        }
        finally
        {
            L.Player?.Close();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_data, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine(_failed == 0 ? "All checks passed." : $"{_failed} check(s) FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ harness

    private static void Check(string name, Action body)
    {
        try
        {
            body();
            Console.WriteLine($"  ok    {name}");
        }
        catch (Exception e)
        {
            _failed++;
            Console.WriteLine($"  FAIL  {name}: {e.Message}");
        }
        finally
        {
            _w.CloseOverlays();
            Pump();
            _w.GoHome();
            Pump();
        }
    }

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private static void Pump(int rounds = 8)
    {
        for (int i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        }
    }

    /// <summary>The virtual pad: one snapshot into the real router.</summary>
    private static void Pad(PadButton buttons, float rt = 0, float lt = 0)
    {
        _now += 16;
        L.Router.Update(new PadState(true, buttons, lt, rt), _now);
    }

    private static void Press(PadButton b, int holdMs = 48)
    {
        Pad(b);
        for (int t = 16; t < holdMs; t += 16) Pad(b);
        Pad(PadButton.None);
        Pump(3);
    }

    private static void Hold(PadButton b, int ms)
    {
        for (int t = 0; t < ms; t += 16) Pad(b);
        Pad(PadButton.None);
        Pump(3);
    }

    private static string? FocusedTag => _w.Top?.Focused?.Tag switch
    {
        HomeEntry e => e.Title,
        string s => s,
        { } o => o.ToString(),
        null => null,
    };

    // ------------------------------------------------------------------ checks

    private static void HomeNavigation()
    {
        Pump();
        var start = _w.Top!.Focused;
        Assert(start is not null, "nothing is focused on Home");
        Press(PadButton.DPadDown);
        Assert(_w.Top.Focused != start, "D-pad down did not move focus");
        Press(PadButton.DPadUp);
        Assert(_w.Top.Focused == start, "D-pad up did not come back");
    }

    private static void ChangeTheme()
    {
        L.Settings.Set(K.Theme, "aurora");
        Press(PadButton.Menu);
        Pump();
        Assert(_w.Overlays.OfType<SettingsPanel>().Any(), "Menu did not open settings");
        Press(PadButton.DPadDown);                 // General -> Appearance
        Pump(12);                                  // the section fills after a short debounce
        Press(PadButton.DPadRight);                // into the rows: Theme
        Press(PadButton.DPadRight);                // step the value
        Pump();
        Assert(L.Settings.String(K.Theme) == "livingroom", $"theme is {L.Settings.String(K.Theme)}, expected livingroom");
        Assert(T.Def.Id == "livingroom", "the new theme was not applied");
        Assert(_w.Overlays.OfType<SettingsPanel>().Any(), "settings closed after the theme change");
        Press(PadButton.B);
        Press(PadButton.B);
        Pump(30);
        Assert(!_w.Overlays.OfType<SettingsPanel>().Any(), "B did not close settings");
        L.Settings.Set(K.Theme, "aurora");
        _w.ApplyTheme(rebuild: true);
    }

    private static string Folder(string name)
    {
        var dir = Path.Combine(_data, "Media", name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void AddLibraryFolder()
    {
        var shows = Folder("Shows");
        _w.Push(new FilesScreen(shows, pickFor: K.VideoRoots));
        Pump();
        Press(PadButton.DPadRight);                // places -> list: first item is "Choose this folder"
        Press(PadButton.A);
        Pump(10);
        Assert(L.Settings.List(K.VideoRoots).Contains(shows, StringComparer.OrdinalIgnoreCase), "the folder was not added to Videos");
        Assert(_w.Top is HomeScreen, "Files did not close after choosing");
    }

    private static void RenameFile()
    {
        var dir = Folder("Rename");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "hello");
        var files = new FilesScreen(dir);
        _w.Push(files);
        Pump();
        Press(PadButton.DPadRight);                // into the list: notes.txt
        Press(PadButton.Y);                        // rename
        Pump(6);
        Assert(_w.Overlays.OfType<OnScreenKeyboard>().Any(), "Y did not open the keyboard");
        // Keyboard starts on "1"; the name is selected, so the first key replaces it.
        Press(PadButton.DPadDown);                 // q row
        Press(PadButton.DPadDown);                 // a row
        Press(PadButton.A);                        // "a"
        Press(PadButton.DPadRight);                // s
        Press(PadButton.A);                        // "s"
        Press(PadButton.Menu);                     // done
        Pump(10);
        Assert(File.Exists(Path.Combine(dir, "as.txt")), "notes.txt was not renamed to as.txt (extension locked)");
        Assert(!File.Exists(Path.Combine(dir, "notes.txt")), "the old name still exists");
    }

    private static void NewFolderCopyPaste()
    {
        var dir = Folder("Copy");
        File.WriteAllText(Path.Combine(dir, "song.mp3"), "x");
        var files = new FilesScreen(dir);
        _w.Push(files);
        Pump();
        Press(PadButton.DPadRight);                // song.mp3
        Press(PadButton.View);                     // options
        Pump(6);
        var menu = _w.Overlays.OfType<ModalLayer>().LastOrDefault();
        Assert(menu is not null, "View did not open options");
        // Options: Open, Rename, Copy, Cut, Delete, New folder...
        Press(PadButton.DPadDown); Press(PadButton.DPadDown);
        Press(PadButton.A);                        // Copy
        Pump(20);
        Press(PadButton.View);
        Pump(6);
        for (int i = 0; i < 6; i++) Press(PadButton.DPadDown);   // ... New folder
        Press(PadButton.A);
        Pump(10);
        Assert(_w.Overlays.OfType<OnScreenKeyboard>().Any(), "New folder did not ask for a name");
        Press(PadButton.Menu);                     // accept "New folder"
        Pump(20);
        var made = Path.Combine(dir, "New folder");
        Assert(Directory.Exists(made), "the new folder was not created");
        // Open it and paste: the first list entry is "Paste 1 item here".
        Press(PadButton.A);
        Pump(6);
        Press(PadButton.A);
        Pump(40);
        Assert(File.Exists(Path.Combine(made, "song.mp3")), "paste did not copy the file");
        Assert(File.Exists(Path.Combine(dir, "song.mp3")), "copy removed the original");
    }

    private static void RemapButton()
    {
        _w.Push(new RemoteScreen());
        Pump();
        // Focus starts on the drawing's A; A jumps to its bindings, A opens one, A picks "Change the button".
        Press(PadButton.A);
        Pump();
        Press(PadButton.A);
        Pump(6);
        Press(PadButton.A);                        // "Change the button"
        Pump(6);
        Assert(L.Router.Capturing, "capture did not start");
        Press(PadButton.X);                        // the new button
        Pump(10);
        // X already does "Secondary action": swap.
        Press(PadButton.A);
        Pump(10);
        var accept = L.Keymap.ActionFor("Global", new InputBinding(PadButton.X, Gesture.Press));
        var secondary = L.Keymap.ActionFor("Global", new InputBinding(PadButton.A, Gesture.Press));
        Assert(accept == Act.Accept, $"X does {accept ?? "nothing"} instead of Select");
        Assert(secondary == Act.Secondary, $"A does {secondary ?? "nothing"} instead of the swapped action");
        L.Keymap.ResetAll();
    }

    private static void ThemesDiffer()
    {
        var shots = new List<(string Id, byte[] Px)>();
        foreach (var t in Core.Themes.Themes.All)
        {
            L.Settings.Set(K.Theme, t.Id);
            _w.ApplyTheme(rebuild: true);
            Pump(20);
            using var frame = _w.CaptureRenderedFrame()!;
            using var fb = ((Avalonia.Media.Imaging.WriteableBitmap)frame).Lock();
            var px = new List<byte>();
            unsafe
            {
                var p = (byte*)fb.Address;
                for (int y = 0; y < fb.Size.Height; y += 12)
                    for (int x = 0; x < fb.Size.Width; x += 12)
                    {
                        var o = y * fb.RowBytes + x * 4;
                        px.Add(p[o]); px.Add(p[o + 1]); px.Add(p[o + 2]);
                    }
            }
            shots.Add((t.Id, px.ToArray()));
            Assert(_w.Top is HomeScreen h && h.Focused is not null, $"{t.Id}: home has no focus");
        }
        // Layout and colour both differ between themes, so per-pixel distance is large.
        for (int i = 0; i < shots.Count; i++)
            for (int j = i + 1; j < shots.Count; j++)
            {
                double d = 0;
                for (int k = 0; k < shots[i].Px.Length; k++) d += Math.Abs(shots[i].Px[k] - shots[j].Px[k]);
                d /= shots[i].Px.Length;
                Assert(d > 12, $"{shots[i].Id} and {shots[j].Id} look the same (mean difference {d:0.0})");
            }
        L.Settings.Set(K.Theme, "aurora");
        _w.ApplyTheme(rebuild: true);
    }

    private static void KeyboardFits()
    {
        _w.Keyboard(new KeyboardRequest("Check", "text"), _ => { });
        Pump(10);
        var osk = _w.Overlays.OfType<OnScreenKeyboard>().Single();
        foreach (var c in osk.GetVisualDescendants().OfType<Card>())
        {
            var p = c.TranslatePoint(new Point(0, 0), _w)!.Value;
            Assert(p.X >= 0 && p.Y >= 0 && p.X + c.Bounds.Width <= _w.DesignWidth + 1 && p.Y + c.Bounds.Height <= _w.DesignHeight + 1,
                $"a key sits off screen at {p}");
        }
        Press(PadButton.B);
    }

    private static void DormantChord()
    {
        var returned = false;
        L.Dormant.Launched("check", null);
        L.Dormant.Observe(loungeInFront: false, launchedAlive: false);
        Pump();
        Assert(L.Router.Context == "Dormant", $"context is {L.Router.Context}, expected Dormant");
        var before = _w.Overlays.Count;
        Press(PadButton.Menu);                     // would open settings if Lounge were listening
        Press(PadButton.A);
        Assert(_w.Overlays.Count == before, "Lounge reacted while dormant");
        L.Router.Fired += Spy;
        Hold(PadButton.View | PadButton.Menu, L.Settings.Int(K.ReturnHoldMs) + 120);
        L.Router.Fired -= Spy;
        Assert(returned, "the return chord did not fire");
        L.Dormant.Observe(loungeInFront: true, launchedAlive: false);
        Pump();
        Assert(L.Router.Context != "Dormant", "Lounge stayed dormant after coming back");

        void Spy(ActionEvent e) { if (e.Action == Act.Return) returned = true; }
    }

    private static void PlayAndSeek()
    {
        var clip = Path.Combine(Path.GetTempPath(), "lounge-clips-v2", "clip.mp4");
        if (!File.Exists(clip)) throw new InvalidOperationException("test clip missing; run the unit tests first");
        Playback.Open(clip, resume: false);
        var deadline = DateTime.Now.AddSeconds(8);
        while (DateTime.Now < deadline && !L.Player.IsPlaying) Pump(5);
        Assert(_w.Top is PlayerScreen, "the player did not open");
        Assert(L.Player.IsPlaying, $"not playing ({L.Player.State}, {L.Player.LastError})");
        Assert(L.Router.Context == "Player", $"context is {L.Router.Context}");
        L.Settings.Set(K.SkipSeconds, 5);   // the test clip is 8 s long
        Press(PadButton.DPadRight);                // +10 s
        Pump(10);
        Assert(L.Player.Position > 3.5, $"skip landed at {L.Player.Position:0.0}");
        var before = L.Player.Position;
        Press(PadButton.DPadLeft);                 // -10 s
        Pump(10);
        Assert(L.Player.Position < before, $"skip back did not move back ({before:0.00} -> {L.Player.Position:0.00}, state {L.Player.State}, top {_w.Top?.GetType().Name}, overlays {_w.Overlays.Count}, ctx {L.Router.Context})");
        // Full trigger: scrub forward at the top speed, then let go.
        for (int i = 0; i < 20; i++) { Pad(PadButton.None, rt: 1f); Pump(1); }
        Assert(L.Player.Scrubbing, "the trigger did not start scrubbing");
        Pad(PadButton.None);
        Pump(10);
        Assert(!L.Player.Scrubbing, "releasing the trigger did not settle");
        Press(PadButton.B);                        // stop
        Pump(10);
        Assert(_w.Top is not PlayerScreen, "B did not leave the player");
    }
}
