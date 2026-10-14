using System.Buffers;

namespace Lounge.Media;

public enum FramePixels
{
    /// <summary>Y plane + interleaved UV plane at half resolution (GPU path).</summary>
    Nv12,
    /// <summary>Y, U, V planes; U and V at half resolution (GPU path).</summary>
    Yuv420p,
    /// <summary>Packed 32-bit BGRA (CPU path and thumbnails).</summary>
    Bgra,
}

/// <summary>A decoded picture in CPU memory, with pooled plane buffers. The presenter
/// owns it after <see cref="MediaPlayer.TakeFrame"/> and must call <see cref="Dispose"/>.</summary>
public sealed class VideoFrame : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public FramePixels Pixels { get; }
    public double Pts { get; internal set; }
    public int Serial { get; internal set; }
    public byte[][] Planes { get; }
    public int[] Strides { get; }
    /// <summary>Sample aspect ratio (1 for square pixels).</summary>
    public double Sar { get; internal set; } = 1;
    private bool _disposed;

    public VideoFrame(int width, int height, FramePixels pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        switch (pixels)
        {
            case FramePixels.Nv12:
                Strides = [width, width + (width & 1)];
                Planes = [Rent(width * height), Rent(Strides[1] * ((height + 1) / 2))];
                break;
            case FramePixels.Yuv420p:
                var cw = (width + 1) / 2;
                Strides = [width, cw, cw];
                Planes = [Rent(width * height), Rent(cw * ((height + 1) / 2)), Rent(cw * ((height + 1) / 2))];
                break;
            default:
                Strides = [width * 4];
                Planes = [Rent(width * 4 * height)];
                break;
        }
    }

    private static byte[] Rent(int size) => ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));

    public double DisplayAspect => Height == 0 ? 16.0 / 9 : Width * Sar / Height;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var p in Planes) ArrayPool<byte>.Shared.Return(p);
    }
}
