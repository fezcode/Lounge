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

    // The waiting screen theme: rendered once, looped, and laid over the ambient,
    // which ducks out while it plays.
    private float[]? _attract;
    private bool _attractRendering;
    private int _attractPos;
    private double _attractGain, _attractTarget;

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
            else if (theme == ThemeLoop) EnsureAttractRendered();
            else _synth = new AmbientSynth(AmbientSynth.Presets.ContainsKey(theme) ? theme : "aurora", _rng.Next());
        }
    }

    /// <summary>The theme whose ambient is the attract theme itself, looped.</summary>
    public const string ThemeLoop = "heatwave";
    private int _loopPos;

    /// <summary>Gets the attract theme ready ahead of time (waiting screen on, or Heatwave),
    /// so it starts the moment it is wanted.</summary>
    public void PrewarmAttract() { lock (_gate) EnsureAttractRendered(); }

    /// <summary>Loads the attract theme on a worker: from the copy kept on disk, or rendered
    /// (a few seconds) and then kept. Call under the lock.</summary>
    private void EnsureAttractRendered()
    {
        if (_attract is not null || _attractRendering || _out is null) return;
        _attractRendering = true;
        Task.Run(() =>
        {
            var data = LoadKept() ?? Keep(AttractTheme.Render());
            lock (_gate) { _attract = data; _attractPos = 0; _attractRendering = false; }
        });
    }

    private static string KeptPath => Path.Combine(Lounge.Core.AppPaths.Root, "cache", $"attract-theme-r{AttractTheme.Revision}.pcm");

    private static float[]? LoadKept()
    {
        try
        {
            if (!File.Exists(KeptPath)) return null;
            var bytes = File.ReadAllBytes(KeptPath);
            if (bytes.Length == 0 || bytes.Length % 8 != 0) return null;
            var data = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
            return data;
        }
        catch (Exception) { return null; }
    }

    private static float[] Keep(float[] data)
    {
        try
        {
            var dir = Path.GetDirectoryName(KeptPath)!;
            Directory.CreateDirectory(dir);
            // Older renders go: only the current one is ever read.
            foreach (var old in Directory.EnumerateFiles(dir, "attract-theme-r*.pcm"))
                if (old != KeptPath) File.Delete(old);
            var bytes = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(KeptPath + ".tmp", bytes);
            File.Move(KeptPath + ".tmp", KeptPath, overwrite: true);
        }
        catch (Exception) { }
        return data;
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
                // Heatwave's music is the theme itself: it starts at once.
                _framesUntilStart = immediate || _theme == ThemeLoop ? 0 : (long)(DelayMs / 1000.0 * AmbientSynth.SampleRate);
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

    /// <summary>Plays the attract theme (fading in over a second, the ambient ducking out
    /// under it) or stops it (400 ms). The first call renders it on a worker.</summary>
    public void SetAttract(bool on)
    {
        lock (_gate)
        {
            _attractTarget = on ? 1 : 0;
            if (on) EnsureAttractRendered();
        }
    }

    public bool AttractPlaying { get { lock (_gate) return _attractTarget > 0; } }

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
                    var g = (float)(Math.Sin(_ambientGain * Math.PI / 2) * AmbientVolume * (1 - _attractGain));
                    outSpan[i * 2] += amb[i * 2] * g;
                    outSpan[i * 2 + 1] += amb[i * 2 + 1] * g;
                }
            }

            if (_attract is { } theme && (_attractGain > 0 || _attractTarget > 0))
            {
                var up = 1.0 / AmbientSynth.SampleRate;
                var down = 1.0 / (0.4 * AmbientSynth.SampleRate);
                var level = Math.Max(AmbientVolume, 0.25f);
                for (int i = 0; i < frames; i++)
                {
                    if (_attractGain < _attractTarget) _attractGain = Math.Min(_attractTarget, _attractGain + up);
                    else if (_attractGain > _attractTarget) _attractGain = Math.Max(_attractTarget, _attractGain - down);
                    var g = (float)(Math.Sin(_attractGain * Math.PI / 2) * level);
                    outSpan[i * 2] += theme[_attractPos] * g;
                    outSpan[i * 2 + 1] += theme[_attractPos + 1] * g;
                    _attractPos += 2;
                    if (_attractPos >= theme.Length) _attractPos = 0;
                }
                if (_attractGain <= 0 && _attractTarget <= 0) _attractPos = 0;   // starts from the top next time
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
        else if (_theme == ThemeLoop)
        {
            // Heatwave: the attract theme, looped (silent for the moment it takes to render).
            if (_attract is not { } loop) { span.Clear(); EnsureAttractRendered(); return; }
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = loop[_loopPos++];
                if (_loopPos >= loop.Length) _loopPos = 0;
            }
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
