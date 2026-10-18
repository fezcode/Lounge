using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Lounge.Controls;

/// <summary>The Arcade theme's backdrop, drawn as real pixel art: a 320 x 180 picture
/// re-drawn every frame and shown scaled up without smoothing. A night sky dithered into
/// bands, twinkling stars, a striped sun on the horizon, two ranges of mountains drifting
/// at different speeds, and a neon grid rolling towards the viewer.</summary>
public sealed class PixelScene
{
    public const int W = 320, H = 180;
    private const int Horizon = 112;

    private readonly uint[] _px = new uint[W * H];
    private readonly Color _sky, _glow, _grid;
    private readonly (int X, int Y, double Phase, double Speed)[] _stars;
    private readonly int[] _farRidge = new int[W * 4], _nearRidge = new int[W * 4];

    public WriteableBitmap Bitmap { get; } = new(new PixelSize(W, H), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    /// <param name="sky">The top of the sky.</param>
    /// <param name="glow">The horizon glow and the sun.</param>
    /// <param name="grid">The neon of the grid.</param>
    public PixelScene(Color sky, Color glow, Color grid)
    {
        _sky = sky; _glow = glow; _grid = grid;
        var rng = new Random(5);
        _stars = Enumerable.Range(0, 70).Select(_ => (rng.Next(W), rng.Next(Horizon - 30), rng.NextDouble() * 6.3, 0.6 + rng.NextDouble() * 1.8)).ToArray();
        // Ridges wrap every 4 screens, so they can scroll forever.
        for (int x = 0; x < _farRidge.Length; x++)
        {
            _farRidge[x] = 30 + (int)(Ridge(x * 0.021, 1) * 22);
            _nearRidge[x] = 14 + (int)(Ridge(x * 0.034, 7) * 18);
        }
    }

    /// <summary>Smooth hills from a few sines (seeded), 0..1.</summary>
    private static double Ridge(double x, int seed) =>
        0.5 + 0.28 * Math.Sin(x + seed) + 0.14 * Math.Sin(x * 2.3 + seed * 1.7) + 0.08 * Math.Sin(x * 5.1 + seed * 3.1);

    // 4 x 4 ordered dither: the textured banding of old palettes.
    private static readonly int[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    private static uint Pack(Color c) => (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    private static Color Dim(Color c, double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    public void Draw(double t)
    {
        var black = Color.FromRgb(6, 2, 16);
        // Sky: 8 bands from the night colour down to the glow, dithered where they meet.
        var bands = Enumerable.Range(0, 9).Select(i => Pack(Mix(Dim(_sky, 0.55), Mix(_sky, _glow, 0.55), i / 8.0))).ToArray();
        for (int y = 0; y < Horizon; y++)
        {
            var pos = Math.Pow(y / (double)Horizon, 1.6) * 8;
            var band = (int)pos;
            var frac = pos - band;
            for (int x = 0; x < W; x++)
            {
                var step = frac * 16 > Bayer[(y & 3) * 4 + (x & 3)] ? 1 : 0;
                _px[y * W + x] = bands[Math.Min(8, band + step)];
            }
        }

        // Stars twinkle in three steps of brightness.
        foreach (var (sx, sy, phase, speed) in _stars)
        {
            var b = Math.Sin(t * speed + phase);
            if (b < -0.3) continue;
            var c = b > 0.6 ? Color.FromRgb(255, 255, 255) : b > 0.1 ? Color.FromRgb(190, 200, 255) : Color.FromRgb(120, 110, 190);
            _px[sy * W + sx] = Pack(c);
            if (b > 0.9 && sx > 0 && sx < W - 1 && sy > 0) { _px[sy * W + sx - 1] = _px[sy * W + sx + 1] = _px[(sy - 1) * W + sx] = Pack(Dim(c, 0.6)); }
        }

        // The sun: a disc fading from yellow to the glow, cut by stripes that thicken downwards.
        const int cx = W / 2, cy = Horizon - 14, r = 34;
        var sunTop = Color.FromRgb(255, 222, 89);
        for (int y = cy - r; y <= Horizon; y++)
        {
            var dy = y - cy;
            var gap = y > cy - 4 ? (y - (cy - 4)) / 5 + 1 : 0;   // stripe thickness grows towards the horizon
            if (gap > 0 && ((y - (cy - 4)) % 7) < Math.Min(gap, 5)) continue;
            var half = (int)Math.Sqrt(Math.Max(0, r * r - dy * dy));
            var col = Pack(Mix(sunTop, _glow, (y - (cy - r)) / (double)(r * 2)));
            for (int x = cx - half; x <= cx + half; x++) if (x is >= 0 and < W && y is >= 0 and < H) _px[y * W + x] = col;
        }

        // Mountains: a far range and a near one, sliding at different speeds.
        Range(_farRidge, (int)(t * 2) % _farRidge.Length, Pack(Mix(_sky, black, 0.25)), Pack(Mix(_sky, _glow, 0.25)));
        Range(_nearRidge, (int)(t * 5) % _nearRidge.Length, Pack(Mix(_sky, black, 0.6)), Pack(Mix(_sky, _glow, 0.4)));

        // The grid: rows rush towards the viewer, columns fan out from the vanishing point.
        var ground = Pack(black);
        var scroll = t * 1.6;
        for (int y = Horizon; y < H; y++)
        {
            var dy = y - Horizon + 1;
            var z = 90.0 / dy;                          // depth of this row
            var row = (z + scroll) % 1.0;
            var near = dy / (double)(H - Horizon);       // 0 at the horizon, 1 at the bottom
            var line = Pack(Mix(Dim(_grid, 0.35), _grid, near));
            var isRow = row < 0.06 + 0.1 * near;
            for (int x = 0; x < W; x++)
            {
                var wx = (x - cx) * z / 6.0;
                var col = Math.Abs(wx - Math.Round(wx / 4) * 4) < 0.06 * z + 0.02;
                _px[y * W + x] = isRow || col ? line : ground;
            }
        }
        // A bright seam where the grid meets the sky.
        for (int x = 0; x < W; x++) _px[Horizon * W + x] = Pack(Mix(_glow, Color.FromRgb(255, 255, 255), 0.3));

        using var fb = Bitmap.Lock();
        unsafe
        {
            fixed (uint* src = _px)
                for (int y = 0; y < H; y++)
                    Buffer.MemoryCopy(src + y * W, (byte*)fb.Address + y * fb.RowBytes, fb.RowBytes, W * 4);
        }
    }

    /// <summary>One mountain range: a solid silhouette with a lit ridge line.</summary>
    private void Range(int[] ridge, int offset, uint fill, uint edge)
    {
        for (int x = 0; x < W; x++)
        {
            var h = ridge[(x + offset) % ridge.Length];
            var top = Horizon - h;
            for (int y = Math.Max(0, top); y < Horizon; y++) _px[y * W + x] = y == top ? edge : fill;
        }
    }

    private static WriteableBitmap? _scanlines;

    /// <summary>CRT scanlines: one dark row in every three, tiled over the whole screen.</summary>
    public static IBrush Scanlines()
    {
        if (_scanlines is null)
        {
            _scanlines = new WriteableBitmap(new PixelSize(1, 3), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var fb = _scanlines.Lock();
            unsafe
            {
                var p = (uint*)fb.Address;
                p[0] = 0; *(uint*)((byte*)fb.Address + fb.RowBytes) = 0; *(uint*)((byte*)fb.Address + 2 * fb.RowBytes) = 0xB0000000;
            }
        }
        return new ImageBrush(_scanlines) { TileMode = TileMode.Tile, Stretch = Stretch.None, DestinationRect = new RelativeRect(0, 0, 1, 3, RelativeUnit.Absolute) };
    }
}
