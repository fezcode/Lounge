using System.Numerics;

namespace Lounge.Core.Games;

/// <summary>This frame's controls: move and strafe (-1..1), turn (-1..1, right positive),
/// and buttons as held (presses are found here).</summary>
public readonly record struct HellgateInput(float Forward, float Strafe, float Turn, bool Fire, bool Use, bool NextWeapon, bool Start);

public enum HellgatePhase { Title, Playing, LevelDone, Dead }

public enum HellgateEvent { Shot, ShotgunBlast, EnemyHurt, EnemyDown, PlayerHurt, Pickup, Weapon, DoorOpen, LevelDone, Died, Fireball, Empty }

public enum MonsterKind { Imp, Grunt, Brute }

public enum PickupKind { Health, Medkit, Armor, Bullets, Shells, Shotgun, Chaingun }

public sealed class Monster
{
    public MonsterKind Kind;
    public Vector2 P;
    public float Hp, Cooldown, Attacking, Pain;
    public bool Awake, Dead;
}

public struct Fireball { public Vector2 P, V; public float Damage; public bool Dead; }

public struct Pickup { public Vector2 P; public PickupKind Kind; public bool Taken; }

/// <summary>Hellgate: a Doom-style first-person shooter on a grid map. Generated levels of
/// rooms, corridors and doors, an exit switch to find, imps that throw fireballs, grunts
/// that shoot, brutes that charge, and a pistol, shotgun and chaingun. Every level is
/// bigger and busier. Pure simulation at a fixed 120 Hz; <see cref="HellgateRenderer"/>
/// draws it.</summary>
public sealed class HellgateGame
{
    private const float Step = 1f / 120, Radius = 0.22f, MoveSpeed = 3.6f, TurnSpeed = 2.8f;

    private readonly int _seed;
    private Random _rng;
    private float _acc;
    private bool _fireHeld, _useHeld, _nextHeld, _startHeld = true;

    public HellgatePhase Phase { get; private set; } = HellgatePhase.Title;
    public int Level { get; private set; }
    public int BestLevel { get; set; }
    public int[,] Map { get; private set; } = new int[1, 1];
    public int MapW => Map.GetLength(0);
    public int MapH => Map.GetLength(1);
    /// <summary>Doors rising, by cell: 0 shut to 1 open (then the cell clears).</summary>
    public Dictionary<(int X, int Y), float> Doors { get; } = new();

    public Vector2 Player { get; private set; }
    public float Angle { get; private set; }
    public int Health { get; private set; }
    public int Armor { get; private set; }
    public int Bullets { get; private set; }
    public int Shells { get; private set; }
    public bool HasShotgun { get; private set; }
    public bool HasChaingun { get; private set; }
    public int Weapon { get; private set; }          // 0 pistol, 1 shotgun, 2 chaingun
    public float WeaponCooldown { get; private set; }
    public float FlashTime { get; private set; }     // muzzle flash
    public float HurtTime { get; private set; }      // red screen
    public float PickupTime { get; private set; }    // gold screen
    public float Walked { get; private set; }        // for the weapon's bob
    public float Time { get; private set; }
    public float LevelTime { get; private set; }
    public int Kills { get; private set; }
    public int MonstersTotal { get; private set; }
    public string? Message { get; private set; }
    public float MessageTime { get; private set; }

    public List<Monster> Monsters { get; } = new();
    public List<Fireball> Fireballs { get; } = new();
    public List<Pickup> Pickups { get; } = new();
    public List<HellgateEvent> Events { get; } = new();

    private int[,] _flow = new int[1, 1];
    private float _flowAge;

    public HellgateGame(int seed = 1) { _seed = seed; _rng = new Random(seed); }

    public float Toughness => 1 + 0.25f * (Level - 1);

    public void Start(int level = 1)
    {
        Health = 100; Armor = 0; Bullets = 50; Shells = 0; HasShotgun = false; HasChaingun = false; Weapon = 0;
        BeginLevel(level);
    }

