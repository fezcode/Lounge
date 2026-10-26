using Monitor = Avalonia.Platform.Screen;
using Lounge.Core.Settings;

namespace Lounge.Shell;

/// <summary>The monitors Lounge can fill, for the Screen setting. A monitor is remembered by
/// its Windows device name; "" means the main one, and so does a remembered monitor that
/// isn't plugged in any more.</summary>
public static class Displays
{
    public static IReadOnlyList<Monitor> All => L.Window?.Screens.All ?? [];

    public static string Id(Monitor s) => s.DisplayName is { Length: > 0 } name ? name : s.Bounds.ToString();

    public static string Label(Monitor s, int index) =>
        $"Screen {index + 1} · {s.Bounds.Width}×{s.Bounds.Height}{(s.IsPrimary ? " · main" : "")}";

    /// <summary>The monitor the setting names, else the main one.</summary>
    public static Monitor? Chosen()
    {
        var id = L.Settings.String(K.Display);
        var all = All;
        return all.FirstOrDefault(s => id.Length > 0 && Id(s) == id) ?? all.FirstOrDefault(s => s.IsPrimary) ?? all.FirstOrDefault();
    }

    /// <summary>The choices in order: the main screen, then each monitor.</summary>
    public static IReadOnlyList<(string Value, string Label)> Options() =>
        [("", "Main screen"), .. All.Select((s, i) => (Id(s), Label(s, i)))];

    public static string Describe(string value)
    {
        if (value.Length == 0) return "Main screen";
        var all = All;
        for (int i = 0; i < all.Count; i++)
            if (Id(all[i]) == value) return Label(all[i], i);
        return "Main screen (that one isn't connected)";
    }

    /// <summary>Left and right on the row: the next monitor along.</summary>
    public static void Step(int direction)
    {
        var options = Options();
        var i = options.Select(o => o.Value).ToList().IndexOf(L.Settings.String(K.Display));
        var next = ((i < 0 ? 0 : i) + direction + options.Count) % options.Count;
        L.Settings.Set(K.Display, options[next].Value);
    }
}
