namespace Lounge.Core.Launch;

public enum ShellMode
{
    /// <summary>Lounge has the foreground and reacts to the controller.</summary>
    Active,
    /// <summary>An app Lounge launched is in front: only the return chord is heard.</summary>
    Dormant,
    /// <summary>Something else is in front (alt-tab, a notification): Lounge hears nothing.</summary>
    Background,
}

/// <summary>Decides who owns the controller. XInput is not exclusive, so a launched game
/// always gets the pad; this only decides whether Lounge reacts too.</summary>
public sealed class DormantState
{
    private int? _launchedPid;
    private string? _launchedTileId;

    public ShellMode Mode { get; private set; } = ShellMode.Active;
    public string? LaunchedTileId => _launchedTileId;
    public int? LaunchedPid => _launchedPid;
    public bool HasLaunchedApp => _launchedTileId is not null;

    public event Action<ShellMode>? ModeChanged;
    /// <summary>The launched app went away by itself: Lounge should come back.</summary>
    public event Action? LaunchedAppExited;

    public void Launched(string tileId, int? pid)
    {
        _launchedTileId = tileId;
        _launchedPid = pid;
    }

    /// <summary>Called on every foreground check. <paramref name="loungeInFront"/>: our
    /// window is the foreground window. <paramref name="launchedAlive"/>: the launched
    /// process (if any was tracked) still runs.</summary>
    public void Observe(bool loungeInFront, bool launchedAlive)
    {
        if (_launchedTileId is not null && _launchedPid is not null && !launchedAlive)
        {
            _launchedTileId = null;
            _launchedPid = null;
            LaunchedAppExited?.Invoke();
        }

        var next = loungeInFront ? ShellMode.Active
                 : _launchedTileId is not null ? ShellMode.Dormant
                 : ShellMode.Background;
        if (next == ShellMode.Active && _launchedTileId is not null && _launchedPid is null)
            _launchedTileId = null;   // a protocol launch we could not track: back in front means done
        if (next == Mode) return;
        Mode = next;
        ModeChanged?.Invoke(next);
    }

    /// <summary>The launched process handed off and quit (launcher stubs, browsers that
    /// reuse a running instance). Keep waiting for Lounge to be brought back instead of
    /// treating it as the app closing.</summary>
    public void Untrack() => _launchedPid = null;

    /// <summary>The user came back through the return chord or Home.</summary>
    public void Returned(bool appClosed)
    {
        if (appClosed) { _launchedTileId = null; _launchedPid = null; }
    }

    /// <summary>The router context to use for a mode, given the context the screen wants.</summary>
    public static string ContextFor(ShellMode mode, string screenContext) => mode switch
    {
        ShellMode.Active => screenContext,
        ShellMode.Dormant => "Dormant",
        _ => "Dormant",
    };
}
