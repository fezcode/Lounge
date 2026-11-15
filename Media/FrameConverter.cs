using System.Runtime.Intrinsics;
using FFmpeg.AutoGen;

namespace Lounge.Media;

/// <summary>Turns decoded AVFrames into <see cref="VideoFrame"/>s. Hardware frames are
/// downloaded first (D3D11VA gives NV12 or P010). On the GPU path NV12 and yuv420p go
/// through untouched for the shader, P010 (10-bit, as most 4K is) is narrowed to NV12 by
/// keeping each sample's top byte, and anything else is converted to NV12. On the CPU path
/// everything becomes BGRA with sws_scale. Big planes are copied on several threads: a 4K
/// frame is 12 MB, and one thread copying it each frame left little time to spare.</summary>
public sealed unsafe class FrameConverter : IDisposable
{
    private SwsContext* _sws;
    private AVFrame* _sw = ffmpeg.av_frame_alloc();
    private readonly bool _gpu;
    private readonly int _maxWidth;

    public FrameConverter(bool gpuPath, int maxWidth = 0)
    {
        _gpu = gpuPath;
        _maxWidth = maxWidth;
    }

    /// <summary>The pixel format frames arrive in after any hardware download, for diagnostics.</summary>
    public string? SourceFormat { get; private set; }
    /// <summary>Diagnostics: total Stopwatch ticks spent downloading hardware frames.</summary>
    public long TransferTicks { get; private set; }

    public VideoFrame? Convert(AVFrame* frame)
    {
        var src = frame;
        if (frame->hw_frames_ctx != null)
        {
            ffmpeg.av_frame_unref(_sw);
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (ffmpeg.av_hwframe_transfer_data(_sw, frame, 0) < 0) return null;
            TransferTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            ffmpeg.av_frame_copy_props(_sw, frame);
            src = _sw;
        }
        var fmt = (AVPixelFormat)src->format;
        SourceFormat ??= (frame->hw_frames_ctx != null ? "hw:" : "") + ffmpeg.av_get_pix_fmt_name(fmt);
        int w = src->width, h = src->height;
        if (w <= 0 || h <= 0) return null;
        var sar = src->sample_aspect_ratio.num > 0 && src->sample_aspect_ratio.den > 0
            ? src->sample_aspect_ratio.num / (double)src->sample_aspect_ratio.den : 1.0;

        int ow = w, oh = h;
        if (_maxWidth > 0 && w > _maxWidth) { ow = _maxWidth; oh = Math.Max(2, (int)Math.Round(h * (_maxWidth / (double)w)) & ~1); }

        VideoFrame result;
        if (_gpu && ow == w && fmt == AVPixelFormat.AV_PIX_FMT_NV12)
        {
            result = new VideoFrame(w, h, FramePixels.Nv12);
            CopyPlane(src->data[0], src->linesize[0], result.Planes[0], result.Strides[0], w, h);
            CopyPlane(src->data[1], src->linesize[1], result.Planes[1], result.Strides[1], result.Strides[1], (h + 1) / 2);
        }
        else if (_gpu && ow == w && fmt == AVPixelFormat.AV_PIX_FMT_P010LE)
        {
            // P010 keeps 10 bits in the top of each 16-bit sample, so its high byte is the
            // 8-bit value. Doing this here took a 4K frame from ~14 ms through sws_scale to
            // a couple of milliseconds.
            result = new VideoFrame(w, h, FramePixels.Nv12);
            Narrow16(src->data[0], src->linesize[0], result.Planes[0], result.Strides[0], w, h);
            Narrow16(src->data[1], src->linesize[1], result.Planes[1], result.Strides[1], result.Strides[1], (h + 1) / 2);
        }
        else if (_gpu && ow == w && fmt is AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P)
        {
            result = new VideoFrame(w, h, FramePixels.Yuv420p);
            CopyPlane(src->data[0], src->linesize[0], result.Planes[0], result.Strides[0], w, h);
            CopyPlane(src->data[1], src->linesize[1], result.Planes[1], result.Strides[1], (w + 1) / 2, (h + 1) / 2);
            CopyPlane(src->data[2], src->linesize[2], result.Planes[2], result.Strides[2], (w + 1) / 2, (h + 1) / 2);
        }
        else
        {
            var target = _gpu ? FramePixels.Nv12 : FramePixels.Bgra;
            result = new VideoFrame(ow, oh, target);
            var dstFmt = _gpu ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_BGRA;
            _sws = ffmpeg.sws_getCachedContext(_sws, w, h, fmt, ow, oh, dstFmt, 2 /* SWS_BILINEAR */, null, null, null);
            if (_sws == null) { result.Dispose(); return null; }
            fixed (byte* p0 = result.Planes[0])
            fixed (byte* p1 = result.Planes.Length > 1 ? result.Planes[1] : result.Planes[0])
            {
                var dst = new byte*[] { p0, result.Planes.Length > 1 ? p1 : null, null, null };
                var dstStride = new int[] { result.Strides[0], result.Strides.Length > 1 ? result.Strides[1] : 0, 0, 0 };
                var srcData = new byte*[] { src->data[0], src->data[1], src->data[2], src->data[3] };
                var srcStride = new int[] { src->linesize[0], src->linesize[1], src->linesize[2], src->linesize[3] };
                ffmpeg.sws_scale(_sws, srcData, srcStride, 0, h, dst, dstStride);
            }
        }
        result.Sar = sar;
        return result;
    }

