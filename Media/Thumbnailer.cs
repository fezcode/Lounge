using FFmpeg.AutoGen;

namespace Lounge.Media;

public sealed record Thumbnail(byte[] Bgra, int Width, int Height, double Pts);

/// <summary>Grabs frames as small BGRA images: posters for videos without one, and the
/// preview strip while scrubbing. Keeps its own demuxer so it never disturbs playback.</summary>
public sealed unsafe class Thumbnailer : IDisposable
{
    private AVFormatContext* _fmt;
    private AVCodecContext* _ctx;
    private int _stream = -1;
    private AVRational _tb;
    private FrameConverter? _conv;
    private readonly int _width;
    private readonly object _gate = new();

    public double Duration { get; private set; }

    private Thumbnailer(int width) => _width = width;

    public static Thumbnailer? Open(string path, int width = 320)
    {
        if (!Ff.Ensure()) return null;
        var t = new Thumbnailer(width);
        AVFormatContext* fmt = null;
        if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return null;
        t._fmt = fmt;
        if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) { t.Dispose(); return null; }
        AVCodec* codec = null;
        var si = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
        if (si < 0 || codec == null || (fmt->streams[si]->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0) { t.Dispose(); return null; }
        t._stream = si;
        t._tb = fmt->streams[si]->time_base;
        t.Duration = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
        for (int i = 0; i < fmt->nb_streams; i++) if (i != si) fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
        t._ctx = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(t._ctx, fmt->streams[si]->codecpar);
        t._ctx->thread_count = 2;
        // Thumbnails only need keyframes; skipping the rest makes a seek nearly free.
        t._ctx->skip_frame = AVDiscard.AVDISCARD_NONKEY;
        if (ffmpeg.avcodec_open2(t._ctx, codec, null) < 0) { t.Dispose(); return null; }
        t._conv = new FrameConverter(gpuPath: false, maxWidth: width);
        return t;
    }

    /// <summary>The keyframe at or before <paramref name="seconds"/>.</summary>
    public Thumbnail? At(double seconds)
    {
        lock (_gate)
        {
            if (_fmt == null) return null;
            var ts = (long)(Math.Max(0, seconds) * ffmpeg.AV_TIME_BASE);
            ffmpeg.av_seek_frame(_fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
            ffmpeg.avcodec_flush_buffers(_ctx);
            var pkt = ffmpeg.av_packet_alloc();
            var frame = ffmpeg.av_frame_alloc();
            try
            {
                for (int reads = 0; reads < 600; reads++)
                {
                    if (ffmpeg.av_read_frame(_fmt, pkt) < 0)
                    {
                        ffmpeg.avcodec_send_packet(_ctx, null);
                        return Receive(frame);
                    }
                    if (pkt->stream_index != _stream) { ffmpeg.av_packet_unref(pkt); continue; }
                    ffmpeg.avcodec_send_packet(_ctx, pkt);
                    ffmpeg.av_packet_unref(pkt);
                    if (Receive(frame) is { } thumb) return thumb;
                }
                return null;
            }
            finally
            {
                ffmpeg.av_packet_free(&pkt);
                ffmpeg.av_frame_free(&frame);
            }
        }
    }

    private Thumbnail? Receive(AVFrame* frame)
    {
        if (ffmpeg.avcodec_receive_frame(_ctx, frame) < 0) return null;
        var ts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
        using var vf = _conv!.Convert(frame);
        ffmpeg.av_frame_unref(frame);
        if (vf is null) return null;
        var bytes = new byte[vf.Width * vf.Height * 4];
        Buffer.BlockCopy(vf.Planes[0], 0, bytes, 0, bytes.Length);
        return new Thumbnail(bytes, vf.Width, vf.Height, ts == ffmpeg.AV_NOPTS_VALUE ? 0 : Ff.ToSeconds(ts, _tb));
    }

    /// <summary>A representative poster frame: a tenth of the way in, past cold opens and logos.</summary>
    public Thumbnail? Poster() => At(Duration > 0 ? Math.Min(Duration * 0.1, 300) : 10) ?? At(0);

    public void Dispose()
    {
        lock (_gate)
        {
            _conv?.Dispose(); _conv = null;
            if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
            if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
        }
    }
}
