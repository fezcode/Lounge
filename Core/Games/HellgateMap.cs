using System.Numerics;

namespace Lounge.Core.Games;

/// <summary>Builds Hellgate's levels: rooms carved out of solid rock, joined by corridors,
/// doors where corridors meet rooms, the exit switch in the room farthest from the start,
/// and monsters and pickups through the rooms. Each level is larger, holds more monsters,
/// and brings tougher ones in (brutes from level 2), so it keeps getting harder.</summary>
public static class HellgateMap
{
    public const int DoorCell = 5, ExitCell = 6;

    public sealed record Level(int[,] Cells, Vector2 Start, float StartAngle,
        List<(MonsterKind Kind, Vector2 P)> Monsters, List<(PickupKind Kind, Vector2 P)> Pickups);

    private readonly record struct Room(int X, int Y, int W, int H)
    {
        public (int X, int Y) Centre => (X + W / 2, Y + H / 2);
        public bool Overlaps(Room o, int gap) => X - gap < o.X + o.W && o.X - gap < X + W && Y - gap < o.Y + o.H && o.Y - gap < Y + H;
        public Vector2 Spot(Random rng) => new(X + 0.5f + rng.Next(W), Y + 0.5f + rng.Next(H));
    }

    public static Level Generate(int level, Random rng)
    {
        var size = Math.Min(24 + level * 4, 64);
        var cells = new int[size, size];
        // Every level has a look: one main wall texture, with a second for some rooms.
        var main = 1 + (level - 1) % 4;
        var accent = 1 + level % 4;
        for (var x = 0; x < size; x++) for (var y = 0; y < size; y++) cells[x, y] = main;

        var want = Math.Min(5 + level, 14);
        var rooms = new List<Room>();
        for (var tries = 0; tries < 400 && rooms.Count < want; tries++)
        {
            int w = rng.Next(4, 9), h = rng.Next(4, 9);
            var r = new Room(rng.Next(1, size - w - 1), rng.Next(1, size - h - 1), w, h);
            if (rooms.Any(o => o.Overlaps(r, 2))) continue;
            rooms.Add(r);
        }
        // Carve the rooms; some get the accent texture around them.
        for (var i = 0; i < rooms.Count; i++)
        {
            var r = rooms[i];
            var skin = i % 3 == 2 ? accent : main;
            for (var x = r.X - 1; x <= r.X + r.W; x++) for (var y = r.Y - 1; y <= r.Y + r.H; y++) if (In(x, y)) cells[x, y] = skin;
            for (var x = r.X; x < r.X + r.W; x++) for (var y = r.Y; y < r.Y + r.H; y++) cells[x, y] = 0;
        }
        // Join each room to the next with an L-shaped corridor; a door where it leaves a room.
        rooms = rooms.OrderBy(r => r.Centre.X + r.Centre.Y * 0.5).ToList();
        for (var i = 1; i < rooms.Count; i++)
        {
            var (ax, ay) = rooms[i - 1].Centre;
            var (bx, by) = rooms[i].Centre;
            var horizontalFirst = rng.Next(2) == 0;
            var path = new List<(int, int)>();
            if (horizontalFirst) { Line(ax, bx, ay, true, path); Line(ay, by, bx, false, path); }
            else { Line(ay, by, ax, false, path); Line(ax, bx, by, true, path); }
            var doorPlaced = false;
            foreach (var (x, y) in path)
            {
                if (!In(x, y)) continue;
                var wasRock = cells[x, y] != 0;
                cells[x, y] = 0;
                // The first rock cell just outside room A becomes a door, if it sits in a wall gap.
                if (!doorPlaced && wasRock && Inside(rooms[i - 1], x, y) == false && WallGap(cells, x, y) && rng.Next(3) != 0)
                {
                    cells[x, y] = DoorCell;
                    doorPlaced = true;
                }
            }
        }

        // The start is the first room; the exit goes in the one farthest from it by walking.
        var start = rooms[0];
        var (sx, sy) = start.Centre;
        var dist = Distances(cells, sx, sy, size);
        var far = rooms.Skip(1).OrderByDescending(r => dist[r.Centre.X, r.Centre.Y] == int.MaxValue ? -1 : dist[r.Centre.X, r.Centre.Y]).FirstOrDefault(start);
        PlaceExit(cells, far, rng);

        var monsters = new List<(MonsterKind, Vector2)>();
        var pickups = new List<(PickupKind, Vector2)>();
        var count = Math.Min(4 + level * 3, 60);
        var others = rooms.Where(r => r != start).ToList();
        if (others.Count == 0) others.Add(start);
        for (var i = 0; i < count; i++)
        {
            var roll = rng.NextDouble();
            var brute = level >= 2 && roll < Math.Min(0.1 + 0.04 * level, 0.35);
            var kind = brute ? MonsterKind.Brute : roll < 0.62 ? MonsterKind.Imp : MonsterKind.Grunt;
            monsters.Add((kind, others[rng.Next(others.Count)].Spot(rng)));
        }
        foreach (var r in rooms)
        {
            if (rng.Next(2) == 0) pickups.Add((rng.Next(4) == 0 ? PickupKind.Medkit : PickupKind.Health, r.Spot(rng)));
            if (rng.Next(2) == 0) pickups.Add((PickupKind.Bullets, r.Spot(rng)));
            if (level >= 2 && rng.Next(3) == 0) pickups.Add((PickupKind.Shells, r.Spot(rng)));
            if (rng.Next(6) == 0) pickups.Add((PickupKind.Armor, r.Spot(rng)));
        }
        // Weapons where you'll find them early.
        pickups.Add((PickupKind.Shotgun, others[Math.Min(1, others.Count - 1)].Spot(rng)));
        if (level >= 3) pickups.Add((PickupKind.Chaingun, others[Math.Min(2, others.Count - 1)].Spot(rng)));

        // Face down the longest open line out of the start.
        var startPos = new Vector2(sx + 0.5f, sy + 0.5f);
        var angle = new[] { 0f, MathF.PI / 2, MathF.PI, -MathF.PI / 2 }
            .OrderByDescending(a => Reach(cells, startPos, a, size)).First();
        return new Level(cells, startPos, angle, monsters, pickups);

        bool In(int x, int y) => x > 0 && y > 0 && x < size - 1 && y < size - 1;
    }

