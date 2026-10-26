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
    double PulseHz = 0,       // slow amplitude breathing, 0 = none
    bool Chip = false)        // chiptune voices: pulse waves instead of soft sines
{
    // An arrangement on a beat, over the pad. All off unless Tempo is set.

    /// <summary>Beats per minute; 0 leaves the music free-floating (pad and falling notes only).</summary>
    public double Tempo { get; init; }
    /// <summary>Steps per beat for the arpeggio and the drum (2 = eighths, 4 = sixteenths).</summary>
    public int Division { get; init; } = 2;
    /// <summary>Chord-tone indices per step (counting past the top wraps up an octave); -1 rests.</summary>
    public int[]? Arp { get; init; }
    public int ArpOctave { get; init; } = 12;
    public double ArpLevel { get; init; }
    public Timbre ArpTimbre { get; init; } = Timbre.Felt;
    /// <summary>The chord's root, low: struck every <see cref="BassBeats"/> beats, or held through the chord at 0.</summary>
    public double BassLevel { get; init; }
    public int BassBeats { get; init; }
    public Timbre BassTimbre { get; init; } = Timbre.Pluck;
    /// <summary>Composed phrases as (MIDI note, beats) pairs, a note of 0 resting. One starts
    /// at every second chord change, in turn, and now and then the music leaves a gap.</summary>
    public (int Note, double Beats)[][]? Phrases { get; init; }
    public double MelodyLevel { get; init; }
    public Timbre MelodyTimbre { get; init; } = Timbre.Felt;
    /// <summary>One character per step: 'X' a full drum stroke, 'x' a soft one, anything else rests.</summary>
    public string? Drums { get; init; }
    public double DrumLevel { get; init; }
}

