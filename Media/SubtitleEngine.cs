using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;

namespace Lounge.Media;

public sealed record SubStyle(string Font, double Scale, uint Color, uint OutlineColor, double Outline, double Shadow);

/// <summary>Subtitles, as in tivi: text tracks (ASS/SSA, and SRT/VTT/... converted by
/// FFmpeg) render through libass; bitmap tracks (PGS, VobSub, DVB) come straight from
/// FFmpeg's decoder. An embedded track is decoded from the packets the player is already
/// reading (<see cref="Feed"/>), so lines appear as playback reaches them with no extra
/// disk reads; a small file is also preloaded in the background so seeking shows subtitles
/// at once. Both sources can deliver the same line, so events are de-duplicated. Plain-text
/// tracks take the user's font and colours through a generated ASS header; authored ASS
/// keeps its own styling.</summary>
public sealed unsafe class SubtitleEngine : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _lib, _renderer, _track;
    private readonly SubPictures _bitmaps = new();
    private bool _bitmapMode;
    private int _frameW, _frameH, _videoW = 1280, _videoH = 720;
    private byte[]? _overlay;
    private bool _hadContent, _overlayFresh;
    private List<SubPictures.Picture> _lastActive = [];
    private CancellationTokenSource? _preload;
    private SubStyle _style = new("sans-serif", 1, 0xFFFFFF, 0, 2, 0);
    private bool _plainText;
    private byte[]? _codecHeader;
    private readonly LibAss.MessageCallback _quiet = (_, _, _, _) => { };
    private long _generation;

    // The live decoder fed by the player's demuxer; guarded by _liveGate because the demux
    // thread decodes while the UI thread may switch tracks.
    private readonly object _liveGate = new();
    private AVCodecContext* _liveCtx;
    private int _liveStream = -1;
    private AVRational _liveTb;
    private bool _liveBitmap;
    private int _liveRefW, _liveRefH;
    // Lines already given to libass (start, text without its ReadOrder), so the live feed and
    // the preload never show a line twice. Bitmaps de-duplicate in SubPictures.
    private readonly HashSet<(long, string)> _seenText = new();
    private int _readOrder;

    /// <summary>Files up to this size are also preloaded in full in the background. Larger
    /// ones (4K films) rely on the live feed alone: reading a 10 GB file twice at once made a
    /// hard drive seek back and forth, starving playback, and a text track only appeared
    /// when that second pass reached the end of the file, minutes in.</summary>
    public static long PreloadLimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;

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
        StopLive();
        lock (_gate)
        {
            if (_track != IntPtr.Zero) { LibAss.ass_free_track(_track); _track = IntPtr.Zero; }
            _bitmaps.Clear();
            _bitmapMode = false;
            _events.Clear();
            _seenText.Clear();
            _lastActive = [];
            ExternalName = null;
        }
        Interlocked.Increment(ref _generation);
    }

    private void StopLive()
    {
        lock (_liveGate)
        {
            if (_liveCtx != null) { var c = _liveCtx; ffmpeg.avcodec_free_context(&c); _liveCtx = null; }
            _liveStream = -1;
        }
    }

    private void StartLive(AVCodecParameters* par, int streamIndex, AVRational timeBase, bool bitmap)
    {
        StopLive();
        if (par == null) return;
        var dec = ffmpeg.avcodec_find_decoder(par->codec_id);
        if (dec == null) return;
        var ctx = ffmpeg.avcodec_alloc_context3(dec);
        if (ffmpeg.avcodec_parameters_to_context(ctx, par) < 0) { ffmpeg.avcodec_free_context(&ctx); return; }
        ctx->pkt_timebase = timeBase;
        if (ffmpeg.avcodec_open2(ctx, dec, null) < 0) { ffmpeg.avcodec_free_context(&ctx); return; }
        lock (_liveGate)
        {
            _liveCtx = ctx;
            _liveStream = streamIndex;
            _liveTb = timeBase;
            _liveBitmap = bitmap;
            lock (_gate) { _liveRefW = ctx->width > 0 ? ctx->width : _videoW; _liveRefH = ctx->height > 0 ? ctx->height : _videoH; }
        }
    }

    /// <summary>The stream the live decoder wants packets from, or -1.</summary>
    public int LiveStream => _liveStream;

    /// <summary>Called by the player's demux thread with every packet of <see cref="LiveStream"/>.</summary>
    public void Feed(AVPacket* pkt)
    {
        lock (_liveGate)
        {
            if (_liveCtx == null || pkt->stream_index != _liveStream) return;
            try { DecodeOne(_liveCtx, pkt, _liveTb, _liveBitmap, _liveRefW, _liveRefH); }
            catch (Exception) { /* a broken subtitle packet must never take playback down */ }
        }
        Interlocked.Increment(ref _generation);
    }

    /// <summary>After a seek: drop the live decoder's state (PGS keeps half-built pictures),
    /// and any picture still waiting for the display set that ends it, which the new read
    /// position will not deliver.</summary>
    public void FlushLive()
    {
        lock (_liveGate)
        {
            if (_liveCtx == null) return;
            ffmpeg.avcodec_flush_buffers(_liveCtx);
            if (_liveBitmap) lock (_gate) _bitmaps.Flush();
        }
        Interlocked.Increment(ref _generation);
    }

    /// <summary>Decodes one subtitle packet and adds what it holds to the track.</summary>
    private void DecodeOne(AVCodecContext* ctx, AVPacket* pkt, AVRational tb, bool bitmap, int refW, int refH)
    {
        AVSubtitle sub;
        int got = 0;
        if (ffmpeg.avcodec_decode_subtitle2(ctx, &sub, &got, pkt) < 0 || got == 0) return;
        try
        {
            var ptsSec = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts * ffmpeg.av_q2d(tb) : 0;
            var start = ptsSec + sub.start_display_time / 1000.0;
            double dur = sub.end_display_time > sub.start_display_time && sub.end_display_time != uint.MaxValue
                ? (sub.end_display_time - sub.start_display_time) / 1000.0
                : pkt->duration > 0 ? pkt->duration * ffmpeg.av_q2d(tb) : double.NaN;
            if (bitmap) { AddBitmap(&sub, start, dur, refW, refH); return; }
            lock (_gate)
            {
                for (int r = 0; r < sub.num_rects; r++)
                {
                    var a = sub.rects[r]->ass;
                    if (a == null) continue;
                    var text = Marshal.PtrToStringUTF8((IntPtr)a) ?? "";
                    if (text.Length == 0) continue;
                    AddTextEvent((long)(start * 1000), (long)((double.IsNaN(dur) ? 4 : dur) * 1000), text);
                }
            }
        }
        finally { ffmpeg.avsubtitle_free(&sub); }
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

    /// <summary>Adds one decoded line (FFmpeg's "ReadOrder,Layer,Style,..." form) unless it
    /// is already there. Each decoder numbers its lines itself, so the ReadOrder is replaced
    /// with the engine's own counter (libass drops events whose ReadOrder repeats) and the
    /// rest of the line is what identifies it. Call under _gate.</summary>
    private void AddTextEvent(long startMs, long durMs, string text)
    {
        if (_track == IntPtr.Zero) return;
        var comma = text.IndexOf(',');
        var body = comma >= 0 ? text[(comma + 1)..] : text;
        if (!_seenText.Add((startMs, body))) return;
        var line = Encoding.UTF8.GetBytes($"{_readOrder++},{body}");
        if (_plainText) _events.Add((startMs, durMs, line));
        fixed (byte* p = line) LibAss.ass_process_chunk(_track, p, line.Length, startMs, durMs);
    }

    /// <summary>Starts using an embedded track: the header now, lines from the player's own
    /// packets as they are read (<paramref name="par"/> is the stream's parameters), and for
    /// a small file every line from a background scan as well.</summary>
    public void BeginEmbedded(string path, int streamIndex, string codec, bool bitmap, byte[]? header,
        AVCodecParameters* par = null, AVRational timeBase = default, bool preload = true)
    {
        Clear();
        lock (_gate)
        {
            if (bitmap) _bitmapMode = true;
            else if (Available) NewTextTrack(header, IsPlainText(codec));
        }
        if (par != null) StartLive(par, streamIndex, timeBase, bitmap);
        PreloadSeconds = -1;
        if (preload || par == null)
        {
            var cts = new CancellationTokenSource();
            _preload = cts;
            var t = new Thread(() => Collect(path, streamIndex, bitmap, cts.Token)) { IsBackground = true, Name = "sub-preload", Priority = ThreadPriority.BelowNormal };
            t.Start();
        }
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
            // The header comes from the decoder, so the lines are collected first and the
            // track made once it is known.
            var events = new List<(long Start, long Dur, string Text)>();
            if (!CollectEvents(path, -1, false, CancellationToken.None, events.Add, out var header, out var codec)) return false;
            lock (_gate)
            {
                NewTextTrack(header, IsPlainText(codec));
                foreach (var e in events) AddTextEvent(e.Start, e.Dur, e.Text);
            }
        }
        ExternalName = Path.GetFileName(path);
        Interlocked.Increment(ref _generation);
        return true;
    }

    /// <summary>Seconds the last background preload took, or -1 while it is still running.</summary>
    public double PreloadSeconds { get; private set; } = -1;

    private void Collect(string path, int stream, bool bitmap, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Lines go in as they are found (in batches), not only once the whole file has
            // been read, so a long file shows its early subtitles straight away.
            var batch = new List<(long Start, long Dur, string Text)>();
            void Flush()
            {
                lock (_gate) { if (!ct.IsCancellationRequested) foreach (var e in batch) AddTextEvent(e.Start, e.Dur, e.Text); }
                batch.Clear();
                Interlocked.Increment(ref _generation);
            }
            var ok = CollectEvents(path, stream, bitmap, ct, e => { batch.Add(e); if (batch.Count >= 64) Flush(); }, out _, out _);
            if (!ok || ct.IsCancellationRequested) return;
            Flush();
            PreloadSeconds = started.Elapsed.TotalSeconds;
        }
        catch (Exception) { /* a broken subtitle stream must never take playback down */ }
    }

    /// <summary>Reads every packet of one subtitle stream of a file. Text lines go to
    /// <paramref name="sink"/>; bitmaps are added to the track directly.</summary>
    private bool CollectEvents(string path, int streamIndex, bool bitmap, CancellationToken ct,
        Action<(long Start, long Dur, string Text)> sink, out byte[]? header, out string codec)
    {
        header = null; codec = "";
        AVFormatContext* fmt = null;
        if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return false;
        AVCodecContext* ctx = null;
        var pkt = ffmpeg.av_packet_alloc();
        try
        {
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) return false;
            var si = streamIndex >= 0 ? streamIndex : ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_SUBTITLE, -1, -1, null, 0);
            if (si < 0 || si >= fmt->nb_streams || fmt->streams[si]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_SUBTITLE) return false;
            var st = fmt->streams[si];
            var dec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
            if (dec == null) return false;
            codec = ffmpeg.avcodec_get_name(st->codecpar->codec_id) ?? "";
            ctx = ffmpeg.avcodec_alloc_context3(dec);
            ffmpeg.avcodec_parameters_to_context(ctx, st->codecpar);
            ctx->pkt_timebase = st->time_base;
            if (ffmpeg.avcodec_open2(ctx, dec, null) < 0) return false;
            for (int i = 0; i < fmt->nb_streams; i++)
                if (i != si) fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            var refW = ctx->width > 0 ? ctx->width : _videoW;
            var refH = ctx->height > 0 ? ctx->height : _videoH;

            var tb = st->time_base;
            while (ffmpeg.av_read_frame(fmt, pkt) >= 0)
            {
                if (ct.IsCancellationRequested) { ffmpeg.av_packet_unref(pkt); return false; }
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
                                var text = Marshal.PtrToStringUTF8((IntPtr)a) ?? "";
                                if (text.Length == 0) continue;
                                sink(((long)(start * 1000), (long)((double.IsNaN(dur) ? 4 : dur) * 1000), text));
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
            return true;
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
            // Every display set, a PGS "clear" (no rects) included, replaces what is on screen.
            _bitmaps.BeginSet(start);
            for (int i = 0; i < sub->num_rects; i++)
            {
                var r = sub->rects[i];
                if (r->type != AVSubtitleType.SUBTITLE_BITMAP || r->w <= 0 || r->h <= 0) continue;
                if (_bitmaps.Has(start, r->x, r->y, r->w, r->h)) continue;
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
                _bitmaps.Add(start, dur, r->x, r->y, r->w, r->h, px, refW, refH);
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
                _overlayFresh = true;
            }

            if (_bitmapMode)
            {
                // Redraw only when the set of pictures on screen changes (or the overlay was
                // just resized): clearing and re-blitting a full-screen overlay every frame
                // cost a 4K screen dearly.
                var active = _bitmaps.Active(now);
                if (!_overlayFresh && active.SequenceEqual(_lastActive, ReferenceEqualityComparer.Instance))
                    return active.Count > 0 ? _overlay : null;
                _overlayFresh = false;
                _lastActive = active;
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

    private void BlitScaled(SubPictures.Picture s, int width, int height)
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
