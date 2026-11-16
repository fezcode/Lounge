namespace Lounge.Core.Audio;

/// <summary>The waiting screen's theme: a tense orchestral piece in D harmonic
/// minor at 120 bpm, built only from strings sawing a sixteenth-note figure, a low string
/// pad, a choir, timpani and a soft kick on the beat. It grows by section: a second string
/// line an octave up, the choir swelling, timpani rolls into each new section, then it
/// thins back to the opening so the loop closes on itself. Rendered once into memory (40
/// bars, 80 seconds); the reverb tail of the last bar is folded into the start so the seam
/// is seamless. Deterministic for a seed.</summary>
public static class AttractTheme
{
    public const int SampleRate = AmbientSynth.SampleRate;
    public const double Bpm = 120;
    public static double Beat => 60 / Bpm;
    public static double BarSeconds => 4 * Beat;
    public static int Bars => Form.Sum(f => f.Chords.Length);
    /// <summary>Bump whenever the music changes: names the rendered copy kept on disk.</summary>
    public const int Revision = 4;

    // Close voicings around middle C; the A major chord carries the harmonic minor's C#.
    private static readonly Dictionary<string, int[]> Chord = new()
    {
        ["Dm"] = [50, 53, 57, 62],
        ["Gm"] = [50, 55, 58, 62],
        ["Bb"] = [50, 53, 58, 62],
        ["A"] = [49, 52, 57, 61],
        ["Eb"] = [51, 55, 58, 63],
        ["F"] = [48, 53, 57, 60],
    };

    /// <summary>One section: its chords (a bar each), how full it is, and which figure the strings saw.</summary>
    private sealed record Section(string[] Chords, double Choir, bool HighStrings, bool Kick, int[] Figure, double Timpani);

    // The strings' sixteenth figures, as indices into the chord (4 and up go an octave higher).
    private static readonly int[] Saw = [0, 2, 4, 2, 1, 2, 4, 2, 0, 2, 4, 2, 1, 3, 5, 3];
    private static readonly int[] Climb = [0, 2, 4, 2, 1, 3, 5, 3, 2, 4, 6, 4, 3, 5, 7, 5];
    private static readonly int[] Pulse = [0, 4, 2, 4, 1, 4, 2, 4, 0, 4, 2, 4, 1, 5, 3, 5];

    private static readonly Section[] Form =
    [
        // The opening, as it is.
        new(["Dm", "Dm", "Bb", "A", "Dm", "Dm", "Bb", "A"], 0.6, false, false, Saw, 0.9),
        // The beat comes in; the strings move to the subdominant.
        new(["Gm", "Gm", "Dm", "Dm", "Bb", "Bb", "A", "A"], 0.7, false, true, Saw, 0.8),
        // A second line an octave up, climbing.
        new(["Dm", "Bb", "Gm", "A", "Dm", "Bb", "Eb", "A"], 0.85, true, true, Climb, 0.9),
        // The peak: everything, the choir full.
        new(["Bb", "F", "Gm", "Dm", "Bb", "F", "Eb", "A"], 1.0, true, true, Pulse, 1.0),
        // Back down to the opening, so the loop closes.
        new(["Dm", "Dm", "Bb", "A", "Dm", "Dm", "Bb", "A"], 0.6, false, true, Saw, 0.8),
    ];

