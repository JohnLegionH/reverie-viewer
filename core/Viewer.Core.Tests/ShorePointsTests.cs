// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Where a live look at the water can stand: on dry ground at a shore, and out at sea.</summary>
public class ShorePointsTests
{
    // a beach: the ground falls from 10 m at x = 0 to -10 m at x = 200 (0.1 m per metre), flat across y; water at 0 m
    private static float Beach(float x, float y) => 10f - x * 0.1f;

    [Fact]
    public void A_shore_point_is_dry_ground_just_above_the_water_with_deep_water_close_by()
    {
        var points = ShorePoints.Shores(Beach, 0f, 256f, 4);
        Assert.NotEmpty(points);
        foreach (var p in points)
        {
            Assert.InRange(p.Z, 0.3f, 2.5f);   // ground height: just above the water
            Assert.InRange(p.X, 80f, 97f);     // where the beach is a metre or two above the water
        }
    }

    [Fact]
    public void Shore_points_are_spread_apart()
    {
        var points = ShorePoints.Shores((x, y) => 10f - x * 0.1f + MathF.Sin(y / 20f), 0f, 256f, 4);
        Assert.True(points.Count >= 2, "there is more than one shore point to spread");
        for (int i = 0; i < points.Count; i++)
            for (int j = i + 1; j < points.Count; j++)
                Assert.True(MathF.Sqrt(MathF.Pow(points[i].X - points[j].X, 2) + MathF.Pow(points[i].Y - points[j].Y, 2)) >= 30f);
    }

    [Fact]
    public void A_sea_point_is_over_water_at_least_three_metres_deep()
    {
        var p = ShorePoints.Sea(Beach, 0f, 256f);
        Assert.NotNull(p);
        Assert.True(Beach(p!.Value.X, p.Value.Y) <= -3f);
    }

    [Fact]
    public void A_region_with_no_shore_gives_none()
    {
        Assert.Empty(ShorePoints.Shores((x, y) => 30f, 0f, 256f, 4));
        Assert.Null(ShorePoints.Sea((x, y) => 30f, 0f, 256f));
    }
}
