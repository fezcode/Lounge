using FFmpeg.AutoGen;

namespace Lounge.Media;

/// <summary>The exact frame at a moment of a video, at its full resolution, as BGRA: what
/// "Take a screenshot" saves. Opens its own decoder (software, every frame), so playback
/// carries on undisturbed: seek to the keyframe before, then decode forward to the frame.</summary>
public static unsafe class FrameGrab
{
    public static Thumbnail? At(string path, double seconds)
    {
        if (!Ff.Ensure()) return null;
        AVFormatContext* fmt = null;
        if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return null;
        AVCodecContext* ctx = null;
        var pkt = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        FrameConverter? conv = null;
        try
        {
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) return null;
            AVCodec* codec = null;
            var si = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            if (si < 0 || codec == null) return null;
            for (int i = 0; i < fmt->nb_streams; i++) if (i != si) fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            var tb = fmt->streams[si]->time_base;
            ctx = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(ctx, fmt->streams[si]->codecpar);
            ctx->thread_count = Math.Clamp(Environment.ProcessorCount, 1, 16);
            if (ffmpeg.avcodec_open2(ctx, codec, null) < 0) return null;
            conv = new FrameConverter(gpuPath: false);

            ffmpeg.av_seek_frame(fmt, -1, (long)(Math.Max(0, seconds) * ffmpeg.AV_TIME_BASE), ffmpeg.AVSEEK_FLAG_BACKWARD);
            Thumbnail? last = null;
            var eof = false;
            for (int reads = 0; reads < 20_000; reads++)
            {
                if (!eof)
                {
                    if (ffmpeg.av_read_frame(fmt, pkt) < 0) { eof = true; ffmpeg.avcodec_send_packet(ctx, null); }
                    else if (pkt->stream_index != si) { ffmpeg.av_packet_unref(pkt); continue; }
                    else { ffmpeg.avcodec_send_packet(ctx, pkt); ffmpeg.av_packet_unref(pkt); }
                }
                while (ffmpeg.avcodec_receive_frame(ctx, frame) >= 0)
                {
                    var ts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
                    var pts = ts == ffmpeg.AV_NOPTS_VALUE ? 0 : Ff.ToSeconds(ts, tb);
                    var dur = frame->duration > 0 ? Ff.ToSeconds(frame->duration, tb) : 1 / 30.0;
                    var due = pts + dur * 0.5 >= seconds;
                    // Only frames at or past the moment are converted (plus the last one, in case the file ends first).
                    if (due || eof) last = Convert(conv, frame, pts) ?? last;
                    ffmpeg.av_frame_unref(frame);
                    if (due && last is not null) return last;
                }
                if (eof) return last;
            }
            return last;
        }
        finally
        {
            conv?.Dispose();
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&pkt);
            if (ctx != null) ffmpeg.avcodec_free_context(&ctx);
            ffmpeg.avformat_close_input(&fmt);
        }
    }

    private static Thumbnail? Convert(FrameConverter conv, AVFrame* frame, double pts)
    {
        using var vf = conv.Convert(frame);
        if (vf is null) return null;
        var bytes = new byte[vf.Width * vf.Height * 4];
        Buffer.BlockCopy(vf.Planes[0], 0, bytes, 0, bytes.Length);
        return new Thumbnail(bytes, vf.Width, vf.Height, pts);
    }
}
