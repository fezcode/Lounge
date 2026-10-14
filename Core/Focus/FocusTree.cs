namespace Lounge.Core.Focus;

public enum Dir { Up, Down, Left, Right }

public readonly record struct Pt(double X, double Y);

/// <summary>Controller focus as a tree: lists (rows, columns) and grids of items. A move
/// is offered to the deepest container first; whichever cannot move further passes it to
/// its parent. Entering a container lands on the item nearest to where focus came from,
/// or on the remembered one (rows remember their place, as on a TV). The UI supplies
/// positions through <see cref="FocusScope.Locate"/>; nothing here knows about pixels.</summary>
public abstract class FocusNode
{
    public FocusNode? Parent { get; internal set; }
    public abstract FocusItem? Current { get; }
    public abstract bool HasItems { get; }
    public abstract IEnumerable<FocusItem> Items { get; }
    internal abstract bool Move(Dir d, FocusScope scope);
    internal abstract void Enter(Dir? from, Pt? hint, FocusScope scope);
    /// <summary>Makes <paramref name="item"/> current inside this subtree; false if absent.</summary>
    internal abstract bool Select(FocusItem item);

    protected static bool Along(Dir d, bool horizontal) => horizontal ? d is Dir.Left or Dir.Right : d is Dir.Up or Dir.Down;
    protected static int StepOf(Dir d) => d is Dir.Left or Dir.Up ? -1 : 1;
}

public class FocusItem : FocusNode
{
    public FocusItem(object? view = null, object? tag = null) { View = view; Tag = tag; }

    public object? View { get; set; }
    public object? Tag { get; set; }
    public Action? Activate { get; set; }
    /// <summary>X on the controller, when the item has a second action.</summary>
    public Action? Secondary { get; set; }
    /// <summary>Called with true/false as focus arrives and leaves; the UI animates here.</summary>
    public Action<bool>? FocusChanged { get; set; }

    public override FocusItem Current => this;
    public override bool HasItems => true;
    public override IEnumerable<FocusItem> Items { get { yield return this; } }
    internal override bool Move(Dir d, FocusScope scope) => false;
    internal override void Enter(Dir? from, Pt? hint, FocusScope scope) { }
    internal override bool Select(FocusItem item) => ReferenceEquals(item, this);
}

public sealed class FocusList : FocusNode
{
    private readonly List<FocusNode> _children = new();

    public FocusList(bool horizontal, bool rememberIndex = true)
    {
        Horizontal = horizontal;
        RememberIndex = rememberIndex;
    }

    public bool Horizontal { get; }
    public bool RememberIndex { get; set; }
    public bool Wrap { get; set; }
    public int Index { get; set; }

    public IReadOnlyList<FocusNode> Children => _children;

    public T Add<T>(T node) where T : FocusNode
    {
        node.Parent = this;
        _children.Add(node);
        return node;
    }

    public void Clear() { _children.Clear(); Index = 0; }

    public void Remove(FocusNode node)
    {
        var i = _children.IndexOf(node);
        if (i < 0) return;
        _children.RemoveAt(i);
        if (Index > i || Index >= _children.Count) Index = Math.Max(0, Index - 1);
    }

    public override bool HasItems => _children.Any(c => c.HasItems);
    public override IEnumerable<FocusItem> Items => _children.SelectMany(c => c.Items);

    public override FocusItem? Current
    {
        get
        {
            if (_children.Count == 0) return null;
            Index = Math.Clamp(Index, 0, _children.Count - 1);
            if (!_children[Index].HasItems)
            {
                var first = _children.FindIndex(c => c.HasItems);
                if (first < 0) return null;
                Index = first;
            }
            return _children[Index].Current;
        }
    }

