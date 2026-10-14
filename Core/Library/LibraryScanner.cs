using System.Security.Cryptography;
using System.Text;
using Lounge.Core.Media;

namespace Lounge.Core.Library;

public readonly record struct ScanProgress(LibraryKind Kind, int Done, int Total, string Current);

public sealed record ScanSummary(int Added, int Updated, int Removed, int Failed, IReadOnlyList<string> MissingRoots);

/// <summary>Walks the library folders and keeps <see cref="LibraryDb"/> in step:
/// new or changed files (by mtime and size) are probed, vanished ones dropped. Art
/// comes from embedded covers or images beside the file; nothing is fetched online.</summary>
public sealed class LibraryScanner
{
    private readonly LibraryDb _db;
    private readonly Func<string, ProbeResult?> _probe;
    private readonly string _thumbs;
    private readonly Func<long> _clock;

    private static readonly string[] FolderArt = ["poster", "folder", "cover", "front", "album"];
    private static readonly string[] ImageExt = [".jpg", ".jpeg", ".png", ".webp"];

    public LibraryScanner(LibraryDb db, Func<string, ProbeResult?> probe, string thumbsDir, Func<long>? clock = null)
    {
        _db = db;
        _probe = probe;
        _thumbs = thumbsDir;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public ScanSummary Scan(LibraryKind kind, IReadOnlyList<string> roots, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var want = kind == LibraryKind.Video ? MediaKind.Video : MediaKind.Audio;
        var known = _db.Fingerprints(kind);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var files = new List<(string Root, FileInfo File)>();

        _db.KeepRoots(kind, roots);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) { missing.Add(root); continue; }
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts))
                if (MediaKinds.Of(f.Name) == want) files.Add((root, f));
        }

        int added = 0, updated = 0, failed = 0, done = 0;
        foreach (var (root, f) in files)
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(f.FullName);
            var mtime = f.LastWriteTimeUtc.Ticks;
            var isKnown = known.TryGetValue(f.FullName, out var fp);
            if (isKnown && fp.Mtime == mtime && fp.Size == f.Length) { done++; continue; }
            progress?.Report(new ScanProgress(kind, done, files.Count, f.Name));

            ProbeResult? p = null;
            try { p = _probe(f.FullName); } catch (Exception) { p = null; }
            if (p is null) failed++;
            p ??= new ProbeResult(0);

            var art = FindArt(f, kind) ?? SaveEmbedded(f.FullName, p.EmbeddedArt);
            var title = kind == LibraryKind.Video
                ? CleanTitle(Path.GetFileNameWithoutExtension(f.Name))
                : (string.IsNullOrWhiteSpace(p.Title) ? Path.GetFileNameWithoutExtension(f.Name) : p.Title!.Trim());
            var item = new LibraryItem(f.FullName, kind, root, f.DirectoryName ?? root, title,
                (p.Artist ?? "").Trim(), (p.Album ?? "").Trim(), (p.AlbumArtist ?? "").Trim(),
                p.Track, p.Year, p.Duration, p.Width, p.Height, art, _clock());
            _db.Upsert(item, mtime, f.Length);
            if (isKnown) updated++; else added++;
            done++;
        }

        var gone = known.Keys.Where(k => !seen.Contains(k) &&
            !missing.Any(m => k.StartsWith(m.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
        _db.Remove(gone);
        progress?.Report(new ScanProgress(kind, files.Count, files.Count, ""));
        return new ScanSummary(added, updated, gone.Count, failed, missing);
    }

    /// <summary>Makes "The.Expanse.S01E02.1080p.WEB" read "The Expanse S01E02".</summary>
    public static string CleanTitle(string stem)
    {
        var s = stem.Replace('.', ' ').Replace('_', ' ');
        var cut = System.Text.RegularExpressions.Regex.Match(s,
            @"\s(?:(?:19|20)\d{2}\s|\b(?:2160p|1080p|720p|480p|4k|uhd|web[- ]?dl|webrip|bluray|brrip|hdtv|x264|x265|h264|h265|hevc|aac|dts|remux)\b)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (cut.Success && cut.Index > 0)
        {
            var year = System.Text.RegularExpressions.Regex.Match(cut.Value, @"(19|20)\d{2}");
            s = s[..cut.Index] + (year.Success && cut.Value.Trim() == year.Value ? $" ({year.Value})" : "");
        }
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    private static string? FindArt(FileInfo f, LibraryKind kind)
    {
        var dir = f.Directory;
        if (dir is null) return null;
        var stem = Path.GetFileNameWithoutExtension(f.Name);
        foreach (var ext in ImageExt)
        {
            var own = Path.Combine(dir.FullName, stem + ext);
            if (File.Exists(own)) return own;
            if (kind == LibraryKind.Video)
            {
                var poster = Path.Combine(dir.FullName, stem + "-poster" + ext);
                if (File.Exists(poster)) return poster;
            }
        }
        foreach (var name in FolderArt)
            foreach (var ext in ImageExt)
            {
                var p = Path.Combine(dir.FullName, name + ext);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    private string? SaveEmbedded(string path, byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        Directory.CreateDirectory(_thumbs);
        var file = Path.Combine(_thumbs, "art-" + Hash(path) + (bytes.Length > 3 && bytes[0] == 0x89 ? ".png" : ".jpg"));
        File.WriteAllBytes(file, bytes);
        return file;
    }

    public static string Hash(string text) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant())))[..16].ToLowerInvariant();
}
