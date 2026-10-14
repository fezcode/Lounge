using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Lounge.Media;

/// <summary>WASAPI shared-mode output that is also the playback clock, as in tivi: audio
/// is the master and video follows it. Chunks carry the media time of their first
/// sample and how much media time each output frame covers (speed / rate), so the
/// clock stays right at any playback speed.</summary>
public sealed class AudioOutput : IWaveProvider, IDisposable
{
    private sealed class Chunk
    {
        public required float[] Data;
        public required int Frames;
        public int Offset;            // frames already played
        public required double Pts;
        public required double SecondsPerFrame;
        public required int Serial;
    }

    private readonly Queue<Chunk> _chunks = new();
    private readonly object _gate = new();
    private readonly WasapiOut _out;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly double _latency;

    private double _clockPts = double.NaN;     // media time at the end of the last read
    private double _clockSpf;
    private long _clockAtTicks;
    private int _clockSerial = -1;
    private double _lastReported = double.NaN;
    private bool _paused = true;
    private double _bufferedSeconds;
    private float _volume = 1, _currentGain = 1;

    public WaveFormat WaveFormat { get; }
    public int SampleRate => WaveFormat.SampleRate;
    public int Channels => WaveFormat.Channels;
    /// <summary>Set by the player: chunks of an older serial are dropped (after a seek).</summary>
    public int Serial { get; set; }
    /// <summary>True once the queue ran dry while playing (end of file or a stall).</summary>
    public bool Starved { get; private set; }

    public AudioOutput(int latencyMs = 80)
    {
        int rate = 48000, channels = 2;
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mix = dev.AudioClient.MixFormat;
            rate = mix.SampleRate;
            // The pipeline speaks mono, stereo, 5.1 and 7.1; anything else gets stereo
            // and Windows maps it.
            channels = mix.Channels is 1 or 2 or 6 or 8 ? mix.Channels : 2;
        }
        catch (Exception) { /* no device: keep defaults, Init below reports it */ }
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        _out = new WasapiOut(AudioClientShareMode.Shared, true, latencyMs);
        _out.Init(this);
        _latency = latencyMs / 1000.0;
    }

    public float Volume { get => _volume; set => _volume = Math.Clamp(value, 0f, 2f); }

    public double BufferedSeconds { get { lock (_gate) return _bufferedSeconds; } }

    public void Push(float[] data, int frames, double pts, double secondsPerFrame, int serial)
    {
        lock (_gate)
        {
            if (serial != Serial) return;
            _chunks.Enqueue(new Chunk { Data = data, Frames = frames, Pts = pts, SecondsPerFrame = secondsPerFrame, Serial = serial });
            _bufferedSeconds += frames / (double)SampleRate;
            Starved = false;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _chunks.Clear();
            _bufferedSeconds = 0;
            _clockPts = double.NaN;
            _lastReported = double.NaN;
        }
    }

    public void Play()
    {
        lock (_gate) { _paused = false; _clockAtTicks = _sw.ElapsedTicks; }
        if (_out.PlaybackState != PlaybackState.Playing) _out.Play();
    }

    public void Pause()
    {
        lock (_gate)
        {
            _lastReported = ClockUnlocked();
            _paused = true;
        }
        _out.Pause();
    }

    /// <summary>The media time being heard now, or NaN before the first chunk plays.</summary>
    public double Clock { get { lock (_gate) return ClockUnlocked(); } }

    private double ClockUnlocked()
    {
        if (double.IsNaN(_clockPts) || _clockSerial != Serial) return double.NaN;
        if (_paused) return double.IsNaN(_lastReported) ? _clockPts - _latency * _clockSpf * SampleRate : _lastReported;
        var since = (_sw.ElapsedTicks - _clockAtTicks) / (double)Stopwatch.Frequency;
        var spf = _clockSpf * SampleRate;               // media seconds per wall second
        var t = _clockPts - _latency * spf + Math.Min(since, _latency) * spf;
        if (!double.IsNaN(_lastReported) && t < _lastReported && _lastReported - t < 0.25) t = _lastReported;
        _lastReported = t;
        return t;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var frames = count / (4 * Channels);
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, frames * 4 * Channels));
        span.Clear();
        int written = 0;
        lock (_gate)
        {
            while (written < frames && _chunks.Count > 0)
            {
                var c = _chunks.Peek();
                if (c.Serial != Serial) { _chunks.Dequeue(); _bufferedSeconds -= (c.Frames - c.Offset) / (double)SampleRate; continue; }
                var n = Math.Min(frames - written, c.Frames - c.Offset);
                c.Data.AsSpan(c.Offset * Channels, n * Channels).CopyTo(span.Slice(written * Channels));
                c.Offset += n;
                written += n;
                _bufferedSeconds -= n / (double)SampleRate;
                _clockPts = c.Pts + c.Offset * c.SecondsPerFrame;
                _clockSpf = c.SecondsPerFrame;
                _clockSerial = c.Serial;
                if (c.Offset >= c.Frames) _chunks.Dequeue();
            }
            _clockAtTicks = _sw.ElapsedTicks;
            if (written < frames && !_paused) Starved = true;
            if (_bufferedSeconds < 0) _bufferedSeconds = 0;
        }

        // Ramp gain changes over the buffer so volume steps never click.
        var target = _volume;
        var start = _currentGain;
        var total = written * Channels;
        for (int i = 0; i < total; i++)
        {
            var g = start + (target - start) * (i / (float)Math.Max(total, 1));
            span[i] *= g;
        }
        _currentGain = target;
        return count;
    }

    public void Dispose()
    {
        try { _out.Stop(); } catch (Exception) { }
        _out.Dispose();
    }
}
