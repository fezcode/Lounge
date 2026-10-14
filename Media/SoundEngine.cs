using Lounge.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Lounge.Media;

/// <summary>Lounge's own sound: the ambient bed (procedural per theme, or a folder of the
/// user's music, shuffled) and the interface sounds, mixed into one WASAPI stream
/// separate from the player. Ambient fades in after a delay when the shell is idle and
/// fades out quickly whenever something else plays.</summary>
public sealed class SoundEngine : IWaveProvider, IDisposable
{
    private readonly WasapiOut? _out;
    private readonly object _gate = new();
    private readonly List<(float[] Data, int Pos, float Gain)> _oneShots = new();
    private readonly Dictionary<(UiSound, string), float[]> _soundCache = new();
    private readonly Random _rng = new();

    private AmbientSynth? _synth;
    private AudioFileReader? _file;
    private List<string> _folderTracks = new();
    private int _folderIndex;
    private string _theme = "aurora";
    private string? _folder;
    private bool _useFolder;
    private float[] _scratch = new float[4096];

    private double _ambientGain;          // current fade gain 0..1
    private double _ambientTarget;        // where the fade is heading
    private double _fadeStep;             // gain per frame
    private long _framesUntilStart = -1;  // delay before fading in

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AmbientSynth.SampleRate, 2);

    public float AmbientVolume { get; set; } = 0.35f;
    public float SoundsVolume { get; set; } = 0.5f;
    public bool SoundsEnabled { get; set; } = true;
    public int FadeMs { get; set; } = 1200;
    public int DelayMs { get; set; } = 2000;
    public bool AmbientWanted { get; private set; }

    public SoundEngine()
    {
        try
        {
            _out = new WasapiOut(AudioClientShareMode.Shared, true, 60);
            _out.Init(this);
            _out.Play();
        }
        catch (Exception) { _out = null; }
    }

    public bool Available => _out is not null;

    /// <summary>Chooses what ambient plays: the theme's synth, or a folder of music.</summary>
    public void Configure(string theme, bool useFolder, string? folder)
    {
        lock (_gate)
        {
            var changed = theme != _theme || useFolder != _useFolder || folder != _folder;
            _theme = theme;
            _useFolder = useFolder && !string.IsNullOrEmpty(folder) && Directory.Exists(folder);
            _folder = folder;
            if (!changed && (_synth is not null || _file is not null)) return;
            _synth = null;
            _file?.Dispose(); _file = null;
            if (_useFolder)
            {
                _folderTracks = Directory.EnumerateFiles(folder!, "*", SearchOption.AllDirectories)
                    .Where(p => Lounge.Core.Media.MediaKinds.Of(p) == Lounge.Core.Media.MediaKind.Audio)
                    .OrderBy(_ => _rng.Next()).ToList();
                _folderIndex = 0;
            }
            else _synth = new AmbientSynth(AmbientSynth.Presets.ContainsKey(theme) ? theme : "aurora", _rng.Next());
        }
    }

    /// <summary>Wants ambient on (shell idle) or off (something else is playing).
    /// On waits <see cref="DelayMs"/> then fades in; off fades out fast.</summary>
    public void SetAmbient(bool on, bool immediate = false)
    {
        lock (_gate)
        {
            AmbientWanted = on;
            if (on)
            {
                if (_ambientTarget >= 1 && _framesUntilStart < 0) return;
                _framesUntilStart = immediate ? 0 : (long)(DelayMs / 1000.0 * AmbientSynth.SampleRate);
                _fadeStep = 1.0 / Math.Max(1, FadeMs / 1000.0 * AmbientSynth.SampleRate);
            }
            else
            {
                _framesUntilStart = -1;
                _ambientTarget = 0;
                // Leaving is quicker than arriving: 400 ms at most, so it never talks over a film.
                _fadeStep = 1.0 / Math.Max(1, Math.Min(FadeMs, 400) / 1000.0 * AmbientSynth.SampleRate);
            }
        }
    }

    public void Play(UiSound sound)
    {
        if (!SoundsEnabled || _out is null || SoundsVolume <= 0) return;
        float[] data;
        lock (_soundCache)
        {
            if (!_soundCache.TryGetValue((sound, _theme), out data!))
                _soundCache[(sound, _theme)] = data = UiSounds.Render(sound, _theme);
        }
        lock (_gate)
        {
            if (sound == UiSound.Move) _oneShots.RemoveAll(s => s.Data.Length < 4000);   // rapid moves don't pile up
            _oneShots.Add((data, 0, SoundsVolume));
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var frames = count / 8;
        var outSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, frames * 8));
        outSpan.Clear();
        lock (_gate)
        {
            if (_scratch.Length < frames * 2) _scratch = new float[frames * 2];
            var amb = _scratch.AsSpan(0, frames * 2);
            var needAmbient = _ambientGain > 0 || _ambientTarget > 0 || _framesUntilStart >= 0;
            if (needAmbient)
            {
                FillAmbient(amb);
                for (int i = 0; i < frames; i++)
                {
                    if (_framesUntilStart > 0) _framesUntilStart--;
                    else if (_framesUntilStart == 0) { _ambientTarget = 1; _framesUntilStart = -1; }
                    if (_ambientGain < _ambientTarget) _ambientGain = Math.Min(_ambientTarget, _ambientGain + _fadeStep);
                    else if (_ambientGain > _ambientTarget) _ambientGain = Math.Max(_ambientTarget, _ambientGain - _fadeStep);
                    // An equal-power curve sounds even across the fade.
                    var g = (float)(Math.Sin(_ambientGain * Math.PI / 2) * AmbientVolume);
                    outSpan[i * 2] += amb[i * 2] * g;
                    outSpan[i * 2 + 1] += amb[i * 2 + 1] * g;
                }
            }

            for (int s = _oneShots.Count - 1; s >= 0; s--)
            {
                var (data, pos, gain) = _oneShots[s];
                int n = Math.Min(frames, data.Length - pos);
                for (int i = 0; i < n; i++)
                {
                    var v = data[pos + i] * gain;
                    outSpan[i * 2] += v;
                    outSpan[i * 2 + 1] += v;
                }
                pos += n;
                if (pos >= data.Length) _oneShots.RemoveAt(s); else _oneShots[s] = (data, pos, gain);
            }
        }
        for (int i = 0; i < outSpan.Length; i++) outSpan[i] = Math.Clamp(outSpan[i], -1f, 1f);
        return count;
    }

    private void FillAmbient(Span<float> span)
    {
        if (_useFolder)
        {
            int done = 0;
            for (int guard = 0; done < span.Length && guard < 4; guard++)
            {
                if (_file is null || _file.Ended)
                {
                    _file?.Dispose();
                    _file = null;
                    if (_folderTracks.Count == 0) break;
                    _file = AudioFileReader.Open(_folderTracks[_folderIndex++ % _folderTracks.Count], AmbientSynth.SampleRate, 2);
                    if (_file is null) continue;
                }
                done += _file.Read(span[done..]);
            }
            span[done..].Clear();
        }
        else (_synth ??= new AmbientSynth(_theme)).Read(span);
    }

    public void Dispose()
    {
        try { _out?.Stop(); } catch (Exception) { }
        _out?.Dispose();
        _file?.Dispose();
    }
}
