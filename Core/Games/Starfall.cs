using System.Numerics;
using System.Runtime.InteropServices;

namespace Lounge.Core.Games;

/// <summary>What the player is doing this frame: movement (-1..1, +Y down), focus (slow,
/// precise movement), and the bomb and start buttons as held (edges are found here).</summary>
public readonly record struct StarfallInput(float X, float Y, bool Focus, bool Bomb, bool Start);

public enum StarfallPhase { Title, Playing, StageClear, GameOver }

/// <summary>Things that happened during a step, for the screen's sound and rumble.</summary>
public enum StarfallEvent { EnemyDown, BigExplosion, PlayerHit, Bomb, PowerUp, ExtraLife, StageClear, BossWarning, GameOver, Graze, PhaseBreak }

public enum EnemyKind { Drone, Swooper, Turret, Spinner, Sniper, Boss }

public struct Bullet
{
    public Vector2 P, V;
    public float R, Accel, Spin;
    public byte Color;
    public bool Grazed, Dead;
}

public struct Shot { public Vector2 P, V; public float Damage; public bool Dead; }

public enum ItemKind { Power, Point, Bomb, Life }

public struct Item { public Vector2 P, V; public ItemKind Kind; public bool Dead; }

public struct Particle { public Vector2 P, V; public float Life, MaxLife; public byte Color; }

public sealed class Enemy
{
    public EnemyKind Kind;
    public Vector2 P, V;
    public float Hp, MaxHp, Age, Fire, Angle, TargetY, Radius;
    public int Score, Phase;
    public float Invulnerable;
    public bool Dead, Left;
}

/// <summary>Starfall: a vertical bullet-hell shooter. Waves of drones, swoopers, ring
/// turrets, spiral spinners and snipers, then a boss with three phases of patterns; every
/// stage adds speed, density, toughness and new patterns. Tiny hitbox, focus movement,
/// grazing, bombs, power and point items. Pure simulation (no UI), deterministic for a
/// seed and stepped at a fixed 120 Hz, so it can be tested; the screen only draws it.</summary>
public sealed class StarfallGame
{
    public const float W = 640, H = 960;
    public const float HitRadius = 2.5f, GrazeRadius = 16, PlayerSpeed = 330, FocusSpeed = 145;
    private const float Step = 1f / 120;

    private readonly Random _rng;
    private float _accumulator;

    public StarfallPhase Phase { get; private set; } = StarfallPhase.Title;
    public int Stage { get; private set; }
    public long Score { get; private set; }
    public long HiScore { get; set; }
    public int Lives { get; private set; }
    public int Bombs { get; private set; }
    public float Power { get; private set; } = 1;
    public int Graze { get; private set; }
    public float Time { get; private set; }

    public Vector2 Player { get; private set; } = new(W / 2, H - 120);
    public float Invulnerable { get; private set; }
    public bool Focused { get; private set; }
    /// <summary>Seconds since the last bomb went off (drawn as an expanding ring), or -1.</summary>
    public float BombAge { get; private set; } = -1;

    public string? Banner { get; private set; }
    public float BannerTime { get; private set; }

    public List<Bullet> Bullets { get; } = new();
    public List<Shot> Shots { get; } = new();
    public List<Enemy> Enemies { get; } = new();
    public List<Item> Items { get; } = new();
    public List<Particle> Particles { get; } = new();
    public List<StarfallEvent> Events { get; } = new();

    public Enemy? Boss => Enemies.FirstOrDefault(e => e.Kind == EnemyKind.Boss && !e.Dead);

    // Stage script: waves still to come, and when the next one is due.
    private readonly Queue<EnemyKind> _waves = new();
    private float _nextWave, _shotCooldown, _stageClearTime;
    private bool _bossSpawned, _bombHeld, _startHeld = true;
    private long _nextExtend = 150_000;

    public StarfallGame(int seed = 1) => _rng = new Random(seed);

    /// <summary>How hard this stage is: toughness, bullet speed and density grow with it.</summary>
    public float Difficulty => 1 + 0.3f * (Stage - 1);
    private float SpeedScale => MathF.Min(1.8f, 1 + 0.08f * (Stage - 1));
    private float Density => MathF.Min(3f, 1 + 0.25f * (Stage - 1));

