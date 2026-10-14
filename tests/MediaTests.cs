using System.Diagnostics;
using Lounge.Core.Library;
using Lounge.Media;
using Xunit;

namespace Lounge.Tests;

/// <summary>Generates small test clips once with the MSYS2 ffmpeg.exe that the FFmpeg
/// DLLs come from. Tests that need them are skipped when it is not installed.</summary>
public sealed class Clips
{
    public static readonly Clips Instance = new();
    public string Dir { get; }
    public string? Error { get; }
    public string Video => Path.Combine(Dir, "clip.mp4");
    public string Mkv => Path.Combine(Dir, "subbed.mkv");
    public string Song => Path.Combine(Dir, "song.mp3");
    public string Hevc10 => Path.Combine(Dir, "hevc10.mkv");

    private Clips()
    {
        Dir = Path.Combine(Path.GetTempPath(), "lounge-clips-v2");
        Directory.CreateDirectory(Dir);
        var exe = FindFfmpeg();
        if (exe is null) { Error = "ffmpeg.exe not found"; return; }
        try
        {
            File.WriteAllText(Path.Combine(Dir, "clip.srt"),
                "1\n00:00:01,000 --> 00:00:04,000\nHello from Lounge\n\n2\n00:00:05,000 --> 00:00:07,000\nSecond line\n");
            Make(exe, Video, "-f lavfi -i testsrc2=size=640x360:rate=25 -f lavfi -i sine=frequency=330:sample_rate=48000 -t 8 -c:v libx264 -g 25 -pix_fmt yuv420p -c:a aac -shortest");
            Make(exe, Mkv, $"-i \"{Video}\" -i \"{Path.Combine(Dir, "clip.srt")}\" -map 0 -map 1 -c copy -c:s ass -metadata:s:s:0 language=eng");
            Make(exe, Song, "-f lavfi -i sine=frequency=440:sample_rate=44100 -t 3 -metadata title=Tone -metadata artist=Lab -metadata album=Tests -metadata track=4 -c:a libmp3lame");
            Make(exe, Hevc10, "-f lavfi -i testsrc2=size=320x240:rate=25 -t 3 -c:v libx265 -pix_fmt yuv420p10le -x265-params log-level=none");
        }
        catch (Exception e) { Error = e.Message; }
    }

    private static string? FindFfmpeg()
    {
        foreach (var d in new[] { @"D:\Apps\msys64\mingw64\bin", @"C:\msys64\mingw64\bin" })
            if (File.Exists(Path.Combine(d, "ffmpeg.exe"))) return Path.Combine(d, "ffmpeg.exe");
        return null;
    }

    private static void Make(string exe, string output, string args)
    {
        if (File.Exists(output) && new FileInfo(output).Length > 0) return;
        var psi = new ProcessStartInfo(exe, $"-y -loglevel error {args} \"{output}\"")
        {
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        psi.Environment["PATH"] = Path.GetDirectoryName(exe) + ";" + Environment.GetEnvironmentVariable("PATH");
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg failed for {Path.GetFileName(output)}: {err}");
    }
}

[CollectionDefinition("Media", DisableParallelization = true)]
public class MediaCollection { }

[Collection("Media")]
public class MediaTests
{
    private static bool Ready(out string why)
    {
        why = "";
        if (!Ff.Ensure()) { why = Ff.Error ?? "no ffmpeg"; return false; }
        if (Clips.Instance.Error is { } e) { why = e; return false; }
        return true;
    }

    [Fact]
    public void FFmpeg_loads_from_the_staged_folder()
    {
        Assert.True(Ff.Ensure(), Ff.Error);
    }

    [Fact]
    public void Probe_reads_duration_tracks_and_tags()
    {
        if (!Ready(out var why)) { Assert.Fail(why); return; }
        var v = MediaProbe.Inspect(Clips.Instance.Mkv)!;
        Assert.InRange(v.Result.Duration, 7.5, 8.5);
        Assert.Contains(v.Tracks, t => t.Kind == TrackKind.Video && t.Width == 640);
        Assert.Contains(v.Tracks, t => t.Kind == TrackKind.Audio);
        Assert.Contains(v.Tracks, t => t.Kind == TrackKind.Subtitle && t.MatchesLanguage("en"));

        var song = MediaProbe.Probe(Clips.Instance.Song)!;
        Assert.Equal("Tone", song.Title);
        Assert.Equal("Lab", song.Artist);
        Assert.Equal(4, song.Track);
    }

