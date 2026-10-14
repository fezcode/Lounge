using Lounge.Core.Focus;
using Xunit;

namespace Lounge.Tests;

public class FocusTests
{
    /// <summary>Rows of items laid out on a grid: item (r, c) sits at x = c*100 + offset, y = r*100.</summary>
    private static (FocusScope Scope, FocusList Rows) Rows(params (int Count, double Offset)[] rows)
    {
        var list = new FocusList(horizontal: false);
        var pos = new Dictionary<FocusItem, Pt>();
        for (int r = 0; r < rows.Length; r++)
        {
            var row = list.Add(new FocusList(horizontal: true));
            for (int c = 0; c < rows[r].Count; c++)
            {
                var item = row.Add(new FocusItem(tag: $"{r},{c}"));
                pos[item] = new Pt(c * 100 + rows[r].Offset, r * 100);
            }
        }
        var scope = new FocusScope(list) { Locate = i => pos.TryGetValue(i, out var p) ? p : null };
        scope.Refresh();
        return (scope, list);
    }

    private static string? At(FocusScope s) => s.Focused?.Tag as string;

    [Fact]
    public void Moves_within_and_between_rows_remembering_position()
    {
        var (s, _) = Rows((5, 0), (5, 0));
        Assert.Equal("0,0", At(s));
        s.Move(Dir.Right); s.Move(Dir.Right);
        Assert.Equal("0,2", At(s));
        s.Move(Dir.Down);
        Assert.Equal("1,0", At(s));      // rows remember their own place, which starts at 0
        s.Move(Dir.Right);
        s.Move(Dir.Up);
        Assert.Equal("0,2", At(s));      // and come back to where they were
    }

    [Fact]
    public void Edges_do_not_move_and_report_false()
    {
        var (s, _) = Rows((2, 0));
        Assert.False(s.Move(Dir.Left));
        Assert.False(s.Move(Dir.Up));
        Assert.True(s.Move(Dir.Right));
        Assert.False(s.Move(Dir.Right));
    }

    [Fact]
    public void Empty_rows_are_skipped()
    {
        var (s, _) = Rows((3, 0), (0, 0), (3, 0));
        s.Move(Dir.Down);
        Assert.Equal("2,0", At(s));
    }

    [Fact]
    public void Side_menu_enters_content_nearest_by_position()
    {
        var root = new FocusList(horizontal: true);
        var menu = root.Add(new FocusList(horizontal: false, rememberIndex: false));
        var content = root.Add(new FocusList(horizontal: false, rememberIndex: false));
        var pos = new Dictionary<FocusItem, Pt>();
        for (int i = 0; i < 6; i++) { var m = menu.Add(new FocusItem(tag: $"m{i}")); pos[m] = new Pt(0, i * 100); }
        for (int i = 0; i < 3; i++) { var c = content.Add(new FocusItem(tag: $"c{i}")); pos[c] = new Pt(500, 150 + i * 200); }
        var s = new FocusScope(root) { Locate = i => pos[i] };
        s.Refresh();
        for (int i = 0; i < 5; i++) s.Move(Dir.Down);
        Assert.Equal("m5", At(s));
        s.Move(Dir.Right);
        Assert.Equal("c2", At(s));
        s.Move(Dir.Left);
        Assert.Equal("m5", At(s));
    }

    [Fact]
    public void Grid_navigation_and_partial_last_row()
    {
        var g = new FocusGrid(4);
        for (int i = 0; i < 10; i++) g.Add(new FocusItem(tag: i.ToString()));
        var s = new FocusScope(g);
        s.Refresh();
        s.Move(Dir.Down); s.Move(Dir.Right); s.Move(Dir.Right); s.Move(Dir.Right);
        Assert.Equal("7", At(s));
        s.Move(Dir.Down);
        Assert.Equal("9", At(s));         // the last row is short: land on its last item
        Assert.False(s.Move(Dir.Down));
        s.Move(Dir.Up);
        Assert.Equal("5", At(s));
    }

    [Fact]
    public void Grid_entered_from_above_picks_nearest_column()
    {
        var root = new FocusList(horizontal: false);
        var top = root.Add(new FocusList(horizontal: true));
        var grid = root.Add(new FocusGrid(3));
        var pos = new Dictionary<FocusItem, Pt>();
        var t = top.Add(new FocusItem(tag: "tab"));
        pos[t] = new Pt(210, 0);
        for (int i = 0; i < 6; i++) { var it = grid.Add(new FocusItem(tag: $"g{i}")); pos[it] = new Pt(i % 3 * 100, 100 + i / 3 * 100); }
        var s = new FocusScope(root) { Locate = i => pos[i] };
        s.Refresh();
        s.Move(Dir.Down);
        Assert.Equal("g2", At(s));
    }

    [Fact]
    public void Focus_changes_are_reported_once()
    {
        var (s, _) = Rows((3, 0));
        var log = new List<string>();
        foreach (var i in s.Root.Items) { var tag = (string)i.Tag!; i.FocusChanged = f => log.Add((f ? "+" : "-") + tag); }
        s.Move(Dir.Right);
        Assert.Equal(["-0,0", "+0,1"], log);
    }

    [Fact]
    public void Refresh_keeps_focus_when_items_are_added()
    {
        var (s, rows) = Rows((3, 0));
        s.Move(Dir.Right);
        var row = (FocusList)rows.Children[0];
        row.Add(new FocusItem(tag: "0,3"));
        s.Refresh();
        Assert.Equal("0,1", At(s));
    }

    [Fact]
    public void Spatial_moves_to_the_nearest_item_in_line()
    {
        var s = new FocusSpatial();
        var pos = new Dictionary<FocusItem, Pt>();
        void Put(string tag, double x, double y) { var i = s.Add(new FocusItem(tag: tag)); pos[i] = new Pt(x, y); }
        Put("Y", 680, 190); Put("X", 620, 250); Put("B", 740, 250); Put("A", 680, 310); Put("Menu", 510, 250);
        var scope = new FocusScope(s) { Locate = i => pos[i] };
        scope.Refresh();
        Assert.Equal("Y", At(scope));
        scope.Move(Dir.Down);
        Assert.Equal("A", At(scope));   // straight below beats the diagonal neighbours
        scope.Move(Dir.Left);
        Assert.Equal("X", At(scope));
        scope.Move(Dir.Left);
        Assert.Equal("Menu", At(scope));
        Assert.False(scope.Move(Dir.Left));
    }

    [Fact]
    public void Wrapping_list()
    {
        var list = new FocusList(horizontal: true) { Wrap = true };
        list.Add(new FocusItem(tag: "a")); list.Add(new FocusItem(tag: "b"));
        var s = new FocusScope(list);
        s.Refresh();
        s.Move(Dir.Left);
        Assert.Equal("b", At(s));
    }
}
