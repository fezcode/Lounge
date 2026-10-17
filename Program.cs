using Avalonia;
using Lounge.Core;
using Lounge.Core.Library;
using Lounge.Core.Settings;
using Lounge.Media;
using Lounge.Platform;

namespace Lounge;

internal static class Program
{
    public static string[] Args { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        // A GUI exe has no console; borrow the terminal's when asked for text.
        if (args.Any(a => a is "-v" or "--version" or "-h" or "--help" or "--selftest")) AttachConsole(-1);
        App.StartArgs = args;
        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.WriteLine($"{Product.Name} {Product.Version}");
            return 0;
        }
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Help);
            return 0;
        }
        if (args.Contains("--selftest")) return SelfTest();

        using var instance = new SingleInstance("Lounge");
        if (!instance.IsFirst && !args.Contains("--new-instance"))
        {
            SingleInstance.Forward("Lounge", args);
            return 0;
        }
        App.Instance = instance;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                // ANGLE gives the video surface its OpenGL ES context; software stays as the fallback.
                RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
            })
            .WithInterFont()
            .LogToTrace();

    /// <summary>Proves the parts that resolve differently in a published build: FFmpeg
    /// and libass beside the exe, SQLite's native provider, XInput, settings.</summary>
    private static int SelfTest()
    {
        var failures = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "lounge-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("LOUNGE_DATA", dir);
        try
        {
            if (!Ff.Ensure()) failures.Add(Ff.Error ?? "FFmpeg did not load");
            using (var subs = new SubtitleEngine())
                if (!subs.Available) failures.Add("libass did not load");
            using (var db = new LibraryDb(Path.Combine(dir, "library.db")))
                db.Upsert(new LibraryItem(@"C:\x.mkv", LibraryKind.Video, @"C:\", @"C:\", "x", "", "", "", 0, 0, 1, 0, 0, null, 0), 1, 1);
            var settings = SettingsStore.OpenDefault();
            settings.SaveAll();
            if (!File.Exists(AppPaths.PlayerConfig)) failures.Add("config.ini was not written");
            _ = new Lounge.Core.Input.XInputPad().ReadSlots(0);
        }
        catch (Exception e) { failures.Add($"{e.GetType().Name}: {e.Message}"); }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        if (failures.Count > 0)
        {
            Console.WriteLine("Self-test FAILED: " + string.Join("; ", failures));
            return 1;
        }
        Console.WriteLine($"Self-test passed. FFmpeg {Ff.Folder}, libass, SQLite and settings are working.");
        return 0;
    }

    private const string Help = """
        Lounge - a controller-first media center for Windows

          -v, --version        Print version and exit
          -h, --help           Show this help and exit
              --selftest       Check FFmpeg, libass, SQLite and settings, then exit
              --windowed       Run in a window instead of full screen
              --play <file>    Open a video or song straight away
              --files <folder> Open the file explorer at a folder
              --new-instance   Start even if Lounge is already running

        Controller (every button can be remapped in Settings > Controller > Remote)
          A select · B back · Menu settings · View options · hold View home
          RB/LB volume · RT/LT fast forward / rewind (press harder to go faster)
          Hold View + Menu in any app to come back to Lounge

        Data lives in %APPDATA%\fezcode\Lounge.
        """;
}
