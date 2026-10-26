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
            case BackdropStyle.Drive:
            {
                // Broad soft-edged bands of colour laid diagonally over black, each fading in
                // and out across its width, drifting very slowly like lights on glass.
                var bands = new[] { (a, -260.0, 0.0), (b, 220.0, 1.7), (c, 640.0, 3.1) }.Select(x =>
                {
                    var (col, y, phase) = x;
                    var band = new Border
                    {
                        Width = 3600, Height = 520,
                        IsHitTestVisible = false,
                        Background = new LinearGradientBrush
                        {
                            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                            GradientStops =
                            {
                                new GradientStop(Color.FromArgb(0, col.R, col.G, col.B), 0),
                                new GradientStop(Color.FromArgb(150, col.R, col.G, col.B), 0.5),
                                new GradientStop(Color.FromArgb(0, col.R, col.G, col.B), 1),
                            },
                        },
                    };
                    var move = new TranslateTransform(-800, y);
                    band.RenderTransform = new TransformGroup { Children = { new RotateTransform(-14), move } };
                    band.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
                    return (band, move, y, phase);
                }).ToArray();
                var canvas = new Canvas { IsHitTestVisible = false };
                foreach (var (band, _, _, _) in bands) canvas.Children.Add(band);
                _scene.Children.Add(canvas);
                // The top and the bottom stay black, where the bar and the dots sit.
                _scene.Children.Add(new Border
                {
                    IsHitTestVisible = false,
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(150, 0, 0, 0), 0),
                            new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.3),
                            new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.7),
                            new GradientStop(Color.FromArgb(170, 0, 0, 0), 1),
                        },
                    },
                });
                _animate = t =>
                {
                    foreach (var (_, move, y, phase) in bands)
                    {
                        move.X = -800 + 160 * Math.Sin(t * 0.05 + phase);
                        move.Y = y + 40 * Math.Sin(t * 0.07 + phase * 1.3);
                    }
                };
                break;
            }
            case BackdropStyle.Mist:
            {
                // Pale grey with pastel light pooling and drifting slowly, brighter at the top
                // like a window in the morning.
                _scene.Children.Add(new Border
                {
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops = { new GradientStop(Color.FromRgb(246, 247, 250), 0), new GradientStop(bg, 0.6), new GradientStop(Color.Parse(d.Bg1), 1) },
                    },
                });
                var blobs = new[] { (a, 0.12, 0.2, 0.6), (b, 0.88, 0.3, 0.55), (c, 0.5, 1.0, 0.7) }
                    .Select(x => Blob(x.Item1, x.Item2, x.Item3, x.Item4, 0.7)).ToArray();
                foreach (var bl in blobs) _scene.Children.Add(bl.Shape);
                _animate = t =>
                {
                    for (int i = 0; i < blobs.Length; i++)
                    {
                        var (_, brush, cx, cy) = blobs[i];
                        brush.Center = new RelativePoint(cx + 0.08 * Math.Sin(t * 0.09 + i * 2.3), cy + 0.06 * Math.Cos(t * 0.08 + i * 1.7), RelativeUnit.Relative);
                        brush.GradientOrigin = brush.Center;
                    }
                };
                break;
            }
        }

        if (d.Backdrop == BackdropStyle.Liquid) Liquid(a, b, c);
        if (d.Backdrop == BackdropStyle.Snowfall) Snowfall(a, b, c);
        var snowy = d.Backdrop == BackdropStyle.Snowfall;

        _scrim.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                // The landscape keeps its mountains: a lighter veil than the abstract scenes need.
                new GradientStop(Color.FromArgb(snowy ? (byte)15 : (byte)40, bg.R, bg.G, bg.B), 0),
                new GradientStop(Color.FromArgb(snowy ? (byte)55 : (byte)110, bg.R, bg.G, bg.B), 0.55),
                new GradientStop(Color.FromArgb(snowy ? (byte)150 : (byte)235, bg.R, bg.G, bg.B), 1),
            },
        };
        _animate?.Invoke(_t);
        UpdateTimer();
    }

    /// <summary>Liquid: warm light pooling top right, cool light low left and a little teal
    /// between, drifting under the glass, while a faint broad sheen slides slowly across.</summary>
    private void Liquid(Color a, Color b, Color c)
    {
        var blobs = new[] { (a, 0.82, 0.12, 0.75, 0.6), (b, 0.12, 0.95, 0.7, 0.55), (c, 0.5, 0.7, 0.5, 0.35) }
            .Select(x => Blob(x.Item1, x.Item2, x.Item3, x.Item4, x.Item5)).ToArray();
        foreach (var bl in blobs) _scene.Children.Add(bl.Shape);
        var sheen = new Border
        {
            Width = 900, Height = 2400, IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(14, 255, 255, 255), 0.5),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                },
            },
        };
        var move = new TranslateTransform(-1000, -600);
        sheen.RenderTransform = new TransformGroup { Children = { new RotateTransform(24), move } };
        _scene.Children.Add(new Canvas { IsHitTestVisible = false, Children = { sheen } });
        _animate = t =>
        {
            for (int i = 0; i < blobs.Length; i++)
            {
                var (_, brush, cx, cy) = blobs[i];
                brush.Center = new RelativePoint(cx + 0.07 * Math.Sin(t * 0.07 + i * 2.2), cy + 0.05 * Math.Cos(t * 0.06 + i * 1.6), RelativeUnit.Relative);
                brush.GradientOrigin = brush.Center;
            }
            // Across and back every few minutes.
            move.X = -1000 + 1600 * (0.5 + 0.5 * Math.Sin(t * 0.02));
        };
    }

    /// <summary>Snowfall: a northern night. Stars that twinkle, two moons, the northern
    /// lights waving slowly, four ridges of mountains (paler with distance, snow on the high
    /// peaks, moonlight along the crests), a pine forest on the nearest ridge, fog lying in
    /// the valleys and snow falling, nearer flakes bigger and quicker. Drawn once from a fixed
    /// seed, so it is the same landscape every time; only the sky and the snow move.</summary>
    private void Snowfall(Color a, Color b, Color c)
    {
        const double W = 1920, H = 1080;
        var rng = new Random(23);
        var scene = new Canvas { Width = W, Height = H, IsHitTestVisible = false };
        _scene.Children.Add(new Viewbox { Stretch = Stretch.UniformToFill, Child = scene });
        void Add(Control x) => scene.Children.Add(x);
        Color Mix(Color x, Color y, double t) => Color.FromArgb(255, (byte)(x.R + (y.R - x.R) * t), (byte)(x.G + (y.G - x.G) * t), (byte)(x.B + (y.B - x.B) * t));

        // Sky: deep blue-black at the top, cold slate toward the horizon.
        Add(new Border
        {
            Width = W, Height = H,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(6, 9, 15), 0), new GradientStop(a, 0.55), new GradientStop(Mix(a, b, 0.25), 0.72), new GradientStop(c, 1) },
            },
        });

        // Stars, thinning toward the horizon.
        var stars = Enumerable.Range(0, 170).Select(_ =>
        {
            var size = rng.NextDouble() < 0.12 ? 2.6 : 1 + rng.NextDouble() * 1.2;
            var y = Math.Pow(rng.NextDouble(), 1.6) * H * 0.6;
            var star = new Avalonia.Controls.Shapes.Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(Color.FromArgb(255, 225, 232, 245)) };
            Canvas.SetLeft(star, rng.NextDouble() * W);
            Canvas.SetTop(star, y);
            var baseOpacity = 0.25 + 0.6 * rng.NextDouble() * (1 - y / (H * 0.6));
            star.Opacity = baseOpacity;
            Add(star);
            return (star, baseOpacity, Speed: 0.4 + rng.NextDouble() * 1.6, Phase: rng.NextDouble() * 6.28);
        }).ToArray();

        // Two moons: a great pale one with faint seas, and a small cold one beside it.
        Control Moon(double cx, double cy, double r, Color tint, bool seas)
        {
            var glow = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = r * 7, Height = r * 7,
                Fill = new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromArgb(60, tint.R, tint.G, tint.B), 0), new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 1) } },
            };
            Canvas.SetLeft(glow, cx - r * 3.5); Canvas.SetTop(glow, cy - r * 3.5);
            Add(glow);
            var disc = new Grid { Width = r * 2, Height = r * 2 };
            disc.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Fill = new RadialGradientBrush
                {
                    Center = new RelativePoint(0.4, 0.38, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.4, 0.38, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Mix(tint, Colors.White, 0.55), 0), new GradientStop(tint, 0.7), new GradientStop(Mix(tint, Colors.Black, 0.35), 1) },
                },
            });
            if (seas)
                foreach (var (x, y, rr, alpha) in new[] { (0.32, 0.35, 0.16, 50), (0.6, 0.55, 0.22, 40), (0.45, 0.72, 0.1, 45), (0.7, 0.3, 0.08, 35), (0.25, 0.62, 0.07, 30) })
                    disc.Children.Add(new Avalonia.Controls.Shapes.Ellipse
                    {
                        Width = r * 2 * rr * 2, Height = r * 2 * rr * 1.7, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                        Margin = new Thickness(r * 2 * (x - rr), r * 2 * (y - rr * 0.85), 0, 0),
                        Fill = new SolidColorBrush(Color.FromArgb((byte)alpha, 70, 66, 72)),
                    });
            Canvas.SetLeft(disc, cx - r); Canvas.SetTop(disc, cy - r);
            Add(disc);
            return disc;
        }
        // High in the open sky between the masthead and the widgets.
        Moon(870, 92, 58, Color.FromRgb(214, 205, 200), seas: true);
        Moon(1020, 54, 20, Color.FromRgb(190, 205, 225), seas: false);

        // The northern lights: curtains bright along their lower hem, rays fading upward.
        Path Curtain(Color tint, double alpha) => new()
        {
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 0),
                    new GradientStop(Color.FromArgb((byte)(alpha * 120), tint.R, tint.G, tint.B), 0.7),
                    new GradientStop(Color.FromArgb((byte)(alpha * 255), tint.R, tint.G, tint.B), 0.9),
                    new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 1),
                },
            },
        };
        var curtains = new[] { (Curtain(Color.FromRgb(90, 230, 170), 0.32), 300.0, 230.0, 0.0), (Curtain(Color.FromRgb(80, 190, 220), 0.22), 250.0, 180.0, 2.1) };
        foreach (var (path, _, _, _) in curtains) Add(path);
        Geometry Ribbon(double hem, double height, double phase, double t)
        {
            var g = new StreamGeometry();
            using var ctx = g.Open();
            var pts = Enumerable.Range(0, 49).Select(i =>
            {
                var x = -100 + i * (W + 200) / 48;
                var y = hem + 60 * Math.Sin(x / 380 + t * 0.12 + phase) + 25 * Math.Sin(x / 140 - t * 0.2 + phase * 1.7) - x * 0.06;
                return new Point(x, y);
            }).ToArray();
            ctx.BeginFigure(new Point(pts[0].X, pts[0].Y - height), true);
            foreach (var pt in pts) ctx.LineTo(new Point(pt.X, pt.Y - height - 40 * Math.Sin(pt.X / 90 + phase + t * 0.1)));
            foreach (var pt in pts.Reverse()) ctx.LineTo(pt);
            ctx.EndFigure(true);
            return g;
        }

        // Mountains, far to near: jagged ridges by midpoint displacement, the far ones paler
        // and bluer, snow on whatever stands high, moonlight along each crest.
        double[] Ridge(double baseY, double amp, double rough, int seed)
        {
            var r = new Random(seed);
            const int n = 257;
            var ys = new double[n];
            ys[0] = baseY + (r.NextDouble() - 0.5) * amp;
            ys[n - 1] = baseY + (r.NextDouble() - 0.5) * amp;
            for (int step = n - 1, scale = 1; step > 1; step /= 2, scale++)
                for (int i = step / 2; i < n; i += step)
                    ys[i] = (ys[i - step / 2] + ys[Math.Min(n - 1, i + step / 2)]) / 2 + (r.NextDouble() - 0.5) * amp * Math.Pow(rough, scale - 1);
            return ys;
        }
        Point[] Points(double[] ys) => ys.Select((y, i) => new Point(i * (W + 40) / (ys.Length - 1) - 20, y)).ToArray();
        void Mountain(double[] ys, Color body, Color snow, double snowLine, double rim)
        {
            var pts = Points(ys);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(-20, H + 10), true);
                foreach (var pt in pts) ctx.LineTo(pt);
                ctx.LineTo(new Point(W + 20, H + 10));
                ctx.EndFigure(true);
            }
            var top = ys.Min();
            var span = H - top;
            var snowAt = Math.Clamp((snowLine - top) / span, 0, 1);
            Add(new Path
            {
                Data = g, IsHitTestVisible = false,
                Fill = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(snow, 0), new GradientStop(Mix(snow, body, 0.6), snowAt * 0.7), new GradientStop(body, snowAt), new GradientStop(Mix(body, Colors.Black, 0.3), 1) },
                },
            });
            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(pts[0], false);
                foreach (var pt in pts.Skip(1)) ctx.LineTo(pt);
                ctx.EndFigure(false);
            }
            Add(new Path { Data = line, Stroke = new SolidColorBrush(Color.FromArgb((byte)(rim * 255), 200, 214, 232)), StrokeThickness = 1.2, IsHitTestVisible = false });
        }
        Path Fog(double y, double height, double alpha) => new()
        {
            IsHitTestVisible = false,
            Data = new RectangleGeometry(new Rect(-200, y, W + 400, height)),
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, b.R, b.G, b.B), 0),
                    new GradientStop(Color.FromArgb((byte)(alpha * 255), b.R, b.G, b.B), 0.5),
                    new GradientStop(Color.FromArgb(0, b.R, b.G, b.B), 1),
                },
            },
        };
        var far = Color.FromRgb(70, 84, 106);
        Mountain(Ridge(540, 380, 0.56, 3), far, Color.FromRgb(146, 158, 176), 500, 0.22);
        var fog1 = Fog(560, 160, 0.16);
        Add(fog1);
        Mountain(Ridge(650, 320, 0.55, 11), Color.FromRgb(44, 54, 70), Color.FromRgb(116, 128, 146), 590, 0.2);
        var fog2 = Fog(660, 150, 0.14);
        Add(fog2);
        Mountain(Ridge(770, 240, 0.52, 17), Color.FromRgb(26, 33, 44), Color.FromRgb(118, 130, 150), 720, 0.16);
        var near = Ridge(890, 160, 0.5, 29);
        Mountain(near, Color.FromRgb(12, 15, 21), Color.FromRgb(52, 60, 74), 850, 0.12);

        // A pine forest along the nearest ridge, as one shape: tiers of narrowing triangles.
        var nearPts = Points(near);
        var forest = new System.Text.StringBuilder();
        for (double x = -10; x < W + 10; x += 9 + rng.NextDouble() * 16)
        {
            var i = Math.Clamp((int)((x + 20) / (W + 40) * (near.Length - 1)), 0, near.Length - 1);
            var ground = nearPts[i].Y + 6;
            var h = 26 + rng.NextDouble() * 42;
            var w = h * 0.38;
            for (int tier = 0; tier < 3; tier++)
            {
                var ty = ground - h * tier / 3.2;
                var tw = w * (1 - tier * 0.25);
                forest.Append(FormattableString.Invariant($"M{x - tw:0.#} {ty:0.#} L{x:0.#} {ty - h * 0.5:0.#} L{x + tw:0.#} {ty:0.#} Z "));
            }
            forest.Append(FormattableString.Invariant($"M{x - 1.5:0.#} {ground:0.#} h3 v12 h-3 Z "));
        }
        // Nonzero fill ("F1"): overlapping tiers merge into one silhouette. Even-odd, the default,
        // punched see-through holes wherever two triangles crossed.
        Add(new Path { Data = Geometry.Parse("F1 " + forest), Fill = new SolidColorBrush(Color.FromRgb(6, 8, 11)), IsHitTestVisible = false });
        var fog3 = Fog(930, 180, 0.12);
        Add(fog3);

        // Snow, in front of everything.
        var flakes = Enumerable.Range(0, 110).Select(_ =>
        {
            var depth = rng.NextDouble();
            var size = 1.5 + depth * 3.5;
            var flake = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = size, Height = size, IsHitTestVisible = false,
                Fill = new SolidColorBrush(Color.FromArgb((byte)(70 + depth * 150), 235, 240, 245)),
            };
            return (flake, X: rng.NextDouble() * W, Y: rng.NextDouble() * (H + 20), Speed: 20 + depth * 55, Sway: 10 + depth * 25, Phase: rng.NextDouble() * 6.28);
        }).ToArray();
        foreach (var f in flakes) Add(f.flake);

        _animate = t =>
        {
            foreach (var (path, hem, height, phase) in curtains) path.Data = Ribbon(hem, height, phase, t);
            foreach (var (star, o, speed, phase) in stars) star.Opacity = o * (0.75 + 0.25 * Math.Sin(t * speed + phase));
            fog1.RenderTransform = new TranslateTransform(80 * Math.Sin(t * 0.02), 0);
            fog2.RenderTransform = new TranslateTransform(-100 * Math.Sin(t * 0.017 + 1), 0);
            fog3.RenderTransform = new TranslateTransform(120 * Math.Sin(t * 0.013 + 2), 0);
            foreach (var f in flakes)
            {
                Canvas.SetLeft(f.flake, f.X + f.Sway * Math.Sin(t * 0.6 + f.Phase));
                Canvas.SetTop(f.flake, (f.Y + t * f.Speed) % (H + 20) - 10);
            }
        };
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
