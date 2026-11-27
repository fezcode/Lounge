using Lounge.Media;
using Xunit;

namespace Lounge.Tests;

/// <summary>Bitmap subtitles (PGS) without durations: a picture lasts until the next display
/// set, and the sets arrive only as playback reads them.</summary>
public class SubPicturesTests
{
    private static readonly byte[] Px = new byte[4];

    private static void Set(SubPictures s, double start, params int[] ys)
    {
        s.BeginSet(start);
        foreach (var y in ys) s.Add(start, double.NaN, 0, y, 1, 1, Px, 1920, 1080);
    }

    private static double[] ShownAt(SubPictures s, double now) => s.Active(now).Select(p => p.Start).ToArray();

    [Fact]
    public void A_clear_ends_the_picture()
    {
        var s = new SubPictures();
        Set(s, 600.0, 900);
        Set(s, 601.5);                       // clear
        Assert.Equal([600.0], ShownAt(s, 601.0));
        Assert.Empty(ShownAt(s, 602.0));
    }

    [Fact]
    public void Two_pictures_of_one_set_show_together()
    {
        var s = new SubPictures();
        Set(s, 10.0, 800, 900);
        Assert.Equal(2, s.Active(10.5).Count);
    }

    [Fact]
    public void Skipping_ahead_does_not_leave_the_last_line_stuck()
    {
        var s = new SubPictures();
        Set(s, 600.0, 900);                  // its clear at 601.5 is never read: the user skips
        s.Flush();
        Assert.Empty(ShownAt(s, 700.0));
        Set(s, 702.0, 900);
        Assert.Equal([702.0], ShownAt(s, 702.5));
    }

    [Fact]
    public void Rewinding_after_a_skip_never_shows_two_lines_at_once()
    {
        var s = new SubPictures();
        Set(s, 600.0, 900);
        s.Flush();                           // skip to 700
        Set(s, 702.0, 900);
        Set(s, 704.0);
        s.Flush();                           // rewind to 599: the sets are read again
        Set(s, 600.0, 900);
        Set(s, 601.5);
        Set(s, 601.6, 900);
        Assert.Equal([601.6], ShownAt(s, 602.0));
    }

    [Fact]
    public void A_late_end_is_corrected_by_the_set_that_really_ends_it()
    {
        // An end decided from the wrong set (here 700) must give way when the real one
        // (601.5) turns up, or the old line is drawn over the new one.
        var s = new SubPictures();
        s.Add(600.0, 100.0, 0, 900, 1, 1, Px, 1920, 1080);
        Set(s, 601.5);
        Set(s, 601.6, 900);
        Assert.Equal([601.6], ShownAt(s, 602.0));
    }

    [Fact]
    public void Reading_a_set_twice_keeps_one_copy()
    {
        var s = new SubPictures();
        Set(s, 50.0, 900);
        Set(s, 50.0, 900);
        Assert.Single(s.Active(50.5));
    }
}
