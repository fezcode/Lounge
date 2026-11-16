using Lounge.Core.Attract;
using Lounge.Core.Audio;
using Xunit;

namespace Lounge.Tests;

public class AttractTests
{
    [Fact]
    public void Starts_only_after_the_set_wait_on_a_menu_with_easter_eggs_on()
    {
        Assert.False(AttractRules.CanStart(true, 29_999, 30_000, true, true, false));
        Assert.True(AttractRules.CanStart(true, 30_000, 30_000, true, true, false));
        Assert.True(AttractRules.CanStart(true, 5_000, 5_000, true, true, false));     // a shorter wait, set by the user
        Assert.False(AttractRules.CanStart(false, 60_000, 30_000, true, true, false));  // setting off
        Assert.False(AttractRules.CanStart(true, 60_000, 30_000, false, true, false));  // an app is in front
        Assert.False(AttractRules.CanStart(true, 60_000, 30_000, true, false, false));  // a film, a page or a game
        Assert.False(AttractRules.CanStart(true, 60_000, 30_000, true, true, true));    // the dim wins
    }

    [Fact]
    public void Scenes_cut_on_bar_lines()
    {
        Assert.Equal(8.0, AttractChoreo.SceneSeconds, 6);
        Assert.Equal((0, 0.0), AttractChoreo.At(0));
        var (scene, t) = AttractChoreo.At(AttractChoreo.SceneSeconds * 2.5);
        Assert.Equal(2, scene);
        Assert.Equal(0.5, t, 6);
    }

    [Fact]
    public void Background_pushes_in_and_foreground_settles()
    {
        Assert.Equal(1.0, AttractChoreo.Background(0, 0).Scale, 6);
        Assert.Equal(1.12, AttractChoreo.Background(1, 0).Scale, 6);
        double last = 0;
        for (var t = 0.0; t <= 1; t += 0.05)
        {
            var s = AttractChoreo.Background(t, 1).Scale;
            Assert.True(s >= last);
            last = s;
        }
        Assert.True(AttractChoreo.Foreground(1, true).Scale < AttractChoreo.Foreground(0, true).Scale);
        Assert.True(AttractChoreo.Foreground(0, true).X > 0);    // starts pushed toward the middle
        Assert.True(AttractChoreo.Foreground(0, false).X < 0);
        Assert.Equal(0, AttractChoreo.Foreground(1, true).X, 6);
        // Background and foreground cover the screen throughout: no edges show.
        for (var t = 0.0; t <= 1; t += 0.05)
        {
            var b = AttractChoreo.Background(t, 0);
            Assert.True((b.Scale - 1) / 2 >= Math.Abs(b.X) && (b.Scale - 1) / 2 >= Math.Abs(b.Y));
        }
    }

    [Fact]
    public void Black_shows_only_at_the_cuts()
    {
        Assert.Equal(1, AttractChoreo.Black(0), 6);
        Assert.Equal(0, AttractChoreo.Black(0.5), 6);
        Assert.Equal(1, AttractChoreo.Black(1), 6);
    }

    [Fact]
    public void Theme_is_deterministic_loud_enough_throughout_and_never_clips()
    {
        var a = AttractTheme.Render(1);
        var b = AttractTheme.Render(1);
        Assert.Equal(a.Length, b.Length);
        Assert.True(a.AsSpan().SequenceEqual(b));
        var expected = (int)Math.Round(AttractTheme.Bars * AttractTheme.BarSeconds * AttractTheme.SampleRate) * 2;
        Assert.Equal(expected, a.Length);
        int window = 2 * AttractTheme.SampleRate * 2;
        for (int w = 0; w + window <= a.Length; w += window)
        {
            double sum = 0;
            for (int i = w; i < w + window; i++) sum += a[i] * a[i];
            Assert.True(Math.Sqrt(sum / window) > 0.02, $"quiet at {w / 2 / AttractTheme.SampleRate} s");
        }
        Assert.All(a, v => Assert.InRange(v, -1f, 1f));
    }
}
