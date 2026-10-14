namespace Lounge.Core.Audio;

public enum UiSound { Move, Select, Back, Toggle, Error, Home, Open, Notify }

/// <summary>Short interface sounds, synthesised per theme so each theme has its own
/// voice and nothing has to be licensed. Mono 48 kHz float.</summary>
public static class UiSounds
{
    public const int SampleRate = AmbientSynth.SampleRate;

    private sealed record Voicing(double Base, double Brightness, double Length);

    private static readonly Dictionary<string, Voicing> ByTheme = new()
    {
        ["aurora"] = new(1.0, 0.5, 1.0),
        ["livingroom"] = new(0.84, 0.7, 1.1),
        ["kodi"] = new(0.75, 0.3, 1.2),
        ["console"] = new(1.12, 0.6, 1.4),
        ["series"] = new(1.26, 0.8, 0.9),
    };

    public static float[] Render(UiSound sound, string theme)
    {
        var v = ByTheme.TryGetValue(theme, out var x) ? x : ByTheme["aurora"];
        var b = v.Base;
        return sound switch
        {
            Move => Tone([(1318 * b, 0)], 0.035 * v.Length, 0.22, v.Brightness),
            Select => Tone([(880 * b, 0), (1318 * b, 0.045)], 0.16 * v.Length, 0.32, v.Brightness),
            Back => Tone([(988 * b, 0), (659 * b, 0.045)], 0.14 * v.Length, 0.28, v.Brightness),
            Toggle => Tone([(1175 * b, 0)], 0.07 * v.Length, 0.26, v.Brightness),
            Error => Tone([(220 * b, 0), (207 * b, 0.07)], 0.22, 0.30, 0.15),
            Home => Tone([(523 * b, 0), (659 * b, 0.05), (784 * b, 0.1), (1047 * b, 0.15)], 0.6 * v.Length, 0.28, v.Brightness),
            Open => Tone([(659 * b, 0), (988 * b, 0.06)], 0.22 * v.Length, 0.28, v.Brightness),
            Notify => Tone([(1047 * b, 0), (1568 * b, 0.09)], 0.4 * v.Length, 0.30, v.Brightness),
            _ => [],
        };

        static float[] Tone((double Hz, double At)[] notes, double length, double level, double bright)
        {
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
    }

    private const UiSound Move = UiSound.Move, Select = UiSound.Select, Back = UiSound.Back, Toggle = UiSound.Toggle,
        Error = UiSound.Error, Home = UiSound.Home, Open = UiSound.Open, Notify = UiSound.Notify;
}
