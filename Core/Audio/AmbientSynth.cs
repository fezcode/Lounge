namespace Lounge.Core.Audio;

/// <summary>A preset for <see cref="AmbientSynth"/>: chord progression, pad colour, and
/// how often bell or piano notes fall over it.</summary>
public sealed record AmbientPreset(
    string Id,
    string Name,
    int[][] Chords,           // MIDI notes per chord
    double ChordSeconds,      // how long each chord holds
    double PadLevel,
    double PadBrightness,     // 0..1: how many upper partials the pad keeps
    double Detune,            // cents of chorus between the pad's two oscillators
    double NoteRate,          // melody notes per second, on average
    double NoteLevel,
    double NoteDecay,         // seconds
    int[] NoteScale,          // MIDI notes the melody picks from
    double NoteBrightness,
    double Reverb,            // wet mix 0..1
    double PulseHz = 0);      // slow amplitude breathing, 0 = none

/// <summary>Procedural ambient music, so every theme has its own original score and
/// nothing has to be licensed or shipped. Produces 48 kHz stereo float samples forever.
/// Deterministic for a given seed, which the tests rely on.</summary>
public sealed class AmbientSynth
{
    public const int SampleRate = 48000;

    public static readonly IReadOnlyDictionary<string, AmbientPreset> Presets = new Dictionary<string, AmbientPreset>
    {
        // Glassy, slow major-seventh pads with high bells: calm and bright.
        ["aurora"] = new("aurora", "Aurora",
            [[48, 55, 64, 71], [45, 52, 60, 67, 71], [41, 48, 57, 64], [43, 50, 59, 62, 69]],
            9.0, 0.16, 0.55, 7, 0.22, 0.10, 3.5, [72, 74, 76, 79, 81, 83, 84, 86, 88], 0.35, 0.55),
        // Warm electric-piano feel over a soft bed, like a smart TV's home screen.
        ["livingroom"] = new("livingroom", "Living Room",
            [[50, 57, 62, 66, 69], [47, 54, 59, 62, 66], [43, 50, 55, 59, 62], [45, 52, 57, 61, 64]],
            7.0, 0.13, 0.4, 5, 0.6, 0.12, 2.2, [62, 64, 66, 69, 71, 74, 76, 78], 0.55, 0.40),
        // Low minor drone with distant chimes: classic media-centre dusk.
        ["kodi"] = new("kodi", "Kodi Classic",
            [[38, 45, 50, 53, 57], [36, 43, 48, 51, 55], [41, 48, 53, 56, 60], [34, 41, 46, 50, 53]],
            12.0, 0.17, 0.3, 9, 0.14, 0.09, 5.0, [69, 72, 74, 76, 77, 81, 84], 0.25, 0.65),
        // Sparse, reflective piano notes in a big room: a console home screen at night.
        ["console"] = new("console", "Console One",
            [[40, 47, 52, 56, 59], [37, 44, 49, 52, 56], [45, 52, 57, 61, 64], [42, 49, 54, 57, 61]],
            10.0, 0.09, 0.35, 4, 0.45, 0.17, 4.5, [64, 66, 68, 71, 73, 76, 78, 80], 0.6, 0.70),
        // Airy, gently pulsing pad with crystalline plucks: bright and modern.
        ["series"] = new("series", "Series",
            [[45, 52, 57, 64, 69], [41, 48, 53, 60, 65], [43, 50, 55, 62, 67], [40, 47, 52, 59, 64]],
            8.0, 0.14, 0.6, 10, 0.5, 0.09, 1.6, [69, 71, 74, 76, 79, 81, 83], 0.7, 0.50, 0.11),
    };

    private readonly AmbientPreset _p;
    private readonly Random _rng;
    private readonly Reverb _reverbL, _reverbR;
    private readonly List<Voice> _notes = new();
    private readonly PadVoice[] _pad;

    private long _t;                 // samples generated
    private int _chord = -1;
    private double _nextNote;