    public void Start()
    {
        Score = 0; Lives = 3; Bombs = 3; Power = 1; Graze = 0; _nextExtend = 150_000;
        Bullets.Clear(); Shots.Clear(); Enemies.Clear(); Items.Clear(); Particles.Clear();
        Player = new(W / 2, H - 120);
        Phase = StarfallPhase.Playing;
        BeginStage(1);
    }

    private void BeginStage(int stage)
    {
        Stage = stage;
        _waves.Clear();
        // Which waves a stage can throw, unlocked as stages go by.
        var pool = new List<EnemyKind> { EnemyKind.Drone, EnemyKind.Swooper, EnemyKind.Turret };
        if (stage >= 2) pool.Add(EnemyKind.Spinner);
        if (stage >= 3) pool.Add(EnemyKind.Sniper);
        var count = Math.Min(5 + stage, 14);
        for (var i = 0; i < count; i++) _waves.Enqueue(i == 0 ? EnemyKind.Drone : pool[_rng.Next(pool.Count)]);
        _nextWave = 2.5f;
        _bossSpawned = false;
        Show($"STAGE {stage}", 2.5f);
    }

    private void Show(string text, float seconds) { Banner = text; BannerTime = seconds; }

    /// <summary>Advances the game by <paramref name="dt"/> seconds in fixed 120 Hz steps.</summary>
    public void Update(float dt, StarfallInput input)
    {
        _accumulator += Math.Clamp(dt, 0, 0.1f);
        while (_accumulator >= Step)
        {
            _accumulator -= Step;
            Tick(input);
        }
    }

    private void Tick(StarfallInput input)
    {
        Time += Step;
        if (BannerTime > 0 && (BannerTime -= Step) <= 0) Banner = null;
        var startPressed = input.Start && !_startHeld;
        _startHeld = input.Start;
        var bombPressed = input.Bomb && !_bombHeld;
        _bombHeld = input.Bomb;
        UpdateParticles();

        switch (Phase)
        {
            case StarfallPhase.Title:
            case StarfallPhase.GameOver:
                if (startPressed) Start();
                return;
            case StarfallPhase.StageClear:
                MovePlayer(input);
                UpdateShots(fire: false);
                UpdateItems();
                if ((_stageClearTime -= Step) <= 0) { Phase = StarfallPhase.Playing; BeginStage(Stage + 1); }
                return;
        }

        MovePlayer(input);
        if (Invulnerable > 0) Invulnerable -= Step;
        if (BombAge >= 0 && (BombAge += Step) > 1.4f) BombAge = -1;
        if (bombPressed && Bombs > 0) DropBomb();

        RunScript();
        UpdateShots(fire: true);
        UpdateEnemies();
        UpdateBullets();
        UpdateItems();
        Cleanup();
    }

    // ------------------------------------------------------------------ player

    private void MovePlayer(StarfallInput input)
    {
        Focused = input.Focus;
        var move = new Vector2(input.X, input.Y);
        if (move.LengthSquared() > 1) move = Vector2.Normalize(move);
        var p = Player + move * (Focused ? FocusSpeed : PlayerSpeed) * Step;
        Player = new(Math.Clamp(p.X, 12, W - 12), Math.Clamp(p.Y, 24, H - 20));
    }

    private void UpdateShots(bool fire)
    {
        if (fire && (_shotCooldown -= Step) <= 0)
        {
            _shotCooldown = 0.075f;
            var level = (int)Power;
            var spread = Focused ? 0.35f : 1f;
            Fire(-7, 0); Fire(7, 0);
            if (level >= 2) { Fire(-18, -0.12f * spread); Fire(18, 0.12f * spread); }
            if (level >= 3) { Fire(-28, -0.25f * spread); Fire(28, 0.25f * spread); }
            if (level >= 4) { Fire(-13, -0.04f * spread); Fire(13, 0.04f * spread); }
        }
        var shots = CollectionsMarshal.AsSpan(Shots);
        for (var i = 0; i < shots.Length; i++)
        {
            ref var s = ref shots[i];
            s.P += s.V * Step;
            if (s.P.Y < -20 || s.P.X < -20 || s.P.X > W + 20) s.Dead = true;
        }

        void Fire(float dx, float angle) =>
            Shots.Add(new Shot { P = Player + new Vector2(dx, -10), V = new Vector2(MathF.Sin(angle), -MathF.Cos(angle)) * 950, Damage = 1 });
    }

