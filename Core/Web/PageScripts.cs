namespace Lounge.Core.Web;

/// <summary>Scripts Lounge runs in web pages on request. AllOff (bundled as a git subtree in
/// extensions/AllOff) works from a toolbar popup, which the embedded browser has no way to
/// show, so Lounge runs AllOff's own in-page function, taken from its popup.js at build time.</summary>
public static class PageScripts
{
    /// <summary>The function in AllOff's popup/popup.js that switches the toggles off. It
    /// runs inside the page and resolves to { toggled, remaining }.</summary>
    public const string AllOffFunction = "disableConsentToggles";

    /// <summary>The source of a top-level <c>function name(...) { ... }</c> (async or not)
    /// in a script, or null. Braces inside strings, template literals and comments are
    /// skipped so they don't end the function early.</summary>
    public static string? ExtractFunction(string source, string name)
    {
        var start = -1;
        foreach (var head in new[] { $"async function {name}(", $"function {name}(" })
        {
            start = source.IndexOf(head, StringComparison.Ordinal);
            if (start >= 0) break;
        }
        if (start < 0) return null;
        var open = source.IndexOf('{', source.IndexOf(')', start));
        if (open < 0) return null;
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            var c = source[i];
            if (c is '"' or '\'' or '`')
            {
                // Skip the string, honouring escapes.
                for (i++; i < source.Length && source[i] != c; i++) if (source[i] == '\\') i++;
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/') { i = source.IndexOf('\n', i); if (i < 0) return null; continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*') { i = source.IndexOf("*/", i + 2, StringComparison.Ordinal); if (i < 0) return null; i++; continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return source[start..(i + 1)];
        }
        return null;
    }

    /// <summary>An expression that runs the function and reports its result as a short
    /// JSON string: {"toggled":n,"remaining":n}, or {"error":"..."}.</summary>
    public static string Invoke(string function) =>
        $"({function})().then(r => JSON.stringify(r || {{}}), e => JSON.stringify({{ error: String(e && e.message || e) }}))";

    /// <summary>The toast after AllOff ran across a page's frames.</summary>
    public static string AllOffSummary(int toggled, int remaining, int frames) =>
        frames == 0 ? "AllOff couldn't reach this page."
        : remaining > 0 ? $"Switched off {toggled}, but {remaining} still on."
        : toggled > 0 ? $"Switched off {toggled}. All clear. Save your choices on the page if it asks."
        : "Nothing was switched on.";
}
