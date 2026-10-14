using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;

namespace Lounge.Media;

public sealed record SubStyle(string Font, double Scale, uint Color, uint OutlineColor, double Outline, double Shadow);

/// <summary>Subtitles, as in tivi: text tracks (ASS/SSA, and SRT/VTT/... converted by
/// FFmpeg) render through libass; bitmap tracks (PGS, VobSub, DVB) come straight from
/// FFmpeg's decoder. An embedded track is preloaded in the background so seeking shows
/// subtitles at once. Plain-text tracks take the user's font and colours through a
/// generated ASS header; authored ASS keeps its own styling.</summary>
public sealed unsafe class SubtitleEngine : IDisposable
{
    private sealed record BitmapSub(double Start, double End, int X, int Y, int W, int H, byte[] Bgra, int RefW, int RefH);

    private readonly object _gate = new();
    private IntPtr _lib, _renderer, _track;
    private readonly List<BitmapSub> _bitmaps = new();
    private bool _bitmapMode;
    private int _frameW, _frameH, _videoW = 1280, _videoH = 720;
    private byte[]? _overlay;
    private bool _hadContent;
    private CancellationTokenSource? _preload;
    private SubStyle _style = new("sans-serif", 1, 0xFFFFFF, 0, 2, 0);
    private bool _plainText;
    private byte[]? _codecHeader;
    private readonly LibAss.MessageCallback _quiet = (_, _, _, _) => { };
    private long _generation;

    public bool HasTrack { get { lock (_gate) return _track != IntPtr.Zero || _bitmapMode; } }
    public int DelayMs { get; set; }
    public string? ExternalName { get; private set; }
    /// <summary>Bumps whenever content may have changed (new track, style) so the UI re-renders.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    public SubtitleEngine()
    {
        if (!Ff.Ensure()) return;
        try
        {
            _lib = LibAss.ass_library_init();
            if (_lib == IntPtr.Zero) return;
            LibAss.ass_set_message_cb(_lib, Marshal.GetFunctionPointerForDelegate(_quiet), IntPtr.Zero);
            _renderer = LibAss.ass_renderer_init(_lib);
            if (_renderer != IntPtr.Zero)
                LibAss.ass_set_fonts(_renderer, null, "sans-serif", LibAss.FontProviderAutodetect, null, 1);
        }
        catch (DllNotFoundException) { _lib = IntPtr.Zero; }
        catch (EntryPointNotFoundException) { _lib = IntPtr.Zero; }
    }

    public bool Available => _renderer != IntPtr.Zero;

    public void SetVideoSize(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        lock (_gate) { _videoW = w; _videoH = h; }
    }

    public void SetStyle(SubStyle style)
    {
        lock (_gate)
        {
            _style = style;
            if (_track != IntPtr.Zero && _plainText) RebuildTextTrack();
        }
        Interlocked.Increment(ref _generation);
    }

    public void Clear()
    {
        _preload?.Cancel();
        lock (_gate)
        {
            if (_track != IntPtr.Zero) { LibAss.ass_free_track(_track); _track = IntPtr.Zero; }
            _bitmaps.Clear();
            _bitmapMode = false;
            _events.Clear();
            ExternalName = null;
        }
        Interlocked.Increment(ref _generation);
    }

    // Events kept for plain-text tracks so a style change can rebuild the track.
    private readonly List<(long Start, long Dur, byte[] Line)> _events = new();

    private static bool IsPlainText(string codec) =>
        codec is not ("ass" or "ssa");

    /// <summary>The ASS header used for plain-text subtitles, carrying the user's style.
    /// 16 at PlayResY 288 is FFmpeg's own default size (about 5.5% of the picture).</summary>
    public static string StyledHeader(SubStyle s)
    {
        static string Bgr(uint rgb) => $"&H00{rgb & 0xFF:X2}{(rgb >> 8) & 0xFF:X2}{(rgb >> 16) & 0xFF:X2}";
        var inv = CultureInfo.InvariantCulture;
        return "[Script Info]\nScriptType: v4.00+\nPlayResX: 384\nPlayResY: 288\nScaledBorderAndShadow: yes\nWrapStyle: 0\n\n" +
               "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
               $"Style: Default,{s.Font},{(16 * s.Scale).ToString("0.##", inv)},{Bgr(s.Color)},{Bgr(s.Color)},{Bgr(s.OutlineColor)},&H80000000,-1,0,0,0,100,100,0,0,1,{s.Outline.ToString("0.##", inv)},{s.Shadow.ToString("0.##", inv)},2,10,10,12,1\n\n" +
               "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";
    }