    private void DropBomb()
    {
        Bombs--;
        BombAge = 0;
        Invulnerable = MathF.Max(Invulnerable, 2.2f);
        ClearBullets(toPoints: true);
        foreach (var e in Enemies)
            if (!e.Dead) Damage(e, e.Kind == EnemyKind.Boss ? e.MaxHp * 0.04f : 40);
        Events.Add(StarfallEvent.Bomb);
    }

    private void HitPlayer()
    {
        Lives--;
        Power = MathF.Max(1, Power - 1);
        Bombs = 3;
        Invulnerable = 3;
        Explode(Player, 40, 5, 260);
        // The bullets round the wreck clear, so a new ship isn't hit on arrival.
        var bullets = CollectionsMarshal.AsSpan(Bullets);
        for (var i = 0; i < bullets.Length; i++)
            if (Vector2.DistanceSquared(bullets[i].P, Player) < 260 * 260) bullets[i].Dead = true;
        Events.Add(StarfallEvent.PlayerHit);
        if (Lives <= 0)
        {
            Phase = StarfallPhase.GameOver;
            HiScore = Math.Max(HiScore, Score);
            Show("GAME OVER", 999);
            Events.Add(StarfallEvent.GameOver);
        }
    }

    private void AddScore(long points)
    {
        Score += points;
        if (Score >= _nextExtend)
        {
            _nextExtend += 150_000 + 50_000L * Stage;
            Lives = Math.Min(Lives + 1, 9);
            Show("EXTEND!", 1.5f);
            Events.Add(StarfallEvent.ExtraLife);
        }
        HiScore = Math.Max(HiScore, Score);
    }

    // ------------------------------------------------------------------ stage script

    private void RunScript()
    {
        if (_waves.Count > 0)
        {
            if ((_nextWave -= Step) <= 0)
            {
                SpawnWave(_waves.Dequeue());
                _nextWave = MathF.Max(2.2f, 4.2f - 0.15f * Stage);
            }
            return;
        }
        // The boss comes once the last wave has gone.
        if (_bossSpawned || !Enemies.All(e => e.Dead)) return;
        _nextWave = MathF.Min(_nextWave, 0);
        if ((_nextWave -= Step) <= -1.5f)
        {
            _bossSpawned = true;
            Show("WARNING", 2.5f);
            Events.Add(StarfallEvent.BossWarning);
            var hp = 900 * Difficulty;
            Enemies.Add(new Enemy { Kind = EnemyKind.Boss, P = new(W / 2, -120), V = new(0, 90), Hp = hp, MaxHp = hp, TargetY = 170, Radius = 52, Score = 10_000 * Stage, Invulnerable = 2.5f });
        }
    }

    private void SpawnWave(EnemyKind kind)
    {
        var d = Difficulty;
        switch (kind)
        {
            case EnemyKind.Drone:
                // A V of drones coming straight down.
                var cx = 120 + _rng.NextSingle() * (W - 240);
                for (var i = 0; i < 7; i++)
                {
                    var off = (i - 3) * 38f;
                    Add(EnemyKind.Drone, new(cx + off, -30 - MathF.Abs(off) * 0.8f), new(0, 120 + 10 * Stage), 3 * d, 14, 100, fire: 0.6f + i * 0.13f);
                }
                break;
            case EnemyKind.Swooper:
                var fromLeft = _rng.Next(2) == 0;
                var y0 = 80 + _rng.NextSingle() * 200;
                for (var i = 0; i < 6; i++)
                    Add(EnemyKind.Swooper, new(fromLeft ? -30 - i * 50 : W + 30 + i * 50, y0), new(fromLeft ? 150 : -150, 0), 4 * d, 14, 150, fire: 0.8f + i * 0.2f, targetY: y0);
                break;
            case EnemyKind.Turret:
                var n = Stage >= 4 ? 3 : 2;
                for (var i = 0; i < n; i++)
                    Add(EnemyKind.Turret, new(W * (i + 1) / (n + 1), -40), new(0, 110), 25 * d, 26, 500, fire: 1.2f, targetY: 160 + _rng.NextSingle() * 140);
                break;
            case EnemyKind.Spinner:
                Add(EnemyKind.Spinner, new(W / 2 + (_rng.NextSingle() - 0.5f) * 240, -40), new(0, 120), 30 * d, 24, 800, fire: 1.0f, targetY: 200 + _rng.NextSingle() * 100);
                break;
            case EnemyKind.Sniper:
                for (var i = 0; i < 4; i++)
                    Add(EnemyKind.Sniper, new(80 + _rng.NextSingle() * (W - 160), -30 - i * 70), new(0, 260), 6 * d, 12, 200, fire: 0.5f, targetY: 120 + _rng.NextSingle() * 200);
                break;
        }

        void Add(EnemyKind k, Vector2 p, Vector2 v, float hp, float r, int score, float fire, float targetY = 0) =>
            Enemies.Add(new Enemy { Kind = k, P = p, V = v, Hp = hp, MaxHp = hp, Radius = r, Score = score, Fire = fire, TargetY = targetY });
    }

