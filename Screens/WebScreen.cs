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

    public WebViewHost(Tile tile) => _tile = tile;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _hwnd = CreateWindowEx(0, "Static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _ = Init();
        return new PlatformHandle(_hwnd, "HWND");
    }

    private async Task Init()
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebView, options);
            Controller = await env.CreateCoreWebView2ControllerAsync(_hwnd);
            Controller.DefaultBackgroundColor = System.Drawing.Color.Black;
            var s = Controller.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsZoomControlEnabled = false;
            if (!string.IsNullOrEmpty(_tile.UserAgent)) s.UserAgent = _tile.UserAgent;
            Controller.ZoomFactor = L.Settings.Double(K.WebZoom);
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

    private void Resize()
    {
        if (Controller is null) return;
        var scale = VisualRoot?.RenderScaling ?? 1;
        var origin = this.TranslatePoint(new Point(0, 0), (Visual)VisualRoot!) ?? new Point(0, 0);
        _ = origin;
        var w = (int)(Bounds.Width * L.Window.Scale * scale);
        var h = (int)(Bounds.Height * L.Window.Scale * scale);
        MoveWindow(_hwnd, 0, 0, w, h, true);
        Controller.Bounds = new System.Drawing.Rectangle(0, 0, w, h);
    }

    public void Focus2() => Controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Controller?.Close();
        Controller = null;
        DestroyWindow(_hwnd);
    }

    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;

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
    private readonly Border _loading;
    private bool _drmChecked;
    private WebPad? _pad;
    private bool _reported;
    private bool _helped, _typing;
    private long _typeQuietUntil;

    public override string Context => "Web";
    public override string Title => _tile.Name;
    public override bool WantsAmbient => false;
    public override bool ShowsChrome => false;
    public override bool ShowsBackdrop => false;

    public WebScreen(Tile tile)
    {
        _tile = tile;
        Background = Brushes.Black;
        _host = new WebViewHost(tile);
        _loading = new Border
        {
            Background = Brushes.Black,
            Child = Ui.V(20, Ui.Monogram(tile.Name, tile.Color, 140).Also(m => { m.Width = 140; m.Height = 140; m.CornerRadius = new CornerRadius(30); }),
                Ui.Text($"Opening {tile.Name}...", T.Heading, Brushes.White, FontWeight.SemiBold)).Align(HorizontalAlignment.Center, VerticalAlignment.Center),
        };
        Children.Add(_host);
        Children.Add(_loading);
        _host.Ready += async c =>
        {
            var pad = new WebPad(c.CoreWebView2, pointer: _tile.AllowMouse);
            try { await pad.Install(); } catch (Exception) { }
            _pad = pad;
            c.CoreWebView2.NavigationCompleted += (_, a) => Dispatcher.UIThread.Post(async () =>
            {
                _loading.IsVisible = false;
                if (!a.IsSuccess) L.Window.Toast($"{_tile.Name} didn't load ({a.WebErrorStatus}).", "warning");
                else CheckDrm(c);
                _host.Focus2();
                await pad.PageReady();
                if (!_helped) { _helped = true; await pad.Hint(pad.HelpText, 9000); }
                if (SelfCheck.Active) _ = ReportSelfCheck(c, a.IsSuccess);
            });
            c.CoreWebView2.NewWindowRequested += (_, a) => { a.Handled = true; c.CoreWebView2.Navigate(a.Uri); };
            // A page that closes itself (YouTube TV's "Exit") brings Lounge back.
            c.CoreWebView2.WindowCloseRequested += (_, _) => Dispatcher.UIThread.Post(Leave);
            c.CoreWebView2.WebMessageReceived += (_, a) =>
            {
                string? json;
                try { json = a.TryGetWebMessageAsString(); } catch (ArgumentException) { return; }
                Dispatcher.UIThread.Post(() => OnPageMessage(json));
            };
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

    /// <summary>ExecuteScriptAsync returns a Promise as "{}", so the result is parked on
    /// the window and read back until it arrives.</summary>
    public static async Task<string> EvalPromise(CoreWebView2 web, string promiseExpression, int timeoutMs = 4000)
    {
        var slot = "__lounge" + Guid.NewGuid().ToString("N")[..8];
        await web.ExecuteScriptAsync($"window.{slot} = undefined; Promise.resolve({promiseExpression}).then(r => window.{slot} = String(r), e => window.{slot} = 'error:' + e);");
        for (int waited = 0; waited < timeoutMs; waited += 100)
        {
            var r = await web.ExecuteScriptAsync($"window.{slot} === undefined ? null : window.{slot}");
            if (r != "null") return System.Text.Json.JsonSerializer.Deserialize<string>(r) ?? "";
            await Task.Delay(100);
        }
        return "timeout";
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
        await Task.Delay(6000);   // TV apps keep building after the load event
        string Eval(string r) => r.Trim('"');
        var pad = _pad!;
        // Controller input reaches the page as trusted events (not via the page's own gamepad code).
        await c.CoreWebView2.ExecuteScriptAsync("window.__loungeKeys = []; addEventListener('keydown', e => window.__loungeKeys.push(e.key + (e.isTrusted ? '' : '?')), true); window.__loungeClicks = 0; addEventListener('click', e => { if (e.isTrusted) window.__loungeClicks++; }, true);");
        var focusBefore = Eval(await c.CoreWebView2.ExecuteScriptAsync("(document.activeElement && (document.activeElement.tagName + '.' + document.activeElement.className)).slice(0, 80)"));
        foreach (var k in new[] { "ArrowDown", "ArrowRight", "ArrowRight" }) { await pad.Key(k); await Task.Delay(500); }
        var focusAfter = Eval(await c.CoreWebView2.ExecuteScriptAsync("(document.activeElement && (document.activeElement.tagName + '.' + document.activeElement.className)).slice(0, 80)"));
        var keys = Eval(await c.CoreWebView2.ExecuteScriptAsync("window.__loungeKeys.join(' ')"));
        pad.MovePointer(5, 5);
        await pad.Click();
        await Task.Delay(300);
        var clicks = Eval(await c.CoreWebView2.ExecuteScriptAsync("String(window.__loungeClicks)"));
        var injected = Eval(await c.CoreWebView2.ExecuteScriptAsync("String(!!window.__lounge)"));
        await pad.Hint(pad.HelpText, 9000);
        await Task.Delay(500);
        using (var png = File.Create(Lounge.Shell.SelfCheck.PathFor("web.png")))
            await c.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
        var title = Eval(await c.CoreWebView2.ExecuteScriptAsync("document.title"));
        var gamepadApi = Eval(await c.CoreWebView2.ExecuteScriptAsync("typeof navigator.getGamepads === 'function' ? 'yes' : 'no'"));
        var pads = Eval(await c.CoreWebView2.ExecuteScriptAsync("Array.from(navigator.getGamepads()).filter(p => p).length.toString()"));
        var drm = await EvalPromise(c.CoreWebView2, DrmProbe);
        Lounge.Shell.SelfCheck.Report("web", new
        {
            loaded, title, gamepadApi, padsVisibleToPage = pads, drm,
            pointerMode = pad.PointerMode, injected, keysReceived = keys, focusBefore, focusAfter, trustedClicks = clicks,
            userAgent = Eval(await c.CoreWebView2.ExecuteScriptAsync("navigator.userAgent")),
            zoom = c.ZoomFactor,
        });
        L.Window.Exit();
    }

    public override bool Handle(ActionEvent e)
    {
        // Leaving, settings and volume are Lounge's everywhere.
        if (e.Action is Act.Home or Act.Settings or Act.VolumeUp or Act.VolumeDown) return false;
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
                Type("", "", search: false);
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

    private void Leave()
    {
        if (L.Window.Find<WebScreen>() == this) L.Window.Remove(this);
    }

    private void OnPageMessage(string? json)
    {
        if (json is null || _pad is not { PointerMode: true }) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.GetProperty("kind").GetString() != "text") return;
            Type(r.GetProperty("label").GetString() ?? "", r.GetProperty("value").GetString() ?? "", r.GetProperty("search").GetBoolean());
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    /// <summary>The controller keyboard for whatever text field has focus in the page. The
    /// typed text replaces the field's value; search fields are submitted.</summary>
    private void Type(string label, string value, bool search)
    {
        if (_typing || Environment.TickCount64 < _typeQuietUntil || _pad is not { } pad) return;
        _typing = true;
        var title = string.IsNullOrWhiteSpace(label) ? $"Type into {_tile.Name}" : label.Trim();
        L.Window.Keyboard(new KeyboardRequest(title, value, SelectAll: false), text =>
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

    private void Options()
    {
        var c = _host.Controller;
        var pad = _pad;
        L.Window.Menu(_tile.Name,
        [
            new MenuEntry("Back to Lounge", "home", Leave),
            new MenuEntry("Pointer", "mouse", () => { if (pad is not null) _ = pad.TogglePointer(); }, pad?.PointerMode == true ? "On" : "Off"),
            new MenuEntry("Keyboard", "keyboard", () => Type("", "", search: false)),
            new MenuEntry("Show controls", "info", () => { if (pad is not null) DispatcherTimer.RunOnce(() => _ = pad.Hint(pad.HelpText, 9000), TimeSpan.FromMilliseconds(300)); }),
            new MenuEntry("Go back a page", "back", () => { if (c?.CoreWebView2.CanGoBack == true) c.CoreWebView2.GoBack(); }),
            new MenuEntry("Reload", "refresh", () => c?.CoreWebView2.Reload()),
            new MenuEntry("Zoom in", "plus", () => { if (c is not null) c.ZoomFactor = Math.Min(3, c.ZoomFactor + 0.1); }),
            new MenuEntry("Zoom out", "search", () => { if (c is not null) c.ZoomFactor = Math.Max(0.5, c.ZoomFactor - 0.1); }),
            new MenuEntry("Open in full browser", "external", () => { Leave(); Launcher.Launch(_tile.Clone().Also(t => t.OpenExternally = true)); }),
        ]);
    }

    public void SetCovered(bool covered)
    {
        _host.IsVisible = !covered;
        if (!covered) _host.Focus2();
    }

    public override void OnShown() => DispatcherTimer.RunOnce(() => _host.Focus2(), TimeSpan.FromMilliseconds(200));

    public override void OnClosed()
    {
        if (L.Window.CursorMode) L.Window.ToggleCursor();
    }
}
