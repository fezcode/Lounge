using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Screen = Lounge.Shell.Screen;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core;
using Lounge.Core.Input;
using Lounge.Core.Launch;
using Lounge.Core.Settings;
using Lounge.Core.Web;
using Lounge.Shell;
using Microsoft.Web.WebView2.Core;

namespace Lounge.Screens;

/// <summary>A child HWND that WebView2 renders into, sized with the control.</summary>
public sealed class WebViewHost : NativeControlHost
{
    private IntPtr _hwnd;
    public CoreWebView2Controller? Controller { get; private set; }
    public event Action<CoreWebView2Controller>? Ready;
    public event Action<string>? Failed;
    private readonly Tile _tile;
    private readonly bool _private;

    /// <param name="inPrivate">A private window: its own InPrivate profile, which keeps
    /// no history, cookies, sign-ins or cache once the last private window closes.</param>
    public WebViewHost(Tile tile, bool inPrivate = false) { _tile = tile; _private = inPrivate; }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // SS_BLACKRECT: any strip the page doesn't cover is black, never white.
        _hwnd = CreateWindowEx(0, "Static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | SS_BLACKRECT, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _fit = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Resize());
        _fit.Start();
        _ = Init();
        return new PlatformHandle(_hwnd, "HWND");
    }

    private async Task Init()
    {
        try
        {
            var args = "--autoplay-policy=no-user-gesture-required";
            if (SelfCheck.Active && Environment.GetEnvironmentVariable("LOUNGE_WEB_ARGS") is { Length: > 0 } extra) args += " " + extra;
            var options = new CoreWebView2EnvironmentOptions(args) { AreBrowserExtensionsEnabled = WebExtensions.Enabled };
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebView, options);
            if (_private)
            {
                var opts = env.CreateCoreWebView2ControllerOptions();
                opts.IsInPrivateModeEnabled = true;
                opts.ProfileName = "Private";
                Controller = await env.CreateCoreWebView2ControllerAsync(_hwnd, opts);
            }
            else Controller = await env.CreateCoreWebView2ControllerAsync(_hwnd);
            // Before the first page, so extensions that work inside pages see it load.
            try { await WebExtensions.Sync(Controller.CoreWebView2.Profile); }
            catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException) { Lounge.Shell.Log.Write("extensions: " + e.Message); }
            Controller.DefaultBackgroundColor = System.Drawing.Color.Black;
            var s = Controller.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsZoomControlEnabled = false;
            // Nothing typed into a site is kept by Lounge's browser: no saved passwords, no form history.
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            // No window.chrome.webview in pages: its presence (and probing its host objects)
            // tells a site it is inside an app, and Prime Video then disables Play. Pages
            // leave messages in a hidden queue that Lounge reads instead (WebScreen.Drain).
            s.IsWebMessageEnabled = false;
            s.AreHostObjectsAllowed = false;
            if (!string.IsNullOrEmpty(_tile.UserAgent)) s.UserAgent = _tile.UserAgent;
            else if (L.Settings.String(K.WebIdentity) == "chrome") s.UserAgent = WebText.ChromeUserAgent(s.UserAgent);
            if (SelfCheck.Active && Environment.GetEnvironmentVariable("LOUNGE_WEB_UA") is { Length: > 0 } ua) s.UserAgent = ua;
            Controller.ZoomFactor = _tile.Zoom > 0 ? _tile.Zoom : L.Settings.Double(K.WebZoom);
            Resize();
            Ready?.Invoke(Controller);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or FileNotFoundException)
        {
            Failed?.Invoke(e is WebView2RuntimeNotFoundException ? "runtime" : e.Message);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Resize();
    }

    /// <summary>Avalonia sizes the host window to this control in real pixels; the page
    /// fills exactly that. (Working the size out from Lounge's own UI scale fell short on
    /// some screens and left a white strip on the right and bottom.) Checked on a timer
    /// too, because full screen and DPI changes resize the window after layout.</summary>
    private void Resize()
    {
        if (Controller is null || _hwnd == IntPtr.Zero || !GetClientRect(_hwnd, out var host)) return;
        // Only the part of the host that is inside Lounge's window: a screen that opened
        // with a zoom can leave the host a few percent larger than the window.
        var top = GetAncestor(_hwnd, 2);
        var size = new System.Drawing.Rectangle(0, 0, host.Right, host.Bottom);
        if (top != IntPtr.Zero && GetClientRect(top, out var win))
        {
            var origin = new POINT();
            MapWindowPoints(_hwnd, top, ref origin, 1);
            var left = Math.Max(0, -origin.X);
            var topEdge = Math.Max(0, -origin.Y);
            var right = Math.Min(host.Right, win.Right - origin.X);
            var bottom = Math.Min(host.Bottom, win.Bottom - origin.Y);
            if (right > left && bottom > topEdge) size = new System.Drawing.Rectangle(left, topEdge, right - left, bottom - topEdge);
        }
        if (size.Width <= 0 || size.Height <= 0 || Controller.Bounds == size) return;
        Controller.Bounds = size;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT pt, int count);

    private DispatcherTimer? _fit;

    /// <summary>Self-check: the window, the page's host and the page, in real pixels.</summary>
    public string Geometry()
    {
        GetClientRect(_hwnd, out var host);
        GetClientRect(GetAncestor(_hwnd, 2), out var top);
        var b = Controller?.Bounds ?? default;
        return $"window {top.Right}x{top.Bottom}, host {host.Right}x{host.Bottom}, page {b.Width}x{b.Height} at {b.X},{b.Y}";
    }

    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    public void Focus2() => Controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _fit?.Stop();
        Controller?.Close();
        Controller = null;
        DestroyWindow(_hwnd);
    }

    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, SS_BLACKRECT = 0x04;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
}