    private void NewTextTrack(byte[]? header, bool plain)
    {
        if (_track != IntPtr.Zero) LibAss.ass_free_track(_track);
        _track = LibAss.ass_new_track(_lib);
        _plainText = plain;
        _codecHeader = header;
        var h = plain ? Encoding.UTF8.GetBytes(StyledHeader(_style)) : header;
        if (h is { Length: > 0 })
            fixed (byte* p = h) LibAss.ass_process_codec_private(_track, p, h.Length);
    }

    private void RebuildTextTrack()
    {
        NewTextTrack(_codecHeader, _plainText);
        foreach (var (s, d, line) in _events)
            fixed (byte* p = line) LibAss.ass_process_chunk(_track, p, line.Length, s, d);
    }

    private void AddTextEvent(long startMs, long durMs, byte[] line)
    {
        if (_track == IntPtr.Zero) return;
        if (_plainText) _events.Add((startMs, durMs, line));
        fixed (byte* p = line) LibAss.ass_process_chunk(_track, p, line.Length, startMs, durMs);
    }

    /// <summary>Starts using an embedded track: the header now, every event from a
    /// background scan of the whole file.</summary>
    public void BeginEmbedded(string path, int streamIndex, string codec, bool bitmap, byte[]? header)
    {
        Clear();
        lock (_gate)
        {
            if (bitmap) _bitmapMode = true;
            else if (Available) NewTextTrack(header, IsPlainText(codec));
        }
        var cts = new CancellationTokenSource();
        _preload = cts;
        var t = new Thread(() => Collect(path, streamIndex, bitmap, cts.Token)) { IsBackground = true, Name = "sub-preload", Priority = ThreadPriority.BelowNormal };
        t.Start();
        Interlocked.Increment(ref _generation);
    }

