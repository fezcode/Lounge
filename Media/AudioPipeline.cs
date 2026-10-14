using System.Globalization;
using FFmpeg.AutoGen;

namespace Lounge.Media;

public readonly record struct PcmChunk(float[] Data, int Frames, double Pts, double SecondsPerFrame);

/// <summary>Decodes one audio stream and turns it into interleaved float PCM at the
/// output's rate and channel count. Speed works like tivi: with pitch correction an
/// atempo chain time-stretches; without it asetrate resamples, so pitch follows speed.
/// Media time is tracked by counting output frames, which stays exact through filters.</summary>
public sealed unsafe class AudioPipeline : IDisposable
{
    private readonly AVCodecContext* _ctx;
    private readonly AVRational _tb;
    private readonly int _outRate, _outChannels;
    private AVFilterGraph* _graph;
    private AVFilterContext* _src, _sink;
    private AVFrame* _frame = ffmpeg.av_frame_alloc();
    private AVFrame* _filtered = ffmpeg.av_frame_alloc();
    private int _graphRate, _graphFormat;
    private string _graphLayout = "";
    private double _runningPts = double.NaN;

    public double Speed { get; private set; } = 1;
    public bool PitchCorrect { get; private set; } = true;
    /// <summary>Output starting before this media time is trimmed (exact seek).</summary>
    public double TrimBefore { get; set; } = double.NegativeInfinity;