    // ------------------------------------------------------------------ enemies

    private float AimAt(Vector2 from) => MathF.Atan2(Player.Y - from.Y, Player.X - from.X);

    private void Emit(Vector2 from, float angle, float speed, float r = 6, byte color = 0, float accel = 0, float spin = 0)
    {
        if (Bullets.Count > 2400) return;
        Bullets.Add(new Bullet { P = from, V = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed * SpeedScale, R = r, Color = color, Accel = accel, Spin = spin });
    }

    private void Ring(Vector2 from, int count, float offset, float speed, float r, byte color, float spin = 0)
    {
        for (var i = 0; i < count; i++) Emit(from, offset + MathF.Tau * i / count, speed, r, color, spin: spin);
    }

    private void Fan(Vector2 from, int count, float aim, float width, float speed, float r, byte color)
    {
        for (var i = 0; i < count; i++) Emit(from, aim + (count == 1 ? 0 : (i / (float)(count - 1) - 0.5f) * width), speed, r, color);
    }

    private void UpdateEnemies()
    {
        var dens = Density;
        foreach (var e in Enemies)
        {
            if (e.Dead) continue;
            e.Age += Step;
            if (e.Invulnerable > 0) e.Invulnerable -= Step;
            switch (e.Kind)
            {
                case EnemyKind.Drone:
                    e.P += e.V * Step;
                    if ((e.Fire -= Step) <= 0) { Emit(e.P, AimAt(e.P), 150, 6, 0); e.Fire = 1.6f / dens; }
                    break;
                case EnemyKind.Swooper:
                    e.P = new(e.P.X + e.V.X * Step, e.TargetY + 40 * MathF.Sin(e.Age * 3));
                    if ((e.Fire -= Step) <= 0) { Fan(e.P, 3, AimAt(e.P), 0.5f, 170, 6, 1); e.Fire = 1.4f / dens; }
                    break;
                case EnemyKind.Turret:
                    Settle(e, 6.5f);
                    if (e.Age > 1 && !e.Left && (e.Fire -= Step) <= 0)
                    {
                        Ring(e.P, (int)(10 + 4 * Difficulty), e.Angle, 120, 7, 2);
                        e.Angle += 0.17f;
                        e.Fire = 1.8f / dens;
                    }
                    break;
                case EnemyKind.Spinner:
                    Settle(e, 5f);
                    if (e.Age > 1.2f && !e.Left && (e.Fire -= Step) <= 0)
                    {
                        var arms = 2 + Stage / 3;
                        for (var a = 0; a < arms; a++) Emit(e.P, e.Angle + MathF.Tau * a / arms, 135, 6, 3);
                        e.Angle += 0.33f;
                        e.Fire = 0.09f / (0.6f + 0.4f * dens);
                    }
                    break;
                case EnemyKind.Sniper:
                    Settle(e, 2.6f);
                    // A quick burst of five fast shots along one aimed line.
                    if (e.Age > 0.6f && !e.Left && e.Phase < 5 && (e.Fire -= Step) <= 0)
                    {
                        if (e.Phase == 0) e.Angle = AimAt(e.P);
                        Emit(e.P, e.Angle, 300, 5, 4);
                        e.Phase++;
                        e.Fire = 0.07f;
                    }
                    break;
                case EnemyKind.Boss:
                    UpdateBoss(e, dens);
                    break;
            }
            if (e.P.Y > H + 60 || e.P.Y < -200 || e.P.X < -420 || e.P.X > W + 420) e.Dead = true;   // left the screen (swoopers enter from far out)
        }
        HitEnemies();
    }

