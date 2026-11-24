// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A minimized window: one click on its bar brings it back at the size and place it had before. No engine; each test makes its own
/// manager, so the class runs in parallel.
/// </summary>
public class FloaterRestoreTests
{
    private const string Id = "build";

    private static (FloaterManager M, MemoryFloaterPlaces Places) Open()
    {
        var places = new MemoryFloaterPlaces();
        var m = new FloaterManager(places);
        m.SetBounds(new UiRect(0, 0, 1280, 720));
        m.Register(new FloaterSpec(Id, "edit.title", new UiSize(460, 600), new UiSize(400, 520)));
        m.Open(Id);
        m.Move(Id, 200, 60);
        m.Resize(Id, 480, 560);
        return (m, places);
    }

    private static readonly UiRect Before = new(200, 60, 480, 560);

    [Fact]
    public void Restoring_puts_the_window_back_at_its_size_and_place_even_after_the_bar_was_moved()
    {
        var (m, _) = Open();
        Assert.True(m.SetMinimized(Id, true));
        Assert.True(m.IsMinimized(Id));
        m.Move(Id, 600, 100);
        Assert.True(m.SetMinimized(Id, false));
        Assert.False(m.IsMinimized(Id));
        Assert.Equal(Before, m.RectOf(Id));
    }

    [Fact]
    public void Minimizing_and_restoring_each_tell_the_view_even_for_the_front_window()
    {
        var (m, _) = Open();
        int changes = 0;
        m.Changed += () => changes++;
        m.SetMinimized(Id, true);
        Assert.Equal(1, changes);
        m.SetMinimized(Id, false);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_minimized_window_does_not_resize()
    {
        var (m, _) = Open();
        m.SetMinimized(Id, true);
        Assert.False(m.ResizeBy(Id, ResizeEdges.Right, Before, 100, 0));
        Assert.False(m.Resize(Id, 700, 700));
        m.SetMinimized(Id, false);
        Assert.Equal(Before, m.RectOf(Id));
    }

    [Fact]
    public void A_window_closed_while_minimized_opens_again_whole_where_it_was()
    {
        var (m, places) = Open();
        m.SetMinimized(Id, true);
        m.Move(Id, 600, 100);
        m.Close(Id);
        Assert.Equal(Before, places.Get(Id));
        m.Open(Id);
        Assert.False(m.IsMinimized(Id));
        Assert.Equal(Before, m.RectOf(Id));
    }

    [Fact]
    public void The_viewer_closing_keeps_the_place_from_before_minimizing()
    {
        var (m, places) = Open();
        m.SetMinimized(Id, true);
        m.Move(Id, 600, 100);
        m.SavePlaces();
        Assert.Equal(Before, places.Get(Id));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(3, 2, true)]
    [InlineData(30, 0, false)]
    [InlineData(0, -12, false)]
    public void A_press_and_release_that_hardly_travel_are_a_click(float dx, float dy, bool click)
    {
        var (m, _) = Open();
        var drag = new FloaterDrag();
        drag.Begin(m, Id, ResizeEdges.None, 300, 70);
        drag.Track(m, Id, 300 + dx, 70 + dy, buttonHeld: true);
        Assert.Equal(click, drag.End());
    }

    [Fact]
    public void A_drag_that_went_out_and_came_back_is_not_a_click()
    {
        var (m, _) = Open();
        var drag = new FloaterDrag();
        drag.Begin(m, Id, ResizeEdges.None, 300, 70);
        drag.Track(m, Id, 360, 90, buttonHeld: true);
        drag.Track(m, Id, 300, 70, buttonHeld: true);
        Assert.False(drag.End());
    }

    [Fact]
    public void A_release_with_no_press_is_not_a_click() => Assert.False(new FloaterDrag().End());
}
