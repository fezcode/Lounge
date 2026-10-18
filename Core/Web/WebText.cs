using System.Text.RegularExpressions;

namespace Lounge.Core.Web;

/// <summary>Pure text helpers for the web screen: what a diagnostic log may contain, the
/// browser brand list, and the icons sites declare. Kept here so they are tested.</summary>
public static partial class WebText
{
    /// <summary>Host and path only: queries and fragments often carry tokens.</summary>
    public static string Address(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : "(address)";

    /// <summary>Masks what could identify someone or unlock something in a diagnostic line:
    /// the query part of any address, emails, and long ID or token runs.</summary>
    public static string Redact(string s)
    {
        s = UrlQuery().Replace(s, "$1");
        s = Email().Replace(s, "[email]");
        return LongToken().Replace(s, "[id]");
    }

    /// <summary>A diagnostic line that talks about a password, passcode, one-time code,
    /// credential or secret is dropped whole rather than masked.</summary>
    public static bool MentionsSecret(string s) => Secret().IsMatch(s);

    /// <summary>Removes the WebView2 entry from a Sec-CH-UA brand list:
    /// "Microsoft Edge WebView2";v="155", "Chromium";v="155" becomes "Chromium";v="155".</summary>
    public static string StripWebViewBrand(string header) => RewriteBrands(header, "edge");

    /// <summary>A Sec-CH-UA brand list as the chosen browser would send it: "edge" drops the
    /// WebView2 entry; "chrome" also names the browser Google Chrome instead of Microsoft Edge.</summary>
    public static string RewriteBrands(string header, string identity) =>
        string.Join(", ", header.Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0 && !p.Contains("WebView", StringComparison.OrdinalIgnoreCase))
            .Select(p => identity == "chrome" ? p.Replace("\"Microsoft Edge\"", "\"Google Chrome\"") : p));

    /// <summary>What the browser opens for what was typed: an address as given ("https://..."),
    /// a bare one made secure ("bbc.co.uk", "localhost:8080"), anything else searched for.</summary>
    public static string AddressOrSearch(string typed)
    {
        var t = typed.Trim();
        if (t.Length == 0) return "https://www.google.com/";
        if (Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") return u.AbsoluteUri;
        var looksLikeHost = !t.Contains(' ') && (t.Contains('.') || t.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
                            && Uri.TryCreate("https://" + t, UriKind.Absolute, out var bare) && bare.Host.Length > 0
                            && !bare.Host.StartsWith('.') && !bare.Host.EndsWith('.');
        return looksLikeHost ? new Uri("https://" + t).AbsoluteUri : "https://www.google.com/search?q=" + Uri.EscapeDataString(t);
    }

    /// <summary>The user-agent string as Chrome sends it: Edge's is Chrome's plus an "Edg/" token.</summary>
    public static string ChromeUserAgent(string edgeUserAgent) => EdgeToken().Replace(edgeUserAgent, "");

    /// <summary>The largest edge in an icon "sizes" attribute ("16x16 192x192"), 0 when unknown.</summary>
    public static int LargestIconSize(string? sizes)
    {
        var best = 0;
        foreach (Match m in SizeToken().Matches(sizes ?? ""))
            best = Math.Max(best, int.Parse(m.Groups[1].Value));
        return best;
    }

    /// <summary>PNG, JPEG, WebP or ICO by their magic bytes (never SVG or an HTML error page).</summary>
    public static bool IsRasterImage(byte[] b) =>
        b.Length > 12 && (
            (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||
            (b[0] == 0xFF && b[1] == 0xD8) ||
            (b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' && b[8] == (byte)'W' && b[9] == (byte)'E') ||
            (b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0));

    /// <summary>The width of a PNG, or of an ICO's largest image, from the header; null
    /// for formats whose size isn't read here (JPEG, WebP).</summary>
    public static int? PixelSize(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50)
            return (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
        if (b.Length > 6 && b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0)
        {
            int count = b[4] | (b[5] << 8), best = 0;
            for (int i = 0; i < count && 6 + i * 16 < b.Length; i++)
                best = Math.Max(best, b[6 + i * 16] == 0 ? 256 : b[6 + i * 16]);
            return best;
        }
        return null;
    }

    [GeneratedRegex(@"(https?://[^\s?#""']+)[?#][^\s""']*")]
    private static partial Regex UrlQuery();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![A-Za-z0-9_\-])[A-Za-z0-9_\-]{24,}")]
    private static partial Regex LongToken();

    [GeneratedRegex(@"pass(word|wd|code|phrase)|\bpwd\b|\botp\b|one[- ]time|credential|secret|\bpin\b", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();

    [GeneratedRegex(@" Edg[A-Z]?/[\d.]+")]
    private static partial Regex EdgeToken();

    [GeneratedRegex("(\\d+)x\\d+", RegexOptions.IgnoreCase)]
    private static partial Regex SizeToken();
}