    public static float[] Render(int seed = 1)
    {
        var rng = new Random(seed);
        int n = (int)Math.Round(Bars * BarSeconds * SampleRate);
        int tail = (int)(2 * BarSeconds * SampleRate);
        var dry = new Bus(n + tail);
        var wet = new Bus(n + tail);   // reverb send

        int bar = 0;
        for (int si = 0; si < Form.Length; si++)
        {
            var sec = Form[si];
            var last = si == Form.Length - 1;
            for (int i = 0; i < sec.Chords.Length; i++, bar++)
            {
                double t0 = bar * BarSeconds;
                var voicing = Chord[sec.Chords[i]];
                bool phraseEnd = i == sec.Chords.Length - 1;
                // The last section fades the beat out over its second half, back to the opening's texture.
                double thin = last && i >= 4 ? 1 - (i - 3) / 5.0 : 1;

                Pad(dry, wet, t0, BarSeconds, voicing, 0.8);
                Choir(dry, wet, t0, BarSeconds, voicing, sec.Choir);

                for (int s = 0; s < 16; s++)
                {
                    var k = sec.Figure[s];
                    var note = voicing[k % 4] + 12 * (k / 4);
                    double accent = (s % 4 == 0 ? 1 : 0.72) * (0.97 + rng.NextDouble() * 0.06);
                    double ts = t0 + s * Beat / 4;
                    Spiccato(dry, wet, ts, note, 0.5 * accent, 0.4);
                    if (sec.HighStrings) Spiccato(dry, wet, ts, note + 12, 0.32 * accent, -0.4);
                }

                if (sec.Kick && thin > 0)
                    for (int q = 0; q < 4; q++) Kick(dry, t0 + q * Beat, (q % 2 == 0 ? 0.85 : 0.65) * thin);

                // Timpani: a hit on each section's first bar and halfway, and in the fuller
                // sections on the third beat; a roll into the next section.
                if (i == 0 || i == 4) Timpani(dry, wet, t0, sec.Timpani, 38);
                if (sec.HighStrings && i % 2 == 1) Timpani(dry, wet, t0 + 2 * Beat, sec.Timpani * 0.6, 33);
                if (phraseEnd && !last)
                    for (int r = 0; r < 8; r++) Timpani(dry, wet, t0 + 2 * Beat + r * Beat / 4, 0.2 + r * 0.07, r < 4 ? 33 : 38);
            }
        }

        var room = Reverb(wet);
        var outBuf = new float[n * 2];
        for (int i = 0; i < n + tail; i++)
        {
            int j = i % n;   // the tail wraps onto the start, so the loop is seamless
            outBuf[j * 2] += dry.L[i] + room.L[i] * 0.8f;
            outBuf[j * 2 + 1] += dry.R[i] + room.R[i] * 0.8f;
        }
        // Level from the loud part, then a soft clip for the odd peak.
        // (A histogram, not a sort: a sort of 7.7 million samples took seconds.)
        var hist = new int[4096];
        float max = 1e-6f;
        foreach (var v in outBuf) max = Math.Max(max, Math.Abs(v));
        foreach (var v in outBuf) hist[Math.Min(4095, (int)(Math.Abs(v) / max * 4095))]++;
        long want = (long)(outBuf.Length * 0.995), seen = 0;
        int bin = 0;
        while (bin < 4095 && (seen += hist[bin]) < want) bin++;
        var loud = Math.Max(1e-6f, (bin + 1) / 4096f * max);
        var g = 0.95f / loud;
        for (int i = 0; i < outBuf.Length; i++) outBuf[i] = (float)Math.Tanh(outBuf[i] * g) * 0.9f;
        return outBuf;
    }

    // ------------------------------------------------------------------ voices

    private sealed class Bus(int n)
    {
        public readonly float[] L = new float[n], R = new float[n];
        public void Add(int i, double v, double pan)
        {
            if ((uint)i >= (uint)L.Length) return;
            L[i] += (float)(v * Math.Sqrt(0.5 * (1 - pan)));
            R[i] += (float)(v * Math.Sqrt(0.5 * (1 + pan)));
        }
    }

    private static double Hz(double midi) => 440 * Math.Pow(2, (midi - 69) / 12);
    private static int S(double t) => (int)Math.Round(t * SampleRate);
    private static double Lp(double fc) => 1 - Math.Exp(-2 * Math.PI * Math.Min(fc, SampleRate * 0.45) / SampleRate);
    private const double Dt = 1.0 / SampleRate;

    /// <summary>Strings bowed short: one sixteenth of the sawing figure.</summary>
    private static void Spiccato(Bus dry, Bus wet, double start, int midi, double gain, double pan)
    {
        double f = Hz(midi), p1 = 0, p2 = 0.5, lp = 0, lp2 = 0;
        int s0 = S(start), len = S(0.2);
        for (int i = 0; i < len; i++)
        {
            double t = i * Dt;
            double env = Math.Min(1, t / 0.004) * Math.Exp(-t / 0.06);
            p1 = (p1 + f * 1.004 * Dt) % 1; p2 = (p2 + f * 0.996 * Dt) % 1;
            double x = (2 * p1 - 1) + (2 * p2 - 1);
            double a = Lp(800 + 2600 * Math.Exp(-t / 0.03));
            lp += a * (x - lp); lp2 += a * (lp - lp2);
            double v = lp2 * env * gain * 0.2;
            dry.Add(s0 + i, v, pan);
            wet.Add(s0 + i, v * 0.4, pan);
        }
    }

    /// <summary>A low string pad under everything.</summary>
    private static void Pad(Bus dry, Bus wet, double start, double dur, int[] chord, double gain)
    {
        foreach (var (note, k) in chord.Select((m, k) => (m - 12, k)))
        {
            double f = Hz(note);
            var ph = new double[] { 0.1 * k, 0.4, 0.75 };
            var det = new[] { 1.0, 1.005, 0.995 };
            double lp = 0, lp2 = 0, a = Lp(700);
            int s0 = S(start), len = S(dur + 0.3);
            double pan = k % 2 == 0 ? -0.5 : 0.5;
            for (int i = 0; i < len; i++)
            {
                double t = i * Dt;
                double env = Math.Min(1, t / 0.15) * (t < dur ? 1 : Math.Max(0, 1 - (t - dur) / 0.3));
                double x = 0;
                for (int v = 0; v < 3; v++) { ph[v] = (ph[v] + f * det[v] * Dt) % 1; x += 2 * ph[v] - 1; }
                lp += a * (x - lp); lp2 += a * (lp - lp2);
                double o = lp2 * env * gain * 0.04;
                dry.Add(s0 + i, o, pan);
                wet.Add(s0 + i, o * 0.4, pan);
            }
        }
    }

