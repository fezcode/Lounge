using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Lounge.Core.Library;
using Lounge.Core.Settings;
using Lounge.Media;
using Lounge.Shell;

namespace Lounge.Controls;

/// <summary>Loads pictures off the UI thread, decoded no larger than they are shown,
/// and keeps the recent ones. Also makes frame thumbnails for videos without art.</summary>
public static class ImageCache
{
    private const int Capacity = 400;
    private static readonly Dictionary<string, Bitmap> Cache = new();
    private static readonly LinkedList<string> Order = new();
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim Thumbs = new(2);

    public static Bitmap? TryGet(string key)
    {
        lock (Gate) return Cache.TryGetValue(key, out var b) ? b : null;
    }

    private static void Put(string key, Bitmap b)
    {
        lock (Gate)
        {
            if (Cache.ContainsKey(key)) return;
            Cache[key] = b;
            Order.AddFirst(key);
            while (Order.Count > Capacity)
            {
                var last = Order.Last!.Value;
                Order.RemoveLast();
                Cache.Remove(last);
            }
        }
    }

    private static readonly Dictionary<string, TaskCompletionSource<Bitmap?>> InFlight = new();
    private static readonly Stack<(string Path, int Width, string Key)> Pending = new();
    private static int _workers;
    private const int MaxWorkers = 2;

    /// <summary>True while pictures are still queued or decoding.</summary>
    public static bool Busy { get { lock (Gate) return InFlight.Count > 0; } }

    /// <summary>Loads (or joins the load already queued for) a picture. Two background
    /// decoders work newest-first, so the screen you are looking at fills in before
    /// anything you scrolled past.</summary>
    public static Task<Bitmap?> LoadAsync(string? path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path)) return Task.FromResult<Bitmap?>(null);
        var key = $"{path}|{decodeWidth}";
        if (TryGet(key) is { } hit) return Task.FromResult<Bitmap?>(hit);
        lock (Gate)
        {
            if (InFlight.TryGetValue(key, out var running)) return running.Task;
            var tcs = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
            InFlight[key] = tcs;
            Pending.Push((path, decodeWidth, key));
            if (_workers < MaxWorkers)
            {
                _workers++;
                new Thread(Work) { IsBackground = true, Name = "image-decode", Priority = ThreadPriority.BelowNormal }.Start();
            }
            return tcs.Task;
        }
    }

    private static void Work()
    {
        while (true)
        {
            (string Path, int Width, string Key) job;
            TaskCompletionSource<Bitmap?>? tcs;
            lock (Gate)
            {
                if (Pending.Count == 0) { _workers--; return; }
                job = Pending.Pop();
                InFlight.TryGetValue(job.Key, out tcs);
            }
            var bmp = Decode(job.Path, job.Width);
            if (bmp is not null) Put(job.Key, bmp);
            lock (Gate) InFlight.Remove(job.Key);
            tcs?.TrySetResult(bmp);
        }
    }

    private static Bitmap? Decode(string path, int decodeWidth)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var s = File.OpenRead(path);
            return Bitmap.DecodeToWidth(s, decodeWidth, BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
    public static WriteableBitmap FromBgra(byte[] bgra, int w, int h)
    {
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = wb.Lock();
        unsafe
        {
            fixed (byte* src = bgra)
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy(src + y * w * 4, (byte*)fb.Address + y * fb.RowBytes, fb.RowBytes, w * 4);
        }
        return wb;
    }

    /// <summary>A border whose background becomes the picture once loaded: rounded
    /// corners antialias properly on a fill, not on a clipped Image.</summary>
    public static Border Art(string? path, double width, double height, Control? placeholder = null, Stretch stretch = Stretch.UniformToFill)
    {
        var border = new Border { Width = width, Height = height, Child = placeholder, Background = T.Surface };
        if (string.IsNullOrEmpty(path)) return border;
        var decode = (int)Math.Clamp(width * 1.5, 64, 1920);
        if (TryGet($"{path}|{decode}") is { } ready)
        {
            border.Background = new ImageBrush(ready) { Stretch = stretch };
            border.Child = null;
            return border;
        }
        _ = LoadAsync(path, decode).ContinueWith(t =>
        {
            if (t.Result is { } bmp)
                Dispatcher.UIThread.Post(() =>
                {
                    border.Background = new ImageBrush(bmp) { Stretch = stretch };
                    border.Child = null;
                });
        });
        return border;
    }

    /// <summary>Poster art for a library video: its own art, else a frame grabbed from the
    /// file and cached in thumbs\ (when the setting allows).</summary>
    public static Border VideoArt(LibraryItem item, double width, double height, Control? placeholder = null)
    {
        if (item.Art is not null || L.Preview || !L.Settings.Bool(K.FrameThumbnails))
            return Art(item.Art, width, height, placeholder);
        var border = Art(null, width, height, placeholder);
        _ = Task.Run(async () =>
        {
            var path = await FrameThumbnail(item.Path);
            if (path is null) return;
            L.Library.SetArt(item.Path, path);
            var bmp = await LoadAsync(path, (int)Math.Clamp(width * 1.5, 64, 1920));
            if (bmp is not null) Dispatcher.UIThread.Post(() => { border.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill }; border.Child = null; });
        });
        return border;
    }

    public static async Task<string?> FrameThumbnail(string video)
    {
        var file = Path.Combine(Lounge.Core.AppPaths.Thumbs, "frame-" + LibraryScanner.Hash(video) + ".png");
        if (File.Exists(file)) return file;
        await Thumbs.WaitAsync();
        try
        {
            if (File.Exists(file)) return file;
            using var t = Thumbnailer.Open(video, 640);
            var th = t?.Poster();
            if (th is null) return null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var wb = FromBgra(th.Bgra, th.Width, th.Height);
                wb.Save(file);
            });
            return file;
        }
        catch (Exception) { return null; }
        finally { Thumbs.Release(); }
    }
}