    public AudioPipeline(AVStream* stream, int outRate, int outChannels)
    {
        _tb = stream->time_base;
        _outRate = outRate;
        _outChannels = outChannels;
        var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null) throw new MediaException("No decoder for this audio.");
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        Ff.Check(ffmpeg.avcodec_parameters_to_context(_ctx, stream->codecpar), "audio parameters");
        _ctx->pkt_timebase = stream->time_base;
        Ff.Check(ffmpeg.avcodec_open2(_ctx, codec, null), "open audio decoder");
    }

    public void SetSpeed(double speed, bool pitchCorrect)
    {
        Speed = Math.Clamp(speed, 0.25, 4);
        PitchCorrect = pitchCorrect;
        FreeGraph();
    }

    public void Flush()
    {
        ffmpeg.avcodec_flush_buffers(_ctx);
        FreeGraph();
        _runningPts = double.NaN;
    }

    /// <summary>Feeds one packet (null drains at end of file) and returns the PCM it produced.</summary>
    public List<PcmChunk> Decode(AVPacket* packet)
    {
        var output = new List<PcmChunk>();
        var rc = ffmpeg.avcodec_send_packet(_ctx, packet);
        if (rc < 0 && rc != ffmpeg.AVERROR(ffmpeg.EAGAIN) && rc != ffmpeg.AVERROR_EOF) return output;
        while (true)
        {
            rc = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (rc < 0) break;
            if (double.IsNaN(_runningPts))
            {
                var ts = _frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? _frame->best_effort_timestamp : _frame->pts;
                _runningPts = ts == ffmpeg.AV_NOPTS_VALUE ? 0 : Ff.ToSeconds(ts, _tb);
            }
            Filter(_frame, output);
            ffmpeg.av_frame_unref(_frame);
        }
        if (packet == null && _graph != null)
        {
            ffmpeg.av_buffersrc_add_frame_flags(_src, null, 0);
            Drain(output);
        }
        return output;
    }

    private void Filter(AVFrame* frame, List<PcmChunk> output)
    {
        var layout = Describe(&frame->ch_layout);
        if (_graph == null || frame->sample_rate != _graphRate || frame->format != _graphFormat || layout != _graphLayout)
            BuildGraph(frame->sample_rate, (AVSampleFormat)frame->format, layout);
        if (ffmpeg.av_buffersrc_add_frame_flags(_src, frame, 8 /* AV_BUFFERSRC_FLAG_KEEP_REF */) < 0) return;
        Drain(output);
    }

    private void Drain(List<PcmChunk> output)
    {
        var spf = Speed / _outRate;
        while (ffmpeg.av_buffersink_get_frame(_sink, _filtered) >= 0)
        {
            var frames = _filtered->nb_samples;
            var data = new float[frames * _outChannels];
            new ReadOnlySpan<float>(_filtered->data[0], frames * _outChannels).CopyTo(data);
            ffmpeg.av_frame_unref(_filtered);

            var pts = _runningPts;
            _runningPts += frames * spf;
            if (_runningPts <= TrimBefore) continue;
            if (pts < TrimBefore)
            {
                var skip = Math.Clamp((int)((TrimBefore - pts) / spf), 0, frames);
                var rest = frames - skip;
                var trimmed = new float[rest * _outChannels];
                Array.Copy(data, skip * _outChannels, trimmed, 0, trimmed.Length);
                output.Add(new PcmChunk(trimmed, rest, pts + skip * spf, spf));
                continue;
            }
            output.Add(new PcmChunk(data, frames, pts, spf));
        }
    }

    private static string Describe(AVChannelLayout* layout)
    {
        var buf = stackalloc byte[128];
        ffmpeg.av_channel_layout_describe(layout, buf, 128);
        return new string((sbyte*)buf);
    }

    private string OutLayout => _outChannels switch { 1 => "mono", 6 => "5.1", 8 => "7.1", _ => "stereo" };

    private static string TempoChain(double speed)
    {
        var parts = new List<string>();
        var s = speed;
        while (s < 0.5) { parts.Add("atempo=0.5"); s /= 0.5; }
        while (s > 2.0) { parts.Add("atempo=2.0"); s /= 2.0; }
        if (Math.Abs(s - 1) > 1e-6) parts.Add("atempo=" + s.ToString("0.######", CultureInfo.InvariantCulture));
        return string.Join(",", parts);
    }

    private void BuildGraph(int rate, AVSampleFormat fmt, string layout)
    {
        FreeGraph();
        _graph = ffmpeg.avfilter_graph_alloc();
        var args = $"time_base=1/{rate}:sample_rate={rate}:sample_fmt={ffmpeg.av_get_sample_fmt_name(fmt)}:channel_layout={layout}";
        AVFilterContext* src, sink;
        Ff.Check(ffmpeg.avfilter_graph_create_filter(&src, ffmpeg.avfilter_get_by_name("abuffer"), "in", args, null, _graph), "abuffer");
        Ff.Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("abuffersink"), "out", null, null, _graph), "abuffersink");
        _src = src; _sink = sink;

        var chain = new List<string>();
        if (Math.Abs(Speed - 1) > 1e-6)
        {
            if (PitchCorrect) chain.Add(TempoChain(Speed));
            else chain.Add($"asetrate={(int)Math.Round(rate * Speed)},aresample={rate}");
        }
        chain.Add($"aresample={_outRate}");
        chain.Add($"aformat=sample_fmts=flt:sample_rates={_outRate}:channel_layouts={OutLayout}");
        var desc = string.Join(",", chain.Where(c => c.Length > 0));

        var outputs = ffmpeg.avfilter_inout_alloc();
        var inputs = ffmpeg.avfilter_inout_alloc();
        outputs->name = ffmpeg.av_strdup("in"); outputs->filter_ctx = src; outputs->pad_idx = 0; outputs->next = null;
        inputs->name = ffmpeg.av_strdup("out"); inputs->filter_ctx = sink; inputs->pad_idx = 0; inputs->next = null;
        var rc = ffmpeg.avfilter_graph_parse_ptr(_graph, desc, &inputs, &outputs, null);
        ffmpeg.avfilter_inout_free(&inputs);
        ffmpeg.avfilter_inout_free(&outputs);
        Ff.Check(rc, "audio filter " + desc);
        Ff.Check(ffmpeg.avfilter_graph_config(_graph, null), "audio filter config");
        _graphRate = rate; _graphFormat = (int)fmt; _graphLayout = layout;
    }

    private void FreeGraph()
    {
        if (_graph != null) { var g = _graph; ffmpeg.avfilter_graph_free(&g); _graph = null; }
        _src = _sink = null;
    }

    public void Dispose()
    {
        FreeGraph();
        var c = _ctx; ffmpeg.avcodec_free_context(&c);
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_filtered != null) { var f = _filtered; ffmpeg.av_frame_free(&f); _filtered = null; }
    }
}
