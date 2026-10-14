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

/// <summary>A streaming site inside Lounge. The page reads the controller itself (YouTube
/// TV is built for it); Lounge only listens for hold View (home), View (options) and
/// View + B (controller mouse). If the page can't play protected video here, Lounge
/// suggests the full browser.</summary>
public sealed class WebScreen : Screen
{
    private readonly Tile _tile;
    private readonly WebViewHost _host;
    private readonly Border _loading;
    private bool _drmChecked;

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
        _host.Ready += c =>
        {
            c.CoreWebView2.NavigationCompleted += (_, a) => Dispatcher.UIThread.Post(() =>
            {
                _loading.IsVisible = false;
                if (!a.IsSuccess) L.Window.Toast($"{_tile.Name} didn't load ({a.WebErrorStatus}).", "warning");
                else CheckDrm(c);
                _host.Focus2();
            });
            c.CoreWebView2.NewWindowRequested += (_, a) => { a.Handled = true; c.CoreWebView2.Navigate(a.Uri); };
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

    private async void CheckDrm(CoreWebView2Controller c)
    {
        if (_drmChecked || !L.Settings.Bool(K.DrmHint)) return;
        _drmChecked = true;
        const string probe = """
            (async () => { try {
              await navigator.requestMediaKeySystemAccess('com.widevine.alpha', [{ initDataTypes: ['cenc'],
                videoCapabilities: [{ contentType: 'video/mp4; codecs="avc1.42E01E"' }] }]);
              return 'ok'; } catch (e) { return 'blocked'; } })()
            """;
        try
        {
            var result = await c.CoreWebView2.ExecuteScriptAsync(probe);
            if (result.Contains("blocked") && _tile.AllowMouse)
                L.Window.Toast($"{_tile.Name} may not play protected video here. Press View and choose \"Open in full browser\" if it won't start.", "web", 7);
        }
        catch (Exception) { }
    }

    public override bool Handle(ActionEvent e)
    {
        if (e.Phase != ActionPhase.Pressed) return true;
        switch (e.Action)
        {
            case Act.ContextMenu:
                Options();
                return true;
            case Act.Back:
                if (_host.Controller?.CoreWebView2.CanGoBack == true) { _host.Controller.CoreWebView2.GoBack(); return true; }
                L.Window.Remove(this);
                return true;
        }
        // Everything else belongs to the page.
        return e.Action is not (Act.Home or Act.CursorToggle);
    }

    private void Options()
    {
        var c = _host.Controller;
        L.Window.Menu(_tile.Name,
        [
            new MenuEntry("Back to Lounge", "home", () => L.Window.Remove(this)),
            new MenuEntry("Controller mouse", "mouse", () => L.Window.ToggleCursor(), L.Window.CursorMode ? "On" : "Off"),
            new MenuEntry("Go back a page", "back", () => { if (c?.CoreWebView2.CanGoBack == true) c.CoreWebView2.GoBack(); }),
            new MenuEntry("Reload", "refresh", () => c?.CoreWebView2.Reload()),
            new MenuEntry("Zoom in", "plus", () => { if (c is not null) c.ZoomFactor = Math.Min(3, c.ZoomFactor + 0.1); }),
            new MenuEntry("Zoom out", "search", () => { if (c is not null) c.ZoomFactor = Math.Max(0.5, c.ZoomFactor - 0.1); }),
            new MenuEntry("Open in full browser", "external", () => { L.Window.Remove(this); Launcher.Launch(_tile.Clone().Also(t => t.OpenExternally = true)); }),
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