    private static void Line(int from, int to, int fixedCoord, bool horizontal, List<(int, int)> path)
    {
        var step = from <= to ? 1 : -1;
        for (var v = from; v != to + step; v += step) path.Add(horizontal ? (v, fixedCoord) : (fixedCoord, v));
    }

    private static bool Inside(Room r, int x, int y) => x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H;

    /// <summary>Rock on both sides across the corridor: a doorway.</summary>
    private static bool WallGap(int[,] c, int x, int y) =>
        (c[x - 1, y] != 0 && c[x + 1, y] != 0) || (c[x, y - 1] != 0 && c[x, y + 1] != 0);

    private static int[,] Distances(int[,] cells, int sx, int sy, int size)
    {
        var d = new int[size, size];
        for (var x = 0; x < size; x++) for (var y = 0; y < size; y++) d[x, y] = int.MaxValue;
        var q = new Queue<(int, int)>();
        d[sx, sy] = 0; q.Enqueue((sx, sy));
        while (q.Count > 0)
        {
            var (x, y) = q.Dequeue();
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= size || ny >= size || d[nx, ny] != int.MaxValue) continue;
                if (cells[nx, ny] != 0 && cells[nx, ny] != DoorCell) continue;
                d[nx, ny] = d[x, y] + 1;
                q.Enqueue((nx, ny));
            }
        }
        return d;
    }

    /// <summary>The exit switch replaces a wall cell on the far room's edge.</summary>
    private static void PlaceExit(int[,] cells, Room r, Random rng)
    {
        var spots = new List<(int, int)>();
        for (var x = r.X; x < r.X + r.W; x++) { spots.Add((x, r.Y - 1)); spots.Add((x, r.Y + r.H)); }
        for (var y = r.Y; y < r.Y + r.H; y++) { spots.Add((r.X - 1, y)); spots.Add((r.X + r.W, y)); }
        var ok = spots.Where(s => cells[s.Item1, s.Item2] is > 0 and < DoorCell).ToList();
        var (ex, ey) = ok.Count > 0 ? ok[rng.Next(ok.Count)] : (r.X - 1, r.Y);
        cells[ex, ey] = ExitCell;
    }

    private static float Reach(int[,] cells, Vector2 from, float angle, int size)
    {
        var d = 0f;
        while (d < size)
        {
            var p = from + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (d + 0.5f);
            if (cells[(int)p.X, (int)p.Y] != 0) break;
            d += 0.5f;
        }
        return d;
    }
}