/// <summary>How a struck note sounds. Felt: a soft piano. Marimba: a wooden bar, quick and
/// round. Glass: a bell-like glass with inharmonic partials and a long ring. Horn: a slow
/// swelling reed-and-brass voice with vibrato, held for the note's length. Pluck: a warm
/// plucked string for bass.</summary>
public enum Timbre { Felt, Marimba, Glass, Horn, Pluck }

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
        // Late-night noir: dark minor-ninth voicings, a lone piano in a velvet room.
        ["screening"] = new("screening", "Screening Room",
            [[38, 45, 53, 57, 60, 64], [43, 50, 53, 57, 59, 64], [36, 43, 52, 55, 59, 62], [45, 52, 55, 58, 61]],
            11.0, 0.11, 0.25, 6, 0.3, 0.13, 3.2, [62, 64, 65, 67, 69, 71, 72, 74, 77], 0.35, 0.62),
        // An arcade attract loop: pulse-wave chords on the old Am-F-C-G turn, quick chip
        // plucks from the pentatonic, a small dry room.
        ["arcade"] = new("arcade", "Arcade",
            [[45, 57, 60, 64], [41, 53, 57, 60], [48, 55, 60, 64], [43, 55, 59, 62]],
            4.0, 0.06, 0.5, 8, 2.6, 0.07, 0.22, [69, 72, 74, 76, 79, 81, 84, 86], 0.5, 0.22, Chip: true),
        // A parlour music box: a quiet string bed turning I-vi-ii-V in E-flat with a slow
        // waltz swell, and quick, bright, short-ringing tines picked from high in the key.
        ["carplay"] = new("carplay", "Motorcar Recital",
            [[39, 51, 55, 58, 63], [36, 48, 55, 60, 63], [41, 53, 56, 60, 65], [34, 46, 53, 58, 62]],
            5.0, 0.07, 0.3, 4, 2.2, 0.11, 0.9, [79, 82, 84, 86, 87, 89, 91, 94, 96, 98], 0.85, 0.36, 0.6),
        // A bright morning at 84 bpm in D: a marimba rippling through each chord in eighths,
        // a plucked bass on the half bar, and now and then a little piano tune.
        ["daylight"] = new("daylight", "Daylight",
            [[50, 57, 62, 64, 69], [47, 54, 59, 61, 66], [43, 50, 55, 57, 62], [45, 52, 57, 59, 64]],
            8 * 60 / 84.0, 0.1, 0.5, 6, 0.15, 0.1, 0.8, [74, 76, 78, 81, 83, 86, 88, 90], 0.55, 0.42)
        {
            Tempo = 84, Division = 2,
            Arp = [0, 2, 4, 2, 1, 3, 5, 3], ArpOctave = 12, ArpLevel = 0.2, ArpTimbre = Timbre.Marimba,
            BassLevel = 0.32, BassBeats = 2, BassTimbre = Timbre.Pluck,
            MelodyLevel = 0.34, MelodyTimbre = Timbre.Felt,
            Phrases =
            [
                [(74, 1), (76, 1), (78, 2), (81, 2), (78, 1), (76, 1), (74, 4), (0, 4)],
                [(78, 1), (81, 1), (83, 2), (81, 1), (78, 1), (76, 2), (78, 4), (0, 4)],
                [(86, 2), (83, 1), (81, 1), (78, 2), (76, 2), (74, 4), (0, 4)],
            ],
        },
        // Under glass at 66 bpm, Lydian in F: glass bells chiming sparsely in sixteenths high
        // above, a deep bass held under each chord, a slow glass melody with a raised fourth.
        ["liquid"] = new("liquid", "Liquid Glass",
            [[41, 48, 55, 59, 64], [43, 50, 57, 62, 66], [38, 45, 52, 57, 64], [40, 47, 55, 59, 62]],
            8 * 60 / 66.0, 0.09, 0.7, 8, 0.12, 0.1, 5.5, [77, 79, 81, 83, 84, 88, 89, 91, 93], 0.6, 0.72, 0.07)
        {
            Tempo = 66, Division = 4,
            Arp = [0, -1, -1, 2, -1, -1, 4, -1, 3, -1, -1, 6, -1, -1, -1, -1], ArpOctave = 24, ArpLevel = 0.15, ArpTimbre = Timbre.Glass,
            BassLevel = 0.17, BassBeats = 0, BassTimbre = Timbre.Pluck,
            MelodyLevel = 0.3, MelodyTimbre = Timbre.Glass,
            Phrases =
            [
                [(77, 3), (79, 1), (81, 4), (84, 4), (83, 4)],
                [(88, 2), (86, 2), (84, 4), (81, 4), (0, 4)],
                [(76, 2), (79, 2), (83, 4), (81, 6), (0, 2)],
            ],
        },
        // A cold hall at 60 bpm: open fifths in D minor, a frame drum beating like a slow
        // heart, a low string held under each chord, and a horn singing old Dorian tunes
        // far off in a stone room.
        ["saga"] = new("saga", "Saga",
            [[38, 45, 50, 57], [34, 41, 46, 53], [36, 43, 48, 55], [33, 40, 45, 52]],
            8.0, 0.11, 0.22, 5, 0.06, 0.1, 4.5, [62, 64, 65, 67, 69, 71, 72, 74], 0.3, 0.75, 0.05)
        {
            Tempo = 60, Division = 2,
            Drums = "X..x....X..x....", DrumLevel = 0.32,
            BassLevel = 0.14, BassBeats = 0, BassTimbre = Timbre.Pluck,
            MelodyLevel = 0.3, MelodyTimbre = Timbre.Horn,
            Phrases =
            [
                [(62, 1), (65, 1), (67, 2), (69, 3), (72, 1), (69, 2), (67, 2), (65, 4)],
                [(69, 1), (72, 1), (74, 2), (72, 2), (69, 2), (67, 2), (69, 6)],
                [(74, 2), (72, 1), (69, 1), (67, 2), (65, 2), (64, 2), (62, 6)],
            ],
        },
    };

    private readonly AmbientPreset _p;
    private readonly Random _rng;
    private readonly Reverb _reverbL, _reverbR;
    private readonly List<Voice> _notes = new();
    private readonly PadVoice[] _pad;

    private long _t;                 // samples generated
    private int _chord = -1;
    private double _nextNote;
    private long _step = -1;         // arrangement steps played
    private int _phraseTurn;
    private readonly Queue<(double At, int Note, double Hold)> _melody = new();
    private readonly List<Drum> _drums = new();

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
        var stepLen = _p.Tempo > 0 ? 60.0 / _p.Tempo / Math.Max(1, _p.Division) * SampleRate : 0;
        for (int i = 0; i + 1 < buffer.Length; i += 2, _t++)
        {
            var seconds = _t / (double)SampleRate;
            var chord = (int)(_t / chordLen % _p.Chords.Length);
            if (chord != _chord)
            {
                _chord = chord;
                Retune(_p.Chords[chord]);
                if (_p.Tempo > 0) StartPhrase(seconds);
            }
            if (stepLen > 0 && (long)(_t / stepLen) != _step)
            {
                _step = (long)(_t / stepLen);
                Step(_step);
            }
            while (_melody.Count > 0 && _melody.Peek().At <= seconds)
            {
                var (_, note, hold) = _melody.Dequeue();
                _notes.Add(new Voice(Hz(note), _p.NoteDecay * 1.4, _p.NoteBrightness, (0.8 + 0.2 * _rng.NextDouble()) * _p.MelodyLevel, 0.42 + 0.16 * _rng.NextDouble(), timbre: _p.MelodyTimbre, hold: hold, scaled: true));
            }

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
                var s = v.Next(SampleRate) * (v.Scaled ? 1 : _p.NoteLevel);
                l += s * v.Pan; r += s * (1 - v.Pan);
                if (v.Done) _notes.RemoveAt(n);
            }
            for (int n = _drums.Count - 1; n >= 0; n--)
            {
                var s = _drums[n].Next(SampleRate, _rng) * _p.DrumLevel;
                l += s; r += s;
                if (_drums[n].Done) _drums.RemoveAt(n);
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

    /// <summary>One step of the arrangement: the arpeggio's note, the bass on its beats, the drum.</summary>
    private void Step(long step)
    {
        var chord = _p.Chords[Math.Max(_chord, 0)];
        var perBeat = Math.Max(1, _p.Division);
        if (_p.Arp is { Length: > 0 } arp && _p.ArpLevel > 0 && arp[step % arp.Length] is var idx and >= 0)
        {
            var note = chord[idx % chord.Length] + _p.ArpOctave + 12 * (idx / chord.Length);
            // Alternate sides, and lean on the first of each beat.
            var accent = step % perBeat == 0 ? 1.0 : 0.72;
            _notes.Add(new Voice(Hz(note), _p.ArpTimbre == Timbre.Glass ? 1.6 : 0.9, _p.NoteBrightness, _p.ArpLevel * accent * (0.85 + 0.15 * _rng.NextDouble()),
                step % 2 == 0 ? 0.3 : 0.7, timbre: _p.ArpTimbre, scaled: true));
        }
        if (_p.BassLevel > 0)
        {
            var root = chord[0] >= 43 ? chord[0] - 12 : chord[0];
            var beats = _p.BassBeats;
            var chordSteps = (long)Math.Round(_p.ChordSeconds * _p.Tempo / 60 * perBeat);
            if (beats > 0 ? step % (beats * perBeat) == 0 : chordSteps > 0 && step % chordSteps == 0)
                _notes.Add(new Voice(Hz(root), beats > 0 ? 1.4 : _p.ChordSeconds * 0.5, 0.3, _p.BassLevel, 0.5, timbre: _p.BassTimbre,
                    hold: beats > 0 ? 0 : _p.ChordSeconds * 0.9, scaled: true));
        }
        if (_p.Drums is { Length: > 0 } drums && _p.DrumLevel > 0)
        {
            var c = drums[(int)(step % drums.Length)];
            if (c is 'X' or 'x') _drums.Add(new Drum(c == 'X' ? 1.0 : 0.5));
        }
        if (_notes.Count > 40) _notes.RemoveAt(0);
    }

    /// <summary>At every second chord change, queues the next composed phrase (sometimes
    /// leaving the space empty instead, so the tune never wears out).</summary>
    private void StartPhrase(double now)
    {
        if (_p.Phrases is not { Length: > 0 } phrases || _p.MelodyLevel <= 0 || _chord % 2 != 0) return;
        if (_phraseTurn > 0 && _rng.NextDouble() < 0.25) { _phraseTurn++; return; }
        var phrase = phrases[_phraseTurn++ % phrases.Length];
        var beat = 60.0 / _p.Tempo;
        var at = now;
        foreach (var (note, beats) in phrase)
        {
            if (note > 0) _melody.Enqueue((at, note, beats * beat * 0.92));
            at += beats * beat;
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
        _notes.Add(new Voice(Hz(note), _p.NoteDecay * (0.7 + 0.6 * _rng.NextDouble()), _p.NoteBrightness, velocity, 0.2 + 0.6 * _rng.NextDouble(), _p.Chip));
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
            // Chip: two detuned pulse waves at 25% duty, the classic console square lead.
            if (p.Chip) return ((_ph1 < 0.25 ? 1 : -0.33) + (_ph2 < 0.25 ? 1 : -0.33)) * 0.18 * _amp;
            var s = Math.Sin(2 * Math.PI * _ph1) + Math.Sin(2 * Math.PI * _ph2)
                  + b * 0.5 * Math.Sin(4 * Math.PI * _ph1) + b * 0.35 * Math.Sin(2 * Math.PI * _ph3)
                  + b * b * 0.2 * Math.Sin(6 * Math.PI * _ph2);
            return s * 0.25 * _amp;
        }
    }

    /// <summary>A struck note: a few inharmonic-ish partials with exponential decay, like
    /// a felt piano or a soft bell depending on brightness, or one of the other timbres.</summary>
    private sealed class Voice
    {
        private readonly double _f, _decay, _bright, _vel, _hold;
        private readonly bool _chip;
        private readonly Timbre _timbre;
        private double _t, _ph1, _ph2, _ph3, _ph4, _ph5, _ph6;
        public readonly double Pan;
        /// <summary>Its level is already set by the arrangement (not the falling notes' level).</summary>
        public readonly bool Scaled;
        public bool Done => _t > _hold + _decay * 6;

        public Voice(double f, double decay, double bright, double vel, double pan, bool chip = false, Timbre timbre = Timbre.Felt, double hold = 0, bool scaled = false)
        {
            _f = f; _decay = decay; _bright = bright; _vel = vel; Pan = pan; _chip = chip; _timbre = timbre; _hold = hold; Scaled = scaled;
        }

        private static double Wrap(double ph) => ph - Math.Floor(ph);

        public double Next(int sr)
        {
            _t += 1.0 / sr;
            if (_timbre == Timbre.Horn) return Horn(sr);
            var attack = Math.Min(1, _t / 0.006);
            var env = attack * Math.Exp(-_t / _decay);
            switch (_timbre)
            {
                case Timbre.Marimba:
                {
                    // A wooden bar: the fundamental, its 4th partial (a bar's, not a string's)
                    // that dies fast, and a click of the 10th.
                    var e = Math.Min(1, _t / 0.002) * Math.Exp(-_t / (_decay * 0.55));
                    _ph1 = Wrap(_ph1 + _f / sr); _ph2 = Wrap(_ph2 + _f * 3.93 / sr); _ph3 = Wrap(_ph3 + _f * 9.9 / sr);
                    return (Math.Sin(2 * Math.PI * _ph1) + 0.35 * Math.Exp(-_t / 0.08) * Math.Sin(2 * Math.PI * _ph2)
                            + 0.12 * Math.Exp(-_t / 0.015) * Math.Sin(2 * Math.PI * _ph3)) * e * _vel * 0.45;
                }
                case Timbre.Glass:
                {
                    // Glass: partials at 1, 2.76 and 5.4, the upper ones beating slowly, a long ring.
                    var e = Math.Min(1, _t / 0.004) * Math.Exp(-_t / (_decay * 1.6));
                    _ph1 = Wrap(_ph1 + _f / sr); _ph2 = Wrap(_ph2 + _f * 2.756 / sr); _ph3 = Wrap(_ph3 + _f * 5.404 / sr); _ph4 = Wrap(_ph4 + _f * 2.762 / sr);
                    return (Math.Sin(2 * Math.PI * _ph1) + 0.3 * Math.Exp(-_t / (_decay * 0.6)) * (Math.Sin(2 * Math.PI * _ph2) + Math.Sin(2 * Math.PI * _ph4)) * 0.5
                            + 0.12 * Math.Exp(-_t / (_decay * 0.25)) * Math.Sin(2 * Math.PI * _ph3)) * e * _vel * 0.42;
                }
                case Timbre.Pluck:
                {
                    // A warm low string: the fundamental and a soft second harmonic that fades first.
                    var e = Math.Min(1, _t / 0.01) * (_hold > 0
                        ? Math.Min(1, Math.Max(0, 1 - (_t - _hold) / 0.6))
                        : Math.Exp(-_t / _decay));
                    _ph1 = Wrap(_ph1 + _f / sr); _ph2 = Wrap(_ph2 + _f * 2 / sr);
                    return (Math.Sin(2 * Math.PI * _ph1) + 0.3 * Math.Exp(-_t / 0.4) * Math.Sin(2 * Math.PI * _ph2)) * e * _vel * 0.6;
                }
            }
            _ph1 = Wrap(_ph1 + _f / sr); _ph2 = Wrap(_ph2 + _f * 2.0 / sr); _ph3 = Wrap(_ph3 + _f * 3.01 / sr);
            // Chip: a 50% square pluck, an octave pulse on top while it's fresh.
            if (_chip) return ((_ph1 < 0.5 ? 1 : -1) * 0.6 + _bright * (_ph2 < 0.25 ? 0.3 : -0.1) * Math.Exp(-_t / 0.05)) * env * _vel * 0.32;
            var upper = Math.Exp(-_t / (_decay * 0.25));
            var s = Math.Sin(2 * Math.PI * _ph1)
                  + _bright * 0.45 * upper * Math.Sin(2 * Math.PI * _ph2)
                  + _bright * 0.2 * upper * Math.Sin(2 * Math.PI * _ph3);
            return s * env * _vel * 0.5;
        }

        /// <summary>A horn-like voice: swells in over a quarter second, brightens as it grows,
        /// holds with a slow vibrato for the note's length, then fades.</summary>
        private double Horn(int sr)
        {
            var swell = 1 - Math.Exp(-_t / 0.12);
            var release = _t <= _hold ? 1 : Math.Exp(-(_t - _hold) / 0.35);
            var env = swell * release;
            var vib = 1 + 0.004 * Math.Sin(2 * Math.PI * 5.2 * _t) * Math.Min(1, Math.Max(0, (_t - 0.3) / 0.4));
            var f = _f * vib;
            _ph1 = Wrap(_ph1 + f / sr); _ph2 = Wrap(_ph2 + f * 2 / sr); _ph3 = Wrap(_ph3 + f * 3 / sr);
            _ph4 = Wrap(_ph4 + f * 4 / sr); _ph5 = Wrap(_ph5 + f * 5 / sr); _ph6 = Wrap(_ph6 + f * 1.003 / sr);
            var b = 0.35 + 0.65 * env;   // brighter as it swells, like breath into brass
            var s = Math.Sin(2 * Math.PI * _ph1) + 0.6 * Math.Sin(2 * Math.PI * _ph6)
                  + b * 0.5 * Math.Sin(2 * Math.PI * _ph2) + b * b * 0.3 * Math.Sin(2 * Math.PI * _ph3)
                  + b * b * 0.16 * Math.Sin(2 * Math.PI * _ph4) + b * b * b * 0.08 * Math.Sin(2 * Math.PI * _ph5);
            return s * env * _vel * 0.32;
        }
    }

    /// <summary>A frame drum: a low skin falling in pitch with a breath of noise on the strike.</summary>
    private sealed class Drum(double velocity)
    {
        private double _t, _ph;
        public bool Done => _t > 1.2;

        public double Next(int sr, Random rng)
        {
            _t += 1.0 / sr;
            var f = 52 + 70 * Math.Exp(-_t / 0.045);
            _ph += f / sr;
            _ph -= Math.Floor(_ph);
            var body = Math.Sin(2 * Math.PI * _ph) * Math.Exp(-_t / 0.32);
            var noise = (rng.NextDouble() * 2 - 1) * Math.Exp(-_t / 0.02) * 0.25;
            return (body + noise) * Math.Min(1, _t / 0.002) * velocity * 0.7;
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
