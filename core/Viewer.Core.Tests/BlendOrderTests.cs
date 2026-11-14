// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The order blended faces are drawn in (<see cref="BlendOrder"/>), for set cameras and face positions (SL axes, z up).
/// The expected orders are worked from the official viewer's rules: each face by its distance along the view axis to its
/// box's centre less a quarter of the box along that axis; the water between the far side and the camera's side.
/// </summary>
public class BlendOrderTests
{
    private static readonly Vector3 Eye = new(40, 128, 30);
    private static readonly Vector3 East = Vector3.UnitX;

    private static float Key(Vector3 centre, Vector3 size) => BlendOrder.Key(Eye, East, centre, size);

    [Fact]
    public void A_wide_near_pane_off_to_the_side_is_drawn_after_a_far_pane_straight_ahead()
    {
        // the near pane's centre is farther from the eye than the far pane's (14.1 m against 13.3 m), but it is nearer
        // along the view: the far pane first, the near pane over it
        var near = Key(new Vector3(50, 118, 33), new Vector3(0.05f, 16, 4));
        var far = Key(new Vector3(53, 125, 33), new Vector3(0.05f, 4, 3));
        Assert.Equal([1, 0], BlendOrder.Order([near, far]));
        Assert.Equal(10f - 0.0125f, near, 4);
        Assert.Equal(13f - 0.0125f, far, 4);
    }

    [Fact]
    public void A_pane_inside_a_box_is_drawn_before_the_box_face_in_front_of_it()
    {
        // a 4 m box centred 14 m away: its near face (centre 12 m away) and the pane half a metre in front of its centre
        var front = Key(new Vector3(52, 134, 33), new Vector3(0, 4, 4));
        var pane = Key(new Vector3(53.5f, 134, 33), new Vector3(0.05f, 2, 2));
        Assert.Equal([1, 0], BlendOrder.Order([front, pane]));
    }

    [Fact]
    public void Two_blended_faces_of_one_object_are_ordered_by_their_own_boxes()
    {
        // a hollow box: its outer near face, and the inner face across the hollow (each face sorted on its own)
        var outer = Key(new Vector3(52, 122, 26), new Vector3(0, 4, 4));
        var inner = Key(new Vector3(55, 122, 26), new Vector3(0, 2, 2));
        Assert.Equal([1, 0], BlendOrder.Order([outer, inner]));
    }

    [Fact]
    public void Distance_is_along_the_view_axis_and_a_long_face_counts_nearer_than_its_centre()
    {
        // two faces at the same depth along the view, one far off to the side: the same key
        Assert.Equal(Key(new Vector3(50, 128, 30), new Vector3(0, 1, 1)), Key(new Vector3(50, 150, 30), new Vector3(0, 1, 1)), 4);
        // a floor 20 m long along the view centred 10 m away counts 5 m away: a pane 7 m away is drawn before it
        var floor = Key(new Vector3(50, 128, 28), new Vector3(20, 4, 0));
        var pane = Key(new Vector3(47, 128, 29), new Vector3(0.05f, 1, 1));
        Assert.Equal(5f, floor, 4);
        Assert.Equal([1, 0], BlendOrder.Order([floor, pane]));
        // looking diagonally the quarter is taken per axis: size times the axis component squared
        var diag = Vector3.Normalize(new Vector3(1, 1, 0));
        Assert.Equal(Vector3.Dot(new Vector3(10, 10, 0), diag) - 0.25f * (4 * 0.5f + 8 * 0.5f), BlendOrder.Key(Eye, diag, Eye + new Vector3(10, 10, 0), new Vector3(4, 8, 2)), 4);
    }

    [Fact]
    public void The_water_divides_the_order_seen_from_above()
    {
        const float water = 20f;
        Assert.Equal((true, false), BlendOrder.Sides(21, 23, water));
        Assert.Equal((false, true), BlendOrder.Sides(16, 19, water));
        Assert.Equal((true, true), BlendOrder.Sides(18.5f, 21.5f, water));   // crossing: drawn on both sides
        Assert.Equal((true, false), BlendOrder.Sides(16, 19, null));          // no water: once, as any face
        // the eye above the water: a face below it is drawn before the water even when nearer than a face above
        float below = BlendOrder.Depth(6, BlendOrder.Side.Below, eyeAbove: true);
        float above = BlendOrder.Depth(30, BlendOrder.Side.Above, eyeAbove: true);
        Assert.Equal([0, 2, 1], BlendOrder.Order([below, above, BlendOrder.WaterDepth]));
        // a crossing face: its part below before the water, its part above after it
        var parts = new[] { BlendOrder.Depth(8, BlendOrder.Side.Above, true), BlendOrder.Depth(8, BlendOrder.Side.Below, true), BlendOrder.WaterDepth };
        Assert.Equal([1, 2, 0], BlendOrder.Order(parts));
    }

    [Fact]
    public void Seen_from_under_the_water_the_faces_above_it_are_drawn_first()
    {
        float below = BlendOrder.Depth(30, BlendOrder.Side.Below, eyeAbove: false);
        float above = BlendOrder.Depth(6, BlendOrder.Side.Above, eyeAbove: false);
        Assert.Equal([1, 2, 0], BlendOrder.Order([below, above, BlendOrder.WaterDepth]));
    }

    [Fact]
    public void Faces_at_any_distance_in_view_stay_on_their_side_of_the_water()
    {
        // the farthest a face is drawn (beyond any draw distance) is still nearer than the water
        Assert.True(BlendOrder.Depth(1024, BlendOrder.Side.Above, true) < BlendOrder.WaterDepth);
        Assert.True(BlendOrder.Depth(-50, BlendOrder.Side.Below, true) > BlendOrder.WaterDepth);
    }

    [Fact]
    public void Equal_depths_keep_their_given_order()
    {
        Assert.Equal([0, 1, 2], BlendOrder.Order([5f, 5f, 5f]));
        Assert.Equal([2, 0, 1], BlendOrder.Order([5f, 5f, 9f]));
    }
}
