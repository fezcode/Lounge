namespace Lounge.Core.Media;

/// <summary>tivi's sidecar rule: for movie.mkv, look for movie.*.srt etc. beside it and
/// take the best one. Score 0: tag names the preferred language ("en", "eng" or
/// "English" in any dot-separated part); 1: plain movie.srt; 2: any tag when there is no
/// preference; 3: another language. Forced/SDH variants add 4. Ties go to the natural
/// sort order of the name.</summary>
public static class SidecarSubs
{
    private static readonly (string Code, string Alt3, string Name)[] Langs =
    [
        ("en", "eng", "English"), ("tr", "tur", "Turkish"), ("de", "ger", "German"),
        ("fr", "fre", "French"), ("es", "spa", "Spanish"), ("it", "ita", "Italian"),
        ("pt", "por", "Portuguese"), ("ru", "rus", "Russian"), ("ja", "jpn", "Japanese"),
    ];

    public static bool TagNamesLanguage(string tag, string? lang)
    {
        if (string.IsNullOrEmpty(lang)) return false;
        var known = Langs.FirstOrDefault(l => l.Code.Equals(lang, StringComparison.OrdinalIgnoreCase));
        foreach (var part in tag.Split('.'))
        {
            if (part.Equals(lang, StringComparison.OrdinalIgnoreCase)) return true;
            if (known.Code is not null &&
                (part.Equals(known.Alt3, StringComparison.OrdinalIgnoreCase) || part.Equals(known.Name, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    /// <summary>Scores one candidate file name against the video stem; null when it is not a sidecar.</summary>
    public static int? Score(string videoStem, string candidateName, string? lang)
    {
        if (MediaKinds.Of(candidateName) != MediaKind.Subtitle) return null;
        if (candidateName.Length <= videoStem.Length + 1) return null;
        if (!candidateName.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase)) return null;
        if (candidateName[videoStem.Length] != '.') return null;
        var rest = candidateName[(videoStem.Length + 1)..];   // "srt" | "en.srt" | "en.forced.srt"
        var lastDot = rest.LastIndexOf('.');
        var tag = lastDot > 0 ? rest[..lastDot] : "";
        int score;
        if (tag.Length > 0 && TagNamesLanguage(tag, lang)) score = 0;
        else if (tag.Length == 0) score = 1;
        else if (string.IsNullOrEmpty(lang)) score = 2;
        else score = 3;
        if (tag.Contains("forced", StringComparison.OrdinalIgnoreCase) || tag.Contains("sdh", StringComparison.OrdinalIgnoreCase))
            score += 4;
        return score;
    }

    public static string? Pick(string videoPath, IEnumerable<string> siblingNames, string? lang)
    {
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        if (stem.Length == 0) return null;
        string? best = null; int bestScore = int.MaxValue;
        foreach (var name in siblingNames)
        {
            if (Score(stem, name, lang) is not { } s) continue;
            if (s > bestScore) continue;
            if (s == bestScore && best is not null && NaturalComparer.Instance.Compare(name, best) >= 0) continue;
            best = name; bestScore = s;
        }
        return best is null ? null : Path.Combine(Path.GetDirectoryName(videoPath) ?? "", best);
    }

    public static string? Find(string videoPath, string? lang)
    {
        var dir = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        try { return Pick(videoPath, Directory.EnumerateFiles(dir).Select(Path.GetFileName)!, lang); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
