using System.Numerics;

namespace Lounge.Core.Games;

/// <summary>Draws a Hellgate frame into a 320 x 200 ARGB buffer, the classic way: floor
/// and ceiling cast row by row, walls cast column by column (DDA) with one side darker,
/// distance fog, sprites sorted far to near and clipped against the walls, then the weapon
/// with its bob and muzzle flash, and a red or gold flash when hurt or picking up.</summary>
public sealed class HellgateRenderer
{
    public const int W = 320, H = 200;
    private const int T = HellgateArt.S;
    private const float Fov = 0.66f, FogDistance = 16;

    public uint[] Pixels { get; } = new uint[W * H];
    private readonly float[] _depth = new float[W];

    private static uint Shade(uint c, float f)
    {
        if (f >= 0.999f) return c;
        if (f <= 0) return 0xFF000000;
        uint r = (uint)(((c >> 16) & 255) * f), g = (uint)(((c >> 8) & 255) * f), b = (uint)((c & 255) * f);
        return 0xFF000000 | r << 16 | g << 8 | b;
    }

    private static float Fog(float dist) => Math.Clamp(1.15f - dist / FogDistance, 0.12f, 1f);

    public void Draw(HellgateGame g)
    {
        var px = Pixels;
        var dir = new Vector2(MathF.Cos(g.Angle), MathF.Sin(g.Angle));
        var plane = new Vector2(-dir.Y, dir.X) * Fov;
        var pos = g.Player;

        // ---- floor and ceiling
        var rayL = dir - plane;
        var rayR = dir + plane;
        for (var y = H / 2 + 1; y < H; y++)
        {
            var rowDist = (0.5f * H) / (y - H / 2f);
            var stepX = rowDist * (rayR.X - rayL.X) / W;
            var stepY = rowDist * (rayR.Y - rayL.Y) / W;
            var fx = pos.X + rowDist * rayL.X;
            var fy = pos.Y + rowDist * rayL.Y;
            var fog = Fog(rowDist);
            int floorRow = y * W, ceilRow = (H - 1 - y) * W;
            for (var x = 0; x < W; x++)
            {
                var tx = (int)(T * (fx - MathF.Floor(fx))) & (T - 1);
                var ty = (int)(T * (fy - MathF.Floor(fy))) & (T - 1);
                fx += stepX; fy += stepY;
                px[floorRow + x] = Shade(HellgateArt.Floor[ty * T + tx], fog);
                px[ceilRow + x] = Shade(HellgateArt.Ceiling[ty * T + tx], fog * 0.85f);
            }
        }
        // The middle row (exactly the horizon) has no cast; fill it dark.
        for (var x = 0; x < W; x++) px[H / 2 * W + x] = 0xFF050505;

        // ---- walls
        for (var x = 0; x < W; x++)
        {
            var camX = 2f * x / W - 1;
            var ray = dir + plane * camX;
            int mx = (int)pos.X, my = (int)pos.Y;
            float dX = MathF.Abs(1 / (ray.X == 0 ? 1e-6f : ray.X)), dY = MathF.Abs(1 / (ray.Y == 0 ? 1e-6f : ray.Y));
            int sx = ray.X < 0 ? -1 : 1, sy = ray.Y < 0 ? -1 : 1;
            float tx = ray.X < 0 ? (pos.X - mx) * dX : (mx + 1 - pos.X) * dX;
            float ty = ray.Y < 0 ? (pos.Y - my) * dY : (my + 1 - pos.Y) * dY;
            var side = 0;
            var cell = 0;
            for (var guard = 0; guard < 256; guard++)
            {
                if (tx < ty) { tx += dX; mx += sx; side = 0; } else { ty += dY; my += sy; side = 1; }
                if (mx < 0 || my < 0 || mx >= g.MapW || my >= g.MapH) { cell = 4; break; }
                cell = g.Map[mx, my];
                if (cell != 0) break;
            }
            var dist = side == 0 ? tx - dX : ty - dY;
            if (dist < 0.01f) dist = 0.01f;
            _depth[x] = dist;
            var lineH = (int)(H / dist);
            var top = -lineH / 2 + H / 2;
            var wallX = side == 0 ? pos.Y + dist * ray.Y : pos.X + dist * ray.X;
            wallX -= MathF.Floor(wallX);
            var texX = (int)(wallX * T);
            if ((side == 0 && ray.X > 0) || (side == 1 && ray.Y < 0)) texX = T - texX - 1;
            var tex = HellgateArt.Walls[Math.Clamp(cell, 1, 6)];
            // A rising door shows its lower part lifting away into the dark above.
            var lift = g.Doors.TryGetValue((mx, my), out var open) ? (int)(open * T) : 0;
            var fog = Fog(dist) * (side == 1 ? 0.72f : 1f);
            var y0 = Math.Max(0, top);
            var y1 = Math.Min(H, top + lineH);
            for (var y = y0; y < y1; y++)
            {
                var texY = (int)((y - top) * (float)T / lineH) + lift;
                px[y * W + x] = texY >= T ? 0xFF080808 : Shade(tex[(texY & (T - 1)) * T + texX], fog);
            }
        }

        // ---- sprites, far to near
        var sprites = new List<(Vector2 P, uint[] Tex, float Scale, float Lift)>();
        foreach (var m in g.Monsters)
        {
            var frame = m.Kind switch
            {
                MonsterKind.Imp => m.Dead ? HellgateArt.Sprite.ImpDead : m.Attacking > 0 ? HellgateArt.Sprite.ImpAttack : HellgateArt.Sprite.Imp,
                MonsterKind.Grunt => m.Dead ? HellgateArt.Sprite.GruntDead : m.Attacking > 0 ? HellgateArt.Sprite.GruntAttack : HellgateArt.Sprite.Grunt,
                _ => m.Dead ? HellgateArt.Sprite.BruteDead : m.Attacking > 0 ? HellgateArt.Sprite.BruteAttack : HellgateArt.Sprite.Brute,
            };
            sprites.Add((m.P, HellgateArt.Sprites[(int)frame], m.Kind == MonsterKind.Brute && !m.Dead ? 1.15f : 1f, 0));
        }
        foreach (var p in g.Pickups)
        {
            var s = p.Kind switch
            {
                PickupKind.Health => HellgateArt.Sprite.Health, PickupKind.Medkit => HellgateArt.Sprite.Medkit, PickupKind.Armor => HellgateArt.Sprite.Armor,
                PickupKind.Bullets => HellgateArt.Sprite.Bullets, PickupKind.Shells => HellgateArt.Sprite.Shells,
                PickupKind.Shotgun => HellgateArt.Sprite.Shotgun, _ => HellgateArt.Sprite.Chaingun,
            };
            sprites.Add((p.P, HellgateArt.Sprites[(int)s], 1f, 0));
        }
        foreach (var f in g.Fireballs) sprites.Add((f.P, HellgateArt.Sprites[(int)HellgateArt.Sprite.Fireball], 0.6f, 0.15f));

        var invDet = 1f / (plane.X * dir.Y - dir.X * plane.Y);
        foreach (var (p, tex, scale, lift) in sprites.OrderByDescending(s => Vector2.DistanceSquared(s.P, pos)))
        {
            var rel = p - pos;
            var camX = invDet * (dir.Y * rel.X - dir.X * rel.Y);
            var depth = invDet * (-plane.Y * rel.X + plane.X * rel.Y);
            if (depth <= 0.1f) continue;
            var screenX = (int)(W / 2f * (1 + camX / depth));
            var size = (int)(H / depth * scale);
            var bottom = (int)(H / 2f + H / depth / 2 - lift * H / depth);
            var top = bottom - size;
            int left = screenX - size / 2, right = screenX + size / 2;
            var fog = Fog(depth);
            for (var x = Math.Max(0, left); x < Math.Min(W, right); x++)
            {
                if (depth >= _depth[x]) continue;
                var texX = (x - left) * T / Math.Max(1, size);
                for (var y = Math.Max(0, top); y < Math.Min(H, bottom); y++)
                {
                    var c = tex[((y - top) * T / Math.Max(1, size)) * T + texX];
                    if (c != 0) px[y * W + x] = Shade(c, fog);
                }
            }
        }

        // ---- the weapon, bobbing as you walk
        if (g.Phase is HellgatePhase.Playing or HellgatePhase.LevelDone)
        {
            var bobX = (int)(MathF.Sin(g.Walked * 3.2f) * 6);
            var bobY = (int)(MathF.Abs(MathF.Cos(g.Walked * 3.2f)) * 5) + (g.WeaponCooldown > 0.05f && g.Weapon == 1 ? 6 : 0);
            int gx = W / 2 - HellgateArt.GunW / 2 + bobX + 20, gy = H - HellgateArt.GunH + bobY + 4;
            if (g.FlashTime > 0) Blit(HellgateArt.Flash, gx, gy + 6);   // on the muzzle (the barrels top out near y 30 in the weapon art)
            Blit(HellgateArt.Guns[g.Weapon], gx, gy);
        }

        // ---- flashes
        if (g.HurtTime > 0) Tint(0xFFFF2020, g.HurtTime * 1.2f);
        else if (g.PickupTime > 0) Tint(0xFFFFD040, g.PickupTime * 1.2f);
        if (g.Phase == HellgatePhase.Dead) Tint(0xFF800000, 0.45f);

        // A small crosshair.
        for (var i = -3; i <= 3; i++) { if (i == 0) continue; px[(H / 2) * W + W / 2 + i] = 0xFFE0E0E0; px[(H / 2 + i) * W + W / 2] = 0xFFE0E0E0; }
    }

    private void Blit(uint[] src, int x0, int y0)
    {
        int w = HellgateArt.GunW, h = HellgateArt.GunH;
        for (var y = 0; y < h; y++)
        {
            var dy = y0 + y;
            if (dy < 0 || dy >= H) continue;
            for (var x = 0; x < w; x++)
            {
                var dx = x0 + x;
                if (dx < 0 || dx >= W) continue;
                var c = src[y * w + x];
                if (c != 0) Pixels[dy * W + dx] = c;
            }
        }
    }

    private void Tint(uint color, float amount)
    {
        amount = Math.Clamp(amount, 0, 0.6f);
        uint tr = (color >> 16) & 255, tg = (color >> 8) & 255, tb = color & 255;
        for (var i = 0; i < Pixels.Length; i++)
        {
            var c = Pixels[i];
            uint r = (uint)(((c >> 16) & 255) * (1 - amount) + tr * amount);
            uint gg = (uint)(((c >> 8) & 255) * (1 - amount) + tg * amount);
            uint b = (uint)((c & 255) * (1 - amount) + tb * amount);
            Pixels[i] = 0xFF000000 | r << 16 | gg << 8 | b;
        }
    }
}
