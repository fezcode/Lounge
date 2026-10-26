using Lounge.Core;
using Lounge.Core.Audio;
using Lounge.Core.Input;
using Lounge.Core.Launch;
using Lounge.Core.Library;
using Lounge.Core.Media;
using Lounge.Core.Settings;
using Lounge.Core.Weather;
using Lounge.Media;
using Lounge.Platform;

namespace Lounge.Shell;

/// <summary>Lounge's services, created once at startup. Screens reach them here rather
/// than through a container: there is one window and one of each.</summary>
public static class L
{
    public static SettingsStore Settings { get; set; } = null!;
    public static Keymap Keymap { get; set; } = null!;
    public static ActionRouter Router { get; set; } = null!;
    /// <summary>Every controller (XInput and HID), each through its own button layout.</summary>
    public static ControllerHub Pad { get; set; } = null!;
    public static TileStore Tiles { get; set; } = null!;
    public static WatchHistory History { get; set; } = null!;
    public static LibraryDb Library { get; set; } = null!;
    public static MediaPlayer Player { get; set; } = null!;
    public static PlayQueue Queue { get; } = new();
    public static SoundEngine Sound { get; set; } = null!;
    public static SystemVolume Volume { get; set; } = null!;
    /// <summary>The home widgets' forecast; offline until start-up gives it a connection.</summary>
    public static WeatherService Weather { get; set; } = new(null, null);
    public static DormantState Dormant { get; } = new();
    public static MainWindow Window { get; set; } = null!;
    public static bool Preview { get; set; }

    /// <summary>What the player is playing: a video (PlayerScreen) or music (Now Playing).</summary>
    public static MediaKind NowPlayingKind { get; set; }

    public static void Play(UiSound s) => Sound?.Play(s);

    /// <summary>Brings the forecast up to date while the weather widget is on.</summary>
    public static void RefreshWeather(bool force = false)
    {
        if (Settings is null || !Settings.Bool(K.ShowWeather)) return;
        _ = Weather.RefreshAsync(Settings.String(K.WeatherPlace), WeatherService.FahrenheitFor(Settings.String(K.WeatherUnits)), force);
    }

    public static PlayerOptions PlayerOptions() => new()
    {
        HwDecode = Settings.Bool(K.HwDecode),
        GpuConvert = Settings.Bool(K.GpuConvert),
        PitchCorrect = Settings.Bool(K.PitchCorrect),
        SubtitlesEnabled = Settings.Bool(K.SubtitlesEnabled),
        SubAuto = Settings.Bool(K.SubAuto),
        SubLanguage = Settings.String(K.SubAutoLang),
        AudioLanguage = Settings.String(K.AudioLanguage),
        Volume = (float)Settings.Double(K.PlayerVolume),
    };

    public static SubStyle SubStyle() => new(
        Settings.String(K.SubFont),
        Settings.Double(K.SubFontScale),
        Convert.ToUInt32(Settings.String(K.SubColor), 16),
        Convert.ToUInt32(Settings.String(K.SubOutlineColor), 16),
        Settings.Double(K.SubOutline),
        Settings.Double(K.SubShadow));

    public static void ApplyRouterOptions()
    {
        var o = Router.Options;
        o.HoldMs = Settings.Int(K.HoldMs);
        o.ReturnHoldMs = Settings.Int(K.ReturnHoldMs);
        o.RepeatDelayMs = Settings.Int(K.RepeatDelayMs);
        o.RepeatRateMs = Settings.Int(K.RepeatRateMs);
        o.Deadzone = Settings.Int(K.Deadzone) / 100f;
        o.TriggerThreshold = Settings.Int(K.TriggerThreshold) / 100f;
        Pad.ReadGuide = Settings.Bool(K.UseGuide);
    }

    public static void Rumble(float strength = 0.35f, int ms = 60)
    {
        if (!Settings.Bool(K.Vibration)) return;
        var (power, duration) = Lounge.Core.Input.XInputPad.Feel(strength, ms, Settings.Int(K.VibrationStrength));
        if (power > 0) _ = Pad.Pulse(power, duration);
    }

    public static string AgentPath => Path.Combine(AppContext.BaseDirectory, "LoungeAgent.exe");
}
