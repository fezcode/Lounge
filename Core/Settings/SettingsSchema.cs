namespace Lounge.Core.Settings;

/// <summary>Setting keys, so code never spells one twice. JSON keys are dotted; ini keys
/// use tivi's exact names.</summary>
public static class K
{
    // General
    public const string ConfirmExit = "general.confirmExit";
    public const string ShowClock = "general.showClock";
    public const string Clock24h = "general.clock24h";
    public const string IdleDimMinutes = "general.idleDimMinutes";
    public const string LaunchAtLogin = "general.launchAtLogin";

    // Appearance
    public const string Theme = "appearance.theme";
    public const string Accent = "appearance.accent";
    public const string Wallpaper = "appearance.wallpaper";
    public const string Blur = "appearance.blur";
    public const string ReduceEffects = "appearance.reduceEffects";
    public const string AnimationSpeed = "appearance.animationSpeed";
    public const string UiScale = "appearance.uiScale";
    public const string Sounds = "appearance.sounds";
    public const string SoundVolume = "appearance.soundVolume";
    public const string ShowJumpBackIn = "home.showJumpBackIn";
    public const string ShowVideos = "home.showVideos";
    public const string ShowMusic = "home.showMusic";
    public const string ShowApps = "home.showApps";
    public const string ShowWeb = "home.showWeb";

    // Controller
    public const string OpenRemote = "controller.remote";
    public const string Deadzone = "controller.deadzone";
    public const string TriggerThreshold = "controller.triggerThreshold";
    public const string Vibration = "controller.vibration";
    public const string VibrationStrength = "controller.vibrationStrength";
    public const string RepeatDelayMs = "controller.repeatDelayMs";
    public const string RepeatRateMs = "controller.repeatRateMs";
    public const string HoldMs = "controller.holdMs";
    public const string UseGuide = "controller.useGuide";
    public const string KeyboardFallback = "controller.keyboardFallback";

    // Playback (ini = tivi names)
    public const string ResumePlayback = "resume_playback";
    public const string ResumePrompt = "playback.resumePrompt";
    public const string AutoQueue = "auto_queue";
    public const string NextUpSeconds = "playback.nextUpSeconds";
    public const string SkipSeconds = "playback.skipSeconds";
    public const string BigSkipSeconds = "playback.bigSkipSeconds";
    public const string SeekBands = "playback.seekBands";
    public const string OsdSeconds = "playback.osdSeconds";
    public const string PlayerVolume = "volume";
    public const string PitchCorrect = "pitch_correct";
    public const string HwDecode = "hw_decode";
    public const string GpuConvert = "gpu_convert";
    public const string LetterboxBlack = "letterbox_black";
    public const string Brightness = "brightness";
    public const string Contrast = "contrast";
    public const string Saturation = "saturation";
    public const string Hue = "hue";
    public const string Gamma = "gamma";

    // Subtitles (ini)
    public const string SubtitlesEnabled = "subtitles_enabled";
    public const string SubAuto = "sub_auto";
    public const string SubAutoLang = "sub_auto_lang";
    public const string SubFont = "sub_font";
    public const string SubFontScale = "sub_font_scale";
    public const string SubColor = "sub_color";
    public const string SubOutlineColor = "sub_outline_color";
    public const string SubOutline = "sub_outline";
    public const string SubShadow = "sub_shadow";

    // Audio
    public const string VolumeStep = "audio.volumeStep";
    public const string AudioLanguage = "audio.language";
    public const string Ambient = "audio.ambient";
    public const string AmbientVolume = "audio.ambientVolume";
    public const string AmbientSource = "audio.ambientSource";
    public const string AmbientFolder = "audio.ambientFolder";
    public const string AmbientDelaySeconds = "audio.ambientDelaySeconds";
    public const string AmbientFadeMs = "audio.ambientFadeMs";

    // Library
    public const string VideoRoots = "library.videoRoots";
    public const string MusicRoots = "library.musicRoots";
    public const string ScanOnStartup = "library.scanOnStartup";
    public const string WatchFolders = "library.watchFolders";
    public const string FrameThumbnails = "library.frameThumbnails";
    public const string Rescan = "library.rescan";