    /// <summary>Come down to the target height, stay a while, then leave upwards.</summary>
    private static void Settle(Enemy e, float stay)
    {
        if (!e.Left && e.Age > stay) { e.Left = true; e.V = new(0, -90); }
        if (e.Left) { e.P += e.V * Step; return; }
        if (e.P.Y < e.TargetY) e.P = new(e.P.X, MathF.Min(e.TargetY, e.P.Y + e.V.Y * Step));
    }

    private void UpdateBoss(Enemy b, float dens)
    {
        if (b.P.Y < b.TargetY) { b.P += new Vector2(0, b.V.Y * Step); return; }
        b.P = new(W / 2 + 170 * MathF.Sin(b.Age * 0.55f), b.TargetY + 18 * MathF.Sin(b.Age * 1.3f));

        var phase = b.Hp > b.MaxHp * 2 / 3 ? 0 : b.Hp > b.MaxHp / 3 ? 1 : 2;
        if (phase != b.Phase)
        {
            // A phase breaks: its bullets turn to points and the boss catches its breath.
            b.Phase = phase;
            b.Invulnerable = 1.5f;
            ClearBullets(toPoints: true);
            Explode(b.P, 30, 2, 200);
            Events.Add(StarfallEvent.PhaseBreak);
        }
        if (b.Invulnerable > 0) return;

        // Each phase plays a pattern; later stages start further along the list and the
        // last phase layers two at once.
        var pattern = (Stage - 1 + phase) % 4;
        Pattern(b, pattern, dens);
        if (phase == 2 && Stage >= 3) Pattern(b, (pattern + 2) % 4, dens * 0.6f);
    }

    private void Pattern(Enemy b, int pattern, float dens)
    {
        b.Fire -= Step;
        if (b.Fire > 0) return;
        switch (pattern)
        {
            case 0: // Flower: many rotating arms.
                var arms = 5 + Stage / 2;
                for (var a = 0; a < arms; a++) Emit(b.P, b.Angle + MathF.Tau * a / arms, 140, 6, 3);
                b.Angle += 0.2f * (MathF.Sin(b.Age * 0.4f) > 0 ? 1 : -1);
                b.Fire = 0.075f / MathF.Sqrt(dens);
                break;
            case 1: // Rings with an aimed fan between them.
                Ring(b.P, (int)(22 + 4 * Difficulty), b.Angle, 130, 7, 2);
                Fan(b.P, 5 + Stage / 2, AimAt(b.P), 0.7f, 230, 6, 1);
                b.Angle += 0.09f;
                b.Fire = 0.9f / dens;
                break;
            case 2: // Rain: bullets thrown up that fall back, over a slow spiral.
                for (var i = 0; i < 2; i++) Emit(b.P, -MathF.PI / 2 + (_rng.NextSingle() - 0.5f) * 2.6f, 160 + _rng.NextSingle() * 80, 6, 5, accel: 0);
                var last = CollectionsMarshal.AsSpan(Bullets);
                for (var i = Math.Max(0, last.Length - 2); i < last.Length; i++) last[i].Accel = 170;   // gravity
                Emit(b.P, b.Angle, 110, 8, 0); Emit(b.P, b.Angle + MathF.PI, 110, 8, 0);
                b.Angle += 0.13f;
                b.Fire = 0.05f / MathF.Sqrt(dens);
                break;
            default: // Curving rings: each bullet bends as it flies, alternating ways.
                var spin = (b.Phase % 2 == 0 ? 1 : -1) * (0.5f + 0.05f * Stage);
                Ring(b.P, (int)(16 + 3 * Difficulty), b.Angle, 120, 6, 4, spin);
                Ring(b.P, (int)(16 + 3 * Difficulty), -b.Angle, 120, 6, 1, -spin);
                b.Angle += 0.11f;
                b.Fire = 0.8f / dens;
                break;
        }
    }

    private void HitEnemies()
    {
        var shots = CollectionsMarshal.AsSpan(Shots);
        foreach (var e in Enemies)
        {
            if (e.Dead || e.P.Y < -10) continue;
            for (var i = 0; i < shots.Length; i++)
            {
                ref var s = ref shots[i];
                if (s.Dead) continue;
                if (Vector2.DistanceSquared(s.P, e.P) < (e.Radius + 4) * (e.Radius + 4))
                {
                    s.Dead = true;
                    Damage(e, s.Damage);
                    if (e.Dead) break;
                }
            }
            // Touching an enemy is as bad as touching its bullets.
            if (!e.Dead && Invulnerable <= 0 && Vector2.DistanceSquared(e.P, Player) < (e.Radius * 0.6f + HitRadius) * (e.Radius * 0.6f + HitRadius)) HitPlayer();
        }
    }

