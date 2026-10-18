namespace Lounge.Core.Games;

/// <summary>Hellgate's textures and sprites, drawn by code into 64 x 64 ARGB pixels so the
/// game ships no image files: brick, metal, tech panels, stone, a hazard door and the exit
/// switch for walls; floor and ceiling flats; three monsters (each with an attack and a
/// dead frame), a fireball, pickups, and the first-person weapons.</summary>
public static class HellgateArt
{
    public const int S = 64;

    /// <summary>Wall textures by map cell value (1-4 walls, 5 door, 6 exit switch).</summary>
    public static readonly uint[][] Walls = new uint[7][];
    public static readonly uint[] Floor, Ceiling;

    public enum Sprite { Imp, ImpAttack, ImpDead, Grunt, GruntAttack, GruntDead, Brute, BruteAttack, BruteDead, Fireball, Health, Medkit, Armor, Bullets, Shells, Shotgun, Chaingun, Count }
    public static readonly uint[][] Sprites = new uint[(int)Sprite.Count][];

    /// <summary>First-person weapons: 128 x 96, drawn at the bottom of the view.</summary>
    public const int GunW = 128, GunH = 96;
    public static readonly uint[][] Guns = new uint[3][];   // pistol, shotgun, chaingun
    public static readonly uint[] Flash;

    static HellgateArt()
    {
        var rng = new Random(66);
        Walls[1] = Brick(rng);
        Walls[2] = Metal(rng);
        Walls[3] = Tech(rng);
        Walls[4] = Stone(rng);
        Walls[5] = Door(rng);
        Walls[6] = ExitSwitch(rng);
        Floor = Tiles(rng, 0xFF3A3530, 0xFF2A2622, 16);
        Ceiling = Tiles(rng, 0xFF26242A, 0xFF1C1A20, 32);
        Sprites[(int)Sprite.Imp] = Imp(false);
        Sprites[(int)Sprite.ImpAttack] = Imp(true);
        Sprites[(int)Sprite.ImpDead] = Corpse(0xFF8A3A1E, 0xFF5A200E);
        Sprites[(int)Sprite.Grunt] = Grunt(false);
        Sprites[(int)Sprite.GruntAttack] = Grunt(true);
        Sprites[(int)Sprite.GruntDead] = Corpse(0xFF4A6A34, 0xFF7A1010);
        Sprites[(int)Sprite.Brute] = Brute(false);
        Sprites[(int)Sprite.BruteAttack] = Brute(true);
        Sprites[(int)Sprite.BruteDead] = Corpse(0xFFC8708A, 0xFF8A1A2A);
        Sprites[(int)Sprite.Fireball] = Fireball();
        Sprites[(int)Sprite.Health] = Box(0xFFE8E8E8, 0xFFD02020, cross: true, small: true);
        Sprites[(int)Sprite.Medkit] = Box(0xFFE8E8E8, 0xFFD02020, cross: true, small: false);
        Sprites[(int)Sprite.Armor] = Armor();
        Sprites[(int)Sprite.Bullets] = Box(0xFFC8A030, 0xFF6A5010, cross: false, small: true);
        Sprites[(int)Sprite.Shells] = Shells();
        Sprites[(int)Sprite.Shotgun] = GunPickup(long_: true);
        Sprites[(int)Sprite.Chaingun] = GunPickup(long_: false);
        Guns[0] = Pistol();
        Guns[1] = ShotgunView();
        Guns[2] = ChaingunView();
        Flash = MuzzleFlash();
    }

    // ------------------------------------------------------------------ drawing helpers

    private static uint Shade(uint c, double f)
    {
        f = Math.Clamp(f, 0, 2);
        uint r = (uint)Math.Min(255, ((c >> 16) & 255) * f), g = (uint)Math.Min(255, ((c >> 8) & 255) * f), b = (uint)Math.Min(255, (c & 255) * f);
        return (c & 0xFF000000) | r << 16 | g << 8 | b;
    }

