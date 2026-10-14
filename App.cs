using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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

namespace Lounge;

public class App : Application
{
    public static SingleInstance? Instance { get; set; }
    public static string[] StartArgs { get; set; } = [];

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Log.Write($"Lounge {Product.Version} starting");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled", (Exception)e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("task", e.Exception); e.SetObserved(); };
            Dispatcher.UIThread.UnhandledException += OnUiException;
            StartServices();
            MainWindow.NoActivate = StartArgs.Contains("--no-activate");
            var window = new MainWindow(windowed: StartArgs.Contains("--windowed"));
            L.Window = window;
            window.ApplyTheme(rebuild: false);
            window.Push(new HomeScreen());
            desktop.MainWindow = window;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;

            foreach (var notice in new[] { L.Settings.LoadNotice, _keymapNotice, L.Tiles.LoadNotice }.Where(n => n is not null))
                DispatcherTimer.RunOnce(() => window.Toast(notice!, "warning", 6), TimeSpan.FromSeconds(1.5));
            if (!Ff.Available)
                DispatcherTimer.RunOnce(() => window.Toast(Ff.Error ?? "Video playback is unavailable.", "warning", 8), TimeSpan.FromSeconds(2));

            Instance?.Listen();
            if (Instance is not null) Instance.Activated += args => Dispatcher.UIThread.Post(() => window.OnActivated(args));
            window.Opened += (_, _) =>
            {
                if (SelfCheck.Active && !StartArgs.Contains("--play") && !StartArgs.Contains("--web"))
                    DispatcherTimer.RunOnce(() =>
                    {
                        // Real-window check of a normal start: capture home, report, exit.
                        var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
                        using (var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size))
                        {
                            rtb.Render(window);
                            rtb.Save(SelfCheck.PathFor("home.png"));
                        }
                        SelfCheck.Report("home", new
                        {
                            top = window.Top?.GetType().Name, focused = window.Top?.Focused is not null, theme = T.Def.Id,
                            topmost = window.Topmost, windowState = window.WindowState.ToString(),
                            sound = L.Sound.Available, volume = L.Volume.Available, ffmpeg = Ff.Available, pads = L.Pad.ConnectedCount,
                        });
                        window.Exit();
                    }, TimeSpan.FromSeconds(4));
                window.HandleArgs(StartArgs);
                if (L.Settings.Bool(K.ScanOnStartup)) LibraryService.ScanSoon(TimeSpan.FromSeconds(2));
                RegisterAgent();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static string? _keymapNotice;
    private static bool _crashDialogOpen;

    /// <summary>Something went wrong on the UI thread: log it, keep Lounge running, and
    /// offer a controller-friendly restart instead of vanishing.</summary>
    private static void OnUiException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("ui", e.Exception);
        e.Handled = true;
        if (_crashDialogOpen || L.Window is null) return;
        _crashDialogOpen = true;
        L.Window.Ask("Something went wrong", "Lounge ran into a problem and noted it in its log. You can carry on, or restart Lounge.",
            ["Carry on", "Restart Lounge"], 0, i =>
            {
                _crashDialogOpen = false;
                if (i != 1 || Environment.ProcessPath is not { } exe) return;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--new-instance") { UseShellExecute = true });
                L.Window.Exit();
            }, "warning");
    }

    private static void StartServices()
    {
        L.Settings = SettingsStore.OpenDefault();
        if (!File.Exists(AppPaths.Settings)) L.Settings.SaveAll();
        L.Keymap = Keymap.Load(AppPaths.Keymap, out _keymapNotice);
        L.Keymap.Changed += () => L.Keymap.Save(AppPaths.Keymap);
        L.Pad = new XInputPad();
        L.Router = new ActionRouter(L.Keymap);
        L.ApplyRouterOptions();
        L.Tiles = new TileStore(AppPaths.Tiles);
        L.History = new WatchHistory(AppPaths.History);
        L.Library = new LibraryDb(AppPaths.LibraryDb);
        Ff.Ensure();
        L.Player = new MediaPlayer();
        L.Player.Volume = (float)L.Settings.Double(K.PlayerVolume);
        L.Player.Subtitles.SetStyle(L.SubStyle());
        L.Volume = new SystemVolume();
        L.Sound = new SoundEngine();
        ApplyMotion();
        ConfigureSound();
    }

    public static void ApplyMotion()
    {
        Motion.Enabled = !L.Settings.Bool(K.ReduceEffects);
        Motion.Speed = L.Settings.Double(K.AnimationSpeed);
    }

    public static void ConfigureSound()
    {
        if (L.Sound is null) return;
        var s = L.Settings;
        L.Sound.Configure(T.Def.AmbientPreset, s.String(K.AmbientSource) == "folder", s.String(K.AmbientFolder));
        L.Sound.AmbientVolume = s.Int(K.AmbientVolume) / 100f;
        L.Sound.FadeMs = s.Int(K.AmbientFadeMs);
        L.Sound.DelayMs = s.Int(K.AmbientDelaySeconds) * 1000;
        L.Sound.SoundsEnabled = s.Bool(K.Sounds);
        L.Sound.SoundsVolume = s.Int(K.SoundVolume) / 100f;
        if (!s.Bool(K.Ambient)) L.Sound.SetAmbient(false);
    }

    /// <summary>The agent starts with Windows when enabled; turning it on also starts it now.</summary>
    public static void RegisterAgent()
    {
        try
        {
            var exe = L.AgentPath;
            if (L.Settings.Bool(K.AgentEnabled) && File.Exists(exe))
            {
                Win32.SetRunAtLogin("LoungeAgent", $"\"{exe}\"");
                if (System.Diagnostics.Process.GetProcessesByName("LoungeAgent").Length == 0)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            }
            else if (!L.Settings.Bool(K.AgentEnabled))
            {
                Win32.SetRunAtLogin("LoungeAgent", null);
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("LoungeAgent"))
                    try { p.Kill(); } catch (Exception) { } finally { p.Dispose(); }
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception) { }
    }

    public static void RegisterLounge()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        Win32.SetRunAtLogin("Lounge", L.Settings.Bool(K.LaunchAtLogin) ? $"\"{exe}\"" : null);
    }
}
