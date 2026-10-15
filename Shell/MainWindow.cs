using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core;
using Lounge.Core.Audio;
using Lounge.Core.Input;
using Lounge.Core.Launch;
using Lounge.Core.Media;
using Lounge.Core.Settings;
using Lounge.Media;
using Lounge.Platform;
using Lounge.Screens;

namespace Lounge.Shell;

/// <summary>The one window. It scales a 1080-line design grid to any screen, stacks the
/// layers (backdrop, screens, status, panels, keyboard, dialogs, toasts, overlays),
/// routes controller actions to whatever is on top, and decides when Lounge is listening
/// at all (dormant while an app it launched is in front).</summary>
public sealed class MainWindow : Window
{
    private readonly LayoutTransformControl _scaler = new();
    private readonly Grid _root = new() { ClipToBounds = true };
    private readonly Backdrop _backdrop = new();
    private readonly Grid _screens = new();
    private readonly StatusBar _status = new();
    private readonly VolumeHud _volume = new();
    private readonly Grid _overlays = new();
    private readonly ToastHost _toasts = new();
    private readonly ControllerOverlay _padOverlay = new();
    private readonly IdleDim _dim = new();
    private readonly List<Screen> _stack = new();
    private readonly List<Control> _overlayStack = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _pump;
    private readonly DispatcherTimer _watch;
    private long _lastInput;
    private long _keyboardUntil;
    private bool _cursorMode;
    private bool _exiting;
    private readonly bool _windowed;
    private IntPtr _hwnd;

    public bool CursorMode => _cursorMode;
    public Screen? Top => _stack.Count > 0 ? _stack[^1] : null;
    public double DesignWidth => _root.Width;
    public double DesignHeight => _root.Height;
    public double Scale { get; private set; } = 1;
    public Backdrop Backdrop => _backdrop;

    /// <summary>Set for self-checks: open without taking the foreground.</summary>
    public static bool NoActivate { get; set; }

    public MainWindow(bool windowed = false)
    {
        ShowActivated = !NoActivate;
        _windowed = windowed;
        Title = Product.Name;
        Background = Brushes.Black;
        FontFamily = T.Font;
        SystemDecorations = windowed ? SystemDecorations.Full : SystemDecorations.None;
        WindowState = windowed ? WindowState.Normal : WindowState.FullScreen;
        if (windowed) { Width = 1280; Height = 720; }
        RenderOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Lounge/Assets/Lounge.ico"))); } catch (Exception) { }

        _root.Children.Add(_backdrop);
        _root.Children.Add(_screens);
        _root.Children.Add(_status);
        _root.Children.Add(_volume);
        _root.Children.Add(_overlays);
        _root.Children.Add(_toasts);
        _root.Children.Add(_padOverlay);
        _root.Children.Add(_dim);
        _scaler.Child = _root;
        Content = _scaler;

        Motion.Attach(this);
        SizeChanged += (_, _) => Relayout();
        Relayout();

        KeyDown += OnKey;
        PointerMoved += (_, _) => { if (_pointerHidden) { _pointerHidden = false; Cursor = Cursor.Default; } };
        Opened += (_, _) =>
        {
            _hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (!NoActivate) Win32.BringToFront(_hwnd);
        };

        // Not the (interval, priority, callback) constructor: that one starts the timer.
        _pump = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
        _pump.Tick += (_, _) => Pump();
        _watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _watch.Tick += (_, _) => Watch();
        if (!L.Preview)
        {
            Wire();
            _pump.Start();
            _watch.Start();
        }
        Closing += (_, e) => { if (!_exiting) { e.Cancel = true; Exit(); } };
    }

    /// <summary>Connects the window to input, settings and the player. The app does this
    /// at startup; headless checks call it to drive the real flows without timers.</summary>
    public void Wire()
    {
        L.Router.Fired += HandleAction;
        L.Router.ConnectionChanged += OnPadConnection;
        L.Router.Activity += () =>
        {
            NoteInput();
            // On top but not focused (a click went to another window): the controller wakes it.
            if (L.Dormant.Mode == ShellMode.Background && KeepsOnTop) ComeBack();
        };
        L.Dormant.ModeChanged += _ => { UpdateContext(); UpdateAmbient(); };
        L.Dormant.LaunchedAppExited += () => { ComeBack(); Toast("Welcome back", "home"); };
        // A website's icon arrived: show it on its tile.
        SiteIcons.Updated += () => Dispatcher.UIThread.Post(() => { if (Top is HomeScreen h) h.Rebuild(); });
        L.Settings.Changed += OnSetting;
        L.Player.StateChanged += _ => Dispatcher.UIThread.Post(() => { UpdateAmbient(); UpdateNowPlaying(); });
    }

