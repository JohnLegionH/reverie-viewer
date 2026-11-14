// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Meshing;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Prims are built at the detail of their level, as the official viewer builds a volume (llvolumemgr.cpp 41 the four detail scales; llvolume.cpp: a circle
/// profile has floor(6 x detail) sides, 760-940; a circle path floor(floor(6 x detail + twist x 3.5 x (detail - 0.5)) x revolutions) points, 1418-1440; a line
/// path floor(twist x 3.5 x (detail - 0.5)) + 2 points; the radius of the level-of-detail rule scales each axis by 0.5, by 0.6 on x and y for a cylinder
/// and on all three for a circle path, 2055-2071). Pure, runs in parallel.
/// </summary>
public class PrimLodTests
{
    private static readonly float[] Detail = [1f, 1.5f, 2.5f, 4f];
    private static readonly PrimShape Cylinder = new() { ProfileCurve = 0, PathCurve = 16 };
    private static readonly PrimShape Torus = new() { ProfileCurve = 0, PathCurve = 32, PathScaleX = 1, PathScaleY = 0.25 };
    private static readonly PrimShape TwistedBox = new() { ProfileCurve = 1, PathCurve = 16, PathTwist = 1, PathTwistBegin = 0 };

    [Fact]
    public void The_four_levels_are_built_at_the_official_detail_scales() => Assert.Equal(Detail, PrimLodGroup.Scales.ToArray());

    [Fact]
    public void The_radius_scales_each_axis_by_the_volumes_bias()
    {
        var s = new Vector3(2, 4, 8);
        Assert.Equal(new Vector3(1, 2, 4).Length(), PrimLodGroup.Radius(new PrimShape { ProfileCurve = 1, PathCurve = 16 }, s), 4);   // a box: 0.5
        Assert.Equal(new Vector3(1.2f, 2.4f, 0).Length(), PrimLodGroup.Radius(Cylinder, s), 4);                                         // a cylinder: 0.6 on x and y, z ignored
        Assert.Equal(new Vector3(1.2f, 2.4f, 4.8f).Length(), PrimLodGroup.Radius(Torus, s), 4);                                         // a circle path: 0.6 on all three
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_circle_profile_has_floor_of_six_times_the_detail_sides(int level)
    {
        // a cylinder's cap has three vertices a side
        var faces = PrimGeometry.Build(Cylinder, Detail[level]);
        Assert.Equal(3 * (int)Math.Floor(6 * Detail[level]), faces[0].Positions.Length / 3);
    }

    [Theory]
    [InlineData(0, 3)]    // floor(1 x 3.5 x 0.5) + 2
    [InlineData(1, 5)]    // floor(3.5 x 1.0) + 2
    [InlineData(2, 9)]    // floor(3.5 x 2.0) + 2
    [InlineData(3, 14)]   // floor(3.5 x 3.5) + 2
    public void A_line_path_has_points_for_its_twist_and_detail(int level, int points)
    {
        var side = PrimGeometry.Build(TwistedBox, Detail[level])[1];
        var heights = Enumerable.Range(0, side.Positions.Length / 3).Select(i => MathF.Round(side.Positions[i * 3 + 2], 3)).Distinct().Count();
        Assert.Equal(points, heights);
    }

    [Fact]
    public void A_torus_gets_finer_with_each_level_and_the_old_three_levels_are_the_same_detail()
    {
        int Tris(float d) => PrimGeometry.Build(Torus, d).Sum(f => f.Indices.Length / 3);
        Assert.True(Tris(1f) < Tris(1.5f) && Tris(1.5f) < Tris(2.5f) && Tris(2.5f) < Tris(4f));
        Assert.Equal(PrimGeometry.Build(Torus, PrimDetail.Low).Sum(f => f.Indices.Length / 3), Tris(1f));
        Assert.Equal(PrimGeometry.Build(Torus, PrimDetail.High).Sum(f => f.Indices.Length / 3), Tris(4f));
    }
}