    private void BeginLevel(int level)
    {
        Level = level;
        _rng = new Random(_seed * 7919 + level);
        Monsters.Clear(); Fireballs.Clear(); Pickups.Clear(); Doors.Clear();
        var gen = HellgateMap.Generate(level, _rng);
        Map = gen.Cells;
        Player = gen.Start;
        Angle = gen.StartAngle;
        foreach (var (kind, p) in gen.Monsters)
            Monsters.Add(new Monster { Kind = kind, P = p, Hp = BaseHp(kind) * Toughness, Cooldown = 1 + _rng.NextSingle() });
        foreach (var (kind, p) in gen.Pickups) Pickups.Add(new Pickup { P = p, Kind = kind });
        MonstersTotal = Monsters.Count;
        Kills = 0;
        LevelTime = 0;
        _flow = new int[MapW, MapH];
        _flowAge = 99;
        Phase = HellgatePhase.Playing;
        BestLevel = Math.Max(BestLevel, level);
        Say($"LEVEL {level}", 2.5f);
    }

    private static float BaseHp(MonsterKind k) => k switch { MonsterKind.Imp => 30, MonsterKind.Grunt => 20, _ => 120 };

    private void Say(string text, float seconds) { Message = text; MessageTime = seconds; }

    public void Update(float dt, HellgateInput input)
    {
        _acc += Math.Clamp(dt, 0, 0.1f);
        while (_acc >= Step) { _acc -= Step; Tick(input); }
    }

    private void Tick(HellgateInput input)
    {
        Time += Step;
        if (MessageTime > 0 && (MessageTime -= Step) <= 0) Message = null;
        bool Press(bool held, ref bool was) { var p = held && !was; was = held; return p; }
        var start = Press(input.Start, ref _startHeld);
        var use = Press(input.Use, ref _useHeld);
        var next = Press(input.NextWeapon, ref _nextHeld);
        var firePressed = Press(input.Fire, ref _fireHeld);
        FlashTime = MathF.Max(0, FlashTime - Step);
        HurtTime = MathF.Max(0, HurtTime - Step);
        PickupTime = MathF.Max(0, PickupTime - Step);

        switch (Phase)
        {
            case HellgatePhase.Title:
                if (start) Start();
                return;
            case HellgatePhase.Dead:
                if (start) Start(Level);   // as in Doom: the level again, from a pistol start
                return;
            case HellgatePhase.LevelDone:
                if (start) BeginLevel(Level + 1);
                return;
        }

        LevelTime += Step;
        Move(input);
        UpdateDoors();
        if (use) Use();
        if (next) CycleWeapon();
        WeaponCooldown = MathF.Max(0, WeaponCooldown - Step);
        // The pistol and shotgun fire per press; the chaingun keeps going while held.
        if ((Weapon == 2 ? input.Fire : firePressed || (input.Fire && Weapon != 1)) && WeaponCooldown <= 0) Fire();
        Pick();
        UpdateMonsters();
        UpdateFireballs();
    }

    // ------------------------------------------------------------------ the player

    public bool Solid(int x, int y) => x < 0 || y < 0 || x >= MapW || y >= MapH || Map[x, y] != 0;

    private bool Blocked(Vector2 p, float r) =>
        Solid((int)(p.X - r), (int)(p.Y - r)) || Solid((int)(p.X + r), (int)(p.Y - r)) ||
        Solid((int)(p.X - r), (int)(p.Y + r)) || Solid((int)(p.X + r), (int)(p.Y + r));

    /// <summary>Moves with wall sliding: each axis is tried on its own.</summary>
    private Vector2 Slide(Vector2 from, Vector2 delta, float r)
    {
        var p = from;
        if (!Blocked(new(p.X + delta.X, p.Y), r)) p.X += delta.X;
        if (!Blocked(new(p.X, p.Y + delta.Y), r)) p.Y += delta.Y;
        return p;
    }

    private Vector2 Facing => new(MathF.Cos(Angle), MathF.Sin(Angle));

    private void Move(HellgateInput input)
    {
        Angle += input.Turn * TurnSpeed * Step;
        var f = Facing;
        var side = new Vector2(-f.Y, f.X);
        var move = f * input.Forward + side * input.Strafe;
        if (move.LengthSquared() > 1) move = Vector2.Normalize(move);
        var before = Player;
        Player = Slide(Player, move * MoveSpeed * Step, Radius);
        Walked += (Player - before).Length();
    }

