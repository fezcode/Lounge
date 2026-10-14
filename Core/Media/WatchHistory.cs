using System.Globalization;
using System.Text;

namespace Lounge.Core.Media;

public readonly record struct WatchEntry(double Position, double Duration, int SubDelayMs, long LastSeenUnix);

/// <summary>Resume history in tivi's history.ini format, kept in Lounge's own folder:
/// <c>position|duration|sub_delay_ms|last_seen_unix|path</c>, newest first, capped at
/// 400 entries. The path comes last so it may contain '|'. Same resume rules as tivi:
/// resume only past 30 s and not within the last 60 s; a file watched to the end, or
/// barely started, is forgotten.</summary>
public sealed class WatchHistory
{
    public const int MaxEntries = 400;
    public const double ResumeMin = 30.0;
    public const double ResumeTail = 60.0;

    private readonly string _path;
    private readonly Dictionary<string, (string Path, WatchEntry Entry)> _items = new(StringComparer.Ordinal);
    private readonly Func<long> _clock;
    private readonly object _gate = new();
    private bool _dirty;

    public WatchHistory(string path, Func<long>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Load();
    }

    public int Count { get { lock (_gate) return _items.Count; } }

    /// <summary>Windows paths differ in case and separator without naming a different file.</summary>
    public static string Key(string path) => path.Replace('/', '\\').ToLowerInvariant();

    private void Load()
    {
        if (!File.Exists(_path)) return;
        foreach (var raw in File.ReadLines(_path))
        {
            if (raw.Length == 0 || raw[0] == '#') continue;
            if (!TryParse(raw, out var path, out var entry)) continue;
            _items.TryAdd(Key(path), (path, entry));
        }
    }

    public static bool TryParse(string line, out string path, out WatchEntry entry)
    {
        path = ""; entry = default;
        var parts = line.TrimEnd('\r', '\n').Split('|', 5);
        if (parts.Length != 5 || parts[4].Length == 0) return false;
        var inv = CultureInfo.InvariantCulture;
        if (!double.TryParse(parts[0], NumberStyles.Float, inv, out var pos)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, inv, out var dur)) return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, inv, out var delay)) return false;
        if (!long.TryParse(parts[3], NumberStyles.Integer, inv, out var seen)) return false;
        if (double.IsNaN(pos) || double.IsNaN(dur)) return false;
        path = parts[4];
        entry = new WatchEntry(pos, dur, delay, seen);
        return true;
    }

    public bool TryGet(string path, out WatchEntry entry)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(Key(path), out var v)) { entry = v.Entry; return true; }
            entry = default;
            return false;
        }
    }

    /// <summary>Where to resume <paramref name="path"/>, or null to start from the top.</summary>
    public double? ResumePoint(string path, double knownDuration = 0)
    {
        if (!TryGet(path, out var e) || e.Position <= ResumeMin) return null;
        var dur = knownDuration > 0 ? knownDuration : e.Duration;
        if (dur > 0 && e.Position >= dur - ResumeTail) return null;
        return e.Position;
    }

    /// <summary>tivi's remember_current: a finished or barely-started file is forgotten.</summary>
    public void Remember(string path, double position, double duration, int subDelayMs, bool reachedEnd)
    {
        bool finished = reachedEnd || (duration > 0 && position >= duration - 10.0);
        var pos = finished ? 0.0 : position;
        if (pos < 5.0 && subDelayMs == 0) Forget(path);
        else Put(path, new WatchEntry(pos, duration, subDelayMs, 0));
    }

    public void Put(string path, WatchEntry entry)
    {
        if (string.IsNullOrEmpty(path)) return;
        lock (_gate)
        {
            var key = Key(path);
            if (_items.TryGetValue(key, out var old) && old.Entry.Position == entry.Position &&
                old.Entry.Duration == entry.Duration && old.Entry.SubDelayMs == entry.SubDelayMs) return;
            _items[key] = (path, entry with { LastSeenUnix = _clock() });
            _dirty = true;
        }
    }

    public void Forget(string path)
    {
        lock (_gate) { if (_items.Remove(Key(path))) _dirty = true; }
    }

    /// <summary>Most recently watched first.</summary>
    public IReadOnlyList<(string Path, WatchEntry Entry)> Recent(int max = MaxEntries)
    {
        lock (_gate)
            return _items.Values.OrderByDescending(v => v.Entry.LastSeenUnix).Take(max).ToList();
    }

    public void Save()
    {
        string text;
        lock (_gate)
        {
            if (!_dirty) return;
            var sb = new StringBuilder("# tivi watch history - position|duration|sub_delay_ms|last_seen|path\n");
            var inv = CultureInfo.InvariantCulture;
            foreach (var (path, e) in Recent(MaxEntries))
                sb.Append(e.Position.ToString("0.000", inv)).Append('|')
                  .Append(e.Duration.ToString("0.000", inv)).Append('|')
                  .Append(e.SubDelayMs.ToString(inv)).Append('|')
                  .Append(e.LastSeenUnix.ToString(inv)).Append('|')
                  .Append(path).Append('\n');
            text = sb.ToString();
            _dirty = false;
        }
        AtomicFile.WriteAllText(_path, text);
    }
}
