// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Resizing a window by dragging an edge or a corner: the change follows the pointer's travel from where the drag began (a larger
/// drag makes the window larger, a smaller one smaller), the edge opposite the one dragged stays where it is, the minimum and the
/// main view limit the result, and the window never ends up partly outside the main view.
/// </summary>
public class FloaterResizeTests
{
    private static readonly FloaterSpec Alpha = new("alpha", "k.alpha", new UiSize(300, 200), new UiSize(150, 100));
    private static readonly UiRect Bounds = new(0, 26, 1000, 600);

    private static FloaterManager OpenAlpha()
    {
        var m = new FloaterManager();
        m.Register(Alpha);
        m.SetBounds(Bounds);
        m.Open("alpha");
        m.Move("alpha", 200, 100);   // x 200..500, y 100..300
        return m;
    }

    private static UiRect Rect(FloaterManager m) => m.RectOf("alpha")!.Value;

    [Fact]
    public void Dragging_the_bottom_right_corner_out_makes_the_window_larger_by_the_travel()
    {
        var m = OpenAlpha();
        var start = Rect(m);
        Assert.True(m.ResizeBy("alpha", ResizeEdges.Right | ResizeEdges.Bottom, start, 120, 70));
        Assert.Equal(new UiRect(200, 100, 420, 270), Rect(m));
    }

    [Fact]
    public void Dragging_the_bottom_right_corner_in_makes_the_window_smaller_by_the_travel()
    {
        var m = OpenAlpha();
        var start = Rect(m);
        Assert.True(m.ResizeBy("alpha", ResizeEdges.Right | ResizeEdges.Bottom, start, -100, -60));
        Assert.Equal(new UiRect(200, 100, 200, 140), Rect(m));
    }

    [Fact]
    public void Dragging_the_left_edge_out_moves_the_left_edge_and_keeps_the_right_edge()
    {
        var m = OpenAlpha();
        var start = Rect(m);
        Assert.True(m.ResizeBy("alpha", ResizeEdges.Left, start, -60, 0));
        Assert.Equal(new UiRect(140, 100, 360, 200), Rect(m));
        Assert.Equal(start.Right, Rect(m).Right);
    }

    [Fact]
    public void Dragging_the_top_edge_in_moves_the_top_edge_and_keeps_the_bottom_edge()
    {
        var m = OpenAlpha();
        var start = Rect(m);
        Assert.True(m.ResizeBy("alpha", ResizeEdges.Top, start, 0, 30));
        Assert.Equal(new UiRect(200, 130, 300, 170), Rect(m));
        Assert.Equal(start.Bottom, Rect(m).Bottom);
    }

    [Fact]
    public void Dragging_the_top_left_corner_in_past_the_minimum_stops_at_the_minimum_with_the_opposite_corner_fixed()
    {
        var m = OpenAlpha();
        var start = Rect(m);
        Assert.True(m.ResizeBy("alpha", ResizeEdges.Left | ResizeEdges.Top, start, 400, 400));
        var r = Rect(m);
        Assert.Equal((150f, 100f), (r.W, r.H));
        Assert.Equal((start.Right, start.Bottom), (r.Right, r.Bottom));
    }

    [Fact]
    public void Dragging_out_past_the_main_view_stops_at_the_main_views_edges()
    {
        var m = OpenAlpha();
        var start = Rect(m);
        Assert.True(m.ResizeBy("alpha", ResizeEdges.Right | ResizeEdges.Bottom, start, 5000, 5000));
        Assert.Equal(new UiRect(200, 100, Bounds.Right - 200, Bounds.Bottom - 100), Rect(m));
        Assert.True(m.ResizeBy("alpha", ResizeEdges.Left | ResizeEdges.Top, start, -5000, -5000));
        Assert.Equal(new UiRect(Bounds.X, Bounds.Y, start.Right - Bounds.X, start.Bottom - Bounds.Y), Rect(m));
    }

    [Fact]
    public void Every_resize_leaves_the_window_inside_the_main_view()
    {
        foreach (var edges in new[] { ResizeEdges.Left, ResizeEdges.Right, ResizeEdges.Top, ResizeEdges.Bottom,
                     ResizeEdges.Left | ResizeEdges.Top, ResizeEdges.Right | ResizeEdges.Top, ResizeEdges.Left | ResizeEdges.Bottom, ResizeEdges.Right | ResizeEdges.Bottom })
            foreach (var (dx, dy) in new[] { (-900f, -900f), (-50f, 40f), (0f, 0f), (70f, -30f), (900f, 900f) })
            {
                var m = OpenAlpha();
                Assert.True(m.ResizeBy("alpha", edges, Rect(m), dx, dy));
                Assert.True(Bounds.Encloses(Rect(m)), $"{edges} by ({dx}, {dy}) left the window at {Rect(m)}");
                Assert.True(Rect(m).W >= 150 && Rect(m).H >= 100);
            }
    }

    [Fact]
    public void A_window_that_is_not_resizable_or_not_open_is_not_resized()
    {
        var m = new FloaterManager();
        m.Register(new FloaterSpec("fixed", "k.fixed", new UiSize(200, 100), new UiSize(200, 100), Resizable: false));
        m.Register(Alpha);
        m.SetBounds(Bounds);
        m.Open("fixed");
        Assert.False(m.ResizeBy("fixed", ResizeEdges.Right, m.RectOf("fixed")!.Value, 50, 0));
        Assert.Equal(200, m.RectOf("fixed")!.Value.W);
        Assert.False(m.ResizeBy("alpha", ResizeEdges.Right, new UiRect(0, 0, 300, 200), 50, 0));
    }

    [Fact]
    public void Fitting_to_content_grows_the_window_to_what_the_content_needs_once_and_a_later_drag_can_make_it_smaller()
    {
        var m = OpenAlpha();
        Assert.True(m.FitToContent("alpha", 460, 340));
        Assert.Equal((460f, 340f), (Rect(m).W, Rect(m).H));
        Assert.True(Bounds.Encloses(Rect(m)));
        Assert.False(m.FitToContent("alpha", 400, 300));   // already large enough: nothing changes
        Assert.Equal((460f, 340f), (Rect(m).W, Rect(m).H));
        Assert.True(m.ResizeBy("alpha", ResizeEdges.Right | ResizeEdges.Bottom, Rect(m), -200, -150));
        Assert.Equal((260f, 190f), (Rect(m).W, Rect(m).H));   // smaller than the content needs: the content scrolls
    }

    [Fact]
    public void Fitting_to_content_never_makes_the_window_larger_than_the_main_view()
    {
        var m = OpenAlpha();
        Assert.True(m.FitToContent("alpha", 5000, 5000));
        Assert.Equal((Bounds.W * FloaterManager.FirstOpenShare, Bounds.H * FloaterManager.FirstOpenShare), (Rect(m).W, Rect(m).H));   // at most 85 % of it when it opens
        Assert.True(Bounds.Encloses(Rect(m)));
    }
}