    [Fact]
    public void Thumbnail_is_scaled_and_near_the_request()
    {
        if (!Ready(out var why)) { Assert.Fail(why); return; }
        using var t = Thumbnailer.Open(Clips.Instance.Video, 160)!;
        var th = t.At(3.2)!;
        Assert.Equal(160, th.Width);
        Assert.Equal(90, th.Height);
        Assert.InRange(th.Pts, 2.0, 3.2);
        Assert.Contains(th.Bgra, b => b > 0);
    }

    [Fact]
    public void Ten_bit_hevc_decodes()
    {
        if (!Ready(out var why)) { Assert.Fail(why); return; }
        using var t = Thumbnailer.Open(Clips.Instance.Hevc10, 160);
        Assert.NotNull(t);
        Assert.NotNull(t!.At(1));
    }

    private static VideoFrame? WaitFrame(MediaPlayer p, int ms = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (p.TakeFrame() is { } f) return f;
            Thread.Sleep(5);
        }
        return null;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Opens_and_shows_the_first_frame(bool gpu)
    {
        if (!Ready(out var why)) { Assert.Fail(why); return; }
        using var p = new MediaPlayer();
        Assert.True(p.Open(Clips.Instance.Video, new PlayerOptions { Volume = 0, GpuConvert = gpu, HwDecode = gpu }), p.LastError);
        Assert.Equal(PlayerState.Paused, p.State);
        using var f = WaitFrame(p);
        Assert.NotNull(f);
        if (gpu) Assert.Contains(f!.Pixels, new[] { FramePixels.Nv12, FramePixels.Yuv420p }); else Assert.Equal(FramePixels.Bgra, f!.Pixels);
        Assert.Equal(640, f.Width);
        Assert.InRange(f.Pts, 0, 0.05);
    }

    [Fact]
    public void Exact_seek_lands_within_one_frame()
    {
        if (!Ready(out var why)) { Assert.Fail(why); return; }
        using var p = new MediaPlayer();
        Assert.True(p.Open(Clips.Instance.Video, new PlayerOptions { Volume = 0 }), p.LastError);
        WaitFrame(p)?.Dispose();
        foreach (var target in new[] { 3.0, 5.52, 1.2 })
        {
            p.Seek(target);
            using var f = WaitFrame(p);
            Assert.NotNull(f);
            Assert.InRange(f!.Pts, target - 0.041, target + 0.041);
        }
    }

    [Fact]
    public void Scrub_shows_keyframes_then_settles_exactly()
    {
        if (!Ready(out var why)) { Assert.Fail(why); return; }
        using var p = new MediaPlayer();
        Assert.True(p.Open(Clips.Instance.Video, new PlayerOptions { Volume = 0 }), p.LastError);
        WaitFrame(p)?.Dispose();
        p.BeginScrub();
        p.ScrubTo(4.6);
        using (var k = WaitFrame(p))
        {
            Assert.NotNull(k);
            Assert.InRange(k!.Pts, 3.9, 4.65);   // the keyframe at or before 4.6 (GOP of 1 s)
        }
        p.EndScrub(4.6);
        using var f = WaitFrame(p);
        Assert.NotNull(f);
        Assert.InRange(f!.Pts, 4.55, 4.65);
    }

    [Fact]
    public void Plays_with_audio_clock_and_frames_advance()
    {
        if (!Ready(out var why)) { Assert.Fail(why); return; }
        using var p = new MediaPlayer();
        Assert.True(p.Open(Clips.Instance.Video, new PlayerOptions { Volume = 0 }), p.LastError);
        Assert.True(p.HasAudio, "this machine has no audio output: " + p.LastError);
        WaitFrame(p)?.Dispose();
        p.Play();
        var sw = Stopwatch.StartNew();
        double last = -1; int frames = 0;
        while (sw.ElapsedMilliseconds < 2000)
        {
            using var f = p.TakeFrame();
            if (f is not null) { Assert.True(f.Pts > last); last = f.Pts; frames++; }
            Thread.Sleep(4);
        }
        Assert.InRange(frames, 35, 55);            // 25 fps over 2 s
        var drift = Math.Abs(p.Position - last);
        Assert.True(drift < 0.06, $"video {last:0.000} vs clock {p.Position:0.000}");
        Assert.InRange(p.Position, 1.6, 2.4);
    }

