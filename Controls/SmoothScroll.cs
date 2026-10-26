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
            if (sv.Content is not Control) { child = sv; continue; }
            // Where the item sits in the scrolled extent, margins included: measured from the
            // content alone, a row's left margin went uncounted and its last card was
            // reckoned in view while it hung past the edge.
            // From the parent, so the item's own focus scale doesn't shift where it seems to be.
            var pos = item.GetVisualParent() is Visual parent ? parent.TranslatePoint(item.Bounds.Position, sv) : null;
            if (pos is null) { child = sv; continue; }
            var st = For(sv);
            var view = sv.Viewport;
            var extent = sv.Extent;
            // The lift on focus, which must fit too.
            var lift = item is Card card ? (card.FocusScale - 1) / 2 : 0;
            if (sv.HorizontalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled && extent.Width > view.Width)
            {
                var target = st.X.Target;
                var x = pos.Value.X + sv.Offset.X;
                var w = item.Bounds.Width;
                if (x - target < anchorX || x + w - target > view.Width - anchorX)
                    target = x - anchorX;
                // An item wider than the room the anchor leaves still shows whole.
                if (x + w * (1 + lift) - target > view.Width) target = x + w * (1 + lift) - view.Width;
                st.X.To(Math.Clamp(target, 0, Math.Max(0, extent.Width - view.Width)));
            }
            if (sv.VerticalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled && extent.Height > view.Height)
            {
                var target = st.Y.Target;
                var y = pos.Value.Y + sv.Offset.Y;
                var h = item.Bounds.Height;
                if (y - target < anchorY || y + h - target > view.Height - anchorY)
                    target = y - anchorY;
                if (y + h * (1 + lift) - target > view.Height) target = y + h * (1 + lift) - view.Height;
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
