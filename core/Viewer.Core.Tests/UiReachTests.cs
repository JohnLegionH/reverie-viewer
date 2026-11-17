// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The live checks' rule for a control found by name: pressed only when wholly shown, reported cut when a scroll area or the window
/// cuts it, and not shown when nothing of it is in view (UiReach, used by the automation's ui command and the colour window's render case).
/// </summary>
public sealed class UiReachTests
{
    private static readonly UiRect Window = new(0, 0, 1152, 648);

    [Fact]
    public void A_control_inside_every_area_is_visible_and_pressed_in_its_middle()
    {
        var r = UiReach.Classify(new UiRect(100, 200, 80, 24), [Window, new UiRect(50, 150, 300, 300)]);
        Assert.Equal(UiReachKind.Visible, r.Kind);
        Assert.Equal(new UiRect(100, 200, 80, 24), r.Shown);
        Assert.Equal((140f, 212f), r.Point);
    }

    [Fact]
    public void A_control_below_its_scroll_area_is_outside_and_has_no_point()
    {
        // the colour window's OK at the 1152x648 test window: below the bottom of the scrolled content
        var r = UiReach.Classify(new UiRect(200, 600, 60, 24), [Window, new UiRect(150, 100, 320, 480)]);
        Assert.Equal(UiReachKind.Outside, r.Kind);
        Assert.Null(r.Point);
    }

    [Fact]
    public void A_control_cut_by_the_scroll_area_is_clipped_and_its_shown_part_is_named()
    {
        var r = UiReach.Classify(new UiRect(200, 570, 60, 24), [Window, new UiRect(150, 100, 320, 480)]);
        Assert.Equal(UiReachKind.Clipped, r.Kind);
        Assert.Equal(new UiRect(200, 570, 60, 10), r.Shown);
        Assert.Equal((230f, 575f), r.Point);
    }

    [Fact]
    public void Every_area_counts_not_only_the_nearest()
    {
        // inside the scroll area, but the scroll area itself runs past the window's bottom edge
        var r = UiReach.Classify(new UiRect(200, 640, 60, 24), [Window, new UiRect(150, 100, 320, 700)]);
        Assert.Equal(UiReachKind.Clipped, r.Kind);
        Assert.Equal(new UiRect(200, 640, 60, 8), r.Shown);
    }

    [Theory]
    [InlineData(100.3f, 200.7f, 80.1f, 24.3f)]
    [InlineData(312.6667f, 553.3333f, 30.6667f, 29.3333f)]   // a control's place at 150 % interface size
    [InlineData(0.1f, 0.7f, 1151.8f, 647.2f)]
    public void A_control_wholly_inside_is_visible_whatever_fractions_its_edges_have(float x, float y, float w, float h)
    {
        var control = new UiRect(x, y, w, h);
        var r = UiReach.Classify(control, [Window, new UiRect(0, 0, 1152, 648)]);
        Assert.Equal(UiReachKind.Visible, r.Kind);
        Assert.Equal(control, r.Shown);
    }

    [Fact]
    public void A_control_of_no_size_is_never_visible()
    {
        var r = UiReach.Classify(new UiRect(100, 100, 0, 0), [Window]);
        Assert.Equal(UiReachKind.Outside, r.Kind);
        Assert.Null(r.Point);
    }
}
