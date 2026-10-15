using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lounge.Core.Launch;

public enum TileKind
{
    /// <summary>An executable or shortcut; Target is the path, Args its arguments.</summary>
    App,
    /// <summary>A URL or protocol handed to the shell (steam://open/bigpicture).</summary>
    Protocol,
    /// <summary>A Microsoft Store app; Target is its AppUserModelID.</summary>
    StoreApp,
    /// <summary>Cogas, opened straight into Big Picture.</summary>
    Cogas,
    /// <summary>A web page in the embedded browser; Target is the URL.</summary>
    Web,
}

public enum ReturnBehaviour { Default, Switch, Close, Ask }

public sealed class Tile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public TileKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Args { get; set; } = "";
    public string WorkingDir { get; set; } = "";
    /// <summary>An image path; empty uses the app's own icon or a generated monogram.</summary>
    public string Icon { get; set; } = "";
    /// <summary>Brand colour used for the monogram and tile glow.</summary>
    public string Color { get; set; } = "#3A3A4A";
    public string UserAgent { get; set; } = "";
    public bool AllowMouse { get; set; }
    public bool OpenExternally { get; set; }
    public bool IsGame { get; set; }
    public ReturnBehaviour OnReturn { get; set; } = ReturnBehaviour.Default;
    public bool Hidden { get; set; }
    public bool Pinned { get; set; }

    public bool IsWeb => Kind == TileKind.Web;

    public Tile Clone() => (Tile)MemberwiseClone();
}

/// <summary>Home tiles: apps, games, web pages. Order is the list order and the user
/// moves tiles with the controller (hold A).</summary>
public sealed class TileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly List<Tile> _tiles;

    public event Action? Changed;
    public string? LoadNotice { get; private set; }
    public bool Persist { get; set; } = true;

    public TileStore(string path)
    {
        _path = path;
        _tiles = Load();
    }

    public static TileStore InMemory() => new(Path.Combine(Path.GetTempPath(), "lounge-tiles-" + Guid.NewGuid().ToString("N") + ".json")) { Persist = false };

    public IReadOnlyList<Tile> All => _tiles;
    /// <summary>Pinned tiles come first, then the rest in the user's order.</summary>
    public IEnumerable<Tile> Web => _tiles.Where(t => t.IsWeb && !t.Hidden).OrderByDescending(t => t.Pinned);
    public IEnumerable<Tile> Apps => _tiles.Where(t => !t.IsWeb && !t.Hidden).OrderByDescending(t => t.Pinned);

    public void SetPinned(string id, bool pinned)
    {
        if (Find(id) is { } t) { t.Pinned = pinned; Save(); }
    }
    public IEnumerable<Tile> HiddenTiles => _tiles.Where(t => t.Hidden);

    private List<Tile> Load()
    {
        if (!File.Exists(_path)) return Defaults();
        try
        {
            var tiles = JsonSerializer.Deserialize<List<Tile>>(File.ReadAllText(_path), Json) ?? Defaults();
            // Saved before 1.0.9: Prime Video was embedded, where Amazon never lets it play.
            foreach (var t in tiles.Where(t => t.IsWeb && t.Target.Contains("primevideo.com", StringComparison.OrdinalIgnoreCase)))
                t.OpenExternally = true;
            return tiles;
        }
        catch (JsonException)
        {
            var moved = AtomicFile.SetAside(_path);
            LoadNotice = "Your home tiles could not be read, so the defaults are back." +
                         (moved is null ? "" : $" The old file was kept as {Path.GetFileName(moved)}.");
            return Defaults();
        }
    }

    public const string TvUserAgent =
        "Mozilla/5.0 (SMART-TV; Linux; Tizen 6.0) AppleWebKit/537.36 (KHTML, like Gecko) 76.0.3809.146/6.0 TV Safari/537.36";

    public static List<Tile> Defaults() =>
    [
        new() { Id = "cogas", Kind = TileKind.Cogas, Name = "Cogas", Color = "#6C4BFF", IsGame = true },
        new() { Id = "steam", Kind = TileKind.Protocol, Name = "Steam Big Picture", Target = "steam://open/bigpicture", Color = "#1B2838", IsGame = true },
        new() { Id = "youtube", Kind = TileKind.Web, Name = "YouTube", Target = "https://www.youtube.com/tv", UserAgent = TvUserAgent, Color = "#FF0033" },
        new() { Id = "netflix", Kind = TileKind.Web, Name = "Netflix", Target = "https://www.netflix.com/browse", Color = "#E50914", AllowMouse = true },
        new() { Id = "disney", Kind = TileKind.Web, Name = "Disney+", Target = "https://www.disneyplus.com/", Color = "#113CCF", AllowMouse = true },
        // Amazon plays Prime Video only in full browsers, not embedded ones (the page loads
        // but play stays greyed out), so it opens in Edge with the controller mouse on.
        new() { Id = "prime", Kind = TileKind.Web, Name = "Prime Video", Target = "https://www.primevideo.com/", Color = "#00A8E1", AllowMouse = true, OpenExternally = true },
        new() { Id = "twitch", Kind = TileKind.Web, Name = "Twitch", Target = "https://www.twitch.tv/", Color = "#9146FF", AllowMouse = true },
    ];

    public Tile? Find(string id) => _tiles.FirstOrDefault(t => t.Id == id);

    public void Add(Tile t) { _tiles.Add(t); Save(); }

    public void Update(Tile t)
    {
        var i = _tiles.FindIndex(x => x.Id == t.Id);
        if (i < 0) _tiles.Add(t); else _tiles[i] = t;
        Save();
    }

    public void Remove(string id) { _tiles.RemoveAll(t => t.Id == id); Save(); }

    /// <summary>Moves a tile one place among the visible tiles of its group.</summary>
    public void Move(string id, int direction)
    {
        var tile = Find(id);
        if (tile is null) return;
        var group = _tiles.Where(t => t.IsWeb == tile.IsWeb && !t.Hidden && t.Pinned == tile.Pinned).ToList();
        var gi = group.IndexOf(tile);
        var target = gi + direction;
        if (target < 0 || target >= group.Count) return;
        var other = group[target];
        int a = _tiles.IndexOf(tile), b = _tiles.IndexOf(other);
        (_tiles[a], _tiles[b]) = (_tiles[b], _tiles[a]);
        Save();
    }

    public void SetHidden(string id, bool hidden)
    {
        if (Find(id) is { } t) { t.Hidden = hidden; Save(); }
    }

    public void ResetDefaults()
    {
        _tiles.Clear();
        _tiles.AddRange(Defaults());
        Save();
    }

    private void Save()
    {
        if (Persist) AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_tiles, Json));
        Changed?.Invoke();
    }
}
