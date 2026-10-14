using Avalonia.Threading;
using Lounge.Core;
using Lounge.Core.Library;
using Lounge.Core.Settings;
using Lounge.Media;

namespace Lounge.Shell;

/// <summary>Runs library scans in the background and keeps watching the folders.</summary>
public static class LibraryService
{
    private static CancellationTokenSource? _scan;
    private static IDisposable? _debounce;
    private static readonly List<FileSystemWatcher> Watchers = new();

    public static bool Scanning { get; private set; }
    public static event Action? Updated;

    public static void ScanSoon(TimeSpan? delay = null)
    {
        _debounce?.Dispose();
        _debounce = DispatcherTimer.RunOnce(() => _ = ScanAsync(quiet: true), delay ?? TimeSpan.FromSeconds(1));
    }

    public static async Task ScanAsync(bool quiet)
    {
        _scan?.Cancel();
        var cts = _scan = new CancellationTokenSource();
        var videos = L.Settings.List(K.VideoRoots);
        var music = L.Settings.List(K.MusicRoots);
        Scanning = true;
        try
        {
            var scanner = new LibraryScanner(L.Library, MediaProbe.Probe, AppPaths.Thumbs);
            var (v, m) = await Task.Run(() =>
                (scanner.Scan(LibraryKind.Video, videos, ct: cts.Token), scanner.Scan(LibraryKind.Music, music, ct: cts.Token)), cts.Token);
            if (cts.IsCancellationRequested) return;
            var added = v.Added + m.Added;
            if (!quiet || added > 0)
                L.Window.Toast(added > 0 ? $"Library updated: {added} new item{(added == 1 ? "" : "s")}." : "Library is up to date.", "refresh");
            foreach (var missing in v.MissingRoots.Concat(m.MissingRoots))
                L.Window.Toast($"Can't reach {missing}. Its items are kept until it is back.", "warning", 5);
            Updated?.Invoke();
            Watch(videos.Concat(music).ToList());
        }
        catch (OperationCanceledException) { }
        finally { Scanning = false; }
    }

    private static void Watch(IReadOnlyList<string> roots)
    {
        foreach (var w in Watchers) w.Dispose();
        Watchers.Clear();
        if (!L.Settings.Bool(K.WatchFolders)) return;
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var w = new FileSystemWatcher(root) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                w.Created += (_, _) => Dispatcher.UIThread.Post(() => ScanSoon(TimeSpan.FromSeconds(4)));
                w.Deleted += (_, _) => Dispatcher.UIThread.Post(() => ScanSoon(TimeSpan.FromSeconds(4)));
                w.Renamed += (_, _) => Dispatcher.UIThread.Post(() => ScanSoon(TimeSpan.FromSeconds(4)));
                Watchers.Add(w);
            }
            catch (Exception e) when (e is ArgumentException or IOException) { }
        }
    }
}
