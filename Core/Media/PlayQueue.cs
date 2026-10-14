namespace Lounge.Core.Media;

public enum RepeatMode { Off, All, One }

/// <summary>The current playback queue. Opening a single file queues the files that sort
/// after it in its folder (tivi's auto-queue), so "Next up" works for episodes and albums.</summary>
public sealed class PlayQueue
{
    private readonly List<string> _items = new();
    private List<int>? _shuffleOrder;
    private readonly Random _rng;

    public PlayQueue(int? seed = null) => _rng = seed is { } s ? new Random(s) : new Random();

    public IReadOnlyList<string> Items => _items;
    public int Index { get; private set; } = -1;
    public RepeatMode Repeat { get; set; }
    public bool Shuffle
    {
        get => _shuffleOrder is not null;
        set
        {
            if (value == Shuffle) return;
            if (!value) { _shuffleOrder = null; return; }
            var rest = Enumerable.Range(0, _items.Count).Where(i => i != Index).OrderBy(_ => _rng.Next()).ToList();
            if (Index >= 0) rest.Insert(0, Index);
            _shuffleOrder = rest;
        }
    }

    public string? Current => Index >= 0 && Index < _items.Count ? _items[Index] : null;

    public event Action? Changed;

    public void Set(IEnumerable<string> items, int start = 0)
    {
        _items.Clear();
        _items.AddRange(items);
        Index = _items.Count == 0 ? -1 : Math.Clamp(start, 0, _items.Count - 1);
        if (Shuffle) { _shuffleOrder = null; Shuffle = true; }
        Changed?.Invoke();
    }

    /// <summary>Plays <paramref name="path"/> and queues its later siblings in natural order.</summary>
    public void OpenWithSiblings(string path, bool autoQueue, Func<string, IEnumerable<string>>? listDir = null)
    {
        if (!autoQueue) { Set([path]); return; }
        var dir = Path.GetDirectoryName(path);
        var kind = MediaKinds.Of(path);
        IEnumerable<string> siblings = [];
        if (!string.IsNullOrEmpty(dir))
        {
            try { siblings = (listDir ?? Directory.EnumerateFiles)(dir); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var name = Path.GetFileName(path);
        var later = siblings.Where(p => MediaKinds.Of(p) == kind)
            .Where(p => NaturalComparer.Instance.Compare(Path.GetFileName(p), name) > 0)
            .OrderBy(p => Path.GetFileName(p), NaturalComparer.Instance);
        Set(new[] { path }.Concat(later));
    }

    private int Position => _shuffleOrder is { } o ? o.IndexOf(Index) : Index;
    private int At(int position) => _shuffleOrder is { } o ? o[position] : position;

    public bool HasNext => Repeat != RepeatMode.Off ? _items.Count > 0 : Position + 1 < _items.Count;
    public bool HasPrevious => Position > 0 || Repeat == RepeatMode.All;

    public string? PeekNext()
    {
        if (_items.Count == 0) return null;
        if (Repeat == RepeatMode.One) return Current;
        var p = Position + 1;
        if (p >= _items.Count) { if (Repeat != RepeatMode.All) return null; p = 0; }
        return _items[At(p)];
    }

    public string? Next()
    {
        if (PeekNext() is null) return null;
        if (Repeat != RepeatMode.One)
        {
            var p = Position + 1;
            if (p >= _items.Count) p = 0;
            Index = At(p);
        }
        Changed?.Invoke();
        return Current;
    }

    public string? Previous()
    {
        if (_items.Count == 0) return null;
        var p = Position - 1;
        if (p < 0) { if (Repeat != RepeatMode.All) return null; p = _items.Count - 1; }
        Index = At(p);
        Changed?.Invoke();
        return Current;
    }

    public void Jump(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        Index = index;
        Changed?.Invoke();
    }
}
