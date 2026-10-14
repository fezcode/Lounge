using System.Diagnostics;
using FFmpeg.AutoGen;

namespace Lounge.Media;

public enum PlayerState { Idle, Opening, Paused, Playing, Ended, Failed }

public sealed class PlayerOptions
{
    public bool HwDecode { get; set; } = true;
    public bool GpuConvert { get; set; } = true;
    public bool PitchCorrect { get; set; } = true;
    public bool SubtitlesEnabled { get; set; } = true;
    public bool SubAuto { get; set; } = true;
    public string SubLanguage { get; set; } = "";
    public string AudioLanguage { get; set; } = "";
    public float Volume { get; set; } = 0.85f;
}

/// <summary>A port of tivi's player: FFmpeg demux on one thread, video and audio decode on
/// their own, audio as the master clock, D3D11VA hardware decoding with software
/// fallback, exact seeks (keyframe, then decode forward) and fast keyframe-only seeks for
/// trigger scrubbing. The UI pulls the frame due now with <see cref="TakeFrame"/>.</summary>
public sealed unsafe class MediaPlayer : IDisposable
{
    private readonly object _gate = new();
    private readonly AudioOutput? _audioOut;
    private readonly Stopwatch _sysClock = Stopwatch.StartNew();

    private AVFormatContext* _fmt;
    private AVCodecContext* _vctx;
    private AVBufferRef* _hwDevice;
    private AVStream* _vstream;
    private AudioPipeline? _audio;
    private int _audioPipelineStream = -1;
    private FrameConverter? _converter;

    private readonly PacketQueue _vq = new(96);
    private readonly PacketQueue _aq = new(256);
    private readonly List<VideoFrame> _frames = new();
    private const int MaxFrames = 4;

    private Thread? _demuxThread, _videoThread, _audioThread;
    private volatile bool _stop;
    private volatile int _serial;
    private int _demuxSerial;
    private (double Target, bool Exact, int Serial)? _seekRequest;
    private double _exactTarget = double.NegativeInfinity;
    private bool _presentFirst;
    private bool _videoEof, _audioEof, _demuxEof;
    private double _seekTarget;
    private bool _seekPending;
    private double _sysBase;
    private long _sysStartTicks;
    private bool _playing;
    private long _audioWaitSince;
    private bool _audioDead;
    private bool _scrubbing, _scrubWasPlaying;
    private double? _queuedScrub;
    private PlayerOptions _opts = new();
    private string? _path;

