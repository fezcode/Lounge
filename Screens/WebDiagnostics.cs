using System.Text.RegularExpressions;
using Lounge.Core;
using Lounge.Core.Web;
using Lounge.Shell;
using Microsoft.Web.WebView2.Core;

namespace Lounge.Screens;

/// <summary>"Record playback diagnostics" (Settings > Web): what a site's video player
/// reports while a page plays inside Lounge, so a site that refuses to play can be
/// explained. Written to logs\web-diagnostics-*.txt.
///
/// Privacy: never passwords, form contents, cookies, request bodies or headers. Addresses
/// are cut to host and path, and anything that looks like an ID, token or email is
/// masked before it is written.</summary>
public sealed partial class WebDiagnostics
{
    private readonly string _file;
    private readonly object _gate = new();
    private int _lines;

    private WebDiagnostics(string file) => _file = file;

    /// <summary>Runs in every page before its own scripts: watches copy protection (EME),
    /// video errors, console errors, and messages the page shows about support.</summary>
    private const string PageScript = """
        (() => {
          if (window.__loungeDiag) return;
          Object.defineProperty(window, '__loungeDiag', { value: true, enumerable: false });
          // Nothing is reported while a password field has focus.
          const typingSecret = () => { const a = document.activeElement; return !!a && a.tagName === 'INPUT' && a.type === 'password'; };
          const send = m => {
            if (typingSecret()) return;
            let q = window.__loungeQ;
            if (!q) { q = []; Object.defineProperty(window, '__loungeQ', { value: q, enumerable: false }); }
            if (q.length < 500) q.push(JSON.stringify({ kind: 'diag', line: String(m).slice(0, 400) }));
          };
          const access = navigator.requestMediaKeySystemAccess && navigator.requestMediaKeySystemAccess.bind(navigator);
          if (access) navigator.requestMediaKeySystemAccess = async (ks, cfgs) => {
            const want = (cfgs || []).flatMap(c => (c.videoCapabilities || []).map(v => (v.robustness || '-') + ' ' + (v.contentType || ''))).slice(0, 6).join('; ');
            try { const a = await access(ks, cfgs); send('DRM access granted: ' + ks + ' [' + want + ']'); return a; }
            catch (e) { send('DRM access REFUSED: ' + ks + ' [' + want + '] ' + e.name + ': ' + e.message); throw e; }
          };
          if (window.MediaKeys) {
            const create = MediaKeys.prototype.createSession;
            MediaKeys.prototype.createSession = function () {
              const s = create.apply(this, arguments);
              s.addEventListener('message', ev => send('DRM session message: ' + ev.messageType));
              s.addEventListener('keystatuseschange', () => { const st = []; s.keyStatuses.forEach(v => st.push(v)); send('DRM key status: ' + st.join(', ')); });
              const gen = s.generateRequest.bind(s);
              s.generateRequest = (t, d) => gen(t, d).then(r => { send('DRM licence request made (' + t + ')'); return r; }, e => { send('DRM licence request FAILED: ' + e.name + ': ' + e.message); throw e; });
              const up = s.update.bind(s);
              s.update = r => up(r).then(x => { send('DRM licence accepted'); return x; }, e => { send('DRM licence REJECTED: ' + e.name + ': ' + e.message); throw e; });
              return s;
            };
            // Sites check the display link's protection (HDCP) before enabling play.
            const policy = MediaKeys.prototype.getStatusForPolicy;
            if (policy) MediaKeys.prototype.getStatusForPolicy = function (p) {
              const want = p && p.minHdcpVersion ? 'HDCP ' + p.minHdcpVersion : JSON.stringify(p);
              return policy.call(this, p).then(s => { send('Display protection check (' + want + '): ' + s); return s; },
                                               e => { send('Display protection check (' + want + ') FAILED: ' + e.name + ': ' + e.message); throw e; });
            };
            const setKeys = HTMLMediaElement.prototype.setMediaKeys;
            HTMLMediaElement.prototype.setMediaKeys = function (k) {
              return setKeys.call(this, k).then(r => { send('DRM attached to the video'); return r; }, e => { send('DRM attach FAILED: ' + e.name + ': ' + e.message); throw e; });
            };
          }
          document.addEventListener('error', e => { const t = e.target; if (t instanceof HTMLMediaElement && t.error) send('Video error ' + t.error.code + ': ' + (t.error.message || '')); }, true);
          for (const ev of ['waitingforkey', 'encrypted', 'stalled']) document.addEventListener(ev, e => { if (e.target instanceof HTMLMediaElement) send('Video event: ' + ev); }, true);
          const err = console.error;
          console.error = function () { send('Console error: ' + Array.from(arguments).map(String).join(' ')); return err.apply(console, arguments); };
          const warn = console.warn;
          console.warn = function () { send('Console warning: ' + Array.from(arguments).map(String).join(' ')); return warn.apply(console, arguments); };
          // Where an error came from: the first frames of its stack (script names and lines).
          const where = err => err && err.stack ? ' at ' + String(err.stack).split('\n').slice(1, 4).map(s => s.trim()).join(' < ') : '';
          addEventListener('error', e => send('Page error: ' + e.message + where(e.error)));
          addEventListener('unhandledrejection', e => send('Unhandled rejection: ' + (e.reason && (e.reason.message || e.reason)) + where(e.reason)));
          // What the page tells the viewer about support, and play controls it disabled.
          const seen = new Set();
          setInterval(() => {
            if (!document.body) return;
            const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
            let n, count = 0;
            while ((n = walker.nextNode()) && count++ < 5000) {
              const s = n.nodeValue.trim();
              if (s.length < 12 || s.length > 300 || seen.has(s)) continue;
              if (/not supported|unsupported|isn.t supported|browser|device|HDCP|display|DRM|error code|can.t play|cannot play|unavailable|try again/i.test(s)) {
                seen.add(s);
                const el = n.parentElement;
                if (el && el.offsetParent !== null) send('Page says: ' + s);
              }
            }
            document.querySelectorAll('button[disabled], [aria-disabled="true"]').forEach(b => {
              const s = (b.innerText || b.getAttribute('aria-label') || '').trim().slice(0, 80);
              if (s && /play|watch|resume|start/i.test(s) && !seen.has('btn:' + s)) { seen.add('btn:' + s); send('Disabled control: "' + s + '"' + (b.title ? ' (' + b.title + ')' : '')); }
            });
          }, 3000);
        })();
        """;

