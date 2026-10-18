using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Lounge.Core.Audio;
using Lounge.Core.Games;
using Lounge.Core.Input;
using Lounge.Shell;
using Screen = Lounge.Shell.Screen;

namespace Lounge.Screens;

/// <summary>Hellgate, Lounge's own Doom-style shooter. The game and its 320 x 200 picture
/// come from <see cref="HellgateGame"/> and <see cref="HellgateRenderer"/>; this screen
/// scales the picture up with hard pixels, lays the status bar under it, reads the
/// controller and keyboard, and plays the sounds.</summary>
public sealed class HellgateScreen : Screen
{
    private readonly HellgateGame _game = new(Environment.TickCount);
    private readonly HellgateRenderer _renderer = new();
    private readonly WriteableBitmap _bitmap = new(new PixelSize(HellgateRenderer.W, HellgateRenderer.H), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
    private readonly Image _view;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly HashSet<Key> _keys = new();
    private readonly TextBlock _health, _armor, _ammo, _weapon, _level, _kills, _message, _title, _prompt;
    private readonly Border _overlay;
    private double _last;
    private bool _alive = true;
    private double _deadAt = -1;

    public override string Title => "Hellgate";
    public override string Context => "Shell";
    public override bool WantsAmbient => false;
    public override bool ShowsChrome => false;
    public override bool ShowsBackdrop => false;

    private static readonly FontFamily Pixel = new($"avares://{typeof(HellgateScreen).Assembly.GetName().Name}/Assets/Fonts#Press Start 2P, fonts:Inter#Inter");

    private static TextBlock Label(double size, string color, string text = "") => new()
    {
        Text = text, FontFamily = Pixel, FontSize = size, Foreground = new SolidColorBrush(Color.Parse(color)),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
    };

    /// <param name="previewSeconds">Previews: start a game and walk this long before the first frame.</param>
    public HellgateScreen(double previewSeconds = 0)
    {
        Background = Brushes.Black;
        _game.BestLevel = (int)GameScores.Get("hellgate");
        RowDefinitions = new RowDefinitions("*,Auto");

        _view = new Image { Source = _bitmap, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(_view, BitmapInterpolationMode.None);
        Children.Add(_view);

        _message = Label(30, "#FFD319");
        _message.VerticalAlignment = VerticalAlignment.Top;
        _message.Margin = new Thickness(0, 60, 0, 0);
        Children.Add(_message);

        _title = Label(64, "#FF3A2A", "HELLGATE");
        _prompt = Label(22, "#FFFFFF");
        _overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
            Child = new StackPanel { Spacing = 34, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _prompt } },
        };
        Children.Add(_overlay);

        // The status bar: health, armour, ammo and weapon, level, kills.
        _health = Label(30, "#FF5050"); _armor = Label(30, "#50D060"); _ammo = Label(30, "#FFD319");
        _weapon = Label(18, "#C0C0C0"); _level = Label(22, "#FFFFFF"); _kills = Label(18, "#C0C0C0");
        Control Cell(string caption, params TextBlock[] values)
        {
            var s = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            s.Children.Add(Label(14, "#808080", caption));
            foreach (var v in values) s.Children.Add(v);
            return s;
        }
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), Height = 130, Background = new SolidColorBrush(Color.FromRgb(28, 24, 22)) };
        bar.Children.Add(Cell("HEALTH", _health));
        bar.Children.Add(Cell("ARMOR", _armor).Also(c => Grid.SetColumn(c, 1)));
        bar.Children.Add(Cell("AMMO", _ammo, _weapon).Also(c => Grid.SetColumn(c, 2)));
        bar.Children.Add(Cell("LEVEL", _level, _kills).Also(c => Grid.SetColumn(c, 3)));
        Grid.SetRow(bar, 1);
        Children.Add(bar);

        if (previewSeconds > 0)
        {
            _game.Start();
            // Stand at the start looking down the level, firing at the very end for the flash.
            for (var t = 0.0; t < previewSeconds; t += 1 / 60.0) _game.Update(1 / 60f, new HellgateInput(0, 0, 0, t > previewSeconds - 0.05, false, false, false));
        }
        Present();
        if (!L.Preview) _ = Loop();
    }

    public override void OnShown()
    {
        if (L.Preview) return;
        L.Window.AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        L.Window.AddHandler(KeyUpEvent, OnKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public override void OnHidden()
    {
        L.Window.RemoveHandler(KeyDownEvent, OnKeyDown);
        L.Window.RemoveHandler(KeyUpEvent, OnKeyUp);
        _keys.Clear();
    }

    public override void OnClosed()
    {
        _alive = false;
        OnHidden();
        GameScores.Set("hellgate", _game.BestLevel);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) => _keys.Add(e.Key);
    private void OnKeyUp(object? sender, KeyEventArgs e) => _keys.Remove(e.Key);

    private async Task Loop()
    {
        while (_alive)
        {
            var tcs = new TaskCompletionSource();
            var top = TopLevel.GetTopLevel(this);
            if (top is null) { await Task.Delay(16); continue; }
            top.RequestAnimationFrame(_ => tcs.TrySetResult());
            await tcs.Task;
            if (!_alive) break;
            Frame();
        }
    }

    private void Frame()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = (float)Math.Clamp(now - _last, 0, 0.05);
        _last = now;
        if (L.Window.Overlays.Count > 0 || L.Window.Top != this) return;
        if (L.Dormant.Mode != Lounge.Core.Launch.ShellMode.Active && _game.Phase == HellgatePhase.Playing) { Pause(); return; }

        var wasDead = _game.Phase == HellgatePhase.Dead;
        _game.Update(dt, ReadInput(now));
        if (_game.Phase == HellgatePhase.Dead && !wasDead) _deadAt = now;
        foreach (var ev in _game.Events) Feedback(ev);
        _game.Events.Clear();
        Present();
    }

    private HellgateInput ReadInput(double now)
    {
        var pad = L.Router.Last;
        float Dz(float v) => MathF.Abs(v) < 0.18f ? 0 : v;
        float forward = Dz(pad.LY), strafe = Dz(pad.LX), turn = Dz(pad.RX);
        if (pad.IsDown(PadButton.DPadUp) || _keys.Contains(Key.W) || _keys.Contains(Key.Up)) forward = 1;
        if (pad.IsDown(PadButton.DPadDown) || _keys.Contains(Key.S) || _keys.Contains(Key.Down)) forward = -1;
        if (_keys.Contains(Key.A)) strafe = -1;
        if (_keys.Contains(Key.D)) strafe = 1;
        if (pad.IsDown(PadButton.DPadLeft) || _keys.Contains(Key.Left)) turn = -1;
        if (pad.IsDown(PadButton.DPadRight) || _keys.Contains(Key.Right)) turn = 1;
        var fire = pad.RightTrigger > 0.3f || _keys.Contains(Key.LeftCtrl) || _keys.Contains(Key.RightCtrl) || _keys.Contains(Key.Space);
        var use = pad.IsDown(PadButton.A) || _keys.Contains(Key.E);
        var next = pad.IsDown(PadButton.Y) || _keys.Contains(Key.Q) || _keys.Contains(Key.Tab);
        // After dying, a moment passes before A restarts, so a held button isn't taken as "again".
        var start = (pad.IsDown(PadButton.A) || _keys.Contains(Key.Enter) || _keys.Contains(Key.E)) && (_deadAt < 0 || now - _deadAt > 1.5);
        return new HellgateInput(forward, strafe, turn, fire, use, next, start);
    }

    private void Present()
    {
        _renderer.Draw(_game);
        using (var fb = _bitmap.Lock())
        unsafe
        {
            fixed (uint* src = _renderer.Pixels)
                for (var y = 0; y < HellgateRenderer.H; y++)
                    Buffer.MemoryCopy(src + y * HellgateRenderer.W, (byte*)fb.Address + y * fb.RowBytes, fb.RowBytes, HellgateRenderer.W * 4);
        }
        _view.InvalidateVisual();

        var g = _game;
        _health.Text = $"{g.Health}%";
        _armor.Text = $"{g.Armor}%";
        _ammo.Text = g.Weapon == 1 ? $"{g.Shells}" : $"{g.Bullets}";
        _weapon.Text = g.Weapon switch { 1 => "SHOTGUN", 2 => "CHAINGUN", _ => "PISTOL" };
        _level.Text = g.Level == 0 ? "-" : $"{g.Level}";
        _kills.Text = g.MonstersTotal == 0 ? "" : $"KILLS {g.Kills}/{g.MonstersTotal}";
        _message.Text = g.Phase == HellgatePhase.Playing ? g.Message ?? "" : "";

        var blink = (int)(_clock.Elapsed.TotalSeconds * 2) % 2 == 0;
        switch (g.Phase)
        {
            case HellgatePhase.Title:
                _overlay.IsVisible = true;
                _title.Text = "HELLGATE";
                _prompt.Text = (blink ? "PRESS A TO START" : " ") + "\n\nLEFT STICK MOVE   RIGHT STICK TURN\nRT FIRE   A OPEN   Y WEAPON\n\nFIND THE EXIT SWITCH ON EVERY LEVEL" +
                               (g.BestLevel > 1 ? $"\n\nBEST: LEVEL {g.BestLevel}" : "");
                break;
            case HellgatePhase.Dead:
                _overlay.IsVisible = true;
                _title.Text = "YOU DIED";
                _prompt.Text = $"LEVEL {g.Level}   KILLS {g.Kills}/{g.MonstersTotal}\n\n" + (blink ? "A TRY AGAIN   B LEAVE" : " ");
                break;
            case HellgatePhase.LevelDone:
                _overlay.IsVisible = true;
                _title.Text = $"LEVEL {g.Level} DONE";
                var pct = g.MonstersTotal == 0 ? 100 : g.Kills * 100 / g.MonstersTotal;
                _prompt.Text = $"KILLS {pct}%   TIME {(int)g.LevelTime / 60}:{(int)g.LevelTime % 60:00}\n\n" + (blink ? "A NEXT LEVEL" : " ");
                break;
            default:
                _overlay.IsVisible = false;
                break;
        }
    }

    private static void Feedback(HellgateEvent ev)
    {
        switch (ev)
        {
            case HellgateEvent.Shot: L.Play(UiSound.Toggle); break;
            case HellgateEvent.ShotgunBlast: L.Play(UiSound.Back); L.Rumble(0.5f, 90); break;
            case HellgateEvent.EnemyDown: L.Play(UiSound.Select); break;
            case HellgateEvent.PlayerHurt: L.Play(UiSound.Error); L.Rumble(0.7f, 160); break;
            case HellgateEvent.Pickup: L.Play(UiSound.Notify); break;
            case HellgateEvent.Weapon: L.Play(UiSound.Move); break;
            case HellgateEvent.DoorOpen: L.Play(UiSound.Open); break;
            case HellgateEvent.LevelDone: L.Play(UiSound.Home); break;
            case HellgateEvent.Died: L.Play(UiSound.Error); L.Rumble(1f, 400); break;
            case HellgateEvent.Empty: L.Play(UiSound.Move); break;
        }
    }

    private void Pause()
    {
        if (_game.Phase != HellgatePhase.Playing) return;
        L.Window.Menu("Paused",
        [
            new MenuEntry("Resume", "play", () => { }),
            new MenuEntry("Restart level", "refresh", () => _game.Start(_game.Level)),
            new MenuEntry("How to play", "info", () => L.Window.Ask("How to play",
                "Left stick moves and strafes, right stick turns (or the D-pad).\nRT fires. A opens doors and presses the exit switch. Y changes weapon.\nEvery level has an EXIT switch on a wall somewhere: find it and press A at it.",
                ["Got it"], 0, _ => { }, "gamepad")),
            new MenuEntry("Quit to Lounge", "close", () => L.Window.Remove(this), Danger: true),
        ]);
    }

    public override bool Handle(ActionEvent e)
    {
        if (e.Action is Act.Home or Act.Mute or Act.VolumeUp or Act.VolumeDown) return false;
        if (e.Phase != ActionPhase.Pressed) return true;
        switch (e.Action)
        {
            case Act.Settings or Act.ContextMenu:
                Pause();
                return true;
            case Act.Back when _game.Phase is HellgatePhase.Title or HellgatePhase.Dead:
                L.Play(UiSound.Back);
                L.Window.Remove(this);
                return true;
        }
        return true;   // the game reads the controller itself; nothing leaks to Lounge
    }
}