    [Fact]
    public void Double_speed_runs_the_clock_twice_as_fast()
    {
        if (!Ready(out var why)) { Assert.Fail(why); return; }
        using var p = new MediaPlayer();
        Assert.True(p.Open(Clips.Instance.Video, new PlayerOptions { Volume = 0 }), p.LastError);
        WaitFrame(p)?.Dispose();
        p.SetSpeed(2.0);
        p.Play();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500) { p.TakeFrame()?.Dispose(); Thread.Sleep(5); }
        Assert.InRange(p.Position, 2.4, 3.4);
    }

    [Fact]
    public void Reaches_the_end()
    {
        if (!Ready(out var why)) { Assert.Fail(why); return; }
        using var p = new MediaPlayer();
        Assert.True(p.Open(Clips.Instance.Video, new PlayerOptions { Volume = 0 }), p.LastError);
        p.Seek(7.0);
        p.Play();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000 && p.State != PlayerState.Ended) { p.TakeFrame()?.Dispose(); Thread.Sleep(5); }
        Assert.Equal(PlayerState.Ended, p.State);
    }

    [Fact]
    public void Sidecar_srt_renders_with_the_user_style()
    {
        if (!Ready(out var why)) { Assert.Fail(why); return; }
        using var p = new MediaPlayer();
        Assert.True(p.Open(Clips.Instance.Video, new PlayerOptions { Volume = 0 }), p.LastError);
        Assert.Equal("clip.srt", p.Subtitles.ExternalName);
        p.Subtitles.SetStyle(new SubStyle("Arial", 1.2, 0xFFE066, 0, 2, 0));
        var img = p.Subtitles.Render(2.0, 640, 360, out var changed);
        Assert.True(changed);
        Assert.NotNull(img);
        Assert.Contains(img!, b => b > 200);
        Assert.Null(p.Subtitles.Render(4.5, 640, 360, out _));
    }

    [Fact]
    public void Embedded_ass_track_preloads()
    {
        if (!Ready(out var why)) { Assert.Fail(why); return; }
        using var p = new MediaPlayer();
        var dir = Path.Combine(Clips.Instance.Dir, "embedded");
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, "subbed.mkv");
        File.Copy(Clips.Instance.Mkv, copy, true);
        Assert.True(p.Open(copy, new PlayerOptions { Volume = 0, SubLanguage = "en" }), p.LastError);
        Assert.True(p.SubtitleStream >= 0);
        var sw = Stopwatch.StartNew();
        byte[]? img = null;
        while (sw.ElapsedMilliseconds < 3000 && img is null) { img = p.Subtitles.Render(5.5, 640, 360, out _); Thread.Sleep(20); }
        Assert.NotNull(img);
    }

    [Fact]
    public void Missing_file_fails_cleanly()
    {
        using var p = new MediaPlayer();
        Assert.False(p.Open(@"C:\definitely\not\here.mkv", new PlayerOptions()));
        Assert.Equal(PlayerState.Failed, p.State);
        Assert.False(string.IsNullOrEmpty(p.LastError));
    }

    [Fact]
    public void Audio_file_reader_decodes_to_the_requested_format()
    {
        if (!Ready(out var why)) { Assert.Fail(why); return; }
        using var r = AudioFileReader.Open(Clips.Instance.Song, 48000, 2)!;
        var buf = new float[48000 * 2 * 4];
        var n = r.Read(buf);
        Assert.InRange(n, 48000 * 2 * 29 / 10, 48000 * 2 * 31 / 10);
        Assert.True(r.Ended);
    }

    [Fact]
    public void Library_probe_adapter_reads_real_files()
    {
        if (!Ready(out var why)) { Assert.Fail(why); return; }
        using var dir = new TempDir();
        File.Copy(Clips.Instance.Song, dir.File("song.mp3"));
        using var db = LibraryDb.InMemory();
        var s = new LibraryScanner(db, MediaProbe.Probe, dir.File("thumbs")).Scan(LibraryKind.Music, [dir.Path]);
        Assert.Equal(1, s.Added);
        Assert.Equal("Tone", db.All(LibraryKind.Music)[0].Title);
    }
}
