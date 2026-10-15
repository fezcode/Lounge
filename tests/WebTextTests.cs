using Lounge.Core.Web;
using Xunit;

namespace Lounge.Tests;

public class WebTextTests
{
    [Fact]
    public void Diagnostic_lines_never_keep_queries_emails_or_tokens()
    {
        var line = "GET https://licence.example.com/widevine/v1?token=abc123&user=42#frag -> 403; signed in as jane.doe@mail.com; session 9f8e7d6c5b4a39281706f5e4d3c2b1a0";
        var safe = WebText.Redact(line);
        Assert.Contains("https://licence.example.com/widevine/v1", safe);
        Assert.DoesNotContain("token=", safe);
        Assert.DoesNotContain("jane.doe", safe);
        Assert.DoesNotContain("9f8e7d6c5b4a39281706f5e4d3c2b1a0", safe);
        Assert.Contains("[email]", safe);
        Assert.Contains("[id]", safe);
    }

    [Theory]
    [InlineData("Console error: invalid password for user")]
    [InlineData("Page says: Enter your one-time code")]
    [InlineData("Console warning: credential store unavailable")]
    [InlineData("Page says: Enter your PIN")]
    [InlineData("passwd field changed")]
    public void Lines_about_passwords_and_codes_are_dropped_whole(string line) =>
        Assert.True(WebText.MentionsSecret(line));

    [Theory]
    [InlineData("DRM access granted: com.widevine.alpha")]
    [InlineData("Page says: Your browser isn't supported")]
    [InlineData("Request GET https://www.primevideo.com/detail -> 403")]
    public void Ordinary_diagnostic_lines_are_kept(string line) =>
        Assert.False(WebText.MentionsSecret(line));

    [Fact]
    public void Ordinary_paths_stay_readable()
    {
        var s = WebText.Redact("GET https://cdn.bitmovin.com/content/assets/art-of-motion/manifest.mpd -> 200");
        Assert.Equal("GET https://cdn.bitmovin.com/content/assets/art-of-motion/manifest.mpd -> 200", s);
        Assert.Equal("https://www.primevideo.com/detail/0ABC", WebText.Address("https://www.primevideo.com/detail/0ABC?ref=xyz&token=1"));
    }

    [Fact]
    public void The_WebView2_brand_is_dropped_from_the_client_hint_header()
    {
        var header = "\"Microsoft Edge WebView2\";v=\"155\", \"Not(A:Brand\";v=\"24\", \"Microsoft Edge\";v=\"155\", \"Chromium\";v=\"155\"";
        Assert.Equal("\"Not(A:Brand\";v=\"24\", \"Microsoft Edge\";v=\"155\", \"Chromium\";v=\"155\"", WebText.StripWebViewBrand(header));
    }

    [Fact]
    public void As_Chrome_the_brand_list_and_user_agent_name_Chrome()
    {
        var header = "\"Microsoft Edge WebView2\";v=\"155\", \"Not(A:Brand\";v=\"24\", \"Microsoft Edge\";v=\"155\", \"Chromium\";v=\"155\"";
        Assert.Equal("\"Not(A:Brand\";v=\"24\", \"Google Chrome\";v=\"155\", \"Chromium\";v=\"155\"", WebText.RewriteBrands(header, "chrome"));
        Assert.Equal(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36",
            WebText.ChromeUserAgent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36 Edg/155.0.0.0"));
    }

    [Fact]
    public void Site_icon_sizes_and_formats_are_read_from_their_headers()
    {
        Assert.Equal(512, WebText.LargestIconSize("16x16 192x192 512x512"));
        Assert.Equal(0, WebText.LargestIconSize(null));
        var png = new byte[32];
        new byte[] { 0x89, 0x50, 0x4E, 0x47 }.CopyTo(png, 0);
        png[18] = 0x01; png[19] = 0x00;                       // width 256
        Assert.True(WebText.IsRasterImage(png));
        Assert.Equal(256, WebText.PixelSize(png));
        var ico = new byte[40];
        ico[2] = 1; ico[4] = 2; ico[6] = 16; ico[22] = 48;     // two images: 16 and 48 px
        Assert.True(WebText.IsRasterImage(ico));
        Assert.Equal(48, WebText.PixelSize(ico));
        Assert.False(WebText.IsRasterImage(System.Text.Encoding.UTF8.GetBytes("<!doctype html><svg></svg>")));
    }
}