using Avalonia.Threading;
using Lounge.Core.Media;
using Lounge.Core.Settings;
using Lounge.Media;
using Lounge.Screens;

namespace Lounge.Shell;

/// <summary>Opening media from anywhere: library tiles, Files, "Jump back in", the
/// command line. Videos go to the player screen, audio to Now Playing; both share the
/// one player. Resume positions live in Lounge's own history.ini (tivi's format).</summary>
public static class Playback
{
    public static string? CurrentTitle { get; private set; }

    public static void Open(string path, IReadOnlyList<string>? queue = null, int index = 0, bool? resume = null)
    {
        if (!File.Exists(path)) { L.Window.Toast("That file is no longer there.", "warning"); return; }
        var kind = MediaKinds.Of(path);
        if (kind is not (MediaKind.Video or MediaKind.Audio)) { L.Window.Toast("Lounge can't play that kind of file.", "warning"); return; }
        RememberCurrent();
        if (queue is { Count: > 0 }) L.Queue.Set(queue, index);
        else L.Queue.OpenWithSiblings(path, L.Settings.Bool(K.AutoQueue));
        L.NowPlayingKind = kind;
        if (kind == MediaKind.Video)
        {
            var existing = L.Window.Find<PlayerScreen>();
            if (existing is not null) L.Window.Remove(existing);
            var nowPlaying = L.Window.Find<NowPlayingScreen>();
            if (nowPlaying is not null) L.Window.Remove(nowPlaying);
            L.Window.Push(new PlayerScreen(path, resume));
        }
        else
        {
            var playerScreen = L.Window.Find<PlayerScreen>();
            if (playerScreen is not null) L.Window.Remove(playerScreen);
            StartAudio(path);
            if (L.Window.Top is not NowPlayingScreen) L.Window.Push(new NowPlayingScreen());
        }
    }

    /// <summary>Opens and plays an audio file in the shared player (no screen change).</summary>
    public static void StartAudio(string path)
    {
        var opts = L.PlayerOptions();
        CurrentTitle = Path.GetFileNameWithoutExtension(path);
        _ = Task.Run(() =>
        {
            var ok = L.Player.Open(path, opts);
            Dispatcher.UIThread.Post(() =>
            {
                if (!ok) { L.Window.Toast(L.Player.LastError ?? "Can't play that file.", "warning"); return; }
                L.Player.Play();
            });
        });
    }

    /// <summary>Advances the queue after a track or episode ends. False at the end.</summary>
    public static bool PlayNext()
    {
        RememberCurrent(reachedEnd: true);
        var next = L.Queue.Next();
        if (next is null) return false;
        if (L.NowPlayingKind == MediaKind.Audio) StartAudio(next);
        else L.Window.Find<PlayerScreen>()?.Load(next, resume: false);
        return true;
    }

    public static bool PlayPrevious()
    {
        if (L.Player.Position > 3) { L.Player.Seek(0); return true; }
        RememberCurrent();
        var prev = L.Queue.Previous();
        if (prev is null) { L.Player.Seek(0); return false; }
        if (L.NowPlayingKind == MediaKind.Audio) StartAudio(prev);
        else L.Window.Find<PlayerScreen>()?.Load(prev, resume: false);
        return true;
    }

    /// <summary>Notes where the open file is, like tivi does on switching files.</summary>
    public static void RememberCurrent(bool reachedEnd = false)
    {
        var p = L.Player;
        if (p?.Path is null || p.State is PlayerState.Idle or PlayerState.Failed or PlayerState.Opening) return;
        if (!L.Settings.Bool(K.ResumePlayback) || !p.HasVideo) return;
        L.History.Remember(p.Path, p.Position, p.Duration, p.Subtitles.DelayMs, reachedEnd || p.State == PlayerState.Ended);
        L.History.Save();
    }

    public static string NowPlayingLine()
    {
        var path = L.Player.Path;
        if (path is null) return "";
        var item = L.Library.Get(path);
        if (item is not null && item.Artist.Length > 0) return $"{item.Title} · {item.Artist}";
        return item?.Title ?? Path.GetFileNameWithoutExtension(path);
    }
}