    // Files
    public const string FilesView = "files.view";
    public const string ShowHidden = "files.showHidden";
    public const string ShowExtensions = "files.showExtensions";
    public const string LockExtension = "files.lockExtension";
    public const string ConfirmDelete = "files.confirmDelete";
    public const string PermanentDelete = "files.permanentDelete";

    // Web
    public const string WebGamepad = "web.passGamepad";
    public const string WebZoom = "web.zoom";
    public const string CursorSpeed = "web.cursorSpeed";
    public const string DrmHint = "web.drmHint";
    public const string ExternalBrowser = "web.externalBrowser";

    // Launcher
    public const string ReturnHoldMs = "launcher.returnHoldMs";
    public const string OnReturn = "launcher.onReturn";
    public const string CogasPath = "launcher.cogasPath";
    public const string MinimizeOnLaunch = "launcher.minimizeOnLaunch";

    // Agent
    public const string AgentEnabled = "agent.enabled";
    public const string AgentWhen = "agent.when";
    public const string AgentQuietMinutes = "agent.quietMinutes";
    public const string AgentTimeoutSeconds = "agent.timeoutSeconds";
    public const string AgentVibrate = "agent.vibrate";

    // About
    public const string ResetAll = "about.resetAll";
}

public static class SettingsSchema
{
    public const int Version = 1;

    public static readonly IReadOnlyList<string> Sections =
        ["General", "Appearance", "Controller", "Playback", "Subtitles", "Audio", "Library", "Files", "Web", "Launcher", "Agent", "About"];

    public static readonly IReadOnlyList<SettingChoice> Themes =
    [
        new("aurora", "Aurora"),
        new("livingroom", "Living Room"),
        new("kodi", "Kodi Classic"),
        new("console", "Console One"),
        new("series", "Series"),
    ];

    public static readonly IReadOnlyList<SettingChoice> Accents =
    [
        new("", "Theme default"),
        new("#0A84FF", "Blue"), new("#30D158", "Green"), new("#FF9F0A", "Orange"),
        new("#FF375F", "Pink"), new("#BF5AF2", "Purple"), new("#64D2FF", "Teal"),
        new("#FFD60A", "Yellow"), new("#E5B567", "Gold"),
    ];

    public static readonly IReadOnlyList<SettingChoice> Languages =
    [
        new("", "Any"), new("en", "English"), new("tr", "Turkish"), new("de", "German"),
        new("fr", "French"), new("es", "Spanish"), new("it", "Italian"), new("pt", "Portuguese"),
        new("ru", "Russian"), new("ja", "Japanese"),
    ];

    private static readonly IReadOnlyList<SettingChoice> SubColors =
    [
        new("ffffff", "White"), new("ffe066", "Yellow"), new("e0e0e0", "Light grey"),
        new("66d9ff", "Cyan"), new("a8ff60", "Green"), new("000000", "Black"),
    ];

    private static readonly IReadOnlyList<SettingChoice> SubFonts =
    [
        new("sans-serif", "Sans-serif"), new("Segoe UI", "Segoe UI"), new("Arial", "Arial"),
        new("Verdana", "Verdana"), new("Georgia", "Georgia"), new("Consolas", "Consolas"),
    ];

    public static readonly IReadOnlyList<SettingDescriptor> All = Build();

    private static readonly Dictionary<string, SettingDescriptor> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static SettingDescriptor Get(string key) =>
        ByKey.TryGetValue(key, out var d) ? d : throw new KeyNotFoundException($"Unknown setting '{key}'.");

    public static bool TryGet(string key, out SettingDescriptor d) => ByKey.TryGetValue(key, out d!);

    private static SettingDescriptor Toggle(string key, string section, string label, bool def, string help = "", SettingStore store = SettingStore.Json) =>
        new(key, section, label, SettingKind.Toggle, def) { Help = help, Store = store };

    private static SettingDescriptor Int(string key, string section, string label, int def, int min, int max, int step, string unit = "", string help = "") =>
        new(key, section, label, SettingKind.Integer, def) { Min = min, Max = max, Step = step, Unit = unit, Help = help };