/// <summary>A streaming site inside Lounge. Lounge drives the page itself through
/// <see cref="WebPad"/>: arrow keys on TV-style sites, an in-page pointer everywhere else,
/// the controller keyboard for text fields. B goes back, View opens options, holding B or
/// View leaves. If the page can't play protected video here, Lounge suggests the full browser.</summary>
public sealed class WebScreen : Screen
{
    private readonly Tile _tile;
    private readonly WebViewHost _host;
    private readonly bool _browser, _private;
    private readonly Border _loading;
    private bool _drmChecked;
    private WebPad? _pad;
    private WebDiagnostics? _diag;
    private DispatcherTimer? _drain;
    private bool _draining;
    private bool _described;
    private bool _reported;
    private readonly List<string> _failedRequests = new();
    private bool _helped, _typing;
    private long _typeQuietUntil;

    public override string Context => "Web";
    public override string Title => _tile.Name;
    public bool IsPrivate => _private;
    public override bool WantsAmbient => false;
    public override bool ShowsChrome => false;
    public override bool ShowsBackdrop => false;

    /// <param name="browser">Lounge's browser: View offers an address or search to go to.</param>
    /// <param name="inPrivate">Private browsing: nothing is kept after it closes.</param>
    public WebScreen(Tile tile, bool browser = false, bool inPrivate = false)
    {
        _tile = tile;
        _browser = browser;
        _private = inPrivate;
        Background = Brushes.Black;
        _host = new WebViewHost(tile, inPrivate);
        _loading = new Border
        {
            Background = Brushes.Black,
            Child = Ui.V(20, Ui.Monogram(tile.Name, tile.Color, 140).Also(m => { m.Width = 140; m.Height = 140; m.CornerRadius = new CornerRadius(30); }),
                Ui.Text(inPrivate ? "Opening a private window..." : $"Opening {tile.Name}...", T.Heading, Brushes.White, FontWeight.SemiBold)).Align(HorizontalAlignment.Center, VerticalAlignment.Center),
        };
        Children.Add(_host);
        Children.Add(_loading);
        _host.Ready += async c =>
        {
            var pad = new WebPad(c.CoreWebView2, pointer: _tile.AllowMouse);
            try { await pad.Install(); } catch (Exception) { }
            if (SelfCheck.Active)
            {
                await c.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                    "window.__loungeErrors = []; const ce = console.error; console.error = (...a) => { window.__loungeErrors.push('console: ' + a.map(String).join(' ').slice(0, 200)); ce.apply(console, a); };" +
                    "addEventListener('error', e => window.__loungeErrors.push('error: ' + e.message)); addEventListener('unhandledrejection', e => window.__loungeErrors.push('rejection: ' + String(e.reason).slice(0, 200)));");
                c.CoreWebView2.WebResourceResponseReceived += (_, a) =>
                {
                    if (a.Response.StatusCode >= 300 || Environment.GetEnvironmentVariable("LOUNGE_WEB_ALLREQ") is not null) _failedRequests.Add(DateTime.Now.ToString("ss.f ") + $"{a.Response.StatusCode} {a.Request.Method} {a.Request.Uri[..Math.Min(120, a.Request.Uri.Length)]}");
                };
            }
            _pad = pad;
            if (L.Settings.String(K.WebIdentity) is "edge" or "chrome") await PresentAs(c.CoreWebView2, L.Settings.String(K.WebIdentity));
            if (L.Settings.Bool(K.WebDiagnostics)) _diag = await WebDiagnostics.Start(c.CoreWebView2, _tile.Name);
            c.CoreWebView2.NavigationCompleted += (_, a) => Dispatcher.UIThread.Post(async () =>
            {
                if (_diag is { } diag && !_described) { _described = true; await diag.Describe(c.CoreWebView2); _ = pad.Hint("Recording playback diagnostics (Settings > Web)", 5000); }
                _loading.IsVisible = false;
                if (!a.IsSuccess) L.Window.Toast($"{_tile.Name} didn't load ({a.WebErrorStatus}).", "warning");
                else CheckDrm(c);
                _host.Focus2();
                await pad.PageReady();
                if (!_helped)
                {
                    _helped = true;
                    // One hint at a time (a new one replaces the last), so private mode leads this one.
                    await pad.Hint((_private ? "Private: nothing is kept when you leave  ·  " : "") +
                                   (_browser ? "View: address or search  ·  " : "") + pad.HelpText, 9000);
                }
                if (SelfCheck.Active) _ = ReportSelfCheck(c, a.IsSuccess).ContinueWith(r => { if (r.Exception is { } x) { SelfCheck.Report("web-error", new { error = x.ToString() }); Dispatcher.UIThread.Post(() => L.Window.Exit()); } });
            });
            c.CoreWebView2.FrameCreated += (_, a) => Track(a.Frame);
            c.CoreWebView2.NewWindowRequested += (_, a) => { a.Handled = true; c.CoreWebView2.Navigate(a.Uri); };
            // A page that closes itself (YouTube TV's "Exit") brings Lounge back.
            c.CoreWebView2.WindowCloseRequested += (_, _) => Dispatcher.UIThread.Post(Leave);
            _drain = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, async (_, _) => await Drain(c.CoreWebView2));
            _drain.Start();
            c.CoreWebView2.Navigate(_tile.Target);
        };
        _host.Failed += why => Dispatcher.UIThread.Post(() =>
        {
            if (why == "runtime")
                L.Window.Ask("WebView2 is missing", "Lounge shows websites with Microsoft's WebView2 runtime, which isn't installed. Get it, or open this site in the full browser.",
                    ["Get WebView2", "Open in full browser", "Back"], 2, i =>
                    {
                        if (i == 0) Launcher.OpenBrowser("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                        if (i == 1) Launcher.Launch(_tile.Clone().Also(t => t.OpenExternally = true));
                        L.Window.Remove(this);
                    }, "web");
            else { L.Window.Toast("The web view couldn't start: " + why, "warning"); L.Window.Remove(this); }
        });
    }

    /// <summary>The DRM systems a page could use here: widevine, playready, or blocked.</summary>
    public const string DrmProbe = """
        (async () => { const cfg = [{ initDataTypes: ['cenc'], videoCapabilities: [{ contentType: 'video/mp4; codecs="avc1.42E01E"' }] }];
          try { await navigator.requestMediaKeySystemAccess('com.widevine.alpha', cfg); return 'widevine'; } catch (e) {}
          try { await navigator.requestMediaKeySystemAccess('com.microsoft.playready.recommendation', cfg); return 'playready'; } catch (e) {}
          return 'blocked'; })()
        """;

    /// <summary>Every copy-protection system and robustness level the browser offers, and its brands.</summary>
    public const string DrmMatrixProbe = """
            (async () => {
              const out = [];
              const vid = 'video/mp4; codecs="avc1.640028"';
              const tries = [
                ['com.widevine.alpha', ''], ['com.widevine.alpha', 'SW_SECURE_DECODE'], ['com.widevine.alpha', 'HW_SECURE_ALL'],
                ['com.microsoft.playready.recommendation', ''], ['com.microsoft.playready.recommendation', '2000'],
                ['com.microsoft.playready.recommendation', '3000'], ['com.microsoft.playready.recommendation.3000', ''],
                ['com.microsoft.playready.hardware', ''], ['com.microsoft.playready', '']];
              for (const [ks, rob] of tries) {
                try {
                  await navigator.requestMediaKeySystemAccess(ks, [{ initDataTypes: ['cenc'], videoCapabilities: [{ contentType: vid, robustness: rob }], audioCapabilities: [{ contentType: 'audio/mp4; codecs="mp4a.40.2"' }] }]);
                  out.push(ks + (rob ? ' ' + rob : '') + ': yes');
                } catch (e) { out.push(ks + (rob ? ' ' + rob : '') + ': no'); }
              }
              const b = navigator.userAgentData ? navigator.userAgentData.brands.map(x => x.brand + ' ' + x.version).join(', ') : 'n/a';
              out.push('brands: ' + b);
              return out.join(' | ');
            })()
            """;

    /// <summary>What each copy-protection system says about the screen link: the check sites
    /// make (MediaKeys.getStatusForPolicy) before they hand out a licence.</summary>
    public const string HdcpProbe = """
            (async () => {
              const out = [];
              const cfg = [{ initDataTypes: ['cenc'], videoCapabilities: [{ contentType: 'video/mp4; codecs="avc1.640028"' }] }];
              for (const ks of ['com.widevine.alpha', 'com.microsoft.playready.recommendation']) {
                try {
                  const keys = await (await navigator.requestMediaKeySystemAccess(ks, cfg)).createMediaKeys();
                  for (const v of ['1.0', '1.4', '2.2']) {
                    try { out.push(ks + ' HDCP ' + v + ': ' + await keys.getStatusForPolicy({ minHdcpVersion: v })); }
                    catch (e) { out.push(ks + ' HDCP ' + v + ': ' + e.name); }
                  }
                } catch (e) { out.push(ks + ': ' + e.name); }
              }
              return out.join(' | ');
            })()
            """;

    /// <summary>ExecuteScriptAsync returns a Promise as "{}", so the result is parked on
    /// the window and read back until it arrives.</summary>
    public static Task<string> EvalPromise(CoreWebView2 web, string promiseExpression, int timeoutMs = 4000) =>
        EvalPromise(web.ExecuteScriptAsync, promiseExpression, timeoutMs);

    /// <summary>The same in any script context: the page or one of its frames.</summary>
    public static async Task<string> EvalPromise(Func<string, Task<string>> execute, string promiseExpression, int timeoutMs = 4000)
    {
        var slot = "__lounge" + Guid.NewGuid().ToString("N")[..8];
        // The slot is hidden from enumeration and removed once read, so pages can't spot it.
        await execute($"Object.defineProperty(window, '{slot}', {{ value: undefined, writable: true, configurable: true, enumerable: false }}); Promise.resolve({promiseExpression}).then(r => window.{slot} = String(r), e => window.{slot} = 'error:' + e);");
        try
        {
            for (int waited = 0; waited < timeoutMs; waited += 100)
            {
                var r = await execute($"window.{slot} === undefined ? null : window.{slot}");
                if (r != "null") return System.Text.Json.JsonSerializer.Deserialize<string>(r) ?? "";
                await Task.Delay(100);
            }
            return "timeout";
        }
        finally { _ = execute($"delete window.{slot}"); }
    }

    /// <summary>The page's frames, nested ones included, for scripts that must reach a
    /// consent dialog wherever it lives.</summary>
    private readonly List<CoreWebView2Frame> _frames = new();

    private void Track(CoreWebView2Frame frame)
    {
        _frames.Add(frame);
        frame.Destroyed += (_, _) => _frames.Remove(frame);
        frame.FrameCreated += (_, a) => Track(a.Frame);
    }

    /// <summary>View > Extensions: run AllOff, switch browser extensions on or off.</summary>
    private async void ExtensionsMenu()
    {
        var c = _host.Controller;
        if (c is null) return;
        var items = new List<MenuEntry>();
        if (WebExtensions.HasAllOff)
            items.Add(new MenuEntry("AllOff: switch off consent toggles", "puzzle", () => DispatcherTimer.RunOnce(RunAllOff, TimeSpan.FromMilliseconds(300)), "Run"));
        if (WebExtensions.Enabled)
        {
            try
            {
                foreach (var ext in await WebExtensions.Ours(c.CoreWebView2.Profile))
                    items.Add(new MenuEntry(ext.Name, "puzzle", async () =>
                    {
                        try { await ext.EnableAsync(!ext.IsEnabled); }
                        catch (Exception e) when (e is COMException or InvalidOperationException) { }
                        // Content scripts attach on load, so the page reloads to pick the change up.
                        c.CoreWebView2.Reload();
                    }, ext.IsEnabled ? "On" : "Off"));
            }
            catch (Exception e) when (e is COMException or InvalidOperationException) { }
        }
        items.Add(new MenuEntry("Add an extension", "folder", () => L.Window.Ask("Add an extension",
            $"Put an unpacked extension (its folder, with manifest.json inside) in\n{AppPaths.Extensions}\nand it loads into every site " +
            (WebExtensions.Enabled ? "the next time a site opens." : "after Lounge restarts.") +
            " Extensions that work inside pages (blockers, tweaks) work here; ones that only act from their toolbar button don't.",
            ["Got it"], 0, _ => { }, "puzzle")));
        L.Window.Menu("Extensions", items);
    }

    private async void RunAllOff()
    {
        var c = _host.Controller;
        if (c is null || _pad is not { } pad) return;
        _ = pad.Hint("AllOff is switching consent toggles off...", 3000);
        var summary = await WebExtensions.RunAllOff(c.CoreWebView2, _frames.ToList());
        _ = pad.Hint(summary, 6000);
    }

    private async void CheckDrm(CoreWebView2Controller c)
    {
        if (_drmChecked || !L.Settings.Bool(K.DrmHint)) return;
        _drmChecked = true;
        try
        {
            var result = await EvalPromise(c.CoreWebView2, DrmProbe);
            if (result.Contains("blocked") && _tile.AllowMouse)
                L.Window.Toast($"{_tile.Name} may not play protected video here. Press View and choose \"Open in full browser\" if it won't start.", "web", 7);
        }
        catch (Exception) { }
    }

    /// <summary>LOUNGE_SELFCHECK: what the embedded browser can do on this machine.</summary>
    private async Task ReportSelfCheck(CoreWebView2Controller c, bool loaded)
    {
        if (_reported) return;
        _reported = true;
        await Task.Delay(int.TryParse(Environment.GetEnvironmentVariable("LOUNGE_WEB_WAIT"), out var wait) ? wait : 6000);   // TV apps keep building after the load event
        string Eval(string r) => r.Trim('"');
        var pad = _pad!;
        // Controller input reaches the page as trusted events (not via the page's own gamepad code).
        await c.CoreWebView2.ExecuteScriptAsync("window.__loungeKeys = []; addEventListener('keydown', e => window.__loungeKeys.push(e.key + (e.isTrusted ? '' : '?')), true); window.__loungeClicks = 0; addEventListener('click', e => { if (e.isTrusted) window.__loungeClicks++; }, true);");
        var focusBefore = Eval(await c.CoreWebView2.ExecuteScriptAsync("(document.activeElement && (document.activeElement.tagName + '.' + document.activeElement.className)).slice(0, 80)"));
        if (Environment.GetEnvironmentVariable("LOUNGE_WEB_KEYS") is null)
            foreach (var k in new[] { "ArrowDown", "ArrowRight", "ArrowRight" }) { await pad.Key(k); await Task.Delay(500); }
        var focusAfter = Eval(await c.CoreWebView2.ExecuteScriptAsync("(document.activeElement && (document.activeElement.tagName + '.' + document.activeElement.className)).slice(0, 80)"));
        var keys = Eval(await c.CoreWebView2.ExecuteScriptAsync("window.__loungeKeys.join(' ')"));
        pad.MovePointer(5, 5);
        await pad.Click();
        await Task.Delay(300);
        var clicks = Eval(await c.CoreWebView2.ExecuteScriptAsync("String(window.__loungeClicks)"));
        var walk = new List<string>();
        // LOUNGE_WEB_KEYS="ArrowDown,Enter,wait:8000,...": walk the page like a person would first.
        foreach (var step in (Environment.GetEnvironmentVariable("LOUNGE_WEB_KEYS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            _failedRequests.Add(DateTime.Now.ToString("ss.f ") + "---- " + step);
            if (step.StartsWith("wait:")) await Task.Delay(int.Parse(step[5..]));
            else if (step.StartsWith("volume:")) { await pad.Volume(int.Parse(step[7..]), false); await Task.Delay(300); }
            else if (step.StartsWith("hash:")) { await c.CoreWebView2.ExecuteScriptAsync($"location.hash = {System.Text.Json.JsonSerializer.Serialize(step[5..])}"); await Task.Delay(700); }
            else if (step.StartsWith("hold:")) { var parts = step.Split(':'); await pad.Key(parts[2], int.Parse(parts[1])); await Task.Delay(700); }
            else if (step == "jsclick") { await c.CoreWebView2.ExecuteScriptAsync("(document.querySelector('.zylon-focus') || document.activeElement).click()"); await Task.Delay(700); }
            else if (step == "mouse")
            {
                var r = Eval(await c.CoreWebView2.ExecuteScriptAsync("(() => { const b = (document.querySelector('.zylon-focus') || document.activeElement).getBoundingClientRect(); return (b.left + b.width / 2) + ',' + (b.top + b.height / 2); })()")).Split(',');
                pad.MovePointer(double.Parse(r[0], System.Globalization.CultureInfo.InvariantCulture) - 10000, double.Parse(r[1], System.Globalization.CultureInfo.InvariantCulture) - 10000);
                pad.MovePointer(double.Parse(r[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(r[1], System.Globalization.CultureInfo.InvariantCulture));
                await Task.Delay(300);
                await pad.Click();
                await Task.Delay(700);
            }
            else { await pad.Key(step); await Task.Delay(700); }
            walk.Add(step + " -> " + Eval(await c.CoreWebView2.ExecuteScriptAsync("(() => { const all = document.querySelectorAll('.zylon-focus'); const e = all.length ? all[all.length - 1] : document.activeElement; return e ? (e.innerText || e.tagName).replace(/\\s+/g, ' ').slice(0, 40) : '' })()")));
        }
        // LOUNGE_WEB_EVAL: an expression to report after the walk (what the page looks like then).
        var probe = Environment.GetEnvironmentVariable("LOUNGE_WEB_EVAL") is { Length: > 0 } expr ? Eval(await c.CoreWebView2.ExecuteScriptAsync(expr)) : null;
        // LOUNGE_WEB_ALLOFF: run AllOff as View > Extensions would.
        var allOff = Environment.GetEnvironmentVariable("LOUNGE_WEB_ALLOFF") is null ? null : await WebExtensions.RunAllOff(c.CoreWebView2, _frames.ToList());
        var extensions = WebExtensions.Enabled ? string.Join(", ", (await WebExtensions.Ours(c.CoreWebView2.Profile)).Select(x => $"{x.Name} ({(x.IsEnabled ? "on" : "off")})")) : "disabled";
        var injected = Eval(await c.CoreWebView2.ExecuteScriptAsync("String(!!window.__lounge)"));
        // What a page can see of the app around it: nothing, ideally.
        var appBridge = Eval(await c.CoreWebView2.ExecuteScriptAsync("String(!!(window.chrome && window.chrome.webview)) + ' / visible globals: ' + Object.keys(window).filter(k => /lounge/i.test(k)).join(',')"));
        // Whether a video on the page is actually playing (not just the page loading).
        var video = Eval(await c.CoreWebView2.ExecuteScriptAsync(
            "(() => { const v = document.querySelector('video'); if (!v) return 'no video element'; " +
            "return `paused=${v.paused} time=${v.currentTime.toFixed(1)} ready=${v.readyState} err=${v.error ? v.error.code + ':' + v.error.message : 'none'} src=${(v.currentSrc || '').slice(0, 40)}`; })()"));
        var text = Eval(await c.CoreWebView2.ExecuteScriptAsync("(document.body ? document.body.innerText : '').replace(/\\s+/g, ' ').slice(0, 2000)"));
        if (!(Environment.GetEnvironmentVariable("LOUNGE_WEB_KEYS") ?? "").Contains("volume:")) await pad.Hint(pad.HelpText, 9000);
        await Task.Delay(500);
        using (var png = File.Create(Lounge.Shell.SelfCheck.PathFor("web.png")))
            await c.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
        var title = Eval(await c.CoreWebView2.ExecuteScriptAsync("document.title"));
        var gamepadApi = Eval(await c.CoreWebView2.ExecuteScriptAsync("typeof navigator.getGamepads === 'function' ? 'yes' : 'no'"));
        var pads = Eval(await c.CoreWebView2.ExecuteScriptAsync("Array.from(navigator.getGamepads()).filter(p => p).length.toString()"));
        var drm = await EvalPromise(c.CoreWebView2, DrmProbe);
        // What a streaming site sees: each DRM system and robustness level, and the browser's brands.
        var drmMatrix = await EvalPromise(c.CoreWebView2, DrmMatrixProbe, 15000);
        var hdcp = await EvalPromise(c.CoreWebView2, HdcpProbe, 15000);
        Lounge.Shell.SelfCheck.Report("web", new
        {
            loaded, title, gamepadApi, padsVisibleToPage = pads, drm, drmMatrix, hdcp, allOff, probe, frames = _frames.Count, extensions,
            pointerMode = pad.PointerMode, injected, keysReceived = keys, focusBefore, focusAfter, trustedClicks = clicks, appBridge, video, text, walk, geometry = _host.Geometry(), failedRequests = _failedRequests.ToArray().TakeLast(60).ToList(),
            errors = Eval(await c.CoreWebView2.ExecuteScriptAsync("(window.__loungeErrors || []).slice(-15).join(' | ')")),
            userAgent = Eval(await c.CoreWebView2.ExecuteScriptAsync("navigator.userAgent")),
            zoom = c.ZoomFactor,
        });
        L.Window.Exit();
    }

    public override bool Handle(ActionEvent e)
    {
        // Leaving, settings and volume are Lounge's everywhere.
        if (e.Action is Act.Home or Act.Settings or Act.VolumeUp or Act.VolumeDown) return false;
        if (e.Action is Act.ZoomIn or Act.ZoomOut)
        {
            if (e.Phase is ActionPhase.Pressed or ActionPhase.Repeated) StepZoom(e.Action == Act.ZoomIn ? 1 : -1);
            return true;
        }
        var pad = _pad;
        var web = _host.Controller?.CoreWebView2;
        if (pad is null || web is null)
        {
            if (e.Action is Act.Back or Act.ContextMenu && e.Phase == ActionPhase.Pressed) Leave();
            return true;
        }
        switch (e.Action)
        {
            case Act.CursorMove:
                if (e.Phase is ActionPhase.Pressed or ActionPhase.Changed)
                {
                    if (pad.PointerMode)
                    {
                        var speed = L.Settings.Int(K.CursorSpeed);
                        var accel = Math.Pow(e.Value, 1.6) * speed * 2.2 / Math.Max(e.Value, 0.0001) / Math.Max(0.5, _host.Controller!.ZoomFactor);
                        pad.MovePointer(e.X * accel, -e.Y * accel);
                    }
                    else if (pad.StickToKey(e.X, e.Y, e.Value, Environment.TickCount64) is { } key) _ = pad.Key(key);
                }
                else pad.StickReleased();
                return true;
            case Act.CursorScroll:
                if (e.Phase is ActionPhase.Pressed or ActionPhase.Changed)
                    _ = pad.Scroll(e.X * 26 * e.Value, -e.Y * 26 * e.Value);
                return true;
        }
        if (e.Phase is not (ActionPhase.Pressed or ActionPhase.Repeated)) return true;
        switch (e.Action)
        {
            case Act.Up: _ = pad.Key("ArrowUp"); return true;
            case Act.Down: _ = pad.Key("ArrowDown"); return true;
            case Act.Left: _ = pad.Key("ArrowLeft"); return true;
            case Act.Right: _ = pad.Key("ArrowRight"); return true;
        }
        if (e.Phase != ActionPhase.Pressed) return true;
        switch (e.Action)
        {
            case Act.Accept:
                if (pad.PointerMode) _ = pad.Click(); else _ = pad.Key("Enter");
                return true;
            case Act.Back:
                Back(pad, web);
                return true;
            case Act.ContextMenu:
                Options();
                return true;
            case Act.Secondary or Act.CursorToggle:
                _ = pad.TogglePointer();
                return true;
            case Act.Tertiary:
                _ = TypeIntoFocused(web);
                return true;
            case Act.PrevSection:
                if (web.CanGoBack) web.GoBack();
                return true;
            case Act.NextSection:
                if (web.CanGoForward) web.GoForward();
                return true;
        }
        return true;   // nothing else leaks to Lounge's screens underneath
    }

    /// <summary>B: TV-style pages get their own Back key (YouTube TV asks before exiting and
    /// then closes, which brings Lounge back); other pages go back a page, and leave from
    /// the first one.</summary>
    private void Back(WebPad pad, CoreWebView2 web)
    {
        if (!pad.PointerMode) { _ = pad.Key("Escape"); return; }
        if (web.CanGoBack) web.GoBack();
        else Leave();
    }

    /// <summary>"Identify to sites as" Edge or Chrome: WebView2 announces itself as "Microsoft
    /// Edge WebView2" in its brand list, which some streaming sites refuse, and sites pick
    /// their video protection by browser (PlayReady for Edge, Widevine for Chrome). Pages
    /// see the chosen brand list (navigator.userAgentData) and requests carry it (Sec-CH-UA).
    /// The engine and its copy protection are unchanged.</summary>
    private static async Task PresentAs(CoreWebView2 web, string identity)
    {
        var chrome = identity == "chrome" ? "true" : "false";
        await web.AddScriptToExecuteOnDocumentCreatedAsync($$"""
            (() => {
              const d = navigator.userAgentData;
              if (!d) return;
              const chrome = {{chrome}};
              const fix = list => (list || []).filter(b => !/webview/i.test(b.brand))
                .map(b => chrome && b.brand === 'Microsoft Edge' ? Object.assign({}, b, { brand: 'Google Chrome' }) : b);
              const proto = Object.getPrototypeOf(d);
              const brands = fix(d.brands);
              Object.defineProperty(proto, 'brands', { get: () => brands, configurable: true });
              const high = proto.getHighEntropyValues;
              proto.getHighEntropyValues = function (hints) {
                return high.call(this, hints).then(v => { if (v.brands) v.brands = fix(v.brands); if (v.fullVersionList) v.fullVersionList = fix(v.fullVersionList); return v; });
              };
              const json = proto.toJSON;
              if (json) proto.toJSON = function () { const o = json.call(this); o.brands = fix(o.brands); return o; };
            })();
            """);
        web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        web.WebResourceRequested += (_, a) =>
        {
            var headers = a.Request.Headers;
            foreach (var name in new[] { "sec-ch-ua", "sec-ch-ua-full-version-list" })
                if (headers.Contains(name)) headers.SetHeader(name, WebText.RewriteBrands(headers.GetHeader(name), identity));
        };
    }
    private void Leave()
    {
        if (L.Window.Find<WebScreen>() == this) L.Window.Remove(this);
    }

    /// <summary>Reads the messages pages leave in their hidden queue (a focused text field,
    /// diagnostic lines). Pages get no app bridge, so this is how they reach Lounge.</summary>
    private async Task Drain(CoreWebView2 web)
    {
        if (_draining) return;
        _draining = true;
        try
        {
            const string take = "window.__loungeQ && window.__loungeQ.length ? JSON.stringify(window.__loungeQ.splice(0)) : ''";
            // The page and its frames: a player (or a text field) can live in a frame.
            var raws = new List<string> { await web.ExecuteScriptAsync(take) };
            foreach (var frame in _frames.ToList())
                try { raws.Add(await frame.ExecuteScriptAsync(take)); }
                catch (Exception e) when (e is InvalidOperationException or COMException) { }
            foreach (var raw in raws)
            {
                var json = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
                if (!string.IsNullOrEmpty(json))
                    foreach (var message in System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? [])
                        OnPageMessage(message);
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or COMException or ArgumentException) { }
        finally { _draining = false; }
    }

    private void OnPageMessage(string? json)
    {
        if (json is null) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var r = doc.RootElement;
            var kind = r.GetProperty("kind").GetString();
            if (kind == "diag") { _diag?.Write(r.GetProperty("line").GetString() ?? ""); return; }
            if (kind != "text" || _pad is not { PointerMode: true }) return;
            var password = r.TryGetProperty("password", out var pw) && pw.GetBoolean();
            Type(r.GetProperty("label").GetString() ?? "", password ? "" : r.GetProperty("value").GetString() ?? "", r.GetProperty("search").GetBoolean(), password);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    /// <summary>Y: the keyboard for the field that has focus, asking the page first so a
    /// password field gets the masked keyboard.</summary>
    private async Task TypeIntoFocused(CoreWebView2 web)
    {
        string label = "", value = "";
        var password = false;
        try
        {
            var json = System.Text.Json.JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("window.__lounge ? window.__lounge.focused() : ''"));
            if (!string.IsNullOrEmpty(json))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                label = doc.RootElement.GetProperty("label").GetString() ?? "";
                password = doc.RootElement.GetProperty("password").GetBoolean();
                value = password ? "" : doc.RootElement.GetProperty("value").GetString() ?? "";
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { }
        Type(label, value, search: false, password);
    }

    /// <summary>The controller keyboard for whatever text field has focus in the page. The
    /// typed text replaces the field's value; search fields are submitted.</summary>
    private void Type(string label, string value, bool search, bool password = false)
    {
        if (_typing || Environment.TickCount64 < _typeQuietUntil || _pad is not { } pad) return;
        _typing = true;
        var title = password ? "Password" : string.IsNullOrWhiteSpace(label) ? $"Type into {_tile.Name}" : label.Trim();
        L.Window.Keyboard(new KeyboardRequest(title, password ? "" : value, SelectAll: false, Secret: password), text =>
        {
            _typing = false;
            // Showing the page again re-focuses the field; don't open the keyboard again for that.
            _typeQuietUntil = Environment.TickCount64 + 1500;
            if (text is null) return;
            DispatcherTimer.RunOnce(async () =>
            {
                var web = _host.Controller?.CoreWebView2;
                if (web is null) return;
                try
                {
                    await web.ExecuteScriptAsync("(() => { const el = document.activeElement; if (!el) return; if (el.select) el.select(); else document.execCommand('selectAll'); })()");
                    await pad.InsertText(text);
                    if (search && text.Length > 0) await pad.Key("Enter");
                }
                catch (Exception) { }
            }, TimeSpan.FromMilliseconds(300));
        });
    }

    /// <summary>The browser's address bar: the controller keyboard, starting from the
    /// page's address. An address opens; anything else is searched for.</summary>
    private void GoTo()
    {
        var web = _host.Controller?.CoreWebView2;
        if (web is null) return;
        _typeQuietUntil = Environment.TickCount64 + 1500;
        L.Window.Keyboard(new KeyboardRequest(_private ? "Address or search (private)" : "Address or search", web.Source, SelectAll: true), text =>
        {
            _typeQuietUntil = Environment.TickCount64 + 1500;
            if (text is null) return;
            _host.Controller?.CoreWebView2.Navigate(WebText.AddressOrSearch(text));
        });
    }

    private void Options()
    {
        var c = _host.Controller;
        var pad = _pad;
        var items = new List<MenuEntry> { new("Back to Lounge", "home", Leave) };
        if (_browser)
        {
            items.Add(new MenuEntry("Go to an address or search", "search", () => DispatcherTimer.RunOnce(GoTo, TimeSpan.FromMilliseconds(250)),
                c is null ? null : WebText.Address(c.CoreWebView2.Source)));
            items.Add(new MenuEntry("Go forward a page", "chevron", () => { if (c?.CoreWebView2.CanGoForward == true) c.CoreWebView2.GoForward(); }));
            if (!_private) items.Add(new MenuEntry("New private window", "eye-off", () => Browser.Start(inPrivate: true)));
        }
        L.Window.Menu(_private ? "Private browsing" : _tile.Name,
        [
            ..items,
            new MenuEntry("Pointer", "mouse", () => { if (pad is not null) _ = pad.TogglePointer(); }, pad?.PointerMode == true ? "On" : "Off"),
            new MenuEntry("Keyboard", "keyboard", () => { if (c is not null) _ = TypeIntoFocused(c.CoreWebView2); }),
            new MenuEntry("Extensions", "puzzle", ExtensionsMenu),
            new MenuEntry("Show controls", "info", () => { if (pad is not null) DispatcherTimer.RunOnce(() => _ = pad.Hint(pad.HelpText, 9000), TimeSpan.FromMilliseconds(300)); }),
            new MenuEntry("Go back a page", "back", () => { if (c?.CoreWebView2.CanGoBack == true) c.CoreWebView2.GoBack(); }),
            new MenuEntry("Reload", "refresh", () => c?.CoreWebView2.Reload()),
            new MenuEntry("Zoom", "search", () => DispatcherTimer.RunOnce(ZoomMenu, TimeSpan.FromMilliseconds(250)), c is null ? null : WebZoom.Percent(c.ZoomFactor)),
            new MenuEntry("Open in full browser", "external", () =>
            {
                // The browser opens the page you are on; a site's tile opens the site.
                var target = _browser && c is not null ? c.CoreWebView2.Source : _tile.Target;
                Leave();
                Launcher.Launch(_tile.Clone().Also(t => { t.Target = target; t.OpenExternally = true; }));
            }),
        ]);
    }

    /// <summary>View > Zoom: every level at once, the current one ticked, so any zoom is
    /// one press away. The choice is remembered for the site.</summary>
    private void ZoomMenu()
    {
        if (_host.Controller is not { } c) return;
        var standard = L.Settings.Double(K.WebZoom);
        var items = WebZoom.Levels.Select(z => new MenuEntry(WebZoom.Percent(z), "", () => SetZoom(z),
            Math.Abs(z - standard) < 0.001 ? "Default" : null, Checked: Math.Abs(z - c.ZoomFactor) < 0.001)).ToList();
        if (!WebZoom.Levels.Any(z => Math.Abs(z - standard) < 0.001))
            items.Add(new MenuEntry($"Default ({WebZoom.Percent(standard)})", "", () => SetZoom(standard), Checked: Math.Abs(standard - c.ZoomFactor) < 0.001));
        L.Window.Menu($"Zoom: {_tile.Name}", items);
    }

    /// <summary>Zoom in/out actions: the next level, shown briefly in the page.</summary>
    private void StepZoom(int direction)
    {
        if (_host.Controller is not { } c) return;
        SetZoom(WebZoom.Step(c.ZoomFactor, direction));
        if (_pad is { } pad) _ = pad.Hint("Zoom " + WebZoom.Percent(c.ZoomFactor), 1200);
    }

    private void SetZoom(double zoom)
    {
        if (_host.Controller is not { } c) return;
        c.ZoomFactor = Math.Clamp(zoom, WebZoom.Levels[0], WebZoom.Levels[^1]);
        // A site's tile keeps its zoom; the browser goes back to the default next time.
        if (!_browser && L.Tiles.Find(_tile.Id) is { } saved)
        {
            saved.Zoom = Math.Abs(c.ZoomFactor - L.Settings.Double(K.WebZoom)) < 0.001 ? 0 : c.ZoomFactor;
            L.Tiles.Update(saved);
        }
    }

    /// <summary>The volume display, drawn inside the page so it shows over the site.</summary>
    public void ShowVolume(int percent, bool muted) { if (_pad is { } pad) _ = pad.Volume(percent, muted); }

    public void SetCovered(bool covered)
    {
        _host.IsVisible = !covered;
        if (!covered) _host.Focus2();
    }

    public override void OnShown() => DispatcherTimer.RunOnce(() => _host.Focus2(), TimeSpan.FromMilliseconds(200));

    public override void OnClosed()
    {
        _drain?.Stop();
        if (L.Window.CursorMode) L.Window.ToggleCursor();
    }
}
