using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Core.Audio;
using Lounge.Core.Games;
using Lounge.Core.Input;
using Lounge.Core.Launch;
using Lounge.Shell;
using Screen = Lounge.Shell.Screen;

namespace Lounge.Screens;

/// <summary>Starfall, Lounge's own bullet-hell shooter. A portrait playfield in the middle
/// with the scoreboard either side, like the arcade shooters it borrows from. The game
/// itself lives in <see cref="StarfallGame"/>; this screen reads the controller (and the
/// keyboard), steps the game every frame, draws it and plays its sounds.</summary>
public sealed class StarfallScreen : Screen
{
    private readonly StarfallGame _game = new(Environment.TickCount);
    private readonly StarfallGame _demo = new(42);   // the attract mode behind the title
    private readonly StarfallView _view;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly HashSet<Key> _keys = new();
    private double _last;
    private bool _alive = true;
    private double _gameOverAt = -1;

    public override string Title => "Starfall";
    public override string Context => "Shell";
    public override bool WantsAmbient => false;
    public override bool ShowsChrome => false;
    public override bool ShowsBackdrop => false;

    /// <param name="demoSeconds">Previews: play the attract mode this long before the first frame.</param>
    public StarfallScreen(double demoSeconds = 0)
    {
        Background = new SolidColorBrush(Color.FromRgb(5, 3, 14));
        _game.HiScore = GameScores.Get("starfall");
        _demo.Start();
        for (var t = 0.0; t < demoSeconds; t += 1 / 60.0) _demo.Update(1 / 60f, _demo.Autopilot());
        _view = new StarfallView(this);
        Children.Add(_view);
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
        SaveScore();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) => _keys.Add(e.Key);
    private void OnKeyUp(object? sender, KeyEventArgs e) => _keys.Remove(e.Key);

    internal StarfallGame Game => _game;
    internal StarfallGame Demo => _demo;
    internal double Now => _clock.Elapsed.TotalSeconds;
    internal bool ShowingDemo => _game.Phase == StarfallPhase.Title;

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
        var now = Now;
        var dt = (float)Math.Clamp(now - _last, 0, 0.05);
        _last = now;

        // Away from Lounge, or a menu over the game: hold everything.
        var away = L.Dormant.Mode != Lounge.Core.Launch.ShellMode.Active;
        if (L.Window.Overlays.Count > 0 || L.Window.Top != this) { _view.InvalidateVisual(); return; }
        if (away && _game.Phase == StarfallPhase.Playing) { Pause(); return; }

