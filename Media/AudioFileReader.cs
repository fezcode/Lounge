using FFmpeg.AutoGen;

namespace Lounge.Media;

/// <summary>Decodes an audio file to interleaved float PCM on demand. Used for the
/// ambient folder, where a whole player would be overkill.</summary>
public sealed unsafe class AudioFileReader : IDisposable
{
    private AVFormatContext* _fmt;
    private AudioPipeline? _pipe;
    private int _stream;
    private readonly Queue<float> _pending = new();
    private readonly int _channels;

    public bool Ended { get; private set; }

    private AudioFileReader(int channels) => _channels = channels;

    public static AudioFileReader? Open(string path, int rate, int channels)
    {
        if (!Ff.Ensure()) return null;
        var r = new AudioFileReader(channels);
        AVFormatContext* fmt = null;
        if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return null;
        r._fmt = fmt;
        try
        {
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) { r.Dispose(); return null; }
            r._stream = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (r._stream < 0) { r.Dispose(); return null; }
            for (int i = 0; i < fmt->nb_streams; i++) if (i != r._stream) fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            r._pipe = new AudioPipeline(fmt->streams[r._stream], rate, channels);
            return r;
        }
        catch (MediaException) { r.Dispose(); return null; }
    }

    /// <summary>Fills <paramref name="buffer"/>; returns samples written (fewer at the end).</summary>
    public int Read(Span<float> buffer)
    {
        int written = 0;
        var pkt = ffmpeg.av_packet_alloc();
        try
        {
            while (written < buffer.Length)
            {
                while (_pending.Count > 0 && written < buffer.Length) buffer[written++] = _pending.Dequeue();
                if (written >= buffer.Length || Ended) break;
                if (ffmpeg.av_read_frame(_fmt, pkt) < 0)
                {
                    foreach (var c in _pipe!.Decode(null)) Enqueue(c);
                    Ended = _pending.Count == 0;
                    if (Ended) break;
                    continue;
                }
                if (pkt->stream_index == _stream)
                    foreach (var c in _pipe!.Decode(pkt)) Enqueue(c);
                ffmpeg.av_packet_unref(pkt);
            }
        }
        finally { ffmpeg.av_packet_free(&pkt); }
        return written;
    }

    private void Enqueue(PcmChunk c)
    {
        for (int i = 0; i < c.Frames * _channels; i++) _pending.Enqueue(c.Data[i]);
    }

    public void Dispose()
    {
        _pipe?.Dispose(); _pipe = null;
        if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
    }
}
