using System.Globalization;
using FFmpeg.AutoGen;
using Lounge.Core.Library;

namespace Lounge.Media;

public enum TrackKind { Video, Audio, Subtitle }

public sealed record TrackInfo(int StreamIndex, TrackKind Kind, string Codec, string? Language, string? Title,
    int Channels = 0, int Width = 0, int Height = 0, bool IsDefault = false, bool IsBitmap = false, bool IsForced = false)
{
    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title!);
            if (!string.IsNullOrWhiteSpace(Language) && Language != "und")
                parts.Add(LanguageName(Language!));
            if (Kind == TrackKind.Audio)
                parts.Add(Channels switch { 1 => "Mono", 2 => "Stereo", 6 => "5.1", 8 => "7.1", _ => Channels > 0 ? $"{Channels} ch" : "" });
            parts.Add(Codec.ToUpperInvariant());
            if (IsForced) parts.Add("Forced");
            return string.Join(" · ", parts.Where(p => p.Length > 0).Distinct());
        }
    }

    public static string LanguageName(string code)
    {
        try
        {
            var c = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .FirstOrDefault(x => x.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase) ||
                                     x.TwoLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase));
            return c?.EnglishName ?? code;
        }
        catch (CultureNotFoundException) { return code; }
    }

    /// <summary>Does this track's language tag match a two-letter preference ("en")?</summary>
    public bool MatchesLanguage(string pref)
    {
        if (string.IsNullOrEmpty(pref) || string.IsNullOrEmpty(Language)) return false;
        if (Language!.Equals(pref, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var c = new CultureInfo(pref);
            return c.ThreeLetterISOLanguageName.Equals(Language, StringComparison.OrdinalIgnoreCase) ||
                   (pref == "de" && Language == "ger") || (pref == "fr" && Language == "fre");
        }
        catch (CultureNotFoundException) { return false; }
    }
}

/// <summary>Reads duration, tags, tracks and cover art without decoding a frame.</summary>
public static unsafe class MediaProbe
{
    public static ProbeResult? Probe(string path) => Inspect(path)?.Result;

    public sealed record Inspection(ProbeResult Result, IReadOnlyList<TrackInfo> Tracks, IReadOnlyList<(double Start, string Title)> Chapters);

    public static Inspection? Inspect(string path)
    {
        if (!Ff.Ensure()) return null;
        AVFormatContext* fmt = null;
        if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return null;
        try
        {
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) return null;
            var duration = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            var tracks = Tracks(fmt);
            byte[]? art = null;
            int w = 0, h = 0;
            for (int i = 0; i < fmt->nb_streams; i++)
            {
                var st = fmt->streams[i];
                if ((st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0 && st->attached_pic.size > 0 && art is null)
                {
                    art = new byte[st->attached_pic.size];
                    new ReadOnlySpan<byte>(st->attached_pic.data, st->attached_pic.size).CopyTo(art);
                }
                else if (st->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && w == 0)
                {
                    w = st->codecpar->width; h = st->codecpar->height;
                }
            }
            var meta = new Dictionary<string, string?>();
            foreach (var k in new[] { "title", "artist", "album", "album_artist", "track", "date", "year" })
                meta[k] = Ff.Tag(fmt->metadata, k) ?? FirstStreamTag(fmt, k);
            static int Num(string? s) => s is null ? 0 : int.TryParse(new string(s.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0;
            var result = new ProbeResult(duration, meta["title"], meta["artist"], meta["album"], meta["album_artist"],
                Num(meta["track"]), Num(meta["date"] ?? meta["year"]), w, h, art);

            var chapters = new List<(double, string)>();
            for (int i = 0; i < fmt->nb_chapters; i++)
            {
                var ch = fmt->chapters[i];
                chapters.Add((Ff.ToSeconds(ch->start, ch->time_base), Ff.Tag(ch->metadata, "title") ?? $"Chapter {i + 1}"));
            }
            return new Inspection(result, tracks, chapters);
        }
        finally
        {
            ffmpeg.avformat_close_input(&fmt);
        }
    }

    private static string? FirstStreamTag(AVFormatContext* fmt, string key)
    {
        for (int i = 0; i < fmt->nb_streams; i++)
            if (Ff.Tag(fmt->streams[i]->metadata, key) is { } v) return v;
        return null;
    }

    internal static List<TrackInfo> Tracks(AVFormatContext* fmt)
    {
        var list = new List<TrackInfo>();
        for (int i = 0; i < fmt->nb_streams; i++)
        {
            var st = fmt->streams[i];
            var par = st->codecpar;
            if ((st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0) continue;
            var codec = ffmpeg.avcodec_get_name(par->codec_id) ?? "?";
            var lang = Ff.Tag(st->metadata, "language");
            var title = Ff.Tag(st->metadata, "title");
            var isDefault = (st->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0;
            var forced = (st->disposition & ffmpeg.AV_DISPOSITION_FORCED) != 0;
            switch (par->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    list.Add(new TrackInfo(i, TrackKind.Video, codec, lang, title, Width: par->width, Height: par->height, IsDefault: isDefault));
                    break;
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    list.Add(new TrackInfo(i, TrackKind.Audio, codec, lang, title, Channels: par->ch_layout.nb_channels, IsDefault: isDefault));
                    break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    var bitmap = par->codec_id is AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE or AVCodecID.AV_CODEC_ID_DVD_SUBTITLE
                        or AVCodecID.AV_CODEC_ID_DVB_SUBTITLE or AVCodecID.AV_CODEC_ID_XSUB;
                    list.Add(new TrackInfo(i, TrackKind.Subtitle, codec, lang, title, IsDefault: isDefault, IsBitmap: bitmap, IsForced: forced));
                    break;
            }
        }
        return list;
    }
}
