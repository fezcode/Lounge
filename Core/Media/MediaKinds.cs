namespace Lounge.Core.Media;

public enum MediaKind { Other, Video, Audio, Image, Subtitle, Program, Folder }

/// <summary>Extension tables. Video and audio mirror tivi's open-dialog filter.</summary>
public static class MediaKinds
{
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".wmv", ".flv", ".mpg", ".mpeg",
        ".ts", ".m2ts", ".mts", ".vob", ".ogv", ".3gp", ".divx",
    };

    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".wma", ".ac3", ".alac", ".aiff", ".ape",
    };

    private static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif",
    };

    private static readonly HashSet<string> Subtitle = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".sbv",
    };

    private static readonly HashSet<string> Program = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".url", ".bat", ".cmd",
    };

    public static MediaKind Of(string path)
    {
        var ext = Path.GetExtension(path);
        if (Video.Contains(ext)) return MediaKind.Video;
        if (Audio.Contains(ext)) return MediaKind.Audio;
        if (Image.Contains(ext)) return MediaKind.Image;
        if (Subtitle.Contains(ext)) return MediaKind.Subtitle;
        if (Program.Contains(ext)) return MediaKind.Program;
        return MediaKind.Other;
    }

    public static bool IsPlayable(string path) => Of(path) is MediaKind.Video or MediaKind.Audio;
}
