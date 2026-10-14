using FFmpeg.AutoGen;

namespace Lounge.Media;

/// <summary>Turns decoded AVFrames into <see cref="VideoFrame"/>s. Hardware frames are
/// downloaded first (D3D11VA gives NV12 or P010). On the GPU path NV12 and yuv420p go
/// through untouched for the shader; anything else is converted to NV12. On the CPU
/// path everything becomes BGRA with sws_scale.</summary>
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

    public VideoFrame? Convert(AVFrame* frame)
    {
        var src = frame;
        if (frame->hw_frames_ctx != null)
        {
            ffmpeg.av_frame_unref(_sw);
            if (ffmpeg.av_hwframe_transfer_data(_sw, frame, 0) < 0) return null;
            ffmpeg.av_frame_copy_props(_sw, frame);
            src = _sw;
        }
        var fmt = (AVPixelFormat)src->format;
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

    private static void CopyPlane(byte* src, int srcStride, byte[] dst, int dstStride, int rowBytes, int rows)
    {
        fixed (byte* d = dst)
            for (int y = 0; y < rows; y++)
                Buffer.MemoryCopy(src + (long)y * srcStride, d + (long)y * dstStride, dstStride, rowBytes);
    }

    public void Dispose()
    {
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_sw != null) { var f = _sw; ffmpeg.av_frame_free(&f); _sw = null; }
    }
}
