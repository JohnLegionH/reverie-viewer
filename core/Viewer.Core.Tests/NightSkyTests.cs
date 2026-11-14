// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The night and sunrise skies against values computed offline in double precision by an independent script from the official
/// shader text (the sky vertex and fragment shaders for the dome, the atmosphere and soften-light shaders for a lit white face).
/// The official light colours the lit-surface shaders receive are the frame's sunlight colour for the sun and the same colour for
/// the moon (the moon carries no brightness factor there; the brightness only drives the sky's glow), each scaled so its largest
/// channel is at most 1. Frames: the official Midnight sky (moon overhead) and the legacy 6 AM sky (sun 5.4 degrees up).
/// Tolerance 0.005 on displayed (dome) or linear (lit face) values.
/// </summary>
public class NightSkyTests
{
    private static SkyFrame Midnight => new(0f, -Vector3.UnitZ, new(0.34877f, 0.35574f, 0.66f), Vector3.UnitZ, 0.5f, new(0.20405f, 0.24247f, 0.33f),
        new(0.24f, 0.24f, 0.24f), new(0.45f, 0.45f, 0.45f), 0f, 0f, 0.0003f, 1f, 906.2f, 0.27f, new Vector3(5f, 0.001f, -0.48f));

    private static SkyFrame Sunrise
    {
        get
        {
            float s = MathF.Asin(0.094108224f);
            return new(0.25f, new(MathF.Cos(s), 0f, MathF.Sin(s)), new(2.37f, 2.37f, 2.37f), new(-MathF.Cos(s), 0f, -MathF.Sin(s)), 0.5f, new(0.81f, 0.46289783f, 0.63f),
                new(0.20673196f, 0.40988314f, 0.48f), new(0.1579318f, 0.4349957f, 0.87f), 0.16f, 0.54f, 0.00062f, 2.7f, 563f, 0.27f, new Vector3(5.000999f, 0.001f, -0.480001f));
        }
    }

    private static void Near(float[] want, Vector3 got, string what, float tol = 0.005f)
    {
        float[] g = [got.X, got.Y, got.Z];
        for (int i = 0; i < 3; i++) Assert.True(Math.Abs(g[i] - want[i]) <= tol, $"{what}[{i}]: want {want[i]}, got {g[i]}");
    }

    [Fact]
    public void The_moon_throws_the_sun_colour_with_no_brightness_factor_and_no_channel_above_one()
    {
        Assert.Equal(Midnight.SunColor, SkyLighting.MoonLight(Midnight));
        var bright = SkyLighting.MoonLight(Sunrise);
        Assert.Equal(1f, MathF.Max(bright.X, MathF.Max(bright.Y, bright.Z)), 4);
    }

    [Fact]
    public void The_sun_light_a_surface_gets_has_no_channel_above_one()
    {
        var s = SkyLighting.SunLight(Sunrise);
        Assert.Equal(1f, MathF.Max(s.X, MathF.Max(s.Y, s.Z)), 4);
        Assert.Equal(Midnight.SunColor, SkyLighting.SunLight(Midnight));
    }

    [Fact]
    public void With_neither_the_sun_nor_the_moon_up_no_light_reaches_a_surface_but_the_ambient()
    {
        var both = Midnight with { MoonDirection = new Vector3(0.6f, 0f, -0.8f) };
        var dark = both with { SunColor = Vector3.Zero };
        Near([SkyLighting.WhiteFace(dark, 1f).X, SkyLighting.WhiteFace(dark, 1f).Y, SkyLighting.WhiteFace(dark, 1f).Z], SkyLighting.WhiteFace(both, 1f), "no light below both horizons", 0.0001f);
        Assert.True(SkyLighting.WhiteFace(both, 1f).Z < 0.1f, "the ambient face only");
    }

    [Theory]
    [InlineData(0.0f, 0.0298f, 0.0353f, 0.04967f)]
    [InlineData(0.5f, 0.12399f, 0.13855f, 0.35279f)]
    [InlineData(1.0f, 0.16727f, 0.18329f, 0.53827f)]
    public void A_white_face_at_midnight_gets_the_official_moonlight(float cosine, float r, float g, float b)
        => Near([r, g, b], SkyLighting.WhiteFace(Midnight, cosine), $"midnight face {cosine}");

    [Theory]
    [InlineData(0.0f, 0.17585f, 0.07647f, 0.11892f)]
    [InlineData(0.5f, 0.38946f, 0.11246f, 0.11422f)]
    [InlineData(1.0f, 0.41264f, 0.09995f, 0.07767f)]
    public void A_white_face_at_sunrise_gets_the_official_light_with_the_sun_colour_capped_at_one(float cosine, float r, float g, float b)
        => Near([r, g, b], SkyLighting.WhiteFace(Sunrise, cosine), $"sunrise face {cosine}");

    // Dome values: elevation, angle from the light, official displayed value.
    [Theory]
    [InlineData(4f, 0f, 0.18572f, 0.20261f, 0.30896f)]
    [InlineData(10f, 90f, 0.12138f, 0.13259f, 0.20419f)]
    [InlineData(20f, 180f, 0.07634f, 0.08334f, 0.12851f)]
    [InlineData(37f, 0f, 0.04945f, 0.0539f, 0.08291f)]
    [InlineData(60f, 90f, 0.03718f, 0.04067f, 0.0624f)]
    public void The_midnight_dome_matches_the_official_values(float elevation, float azimuth, float r, float g, float b)
        => Near([r, g, b], SkyLighting.DomeShown(Midnight, elevation, azimuth), $"midnight dome {elevation}@{azimuth}");

    [Theory]
    [InlineData(20f, 0f, 0.87417f, 0.98841f, 1f)]
    [InlineData(37f, 0f, 0.43643f, 0.64906f, 0.95995f)]
    [InlineData(10f, 90f, 0.56926f, 0.7317f, 0.881f)]
    [InlineData(37f, 90f, 0.32178f, 0.55743f, 0.89513f)]
    [InlineData(20f, 180f, 0.41139f, 0.66362f, 0.95748f)]
    [InlineData(60f, 180f, 0.24073f, 0.45136f, 0.78196f)]
    public void The_sunrise_dome_matches_the_official_values(float elevation, float azimuth, float r, float g, float b)
        => Near([r, g, b], SkyLighting.DomeShown(Sunrise, elevation, azimuth), $"sunrise dome {elevation}@{azimuth}");
}
