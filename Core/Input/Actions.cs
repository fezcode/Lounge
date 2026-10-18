namespace Lounge.Core.Input;

/// <summary>Everything the controller can ask Lounge to do. Modules react to these and
/// never to buttons, which is what makes every button remappable.</summary>
public static class Act
{
    // Navigation
    public const string Up = "Nav.Up";
    public const string Down = "Nav.Down";
    public const string Left = "Nav.Left";
    public const string Right = "Nav.Right";
    public const string Accept = "Nav.Accept";
    public const string Back = "Nav.Back";
    public const string Secondary = "Nav.Secondary";
    public const string Tertiary = "Nav.Tertiary";
    public const string PageUp = "Nav.PageUp";
    public const string PageDown = "Nav.PageDown";
    public const string PrevSection = "Nav.PrevSection";
    public const string NextSection = "Nav.NextSection";
    public const string MoveMode = "Nav.MoveMode";

    // App
    public const string Home = "App.Home";
    public const string Settings = "App.Settings";
    public const string ContextMenu = "App.ContextMenu";
    public const string Return = "App.Return";
    public const string VolumeUp = "Volume.Up";
    public const string VolumeDown = "Volume.Down";
    public const string Mute = "Volume.Mute";

    // Player
    public const string PlayPause = "Player.PlayPause";
    public const string Stop = "Player.Stop";
    public const string SeekForward = "Player.SeekForward";
    public const string SeekBackward = "Player.SeekBackward";
    public const string SkipForward = "Player.SkipForward";
    public const string SkipBack = "Player.SkipBack";
    public const string BigSkipForward = "Player.BigSkipForward";
    public const string BigSkipBack = "Player.BigSkipBack";
    public const string Tracks = "Player.Tracks";
    public const string Info = "Player.Info";
    public const string ToggleSubtitles = "Player.ToggleSubtitles";
    public const string NextItem = "Player.Next";
    public const string PrevItem = "Player.Previous";
    public const string Screenshot = "Player.Screenshot";

    // On-screen keyboard
    public const string KeyBackspace = "Keyboard.Backspace";
    public const string KeySpace = "Keyboard.Space";
    public const string KeyCursorLeft = "Keyboard.CursorLeft";
    public const string KeyCursorRight = "Keyboard.CursorRight";
    public const string KeyShift = "Keyboard.Shift";
    public const string KeySuggest = "Keyboard.Suggestion";
    public const string KeyDone = "Keyboard.Done";

    // Files
    public const string FileSelect = "Files.Select";
    public const string FileRename = "Files.Rename";

    // Controller mouse
    public const string CursorToggle = "Cursor.Toggle";
    public const string CursorMove = "Cursor.Move";
    public const string CursorScroll = "Cursor.Scroll";
    public const string CursorClick = "Cursor.Click";
    public const string CursorRightClick = "Cursor.RightClick";
    public const string CursorKeyboard = "Cursor.Keyboard";
    public const string CursorBack = "Cursor.Back";

    public sealed record Info_(string Id, string Label, bool Repeats = false, bool Analog = false);

    public static readonly IReadOnlyList<Info_> Catalog =
    [
        new(Up, "Move up", true), new(Down, "Move down", true), new(Left, "Move left", true), new(Right, "Move right", true),
        new(Accept, "Select"), new(Back, "Back"), new(Secondary, "Secondary action"), new(Tertiary, "Third action"),
        new(PageUp, "Page up", true), new(PageDown, "Page down", true),
        new(PrevSection, "Previous section"), new(NextSection, "Next section"), new(MoveMode, "Move the focused tile"),
        new(Home, "Home"), new(Settings, "Settings"), new(ContextMenu, "Options menu"), new(Return, "Return to Lounge"),
        new(VolumeUp, "Volume up", true), new(VolumeDown, "Volume down", true), new(Mute, "Mute"),
        new(PlayPause, "Play / pause"), new(Stop, "Stop"),
        new(SeekForward, "Fast forward (pressure = speed)", Analog: true),
        new(SeekBackward, "Rewind (pressure = speed)", Analog: true),
        new(SkipForward, "Skip forward", true), new(SkipBack, "Skip back", true),
        new(BigSkipForward, "Long skip forward", true), new(BigSkipBack, "Long skip back", true),
        new(Tracks, "Audio, subtitles and picture"), new(Info, "Information"),
        new(ToggleSubtitles, "Subtitles on/off"), new(NextItem, "Next in queue"), new(PrevItem, "Previous in queue"),
        new(Screenshot, "Take a screenshot"),
        new(KeyBackspace, "Backspace", true), new(KeySpace, "Space", true),
        new(KeyCursorLeft, "Cursor left", true), new(KeyCursorRight, "Cursor right", true),
        new(KeyShift, "Shift / symbols"), new(KeySuggest, "Accept suggestion"), new(KeyDone, "Done"),
        new(FileSelect, "Select file"), new(FileRename, "Rename"),
        new(CursorToggle, "Controller mouse on/off"), new(CursorMove, "Move pointer", Analog: true),
        new(CursorScroll, "Scroll", Analog: true), new(CursorClick, "Click"), new(CursorRightClick, "Right click"),
        new(CursorKeyboard, "Keyboard"), new(CursorBack, "Browser back"),
    ];

    private static readonly Dictionary<string, Info_> ById = Catalog.ToDictionary(i => i.Id);

    public static Info_ Describe(string id) => ById.TryGetValue(id, out var i) ? i : new Info_(id, id);
}