    /// <summary>An "aah" choir: saws through two vowel formants, a swell, vibrato.</summary>
    private static void Choir(Bus dry, Bus wet, double start, double dur, int[] chord, double gain)
    {
        foreach (var (note, k) in chord.Select((m, k) => (m + 12, k)))
        {
            double f = Hz(note), ph = 0.2 * k;
            var f1 = new Biquad(720, 5); var f2 = new Biquad(1150, 6);
            int s0 = S(start), len = S(dur + 0.5);
            for (int i = 0; i < len; i++)
            {
                double t = i * Dt;
                double env = Math.Min(1, t / 0.3) * (t < dur ? 1 : Math.Max(0, 1 - (t - dur) / 0.5));
                ph = (ph + f * (1 + 0.005 * Math.Sin(2 * Math.PI * (5 + k * 0.3) * t)) * Dt) % 1;
                double x = 2 * ph - 1;
                double o = (f1.Run(x) + 0.7 * f2.Run(x)) * env * gain * 0.045;
                dry.Add(s0 + i, o * 0.6, (k - 1.5) * 0.35);
                wet.Add(s0 + i, o, (k - 1.5) * 0.35);
            }
        }
    }

    private static void Kick(Bus dry, double start, double gain)
    {
        double ph = 0;
        int s0 = S(start), len = S(0.35);
        for (int i = 0; i < len; i++)
        {
            double t = i * Dt;
            double f = 48 + 110 * Math.Exp(-t / 0.03);
            ph += f * Dt;
            double v = Math.Sin(2 * Math.PI * ph) * Math.Exp(-t / 0.16) + (t < 0.003 ? 0.5 * (1 - t / 0.003) : 0);
            dry.Add(s0 + i, Math.Tanh(v * 1.8) * gain * 0.62, 0);
        }
    }

    private static void Timpani(Bus dry, Bus wet, double start, double gain, int midi)
    {
        double ph = 0, ph2 = 0;
        int s0 = S(start), len = S(1.5);
        var rng = new Random(s0 ^ midi);
        for (int i = 0; i < len; i++)
        {
            double t = i * Dt;
            double f = Hz(midi) * (1 + 0.04 * Math.Exp(-t / 0.08));
            ph += f * Dt; ph2 += f * 1.5 * Dt;
            double v = (Math.Sin(2 * Math.PI * ph) + 0.35 * Math.Sin(2 * Math.PI * ph2) * Math.Exp(-t / 0.3)) * Math.Exp(-t / 0.6)
                       + (rng.NextDouble() * 2 - 1) * Math.Exp(-t / 0.015) * 0.3;
            dry.Add(s0 + i, v * gain * 0.5, -0.1);
            wet.Add(s0 + i, v * gain * 0.3, -0.1);
        }
    }

    // ------------------------------------------------------------------ effects

    private sealed class Biquad
    {
        private readonly double _b0, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;
        public Biquad(double fc, double q)
        {
            double w = 2 * Math.PI * fc / SampleRate, al = Math.Sin(w) / (2 * q), a0 = 1 + al;
            _b0 = al / a0; _b2 = -al / a0; _a1 = -2 * Math.Cos(w) / a0; _a2 = (1 - al) / a0;
        }
        public double Run(double x)
        {
            double y = _b0 * x + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
    }

    /// <summary>A Schroeder room: four damped combs and two allpasses per side.</summary>
    private static Bus Reverb(Bus send)
    {
        int n = send.L.Length;
        var o = new Bus(n);
        Side(send.L, o.L, [1487, 1601, 1687, 1777], [389, 149]);
        Side(send.R, o.R, [1523, 1637, 1723, 1811], [401, 157]);
        return o;

        static void Side(float[] x, float[] y, int[] combs, int[] alls)
        {
            int n = x.Length;
            var acc = new double[n];
            foreach (var d in combs)
            {
                var buf = new double[d]; int k = 0; double damp = 0;
                for (int i = 0; i < n; i++)
                {
                    double outv = buf[k];
                    damp = outv * 0.55 + damp * 0.45;
                    buf[k] = x[i] + damp * 0.8;
                    acc[i] += outv;
                    if (++k == d) k = 0;
                }
            }
            foreach (var d in alls)
            {
                var buf = new double[d]; int k = 0;
                for (int i = 0; i < n; i++)
                {
                    double b = buf[k], v = acc[i];
                    buf[k] = v + b * 0.5;
                    acc[i] = -v * 0.5 + b;
                    if (++k == d) k = 0;
                }
            }
            for (int i = 0; i < n; i++) y[i] = (float)(acc[i] * 0.2);
        }
    }
}
