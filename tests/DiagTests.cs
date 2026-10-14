using System.Diagnostics;
using Lounge.Media;
using Xunit;
using Xunit.Abstractions;

namespace Lounge.Tests;

[Collection("Media")]
public class DiagTests(ITestOutputHelper output)
{
    [Fact]
    public void Trace_clock()
    {
        if (!Ff.Ensure() || Clips.Instance.Error is not null) return;
        var sw = Stopwatch.StartNew();
        using var p = new MediaPlayer();
        p.Open(Clips.Instance.Video, new PlayerOptions { Volume = 0 });
        output.WriteLine($"open {sw.ElapsedMilliseconds} ms hw={p.HardwareDecoder}");
        p.TakeFrame()?.Dispose();
        Thread.Sleep(200);
        p.TakeFrame()?.Dispose();
        p.Play();
        var t0 = sw.ElapsedMilliseconds;
        int frames = 0;
        while (sw.ElapsedMilliseconds - t0 < 2000)
        {
            using var f = p.TakeFrame();
            if (f is not null) frames++;
            if ((sw.ElapsedMilliseconds - t0) % 200 < 5)
                output.WriteLine($"t={sw.ElapsedMilliseconds - t0} pos={p.Position:0.000} frames={frames} state={p.State}");
            Thread.Sleep(4);
        }
        var c0 = sw.ElapsedMilliseconds;
        p.Close();
        output.WriteLine($"close {sw.ElapsedMilliseconds - c0} ms");
    }

    [Fact]
    public void Trace_end()
    {
        if (!Ff.Ensure() || Clips.Instance.Error is not null) return;
        using var p = new MediaPlayer();
        p.Open(Clips.Instance.Video, new PlayerOptions { Volume = 0 });
        p.Seek(7.0);
        p.Play();
        var sw = Stopwatch.StartNew();
        var t = typeof(MediaPlayer);
        object? F(string n) => t.GetField(n, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(p);
        int frames = 0;
        while (sw.ElapsedMilliseconds < 3000)
        {
            using var f = p.TakeFrame();
            if (f is not null) frames++;
            if (sw.ElapsedMilliseconds % 250 < 5)
            {
                var ao = F("_audioOut") as AudioOutput;
                output.WriteLine($"t={sw.ElapsedMilliseconds} pos={p.Position:0.000} st={p.State} frames={frames} vEof={F("_videoEof")} aEof={F("_audioEof")} dEof={F("_demuxEof")} buf={ao?.BufferedSeconds:0.000} pend={F("_seekPending")} first={F("_presentFirst")} dead={F("_audioDead")}");
            }
            Thread.Sleep(4);
        }
    }
}