        if (ShowingDemo)
        {
            _demo.Update(dt, _demo.Autopilot());
            if (_demo.Phase == StarfallPhase.GameOver) { _demo.Start(); }
            _demo.Events.Clear();
        }
        var input = ReadInput();
        var wasOver = _game.Phase == StarfallPhase.GameOver;
        _game.Update(dt, input);
        if (_game.Phase == StarfallPhase.GameOver && !wasOver) { _gameOverAt = now; SaveScore(); }
        foreach (var ev in _game.Events) Feedback(ev);
        _game.Events.Clear();
        _view.InvalidateVisual();
    }

    private StarfallInput ReadInput()
    {
        var pad = L.Router.Last;
        float x = pad.LX, y = -pad.LY;
        if (MathF.Abs(x) < 0.2f) x = 0;
        if (MathF.Abs(y) < 0.2f) y = 0;
        if (pad.IsDown(PadButton.DPadLeft) || _keys.Contains(Key.Left)) x = -1;
        if (pad.IsDown(PadButton.DPadRight) || _keys.Contains(Key.Right)) x = 1;
        if (pad.IsDown(PadButton.DPadUp) || _keys.Contains(Key.Up)) y = -1;
        if (pad.IsDown(PadButton.DPadDown) || _keys.Contains(Key.Down)) y = 1;
        var focus = pad.RightTrigger > 0.3f || pad.LeftTrigger > 0.3f || pad.IsDown(PadButton.A) || _keys.Contains(Key.LeftShift) || _keys.Contains(Key.RightShift);
        var bomb = pad.IsDown(PadButton.B) || _keys.Contains(Key.X);
        // After a game over, a moment passes before A starts again, so a held focus button
        // doesn't throw the next game away.
        var start = (pad.IsDown(PadButton.A) || _keys.Contains(Key.Z) || _keys.Contains(Key.Enter) || _keys.Contains(Key.Space))
                    && (_gameOverAt < 0 || Now - _gameOverAt > 1.5);
        return new StarfallInput(x, y, focus, bomb && _game.Phase == StarfallPhase.Playing, start);
    }

    private static void Feedback(StarfallEvent ev)
    {
        switch (ev)
        {
            case StarfallEvent.BigExplosion: L.Play(UiSound.Back); L.Rumble(0.3f, 60); break;
            case StarfallEvent.PlayerHit: L.Play(UiSound.Error); L.Rumble(0.9f, 240); break;
            case StarfallEvent.Bomb: L.Play(UiSound.Open); L.Rumble(0.6f, 180); break;
            case StarfallEvent.PowerUp: L.Play(UiSound.Toggle); break;
            case StarfallEvent.ExtraLife: L.Play(UiSound.Notify); L.Rumble(0.3f, 80); break;
            case StarfallEvent.StageClear: L.Play(UiSound.Home); break;
            case StarfallEvent.BossWarning: L.Play(UiSound.Notify); L.Rumble(0.5f, 200); break;
            case StarfallEvent.GameOver: L.Play(UiSound.Error); break;
            case StarfallEvent.PhaseBreak: L.Play(UiSound.Select); L.Rumble(0.4f, 120); break;
            case StarfallEvent.Graze: L.Play(UiSound.Move); break;
        }
    }

    private void SaveScore() => GameScores.Set("starfall", _game.HiScore);

    private void Pause()
    {
        if (_game.Phase is not (StarfallPhase.Playing or StarfallPhase.StageClear)) return;
        L.Window.Menu("Paused",
        [
            new MenuEntry("Resume", "play", () => { }),
            new MenuEntry("Restart", "refresh", () => { _game.Start(); }),
            new MenuEntry("How to play", "info", () => L.Window.Ask("How to play",
                "Move with the stick or D-pad. Fire is automatic.\nHold RT (or A) to focus: slower, and your tiny hitbox shows.\nB drops a bomb that clears the screen.\nBullets that brush past score a graze. Stay near the top to pull in items.",
                ["Got it"], 0, _ => { }, "gamepad")),
            new MenuEntry("Quit to Lounge", "close", () => L.Window.Remove(this), Danger: true),
        ]);
    }

    public override bool Handle(ActionEvent e)
    {
        if (e.Action is Act.Home or Act.VolumeUp or Act.VolumeDown or Act.Mute) return false;
        if (e.Phase != ActionPhase.Pressed) return true;
        switch (e.Action)
        {
            case Act.Settings or Act.ContextMenu:
                Pause();
                return true;
            case Act.Back when _game.Phase is StarfallPhase.Title or StarfallPhase.GameOver:
                L.Play(UiSound.Back);
                L.Window.Remove(this);
                return true;
        }
        return true;   // the game reads the controller itself; nothing leaks to Lounge
    }
}