    private void Damage(Enemy e, float amount)
    {
        if (e.Invulnerable > 0) return;
        e.Hp -= amount;
        if (e.Hp > 0) return;
        e.Dead = true;
        AddScore(e.Score);
        if (e.Kind == EnemyKind.Boss)
        {
            Explode(e.P, 90, 4, 420);
            ClearBullets(toPoints: true);
            for (var i = 0; i < 24; i++) Drop(e.P + Scatter(60), i % 4 == 0 ? ItemKind.Power : ItemKind.Point);
            if (Stage % 3 == 0) Drop(e.P, ItemKind.Life);
            Phase = StarfallPhase.StageClear;
            _stageClearTime = 4;
            Show("STAGE CLEAR", 3.5f);
            Events.Add(StarfallEvent.BigExplosion);
            Events.Add(StarfallEvent.StageClear);
            return;
        }
        Explode(e.P, e.Kind is EnemyKind.Turret or EnemyKind.Spinner ? 26 : 12, (byte)(e.Kind == EnemyKind.Turret ? 2 : 1), 180);
        Events.Add(e.Kind is EnemyKind.Turret or EnemyKind.Spinner ? StarfallEvent.BigExplosion : StarfallEvent.EnemyDown);
        var roll = _rng.NextSingle();
        if (e.Kind is EnemyKind.Turret or EnemyKind.Spinner)
        {
            Drop(e.P, ItemKind.Power); Drop(e.P + Scatter(20), ItemKind.Point); Drop(e.P + Scatter(20), ItemKind.Point);
            if (roll < 0.12f) Drop(e.P, ItemKind.Bomb);
        }
        else if (roll < 0.18f) Drop(e.P, ItemKind.Power);
        else if (roll < 0.5f) Drop(e.P, ItemKind.Point);
    }

    private Vector2 Scatter(float r) => new((_rng.NextSingle() - 0.5f) * 2 * r, (_rng.NextSingle() - 0.5f) * 2 * r);

    // ------------------------------------------------------------------ bullets, items, effects

    private void UpdateBullets()
    {
        var bullets = CollectionsMarshal.AsSpan(Bullets);
        var hitR = HitRadius;
        for (var i = 0; i < bullets.Length; i++)
        {
            ref var b = ref bullets[i];
            if (b.Spin != 0)
            {
                var (sin, cos) = MathF.SinCos(b.Spin * Step);
                b.V = new(b.V.X * cos - b.V.Y * sin, b.V.X * sin + b.V.Y * cos);
            }
            if (b.Accel != 0) b.V.Y += b.Accel * Step;
            b.P += b.V * Step;
            if (b.P.X < -40 || b.P.X > W + 40 || b.P.Y < -60 || b.P.Y > H + 40) { b.Dead = true; continue; }
            var d2 = Vector2.DistanceSquared(b.P, Player);
            if (Invulnerable <= 0 && d2 < (b.R * 0.75f + hitR) * (b.R * 0.75f + hitR)) { b.Dead = true; HitPlayer(); if (Phase != StarfallPhase.Playing) return; bullets = CollectionsMarshal.AsSpan(Bullets); continue; }
            // Grazing: a bullet that brushes past scores.
            if (!b.Grazed && d2 < (b.R + GrazeRadius) * (b.R + GrazeRadius))
            {
                b.Grazed = true;
                Graze++;
                AddScore(50 + 10 * Stage);
                Spark(b.P);
                if (Graze % 25 == 0) Events.Add(StarfallEvent.Graze);
            }
        }
    }

    private void ClearBullets(bool toPoints)
    {
        if (toPoints) AddScore(Bullets.Count(b => !b.Dead) * 10L);
        foreach (var b in Bullets) if (!b.Dead && Particles.Count < 1500) Particles.Add(new Particle { P = b.P, V = new(0, -40), Life = 0.4f, MaxLife = 0.4f, Color = b.Color });
        Bullets.Clear();
    }

    private void Drop(Vector2 p, ItemKind kind) => Items.Add(new Item { P = p, V = new((_rng.NextSingle() - 0.5f) * 60, -140), Kind = kind });

