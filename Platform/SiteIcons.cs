using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lounge.Core.Web;

namespace Lounge.Platform;

/// <summary>The icon a website publishes for itself (its web-app manifest icon, its
/// touch icon, or its largest declared icon), fetched once and cached, so web tiles
/// show the service's own logo instead of a monogram. Lounge only talks to the site the
/// tile points at, the way a browser fetches a bookmark's icon, and sends nothing else.</summary>
public static partial class SiteIcons
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0" },
            { "Accept-Language", "en" },
        },
    };

    private static readonly HashSet<string> InFlight = new();

    /// <summary>Raised (on a worker thread) when an icon arrives.</summary>
    public static event Action? Updated;

    /// <summary>The cache file for a site, whether or not it exists yet.</summary>
    public static string CacheFile(string cacheDir, Uri site) =>
        Path.Combine(cacheDir, "site-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(site.Host))).ToLowerInvariant()[..16] + ".img");

    /// <summary>The cached icon for <paramref name="url"/>, or null while it is fetched in the background.</summary>
    public static string? Get(string url, string cacheDir)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var site) || site.Scheme is not ("http" or "https")) return null;
        var file = CacheFile(cacheDir, site);
        if (File.Exists(file)) return new FileInfo(file).Length > 0 ? file : null;   // empty = the site has none
        lock (InFlight)
            if (!InFlight.Add(site.Host)) return null;
        _ = Task.Run(async () =>
        {
            try
            {
                var (reached, bytes) = await Fetch(new Uri(site.GetLeftPart(UriPartial.Authority) + "/"));
                // A site that couldn't be reached is asked again next time; only one that
                // answered without a usable icon is remembered as having none (empty file).
                if (!reached) return;
                // Written aside and moved into place, so a tile never decodes a half-written icon.
                var part = file + ".part";
                await File.WriteAllBytesAsync(part, bytes ?? []);
                File.Move(part, file, overwrite: true);
                if (bytes is not null) Updated?.Invoke();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UriFormatException) { }
            finally { lock (InFlight) InFlight.Remove(site.Host); }
        });
        return null;
    }

    /// <summary>The site's best icon. <c>Reached</c> is false when the site didn't answer at
    /// all (offline, a timeout), so the attempt isn't remembered as "no icon".</summary>
    private static async Task<(bool Reached, byte[]? Icon)> Fetch(Uri home)
    {
        var candidates = new List<(Uri Url, int Size)>();
        string html = "";
        var reached = false;
        try { html = await Http.GetStringAsync(home); reached = true; }
        catch (HttpRequestException e) { reached = e.StatusCode is not null; }   // an HTTP error is still an answer
        catch (TaskCanceledException) { }

        foreach (Match link in LinkTag().Matches(html))
        {
            var tag = link.Value;
            var rel = Attr(tag, "rel")?.ToLowerInvariant() ?? "";
            var href = Attr(tag, "href");
            if (href is null || !Uri.TryCreate(home, System.Net.WebUtility.HtmlDecode(href), out var u)) continue;
            if (rel.Contains("manifest"))
                candidates.AddRange(await FromManifest(u));
            else if (rel.Contains("apple-touch-icon"))
                candidates.Add((u, Math.Max(180, Largest(Attr(tag, "sizes")))));
            else if (rel.Split(' ').Contains("icon"))
                candidates.Add((u, Largest(Attr(tag, "sizes"))));
        }
        candidates.Add((new Uri(home, "/apple-touch-icon.png"), 180));

        foreach (var (url, _) in candidates.Where(c => c.Size >= 96 || c.Size == 0).OrderByDescending(c => c.Size).DistinctBy(c => c.Url))
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url);
                reached = true;
                // Small icons look smudgy on a tile: below 96 px the monogram is better.
                if (IsRasterImage(bytes) && (PixelSize(bytes) is not { } px || px >= 96)) return (true, bytes);
            }
            catch (HttpRequestException e) { reached |= e.StatusCode is not null; }
            catch (TaskCanceledException) { }
        }
        return (reached, null);
    }

    private static async Task<IEnumerable<(Uri, int)>> FromManifest(Uri manifest)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(manifest));
            if (!doc.RootElement.TryGetProperty("icons", out var icons) || icons.ValueKind != JsonValueKind.Array) return [];
            var list = new List<(Uri, int)>();
            foreach (var icon in icons.EnumerateArray())
            {
                var src = icon.TryGetProperty("src", out var s) ? s.GetString() : null;
                var type = icon.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (src is null || type.Contains("svg") || src.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
                var purpose = icon.TryGetProperty("purpose", out var p) ? p.GetString() ?? "" : "";
                if (purpose == "monochrome") continue;
                if (Uri.TryCreate(manifest, src, out var u))
                    list.Add((u, Largest(icon.TryGetProperty("sizes", out var z) ? z.GetString() : null)));
            }
            return list;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException) { return []; }
    }

    private static int Largest(string? sizes) => WebText.LargestIconSize(sizes);
    private static bool IsRasterImage(byte[] b) => WebText.IsRasterImage(b);
    private static int? PixelSize(byte[] b) => WebText.PixelSize(b);

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $"""\b{name}\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))""", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : null;
    }

    [GeneratedRegex("<link\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTag();

}
