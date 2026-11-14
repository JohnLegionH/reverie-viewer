// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The tone-mapped path of a sky that sets a reflection probe ambiance (the official post pass). The expected numbers were worked out outside the code
/// under test, by transcribing the official shader's functions and evaluating them in double precision.
/// </summary>
public class ToneMapTests
{
    private static void Near(float r, float g, float b, Vector3 v, int digits = 4)
    {
        Assert.Equal(r, v.X, digits); Assert.Equal(g, v.Y, digits); Assert.Equal(b, v.Z, digits);
    }

    [Theory]
    [InlineData(0.18f, 0.18f, 0.18f, 0.10559f, 0.10559f, 0.10559f)]
    [InlineData(1f, 1f, 1f, 0.61912f, 0.61912f, 0.61912f)]
    [InlineData(2f, 2f, 2f, 0.80356f, 0.80356f, 0.80356f)]
    [InlineData(0.5f, 0.2f, 0.1f, 0.36523f, 0.12881f, 0.05364f)]
    [InlineData(3f, 1.5f, 0.3f, 0.90814f, 0.74677f, 0.37278f)]
    [InlineData(0f, 0f, 0f, 0f, 0f, 0f)]
    public void The_aces_fit_by_hill_maps_a_colour_as_the_official_shader_does(float r, float g, float b, float er, float eg, float eb) =>
        Near(er, eg, eb, ToneMap.AcesHill(new(r, g, b)));

    [Theory]
    [InlineData(0.18f, 0.18f, 0.18f, 0.14f, 0.14f, 0.14f)]
    [InlineData(1f, 1f, 1f, 0.86909f, 0.86909f, 0.86909f)]
    [InlineData(2f, 2f, 2f, 0.96f, 0.96f, 0.96f)]
    [InlineData(0.5f, 0.2f, 0.1f, 0.46f, 0.16f, 0.06f)]
    [InlineData(3f, 1.5f, 0.3f, 0.97639f, 0.59506f, 0.29f)]
    [InlineData(0.02f, 0.05f, 0.01f, 0.01063f, 0.04063f, 0.00063f)]
    public void The_neutral_tone_mapper_maps_a_colour_as_the_official_shader_does(float r, float g, float b, float er, float eg, float eb) =>
        Near(er, eg, eb, ToneMap.PbrNeutral(new(r, g, b)));

    [Theory]
    // (colour, exposure, type, mix) -> result
    [InlineData(2f, 2f, 2f, 1f, 1, 0.7f, 1f, 1f, 1f)]
    [InlineData(0.5f, 0.2f, 0.1f, 1f, 1, 0.7f, 0.40565872f, 0.15016432f, 0.06754613f)]
    [InlineData(3f, 1.5f, 0.3f, 1f, 1, 0.7f, 1f, 0.97273641f, 0.35094861f)]
    [InlineData(0.5f, 0.2f, 0.1f, 2f, 1, 0.7f, 0.75181305f, 0.33965251f, 0.16817227f)]
    [InlineData(3f, 1.5f, 0.3f, 1f, 0, 0.7f, 1f, 0.86654297f, 0.29299702f)]
    [InlineData(0.5f, 0.2f, 0.1f, 1f, 1, 0f, 0.5f, 0.2f, 0.1f)]
    [InlineData(0.5f, 0.2f, 0.1f, 1f, 1, 1f, 0.36522674f, 0.12880618f, 0.05363732f)]
    [InlineData(4f, 4f, 4f, 1f, 1, 0.7f, 1f, 1f, 1f)]
    public void The_post_pass_scales_maps_mixes_and_clamps(float r, float g, float b, float exposure, int type, float mix, float er, float eg, float eb) =>
        Near(er, eg, eb, ToneMap.Apply(new(r, g, b), exposure, (ToneMapType)type, mix));

    [Fact]
    public void The_defaults_are_the_official_ones()
    {
        Assert.Equal(1f, ToneMap.DefaultExposure);
        Assert.Equal(0.7f, ToneMap.DefaultMix);
        Assert.Equal(ToneMapType.AcesHill, ToneMap.DefaultType);
    }

    [Theory]
    [InlineData(1f, 2f)]
    [InlineData(2.25f, 3f)]
    [InlineData(4f, 4f)]
    public void The_sky_scale_is_twice_the_square_root_of_the_gamma(float gamma, float expected) =>
        Assert.Equal(expected, ToneMap.HdrScale(gamma), 4);

    [Theory]
    [InlineData(0f, false)]       // a legacy sky: the classic path
    [InlineData(0.01f, true)]
    [InlineData(1f, true)]        // a sky that sets a probe ambiance
    [InlineData(2.5f, true)]
    public void A_sky_takes_the_tone_mapped_path_when_it_sets_a_probe_ambiance(float ambiance, bool hdr) =>
        Assert.Equal(hdr, ColourPath.IsHdr(ambiance));

    [Fact]
    public void The_auto_adjust_setting_puts_a_legacy_sky_on_the_tone_mapped_path_too()
    {
        Assert.False(ColourPath.IsHdr(0f, autoAdjustLegacy: false));
        Assert.True(ColourPath.IsHdr(0f, autoAdjustLegacy: true));
    }

    [Fact]
    public void The_mix_is_the_setting_on_the_tone_mapped_path_and_zero_on_the_classic_one()
    {
        Assert.Equal(0.7f, ColourPath.TonemapMix(1f), 5);
        Assert.Equal(0.4f, ColourPath.TonemapMix(1f, 0.4f), 5);
        Assert.Equal(0f, ColourPath.TonemapMix(0f), 5);
        Assert.Equal(0.7f, ColourPath.TonemapMix(0f, 0.7f, autoAdjustLegacy: true), 5);
    }
}