    public PlayerState State { get; private set; } = PlayerState.Idle;
    public string? Path => _path;
    public double Duration { get; private set; }
    public IReadOnlyList<TrackInfo> Tracks { get; private set; } = [];
    public IReadOnlyList<(double Start, string Title)> Chapters { get; private set; } = [];
    public int VideoStream { get; private set; } = -1;
    public int AudioStream { get; private set; } = -1;
    public int SubtitleStream { get; private set; } = -1;
    public bool HasVideo => VideoStream >= 0;
    public bool HasAudio => AudioStream >= 0 && _audioOut is not null;
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }
    public string? HardwareDecoder { get; private set; }
    public double Speed { get; private set; } = 1;
    public bool Scrubbing => _scrubbing;
    public string? LastError { get; private set; }
    public SubtitleEngine Subtitles { get; } = new();

    public event Action<PlayerState>? StateChanged;
    public event Action? TracksChanged;

    public MediaPlayer()
    {
        try { _audioOut = new AudioOutput(); }
        catch (Exception e) { LastError = "No audio output: " + e.Message; _audioOut = null; }
    }

    public float Volume
    {
        get => _audioOut?.Volume ?? 0;
        set { if (_audioOut is not null) _audioOut.Volume = value; }
    }

    private void SetState(PlayerState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    // ------------------------------------------------------------------ open / close

    /// <summary>Opens a file. Call off the UI thread; it probes the streams. Returns false
    /// with <see cref="LastError"/> set when the file cannot be played.</summary>
    public bool Open(string path, PlayerOptions options, double startAt = 0)
    {
        Close();
        _opts = options;
        _path = path;
        SetState(PlayerState.Opening);
        if (!Ff.Ensure()) { Fail(Ff.Error ?? "FFmpeg is missing."); return false; }

        AVFormatContext* fmt = null;
        var rc = ffmpeg.avformat_open_input(&fmt, path, null, null);
        if (rc < 0) { Fail("Can't open this file: " + Ff.Describe(rc)); return false; }
        _fmt = fmt;
        rc = ffmpeg.avformat_find_stream_info(_fmt, null);
        if (rc < 0) { Fail("Can't read this file: " + Ff.Describe(rc)); return false; }

        Duration = _fmt->duration > 0 ? _fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
        Tracks = MediaProbe.Tracks(_fmt);
        var chapters = new List<(double, string)>();
        for (int i = 0; i < _fmt->nb_chapters; i++)
        {
            var ch = _fmt->chapters[i];
            chapters.Add((Ff.ToSeconds(ch->start, ch->time_base), Ff.Tag(ch->metadata, "title") ?? $"Chapter {i + 1}"));
        }
        Chapters = chapters;

        var v = Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video && t.IsDefault) ?? Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video);
        if (v is not null) VideoStream = v.StreamIndex;
        var audioTracks = Tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
        var a = audioTracks.FirstOrDefault(t => t.MatchesLanguage(options.AudioLanguage))
                ?? audioTracks.FirstOrDefault(t => t.IsDefault) ?? audioTracks.FirstOrDefault();
        if (a is not null) AudioStream = a.StreamIndex;
        if (VideoStream < 0 && AudioStream < 0) { Fail("This file has no video or audio Lounge can play."); return false; }

        try
        {
            if (VideoStream >= 0) OpenVideo();
            if (AudioStream >= 0 && _audioOut is not null)
            {
                _audio = new AudioPipeline(_fmt->streams[AudioStream], _audioOut.SampleRate, _audioOut.Channels);
                _audio.SetSpeed(1, options.PitchCorrect);
                _audioPipelineStream = AudioStream;
            }
        }
        catch (MediaException e)
        {
            if (VideoStream < 0 || _vctx == null) { Fail(e.Message); return false; }
            AudioStream = -1;   // play the picture even if the sound cannot be decoded
        }

        for (int i = 0; i < _fmt->nb_streams; i++)
            if (i != VideoStream && i != AudioStream) _fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;

        Subtitles.SetVideoSize(VideoWidth, VideoHeight);
        ChooseSubtitles(path, options);

        if (_audioOut is not null) { _audioOut.Volume = options.Volume; _audioOut.Serial = _serial; _audioOut.Clear(); }
        _stop = false;
        _videoEof = VideoStream < 0;
        _audioEof = !HasAudio;
        _demuxEof = false;
        _presentFirst = true;
        _seekTarget = 0;
        _sysBase = 0;
        if (startAt > 0.5 && (Duration <= 0 || startAt < Duration)) RequestSeek(startAt, exact: true);

        _demuxThread = new Thread(DemuxLoop) { IsBackground = true, Name = "demux" };
        _demuxThread.Start();
        if (VideoStream >= 0) { _videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "video" }; _videoThread.Start(); }
        if (HasAudio) { _audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "audio" }; _audioThread.Start(); }
        SetState(PlayerState.Paused);
        TracksChanged?.Invoke();
        return true;
    }

    private void OpenVideo()
    {
        _vstream = _fmt->streams[VideoStream];
        var par = _vstream->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(par->codec_id);
        if (codec == null) throw new MediaException("No decoder for this video.");
        _vctx = ffmpeg.avcodec_alloc_context3(codec);
        Ff.Check(ffmpeg.avcodec_parameters_to_context(_vctx, par), "video parameters");
        _vctx->pkt_timebase = _vstream->time_base;
        VideoWidth = par->width;
        VideoHeight = par->height;
        HardwareDecoder = null;

        if (_opts.HwDecode && SupportsD3D11(codec))
        {
            AVBufferRef* dev = null;
            if (ffmpeg.av_hwdevice_ctx_create(&dev, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) >= 0)
            {
                _hwDevice = dev;
                _vctx->hw_device_ctx = ffmpeg.av_buffer_ref(dev);
                HardwareDecoder = "D3D11VA";
            }
        }
        if (HardwareDecoder is null)
        {
            _vctx->thread_count = Math.Clamp(Environment.ProcessorCount, 1, 16);
            _vctx->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
        }
        var rc = ffmpeg.avcodec_open2(_vctx, codec, null);
        if (rc < 0 && HardwareDecoder is not null)
        {
            // Some profiles the GPU refuses outright; decode them in software instead.
            var c = _vctx; ffmpeg.avcodec_free_context(&c);
            _vctx = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(_vctx, par);
            _vctx->pkt_timebase = _vstream->time_base;
            _vctx->thread_count = Math.Clamp(Environment.ProcessorCount, 1, 16);
            HardwareDecoder = null;
            rc = ffmpeg.avcodec_open2(_vctx, codec, null);
        }
        Ff.Check(rc, "open video decoder");
        _converter = new FrameConverter(_opts.GpuConvert);
    }

    private static bool SupportsD3D11(AVCodec* codec)
    {
        for (int i = 0; ; i++)
        {
            var cfg = ffmpeg.avcodec_get_hw_config(codec, i);
            if (cfg == null) return false;
            if (cfg->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA &&
                (cfg->methods & 0x01 /* AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX */) != 0) return true;
        }
    }

    private void ChooseSubtitles(string path, PlayerOptions options)
    {
        SubtitleStream = -1;
        Subtitles.Clear();
        if (!options.SubtitlesEnabled) return;
        if (options.SubAuto && HasVideo && Lounge.Core.Media.SidecarSubs.Find(path, options.SubLanguage) is { } sidecar)
        {
            if (Subtitles.LoadFile(sidecar)) return;
        }
        var subs = Tracks.Where(t => t.Kind == TrackKind.Subtitle).ToList();
        var pick = subs.FirstOrDefault(t => t.MatchesLanguage(options.SubLanguage) && !t.IsForced)
                   ?? subs.FirstOrDefault(t => t.IsDefault)
                   ?? subs.FirstOrDefault(t => t.IsForced);
        if (pick is not null) SelectSubtitle(pick.StreamIndex);
    }

    private void Fail(string message)
    {
        LastError = message;
        SetState(PlayerState.Failed);
    }

    public void Close()
    {
        _stop = true;
        _vq.Close(); _aq.Close();
        lock (_frames) Monitor.PulseAll(_frames);
        _demuxThread?.Join(2000); _videoThread?.Join(2000); _audioThread?.Join(2000);
        _demuxThread = _videoThread = _audioThread = null;
        _audioOut?.Pause();
        _audioOut?.Clear();
        lock (_frames) { foreach (var f in _frames) f.Dispose(); _frames.Clear(); }
        _vq.Clear(); _aq.Clear();
        _converter?.Dispose(); _converter = null;
        _audio?.Dispose(); _audio = null; _audioPipelineStream = -1;
        if (_vctx != null) { var c = _vctx; ffmpeg.avcodec_free_context(&c); _vctx = null; }
        if (_hwDevice != null) { var d = _hwDevice; ffmpeg.av_buffer_unref(&d); _hwDevice = null; }
        if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
        Subtitles.Clear();
        VideoStream = AudioStream = SubtitleStream = -1;
        Tracks = [];
        Chapters = [];
        Duration = 0;
        Speed = 1;
        _playing = false;
        _scrubbing = false;
        _audioDead = false;
        _vq.Reopen(); _aq.Reopen();
        if (State != PlayerState.Idle) SetState(PlayerState.Idle);
    }

    // ------------------------------------------------------------------ transport

    public void Play()
    {
        if (_fmt == null || _scrubbing) return;
        if (State == PlayerState.Ended) Seek(0);
        lock (_gate)
        {
            if (_playing) return;
            _playing = true;
            _sysStartTicks = _sysClock.ElapsedTicks;
            _audioWaitSince = 0;
        }
        _audioOut?.Play();
        SetState(PlayerState.Playing);
    }

    public void Pause()
    {
        if (_fmt == null) return;
        lock (_gate)
        {
            if (!_playing) return;
            _sysBase = SystemClock();
            _playing = false;
        }
        _audioOut?.Pause();
        SetState(PlayerState.Paused);
    }

    public void TogglePause()
    {
        if (_playing) Pause(); else Play();
    }

    public bool IsPlaying => _playing;

    private double SystemClock() =>
        _playing ? _sysBase + (_sysClock.ElapsedTicks - _sysStartTicks) / (double)Stopwatch.Frequency * Speed : _sysBase;

    /// <summary>The media time being presented now.</summary>
    public double Position
    {
        get
        {
            if (_seekPending) return _seekTarget;
            return MasterClock();
        }
    }

    private double MasterClock()
    {
        if (HasAudio && !_audioDead)
        {
            var c = _audioOut!.Clock;
            if (!double.IsNaN(c))
            {
                _audioWaitSince = 0;
                // Sound ran out before the picture (a short audio track): let the system
                // clock carry the video on from here.
                if (_audioEof && _audioOut.Starved && _audioOut.BufferedSeconds <= 0 && HasVideo)
                {
                    _audioDead = true;
                    _sysBase = c;
                    _sysStartTicks = _sysClock.ElapsedTicks;
                }
                return c;
            }
            // Audio has not started yet after an open or a seek: hold the picture at the
            // seek point; give up on audio as the clock if it never arrives.
            if (_playing)
            {
                var now = _sysClock.ElapsedMilliseconds;
                if (_audioWaitSince == 0) _audioWaitSince = now;
                else if (now - _audioWaitSince > 1500 && _audioEof == false && _aq.Count == 0)
                {
                    _audioDead = true;
                    _sysBase = _seekTarget;
                    _sysStartTicks = _sysClock.ElapsedTicks;
                }
            }
            return _seekTarget;
        }
        return SystemClock();
    }

    public void Seek(double seconds, bool exact = true)
    {
        if (_fmt == null) return;
        if (Duration > 0) seconds = Math.Clamp(seconds, 0, Math.Max(0, Duration - 0.25));
        else seconds = Math.Max(0, seconds);
        RequestSeek(seconds, exact);
        if (State == PlayerState.Ended) SetState(_playing ? PlayerState.Playing : PlayerState.Paused);
    }

    private void RequestSeek(double seconds, bool exact)
    {
        lock (_gate)
        {
            _serial++;
            _seekRequest = (seconds, exact, _serial);
            _seekTarget = seconds;
            _seekPending = true;
            _presentFirst = true;
            _exactTarget = exact ? seconds : double.NegativeInfinity;
            _sysBase = seconds;
            _sysStartTicks = _sysClock.ElapsedTicks;
            _audioWaitSince = 0;
            _videoEof = VideoStream < 0;
            _audioEof = !HasAudio;
            _audioDead = HasAudio && _audio is null;
        }
        if (_audioOut is not null) { _audioOut.Serial = _serial; _audioOut.Clear(); }
        lock (_frames) { foreach (var f in _frames) f.Dispose(); _frames.Clear(); Monitor.PulseAll(_frames); }
        _vq.Clear(); _aq.Clear();
    }

    public void SeekRelative(double delta) => Seek(Position + delta);

    // ---- trigger scrubbing: keyframe seeks while held, exact seek on release

    public void BeginScrub()
    {
        if (_fmt == null || _scrubbing) return;
        _scrubWasPlaying = _playing;
        Pause();
        _scrubbing = true;
    }

    /// <summary>Moves the scrub position. Fast keyframe seeks; if one is still in flight
    /// the newest request waits for it, so a held trigger never floods the demuxer.</summary>
    public void ScrubTo(double seconds)
    {
        if (!_scrubbing) return;
        if (_seekPending) { _queuedScrub = seconds; _seekTarget = Math.Clamp(seconds, 0, Duration > 0 ? Duration : seconds); return; }
        Seek(seconds, exact: false);
    }

    public void EndScrub(double seconds)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        _queuedScrub = null;
        Seek(seconds, exact: true);
        if (_scrubWasPlaying) Play();
    }

    public void SetSpeed(double speed)
    {
        speed = Math.Clamp(Math.Round(speed, 2), 0.25, 2.0);
        if (Math.Abs(speed - Speed) < 1e-6) return;
        var pos = Position;
        lock (_gate)
        {
            _sysBase = SystemClock();
            _sysStartTicks = _sysClock.ElapsedTicks;
            Speed = speed;
        }
        if (HasAudio) Seek(pos);   // rebuilds the tempo filter at the new speed
    }

    public void SetPitchCorrect(bool on)
    {
        _opts.PitchCorrect = on;
        if (HasAudio && Math.Abs(Speed - 1) > 1e-6) Seek(Position);
    }

    public void SelectAudio(int streamIndex)
    {
        if (_fmt == null || streamIndex == AudioStream) return;
        if (Tracks.All(t => t.StreamIndex != streamIndex || t.Kind != TrackKind.Audio)) return;
        var pos = Position;
        if (AudioStream >= 0) _fmt->streams[AudioStream]->discard = AVDiscard.AVDISCARD_ALL;
        _fmt->streams[streamIndex]->discard = AVDiscard.AVDISCARD_DEFAULT;
        AudioStream = streamIndex;
        Seek(pos);
        TracksChanged?.Invoke();
    }

    /// <summary>-1 turns subtitles off.</summary>
    public void SelectSubtitle(int streamIndex)
    {
        if (_path is null) return;
        SubtitleStream = -1;
        if (streamIndex < 0) { Subtitles.Clear(); TracksChanged?.Invoke(); return; }
        var t = Tracks.FirstOrDefault(x => x.StreamIndex == streamIndex && x.Kind == TrackKind.Subtitle);
        if (t is null) return;
        byte[]? header = null;
        if (_fmt != null)
        {
            var par = _fmt->streams[streamIndex]->codecpar;
            if (par->extradata != null && par->extradata_size > 0)
            {
                header = new byte[par->extradata_size];
                new ReadOnlySpan<byte>(par->extradata, par->extradata_size).CopyTo(header);
            }
        }
        Subtitles.BeginEmbedded(_path, streamIndex, t.Codec, t.IsBitmap, header);
        SubtitleStream = streamIndex;
        TracksChanged?.Invoke();
    }

    public bool LoadSubtitleFile(string path)
    {
        var ok = Subtitles.LoadFile(path);
        if (ok) SubtitleStream = -2;   // external
        TracksChanged?.Invoke();
        return ok;
    }

    // ------------------------------------------------------------------ presentation

    /// <summary>The frame to show now, or null to keep the current one. The caller owns
    /// the returned frame and disposes it after uploading.</summary>
    public VideoFrame? TakeFrame()
    {
        if (_fmt == null) return null;
        var clock = MasterClock();
        lock (_frames)
        {
            _frames.RemoveAll(f => { if (f.Serial == _serial) return false; f.Dispose(); return true; });
            if (_frames.Count == 0) { CheckEnded(); return null; }
            VideoFrame? pick = null;
            if (_presentFirst)
            {
                pick = _frames[0];
                _frames.RemoveAt(0);
                _presentFirst = false;
                if (!HasAudio || _audioDead) { _sysBase = Math.Max(_seekTarget, pick.Pts); _sysStartTicks = _sysClock.ElapsedTicks; }
                _seekPending = HasAudio && !_audioDead && double.IsNaN(_audioOut!.Clock) && _playing;
                if (!_seekPending) AfterSeekSettled();
            }
            else
            {
                if (!_playing) return null;
                while (_frames.Count > 0 && _frames[0].Pts <= clock + 0.004)
                {
                    pick?.Dispose();
                    pick = _frames[0];
                    _frames.RemoveAt(0);
                }
                if (pick is not null && _seekPending) { _seekPending = false; AfterSeekSettled(); }
            }
            Monitor.PulseAll(_frames);
            return pick;
        }
    }

    private void AfterSeekSettled()
    {
        _seekPending = false;
        if (_queuedScrub is { } next && _scrubbing)
        {
            _queuedScrub = null;
            Seek(next, exact: false);
        }
    }

    /// <summary>For audio-only files the UI polls this instead of frames.</summary>
    public void Tick()
    {
        if (_fmt == null) return;
        if (_seekPending && (!HasVideo) && HasAudio && !double.IsNaN(_audioOut!.Clock)) AfterSeekSettled();
        if (!HasVideo) CheckEnded();
    }

    private void CheckEnded()
    {
        if (State is not (PlayerState.Playing or PlayerState.Paused) || _scrubbing) return;
        bool videoDone = _videoEof;
        bool audioDone = _audioEof && (_audioOut is null || _audioOut.BufferedSeconds <= 0.01);
        if (!(videoDone && (audioDone || !HasAudio || _audioDead))) return;
        lock (_frames) if (_frames.Count > 0) return;
        _playing = false;
        _audioOut?.Pause();
        SetState(PlayerState.Ended);
    }

    // ------------------------------------------------------------------ threads

    private void DemuxLoop()
    {
        var pkt = ffmpeg.av_packet_alloc();
        try
        {
            while (!_stop)
            {
                (double Target, bool Exact, int Serial)? seek;
                lock (_gate) { seek = _seekRequest; _seekRequest = null; }
                if (seek is { } s)
                {
                    var ts = (long)(s.Target * ffmpeg.AV_TIME_BASE);
                    ffmpeg.av_seek_frame(_fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    _demuxSerial = s.Serial;
                    _demuxEof = false;
                    if (VideoStream >= 0) _vq.Put(new QueueItem(QueueItemKind.Flush, IntPtr.Zero, s.Serial));
                    if (HasAudio) _aq.Put(new QueueItem(QueueItemKind.Flush, IntPtr.Zero, s.Serial));
                }
                if (_demuxEof || (_vq.Full && VideoStream >= 0) || (_aq.Full && HasAudio))
                {
                    Thread.Sleep(5);
                    continue;
                }
                var rc = ffmpeg.av_read_frame(_fmt, pkt);
                if (rc < 0)
                {
                    if (rc == ffmpeg.AVERROR(ffmpeg.EAGAIN)) { Thread.Sleep(5); continue; }
                    _demuxEof = true;
                    if (VideoStream >= 0) _vq.Put(new QueueItem(QueueItemKind.Eof, IntPtr.Zero, _demuxSerial));
                    if (HasAudio) _aq.Put(new QueueItem(QueueItemKind.Eof, IntPtr.Zero, _demuxSerial));
                    continue;
                }
                if (pkt->stream_index == VideoStream || pkt->stream_index == AudioStream)
                {
                    var copy = ffmpeg.av_packet_alloc();
                    ffmpeg.av_packet_move_ref(copy, pkt);
                    var item = new QueueItem(QueueItemKind.Packet, (IntPtr)copy, _demuxSerial);
                    if (copy->stream_index == VideoStream) _vq.Put(item);
                    else if (HasAudio) _aq.Put(item);
                    else PacketQueue.Free(item);
                }
                else ffmpeg.av_packet_unref(pkt);
            }
        }
        finally { ffmpeg.av_packet_free(&pkt); }
    }

    private void VideoLoop()
    {
        var frame = ffmpeg.av_frame_alloc();
        var tb = _vstream->time_base;
        try
        {
            while (!_stop)
            {
                if (!_vq.TryTake(out var item, 50)) continue;
                if (item.Serial != _serial) { PacketQueue.Free(item); continue; }
                switch (item.Kind)
                {
                    case QueueItemKind.Flush:
                        ffmpeg.avcodec_flush_buffers(_vctx);
                        continue;
                    case QueueItemKind.Eof:
                        ffmpeg.avcodec_send_packet(_vctx, null);
                        Receive(frame, tb, item.Serial);
                        _videoEof = true;
                        ffmpeg.avcodec_flush_buffers(_vctx);
                        continue;
                }
                var p = (AVPacket*)item.Packet;
                ffmpeg.avcodec_send_packet(_vctx, p);
                PacketQueue.Free(item);
                Receive(frame, tb, item.Serial);
            }
        }
        finally { ffmpeg.av_frame_free(&frame); }
    }

    private void Receive(AVFrame* frame, AVRational tb, int serial)
    {
        while (!_stop && ffmpeg.avcodec_receive_frame(_vctx, frame) >= 0)
        {
            if (serial != _serial) { ffmpeg.av_frame_unref(frame); continue; }
            var ts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
            var pts = ts == ffmpeg.AV_NOPTS_VALUE ? 0 : Ff.ToSeconds(ts, tb);
            var frameDur = frame->duration > 0 ? Ff.ToSeconds(frame->duration, tb) : 1 / 30.0;
            if (pts + frameDur * 0.5 < _exactTarget) { ffmpeg.av_frame_unref(frame); continue; }
            var vf = _converter!.Convert(frame);
            ffmpeg.av_frame_unref(frame);
            if (vf is null) continue;
            vf.Pts = pts;
            vf.Serial = serial;
            lock (_frames)
            {
                while (_frames.Count >= MaxFrames && !_stop && serial == _serial) Monitor.Wait(_frames, 50);
                if (_stop || serial != _serial) { vf.Dispose(); continue; }
                _frames.Add(vf);
            }
        }
    }

    private void AudioLoop()
    {
        while (!_stop)
        {
            if (!_aq.TryTake(out var item, 50)) continue;
            if (item.Serial != _serial) { PacketQueue.Free(item); continue; }
            var pipeline = _audio;
            if (pipeline is null) { PacketQueue.Free(item); continue; }
            switch (item.Kind)
            {
                case QueueItemKind.Flush:
                    if (_audioPipelineStream != AudioStream && AudioStream >= 0)
                    {
                        pipeline.Dispose();
                        try
                        {
                            pipeline = _audio = new AudioPipeline(_fmt->streams[AudioStream], _audioOut!.SampleRate, _audioOut.Channels);
                            _audioPipelineStream = AudioStream;
                        }
                        catch (MediaException) { _audio = null; _audioDead = true; continue; }
                    }
                    pipeline.SetSpeed(Speed, _opts.PitchCorrect);
                    pipeline.Flush();
                    pipeline.TrimBefore = _exactTarget;
                    continue;
                case QueueItemKind.Eof:
                    Push(pipeline.Decode(null), item.Serial);
                    _audioEof = true;
                    continue;
            }
            var p = (AVPacket*)item.Packet;
            var chunks = pipeline.Decode(p);
            PacketQueue.Free(item);
            Push(chunks, item.Serial);
        }
    }

    private void Push(List<PcmChunk> chunks, int serial)
    {
        foreach (var c in chunks)
        {
            while (!_stop && serial == _serial && _audioOut!.BufferedSeconds > 0.35) Thread.Sleep(8);
            if (_stop || serial != _serial) return;
            _audioOut!.Push(c.Data, c.Frames, c.Pts, c.SecondsPerFrame, serial);
            if (_seekPending && !HasVideo) { /* audio-only: Tick() settles */ }
        }
    }

    public void Dispose()
    {
        Close();
        Subtitles.Dispose();
        _audioOut?.Dispose();
    }
}