    /// <summary>Starts recording for this page, or does nothing when the setting is off.</summary>
    public static async Task<WebDiagnostics?> Start(CoreWebView2 web, string site)
    {
        var safe = string.Concat(site.Where(char.IsLetterOrDigit));
        var d = new WebDiagnostics(Path.Combine(AppPaths.Logs, $"web-diagnostics-{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.txt"));
        d.Write($"Lounge {Product.Version}, page '{site}'. Passwords, forms, cookies and full addresses are never recorded.");
        await web.AddScriptToExecuteOnDocumentCreatedAsync(PageScript);
        web.WebResourceResponseReceived += (_, a) =>
        {
            var status = a.Response.StatusCode;
            var uri = a.Request.Uri;
            var drm = DrmRequest().IsMatch(uri);
            if (status >= 400 || drm)
                d.Write($"Request {(drm ? "(licence/playback) " : "")}{a.Request.Method} {Address(uri)} -> {status} {a.Response.ReasonPhrase}");
        };
        web.NavigationCompleted += (_, a) => d.Write($"Page loaded: {Address(web.Source)} ({(a.IsSuccess ? "ok" : a.WebErrorStatus.ToString())})");
        return d;
    }

    /// <summary>Once per page: the browser's identity and every copy-protection level it offers.</summary>
    public async Task Describe(CoreWebView2 web)
    {
        try
        {
            Write("Browser: " + System.Text.Json.JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("navigator.userAgent")));
            foreach (var line in (await WebScreen.EvalPromise(web, WebScreen.DrmMatrixProbe, 15000)).Split(" | "))
                Write("Supports " + line);
        }
        catch (Exception e) { Write("Could not describe the browser: " + e.Message); }
    }

    public void Write(string line)
    {
        if (WebText.MentionsSecret(line)) return;   // never a line about passwords, codes or credentials
        lock (_gate)
        {
            if (_lines++ > 5000) return;   // a runaway page can't fill the disk
            try { File.AppendAllText(_file, $"{DateTime.Now:HH:mm:ss.fff}  {Redact(line)}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }

    private static string Address(string uri) => WebText.Address(uri);
    private static string Redact(string s) => WebText.Redact(s);

    [GeneratedRegex(@"licen[cs]e|widevine|playready|/drm|playback|manifest|\.mpd|\.m3u8", RegexOptions.IgnoreCase)]
    private static partial Regex DrmRequest();
}
