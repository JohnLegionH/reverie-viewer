// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Each region's water at its own height over its own extent; the void around the regions at the anchor region's height.</summary>
public class WaterPlanTests
{
    private const float Reach = 4096f;
    private static readonly WaterRect Home = new(0, 0, 256, 256, 20f);

    private static float HeightAt(IReadOnlyList<WaterRect> plan, float x, float y) =>
        plan.Where(r => x >= r.X0 && x < r.X1 && y >= r.Y0 && y < r.Y1).Select(r => r.Height).DefaultIfEmpty(float.NaN).First();

    [Fact]
    public void The_plan_covers_the_whole_square_once()
    {
        var plan = WaterPlan.Build(20f, 128, 128, Reach, [Home, new(256, 0, 512, 256, 26f)]);
        Assert.Equal((2 * Reach) * (2 * Reach), plan.Sum(r => (double)r.Area), 1.0);
        foreach (var (x, y) in new[] { (-3900f, -3900f), (128f, 128f), (300f, 100f), (4000f, 4000f), (-1f, 255f), (256f, 0f) })
            Assert.Equal(1, plan.Count(r => x >= r.X0 && x < r.X1 && y >= r.Y0 && y < r.Y1));
    }

    [Fact]
    public void A_neighbour_has_its_own_height_over_its_own_extent()
    {
        var plan = WaterPlan.Build(20f, 128, 128, Reach, [Home, new(256, 0, 512, 256, 26f)]);
        Assert.Equal(20f, HeightAt(plan, 100, 100));
        Assert.Equal(26f, HeightAt(plan, 300, 100));
        Assert.Equal(26f, HeightAt(plan, 511, 255));
    }

    [Fact]
    public void The_void_and_the_gaps_between_regions_take_the_anchors_height()
    {
        // a neighbour to the north-east with a gap to the north-west
        var plan = WaterPlan.Build(20f, 128, 128, Reach, [Home, new(256, 256, 512, 512, 31f)]);
        Assert.Equal(20f, HeightAt(plan, 100, 400));    // the gap above the home region
        Assert.Equal(20f, HeightAt(plan, 3000, 3000));  // far away
        Assert.Equal(31f, HeightAt(plan, 400, 400));
        Assert.Equal(20f, HeightAt(plan, 400, 100));    // the gap to the east of the home region, below the neighbour
    }

    [Fact]
    public void A_large_neighbour_covers_all_its_cells()
    {
        var plan = WaterPlan.Build(20f, 128, 128, Reach, [Home, new(256, 0, 1280, 1024, 12f)]);
        Assert.Equal(12f, HeightAt(plan, 300, 10));
        Assert.Equal(12f, HeightAt(plan, 1279, 1023));
        Assert.Equal(12f, HeightAt(plan, 800, 600));
        Assert.Equal(20f, HeightAt(plan, 1300, 600));
    }

    [Fact]
    public void With_only_the_anchor_region_it_is_one_height_everywhere()
    {
        var plan = WaterPlan.Build(20f, 128, 128, Reach, [Home]);
        Assert.All(plan, r => Assert.Equal(20f, r.Height));
        Assert.Equal((2 * Reach) * (2 * Reach), plan.Sum(r => (double)r.Area), 1.0);
    }
}
