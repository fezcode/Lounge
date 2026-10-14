using System.Diagnostics;
using Lounge.Core;
using Lounge.Core.Input;
using Lounge.Core.Settings;
using Lounge.Platform;

namespace Lounge.Agent;

/// <summary>LoungeAgent: a tiny helper that starts with Windows, watches for a controller
/// to connect, and offers "Would you like to go to Lounge?". It polls XInput once a
/// second while idle (empty slots are only probed that often anyway) and reads its
/// options from Lounge's settings.json each time it might ask.</summary>
internal static class Program
{
    private const string Help = """
        LoungeAgent - offers Lounge when a controller connects

          -v, --version   Print version and exit
          -h, --help      Show this help and exit
              --ask       Show the prompt once now (for trying it out)

        Turn it on or off in Lounge: Settings > Agent. It reads its options from
        %APPDATA%\fezcode\Lounge\settings.json.
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("-v") || args.Contains("--version"))
        {
            AttachConsole(-1);
            Console.WriteLine($"LoungeAgent {Product.Version}");
            return 0;
        }
        if (args.Contains("-h") || args.Contains("--help"))
        {
            AttachConsole(-1);
            Console.WriteLine(Help);
            return 0;
        }

        var render = Array.IndexOf(args, "--render");
        if (render >= 0 && render + 1 < args.Length)
        {
            // Development: draw the prompt to a PNG without showing it.
            ApplicationConfiguration.Initialize();
            using var form = new PromptForm(new XInputPad(), 10);
            form.CreateControl();
            using var bmp = new System.Drawing.Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(args[render + 1], System.Drawing.Imaging.ImageFormat.Png);
            return 0;
        }

        using var mutex = new Mutex(true, $@"Local\LoungeAgent-{Environment.UserName}", out var first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();
        var agent = new Agent(askNow: args.Contains("--ask"));
        Application.Run(agent);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);
}

/// <summary>The invisible owner of the polling timer and the prompt.</summary>
internal sealed class Agent : ApplicationContext
{
    private readonly XInputPad _pad = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _lastCount;
    private DateTime _quietUntil = DateTime.MinValue;
    private PromptForm? _prompt;

    public Agent(bool askNow)
    {
        _lastCount = _pad.Read(0).Connected ? Math.Max(1, _pad.ConnectedCount) : 0;
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        if (askNow) Ask(force: true);
    }

    private static SettingsStore Settings() => new(AppPaths.Settings, AppPaths.PlayerConfig) { Persist = false };

    private void Poll()
    {
        if (_prompt is not null) return;
        var state = _pad.Read(_clock.ElapsedMilliseconds);
        var count = state.Connected ? Math.Max(1, _pad.ConnectedCount) : 0;
        var connectedNow = Lounge.Core.Launch.AgentPolicy.ControllerArrived(_lastCount, count);
        _lastCount = count;
        if (connectedNow) Ask(force: false);
    }

    private void Ask(bool force)
    {
        var s = Settings();
        if (!force)
        {
            if (!s.Bool(K.AgentEnabled)) { ExitThread(); return; }
            if (!Lounge.Core.Launch.AgentPolicy.ShouldAsk(true, DateTime.Now, _quietUntil,
                    Process.GetProcessesByName("Lounge").Length > 0, s.String(K.AgentWhen), Win32.FullscreenAppActive()))
                return;
        }
        _prompt = new PromptForm(_pad, s.Int(K.AgentTimeoutSeconds));
        if (s.Bool(K.AgentVibrate)) _ = _pad.Pulse(0.8f, 350);
        _prompt.FormClosed += (_, _) =>
        {
            var answer = _prompt.Answer;
            _prompt = null;
            if (answer == true) OpenLounge();
            else if (answer == false) _quietUntil = DateTime.Now.AddMinutes(Settings().Int(K.AgentQuietMinutes));
            // Ignore buttons still held from answering when polling resumes.
            _lastCount = _pad.Read(_clock.ElapsedMilliseconds).Connected ? Math.Max(1, _pad.ConnectedCount) : 0;
        };
        _prompt.Show();
    }

    private static void OpenLounge()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Lounge.exe");
        if (!File.Exists(exe)) return;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
