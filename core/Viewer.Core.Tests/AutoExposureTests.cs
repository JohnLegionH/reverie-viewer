// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The automatic exposure against the official rule (luminanceF.glsl, exposureF.glsl, LLPipeline::generateExposure): the range a sky gives it, the scale a steady scene settles at,
/// the speed it moves at, and what it does to a dark dusk picture. Each test builds its own values, so the class runs in parallel.</summary>
public class AutoExposureTests
{
    [Fact]
    public void A_classic_sky_never_changes_the_scale()
    {
        Assert.Equal(1f, AutoExposure.Settled(0f, 0f), 6);
        Assert.Equal(1f, AutoExposure.Settled(0.5f, 0f), 6);
        Assert.Equal((1f, 1f), AutoExposure.Range(0f));
    }

    [Fact]
    public void A_high_dynamic_range_sky_gives_a_range_of_one_over_its_scale_to_its_scale()
    {
        var (min, max) = AutoExposure.Range(0.5f, 1f);
        Assert.Equal(0.5f, min, 5);
        Assert.Equal(2f, max, 5);
        var (min4, max4) = AutoExposure.Range(0.5f, 4f);
        Assert.Equal(0.25f, min4, 5);
        Assert.Equal(4f, max4, 5);
    }

    [Fact]
    public void A_dark_scene_settles_at_the_top_of_the_range_and_a_bright_one_at_the_bottom()
    {
        Assert.Equal(2f, AutoExposure.Settled(0f, 0.5f), 5);
        Assert.Equal(0.5f, AutoExposure.Settled(0.175f, 0.5f), 5);
        Assert.Equal(0.5f, AutoExposure.Settled(3f, 0.5f), 5);   // brighter than the coefficient holds at the bottom
    }

    [Fact]
    public void A_middle_scene_settles_on_the_squared_curve()
    {
        // luminance 0.0875 is half the coefficient: squared 0.25, a quarter of the way from the top (2) to the bottom (0.5)
        Assert.Equal(2f + (0.5f - 2f) * 0.25f, AutoExposure.Settled(0.0875f, 0.5f), 5);
    }

    [Fact]
    public void The_scale_closes_nine_tenths_of_the_gap_in_the_speed_target_time()
    {
        float after = AutoExposure.Step(1f, 2f, AutoExposure.DefaultSpeedTarget);
        Assert.Equal(1.9f, after, 4);
        Assert.Equal(1f, AutoExposure.Step(1f, 2f, 0f), 6);
    }

    [Fact]
    public void The_luminance_weights_are_the_official_ones()
    {
        Assert.Equal(0.2126f, AutoExposure.Luminance(Vector3.UnitX), 6);
        Assert.Equal(0.7152f, AutoExposure.Luminance(Vector3.UnitY), 6);
        Assert.Equal(0.0722f, AutoExposure.Luminance(Vector3.UnitZ), 6);
    }

    [Fact]
    public void A_dusk_picture_is_brighter_than_the_unexposed_one_in_every_channel()
    {
        // a leaf at dusk: dim, green. Under the unexposed chain it lands near black; the exposure the dark scene settles at lifts it.
        var leaf = new Vector3(0.012f, 0.030f, 0.010f);
        float exposure = AutoExposure.Settled(AutoExposure.Luminance(leaf), 0.5f);
        Assert.True(exposure > 1.9f, $"a dark scene's exposure {exposure}");
        var plain = ToneMap.Apply(leaf, 1f, ToneMap.DefaultType, ToneMap.DefaultMix);
        var lifted = ToneMap.Apply(leaf, exposure, ToneMap.DefaultType, ToneMap.DefaultMix);
        Assert.True(lifted.X > plain.X && lifted.Y > plain.Y && lifted.Z > plain.Z, $"lifted {lifted} is not brighter than {plain}");
    }
}
