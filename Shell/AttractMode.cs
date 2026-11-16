using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core;
using Lounge.Core.Attract;

namespace Lounge.Shell;

/// <summary>The waiting screen (Settings, General, Waiting screen): after a while
/// untouched on a menu, Lounge becomes a loading screen. Illustrated scenes, each a
/// background and a cut-out foreground that move at different speeds (see
/// <see cref="AttractChoreo"/>), cut on the bar lines of the attract theme. Any button
/// ends it, like the dim.</summary>
public sealed class AttractMode : Grid
{
    public const int Pairs = 10;
    /// <summary>Scenes whose foreground stands on the left of the frame.</summary>
    private static readonly HashSet<int> LeftFigures = [2, 4, 6, 7, 8, 10];

    private readonly Image _bg = Layer(), _fg = Layer(new RelativePoint(0.5, 1, RelativeUnit.Relative));
    private readonly ScaleTransform _bgScale = new(), _fgScale = new();
    private readonly TranslateTransform _bgMove = new(), _fgMove = new();
    private readonly Rectangle _black = new() { Fill = Brushes.Black, IsHitTestVisible = false };
    /// <summary>The mark's face: soft white with a band of light that sweeps across and back.</summary>
    private readonly LinearGradientBrush _shine = new()
    {
        GradientStops =
        {
            new GradientStop(Color.FromArgb(150, 205, 205, 210), 0.36),
            new GradientStop(Color.FromArgb(230, 255, 255, 255), 0.45),
            new GradientStop(Color.FromArgb(255, 255, 248, 222), 0.50),
            new GradientStop(Color.FromArgb(230, 255, 255, 255), 0.55),
            new GradientStop(Color.FromArgb(150, 205, 205, 210), 0.64),
        },
    };
    private readonly DispatcherTimer _tick;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly Dictionary<int, Task<(Bitmap? Bg, Bitmap? Fg)>> _loads = new();
    private int[] _order = Enumerable.Range(1, Pairs).ToArray();
    private int _shown = -1;

    public bool Active { get; private set; }

    public AttractMode()
    {
        Background = Brushes.Black;
        IsHitTestVisible = false;
        ClipToBounds = true;
        _bg.RenderTransform = new TransformGroup { Children = { _bgScale, _bgMove } };
        _fg.RenderTransform = new TransformGroup { Children = { _fgScale, _fgMove } };
        Children.Add(_bg);
        Children.Add(_fg);
        Children.Add(_black);

        var mark = Ui.Text(Product.Name.ToUpperInvariant(), 30, _shine, FontWeight.Black);
        mark.LetterSpacing = 6;
        mark.HorizontalAlignment = HorizontalAlignment.Right;
        mark.VerticalAlignment = VerticalAlignment.Bottom;
        mark.Margin = new Thickness(0, 0, 64, 48);
        mark.Effect = new DropShadowEffect { BlurRadius = 12, OffsetX = 0, OffsetY = 2, Color = Color.FromArgb(200, 0, 0, 0) };
        Children.Add(mark);

        IsVisible = false;
        // Created stopped: it runs only while the loop shows.
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _tick.Tick += (_, _) => Frame(_clock.Elapsed.TotalSeconds);
    }

    /// <param name="origin">Where it scales from: the foreground from the bottom, so it keeps standing on the edge.</param>
    private static Image Layer(RelativePoint? origin = null) => new()
    {
        Stretch = Stretch.UniformToFill,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        RenderTransformOrigin = origin ?? RelativePoint.Center,
        IsHitTestVisible = false,
    };

    public void Set(bool on)
    {
        if (on == Active) return;
        Active = on;
        if (on)
        {
            _order = Enumerable.Range(1, Pairs).OrderBy(_ => Random.Shared.Next()).ToArray();
            _shown = -1;
            Preload(0);
            _clock.Restart();
            Frame(0);
            IsVisible = true;
            _tick.Start();
        }
        else
        {
            _tick.Stop();
            _clock.Reset();
            IsVisible = false;
            _bg.Source = _fg.Source = null;
            foreach (var t in _loads.Values) if (t.IsCompletedSuccessfully) { t.Result.Bg?.Dispose(); t.Result.Fg?.Dispose(); }
            _loads.Clear();
        }
    }

    /// <summary>Previews: show the loop at this moment, loading synchronously.</summary>
    public void ShowAt(double seconds, int firstPair = 1)
    {
        Active = true;
        _order = Enumerable.Range(0, Pairs).Select(i => (firstPair - 1 + i) % Pairs + 1).ToArray();
        var (scene, _) = AttractChoreo.At(seconds);
        Preload(scene).Wait();
        IsVisible = true;
        Frame(seconds);
    }