    private void Use()
    {
        // The cell straight ahead, within reach.
        for (var d = 0.4f; d <= 1.4f; d += 0.2f)
        {
            var p = Player + Facing * d;
            int x = (int)p.X, y = (int)p.Y;
            if (x < 0 || y < 0 || x >= MapW || y >= MapH) return;
            var c = Map[x, y];
            if (c == HellgateMap.DoorCell && !Doors.ContainsKey((x, y))) { Doors[(x, y)] = 0; Events.Add(HellgateEvent.DoorOpen); return; }
            if (c == HellgateMap.ExitCell)
            {
                Phase = HellgatePhase.LevelDone;
                BestLevel = Math.Max(BestLevel, Level + 1);
                Events.Add(HellgateEvent.LevelDone);
                return;
            }
            if (c != 0) return;
        }
    }

    private void UpdateDoors()
    {
        foreach (var key in Doors.Keys.ToList())
        {
            var v = Doors[key] + Step * 2f;
            if (v >= 1) { Doors.Remove(key); Map[key.X, key.Y] = 0; _flowAge = 99; }
            else Doors[key] = v;
        }
    }

    private void CycleWeapon()
    {
        for (var i = 1; i <= 3; i++)
        {
            var w = (Weapon + i) % 3;
            if (w == 0 || (w == 1 && HasShotgun) || (w == 2 && HasChaingun)) { Weapon = w; Events.Add(HellgateEvent.Weapon); return; }
        }
    }

    private void Fire()
    {
        var ammoOk = Weapon == 1 ? Shells > 0 : Bullets > 0;
        if (!ammoOk)
        {
            WeaponCooldown = 0.4f;
            Events.Add(HellgateEvent.Empty);
            // Out of shells or bullets: fall back to whatever still has ammo.
            if (Weapon == 1 && Bullets > 0) Weapon = HasChaingun ? 2 : 0;
            else if (Weapon != 1 && Shells > 0 && HasShotgun) Weapon = 1;
            return;
        }
        FlashTime = 0.08f;
        switch (Weapon)
        {
            case 0: Bullets--; Hitscan(0.01f, 1, 10, 16); WeaponCooldown = 0.32f; Events.Add(HellgateEvent.Shot); break;
            case 1: Shells--; Hitscan(0.09f, 7, 6, 11); WeaponCooldown = 0.85f; Events.Add(HellgateEvent.ShotgunBlast); break;
            default: Bullets--; Hitscan(0.03f, 1, 8, 12); WeaponCooldown = 0.1f; Events.Add(HellgateEvent.Shot); break;
        }
        // Gunfire wakes monsters that can hear it.
        foreach (var m in Monsters) if (!m.Dead && Vector2.DistanceSquared(m.P, Player) < 12 * 12) m.Awake = true;
    }

    private void Hitscan(float spread, int pellets, int min, int max)
    {
        for (var i = 0; i < pellets; i++)
        {
            var a = Angle + (pellets == 1 ? (_rng.NextSingle() - 0.5f) * spread * 2 : (i / (float)(pellets - 1) - 0.5f) * spread * 2 + (_rng.NextSingle() - 0.5f) * 0.02f);
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            var wall = CastRay(Player, a);
            Monster? best = null;
            var bestDist = wall;
            foreach (var m in Monsters)
            {
                if (m.Dead) continue;
                var to = m.P - Player;
                var along = Vector2.Dot(to, dir);
                if (along <= 0 || along >= bestDist) continue;
                var off = MathF.Abs(to.X * dir.Y - to.Y * dir.X);
                if (off < (m.Kind == MonsterKind.Brute ? 0.45f : 0.32f)) { best = m; bestDist = along; }
            }
            if (best is not null) Hurt(best, _rng.Next(min, max + 1));
        }
    }

    /// <summary>Distance from <paramref name="from"/> to the first wall along <paramref name="angle"/> (DDA).</summary>
    public float CastRay(Vector2 from, float angle, float max = 64)
    {
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        int mx = (int)from.X, my = (int)from.Y;
        float dX = MathF.Abs(1 / (dir.X == 0 ? 1e-6f : dir.X)), dY = MathF.Abs(1 / (dir.Y == 0 ? 1e-6f : dir.Y));
        int sx = dir.X < 0 ? -1 : 1, sy = dir.Y < 0 ? -1 : 1;
        float tx = dir.X < 0 ? (from.X - mx) * dX : (mx + 1 - from.X) * dX;
        float ty = dir.Y < 0 ? (from.Y - my) * dY : (my + 1 - from.Y) * dY;
        while (true)
        {
            float t;
            if (tx < ty) { t = tx; tx += dX; mx += sx; } else { t = ty; ty += dY; my += sy; }
            if (t > max) return max;
            if (Solid(mx, my)) return t;
        }
    }

