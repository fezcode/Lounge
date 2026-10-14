using Microsoft.Data.Sqlite;

namespace Lounge.Core.Library;

public enum LibraryKind { Video = 1, Music = 2 }

/// <summary>What a probe learned about one file. Everything but the duration may be missing.</summary>
public sealed record ProbeResult(
    double Duration,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? AlbumArtist = null,
    int Track = 0,
    int Year = 0,
    int Width = 0,
    int Height = 0,
    byte[]? EmbeddedArt = null);

public sealed record LibraryItem(
    string Path, LibraryKind Kind, string Root, string Folder, string Title,
    string Artist, string Album, string AlbumArtist, int Track, int Year,
    double Duration, int Width, int Height, string? Art, long Added)
{
    public string FolderName => System.IO.Path.GetFileName(Folder.TrimEnd('\\')) is { Length: > 0 } n ? n : Folder;
}

public sealed record Album(string Name, string Artist, int Year, string? Art, int Tracks);

/// <summary>Collection of videos that share a folder: a show season, a film folder.</summary>
public sealed record VideoCollection(string Folder, string Name, string? Art, int Count, long Added);

/// <summary>A rebuildable cache of what the scanner found. Safe to delete.</summary>
public sealed class LibraryDb : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public LibraryDb(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS items(
              path TEXT PRIMARY KEY COLLATE NOCASE,
              kind INTEGER NOT NULL,
              root TEXT NOT NULL,
              folder TEXT NOT NULL,
              title TEXT NOT NULL,
              artist TEXT NOT NULL DEFAULT '',
              album TEXT NOT NULL DEFAULT '',
              album_artist TEXT NOT NULL DEFAULT '',
              track INTEGER NOT NULL DEFAULT 0,
              year INTEGER NOT NULL DEFAULT 0,
              duration REAL NOT NULL DEFAULT 0,
              width INTEGER NOT NULL DEFAULT 0,
              height INTEGER NOT NULL DEFAULT 0,
              art TEXT,
              mtime INTEGER NOT NULL,
              size INTEGER NOT NULL,
              added INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS items_kind ON items(kind);
            CREATE INDEX IF NOT EXISTS items_folder ON items(folder);
            """);
    }

    public static LibraryDb InMemory() => new(":memory:");

    private void Exec(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    public Dictionary<string, (long Mtime, long Size)> Fingerprints(LibraryKind kind)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT path, mtime, size FROM items WHERE kind=$k";
            c.Parameters.AddWithValue("$k", (int)kind);
            using var r = c.ExecuteReader();
            var d = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
            while (r.Read()) d[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
            return d;
        }
    }

    public void Upsert(LibraryItem item, long mtime, long size)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = """
                INSERT INTO items(path,kind,root,folder,title,artist,album,album_artist,track,year,duration,width,height,art,mtime,size,added)
                VALUES($p,$k,$r,$f,$t,$ar,$al,$aa,$tr,$y,$d,$w,$h,$art,$m,$s,$add)
                ON CONFLICT(path) DO UPDATE SET kind=$k,root=$r,folder=$f,title=$t,artist=$ar,album=$al,album_artist=$aa,
                  track=$tr,year=$y,duration=$d,width=$w,height=$h,art=COALESCE($art,art),mtime=$m,size=$s
                """;
            c.Parameters.AddWithValue("$p", item.Path);
            c.Parameters.AddWithValue("$k", (int)item.Kind);
            c.Parameters.AddWithValue("$r", item.Root);
            c.Parameters.AddWithValue("$f", item.Folder);
            c.Parameters.AddWithValue("$t", item.Title);
            c.Parameters.AddWithValue("$ar", item.Artist);
            c.Parameters.AddWithValue("$al", item.Album);
            c.Parameters.AddWithValue("$aa", item.AlbumArtist);
            c.Parameters.AddWithValue("$tr", item.Track);
            c.Parameters.AddWithValue("$y", item.Year);
            c.Parameters.AddWithValue("$d", item.Duration);
            c.Parameters.AddWithValue("$w", item.Width);
            c.Parameters.AddWithValue("$h", item.Height);
            c.Parameters.AddWithValue("$art", (object?)item.Art ?? DBNull.Value);
            c.Parameters.AddWithValue("$m", mtime);
            c.Parameters.AddWithValue("$s", size);
            c.Parameters.AddWithValue("$add", item.Added);
            c.ExecuteNonQuery();
        }
    }

    public void SetArt(string path, string art)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE items SET art=$a WHERE path=$p";
            c.Parameters.AddWithValue("$a", art);
            c.Parameters.AddWithValue("$p", path);
            c.ExecuteNonQuery();
        }
    }

    public void Remove(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "DELETE FROM items WHERE path=$p";
            var p = c.Parameters.Add("$p", SqliteType.Text);
            foreach (var path in paths) { p.Value = path; c.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    /// <summary>Drops everything under roots that are no longer in the library.</summary>
    public void KeepRoots(LibraryKind kind, IReadOnlyCollection<string> roots)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT DISTINCT root FROM items WHERE kind=$k";
            c.Parameters.AddWithValue("$k", (int)kind);
            var stale = new List<string>();
            using (var r = c.ExecuteReader())
                while (r.Read())
                {
                    var root = r.GetString(0);
                    if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase)) stale.Add(root);
                }
            foreach (var root in stale)
            {
                using var d = _db.CreateCommand();
                d.CommandText = "DELETE FROM items WHERE kind=$k AND root=$r";
                d.Parameters.AddWithValue("$k", (int)kind);
                d.Parameters.AddWithValue("$r", root);
                d.ExecuteNonQuery();
            }
        }
    }

    private List<LibraryItem> Query(string where, Action<SqliteCommand>? bind = null, string order = "folder, title")
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = $"SELECT path,kind,root,folder,title,artist,album,album_artist,track,year,duration,width,height,art,added FROM items WHERE {where} ORDER BY {order}";
            bind?.Invoke(c);
            using var r = c.ExecuteReader();
            var list = new List<LibraryItem>();
            while (r.Read())
                list.Add(new LibraryItem(r.GetString(0), (LibraryKind)r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt32(8), r.GetInt32(9), r.GetDouble(10),
                    r.GetInt32(11), r.GetInt32(12), r.IsDBNull(13) ? null : r.GetString(13), r.GetInt64(14)));
            return list;
        }
    }

    public int Count(LibraryKind kind)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM items WHERE kind=$k";
            c.Parameters.AddWithValue("$k", (int)kind);
            return Convert.ToInt32(c.ExecuteScalar());
        }
    }

    public LibraryItem? Get(string path) => Query("path=$p", c => c.Parameters.AddWithValue("$p", path)).FirstOrDefault();

    public List<LibraryItem> All(LibraryKind kind) =>
        Query("kind=$k", c => c.Parameters.AddWithValue("$k", (int)kind));

    public List<LibraryItem> RecentlyAdded(LibraryKind kind, int max) =>
        Query("kind=$k", c => c.Parameters.AddWithValue("$k", (int)kind), $"added DESC, title LIMIT {max}");

    public List<LibraryItem> InFolder(string folder) =>
        Query("folder=$f", c => c.Parameters.AddWithValue("$f", folder));

    public List<VideoCollection> VideoCollections()
    {
        var items = All(LibraryKind.Video);
        return items.GroupBy(i => i.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g => new VideoCollection(g.Key, g.First().FolderName,
                g.Select(i => i.Art).FirstOrDefault(a => a is not null), g.Count(), g.Max(i => i.Added)))
            .OrderBy(c => c.Name, Media.NaturalComparer.Instance)
            .ToList();
    }

    public List<Album> Albums()
    {
        var items = All(LibraryKind.Music);
        return items.GroupBy(i => (Album: i.Album.Length > 0 ? i.Album : i.FolderName, Artist: i.AlbumArtist.Length > 0 ? i.AlbumArtist : i.Artist))
            .Select(g => new Album(g.Key.Album, g.Key.Artist, g.Max(i => i.Year),
                g.Select(i => i.Art).FirstOrDefault(a => a is not null), g.Count()))
            .OrderBy(a => a.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Year).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<LibraryItem> AlbumTracks(Album album) =>
        All(LibraryKind.Music)
            .Where(i => (i.Album.Length > 0 ? i.Album : i.FolderName) == album.Name &&
                        (i.AlbumArtist.Length > 0 ? i.AlbumArtist : i.Artist) == album.Artist)
            .OrderBy(i => i.Track == 0 ? int.MaxValue : i.Track)
            .ThenBy(i => System.IO.Path.GetFileName(i.Path), Media.NaturalComparer.Instance)
            .ToList();

    public List<string> Artists() =>
        All(LibraryKind.Music).Select(i => i.AlbumArtist.Length > 0 ? i.AlbumArtist : i.Artist)
            .Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();

    public List<LibraryItem> Search(string text, int max = 60)
    {
        var like = "%" + text.Replace("%", "").Replace("_", "") + "%";
        return Query("title LIKE $q OR artist LIKE $q OR album LIKE $q OR folder LIKE $q",
            c => c.Parameters.AddWithValue("$q", like), $"kind, title LIMIT {max}");
    }

    public void Dispose()
    {
        _db.Dispose();
    }
}
