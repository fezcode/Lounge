using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Lounge.Controls;

/// <summary>Keeps the focused item in view by gliding the scroll offset on a spring.
/// Rows keep focus at a fixed "anchor" from the left edge the way TV launchers do,
/// instead of letting it drift to the far side.</summary>
public static class SmoothScroll
{
    private sealed class State
    {
        public required Spring X;
        public required Spring Y;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    private static State For(ScrollViewer sv) => States.GetValue(sv, s => new State
    {
        X = new Spring(s.Offset.X, v => s.Offset = new Vector(Math.Max(0, v), s.Offset.Y), 260, 30),
        Y = new Spring(s.Offset.Y, v => s.Offset = new Vector(s.Offset.X, Math.Max(0, v)), 260, 30),
    });

    /// <summary>Scrolls every scroll viewer above <paramref name="item"/> so it is visible,
    /// with <paramref name="anchor"/> as the preferred distance from the leading edge.</summary>
    public static void BringIntoView(Control item, double anchorX = 96, double anchorY = 120)
    {
        Control child = item;
        foreach (var sv in item.GetVisualAncestors().OfType<ScrollViewer>())
        {
            if (sv.Content is not Control content) { child = sv; continue; }
            var pos = item.TranslatePoint(new Point(0, 0), content);
            if (pos is null) { child = sv; continue; }
            var st = For(sv);
            var view = sv.Viewport;
            var extent = sv.Extent;
            if (sv.HorizontalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled && extent.Width > view.Width)
            {
                var target = st.X.Target;
                var x = pos.Value.X;
                if (x - target < anchorX || x + item.Bounds.Width - target > view.Width - anchorX)
                    target = x - anchorX;
                st.X.To(Math.Clamp(target, 0, Math.Max(0, extent.Width - view.Width)));
            }
            if (sv.VerticalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled && extent.Height > view.Height)
            {
                var target = st.Y.Target;
                var y = pos.Value.Y;
                if (y - target < anchorY || y + item.Bounds.Height - target > view.Height - anchorY)
                    target = y - anchorY;
                st.Y.To(Math.Clamp(target, 0, Math.Max(0, extent.Height - view.Height)));
            }
            child = sv;
        }
    }

    public static void Reset(ScrollViewer sv)
    {
        var st = For(sv);
        st.X.Snap(0);
        st.Y.Snap(0);
    }
}
