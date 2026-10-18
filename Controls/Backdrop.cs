using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Threading;
using Lounge.Core.Themes;
using Lounge.Shell;

namespace Lounge.Controls;

/// <summary>The scene behind every screen. Each theme draws its own living backdrop
/// (aurora ribbons, a sunset wash, fanart dusk, console light waves, a green glow) with
/// slow drifting motion. Focused content can bring its own art, which crossfades in over
/// the scene, blurred and scrimmed so text stays readable.</summary>
public sealed class Backdrop : Grid
{
    private readonly Grid _scene = new();
    private readonly Border _artA = new() { Opacity = 0 };
    private readonly Border _artB = new() { Opacity = 0 };
    private readonly Border _scrim = new() { IsHitTestVisible = false };
    private readonly Border _wallpaper = new() { Opacity = 0 };
    private bool _aFront;
    private string? _art;
    private readonly Spring _fadeA, _fadeB, _wallFade;
    private readonly DispatcherTimer _timer;
    private double _t;
    private Action<double>? _animate;

    public bool Animated { get; set; } = true;

    public Backdrop()
    {
        ClipToBounds = true;
        Children.Add(_scene);
        Children.Add(_wallpaper);
        Children.Add(_artA);
        Children.Add(_artB);
        Children.Add(_scrim);
        _fadeA = new Spring(0, v => _artA.Opacity = v, 120, 22);
        _fadeB = new Spring(0, v => _artB.Opacity = v, 120, 22);
        _wallFade = new Spring(0, v => _wallpaper.Opacity = v, 120, 22);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Step();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) UpdateTimer();
        };
        Restyle();
    }

    private void UpdateTimer()
    {
        if (IsVisible && Animated && Motion.Enabled && _animate is not null) _timer.Start(); else _timer.Stop();
    }

    private void Step()
    {
        _t += 0.04 * Motion.Speed;
        _animate?.Invoke(_t);
    }

    /// <summary>Rebuilds the scene for the current theme.</summary>
    public void Restyle()
    {
        _scene.Children.Clear();
        _animate = null;
        var d = T.Def;
        var bg = Color.Parse(d.Bg0);
        var a = Color.Parse(d.BackdropA);
        var b = Color.Parse(d.BackdropB);
        var c = Color.Parse(d.BackdropC);
        _scene.Background = new SolidColorBrush(Color.Parse(d.Bg1));

        switch (d.Backdrop)
        {
            case BackdropStyle.Aurora:
            {
                var blobs = new[] { (a, 0.25, 0.3, 0.75), (b, 0.75, 0.25, 0.65), (c, 0.55, 0.85, 0.7) }
                    .Select(x => Blob(x.Item1, x.Item2, x.Item3, x.Item4, 0.85)).ToArray();
                foreach (var bl in blobs) _scene.Children.Add(bl.Shape);
                _animate = t =>
                {
                    for (int i = 0; i < blobs.Length; i++)
                    {
                        var (shape, brush, cx, cy) = blobs[i];
                        brush.Center = new RelativePoint(cx + 0.12 * Math.Sin(t * 0.13 + i * 2.1), cy + 0.08 * Math.Cos(t * 0.11 + i * 1.3), RelativeUnit.Relative);
                        brush.GradientOrigin = brush.Center;
                    }
                };
                break;
            }
            case BackdropStyle.Sunset:
            {
                _scene.Children.Add(new Border
                {
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                        GradientStops = { new GradientStop(c, 0), new GradientStop(b, 0.55), new GradientStop(a, 1) },
                    },
                    Opacity = 0.75,
                });
                var glow = Blob(Color.FromArgb(255, 255, 190, 120), 0.75, 0.9, 0.6, 0.35);
                _scene.Children.Add(glow.Shape);
                _animate = t =>
                {
                    glow.Brush.Center = new RelativePoint(0.7 + 0.08 * Math.Sin(t * 0.07), 0.9 + 0.04 * Math.Cos(t * 0.09), RelativeUnit.Relative);
                    glow.Brush.GradientOrigin = glow.Brush.Center;
                };
                break;
            }
            case BackdropStyle.Fanart:
            {
                _scene.Children.Add(new Border
                {
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0.4, 1, RelativeUnit.Relative),
                        GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
                    },
                });
                var stripes = new Canvas { Opacity = 0.06 };
                for (int i = -20; i < 60; i++)
                    stripes.Children.Add(new Line { StartPoint = new Point(i * 60, 0), EndPoint = new Point(i * 60 + 800, 1400), Stroke = Brushes.White, StrokeThickness = 1 });
                _scene.Children.Add(stripes);
                var spot = Blob(c, 0.85, 0.15, 0.55, 0.45);
                _scene.Children.Add(spot.Shape);
                _animate = t => stripes.RenderTransform = new TranslateTransform(-(t * 6 % 60), 0);
                break;
            }
            case BackdropStyle.Waves:
            {
                _scene.Children.Add(new Border
                {
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops = { new GradientStop(c, 0), new GradientStop(a, 0.6), new GradientStop(b, 1) },
                    },
                });
                var waves = Enumerable.Range(0, 4).Select(i => new Path
                {
                    Stroke = new SolidColorBrush(Color.FromArgb((byte)(70 - i * 12), 160, 200, 255)),
                    StrokeThickness = 2 + i,
                    Opacity = 0.9,
                }).ToArray();
                foreach (var w in waves) _scene.Children.Add(w);
                _animate = t =>
                {
                    for (int i = 0; i < waves.Length; i++) waves[i].Data = Wave(t * (0.5 + i * 0.13) + i * 1.7, 640 + i * 70, 60 - i * 8);
                };
                break;
            }
            case BackdropStyle.Glow:
            {
                _scene.Children.Add(new Border { Background = new SolidColorBrush(c) });
                var glow = Blob(b, 0.15, 0.1, 0.7, 0.45);
                var glow2 = Blob(a, 0.9, 0.95, 0.6, 0.35);
                _scene.Children.Add(glow.Shape);
                _scene.Children.Add(glow2.Shape);
                _animate = t =>
                {
                    glow.Shape.Opacity = 0.75 + 0.25 * Math.Sin(t * 0.25);
                };
                break;
            }
            case BackdropStyle.Projector:
            {
                // The beam: a tall, narrow wash of warm light falling from above the screen.
                var beam = new RadialGradientBrush
                {
                    Center = new RelativePoint(0.62, -0.12, RelativeUnit.Relative),
                    GradientOrigin = new RelativePoint(0.62, -0.12, RelativeUnit.Relative),
                    RadiusX = new RelativeScalar(0.42, RelativeUnit.Relative),
                    RadiusY = new RelativeScalar(1.15, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(70, a.R, a.G, a.B), 0),
                        new GradientStop(Color.FromArgb(26, a.R, a.G, a.B), 0.45),
                        new GradientStop(Color.FromArgb(0, a.R, a.G, a.B), 1),
                    },
                };
                var beamShape = new Border { Background = beam, IsHitTestVisible = false };
                _scene.Children.Add(beamShape);
                // Warm light pooling low, where a screen's glow would land in the room.
                var pool = Blob(b, 0.3, 1.05, 0.65, 0.35);
                _scene.Children.Add(pool.Shape);
                // Film grain, re-dealt every frame by moving the tile.
                var grain = new ImageBrush(Grain()) { TileMode = TileMode.Tile, Stretch = Stretch.None, DestinationRect = new RelativeRect(0, 0, 256, 256, RelativeUnit.Absolute) };
                _scene.Children.Add(new Border { Background = grain, Opacity = 0.55, IsHitTestVisible = false });
                // Vignette: the edges of the frame fall away to black.
                _scene.Children.Add(new Border
                {
                    IsHitTestVisible = false,
                    Background = new RadialGradientBrush
                    {
                        Center = new RelativePoint(0.5, 0.45, RelativeUnit.Relative),
                        GradientOrigin = new RelativePoint(0.5, 0.45, RelativeUnit.Relative),
                        RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
                        RadiusY = new RelativeScalar(0.8, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55),
                            new GradientStop(Color.FromArgb(200, 0, 0, 0), 1),
                        },
                    },
                });
                var rng = new Random(7);
                _animate = t =>
                {
                    // A lamp's flicker: two slow beats and a little irregular shimmer.
                    beamShape.Opacity = 0.86 + 0.07 * Math.Sin(t * 0.9) + 0.04 * Math.Sin(t * 2.7 + 1.3) + 0.03 * rng.NextDouble();
                    grain.DestinationRect = new RelativeRect(-rng.Next(256), -rng.Next(256), 256, 256, RelativeUnit.Absolute);
                };
                break;
            }
            case BackdropStyle.Pixel:
            {
                // Real pixels: a small picture scaled up with no smoothing, so every pixel is a block.
                var pixels = new PixelScene(a, b, c);
                var image = new Image { Source = pixels.Bitmap, Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
                RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
                _scene.Children.Add(image);
                _scene.Children.Add(new Border { Background = PixelScene.Scanlines(), Opacity = 0.45, IsHitTestVisible = false });
                _animate = t => { pixels.Draw(t); image.InvalidateVisual(); };
                break;
            }
        }

        _scrim.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(40, bg.R, bg.G, bg.B), 0),
                new GradientStop(Color.FromArgb(110, bg.R, bg.G, bg.B), 0.55),
                new GradientStop(Color.FromArgb(235, bg.R, bg.G, bg.B), 1),
            },
        };
        _animate?.Invoke(_t);
        UpdateTimer();
    }

    private static Avalonia.Media.Imaging.WriteableBitmap? _grain;

    /// <summary>A 256 px tile of monochrome noise for film grain (made once).</summary>
    private static Avalonia.Media.Imaging.WriteableBitmap Grain()
    {
        if (_grain is not null) return _grain;
        var bmp = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(256, 256), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var fb = bmp.Lock())
        {
            var rng = new Random(11);
            var row = new byte[256 * 4];
            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    // Light and dark specks, premultiplied, faint on average.
                    var light = rng.Next(2) == 0;
                    var alpha = (byte)(rng.NextDouble() < 0.5 ? rng.Next(0, 22) : rng.Next(0, 8));
                    var v = light ? alpha : (byte)0;
                    row[x * 4] = v; row[x * 4 + 1] = v; row[x * 4 + 2] = v; row[x * 4 + 3] = alpha;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
            }
        }
        return _grain = bmp;
    }

    private static Geometry Wave(double phase, double baseY, double amp)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(new Point(-20, baseY), false);
        for (int x = -20; x <= 1960; x += 40)
        {
            var y = baseY + amp * Math.Sin(x / 300.0 + phase) + amp * 0.4 * Math.Sin(x / 130.0 - phase * 1.3);
            ctx.LineTo(new Point(x, y));
        }
        ctx.EndFigure(false);
        return g;
    }

    private static (Border Shape, RadialGradientBrush Brush, double Cx, double Cy) Blob(Color c, double cx, double cy, double radius, double alpha)
    {
        var brush = new RadialGradientBrush
        {
            Center = new RelativePoint(cx, cy, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(cx, cy, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(radius, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(radius, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb((byte)(alpha * 90), c.R, c.G, c.B), 0.45),
                new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
            },
        };
        return (new Border { Background = brush, IsHitTestVisible = false }, brush, cx, cy);
    }

    /// <summary>The user's wallpaper over the scene (empty path = the theme's own).</summary>
    public void SetWallpaper(string? path)
    {
        if (string.IsNullOrEmpty(path)) { _wallFade.To(0); return; }
        _wallpaper.Background = null;
        _ = ImageCache.LoadAsync(path, 1920).ContinueWith(t =>
        {
            if (t.Result is { } bmp) Dispatcher.UIThread.Post(() =>
            {
                _wallpaper.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                _wallFade.To(1);
            });
        });
    }

    /// <summary>Crossfades the focused item's art in (null fades back to the scene).</summary>
    public void SetArt(string? path, bool blur = true, double strength = 0.85)
    {
        if (path == _art) return;
        _art = path;
        var (back, backFade, front, frontFade) = _aFront ? (_artB, _fadeB, _artA, _fadeA) : (_artA, _fadeA, _artB, _fadeB);
        if (path is null) { frontFade.To(0); backFade.To(0); return; }
        _ = ImageCache.LoadAsync(path, 1280).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_art != path || t.Result is not { } bmp) return;
                back.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                back.Effect = blur && L.Settings is not null && L.Settings.Bool(Lounge.Core.Settings.K.Blur) && !L.Settings.Bool(Lounge.Core.Settings.K.ReduceEffects)
                    ? new BlurEffect { Radius = 36 } : null;
                ZIndexSwap(back, front);
                backFade.To(strength);
                frontFade.To(0);
                _aFront = !_aFront;
            });
        });
    }

    private void ZIndexSwap(Border toFront, Border toBack)
    {
        toFront.ZIndex = 2;
        toBack.ZIndex = 1;
        _scrim.ZIndex = 3;
    }
}