    private int PairOf(int scene) => _order[scene % _order.Length];

    private Task<(Bitmap?, Bitmap?)> Preload(int scene)
    {
        var pair = PairOf(scene);
        if (_loads.TryGetValue(pair, out var t)) return t;
        return _loads[pair] = Task.Run(() => (Load(pair, "bg"), Load(pair, "fg")));
    }

    private static Bitmap? Load(int pair, string layer)
    {
        // By assembly name: previews compile the app into Lounge.Preview.
        var asm = typeof(AttractMode).Assembly.GetName().Name;
        try
        {
            var bmp = new Bitmap(AssetLoader.Open(new Uri($"avares://{asm}/Assets/Attract/{pair:00}-{layer}.webp")));
            if (layer != "bg") return bmp;
            using (bmp) return Monochrome(bmp);
        }
        catch (Exception e) { Log.Write($"attract: {pair:00}-{layer} did not load: {e.Message}"); return null; }
    }

    /// <summary>The loading-screen look: the background in dark black and white, so the
    /// figure in front keeps all the colour. (Only here: Heatwave shows the same scenes in colour.)</summary>
    private static unsafe Bitmap Monochrome(Bitmap src)
    {
        var size = src.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        fixed (byte* p = pixels)
            src.CopyPixels(new PixelRect(size), (IntPtr)p, pixels.Length, stride);
        var fmt = src.Format ?? PixelFormat.Bgra8888;
        // Red and blue swap places between the two byte orders; the weights follow.
        var (wr, wb) = fmt == PixelFormat.Rgba8888 ? (0.299, 0.114) : (0.114, 0.299);
        var curve = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            // Darker, with a little contrast kept in the highlights.
            var v = i / 255.0;
            curve[i] = (byte)Math.Clamp(Math.Pow(v, 1.25) * 0.62 * 255, 0, 255);
        }
        for (int i = 0; i < pixels.Length; i += 4)
        {
            var y = curve[(int)(pixels[i] * wr + pixels[i + 1] * 0.587 + pixels[i + 2] * wb)];
            pixels[i] = pixels[i + 1] = pixels[i + 2] = y;
        }
        var outBmp = new WriteableBitmap(size, src.Dpi, fmt, AlphaFormat.Premul);
        using (var fb = outBmp.Lock())
            for (int row = 0; row < size.Height; row++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, row * stride, fb.Address + row * fb.RowBytes, stride);
        return outBmp;
    }

    /// <summary>Moves the band of light: from just off the left of the mark to just off the
    /// right and back, easing at each end, every five seconds.</summary>
    private void Shine(double seconds)
    {
        const double period = 5.0;
        var p = -0.35 + 1.7 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * seconds / period));
        // The band sits at the brush's middle; shifting the brush moves it, and outside it
        // the ends keep the soft white. A slight slant, like light catching chrome.
        _shine.StartPoint = new RelativePoint(p - 0.5, -0.25, RelativeUnit.Relative);
        _shine.EndPoint = new RelativePoint(p + 0.5, 1.25, RelativeUnit.Relative);
    }

    private void Frame(double seconds)
    {
        var (scene, t) = AttractChoreo.At(seconds);
        if (scene != _shown)
        {
            var load = Preload(scene);
            // Hold the black until the pictures are decoded; it is only ever a moment.
            if (!load.IsCompleted) { _black.Opacity = 1; return; }
            var pair = PairOf(scene);
            var (bg, fg) = load.Result;
            _bg.Source = bg;
            _fg.Source = fg;
            // Drop the scene before last, start decoding the next.
            if (_shown >= 0)
            {
                var old = PairOf(_shown);
                if (old != pair && _loads.Remove(old, out var gone) && gone.IsCompletedSuccessfully)
                    Dispatcher.UIThread.Post(() => { gone.Result.Bg?.Dispose(); gone.Result.Fg?.Dispose(); }, DispatcherPriority.Background);
            }
            _shown = scene;
            Preload(scene + 1);
        }

        var w = Bounds.Width > 0 ? Bounds.Width : 1920;
        var h = Bounds.Height > 0 ? Bounds.Height : 1080;
        var b = AttractChoreo.Background(t, scene);
        var f = AttractChoreo.Foreground(t, LeftFigures.Contains(PairOf(scene)));
        _bgScale.ScaleX = _bgScale.ScaleY = b.Scale;
        _bgMove.X = b.X * w; _bgMove.Y = b.Y * h;
        _fgScale.ScaleX = _fgScale.ScaleY = f.Scale;
        _fgMove.X = f.X * w; _fgMove.Y = f.Y * h;
        _black.Opacity = AttractChoreo.Black(t);
        Shine(seconds);
    }
}
