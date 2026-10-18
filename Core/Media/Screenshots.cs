namespace Lounge.Core.Media;

/// <summary>Where a video screenshot goes and what it is called: "Title 12-34.png" in the
/// chosen folder (the Desktop by default), never overwriting an earlier one.</summary>
public static class Screenshots
{
    /// <summary>The setting's folder when it exists, otherwise the Desktop.</summary>
    public static string Folder(string? setting) =>
        !string.IsNullOrWhiteSpace(setting) && Directory.Exists(setting) ? setting : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string PathFor(string folder, string title, double seconds, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(title.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        if (safe.Length > 80) safe = safe[..80].TrimEnd();
        if (safe.Length == 0) safe = "Screenshot";
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var stamp = t.TotalHours >= 1 ? $"{(int)t.TotalHours}-{t.Minutes:00}-{t.Seconds:00}" : $"{t.Minutes:00}-{t.Seconds:00}";
        var path = Path.Combine(folder, $"{safe} {stamp}.png");
        for (var n = 2; exists(path); n++) path = Path.Combine(folder, $"{safe} {stamp} ({n}).png");
        return path;
    }
}