    public IReadOnlyList<Control> Overlays => _overlayStack;

    // ------------------------------------------------------------------ layout

    private void Relayout()
    {
        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0) size = new Size(1920, 1080);
        var uiScale = L.Settings?.Double(K.UiScale) ?? 1.0;
        var designH = 1080 / Math.Clamp(uiScale, 0.5, 2);
        var designW = Math.Max(1280, size.Width / size.Height * designH);
        _root.Width = designW;
        _root.Height = designH;
        Scale = size.Height / designH;
        _scaler.LayoutTransform = new ScaleTransform(Scale, Scale);
    }

    /// <summary>Device pixels per design unit, for drawing at native resolution (subtitles).</summary>
    public double PixelScale => Scale * (RenderScaling > 0 ? RenderScaling : 1);

    // ------------------------------------------------------------------ navigation

    public void Push(Screen screen)
    {
        var old = Top;
        _stack.Add(screen);
        _screens.Children.Add(screen);
        Transition(screen, old, forward: true);
        old?.OnHidden();
        screen.OnShown();
        AfterNavigation();
    }

    /// <summary>Leaves the top screen. False at the root (the caller may offer to exit).</summary>
    public bool Pop()
    {
        if (_stack.Count <= 1) return false;
        var leaving = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        var now = Top!;
        now.IsVisible = true;
        Transition(now, leaving, forward: false);
        leaving.OnHidden();
        leaving.OnClosed();
        now.OnShown();
        AfterNavigation();
        return true;
    }

    public void GoHome()
    {
        CloseOverlays();
        if (_stack.Count == 1) { Top!.OnShown(); return; }
        L.Play(UiSound.Home);
        while (_stack.Count > 2)
        {
            var s = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            _screens.Children.Remove(s);
            s.OnHidden();
            s.OnClosed();
        }
        Pop();
    }

    public T? Find<T>() where T : Screen => _stack.OfType<T>().LastOrDefault();

    public void Remove(Screen s)
    {
        if (s == Top) { Pop(); return; }
        if (_stack.Remove(s)) { _screens.Children.Remove(s); s.OnClosed(); }
    }

    private void Transition(Screen incoming, Screen? outgoing, bool forward)
    {
        var tin = new ScaleTransform(1, 1);
        incoming.RenderTransform = tin;
        incoming.RenderTransformOrigin = RelativePoint.Center;
        incoming.Opacity = 0;
        var from = forward ? 1.04 : 0.97;
        new Spring(0, v =>
        {
            incoming.Opacity = Math.Clamp(v, 0, 1);
            var s = from + (1 - from) * v;
            tin.ScaleX = tin.ScaleY = s;
        }, 260, 28).To(1);
        if (outgoing is null) return;
        var tout = new ScaleTransform(1, 1);
        outgoing.RenderTransform = tout;
        outgoing.RenderTransformOrigin = RelativePoint.Center;
        var to = forward ? 0.96 : 1.04;
        var removeAfter = !forward;
        new Spring(1, v =>
        {
            outgoing.Opacity = Math.Clamp(v, 0, 1);
            var s = to + (1 - to) * v;
            tout.ScaleX = tout.ScaleY = s;
            if (v < 0.02)
            {
                outgoing.IsVisible = false;
                if (removeAfter) _screens.Children.Remove(outgoing);
            }
        }, 260, 28).To(0);
    }

    private void AfterNavigation()
    {
        var top = Top;
        _backdrop.IsVisible = top?.ShowsBackdrop ?? true;
        _status.IsVisible = top?.ShowsChrome ?? true;
        UpdateContext();
        UpdateAmbient();
        UpdateNowPlaying();
    }

    // ------------------------------------------------------------------ overlays

    public void AddOverlay(Control layer)
    {
        _overlayStack.Add(layer);
        _overlays.Children.Add(layer);
        CoverNative();
        UpdateContext();
    }

    public void RemoveOverlay(Control layer)
    {
        _overlayStack.Remove(layer);
        _overlays.Children.Remove(layer);
        CoverNative();
        UpdateContext();
    }

    /// <summary>Takes a closing layer out of the input stack now (the next press goes to
    /// whatever is underneath) and out of the visual tree once its fade has played.</summary>
    public void DismissOverlay(Control layer, int fadeMs = 240)
    {
        _overlayStack.Remove(layer);
        CoverNative();
        UpdateContext();
        DispatcherTimer.RunOnce(() => _overlays.Children.Remove(layer), TimeSpan.FromMilliseconds(fadeMs));
    }

    /// <summary>A web page is a native window drawn above everything Avalonia paints, so
    /// it steps aside while a menu, dialog or keyboard is open over it.</summary>
    private void CoverNative()
    {
        if (Top is WebScreen web) web.SetCovered(_overlayStack.Count > 0);
    }

    public void CloseOverlays()
    {
        foreach (var o in _overlayStack.ToList())
        {
            switch (o)
            {
                case ModalLayer m: m.Close(-1); break;
                case SettingsPanel sp: sp.Close(); break;
                default: RemoveOverlay(o); break;
            }
        }
    }

    private IInputLayer? TopLayer => _overlayStack.OfType<IInputLayer>().LastOrDefault() ?? Top;

    public void Toast(string message, string icon = "info", double seconds = 3.2) => _toasts.Show(message, icon, seconds);

    public void Ask(string title, string message, string[] buttons, int cancelIndex, Action<int> done, string icon = "info")
    {
        L.Play(UiSound.Notify);
        AddOverlay(ModalLayer.Dialog(title, message, buttons, cancelIndex, done, icon));
    }

    public Task<int> AskAsync(string title, string message, string[] buttons, int cancelIndex, string icon = "info")
    {
        var tcs = new TaskCompletionSource<int>();
        Ask(title, message, buttons, cancelIndex, i => tcs.TrySetResult(i), icon);
        return tcs.Task;
    }

    /// <summary>An options menu. Each item runs its own action when chosen.</summary>
    public void Menu(string title, IReadOnlyList<MenuEntry> items)
    {
        L.Play(UiSound.Open);
        AddOverlay(ModalLayer.Menu(title, items, i =>
        {
            if (i >= 0 && i < items.Count) Dispatcher.UIThread.Post(() => items[i].Run?.Invoke());
        }));
    }

    public void Keyboard(KeyboardRequest request, Action<string?> done)
    {
        L.Play(UiSound.Open);
        AddOverlay(new OnScreenKeyboard(request, done));
    }

    public Task<string?> KeyboardAsync(KeyboardRequest request)
    {
        var tcs = new TaskCompletionSource<string?>();
        Keyboard(request, r => tcs.TrySetResult(r));
        return tcs.Task;
    }

    public void OpenSettings(string? section = null)
    {
        if (_overlayStack.OfType<SettingsPanel>().FirstOrDefault() is { } open) { open.Close(); return; }
        L.Play(UiSound.Open);
        AddOverlay(new SettingsPanel(section));
    }

    // ------------------------------------------------------------------ input

    private void Pump()
    {
        var now = _clock.ElapsedMilliseconds;
        var state = L.Pad.Read(now);
        L.Router.Update(state, now);
    }

    private void NoteInput()
    {
        _lastInput = _clock.ElapsedMilliseconds;
        // The mouse pointer hides while the controller is in use and returns when the mouse moves.
        if (!_pointerHidden && !_cursorMode && !L.Preview)
        {
            _pointerHidden = true;
            Cursor = new Cursor(StandardCursorType.None);
        }
    }

    private bool _pointerHidden;

    private void OnPadConnection(bool connected)
    {
        var watching = Top is PlayerScreen;
        _padOverlay.Set(!connected && _clock.ElapsedMilliseconds > _keyboardUntil && L.Dormant.Mode == ShellMode.Active);
        if (!connected && watching && L.Player.IsPlaying) L.Player.Pause();
        if (connected) L.Rumble(0.3f, 80);
    }

    public void UpdateContext()
    {
        var screenContext = TopLayer?.Context ?? "Shell";
        L.Router.Context = _cursorMode ? "Cursor" : DormantState.ContextFor(L.Dormant.Mode, screenContext);
    }

    private void HandleAction(ActionEvent e)
    {
        if (e.Phase == ActionPhase.Pressed) NoteInput();
        if (_dim.Active)
        {
            if (e.Phase == ActionPhase.Pressed) { _dim.Set(false); UpdateAmbient(); }
            return;
        }

        if (L.Dormant.Mode != ShellMode.Active)
        {
            if (e.Action == Act.Return && e.Phase == ActionPhase.Pressed) ReturnFromApp();
            else if (_cursorMode) HandleCursor(e);
            else if (e.Action == Act.CursorToggle && e.Phase == ActionPhase.Pressed && LaunchedTileAllowsMouse()) ToggleCursor();
            return;
        }

        if (_cursorMode && HandleCursor(e)) return;

        // Volume works everywhere, even over dialogs.
        if (e.Action is Act.VolumeUp or Act.VolumeDown && e.Phase != ActionPhase.Released && TopLayer is not OnScreenKeyboard)
        {
            if (TopLayer?.Handle(e) == true) return;
            ChangeVolume(e.Action == Act.VolumeUp ? 1 : -1);
            return;
        }

        if (TopLayer?.Handle(e) == true) return;
        if (e.Phase != ActionPhase.Pressed) return;
        switch (e.Action)
        {
            case Act.Home: GoHome(); break;
            case Act.Settings: OpenSettings(); break;
            case Act.CursorToggle: ToggleCursor(); break;
            case Act.Mute:
                L.Volume.Muted = !L.Volume.Muted;
                _volume.Show(L.Volume.Level, L.Volume.Muted);
                break;
            case Act.Back:
                if (_overlayStack.Count == 0 && _stack.Count == 1) AskExit();
                break;
        }
    }

    private void ChangeVolume(int direction)
    {
        var step = L.Settings.Int(K.VolumeStep);
        var level = L.Volume.Step(direction * step);
        if (level < 0) { Toast("No sound output is available.", "mute"); return; }
        L.Play(UiSound.Move);
        _volume.Show(level, L.Volume.Muted);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (!L.Settings.Bool(K.KeyboardFallback) || L.Preview) return;
        string? action = e.Key switch
        {
            Key.Up => Act.Up, Key.Down => Act.Down, Key.Left => Act.Left, Key.Right => Act.Right,
            Key.Enter => Act.Accept, Key.Escape or Key.Back when TopLayer is not OnScreenKeyboard => Act.Back,
            Key.Escape => Act.Back,
            Key.Back => Act.KeyBackspace,
            Key.Space => TopLayer is PlayerScreen ? Act.PlayPause : TopLayer is OnScreenKeyboard ? Act.KeySpace : Act.Accept,
            Key.F1 => Act.Settings, Key.Apps or Key.F2 => Act.ContextMenu, Key.Home => Act.Home,
            Key.PageUp => Act.PageUp, Key.PageDown => Act.PageDown,
            Key.MediaPlayPause => Act.PlayPause, Key.MediaNextTrack => Act.NextItem, Key.MediaPreviousTrack => Act.PrevItem,
            Key.Tab => Act.Tertiary, Key.Delete => Act.Secondary,
            _ => null,
        };
        if (TopLayer is OnScreenKeyboard osk && e.KeySymbol is { Length: 1 } sym && !char.IsControl(sym[0]))
        {
            // A real keyboard types directly into the controller keyboard.
            osk.TypeFromKeyboard(sym);
            e.Handled = true;
            return;
        }
        if (action is null) return;
        e.Handled = true;
        _keyboardUntil = _clock.ElapsedMilliseconds + 60_000;
        _padOverlay.Set(false);
        HandleAction(new ActionEvent(action, ActionPhase.Pressed));
    }

    // ------------------------------------------------------------------ controller mouse

    public void ToggleCursor()
    {
        _cursorMode = !_cursorMode;
        Cursor = _cursorMode ? Cursor.Default : new Cursor(StandardCursorType.None);
        Toast(_cursorMode ? "Controller mouse on. Hold View + B to turn it off." : "Controller mouse off", "mouse", 2.2);
        L.Rumble(0.25f, 60);
        UpdateContext();
    }

    /// <summary>Any app that is not a game can be driven with the controller mouse (hold
    /// View + B); games keep every button for themselves.</summary>
    private bool LaunchedTileAllowsMouse() =>
        L.Dormant.LaunchedTileId is { } id && L.Tiles.Find(id) is { IsGame: false, Kind: not TileKind.Cogas };

    private bool HandleCursor(ActionEvent e)
    {
        var speed = L.Settings.Int(K.CursorSpeed);
        switch (e.Action)
        {
            case Act.CursorMove when e.Phase is ActionPhase.Pressed or ActionPhase.Changed:
                var accel = Math.Pow(e.Value, 1.6) * speed * 3.2 / Math.Max(e.Value, 0.0001);
                Mouse.MoveBy(e.X * accel, -e.Y * accel);
                return true;
            case Act.CursorScroll when e.Phase is ActionPhase.Pressed or ActionPhase.Changed:
                if (Math.Abs(e.Y) > Math.Abs(e.X)) Mouse.Scroll((int)(e.Y * 60));
                else Mouse.Scroll((int)(e.X * 60), horizontal: true);
                return true;
            case Act.CursorClick when e.Phase == ActionPhase.Pressed: Mouse.Click(); return true;
            case Act.CursorRightClick when e.Phase == ActionPhase.Pressed: Mouse.Click(right: true); return true;
            case Act.CursorBack when e.Phase == ActionPhase.Pressed: Mouse.Key(Mouse.VK_BROWSER_BACK); return true;
            case Act.CursorKeyboard when e.Phase == ActionPhase.Pressed:
                _cursorMode = false;
                UpdateContext();
                Keyboard(new KeyboardRequest("Type into the page", SelectAll: false), text =>
                {
                    _cursorMode = true;
                    UpdateContext();
                    if (!string.IsNullOrEmpty(text)) DispatcherTimer.RunOnce(() => Mouse.Type(text), TimeSpan.FromMilliseconds(250));
                });
                return true;
            case Act.CursorToggle when e.Phase == ActionPhase.Pressed: ToggleCursor(); return true;
            case Act.Home when e.Phase == ActionPhase.Pressed:
                ToggleCursor();
                if (L.Dormant.Mode != ShellMode.Active) ReturnFromApp(); else GoHome();
                return true;
            case Act.VolumeUp or Act.VolumeDown when e.Phase is ActionPhase.Pressed or ActionPhase.Repeated:
                ChangeVolume(e.Action == Act.VolumeUp ? 1 : -1);
                return true;
        }
        return e.Action.StartsWith("Cursor.");
    }

    // ------------------------------------------------------------------ apps and dormancy

    public void OnAppLaunched(Tile tile)
    {
        // The app comes to the front now: Lounge stays off the top while it starts (then
        // the dormant mode keeps it off while the app runs).
        _handOverUntil = _clock.ElapsedMilliseconds + 10_000;
        Topmost = false;
        L.Sound.SetAmbient(false);
        if (L.Player.IsPlaying) L.Player.Pause();
        var mouse = !tile.IsGame && tile.Kind != TileKind.Cogas;
        Toast(mouse
            ? $"Opening {tile.Name}. Hold View + B for the controller mouse; hold View + Menu to come back."
            : $"Opening {tile.Name}. Hold View + Menu to come back.", "external", 5);
        // Apps that don't know controllers start with the controller mouse on.
        if (mouse && tile.AllowMouse && !_cursorMode) DispatcherTimer.RunOnce(() => { if (L.Dormant.HasLaunchedApp && !_cursorMode) ToggleCursor(); }, TimeSpan.FromSeconds(1));
        if (L.Settings.Bool(K.MinimizeOnLaunch) && !_windowed)
            DispatcherTimer.RunOnce(() => { if (L.Dormant.HasLaunchedApp) WindowState = WindowState.Minimized; }, TimeSpan.FromSeconds(3));
    }

    private void Watch()
    {
        var inFront = _hwnd != IntPtr.Zero && Win32.ForegroundWindow() == _hwnd;
        var pid = L.Dormant.LaunchedPid;
        var alive = pid is { } p && Win32.IsProcessAlive(p);
        L.Dormant.Observe(inFront, alive);
        // While Lounge is in charge it stays on top and full screen (Windows can knock a
        // full-screen window back to normal when another app takes the front). Once an app
        // is launched from Lounge, Lounge steps aside so that app can be in front.
        var onTop = KeepsOnTop;
        if (Topmost != onTop) Topmost = onTop;
        if ((onTop || inFront) && !_windowed && L.Dormant.Mode != ShellMode.Dormant && WindowState != WindowState.FullScreen)
            WindowState = WindowState.FullScreen;

        var idleMinutes = L.Settings.Int(K.IdleDimMinutes);
        var idleFor = _clock.ElapsedMilliseconds - _lastInput;
        var watching = Top is PlayerScreen && L.Player.IsPlaying;
        if (idleMinutes > 0 && !_dim.Active && !watching && L.Dormant.Mode == ShellMode.Active && idleFor > idleMinutes * 60_000L)
            _dim.Set(true);
    }

    private long _handOverUntil;

    /// <summary>Lounge stays on top and full screen unless an app launched from it is
    /// running (or just starting), Lounge is windowed, or the setting is off.</summary>
    private bool KeepsOnTop =>
        !_windowed && L.Settings.Bool(K.AlwaysOnTop) && L.Dormant.Mode != ShellMode.Dormant &&
        _clock.ElapsedMilliseconds >= _handOverUntil;

    private void ReturnFromApp()
    {
        var tileId = L.Dormant.LaunchedTileId;
        var tile = tileId is null ? null : L.Tiles.Find(tileId);
        var behaviour = tile?.OnReturn is { } r && r != ReturnBehaviour.Default ? r : L.Settings.String(K.OnReturn) switch
        {
            "close" => ReturnBehaviour.Close, "ask" => ReturnBehaviour.Ask, _ => ReturnBehaviour.Switch,
        };
        var pid = L.Dormant.LaunchedPid;
        _handOverUntil = 0;
        ComeBack();
        L.Rumble(0.4f, 90);
        switch (behaviour)
        {
            case ReturnBehaviour.Close when pid is { } p:
                Win32.CloseProcess(p);
                L.Dormant.Returned(appClosed: true);
                break;
            case ReturnBehaviour.Ask when pid is { } p2:
                Ask($"Close {tile?.Name ?? "the app"}?", "It can keep running in the background, or close now.", ["Keep running", "Close it"], 0,
                    i => { if (i == 1) { Win32.CloseProcess(p2); L.Dormant.Returned(true); } }, "external");
                break;
            default:
                L.Dormant.Returned(appClosed: false);
                break;
        }
    }

    private void ComeBack()
    {
        if (!_windowed) WindowState = WindowState.FullScreen;
        else if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Win32.BringToFront(_hwnd);
        Activate();
        UpdateAmbient();
    }

    /// <summary>Brought forward by a second launch (the agent's "Yes").</summary>
    public void OnActivated(string[] args)
    {
        ComeBack();
        HandleArgs(args);
    }

    public void HandleArgs(string[] args)
    {
        var play = Array.IndexOf(args, "--play");
        if (play >= 0 && play + 1 < args.Length) Playback.Open(args[play + 1]);
        var web = Array.IndexOf(args, "--web");
        if (web >= 0 && web + 1 < args.Length)
            Push(new WebScreen(L.Tiles.All.FirstOrDefault(t => t.IsWeb && t.Target == args[web + 1])?.Clone()
                ?? new Lounge.Core.Launch.Tile { Kind = Lounge.Core.Launch.TileKind.Web, Name = "Web check", Target = args[web + 1], AllowMouse = true }));
        if (args.Contains("--web-apps")) Push(new AppsScreen(web: true));
        var files = Array.IndexOf(args, "--files");
        if (files >= 0 && files + 1 < args.Length) Push(new FilesScreen(args[files + 1]));
        var lone = args.FirstOrDefault(a => !a.StartsWith("--") && (File.Exists(a) || Directory.Exists(a)));
        if (lone is not null && play < 0)
        {
            if (Directory.Exists(lone)) Push(new FilesScreen(lone));
            else if (MediaKinds.IsPlayable(lone)) Playback.Open(lone);
        }
    }

    // ------------------------------------------------------------------ ambient, now playing

    public void UpdateAmbient()
    {
        if (L.Sound is null || L.Settings is null) return;
        var playing = L.Player.IsPlaying;
        var want = L.Settings.Bool(K.Ambient) && L.Dormant.Mode == ShellMode.Active && (Top?.WantsAmbient ?? true) && !playing;
        L.Sound.SetAmbient(want);
    }

    private void UpdateNowPlaying()
    {
        if (L.NowPlayingKind == MediaKind.Audio && L.Player.State is PlayerState.Playing or PlayerState.Paused && Top is not NowPlayingScreen)
            _status.SetNowPlaying(Playback.NowPlayingLine());
        else _status.SetNowPlaying(null);
    }

    // ------------------------------------------------------------------ settings

    private void OnSetting(string key)
    {
        switch (key)
        {
            case K.Theme or K.Accent:
                ApplyTheme(rebuild: true);
                break;
            case K.UiScale:
                Relayout();
                break;
            case var w when w == K.WallpaperFor(T.Def.Id):
                _backdrop.SetWallpaper(L.Settings.String(w));
                break;
            case K.ReduceEffects or K.AnimationSpeed:
                App.ApplyMotion();
                _backdrop.Restyle();
                break;
            case K.Ambient or K.AmbientSource or K.AmbientFolder or K.AmbientVolume or K.AmbientFadeMs or K.AmbientDelaySeconds or K.Sounds or K.SoundVolume:
                App.ConfigureSound();
                UpdateAmbient();
                break;
            case K.ShowClock or K.Clock24h:
                _status.Tick();
                break;
            case K.HoldMs or K.ReturnHoldMs or K.RepeatDelayMs or K.RepeatRateMs or K.Deadzone or K.TriggerThreshold or K.UseGuide:
                L.ApplyRouterOptions();
                break;
            case K.AgentEnabled:
                App.RegisterAgent();
                break;
            case K.LaunchAtLogin:
                App.RegisterLounge();
                break;
            case K.VideoRoots or K.MusicRoots:
                LibraryService.ScanSoon();
                break;
            case K.PlayerVolume:
                L.Player.Volume = (float)L.Settings.Double(K.PlayerVolume);
                break;
        }
        if (key.StartsWith("sub_")) L.Player.Subtitles.SetStyle(L.SubStyle());
    }

    /// <summary>Applies the theme; a rebuild recreates the screens so every brush changes.</summary>
    public void ApplyTheme(bool rebuild)
    {
        T.Apply(Lounge.Core.Themes.Themes.Get(L.Settings.String(K.Theme)), L.Settings.String(K.Accent));
        FontFamily = T.Font;
        _backdrop.Restyle();
        _backdrop.SetWallpaper(L.Settings.String(K.WallpaperFor(T.Def.Id)));
        App.ConfigureSound();
        if (!rebuild) return;
        var settingsOpen = _overlayStack.OfType<SettingsPanel>().FirstOrDefault();
        var section = settingsOpen?.Section;
        foreach (var o in _overlayStack.ToList()) RemoveOverlay(o);
        var keep = _stack.Skip(1).Where(s => s is PlayerScreen or NowPlayingScreen).ToList();
        foreach (var s in _stack) if (!keep.Contains(s)) { s.OnClosed(); _screens.Children.Remove(s); }
        _stack.Clear();
        var home = new HomeScreen();
        _stack.Add(home);
        _screens.Children.Insert(0, home);
        foreach (var k in keep) { _stack.Add(k); k.IsVisible = false; }
        if (keep.Count > 0) keep[^1].IsVisible = true; else home.OnShown();
        AfterNavigation();
        if (section is not null) AddOverlay(new SettingsPanel(section));
    }

    // ------------------------------------------------------------------ exit

    /// <summary>B at the root of Home. One press never closes Lounge: it opens the Power
    /// menu, and leaving is a choice made there.</summary>
    private void AskExit() => Lounge.Shell.Launcher.ShowPowerMenu();

    public void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _pump.Stop();
        _watch.Stop();
        try
        {
            Playback.RememberCurrent();
            L.History.Save();
            L.Keymap.Save(AppPaths.Keymap);
        }
        catch (IOException) { }
        L.Sound.SetAmbient(false);
        L.Player.Close();
        Close();
        (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
