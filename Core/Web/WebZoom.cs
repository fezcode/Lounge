namespace Lounge.Core.Web;

/// <summary>Page zoom steps, as browsers have them: zooming in or out moves to the next
/// level rather than by a fixed 10%, so 100% to 200% is five presses, not ten.</summary>
public static class WebZoom
{
    public static readonly double[] Levels = [0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    /// <summary>The next level up (+1) or down (-1) from any zoom, even one between levels.</summary>
    public static double Step(double current, int direction)
    {
        if (direction > 0)
        {
            foreach (var l in Levels) if (l > current + 0.001) return l;
            return Levels[^1];
        }
        for (var i = Levels.Length - 1; i >= 0; i--) if (Levels[i] < current - 0.001) return Levels[i];
        return Levels[0];
    }

    public static string Percent(double zoom) => $"{Math.Round(zoom * 100)}%";
}