    internal override bool Move(Dir d, FocusScope scope)
    {
        if (Current is null) return false;
        if (_children[Index].Move(d, scope)) return true;
        if (!Along(d, Horizontal)) return false;
        var step = StepOf(d);
        var next = Index + step;
        while (next >= 0 && next < _children.Count && !_children[next].HasItems) next += step;
        if (next < 0 || next >= _children.Count)
        {
            if (!Wrap) return false;
            next = step > 0 ? _children.FindIndex(c => c.HasItems) : _children.FindLastIndex(c => c.HasItems);
            if (next < 0 || next == Index) return false;
        }
        var hint = scope.LocateItem(Current);
        Index = next;
        _children[next].Enter(d, hint, scope);
        return true;
    }

    internal override void Enter(Dir? from, Pt? hint, FocusScope scope)
    {
        if (!HasItems) return;
        var arrivingAlong = from is { } f && Along(f, Horizontal);
        if (arrivingAlong)
        {
            // Coming in along our own axis: start from the near end.
            Index = StepOf(from!.Value) > 0 ? _children.FindIndex(c => c.HasItems) : _children.FindLastIndex(c => c.HasItems);
        }
        else if (!RememberIndex && hint is { } h)
        {
            var best = -1; var bestDist = double.MaxValue;
            for (int i = 0; i < _children.Count; i++)
            {
                if (!_children[i].HasItems) continue;
                if (scope.LocateItem(_children[i].Current) is not { } p) continue;
                var dist = Horizontal ? Math.Abs(p.X - h.X) : Math.Abs(p.Y - h.Y);
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            if (best >= 0) Index = best;
        }
        _ = Current;   // clamps onto a child that has items
        _children[Index].Enter(from, hint, scope);
    }

    internal override bool Select(FocusItem item)
    {
        for (int i = 0; i < _children.Count; i++)
            if (_children[i].Select(item)) { Index = i; return true; }
        return false;
    }
}

public sealed class FocusGrid : FocusNode
{
    private readonly List<FocusItem> _items = new();

    public FocusGrid(int columns) => Columns = Math.Max(1, columns);

    public int Columns { get; set; }
    public int Index { get; set; }
    public IReadOnlyList<FocusItem> All => _items;

    public FocusItem Add(FocusItem item)
    {
        item.Parent = this;
        _items.Add(item);
        return item;
    }

    public void Clear() { _items.Clear(); Index = 0; }

    public override bool HasItems => _items.Count > 0;
    public override IEnumerable<FocusItem> Items => _items;
    public override FocusItem? Current => _items.Count == 0 ? null : _items[Index = Math.Clamp(Index, 0, _items.Count - 1)];

    internal override bool Move(Dir d, FocusScope scope)
    {
        if (_items.Count == 0) return false;
        int col = Index % Columns, row = Index / Columns, lastRow = (_items.Count - 1) / Columns;
        switch (d)
        {
            case Dir.Left when col > 0: Index--; return true;
            case Dir.Right when col < Columns - 1 && Index + 1 < _items.Count: Index++; return true;
            case Dir.Up when row > 0: Index -= Columns; return true;
            case Dir.Down when Index + Columns < _items.Count: Index += Columns; return true;
            case Dir.Down when row < lastRow: Index = _items.Count - 1; return true;
            default: return false;
        }
    }

    internal override void Enter(Dir? from, Pt? hint, FocusScope scope)
    {
        if (_items.Count == 0) return;
        if (from is null || hint is not { } h) { _ = Current; return; }
        int lastRow = (_items.Count - 1) / Columns;
        IEnumerable<int> candidates = from switch
        {
            Dir.Down => Enumerable.Range(0, Math.Min(Columns, _items.Count)),
            Dir.Up => Enumerable.Range(lastRow * Columns, _items.Count - lastRow * Columns),
            Dir.Right => Enumerable.Range(0, lastRow + 1).Select(r => r * Columns),
            _ => Enumerable.Range(0, lastRow + 1).Select(r => Math.Min(r * Columns + Columns - 1, _items.Count - 1)),
        };
        var vertical = from is Dir.Up or Dir.Down;
        var best = Index; var bestDist = double.MaxValue;
        foreach (var i in candidates)
        {
            if (scope.LocateItem(_items[i]) is not { } p) continue;
            var dist = vertical ? Math.Abs(p.X - h.X) : Math.Abs(p.Y - h.Y);
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        Index = best;
    }

    internal override bool Select(FocusItem item)
    {
        var i = _items.IndexOf(item);
        if (i < 0) return false;
        Index = i;
        return true;
    }
}

/// <summary>Free-form layouts (the controller diagram): a move goes to the nearest item
/// in that direction, favouring items in line over items off to the side.</summary>
public sealed class FocusSpatial : FocusNode
{
    private readonly List<FocusItem> _items = new();
    public int Index { get; set; }