    private static void Rect(uint[] px, int w, int x0, int y0, int x1, int y1, uint c)
    {
        var h = px.Length / w;
        for (var y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
            for (var x = Math.Max(0, x0); x < Math.Min(w, x1); x++) px[y * w + x] = c;
    }

    private static void Ellipse(uint[] px, int w, double cx, double cy, double rx, double ry, uint c)
    {
        var h = px.Length / w;
        for (var y = (int)(cy - ry); y <= (int)(cy + ry); y++)
            for (var x = (int)(cx - rx); x <= (int)(cx + rx); x++)
            {
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                var dx = (x + 0.5 - cx) / rx; var dy = (y + 0.5 - cy) / ry;
                if (dx * dx + dy * dy <= 1) px[y * w + x] = c;
            }
    }

    /// <summary>A dark outline around every opaque pixel, so sprites read against walls.</summary>
    private static void Outline(uint[] px, int w, uint c = 0xFF100808)
    {
        var h = px.Length / w;
        var copy = (uint[])px.Clone();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (copy[y * w + x] != 0) continue;
                for (var d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h && copy[ny * w + nx] != 0) { px[y * w + x] = c; break; }
                }
            }
    }

    private static void Noise(uint[] px, Random rng, double amount)
    {
        for (var i = 0; i < px.Length; i++) if (px[i] != 0) px[i] = Shade(px[i], 1 + (rng.NextDouble() - 0.5) * amount);
    }

    // ------------------------------------------------------------------ walls and flats

    private static uint[] Brick(Random rng)
    {
        var px = new uint[S * S];
        Rect(px, S, 0, 0, S, S, 0xFF4A3A32);   // mortar
        for (var row = 0; row < 8; row++)
            for (var col = -1; col < 4; col++)
            {
                var x0 = col * 16 + (row % 2 == 0 ? 0 : 8);
                var tone = 0.85 + rng.NextDouble() * 0.3;
                Rect(px, S, x0 + 1, row * 8 + 1, x0 + 16, row * 8 + 8, Shade(0xFF8A3A26, tone));
            }
        Noise(px, rng, 0.25);
        return px;
    }

    private static uint[] Metal(Random rng)
    {
        var px = new uint[S * S];
        Rect(px, S, 0, 0, S, S, 0xFF6A7078);
        for (var p = 0; p < 2; p++)
        {
            Rect(px, S, 0, p * 32, S, p * 32 + 2, 0xFF3A3E44);
            Rect(px, S, 0, p * 32 + 2, S, p * 32 + 3, 0xFF9AA0A8);
            foreach (var rx in new[] { 5, 58 }) foreach (var ry in new[] { 7, 26 }) { Rect(px, S, rx, p * 32 + ry, rx + 2, p * 32 + ry + 2, 0xFF2A2E34); Rect(px, S, rx, p * 32 + ry, rx + 1, p * 32 + ry + 1, 0xFFC8CCD2); }
        }
        Noise(px, rng, 0.12);
        return px;
    }

    private static uint[] Tech(Random rng)
    {
        var px = new uint[S * S];
        Rect(px, S, 0, 0, S, S, 0xFF2A3036);
        Rect(px, S, 6, 6, 58, 40, 0xFF1A1E22);
        for (var y = 10; y < 36; y += 4) Rect(px, S, 10, y, 10 + rng.Next(10, 44), y + 2, rng.Next(3) == 0 ? 0xFF30E070 : 0xFF208A4A);
        Rect(px, S, 0, 46, S, 48, 0xFF4A545C);
        for (var x = 4; x < S; x += 12) Rect(px, S, x, 52, x + 6, 60, rng.Next(2) == 0 ? 0xFFE0A020 : 0xFF404850);
        Noise(px, rng, 0.1);
        return px;
    }

    private static uint[] Stone(Random rng)
    {
        var px = new uint[S * S];
        Rect(px, S, 0, 0, S, S, 0xFF3A3A3E);
        for (var row = 0; row < 4; row++)
            for (var col = 0; col < 3; col++)
            {
                var x0 = col * 22 + (row % 2) * 11 - 11;
                Rect(px, S, x0 + 1, row * 16 + 1, x0 + 21, row * 16 + 15, Shade(0xFF707074, 0.8 + rng.NextDouble() * 0.35));
            }
        Noise(px, rng, 0.35);
        return px;
    }

    private static uint[] Door(Random rng)
    {
        var px = new uint[S * S];
        Rect(px, S, 0, 0, S, S, 0xFF5A5E64);
        Rect(px, S, 4, 4, 60, 60, 0xFF767C84);
        // Hazard stripes across the middle.
        for (var y = 26; y < 38; y++) for (var x = 4; x < 60; x++) px[y * S + x] = ((x + y) / 5) % 2 == 0 ? 0xFFE0B020 : 0xFF202020;
        Rect(px, S, 31, 4, 33, 26, 0xFF3A3E44);
        Rect(px, S, 31, 38, 33, 60, 0xFF3A3E44);
        Noise(px, rng, 0.1);
        return px;
    }

    private static uint[] ExitSwitch(Random rng)
    {
        var px = Tech(rng);
        Rect(px, S, 18, 8, 46, 22, 0xFF101010);
        // "EXIT" in tiny red lamps.
        string[] glyphs = ["###.#.#.###.###", "#...#.#..#...#.", "##...#...#...#.", "#...#.#..#...#.", "###.#.#.###..#."];
        for (var r = 0; r < glyphs.Length; r++)
            for (var c = 0; c < glyphs[r].Length; c++)
                if (glyphs[r][c] == '#') Rect(px, S, 20 + c * 2 - c / 4, 10 + r * 2, 21 + c * 2 - c / 4, 11 + r * 2, 0xFFFF3030);
        Rect(px, S, 26, 28, 38, 44, 0xFF202428);
        Rect(px, S, 29, 30, 35, 42, 0xFF30E050);   // the switch
        return px;
    }

    private static uint[] Tiles(Random rng, uint a, uint b, int size)
    {
        var px = new uint[S * S];
        for (var y = 0; y < S; y++)
            for (var x = 0; x < S; x++)
                px[y * S + x] = (x % size == 0 || y % size == 0) ? Shade(b, 0.7) : ((x / size + y / size) % 2 == 0 ? a : b);
        Noise(px, rng, 0.18);
        return px;
    }

    // ------------------------------------------------------------------ monsters

    private static uint[] Imp(bool attack)
    {
        var px = new uint[S * S];
        const uint skin = 0xFF9A4A26, dark = 0xFF6A2A14;
        Ellipse(px, S, 32, 40, 13, 16, skin);                      // body
        Ellipse(px, S, 32, 18, 9, 9, skin);                        // head
        Rect(px, S, 24, 52, 29, 64, dark); Rect(px, S, 35, 52, 40, 64, dark);   // legs
        // Horns, eyes, and arms (raised with a fireball when attacking).
        Rect(px, S, 24, 8, 26, 13, 0xFFE0D0B0); Rect(px, S, 38, 8, 40, 13, 0xFFE0D0B0);
        Rect(px, S, 27, 16, 31, 19, 0xFFFFE040); Rect(px, S, 33, 16, 37, 19, 0xFFFFE040);
        if (attack)
        {
            Rect(px, S, 14, 14, 20, 34, skin); Rect(px, S, 44, 14, 50, 34, skin);
            Ellipse(px, S, 32, 6, 7, 6, 0xFFFF8A20); Ellipse(px, S, 32, 6, 3, 3, 0xFFFFF0A0);
        }
        else { Rect(px, S, 16, 30, 21, 50, skin); Rect(px, S, 43, 30, 48, 50, skin); }
        Rect(px, S, 26, 24, 38, 26, 0xFF3A0A04);                   // mouth
        Outline(px, S);
        return px;
    }

    private static uint[] Grunt(bool attack)
    {
        var px = new uint[S * S];
        const uint suit = 0xFF4A6A34, skin = 0xFFD0A080;
        Rect(px, S, 22, 24, 42, 50, suit);                         // torso
        Ellipse(px, S, 32, 16, 8, 9, skin);                        // head
        Rect(px, S, 23, 6, 41, 12, 0xFF3A4A2A);                    // helmet
        Rect(px, S, 27, 15, 30, 17, 0xFF101010); Rect(px, S, 34, 15, 37, 17, 0xFF101010);
        Rect(px, S, 23, 50, 30, 64, 0xFF34482A); Rect(px, S, 34, 50, 41, 64, 0xFF34482A);
        // The rifle, levelled at you when firing.
        if (attack) { Rect(px, S, 26, 30, 38, 38, 0xFF2A2A2A); Ellipse(px, S, 32, 34, 6, 6, 0xFFFFE070); }
        else Rect(px, S, 40, 26, 44, 50, 0xFF2A2A2A);
        Rect(px, S, 18, 26, 22, 44, suit); Rect(px, S, 42, 26, 46, 44, suit);
        Outline(px, S);
        return px;
    }

    private static uint[] Brute(bool attack)
    {
        var px = new uint[S * S];
        const uint skin = 0xFFC8708A, dark = 0xFF8A4058;
        Ellipse(px, S, 32, 40, 24, 18, skin);                      // hulking body
        Ellipse(px, S, 32, attack ? 26 : 30, 14, 11, dark);         // head thrust forward when biting
        Rect(px, S, 22, attack ? 30 : 34, 42, attack ? 38 : 38, 0xFF2A0A10); // jaws
        for (var x = 23; x < 42; x += 3) { Rect(px, S, x, attack ? 30 : 34, x + 1, attack ? 33 : 36, 0xFFF0F0E0); }
        Rect(px, S, 25, 22, 29, 25, 0xFFFF2020); Rect(px, S, 35, 22, 39, 25, 0xFFFF2020);
        Rect(px, S, 12, 52, 22, 64, dark); Rect(px, S, 42, 52, 52, 64, dark);
        Outline(px, S);
        return px;
    }

    private static uint[] Corpse(uint body, uint blood)
    {
        var px = new uint[S * S];
        Ellipse(px, S, 32, 58, 26, 5, blood);
        Ellipse(px, S, 30, 55, 16, 5, body);
        Ellipse(px, S, 44, 56, 5, 3, Shade(body, 0.8));
        Outline(px, S);
        return px;
    }

    private static uint[] Fireball()
    {
        var px = new uint[S * S];
        Ellipse(px, S, 32, 32, 14, 14, 0xFFE04010);
        Ellipse(px, S, 32, 32, 10, 10, 0xFFFF9020);
        Ellipse(px, S, 32, 32, 5, 5, 0xFFFFF0B0);
        return px;
    }

    // ------------------------------------------------------------------ pickups

    private static uint[] Box(uint body, uint mark, bool cross, bool small)
    {
        var px = new uint[S * S];
        int half = small ? 9 : 14, top = 64 - half * 2;
        Rect(px, S, 32 - half, top, 32 + half, 64, body);
        Rect(px, S, 32 - half, top, 32 + half, top + 2, Shade(body, 1.2));
        if (cross) { Rect(px, S, 30, top + 4, 34, 60, mark); Rect(px, S, 32 - half + 4, top + half - 2, 32 + half - 4, top + half + 2, mark); }
        else for (var x = 32 - half + 3; x < 32 + half - 2; x += 4) Rect(px, S, x, top + 3, x + 2, 62, mark);
        Outline(px, S);
        return px;
    }

    private static uint[] Armor()
    {
        var px = new uint[S * S];
        Ellipse(px, S, 32, 50, 14, 14, 0xFF30A040);
        Rect(px, S, 26, 36, 38, 40, 0);   // neck hole (transparent)
        Rect(px, S, 22, 46, 42, 48, 0xFF60D070);
        Outline(px, S);
        return px;
    }

    private static uint[] Shells()
    {
        var px = new uint[S * S];
        for (var i = 0; i < 4; i++) { Rect(px, S, 20 + i * 7, 46, 25 + i * 7, 62, 0xFFC02020); Rect(px, S, 20 + i * 7, 58, 25 + i * 7, 62, 0xFFD0B040); }
        Outline(px, S);
        return px;
    }

    private static uint[] GunPickup(bool long_)
    {
        var px = new uint[S * S];
        Rect(px, S, 10, 54, long_ ? 56 : 48, 58, 0xFF404448);
        Rect(px, S, 10, 54, 24, 62, 0xFF6A4A2A);
        if (!long_) for (var i = 0; i < 3; i++) Rect(px, S, 40, 52 + i * 2, 54, 53 + i * 2, 0xFF2A2E32);
        Outline(px, S);
        return px;
    }

    // ------------------------------------------------------------------ first-person weapons

    private static uint[] Pistol()
    {
        var px = new uint[GunW * GunH];
        Rect(px, GunW, 56, 40, 72, 96, 0xFF505458);            // barrel and slide
        Rect(px, GunW, 58, 36, 70, 44, 0xFF6A6E74);
        Rect(px, GunW, 50, 64, 78, 96, 0xFFD0A080);            // hand
        Rect(px, GunW, 54, 70, 74, 76, 0xFFB08060);
        Outline(px, GunW);
        return px;
    }

    private static uint[] ShotgunView()
    {
        var px = new uint[GunW * GunH];
        Rect(px, GunW, 52, 24, 62, 96, 0xFF3A3E42);            // double barrel
        Rect(px, GunW, 66, 24, 76, 96, 0xFF3A3E42);
        Rect(px, GunW, 50, 56, 78, 72, 0xFF7A5A34);            // pump
        Rect(px, GunW, 44, 74, 84, 96, 0xFFD0A080);            // hands
        Outline(px, GunW);
        return px;
    }

    private static uint[] ChaingunView()
    {
        var px = new uint[GunW * GunH];
        for (var i = 0; i < 4; i++) Rect(px, GunW, 48 + i * 9, 20, 54 + i * 9, 80, i % 2 == 0 ? 0xFF4A4E52 : 0xFF2A2E32);
        Rect(px, GunW, 44, 40, 86, 48, 0xFF5A5E62);
        Rect(px, GunW, 42, 72, 88, 96, 0xFFD0A080);
        Outline(px, GunW);
        return px;
    }

    private static uint[] MuzzleFlash()
    {
        var px = new uint[GunW * GunH];
        Ellipse(px, GunW, 64, 18, 22, 16, 0xFFFF9020);
        Ellipse(px, GunW, 64, 18, 13, 10, 0xFFFFE070);
        Ellipse(px, GunW, 64, 18, 6, 5, 0xFFFFFFF0);
        return px;
    }
}
