// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The sphere-in-view test (<see cref="ViewFrustum"/>), for a view at the origin looking along +x, y to the left, z up,
/// 90 degrees across (half-tangent 1) both ways, near 0.1 and far 100.</summary>
public class ViewFrustumTests
{
    private static bool Visible(Vector3 centre, float radius, float margin = 0.1f) =>
        ViewFrustum.SphereVisible(Vector3.Zero, Vector3.UnitX, -Vector3.UnitY, Vector3.UnitZ, 1f, 1f, 0.1f, 100f, centre, radius, margin);

    [Fact]
    public void A_sphere_straight_ahead_is_visible_and_one_behind_or_beyond_the_far_distance_is_not()
    {
        Assert.True(Visible(new Vector3(10, 0, 0), 1f));
        Assert.False(Visible(new Vector3(-10, 0, 0), 1f));
        Assert.False(Visible(new Vector3(200, 0, 0), 1f));
        Assert.True(Visible(new Vector3(100.5f, 0, 0), 1f));   // its near edge is inside the far distance
    }

    [Fact]
    public void A_sphere_is_visible_while_any_of_it_is_inside_the_sides_and_a_little_beyond_them()
    {
        // at 10 m the view is 10 m to each side
        Assert.True(Visible(new Vector3(10, 9, 0), 0.5f));
        Assert.True(Visible(new Vector3(10, 10.5f, 0), 1f));            // straddling the edge
        Assert.True(Visible(new Vector3(10, 10.8f, 0), 0.2f));          // in the margin outside the edge (10 percent: 11 m)
        Assert.False(Visible(new Vector3(10, 14, 0), 1f));
        Assert.False(Visible(new Vector3(10, 10.8f, 0), 0.2f, margin: 0f));
        Assert.False(Visible(new Vector3(10, 0, 14), 1f));              // above
        Assert.False(Visible(new Vector3(10, 0, -14), 1f));             // below
    }

    [Fact]
    public void A_sphere_around_the_eye_is_visible()
    {
        Assert.True(Visible(Vector3.Zero, 2f));
    }
}