/// <summary>Draws Starfall: the playfield scaled to the screen's height, with the side
/// panels holding the score, lives, bombs, power and stage.</summary>
internal sealed class StarfallView : Control
{
    private readonly StarfallScreen _screen;
    private static readonly Color[] Palette =
    [
        Color.Parse("#FF2A6D"), Color.Parse("#05D9E8"), Color.Parse("#FFD319"), Color.Parse("#B967FF"),
        Color.Parse("#39FF14"), Color.Parse("#FF8C1A"), Color.Parse("#FFFFFF"),
    ];
    private static readonly IBrush[] Fill = Palette.Select(c => (IBrush)new SolidColorBrush(c).ToImmutable()).ToArray();
    private static readonly IBrush[] Glow = Palette.Select(c => (IBrush)new SolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B)).ToImmutable()).ToArray();
    private static readonly IBrush White = Brushes.White;
    private static readonly IBrush Faint = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)).ToImmutable();
    private static readonly IBrush Panel = new SolidColorBrush(Color.FromArgb(200, 12, 6, 30)).ToImmutable();
    private static readonly IBrush Field = new SolidColorBrush(Color.FromRgb(8, 4, 22)).ToImmutable();
    private static readonly IPen Edge = new Pen(new SolidColorBrush(Color.FromArgb(140, 5, 217, 232)), 3);
    private static readonly Typeface Pixel = new(new FontFamily($"avares://{typeof(StarfallView).Assembly.GetName().Name}/Assets/Fonts#Press Start 2P, fonts:Inter#Inter"));
    private static readonly Typeface Body = new(new FontFamily($"avares://{typeof(StarfallView).Assembly.GetName().Name}/Assets/Fonts#Pixelify Sans, fonts:Inter#Inter"));

    private static readonly Geometry Ship = Geometry.Parse("M 0,-16 L 5,-4 L 13,6 L 13,12 L 5,8 L 3,13 L -3,13 L -5,8 L -13,12 L -13,6 L -5,-4 Z");
    private static readonly Geometry Diamond = Geometry.Parse("M 0,-14 L 12,0 L 0,14 L -12,0 Z");
    private static readonly Geometry Chevron = Geometry.Parse("M -14,-8 L 0,10 L 14,-8 L 7,-8 L 0,1 L -7,-8 Z");
    private static readonly Geometry Hexagon = Geometry.Parse("M -13,-23 L 13,-23 L 26,0 L 13,23 L -13,23 L -26,0 Z");
    private static readonly Geometry Dart = Geometry.Parse("M 0,14 L 6,-12 L 0,-6 L -6,-12 Z");
    private static readonly Geometry BossHull = Geometry.Parse("M 0,-46 L 30,-30 L 60,-8 L 52,22 L 24,40 L 0,30 L -24,40 L -52,22 L -60,-8 L -30,-30 Z");

    private readonly (float X, float Y, float Speed, byte Size)[] _stars;

    public StarfallView(StarfallScreen screen)
    {
        _screen = screen;
        var rng = new Random(9);
        _stars = Enumerable.Range(0, 140).Select(_ => (rng.NextSingle() * StarfallGame.W, rng.NextSingle() * StarfallGame.H, 20 + rng.NextSingle() * 120, (byte)(rng.Next(3) == 0 ? 3 : 2))).ToArray();
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = Bounds;
        var game = _screen.ShowingDemo ? _screen.Demo : _screen.Game;
        var scale = bounds.Height * 0.96 / StarfallGame.H;
        var fieldW = StarfallGame.W * scale;
        var left = (bounds.Width - fieldW) / 2;
        var top = bounds.Height * 0.02;
        var field = new Rect(left, top, fieldW, StarfallGame.H * scale);

        ctx.FillRectangle(Panel, bounds);
        ctx.FillRectangle(Field, field);
        using (ctx.PushClip(field))
        using (ctx.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(left, top)))
            DrawField(ctx, game);
        ctx.DrawRectangle(Edge, field.Inflate(2));
        DrawPanels(ctx, bounds, field, game);
        DrawOverlay(ctx, field);
    }

    private void DrawField(DrawingContext ctx, StarfallGame g)
    {
        var t = (float)_screen.Now;
        foreach (var (x, y0, speed, size) in _stars)
        {
            var y = (y0 + t * speed) % StarfallGame.H;
            ctx.FillRectangle(speed > 100 ? White : Glow[1], new Rect(x, y, size, size));
        }

        foreach (var it in g.Items)
        {
            var (brush, s) = it.Kind switch { ItemKind.Power => (Fill[0], 9f), ItemKind.Point => (Fill[1], 7f), ItemKind.Bomb => (Fill[4], 10f), _ => (Fill[3], 11f) };
            ctx.FillRectangle(brush, new Rect(it.P.X - s, it.P.Y - s, s * 2, s * 2));
            ctx.FillRectangle(White, new Rect(it.P.X - s * 0.4, it.P.Y - s * 0.4, s * 0.8, s * 0.8));
        }

        foreach (var s in g.Shots) ctx.FillRectangle(Fill[1], new Rect(s.P.X - 2, s.P.Y - 9, 4, 18));

        foreach (var e in g.Enemies) DrawEnemy(ctx, e, t);

        // The player: hidden in blinks while invulnerable.
        if (g.Phase != StarfallPhase.GameOver && !(g.Invulnerable > 0 && (int)(t * 16) % 2 == 0))
        {
            using (ctx.PushTransform(Matrix.CreateTranslation(g.Player.X, g.Player.Y)))
            {
                ctx.DrawGeometry(Fill[0], new Pen(White, 2), Ship);
                ctx.FillRectangle(Fill[2], new Rect(-3, 10, 6, 4 + (int)(t * 30) % 4));   // engine flame
            }
        }

        var bullets = g.Bullets;
        var glow = bullets.Count < 700;
        foreach (var b in bullets)
        {
            var c = b.Color % Palette.Length;
            if (glow) ctx.DrawEllipse(Glow[c], null, new Point(b.P.X, b.P.Y), b.R * 1.9, b.R * 1.9);
            ctx.DrawEllipse(Fill[c], null, new Point(b.P.X, b.P.Y), b.R, b.R);
            ctx.DrawEllipse(White, null, new Point(b.P.X, b.P.Y), b.R * 0.5, b.R * 0.5);
        }

        foreach (var p in g.Particles)
        {
            var a = p.Life / p.MaxLife;
            var s = 2 + 4 * a;
            using (ctx.PushOpacity(a)) ctx.FillRectangle(Fill[p.Color % Palette.Length], new Rect(p.P.X - s / 2, p.P.Y - s / 2, s, s));
        }

        // Focus shows the true hitbox, the only part of the ship that counts.
        if (g.Focused && g.Phase == StarfallPhase.Playing)
        {
            ctx.DrawEllipse(null, new Pen(Fill[0], 2), new Point(g.Player.X, g.Player.Y), 9, 9);
            ctx.DrawEllipse(White, null, new Point(g.Player.X, g.Player.Y), StarfallGame.HitRadius + 1, StarfallGame.HitRadius + 1);
        }
        if (g.BombAge >= 0)
        {
            var r = g.BombAge * 900;
            using (ctx.PushOpacity(Math.Max(0, 1 - g.BombAge / 1.4)))
                ctx.DrawEllipse(null, new Pen(Fill[2], 16), new Point(g.Player.X, g.Player.Y), r, r);
        }

        if (g.Boss is { } boss)
        {
            ctx.FillRectangle(Glow[0], new Rect(20, 14, StarfallGame.W - 40, 10));
            ctx.FillRectangle(Fill[0], new Rect(20, 14, (StarfallGame.W - 40) * Math.Max(0, boss.Hp / boss.MaxHp), 10));
        }
    }

    private static void DrawEnemy(DrawingContext ctx, Enemy e, float t)
    {
        var (geo, brush) = e.Kind switch
        {
            EnemyKind.Drone => (Diamond, Fill[0]),
            EnemyKind.Swooper => (Chevron, Fill[1]),
            EnemyKind.Turret => (Hexagon, Fill[2]),
            EnemyKind.Spinner => (Hexagon, Fill[3]),
            EnemyKind.Sniper => (Dart, Fill[4]),
            _ => (BossHull, Fill[3]),
        };
        var spin = e.Kind == EnemyKind.Spinner ? Matrix.CreateRotation(t * 2.5) : Matrix.Identity;
        using (ctx.PushTransform(spin * Matrix.CreateTranslation(e.P.X, e.P.Y)))
        {
            using (ctx.PushOpacity(e.Invulnerable > 0 && (int)(t * 12) % 2 == 0 ? 0.4 : 1))
                ctx.DrawGeometry(brush, new Pen(White, e.Kind == EnemyKind.Boss ? 3 : 1.5), geo);
            if (e.Kind == EnemyKind.Boss)
            {
                var pulse = 10 + 4 * Math.Sin(t * 6);
                ctx.DrawEllipse(Fill[2], new Pen(White, 2), new Point(0, 0), pulse, pulse);
            }
        }
    }

    private void DrawPanels(DrawingContext ctx, Rect bounds, Rect field, StarfallGame g)
    {
        var lx = Math.Max(40, field.Left - 470);
        var rx = field.Right + 60;
        double y = field.Top + 30;
        Label(ctx, "STARFALL", lx, y, 34, Fill[2]);
        Label(ctx, "STARFALL", lx - 4, y - 4, 34, Fill[0]);
        y += 90;
        Stat("HI-SCORE", Math.Max(_screen.Game.HiScore, g == _screen.Game ? g.Score : 0).ToString("N0", CultureInfo.InvariantCulture));   // yours, even behind the demo
        Stat("SCORE", g.Score.ToString("N0", CultureInfo.InvariantCulture));
        Stat("STAGE", g.Stage == 0 ? "-" : g.Stage.ToString(CultureInfo.InvariantCulture));
        Stat("GRAZE", g.Graze.ToString(CultureInfo.InvariantCulture));

        y = field.Top + 120;
        Label(ctx, "PLAYER", rx, y, 20, Fill[1]);
        Pips(rx, y + 40, g.Lives, Fill[0]);
        Label(ctx, "BOMB", rx, y + 110, 20, Fill[1]);
        Pips(rx, y + 150, g.Bombs, Fill[4]);
        Label(ctx, "POWER", rx, y + 220, 20, Fill[1]);
        Label(ctx, g.Power >= 4 ? "MAX" : g.Power.ToString("0.00", CultureInfo.InvariantCulture), rx, y + 256, 28, White);
        Text(ctx, "Stick move  ·  RT focus\nB bomb  ·  Menu pause", rx, field.Bottom - 110, 24, Faint);

        void Stat(string name, string value)
        {
            Label(ctx, name, lx, y, 18, Fill[1]);
            Label(ctx, value, lx, y + 32, 30, White);
            y += 100;
        }
        void Pips(double x, double py, int n, IBrush brush)
        {
            for (var i = 0; i < Math.Min(n, 9); i++) ctx.FillRectangle(brush, new Rect(x + i * 34, py, 24, 24));
        }
    }

    private void DrawOverlay(DrawingContext ctx, Rect field)
    {
        var real = _screen.Game;
        var blink = (int)(_screen.Now * 2) % 2 == 0;
        if (_screen.ShowingDemo)
        {
            Centered(ctx, "STARFALL", field, field.Top + field.Height * 0.32, 56, Fill[2], shadow: Fill[0]);
            Centered(ctx, "A BULLET HELL SHOOTER", field, field.Top + field.Height * 0.32 + 90, 18, Fill[1]);
            if (blink) Centered(ctx, "PRESS A TO START", field, field.Top + field.Height * 0.55, 26, White);
            Centered(ctx, "B TO LEAVE", field, field.Top + field.Height * 0.55 + 60, 16, Fill[1]);
            return;
        }
        if (real.Banner is { } banner)
        {
            var warning = banner == "WARNING";
            if (!warning || blink)
                Centered(ctx, banner, field, field.Top + field.Height * 0.4, warning ? 52 : 40, warning ? Fill[0] : Fill[2], shadow: warning ? Fill[2] : Fill[0]);
        }
        if (real.Phase == StarfallPhase.GameOver)
        {
            var best = real.Score > 0 && real.Score >= real.HiScore;
            Centered(ctx, best ? "NEW HIGH SCORE!" : $"SCORE {real.Score:N0}", field, field.Top + field.Height * 0.5, 22, best ? Fill[4] : White);
            if (blink) Centered(ctx, "A PLAY AGAIN   B LEAVE", field, field.Top + field.Height * 0.6, 18, White);
        }
    }

    private static FormattedText Format(string text, double size, IBrush brush, Typeface face) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush);

    private static void Label(DrawingContext ctx, string text, double x, double y, double size, IBrush brush) =>
        ctx.DrawText(Format(text, size, brush, Pixel), new Point(x, y));

    private static void Text(DrawingContext ctx, string text, double x, double y, double size, IBrush brush) =>
        ctx.DrawText(Format(text, size, brush, Body), new Point(x, y));

    private static void Centered(DrawingContext ctx, string text, Rect field, double y, double size, IBrush brush, IBrush? shadow = null)
    {
        var ft = Format(text, size, brush, Pixel);
        var x = field.Left + (field.Width - ft.Width) / 2;
        if (shadow is not null) ctx.DrawText(Format(text, size, shadow, Pixel), new Point(x + size / 9, y + size / 9));
        ctx.DrawText(ft, new Point(x, y));
    }
}
