using Lounge.Core.Audio;

namespace Lounge.Core.Attract;

/// <summary>When the waiting screen (Settings, General, Waiting screen) may start.</summary>
public static class AttractRules
{
    /// <param name="enabled">The Waiting screen setting.</param>
    /// <param name="idleMs">Time since the last input.</param>
    /// <param name="afterMs">How long Lounge must sit untouched first (Waiting screen after).</param>
    /// <param name="active">Lounge is in charge and in front (no app launched from it).</param>
    /// <param name="menu">A menu is showing: not a film, a web page or a game.</param>
    /// <param name="dimmed">The idle dim is up; it wins.</param>
    public static bool CanStart(bool enabled, long idleMs, long afterMs, bool active, bool menu, bool dimmed) =>
        enabled && active && menu && !dimmed && idleMs >= afterMs;
}

/// <summary>Position and size of one picture layer. Scale 1 fills the screen; X and Y move
/// the layer by that fraction of the screen's width and height.</summary>
public readonly record struct AttractLayer(double Scale, double X, double Y);

/// <summary>How the attract loop moves, as pure functions of time so previews and tests can
/// ask for any moment. Each scene lasts four bars of the 120 bpm theme, so cuts land on a
/// bar line. The background slowly pushes in and drifts; the foreground starts larger and
/// pushed toward the middle, settles to its side, then creeps on, so the two layers part
/// like a camera move.</summary>
public static class AttractChoreo
{
    public const double Bpm = AttractTheme.Bpm;
    public static double BarSeconds => 4 * 60 / Bpm;
    public static double SceneSeconds => 4 * BarSeconds;

    /// <summary>Which scene shows at this time since the loop began, and how far into it (0..1).</summary>
    public static (int Scene, double T) At(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var n = (int)Math.Floor(seconds / SceneSeconds);
        return (n, seconds / SceneSeconds - n);
    }

    public static AttractLayer Background(double t, int scene)
    {
        t = Math.Clamp(t, 0, 1);
        // The drift only ever uses the margin the zoom has made, so no edge shows.
        var dir = scene % 2 == 0 ? 1 : -1;
        var scale = 1 + 0.12 * Ease(t);
        var margin = (scale - 1) / 2;
        return new(scale, dir * margin * 0.8, -margin * 0.3);
    }

    /// <param name="left">The figure stands on the left of the frame.</param>
    public static AttractLayer Foreground(double t, bool left)
    {
        t = Math.Clamp(t, 0, 1);
        var settle = 1 - Math.Pow(1 - t, 3);          // fast at first, then gentle
        var toCentre = left ? 1 : -1;
        // Y stays 0: the figure stands on the bottom edge and scales from there.
        return new(1.10 - 0.10 * settle + 0.035 * t, toCentre * 0.035 * (1 - settle), 0);
    }

    /// <summary>Opacity of the black between scenes: 1 at a cut, 0 for most of the scene.</summary>
    public static double Black(double t)
    {
        var s = Math.Clamp(t, 0, 1) * SceneSeconds;
        const double fadeIn = 0.35, fadeOut = 0.22;
        var a = s < fadeIn ? 1 - s / fadeIn : 0;
        var b = SceneSeconds - s < fadeOut ? 1 - (SceneSeconds - s) / fadeOut : 0;
        return Math.Clamp(Math.Max(a, b), 0, 1);
    }

    private static double Ease(double t) => t * t * (3 - 2 * t) * 0.35 + t * 0.65;
}