    public AmbientSynth(string presetId, int seed = 1)
    {
        _p = Presets.TryGetValue(presetId, out var p) ? p : Presets["aurora"];
        _rng = new Random(seed);
        _reverbL = new Reverb(0);
        _reverbR = new Reverb(23);
        _pad = Enumerable.Range(0, 6).Select(_ => new PadVoice()).ToArray();
        _nextNote = 1.5;
    }

    public AmbientPreset Preset => _p;

    private static double Hz(int midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

    /// <summary>Fills <paramref name="buffer"/> with interleaved stereo samples.</summary>
    public void Read(Span<float> buffer)
    {
        var chordLen = (long)(_p.ChordSeconds * SampleRate);
        for (int i = 0; i + 1 < buffer.Length; i += 2, _t++)
        {
            var seconds = _t / (double)SampleRate;
            var chord = (int)(_t / chordLen % _p.Chords.Length);
            if (chord != _chord) { _chord = chord; Retune(_p.Chords[chord]); }

            if (seconds >= _nextNote)
            {
                SpawnNote();
                // Exponential gaps sound like someone playing rather than a metronome.
                var gap = -Math.Log(1 - _rng.NextDouble()) / Math.Max(_p.NoteRate, 0.01);
                _nextNote = seconds + Math.Clamp(gap, 0.18, 8);
            }

            double l = 0, r = 0;
            foreach (var v in _pad)
            {
                var s = v.Next(_p, SampleRate);
                l += s * v.Pan; r += s * (1 - v.Pan);
            }
            var pulse = _p.PulseHz > 0 ? 0.8 + 0.2 * Math.Sin(2 * Math.PI * _p.PulseHz * seconds) : 1.0;
            l *= _p.PadLevel * pulse; r *= _p.PadLevel * pulse;

            for (int n = _notes.Count - 1; n >= 0; n--)
            {
                var v = _notes[n];
                var s = v.Next(SampleRate) * _p.NoteLevel;
                l += s * v.Pan; r += s * (1 - v.Pan);
                if (v.Done) _notes.RemoveAt(n);
            }

            var wetL = _reverbL.Process(l);
            var wetR = _reverbR.Process(r);
            var outL = l * (1 - _p.Reverb * 0.6) + wetL * _p.Reverb;
            var outR = r * (1 - _p.Reverb * 0.6) + wetR * _p.Reverb;
            buffer[i] = (float)Math.Tanh(outL * 1.2) * 0.8f;
            buffer[i + 1] = (float)Math.Tanh(outR * 1.2) * 0.8f;
        }
    }

    private void Retune(int[] notes)
    {
        for (int i = 0; i < _pad.Length; i++)
        {
            if (i < notes.Length) _pad[i].Glide(Hz(notes[i]), 0.15 + 0.7 * (i / (double)_pad.Length), _p.Detune);
            else _pad[i].Glide(0, 0.5, _p.Detune);
        }
    }

    private void SpawnNote()
    {
        var note = _p.NoteScale[_rng.Next(_p.NoteScale.Length)];
        // Keep melody notes inside the current chord's colour most of the time.
        var chord = _p.Chords[Math.Max(_chord, 0)];
        if (_rng.NextDouble() < 0.55)
        {
            var pc = chord[_rng.Next(chord.Length)] % 12;
            note = _p.NoteScale.Where(n => n % 12 == pc).DefaultIfEmpty(note).OrderBy(_ => _rng.Next()).First();
        }
        var velocity = 0.55 + 0.45 * _rng.NextDouble();
        _notes.Add(new Voice(Hz(note), _p.NoteDecay * (0.7 + 0.6 * _rng.NextDouble()), _p.NoteBrightness, velocity, 0.2 + 0.6 * _rng.NextDouble()));
        if (_notes.Count > 24) _notes.RemoveAt(0);
    }

    /// <summary>A sustained pad partial bank that glides between chord tones.</summary>
    private sealed class PadVoice
    {
        private double _freq, _target, _amp, _targetAmp, _ph1, _ph2, _ph3, _lfo;
        private double _detune;
        public double Pan = 0.5;

        public void Glide(double freq, double pan, double detuneCents)
        {
            _target = freq;
            _targetAmp = freq > 0 ? 1 : 0;
            if (_freq <= 0) _freq = freq;
            Pan = pan;
            _detune = detuneCents;
        }

        public double Next(AmbientPreset p, int sr)
        {
            if (_target > 0) _freq += (_target - _freq) * 0.00005;
            _amp += (_targetAmp - _amp) * 0.00003;   // very slow swell, about 0.7 s
            if (_amp < 1e-5 || _freq <= 0) return 0;
            _lfo += 0.13 / sr;
            var spread = Math.Pow(2, _detune * (1 + 0.3 * Math.Sin(2 * Math.PI * _lfo)) / 1200.0);
            _ph1 += _freq / sr; _ph2 += _freq * spread / sr; _ph3 += _freq * 2 / sr;
            _ph1 -= Math.Floor(_ph1); _ph2 -= Math.Floor(_ph2); _ph3 -= Math.Floor(_ph3);
            var b = p.PadBrightness;
            var s = Math.Sin(2 * Math.PI * _ph1) + Math.Sin(2 * Math.PI * _ph2)
                  + b * 0.5 * Math.Sin(4 * Math.PI * _ph1) + b * 0.35 * Math.Sin(2 * Math.PI * _ph3)
                  + b * b * 0.2 * Math.Sin(6 * Math.PI * _ph2);
            return s * 0.25 * _amp;
        }
    }

    /// <summary>A struck note: a few inharmonic-ish partials with exponential decay, like
    /// a felt piano or a soft bell depending on brightness.</summary>
    private sealed class Voice
    {
        private readonly double _f, _decay, _bright, _vel;
        private double _t, _ph1, _ph2, _ph3;
        public readonly double Pan;
        public bool Done => _t > _decay * 6;

        public Voice(double f, double decay, double bright, double vel, double pan)
        {
            _f = f; _decay = decay; _bright = bright; _vel = vel; Pan = pan;
        }

        public double Next(int sr)
        {
            _t += 1.0 / sr;
            var attack = Math.Min(1, _t / 0.006);
            var env = attack * Math.Exp(-_t / _decay);
            _ph1 += _f / sr; _ph2 += _f * 2.0 / sr; _ph3 += _f * 3.01 / sr;
            _ph1 -= Math.Floor(_ph1); _ph2 -= Math.Floor(_ph2); _ph3 -= Math.Floor(_ph3);
            var upper = Math.Exp(-_t / (_decay * 0.25));
            var s = Math.Sin(2 * Math.PI * _ph1)
                  + _bright * 0.45 * upper * Math.Sin(2 * Math.PI * _ph2)
                  + _bright * 0.2 * upper * Math.Sin(2 * Math.PI * _ph3);
            return s * env * _vel * 0.5;
        }
    }

    /// <summary>A small Schroeder reverb: four combs into two allpasses.</summary>
    private sealed class Reverb
    {
        private readonly Comb[] _combs;
        private readonly AllPass[] _aps;

        public Reverb(int spread)
        {
            _combs = [new(1557 + spread), new(1617 + spread), new(1491 + spread), new(1422 + spread),
                      new(2277 + spread), new(2557 + spread)];
            _aps = [new(225 + spread), new(556 + spread), new(441 + spread)];
        }

        public double Process(double x)
        {
            double s = 0;
            foreach (var c in _combs) s += c.Process(x * 0.15);
            foreach (var a in _aps) s = a.Process(s);
            return s;
        }

        private sealed class Comb(int size)
        {
            private readonly double[] _buf = new double[size];
            private int _i;
            private double _lp;
            public double Process(double x)
            {
                var y = _buf[_i];
                _lp = y * 0.6 + _lp * 0.4;            // damping: darker tails
                _buf[_i] = x + _lp * 0.86;
                if (++_i >= _buf.Length) _i = 0;
                return y;
            }
        }

        private sealed class AllPass(int size)
        {
            private readonly double[] _buf = new double[size];
            private int _i;
            public double Process(double x)
            {
                var b = _buf[_i];
                var y = -x + b;
                _buf[_i] = x + b * 0.5;
                if (++_i >= _buf.Length) _i = 0;
                return y;
            }
        }
    }
}