    /// <summary>Planes with at least this many rows are split across threads.</summary>
    private const int ParallelRows = 720;

    /// <summary>Runs <paramref name="rows"/>(from, to) over bands of rows, in parallel for big planes.</summary>
    private static void Bands(int rows, Action<int, int> body)
    {
        if (rows < ParallelRows) { body(0, rows); return; }
        var bands = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        var per = (rows + bands - 1) / bands;
        Parallel.For(0, bands, b => body(b * per, Math.Min(rows, (b + 1) * per)));
    }

    private static void CopyPlane(byte* src, int srcStride, byte[] dst, int dstStride, int rowBytes, int rows)
    {
        var s0 = (nint)src;
        Bands(rows, (from, to) =>
        {
            fixed (byte* d = dst)
                for (int y = from; y < to; y++)
                    Buffer.MemoryCopy((byte*)s0 + (long)y * srcStride, d + (long)y * dstStride, dstStride, rowBytes);
        });
    }

    /// <summary>Copies a plane of 16-bit little-endian samples (P010) into 8-bit samples by
    /// keeping each one's high byte. <paramref name="samples"/> is samples per row.</summary>
    internal static void Narrow16(byte* src, int srcStride, byte[] dst, int dstStride, int samples, int rows)
    {
        var s0 = (nint)src;
        Bands(rows, (from, to) =>
        {
            fixed (byte* d0 = dst)
                for (int y = from; y < to; y++)
                {
                    var s = (ushort*)((byte*)s0 + (long)y * srcStride);
                    var d = d0 + (long)y * dstStride;
                    int x = 0;
                    if (Vector256.IsHardwareAccelerated)
                        for (; x + 32 <= samples; x += 32)
                        {
                            var a = Vector256.ShiftRightLogical(Vector256.Load(s + x), 8);
                            var b = Vector256.ShiftRightLogical(Vector256.Load(s + x + 16), 8);
                            Vector256.Narrow(a, b).Store(d + x);
                        }
                    else if (Vector128.IsHardwareAccelerated)
                        for (; x + 16 <= samples; x += 16)
                        {
                            var a = Vector128.ShiftRightLogical(Vector128.Load(s + x), 8);
                            var b = Vector128.ShiftRightLogical(Vector128.Load(s + x + 8), 8);
                            Vector128.Narrow(a, b).Store(d + x);
                        }
                    for (; x < samples; x++) d[x] = (byte)(s[x] >> 8);
                }
        });
    }

    public void Dispose()
    {
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_sw != null) { var f = _sw; ffmpeg.av_frame_free(&f); _sw = null; }
    }
}
