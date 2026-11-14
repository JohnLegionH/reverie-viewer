// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The ground under an avatar, as the official viewer finds it (LLWorld::resolveStepHeightGlobal): the land, or the avatar's foot plane from the
/// region when that is hit first. Pure arithmetic, parallel.
/// </summary>
public class OfficialGroundTests
{
    private static readonly Vector3 Soles = new(100, 100, 30f);
    private static Vector4 Floor(float z) => new(0, 0, 1, z);

    [Fact]
    public void Land_alone_is_the_ground_when_the_region_sent_no_plane()
    {
        Assert.Equal(24.15f, OfficialGround.HeightUnder(Soles, 24.15f, default));
    }

    [Fact]
    public void A_floor_above_the_land_is_the_ground_less_the_physics_allowance()
    {
        // the plane is hit before the land; the official distance carries 0.05 m for the error in the reported plane
        Assert.Equal(30f - OfficialGround.PlaneAllowance, OfficialGround.HeightUnder(Soles, 24.15f, Floor(30f))!.Value, 4);
        Assert.Equal(24.65f - OfficialGround.PlaneAllowance, OfficialGround.HeightUnder(new(100, 100, 24.65f), 24.15f, Floor(24.65f))!.Value, 4);
    }

    [Fact]
    public void A_plane_below_the_land_does_not_hide_the_land()
    {
        Assert.Equal(24.15f, OfficialGround.HeightUnder(Soles, 24.15f, Floor(20f)));
    }

    [Fact]
    public void A_plane_alone_is_the_ground_where_the_land_is_unknown_and_nothing_is_unknown_without_either()
    {
        var onPlane = OfficialGround.HeightUnder(Soles, null, Floor(30f));
        Assert.True(onPlane.HasValue, "a foot plane alone is a ground");
        Assert.Equal(30f - OfficialGround.PlaneAllowance, onPlane!.Value, 4);
        Assert.Null(OfficialGround.HeightUnder(Soles, null, default));
    }

    [Fact]
    public void A_sloping_plane_is_read_at_the_point()
    {
        // a ramp rising 0.5 m a metre eastward; normal (-0.4472, 0, 0.8944) through (100, 100, 30): n.p = w
        var n = Vector3.Normalize(new Vector3(-0.5f, 0, 1));
        var plane = new Vector4(n, Vector3.Dot(n, new Vector3(100, 100, 30)));
        float ground = OfficialGround.HeightUnder(new(100, 100, 30.4f), 24.15f, plane)!.Value;
        // the official distance is measured along the normal's dot product with the point, so a point 0.4 m above the plane's height is 0.4 * n.z away
        Assert.Equal(30.4f - (0.4f * n.Z + OfficialGround.PlaneAllowance), ground, 3);
    }
}