    private static SettingDescriptor Num(string key, string section, string label, double def, double min, double max, double step, string unit = "", string help = "", SettingStore store = SettingStore.Json, double scale = 1) =>
        new(key, section, label, SettingKind.Number, def) { Min = min, Max = max, Step = step, Unit = unit, Help = help, Store = store, DisplayScale = scale };

    private static SettingDescriptor Choice(string key, string section, string label, string def, IReadOnlyList<SettingChoice> choices, string help = "", SettingStore store = SettingStore.Json, bool hex = false) =>
        new(key, section, label, SettingKind.Choice, def) { Choices = choices, Help = help, Store = store, IniHex = hex };

    private static SettingChoice[] C(params string[] pairs)
    {
        var list = new SettingChoice[pairs.Length / 2];
        for (int i = 0; i < list.Length; i++) list[i] = new SettingChoice(pairs[i * 2], pairs[i * 2 + 1]);
        return list;
    }

    private static List<SettingDescriptor> Build()
    {
        const SettingStore ini = SettingStore.Ini;
        return
        [
            // ---- General
            Toggle(K.ConfirmExit, "General", "Ask before leaving Lounge", true),
            Toggle(K.ShowClock, "General", "Show clock", true),
            Toggle(K.Clock24h, "General", "24-hour clock", true),
            Int(K.IdleDimMinutes, "General", "Dim when idle", 10, 0, 60, 1, " min", "Fades to a quiet clock after this long without input. 0 turns it off."),
            Toggle(K.LaunchAtLogin, "General", "Open Lounge when Windows starts", false),

            // ---- Appearance
            Choice(K.Theme, "Appearance", "Theme", "aurora", Themes, "Each theme has its own home layout, colours, sounds and ambient music."),
            Choice(K.Accent, "Appearance", "Accent colour", "", Accents),
            new(K.Wallpaper, "Appearance", "Wallpaper", SettingKind.Path, "") { Help = "An image picked in Files. Default uses the theme's own backdrop." },
            Toggle(K.Blur, "Appearance", "Frosted glass", true, "Blurs the backdrop behind panels."),
            Toggle(K.ReduceEffects, "Appearance", "Reduce effects", false, "Simpler motion and no blur, for slower graphics cards."),
            Num(K.AnimationSpeed, "Appearance", "Animation speed", 1.0, 0.5, 2.0, 0.25, "x"),
            Num(K.UiScale, "Appearance", "Interface size", 1.0, 0.8, 1.3, 0.05, "%", scale: 100),
            Toggle(K.Sounds, "Appearance", "Interface sounds", true),
            Int(K.SoundVolume, "Appearance", "Interface sound volume", 50, 0, 100, 5, "%"),
            Toggle(K.ShowJumpBackIn, "Appearance", "Home: Jump back in", true),
            Toggle(K.ShowVideos, "Appearance", "Home: Videos", true),
            Toggle(K.ShowMusic, "Appearance", "Home: Music", true),
            Toggle(K.ShowApps, "Appearance", "Home: Apps & Games", true),
            Toggle(K.ShowWeb, "Appearance", "Home: Web", true),

            // ---- Controller
            new(K.OpenRemote, "Controller", "Remote - button mapping", SettingKind.Action, null) { Help = "See every button on a controller diagram and remap it." },
            Int(K.Deadzone, "Controller", "Stick dead zone", 24, 5, 50, 1, "%"),
            Int(K.TriggerThreshold, "Controller", "Trigger threshold", 12, 5, 60, 1, "%", "How far a trigger moves before it counts."),
            Toggle(K.Vibration, "Controller", "Vibration", true),
            Int(K.VibrationStrength, "Controller", "Vibration strength", 50, 0, 100, 5, "%"),
            Int(K.RepeatDelayMs, "Controller", "Repeat delay", 380, 200, 800, 20, " ms", "How long a held direction waits before repeating."),
            Int(K.RepeatRateMs, "Controller", "Repeat rate", 90, 40, 200, 10, " ms"),
            Int(K.HoldMs, "Controller", "Hold time", 600, 300, 1500, 50, " ms", "How long a press must last to count as a hold (hold View for Home)."),
            Toggle(K.UseGuide, "Controller", "Use the Xbox button as Home", false, "Windows Game Bar takes the Xbox button. Turn Game Bar's controller shortcut off in Windows Settings first."),
            Toggle(K.KeyboardFallback, "Controller", "Keyboard and mouse fallback", true, "Arrow keys, Enter and Esc also work, but are never needed."),

            // ---- Playback
            Toggle(K.ResumePlayback, "Playback", "Resume where you left off", true, store: ini),
            Toggle(K.ResumePrompt, "Playback", "Ask before resuming", true, "Off resumes straight away."),
            Toggle(K.AutoQueue, "Playback", "Queue the rest of the folder", true, "Opening an episode queues the ones after it, in natural order.", ini),
            Int(K.NextUpSeconds, "Playback", "Show \"Next up\"", 20, 5, 60, 5, " s before end"),
            Int(K.SkipSeconds, "Playback", "Short skip", 10, 5, 60, 5, " s"),
            Int(K.BigSkipSeconds, "Playback", "Long skip", 60, 30, 600, 30, " s"),
            Choice(K.SeekBands, "Playback", "Trigger seek speeds", "2,4,8,16,32",
                C("2,4,8", "Gentle (2x-8x)", "2,4,8,16,32", "Standard (2x-32x)", "4,8,16,32,64", "Fast (4x-64x)"),
                "Pressing a trigger further moves through these speeds."),
            Int(K.OsdSeconds, "Playback", "Hide controls after", 4, 2, 10, 1, " s"),
            Num(K.PlayerVolume, "Playback", "Player volume", 0.85, 0.0, 1.0, 0.05, "%", "Player gain on top of the Windows volume (RB/LB).", ini, 100),
            Toggle(K.PitchCorrect, "Playback", "Keep pitch when changing speed", true, store: ini),
            Toggle(K.HwDecode, "Playback", "Hardware decoding", true, "Decode on the GPU with D3D11VA, falling back to software.", ini),
            Toggle(K.GpuConvert, "Playback", "GPU colour conversion", true, "Convert YUV to RGB in a shader instead of on the CPU.", ini),
            Toggle(K.LetterboxBlack, "Playback", "Pure black letterbox", true, store: ini),
            Num(K.Brightness, "Playback", "Brightness", 0.0, -1.0, 1.0, 0.05, "", store: ini),
            Num(K.Contrast, "Playback", "Contrast", 1.0, 0.0, 2.0, 0.05, "", store: ini),
            Num(K.Saturation, "Playback", "Saturation", 1.0, 0.0, 2.0, 0.05, "", store: ini),
            Num(K.Hue, "Playback", "Hue", 0.0, -180, 180, 5, " deg", store: ini),
            Num(K.Gamma, "Playback", "Gamma", 1.0, 0.2, 3.0, 0.05, "", store: ini),

            // ---- Subtitles
            Toggle(K.SubtitlesEnabled, "Subtitles", "Subtitles", true, store: ini),
            Toggle(K.SubAuto, "Subtitles", "Load matching subtitle files", true, "movie.mkv picks up movie.srt or movie.en.srt.", ini),
            Choice(K.SubAutoLang, "Subtitles", "Preferred subtitle language", "", Languages, store: ini),
            Choice(K.SubFont, "Subtitles", "Font", "sans-serif", SubFonts, "Plain-text subtitles only; ASS/SSA keep their own styling.", ini),
            Num(K.SubFontScale, "Subtitles", "Size", 1.0, 0.25, 2.5, 0.05, "%", store: ini, scale: 100),
            Choice(K.SubColor, "Subtitles", "Colour", "ffffff", SubColors, store: ini, hex: true),
            Choice(K.SubOutlineColor, "Subtitles", "Outline colour", "000000", SubColors, store: ini, hex: true),
            Num(K.SubOutline, "Subtitles", "Outline", 2.0, 0, 4, 0.5, " px", store: ini),
            Num(K.SubShadow, "Subtitles", "Shadow", 0.0, 0, 4, 0.5, " px", store: ini),

            // ---- Audio
            Int(K.VolumeStep, "Audio", "Volume step (RB/LB)", 2, 1, 10, 1, "%"),
            Choice(K.AudioLanguage, "Audio", "Preferred audio language", "", Languages),
            Toggle(K.Ambient, "Audio", "Ambient music", true, "Soft music while you browse. Fades out whenever something else plays."),
            Int(K.AmbientVolume, "Audio", "Ambient volume", 35, 0, 100, 5, "%"),
            Choice(K.AmbientSource, "Audio", "Ambient source", "theme", C("theme", "Theme music", "folder", "My folder")),
            new(K.AmbientFolder, "Audio", "Ambient folder", SettingKind.Path, "") { Help = "A folder of music picked in Files, played shuffled." },
            Int(K.AmbientDelaySeconds, "Audio", "Ambient starts after", 2, 0, 10, 1, " s"),
            Int(K.AmbientFadeMs, "Audio", "Ambient fade", 1200, 200, 3000, 100, " ms"),

            // ---- Library
            new(K.VideoRoots, "Library", "Video folders", SettingKind.Folders, Array.Empty<string>()) { Help = "Add folders from Files: open a folder, press View, then \"Add to Videos\"." },
            new(K.MusicRoots, "Library", "Music folders", SettingKind.Folders, Array.Empty<string>()) { Help = "Add folders from Files: open a folder, press View, then \"Add to Music\"." },
            Toggle(K.ScanOnStartup, "Library", "Scan when Lounge starts", true),
            Toggle(K.WatchFolders, "Library", "Watch folders for changes", true),
            Toggle(K.FrameThumbnails, "Library", "Make thumbnails from video frames", true, "Used when a video has no poster beside it."),
            new(K.Rescan, "Library", "Rescan now", SettingKind.Action, null),

            // ---- Files
            Choice(K.FilesView, "Files", "View", "list", C("list", "List", "grid", "Grid")),
            Toggle(K.ShowHidden, "Files", "Show hidden files", false),
            Toggle(K.ShowExtensions, "Files", "Show file extensions", true),
            Toggle(K.LockExtension, "Files", "Protect extension when renaming", true),
            Toggle(K.ConfirmDelete, "Files", "Confirm before deleting", true),
            Toggle(K.PermanentDelete, "Files", "Delete permanently", false, "Off sends files to the Recycle Bin."),

            // ---- Web
            Toggle(K.WebGamepad, "Web", "Give the controller to web pages", true, "Pages like YouTube TV read the controller themselves."),
            Num(K.WebZoom, "Web", "Page zoom", 1.25, 1.0, 2.0, 0.05, "%", scale: 100),
            Int(K.CursorSpeed, "Web", "Pointer speed", 8, 1, 20, 1),
            Toggle(K.DrmHint, "Web", "Suggest the full browser when video is blocked", true),
            Choice(K.ExternalBrowser, "Web", "Full browser", "edge", C("edge", "Microsoft Edge", "chrome", "Google Chrome", "default", "System default")),

            // ---- Launcher
            Int(K.ReturnHoldMs, "Launcher", "Return chord hold", 1000, 500, 3000, 100, " ms", "Hold View + Menu this long to come back to Lounge from any app."),
            Choice(K.OnReturn, "Launcher", "When returning from an app", "switch",
                C("switch", "Leave the app running", "close", "Close the app", "ask", "Ask")),
            new(K.CogasPath, "Launcher", "Cogas location", SettingKind.Path, "") { Help = "Found automatically when Cogas is installed." },
            Toggle(K.MinimizeOnLaunch, "Launcher", "Step aside while an app runs", true),

            // ---- Agent
            Toggle(K.AgentEnabled, "Agent", "Offer Lounge when a controller connects", true, "A small helper starts with Windows and asks \"Would you like to go to Lounge?\"."),
            Choice(K.AgentWhen, "Agent", "Ask", "notFullscreen",
                C("always", "Always", "notFullscreen", "Not over full-screen apps")),
            Int(K.AgentQuietMinutes, "Agent", "After \"No\", stay quiet for", 15, 0, 120, 5, " min"),
            Int(K.AgentTimeoutSeconds, "Agent", "Prompt disappears after", 10, 5, 30, 1, " s"),
            Toggle(K.AgentVibrate, "Agent", "Vibrate when asking", true),

            // ---- About
            new(K.ResetAll, "About", "Reset all settings", SettingKind.Action, null) { Help = "Every setting and button goes back to its default. Your library and history stay." },
        ];
    }
}
