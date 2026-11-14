// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// One pointer drag on a window's title bar or a resize edge, as the official floater does it (LLDragHandle, LLResizeBar, LLResizeHandle): the
/// drag lasts only while the button that started it is held, and every step puts the window at the press-time rectangle changed by the pointer's
/// whole travel since the press, so a drag that goes out and back ends where it began and never swings back and forth.
/// </summary>
public class FloaterDragTests
{
    private static readonly FloaterSpec Alpha = new("alpha", "k.alpha", new UiSize(300, 200), new UiSize(150, 100));

    private static FloaterManager OpenAlpha()
    {
        var m = new FloaterManager();
        m.Register(Alpha);
        m.SetBounds(new UiRect(0, 26, 1000, 600));
        m.Open("alpha");
        m.Move("alpha", 200, 100);   // x 200..500, y 100..300
        return m;
    }

    private static UiRect Rect(FloaterManager m) => m.RectOf("alpha")!.Value;

    [Fact]
    public void A_title_bar_drag_moves_the_window_by_the_whole_travel_over_many_steps()
    {
        var m = OpenAlpha();
        var drag = new FloaterDrag();
        drag.Begin(m, "alpha", ResizeEdges.None, 250, 110);
        for (int i = 1; i <= 10; i++)
            Assert.True(drag.Track(m, "alpha", 250 - 6 * i, 110 + 5 * i, buttonHeld: true));
        Assert.Equal(new UiRect(140, 150, 300, 200), Rect(m));
    }

    [Fact]
    public void A_corner_drag_out_then_in_past_the_start_grows_then_shrinks_by_the_travel_every_step()
    {
        var m = OpenAlpha();
        var drag = new FloaterDrag();
        drag.Begin(m, "alpha", ResizeEdges.Right | ResizeEdges.Bottom, 500, 300);
        for (int k = 1; k <= 10; k++)
        {
            Assert.True(drag.Track(m, "alpha", 500 + 8 * k, 300 + 8 * k, buttonHeld: true));
            Assert.Equal((300f + 8 * k, 200f + 8 * k), (Rect(m).W, Rect(m).H));
        }
        for (int k = 9; k >= -5; k--)
        {
            Assert.True(drag.Track(m, "alpha", 500 + 8 * k, 300 + 8 * k, buttonHeld: true));
            Assert.Equal((300f + 8 * k, 200f + 8 * k), (Rect(m).W, Rect(m).H));
        }
    }

    [Fact]
    public void A_motion_without_the_button_held_ends_the_drag_and_leaves_the_window_where_the_last_held_step_put_it()
    {
        var m = OpenAlpha();
        var drag = new FloaterDrag();
        drag.Begin(m, "alpha", ResizeEdges.Left, 200, 150);
        Assert.True(drag.Track(m, "alpha", 180, 150, buttonHeld: true));
        Assert.Equal(new UiRect(180, 100, 320, 200), Rect(m));
        Assert.True(drag.Active);
        // the release was never seen (the engine took the pointer away); hovering afterwards must not resize
        Assert.False(drag.Track(m, "alpha", 260, 150, buttonHeld: false));
        Assert.False(drag.Active);
        Assert.Equal(new UiRect(180, 100, 320, 200), Rect(m));
        Assert.False(drag.Track(m, "alpha", 100, 150, buttonHeld: true));
        Assert.Equal(new UiRect(180, 100, 320, 200), Rect(m));
    }

    [Fact]
    public void After_the_drag_ends_held_motions_change_nothing()
    {
        var m = OpenAlpha();
        var drag = new FloaterDrag();
        drag.Begin(m, "alpha", ResizeEdges.Bottom, 300, 300);
        Assert.True(drag.Track(m, "alpha", 300, 340, buttonHeld: true));
        Assert.Equal(240f, Rect(m).H);
        drag.End();
        Assert.False(drag.Active);
        Assert.False(drag.Track(m, "alpha", 300, 400, buttonHeld: true));
        Assert.Equal(240f, Rect(m).H);
    }

    [Fact]
    public void A_drag_on_a_window_that_is_not_open_does_not_start()
    {
        var m = OpenAlpha();
        m.Close("alpha");
        var drag = new FloaterDrag();
        drag.Begin(m, "alpha", ResizeEdges.Right, 500, 200);
        Assert.False(drag.Active);
        Assert.False(drag.Track(m, "alpha", 520, 200, buttonHeld: true));
        // and one on an open window does
        m.Open("alpha");
        drag.Begin(m, "alpha", ResizeEdges.Right, 500, 200);
        Assert.True(drag.Active);
    }
}
