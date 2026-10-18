namespace Lounge.Core.Audio;

public enum UiSound { Move, Select, Back, Toggle, Error, Home, Open, Notify }

/// <summary>Short interface sounds, synthesised per theme so each theme has its own
/// voice and nothing has to be licensed. Mono 48 kHz float.</summary>
public static class UiSounds
{
    public const int SampleRate = AmbientSynth.SampleRate;

    /// <summary><paramref name="Chip"/>: pulse-wave blips that hold and stop short, like a console menu.</summary>
    private sealed record Voicing(double Base, double Brightness, double Length, bool Chip = false);

    private static readonly Dictionary<string, Voicing> ByTheme = new()
    {
        ["aurora"] = new(1.0, 0.5, 1.0),
        ["livingroom"] = new(0.84, 0.7, 1.1),
        ["kodi"] = new(0.75, 0.3, 1.2),
        ["console"] = new(1.12, 0.6, 1.4),
        ["series"] = new(1.26, 0.8, 0.9),
        ["screening"] = new(0.66, 0.18, 0.75),   // low, soft and short: a projector's click
        ["arcade"] = new(1.0, 0.5, 0.8, Chip: true),
    };

    public static float[] Render(UiSound sound, string theme)
    {
        var v = ByTheme.TryGetValue(theme, out var x) ? x : ByTheme["aurora"];
        var b = v.Base;
        var chip = v.Chip;
        return sound switch
        {
            Move => Tone([(1318 * b, 0)], 0.035 * v.Length, 0.22, v.Brightness, chip),
            Select => Tone([(880 * b, 0), (1318 * b, 0.045)], 0.16 * v.Length, 0.32, v.Brightness, chip),
            Back => Tone([(988 * b, 0), (659 * b, 0.045)], 0.14 * v.Length, 0.28, v.Brightness, chip),
            Toggle => Tone([(1175 * b, 0)], 0.07 * v.Length, 0.26, v.Brightness, chip),
            Error => Tone([(220 * b, 0), (207 * b, 0.07)], 0.22, 0.30, 0.15, chip),
            Home => Tone([(523 * b, 0), (659 * b, 0.05), (784 * b, 0.1), (1047 * b, 0.15)], 0.6 * v.Length, 0.28, v.Brightness, chip),
            Open => Tone([(659 * b, 0), (988 * b, 0.06)], 0.22 * v.Length, 0.28, v.Brightness, chip),
            Notify => Tone([(1047 * b, 0), (1568 * b, 0.09)], 0.4 * v.Length, 0.30, v.Brightness, chip),
            _ => [],
        };

        static float[] Tone((double Hz, double At)[] notes, double length, double level, double bright, bool chip = false)
        {
            if (chip) return Chip(notes, length, level);
            var total = length + notes.Max(n => n.At);
            var data = new float[(int)(total * SampleRate) + 1];
            foreach (var (hz, at) in notes)
            {
                var start = (int)(at * SampleRate);
                for (int i = start; i < data.Length; i++)
                {
                    var t = (i - start) / (double)SampleRate;
                    var env = Math.Min(1, t / 0.003) * Math.Exp(-t / (length * 0.35));
                    var s = Math.Sin(2 * Math.PI * hz * t) + bright * 0.3 * Math.Sin(4 * Math.PI * hz * t) * Math.Exp(-t / 0.02);
                    data[i] += (float)(s * env * level / notes.Length * 1.6);
                }
            }
            return data;
        }

        // Each note is a 25% pulse that holds until the next one, then the last stops short:
        // no ringing tail, just the blip (a few ms of fade only to keep it click-free).
        static float[] Chip((double Hz, double At)[] notes, double length, double level)
        {
            var total = notes.Max(n => n.At) + Math.Min(length, 0.09);
            var data = new float[(int)(total * SampleRate) + 1];
            for (int n = 0; n < notes.Length; n++)
            {
                var (hz, at) = notes[n];
                var start = (int)(at * SampleRate);
                var end = n + 1 < notes.Length ? (int)(notes[n + 1].At * SampleRate) : data.Length;
                for (int i = start; i < end; i++)
                {
                    var t = (i - start) / (double)SampleRate;
                    var left = (end - i) / (double)SampleRate;
                    var env = Math.Min(1, t / 0.002) * Math.Min(1, left / 0.004) * (1 - 0.35 * t / total);
                    var phase = hz * t - Math.Floor(hz * t);
                    data[i] = (float)((phase < 0.25 ? 1 : -0.33) * env * level * 0.55);
                }
            }
            return data;
        }
    }

    private const UiSound Move = UiSound.Move, Select = UiSound.Select, Back = UiSound.Back, Toggle = UiSound.Toggle,
        Error = UiSound.Error, Home = UiSound.Home, Open = UiSound.Open, Notify = UiSound.Notify;
}
