using System.Text.Json;
using Lounge.Core;
using Lounge.Core.Web;
using Lounge.Shell;
using Microsoft.Web.WebView2.Core;

namespace Lounge.Screens;

/// <summary>What the web view's Extensions menu offers: AllOff, built in, and any unpacked
/// browser extension in %APPDATA%\fezcode\Lounge\extensions. The embedded browser has no
/// toolbar, so extensions that work inside pages (blockers, tweaks) work here; ones that
/// only act from their toolbar popup don't.</summary>
public static class WebExtensions
{
    /// <summary>Decided once per run: every web view shares one browser, and its options
    /// can't change while it lives. A folder added later loads from the next start.</summary>
    public static bool Enabled => _enabled.Value;
    private static readonly Lazy<bool> _enabled = new(() => Folders().Any());

    /// <summary>Extension folders (each with a manifest.json) waiting in the extensions folder.</summary>
    public static IEnumerable<string> Folders()
    {
        try { return Directory.EnumerateDirectories(AppPaths.Extensions).Where(d => File.Exists(Path.Combine(d, "manifest.json"))).ToList(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    // ---------------------------------------------------------------- AllOff

    private static readonly Lazy<(string? Function, string? Version)> _allOff = new(LoadAllOff);

    public static bool HasAllOff => _allOff.Value.Function is not null;
    public static string? AllOffVersion => _allOff.Value.Version;

    private static (string?, string?) LoadAllOff()
    {
        var asm = typeof(WebExtensions).Assembly;
        string? Read(string name)
        {
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) return null;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        var script = Read("AllOff/popup.js");
        var function = script is null ? null : PageScripts.ExtractFunction(script, PageScripts.AllOffFunction);
        string? version = null;
        try
        {
            if (Read("AllOff/manifest.json") is { } manifest)
                version = JsonDocument.Parse(manifest).RootElement.GetProperty("version").GetString();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
        return (function, version);
    }

    /// <summary>Runs AllOff in the page and in every frame it holds (consent dialogs often
    /// live in a frame from another site), and says what happened.</summary>
    public static async Task<string> RunAllOff(CoreWebView2 web, IEnumerable<CoreWebView2Frame> frames)
    {
        if (_allOff.Value.Function is not { } function) return "AllOff isn't part of this build.";
        var expression = PageScripts.Invoke(function);
        var targets = new List<Func<string, Task<string>>> { web.ExecuteScriptAsync };
        targets.AddRange(frames.Select(f => (Func<string, Task<string>>)f.ExecuteScriptAsync));
        var results = await Task.WhenAll(targets.Select(async exec =>
        {
            try { return await WebScreen.EvalPromise(exec, expression, 8000); }
            catch (Exception) { return ""; }   // a frame that went away meanwhile
        }));
        int toggled = 0, remaining = 0, reached = 0;
        foreach (var r in results)
        {
            try
            {
                using var doc = JsonDocument.Parse(r);
                if (!doc.RootElement.TryGetProperty("toggled", out var t)) continue;
                reached++;
                toggled += t.GetInt32();
                if (doc.RootElement.TryGetProperty("remaining", out var left)) remaining += left.GetInt32();
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
        }
        return PageScripts.AllOffSummary(toggled, remaining, reached);
    }

    // ---------------------------------------------------------------- browser extensions

    /// <summary>Adds the extension folders the browser profile doesn't have yet. The
    /// profile remembers added extensions (and whether each is on) between runs.</summary>
    public static async Task Sync(CoreWebView2Profile profile)
    {
        if (!Enabled) return;
        var known = (await profile.GetBrowserExtensionsAsync()).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Folders())
        {
            if (ManifestName(dir) is { } name && known.Contains(name)) continue;
            try { await profile.AddBrowserExtensionAsync(dir); }
            catch (Exception e) { Log.Write($"extension {Path.GetFileName(dir)} not loaded: {e.Message}"); }
        }
    }

    /// <summary>The profile's extensions that came from the extensions folder (the browser
    /// also lists its own built-in ones, such as the PDF viewer).</summary>
    public static async Task<List<CoreWebView2BrowserExtension>> Ours(CoreWebView2Profile profile)
    {
        var names = Folders().Select(ManifestName).Where(n => n is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (await profile.GetBrowserExtensionsAsync()).Where(e => names.Contains(e.Name)).ToList();
    }

    private static string? ManifestName(string dir)
    {
        try { return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))).RootElement.GetProperty("name").GetString(); }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
}