    private bool CanSee(Vector2 from, Vector2 to)
    {
        var d = to - from;
        var dist = d.Length();
        return dist < 0.01f || CastRay(from, MathF.Atan2(d.Y, d.X), dist + 0.1f) >= dist;
    }

    private void Hurt(Monster m, float damage)
    {
        m.Hp -= damage;
        m.Awake = true;
        m.Pain = 0.15f;
        if (m.Hp > 0) { Events.Add(HellgateEvent.EnemyHurt); return; }
        m.Dead = true;
        Kills++;
        Events.Add(HellgateEvent.EnemyDown);
        // Grunts drop their clip, sometimes their shotgun shells.
        if (m.Kind == MonsterKind.Grunt) Pickups.Add(new Pickup { P = m.P, Kind = _rng.Next(3) == 0 ? PickupKind.Shells : PickupKind.Bullets });
    }

    private void HurtPlayer(int damage)
    {
        if (Phase != HellgatePhase.Playing) return;
        if (Armor > 0) { var soak = Math.Min(Armor, damage / 3); Armor -= soak; damage -= soak; }
        Health -= damage;
        HurtTime = 0.35f;
        Events.Add(HellgateEvent.PlayerHurt);
        if (Health <= 0)
        {
            Health = 0;
            Phase = HellgatePhase.Dead;
            Say("YOU DIED", 999);
            Events.Add(HellgateEvent.Died);
        }
    }

    private void Pick()
    {
        for (var i = 0; i < Pickups.Count; i++)
        {
            var p = Pickups[i];
            if (p.Taken || Vector2.DistanceSquared(p.P, Player) > 0.5f * 0.5f) continue;
            var took = p.Kind switch
            {
                PickupKind.Health when Health < 100 => Give(() => Health = Math.Min(100, Health + 15), "HEALTH"),
                PickupKind.Medkit when Health < 100 => Give(() => Health = Math.Min(100, Health + 30), "MEDKIT"),
                PickupKind.Armor when Armor < 100 => Give(() => Armor = Math.Min(100, Armor + 50), "ARMOR"),
                PickupKind.Bullets when Bullets < 200 => Give(() => Bullets = Math.Min(200, Bullets + 20), "BULLETS"),
                PickupKind.Shells when Shells < 50 => Give(() => Shells = Math.Min(50, Shells + 6), "SHELLS"),
                PickupKind.Shotgun => Give(() => { var had = HasShotgun; HasShotgun = true; Shells = Math.Min(50, Shells + 8); if (!had) Weapon = 1; }, "SHOTGUN!"),
                PickupKind.Chaingun => Give(() => { var had = HasChaingun; HasChaingun = true; Bullets = Math.Min(200, Bullets + 40); if (!had) Weapon = 2; }, "CHAINGUN!"),
                _ => false,
            };
            if (took) { p.Taken = true; Pickups[i] = p; }
        }
        Pickups.RemoveAll(static p => p.Taken);

        bool Give(Action apply, string what)
        {
            apply();
            PickupTime = 0.25f;
            Say(what, 1.2f);
            Events.Add(HellgateEvent.Pickup);
            return true;
        }
    }

    // ------------------------------------------------------------------ monsters

