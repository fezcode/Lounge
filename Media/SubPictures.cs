namespace Lounge.Media;

/// <summary>The pictures of a bitmap subtitle track (PGS, VobSub, DVB) and when each is on
/// screen. In these formats a display set replaces whatever was showing, and many files carry
/// no durations at all: a picture lasts until the next display set, often an empty "clear".
/// Lines arrive as playback reads them, so after a seek the set that would have ended a picture
/// may never be read; these rules keep a picture from outliving the set that ends it.
/// Not thread-safe: the engine calls it under its own lock.</summary>
public sealed class SubPictures
{
    public sealed record Picture(double Start, double End, int X, int Y, int W, int H, byte[] Bgra, int RefW, int RefH);

    private readonly List<Picture> _items = new();
    private readonly HashSet<(long, int, int, int, int)> _seen = new();

    public IReadOnlyList<Picture> All => _items;

    /// <summary>A display set (with or without pictures) starts at <paramref name="start"/>:
    /// every picture that began earlier and would still be showing ends there. That includes
    /// pictures that already had an end, since one decided before a seek can be too late.</summary>
    public void BeginSet(double start)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var p = _items[i];
            if (p.Start < start && p.End > start) _items[i] = p with { End = start };
        }
    }

    public bool Has(double start, int x, int y, int w, int h) => _seen.Contains(Key(start, x, y, w, h));

    private static (long, int, int, int, int) Key(double start, int x, int y, int w, int h) => ((long)Math.Round(start * 1000), x, y, w, h);

    /// <summary>Adds a picture unless the same one (start, rectangle) is already known.
    /// <paramref name="duration"/> NaN or not positive means "until the next display set".</summary>
    public bool Add(double start, double duration, int x, int y, int w, int h, byte[] bgra, int refW, int refH)
    {
        if (!_seen.Add(Key(start, x, y, w, h))) return false;
        var end = double.IsNaN(duration) || duration <= 0 ? double.PositiveInfinity : start + duration;
        _items.Add(new Picture(start, end, x, y, w, h, bgra, refW, refH));
        return true;
    }

    /// <summary>After a seek: a picture still waiting for its end will not get it, because the
    /// set that ends it is behind the new read position (or ahead, and read again later). Drop
    /// it, and forget it, so reading that part again adds it back with its real end.</summary>
    public void Flush()
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var p = _items[i];
            if (!double.IsInfinity(p.End)) continue;
            _seen.Remove(Key(p.Start, p.X, p.Y, p.W, p.H));
            _items.RemoveAt(i);
        }
    }

    public List<Picture> Active(double now) => _items.Where(p => p.Start <= now && now < p.End).ToList();

    public void Clear() { _items.Clear(); _seen.Clear(); }
}
