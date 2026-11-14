// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The search for bare, level land well above the water for a live ground check. Pure, parallel.</summary>
public class PlainGroundTests
{
    private const float Water = 20f, Size = 256f;

    [Fact]
    public void The_nearest_dry_level_place_with_nothing_within_the_radius_is_chosen()
    {
        float? Flat(float x, float y) => 25f;
        var p = PlainGround.Find(Flat, Water, Size, [new Vector3(100, 100, 25)], 1f, 3f, 100f, 100f);
        Assert.NotNull(p);
        float d = Vector2.Distance(new Vector2(p!.Value.X, p.Value.Y), new Vector2(100, 100));
        Assert.True(d >= 3f && d < 5f, $"chosen {d:F2} m from the object (wanted the nearest place at least 3 m away)");
        Assert.Equal(25f, p.Value.Z);
    }

    [Fact]
    public void Land_under_a_metre_above_the_water_or_with_a_slope_is_not_chosen_and_none_is_found_when_nothing_fits()
    {
        float? Low(float x, float y) => Water + 0.5f;
        Assert.Null(PlainGround.Find(Low, Water, Size, [], 1f, 3f, 100f, 100f));
        float? Slope(float x, float y) => 25f + x * 0.5f;   // half a metre per metre eastward
        Assert.Null(PlainGround.Find(Slope, Water, Size, [], 1f, 3f, 100f, 100f));
        float? Missing(float x, float y) => null;            // no terrain has arrived
        Assert.Null(PlainGround.Find(Missing, Water, Size, [], 1f, 3f, 100f, 100f));
    }

    [Fact]
    public void A_place_is_taken_from_the_far_side_of_a_slope_when_only_that_side_is_level_and_high_enough()
    {
        float? Hill(float x, float y) => x < 150 ? Water + 0.5f : 30f;   // water-level shore west of 150, a plateau east of it
        var p = PlainGround.Find(Hill, Water, Size, [], 1f, 3f, 60f, 128f);
        Assert.NotNull(p);
        Assert.True(p!.Value.X >= 150f);
        Assert.Equal(30f, p.Value.Z);
    }
}

/// <summary>The search also says why places were turned down, so a run that finds nothing shows which rule rejected them.</summary>
public class PlainGroundSurveyTests
{
    private const float Water = 20f, Size = 256f;

    [Fact]
    public void Each_place_turned_down_is_counted_under_the_rule_that_turned_it_down()
    {
        float? Low(float x, float y) => Water + 0.5f;
        var low = PlainGround.Search(Low, Water, Size, [], 1f, 3f, 100f, 100f);
        Assert.Null(low.Found);
        Assert.True(low.Cells > 0);
        Assert.Equal(low.Cells, low.TooLow);
        Assert.Equal(0, low.NotLevel + low.ObjectNear + low.NoTerrain);

        float? Slope(float x, float y) => 25f + x * 0.5f;
        var slope = PlainGround.Search(Slope, Water, Size, [], 1f, 3f, 100f, 100f);
        Assert.Equal(slope.Cells, slope.NotLevel);

        float? Missing(float x, float y) => null;
        var missing = PlainGround.Search(Missing, Water, Size, [], 1f, 3f, 100f, 100f);
        Assert.Equal(missing.Cells, missing.NoTerrain);

        float? Flat(float x, float y) => 25f;
        var crowded = PlainGround.Search(Flat, Water, Size, [new Vector3(100, 100, 25)], 1f, 3f, 100f, 100f);
        Assert.NotNull(crowded.Found);
        Assert.True(crowded.ObjectNear > 0);
        Assert.True(crowded.Plain > 0);
        Assert.Equal(crowded.Cells, crowded.NoTerrain + crowded.TooLow + crowded.NotLevel + crowded.ObjectNear + crowded.Plain);   // every place is counted once
    }

    [Fact]
    public void A_region_with_no_dry_cell_is_reported_as_such_and_the_best_of_several_regions_is_the_nearest_found()
    {
        float? Low(float x, float y) => Water + 0.5f;
        float? Plateau(float x, float y) => 30f;
        var a = PlainGround.Search(Low, Water, Size, [], 1f, 3f, 128f, 128f);
        var b = PlainGround.Search(Plateau, Water, Size, [], 1f, 3f, 128f, 128f);
        Assert.Null(a.Found);
        Assert.NotNull(b.Found);
        Assert.Equal(30f, b.Found!.Value.Z);
    }
}