    /// <summary>Steps to the player from every cell (breadth-first), so monsters walk
    /// round walls towards you.</summary>
    private void RefreshFlow()
    {
        for (var x = 0; x < MapW; x++) for (var y = 0; y < MapH; y++) _flow[x, y] = int.MaxValue;
        var q = new Queue<(int, int)>();
        int px = (int)Player.X, py = (int)Player.Y;
        _flow[px, py] = 0;
        q.Enqueue((px, py));
        while (q.Count > 0)
        {
            var (x, y) = q.Dequeue();
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = x + dx, ny = y + dy;
                if (Solid(nx, ny) || _flow[nx, ny] != int.MaxValue) continue;
                _flow[nx, ny] = _flow[x, y] + 1;
                q.Enqueue((nx, ny));
            }
        }
        _flowAge = 0;
    }

    private void UpdateMonsters()
    {
        if ((_flowAge += Step) > 0.3f) RefreshFlow();
        foreach (var m in Monsters)
        {
            if (m.Dead) continue;
            m.Pain = MathF.Max(0, m.Pain - Step);
            m.Attacking = MathF.Max(0, m.Attacking - Step);
            var dist = Vector2.Distance(m.P, Player);
            var sees = dist < 18 && CanSee(m.P, Player);
            if (!m.Awake && sees && dist < 14) m.Awake = true;
            if (!m.Awake || m.Pain > 0) continue;

            var (speed, range) = m.Kind switch { MonsterKind.Imp => (1.6f, 11f), MonsterKind.Grunt => (1.8f, 13f), _ => (2.7f, 1.1f) };
            // Walk unless close enough to attack (brutes always close in).
            if (m.Attacking <= 0 && (!sees || dist > range * 0.6f || m.Kind == MonsterKind.Brute) && dist > 0.8f)
                m.P = Slide(m.P, StepToward(m) * speed * Step, 0.25f);

            m.Cooldown -= Step;
            if (m.Cooldown > 0 || !sees || dist > range) continue;
            m.Attacking = 0.35f;
            switch (m.Kind)
            {
                case MonsterKind.Imp:
                    var dir = Vector2.Normalize(Player - m.P);
                    Fireballs.Add(new Fireball { P = m.P + dir * 0.3f, V = dir * (4.5f + 0.2f * Level), Damage = 10 + _rng.Next(9) + Level });
                    Events.Add(HellgateEvent.Fireball);
                    m.Cooldown = 1.5f + _rng.NextSingle() - MathF.Min(0.6f, 0.06f * Level);
                    break;
                case MonsterKind.Grunt:
                    if (_rng.NextSingle() < 0.8f - 0.045f * dist) HurtPlayer(5 + _rng.Next(8) + Level / 2);
                    Events.Add(HellgateEvent.Shot);
                    m.Cooldown = 1.3f + _rng.NextSingle() - MathF.Min(0.5f, 0.05f * Level);
                    break;
                default:
                    HurtPlayer(18 + _rng.Next(12) + Level);
                    m.Cooldown = 1.0f;
                    break;
            }
        }
    }

    /// <summary>The direction to the neighbouring cell nearer the player, or straight at
    /// the player when there's a clear line.</summary>
    private Vector2 StepToward(Monster m)
    {
        int x = (int)m.P.X, y = (int)m.P.Y;
        if (CanSee(m.P, Player)) return Vector2.Normalize(Player - m.P + new Vector2(0.001f, 0));
        var best = (x, y);
        var bestD = _flow[x, y];
        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            int nx = x + dx, ny = y + dy;
            if (!Solid(nx, ny) && _flow[nx, ny] < bestD) { bestD = _flow[nx, ny]; best = (nx, ny); }
        }
        var target = new Vector2(best.x + 0.5f, best.y + 0.5f);
        var d = target - m.P;
        return d.LengthSquared() < 1e-4f ? Vector2.Zero : Vector2.Normalize(d);
    }

    private void UpdateFireballs()
    {
        for (var i = 0; i < Fireballs.Count; i++)
        {
            var f = Fireballs[i];
            f.P += f.V * Step;
            if (Solid((int)f.P.X, (int)f.P.Y)) f.Dead = true;
            else if (Vector2.DistanceSquared(f.P, Player) < 0.35f * 0.35f) { f.Dead = true; HurtPlayer((int)f.Damage); }
            Fireballs[i] = f;
        }
        Fireballs.RemoveAll(static f => f.Dead);
    }

    // ------------------------------------------------------------------ tests

    /// <summary>Tests: every monster takes a hit.</summary>
    public void DamageAll(float amount) { foreach (var m in Monsters.ToList()) if (!m.Dead) Hurt(m, amount); }

    /// <summary>Tests: stand right in front of the exit switch, facing it.</summary>
    public void FaceExit()
    {
        for (var x = 0; x < MapW; x++)
            for (var y = 0; y < MapH; y++)
            {
                if (Map[x, y] != HellgateMap.ExitCell) continue;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    if (Solid(x + dx, y + dy)) continue;
                    Player = new(x + dx + 0.5f, y + dy + 0.5f);
                    Angle = MathF.Atan2(-dy, -dx);
                    return;
                }
            }
    }

    /// <summary>Tests: a monster right in front of the player, awake.</summary>
    public Monster SpawnAhead(MonsterKind kind, float distance)
    {
        var m = new Monster { Kind = kind, P = Player + Facing * distance, Hp = BaseHp(kind) * Toughness, Awake = true, Cooldown = 99 };
        Monsters.Add(m);
        return m;
    }
}