    /// <summary>Loads a subtitle file: .ass/.ssa straight into libass, anything else
    /// through FFmpeg's demuxer and decoder.</summary>
    public bool LoadFile(string path)
    {
        Clear();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!Available) return false;
        if (ext is ".ass" or ".ssa")
        {
            var bytes = File.ReadAllBytes(path);
            lock (_gate)
            {
                fixed (byte* p = bytes) _track = LibAss.ass_read_memory(_lib, p, (nuint)bytes.Length, null);
                _plainText = false;
                if (_track == IntPtr.Zero) return false;
            }
        }
        else
        {
            var events = CollectEvents(path, -1, false, CancellationToken.None, out var header, out var codec);
            if (events is null) return false;
            lock (_gate)
            {
                NewTextTrack(header, IsPlainText(codec));
                foreach (var e in events) AddTextEvent(e.Start, e.Dur, e.Line);
            }
        }
        ExternalName = Path.GetFileName(path);
        Interlocked.Increment(ref _generation);
        return true;
    }

    private void Collect(string path, int stream, bool bitmap, CancellationToken ct)
    {
        try
        {
            var events = CollectEvents(path, stream, bitmap, ct, out _, out _);
            if (events is null || ct.IsCancellationRequested) return;
            lock (_gate)
            {
                if (ct.IsCancellationRequested) return;
                if (!bitmap) foreach (var e in events) AddTextEvent(e.Start, e.Dur, e.Line);
            }
            Interlocked.Increment(ref _generation);
        }
        catch (Exception) { /* a broken subtitle stream must never take playback down */ }
    }

    private sealed record TextEvent(long Start, long Dur, byte[] Line);

    private List<TextEvent>? CollectEvents(string path, int streamIndex, bool bitmap, CancellationToken ct, out byte[]? header, out string codec)
    {
        header = null; codec = "";
        AVFormatContext* fmt = null;
        if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return null;
        AVCodecContext* ctx = null;
        var pkt = ffmpeg.av_packet_alloc();
        try
        {
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) return null;
            var si = streamIndex >= 0 ? streamIndex : ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_SUBTITLE, -1, -1, null, 0);
            if (si < 0 || si >= fmt->nb_streams || fmt->streams[si]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_SUBTITLE) return null;
            var st = fmt->streams[si];
            var dec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
            if (dec == null) return null;
            codec = ffmpeg.avcodec_get_name(st->codecpar->codec_id) ?? "";
            ctx = ffmpeg.avcodec_alloc_context3(dec);
            ffmpeg.avcodec_parameters_to_context(ctx, st->codecpar);
            ctx->pkt_timebase = st->time_base;
            if (ffmpeg.avcodec_open2(ctx, dec, null) < 0) return null;
            for (int i = 0; i < fmt->nb_streams; i++)
                if (i != si) fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            var refW = ctx->width > 0 ? ctx->width : _videoW;
            var refH = ctx->height > 0 ? ctx->height : _videoH;

            var list = new List<TextEvent>();
            var tb = st->time_base;
            while (ffmpeg.av_read_frame(fmt, pkt) >= 0)
            {
                if (ct.IsCancellationRequested) { ffmpeg.av_packet_unref(pkt); return null; }
                if (pkt->stream_index == si)
                {
                    AVSubtitle sub;
                    int got = 0;
                    if (ffmpeg.avcodec_decode_subtitle2(ctx, &sub, &got, pkt) >= 0 && got != 0)
                    {
                        var ptsSec = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts * ffmpeg.av_q2d(tb) : 0;
                        var start = ptsSec + sub.start_display_time / 1000.0;
                        double dur = sub.end_display_time > sub.start_display_time && sub.end_display_time != uint.MaxValue
                            ? (sub.end_display_time - sub.start_display_time) / 1000.0
                            : pkt->duration > 0 ? pkt->duration * ffmpeg.av_q2d(tb) : double.NaN;
                        if (bitmap) AddBitmap(&sub, start, dur, refW, refH);
                        else
                        {
                            for (int r = 0; r < sub.num_rects; r++)
                            {
                                var a = sub.rects[r]->ass;
                                if (a == null) continue;
                                var line = Encoding.UTF8.GetBytes(Marshal.PtrToStringUTF8((IntPtr)a) ?? "");
                                if (line.Length == 0) continue;
                                list.Add(new TextEvent((long)(start * 1000), (long)((double.IsNaN(dur) ? 4 : dur) * 1000), line));
                            }
                        }
                        ffmpeg.avsubtitle_free(&sub);
                    }
                }
                ffmpeg.av_packet_unref(pkt);
            }
            if (ctx->subtitle_header != null && ctx->subtitle_header_size > 0)
            {
                header = new byte[ctx->subtitle_header_size];
                new ReadOnlySpan<byte>(ctx->subtitle_header, ctx->subtitle_header_size).CopyTo(header);
            }
            return list;
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            if (ctx != null) ffmpeg.avcodec_free_context(&ctx);
            ffmpeg.avformat_close_input(&fmt);
        }
    }

    private void AddBitmap(AVSubtitle* sub, double start, double dur, int refW, int refH)
    {
        lock (_gate)
        {
            // A PGS "clear" (no rects) ends whatever is on screen.
            foreach (var open in _bitmaps.Where(b => double.IsInfinity(b.End) && b.Start < start).ToList())
                _bitmaps[_bitmaps.IndexOf(open)] = open with { End = start };
            for (int i = 0; i < sub->num_rects; i++)
            {
                var r = sub->rects[i];
                if (r->type != AVSubtitleType.SUBTITLE_BITMAP || r->w <= 0 || r->h <= 0) continue;
                var px = new byte[r->w * r->h * 4];
                var palette = (uint*)r->data[1];
                for (int y = 0; y < r->h; y++)
                    for (int x = 0; x < r->w; x++)
                    {
                        var idx = r->data[0][y * r->linesize[0] + x];
                        var argb = palette[idx];               // FFmpeg palettes are 0xAARRGGBB
                        var a = (byte)(argb >> 24);
                        var o = (y * r->w + x) * 4;
                        px[o] = (byte)((argb & 0xFF) * a / 255);
                        px[o + 1] = (byte)(((argb >> 8) & 0xFF) * a / 255);
                        px[o + 2] = (byte)(((argb >> 16) & 0xFF) * a / 255);
                        px[o + 3] = a;
                    }
                var end = double.IsNaN(dur) || dur <= 0 ? double.PositiveInfinity : start + dur;
                _bitmaps.Add(new BitmapSub(start, end, r->x, r->y, r->w, r->h, px, refW, refH));
            }
        }
    }

    /// <summary>Renders the subtitles for <paramref name="seconds"/> into a premultiplied
    /// BGRA overlay of the given size. Returns null when nothing is on screen; the same
    /// array is reused while the size stays.</summary>
    public byte[]? Render(double seconds, int width, int height, out bool changed)
    {
        changed = false;
        if (width <= 0 || height <= 0) return null;
        lock (_gate)
        {
            var now = seconds - DelayMs / 1000.0;
            if (_overlay is null || _frameW != width || _frameH != height)
            {
                _overlay = new byte[width * height * 4];
                _frameW = width; _frameH = height;
                if (_renderer != IntPtr.Zero)
                {
                    LibAss.ass_set_frame_size(_renderer, width, height);
                    LibAss.ass_set_storage_size(_renderer, _videoW, _videoH);
                    LibAss.ass_set_use_margins(_renderer, 0);
                    LibAss.ass_set_margins(_renderer, 0, 0, 0, 0);
                }
                _hadContent = true;   // force a clear + redraw
            }

            if (_bitmapMode)
            {
                var active = _bitmaps.Where(b => b.Start <= now && now < b.End).ToList();
                Array.Clear(_overlay);
                foreach (var b in active) BlitScaled(b, width, height);
                changed = true;
                _hadContent = active.Count > 0;
                return active.Count > 0 ? _overlay : null;
            }

            if (_track == IntPtr.Zero || _renderer == IntPtr.Zero)
            {
                changed = _hadContent;
                _hadContent = false;
                return null;
            }

            int detect;
            var img = LibAss.ass_render_frame(_renderer, _track, (long)(now * 1000), &detect);
            if (detect == 0 && !_hadContent && img == null) return null;
            if (detect == 0 && _hadContent) { return _overlay; }
            changed = true;
            Array.Clear(_overlay);
            var any = false;
            for (var i = img; i != null; i = i->next)
            {
                if (i->w <= 0 || i->h <= 0) continue;
                any = true;
                Blend(i, width, height);
            }
            _hadContent = any;
            return any ? _overlay : null;
        }
    }

    private void Blend(LibAss.AssImage* img, int width, int height)
    {
        var c = img->color;
        int r = (int)(c >> 24), g = (int)((c >> 16) & 0xFF), b = (int)((c >> 8) & 0xFF);
        int alpha = 255 - (int)(c & 0xFF);
        var o = _overlay!;
        for (int y = 0; y < img->h; y++)
        {
            int dy = img->dst_y + y;
            if (dy < 0 || dy >= height) continue;
            for (int x = 0; x < img->w; x++)
            {
                int dx = img->dst_x + x;
                if (dx < 0 || dx >= width) continue;
                int a = img->bitmap[y * img->stride + x] * alpha / 255;
                if (a == 0) continue;
                int p = (dy * width + dx) * 4;
                int inv = 255 - a;
                o[p] = (byte)((b * a + o[p] * inv) / 255);
                o[p + 1] = (byte)((g * a + o[p + 1] * inv) / 255);
                o[p + 2] = (byte)((r * a + o[p + 2] * inv) / 255);
                o[p + 3] = (byte)(a + o[p + 3] * inv / 255);
            }
        }
    }

    private void BlitScaled(BitmapSub s, int width, int height)
    {
        var sx = width / (double)s.RefW;
        var sy = height / (double)s.RefH;
        int x0 = (int)(s.X * sx), y0 = (int)(s.Y * sy);
        int w = Math.Max(1, (int)(s.W * sx)), h = Math.Max(1, (int)(s.H * sy));
        var o = _overlay!;
        for (int y = 0; y < h; y++)
        {
            int dy = y0 + y;
            if (dy < 0 || dy >= height) continue;
            int srcY = Math.Min(s.H - 1, (int)(y / sy));
            for (int x = 0; x < w; x++)
            {
                int dx = x0 + x;
                if (dx < 0 || dx >= width) continue;
                int srcX = Math.Min(s.W - 1, (int)(x / sx));
                int si = (srcY * s.W + srcX) * 4;
                int a = s.Bgra[si + 3];
                if (a == 0) continue;
                int p = (dy * width + dx) * 4;
                int inv = 255 - a;
                o[p] = (byte)(s.Bgra[si] + o[p] * inv / 255);
                o[p + 1] = (byte)(s.Bgra[si + 1] + o[p + 1] * inv / 255);
                o[p + 2] = (byte)(s.Bgra[si + 2] + o[p + 2] * inv / 255);
                o[p + 3] = (byte)(a + o[p + 3] * inv / 255);
            }
        }
    }

    public void Dispose()
    {
        Clear();
        lock (_gate)
        {
            if (_renderer != IntPtr.Zero) { LibAss.ass_renderer_done(_renderer); _renderer = IntPtr.Zero; }
            if (_lib != IntPtr.Zero) { LibAss.ass_library_done(_lib); _lib = IntPtr.Zero; }
        }
    }
}