    private void UpdateItems()
    {
        var collect = Player.Y < H * 0.25f || BombAge >= 0 || Phase == StarfallPhase.StageClear;
        var items = CollectionsMarshal.AsSpan(Items);
        for (var i = 0; i < items.Length; i++)
        {
            ref var it = ref items[i];
            if (collect) it.V = Vector2.Normalize(Player - it.P + new Vector2(0.01f, 0)) * 720;
            else { it.V.X *= 0.98f; it.V.Y = MathF.Min(it.V.Y + 260 * Step, 160); }
            it.P += it.V * Step;
            if (it.P.Y > H + 30) { it.Dead = true; continue; }
            if (Vector2.DistanceSquared(it.P, Player) < 30 * 30) { it.Dead = true; Collect(it.Kind, it.P); }
        }
    }

    private void Collect(ItemKind kind, Vector2 at)
    {
        switch (kind)
        {
            case ItemKind.Power:
                var before = (int)Power;
                if (Power >= 4) AddScore(1_000);
                Power = MathF.Min(4, Power + 0.25f);
                if ((int)Power > before) { Events.Add(StarfallEvent.PowerUp); Show("POWER UP", 1); }
                break;
            case ItemKind.Point:
                // Higher up the screen is worth more.
                AddScore((long)(300 + 700 * (1 - at.Y / H)));
                break;
            case ItemKind.Bomb:
                Bombs = Math.Min(Bombs + 1, 6);
                break;
            case ItemKind.Life:
                Lives = Math.Min(Lives + 1, 9);
                Show("EXTEND!", 1.5f);
                Events.Add(StarfallEvent.ExtraLife);
                break;
        }
    }

    private void Explode(Vector2 at, int count, byte color, float speed)
    {
        for (var i = 0; i < count && Particles.Count < 1500; i++)
        {
            var a = _rng.NextSingle() * MathF.Tau;
            var s = speed * (0.3f + 0.7f * _rng.NextSingle());
            var life = 0.4f + 0.5f * _rng.NextSingle();
            Particles.Add(new Particle { P = at, V = new(MathF.Cos(a) * s, MathF.Sin(a) * s), Life = life, MaxLife = life, Color = color });
        }
    }

    private void Spark(Vector2 at)
    {
        if (Particles.Count < 1500) Particles.Add(new Particle { P = at, V = Scatter(80), Life = 0.25f, MaxLife = 0.25f, Color = 6 });
    }

    private void UpdateParticles()
    {
        var parts = CollectionsMarshal.AsSpan(Particles);
        for (var i = 0; i < parts.Length; i++)
        {
            ref var p = ref parts[i];
            p.P += p.V * Step;
            p.V *= 0.97f;
            p.Life -= Step;
        }
        Particles.RemoveAll(static p => p.Life <= 0);
    }

    private void Cleanup()
    {
        Bullets.RemoveAll(static b => b.Dead);
        Shots.RemoveAll(static s => s.Dead);
        Items.RemoveAll(static i => i.Dead);
        Enemies.RemoveAll(static e => e.Dead);
    }

    // ------------------------------------------------------------------ testing and demos

    /// <summary>A simple pilot for the title-screen attract mode and the tests: drifts
    /// towards the enemies' side of the screen and away from the nearest bullets.</summary>
    public StarfallInput Autopilot()
    {
        var target = Enemies.Where(e => !e.Dead && e.P.Y > 0).OrderBy(e => MathF.Abs(e.P.X - Player.X)).FirstOrDefault()?.P.X ?? W / 2;
        var dx = Math.Clamp((target - Player.X) / 120, -1, 1);
        var dy = Math.Clamp((H - 160 - Player.Y) / 120, -1, 1);
        foreach (var b in Bullets)
        {
            var d = b.P - Player;
            if (d.LengthSquared() < 70 * 70) { dx -= Math.Sign(d.X) * 0.9f; dy -= Math.Sign(d.Y) * 0.6f; }
        }
        return new StarfallInput(Math.Clamp(dx, -1, 1), Math.Clamp(dy, -1, 1), Focus: Bullets.Count > 120, Bomb: false, Start: false);
    }

    /// <summary>Tests: jump straight to a stage.</summary>
    public void SkipToStage(int stage)
    {
        Bullets.Clear(); Enemies.Clear();
        BeginStage(stage);
    }

    /// <summary>Tests: everything on screen takes a hit (the boss included).</summary>
    public void DamageAll(float amount) { foreach (var e in Enemies.ToList()) if (!e.Dead) Damage(e, amount); }
}