    public FocusItem Add(FocusItem item)
    {
        item.Parent = this;
        _items.Add(item);
        return item;
    }

    public override bool HasItems => _items.Count > 0;
    public override IEnumerable<FocusItem> Items => _items;
    public override FocusItem? Current => _items.Count == 0 ? null : _items[Index = Math.Clamp(Index, 0, _items.Count - 1)];

    internal override bool Move(Dir d, FocusScope scope)
    {
        if (scope.LocateItem(Current) is not { } from) return false;
        var best = -1; var bestScore = double.MaxValue;
        for (int i = 0; i < _items.Count; i++)
        {
            if (i == Index || scope.LocateItem(_items[i]) is not { } p) continue;
            double dx = p.X - from.X, dy = p.Y - from.Y;
            var (primary, secondary) = d switch
            {
                Dir.Left => (-dx, dy), Dir.Right => (dx, dy), Dir.Up => (-dy, dx), _ => (dy, dx),
            };
            if (primary <= 1) continue;
            var score = primary + Math.Abs(secondary) * 2.5;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        if (best < 0) return false;
        Index = best;
        return true;
    }

    internal override void Enter(Dir? from, Pt? hint, FocusScope scope)
    {
        if (hint is not { } h || _items.Count == 0) return;
        var best = Index; var bestDist = double.MaxValue;
        for (int i = 0; i < _items.Count; i++)
        {
            if (scope.LocateItem(_items[i]) is not { } p) continue;
            var dist = Math.Abs(p.X - h.X) + Math.Abs(p.Y - h.Y);
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        Index = best;
    }

    internal override bool Select(FocusItem item)
    {
        var i = _items.IndexOf(item);
        if (i < 0) return false;
        Index = i;
        return true;
    }
}

/// <summary>The focus of one screen or layer. Tells the old and new item when focus
/// moves, so the UI can animate, scroll and play a sound.</summary>
public sealed class FocusScope
{
    private FocusNode _root;

    public FocusScope(FocusNode root) => _root = root;

    public FocusNode Root
    {
        get => _root;
        set { _root = value; Refresh(); }
    }

    public FocusItem? Focused { get; private set; }

    /// <summary>Where an item sits on screen (its centre), supplied by the UI.</summary>
    public Func<FocusItem, Pt?> Locate { get; set; } = _ => null;

    /// <summary>(old, new) after every change.</summary>
    public event Action<FocusItem?, FocusItem?>? Moved;

    internal Pt? LocateItem(FocusItem? item) => item is null ? null : Locate(item);

    public bool Move(Dir d)
    {
        if (Focused is null) { Refresh(); return Focused is not null; }
        if (!_root.Move(d, this)) return false;
        Set(_root.Current);
        return true;
    }

    public void Focus(FocusItem item)
    {
        if (_root.Select(item)) Set(item);
    }

    /// <summary>Re-reads the current item after the tree changed (items added or removed).</summary>
    public void Refresh()
    {
        var current = _root.Current;
        if (Focused is not null && _root.Items.Contains(Focused) && current != Focused && _root.Select(Focused)) current = Focused;
        Set(current);
    }

    public void Activate() => Focused?.Activate?.Invoke();

    private void Set(FocusItem? item)
    {
        if (ReferenceEquals(item, Focused)) return;
        var old = Focused;
        Focused = item;
        old?.FocusChanged?.Invoke(false);
        item?.FocusChanged?.Invoke(true);
        Moved?.Invoke(old, item);
    }
}
