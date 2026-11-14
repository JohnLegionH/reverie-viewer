// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The haze of the world against the official atmospherics (class1/windlight/atmosphericsFuncs.glsl, calcAtmosphericVars) and the haze pass that applies it
/// (class3/deferred/hazeF.glsl, blended as in pipeline.cpp: source times one plus destination times the source alpha, the alpha being the RED channel survival).
/// Each test builds its own values, so the class runs in parallel.</summary>
public class OfficialHazeTests
{
    private static SkyFrame Frame(Vector3 blueHorizon, Vector3 blueDensity, float hazeHorizon, float hazeDensity, Vector3 ambient, Vector3 sun, float densityMultiplier = 0.0001f, float distanceMultiplier = 0.8f, Vector3? sunDir = null) =>
        new(0.5f, sunDir ?? Vector3.Normalize(new Vector3(1f, 0f, 1f)), sun, -Vector3.UnitZ, 0.5f, ambient, blueHorizon, blueDensity, hazeHorizon, hazeDensity, densityMultiplier, distanceMultiplier, CloudShadow: 0f);

    private static float Decode(float v) => v < 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    [Fact]
    public void No_distance_leaves_the_scene_as_it_is()
    {
        var f = Frame(new Vector3(0.5f), new Vector3(0.2f, 0.4f, 0.7f), 0.2f, 0.7f, new Vector3(0.3f), new Vector3(0.8f));
        var scene = new Vector3(0.4f, 0.2f, 0.1f);
        var seen = SkyLighting.SeenThroughHaze(f, scene, Vector3.UnitX, 0f);
        Assert.Equal(scene.X, seen.X, 5); Assert.Equal(scene.Y, seen.Y, 5); Assert.Equal(scene.Z, seen.Z, 5);
    }

    [Fact]
    public void A_far_view_of_a_dim_dusk_sky_is_the_dim_haze_light_made_linear_not_the_value_itself()
    {
        // blue horizon 1, no haze density, ambient 0.15, no sun: the haze light in display encoding is 0.15 once the distance has thinned everything; the pass doubles it (0.3),
        // makes it linear (Decode(0.3) = 0.0733) and scales it by the sky scale (2): 0.1466. Taking 0.3 as if it were linear (as the engine fog did) is four times too bright.
        var f = Frame(Vector3.One, new Vector3(0.3f), 0.2f, 0f, new Vector3(0.15f), Vector3.Zero, 0.0003f, 1f);
        var seen = SkyLighting.SeenThroughHaze(f, new Vector3(0.5f), Vector3.UnitX, 1e7f);
        float expected = Decode(0.3f) * 2f;
        Assert.Equal(expected, seen.X, 3); Assert.Equal(expected, seen.Y, 3); Assert.Equal(expected, seen.Z, 3);
    }

    [Fact]
    public void What_is_behind_is_thinned_by_the_red_channels_survival_alone()
    {
        var f = Frame(new Vector3(0.5f), new Vector3(0.2f, 0.4f, 0.8f), 0.2f, 0.7f, new Vector3(0.3f), Vector3.Zero, 0.0003f, 1f);
        const float distance = 2000f;
        float red = MathF.Exp(-(0.2f + 0.7f) * distance * 0.0003f * 1f);
        var (survival, additive) = SkyLighting.HazeAlong(f, Vector3.UnitX, distance);
        Assert.Equal(red, survival.X, 5);
        // each channel own survival thins the haze light, not the scene
        float blueSurvival = MathF.Exp(-(0.8f + 0.7f) * distance * 0.0003f * 1f);
        Assert.Equal(blueSurvival, survival.Z, 5);
        var seen = SkyLighting.SeenThroughHaze(f, new Vector3(1f, 1f, 1f), Vector3.UnitX, distance);
        float expectedBlue = red + Decode(MathF.Min(additive.Z, 10f) * 2f) * 2f;
        Assert.Equal(expectedBlue, seen.Z, 4);
    }

    [Fact]
    public void The_haze_follows_the_density_and_distance_multipliers()
    {
        var f = Frame(new Vector3(0.5f), new Vector3(0.2f, 0.4f, 0.8f), 0.2f, 0.7f, new Vector3(0.3f), Vector3.Zero, 0.0002f, 0.5f);
        var (survival, _) = SkyLighting.HazeAlong(f, Vector3.UnitX, 1000f);
        Assert.Equal(MathF.Exp(-0.9f * 1000f * 0.0002f * 0.5f), survival.X, 5);
    }

    [Fact]
    public void The_glow_brightens_the_haze_towards_the_light_and_the_terms_say_how()
    {
        var f = Frame(new Vector3(0.5f), new Vector3(0.2f, 0.4f, 0.8f), 0.2f, 0.7f, new Vector3(0.3f), new Vector3(1f), 0.0001f, 0.8f, Vector3.Normalize(new Vector3(1f, 0f, 0.5f)));
        var towards = SkyLighting.HazeAlong(f, Vector3.Normalize(new Vector3(1f, 0f, 0.5f)), 5000f).Additive;
        var away = SkyLighting.HazeAlong(f, Vector3.Normalize(new Vector3(-1f, 0f, 0.5f)), 5000f).Additive;
        Assert.True(towards.X > away.X, $"towards the light {towards} is not brighter than away {away}");
        var t = SkyLighting.HazeOf(f);
        Assert.True(t.GlowWeighted.X > 0f && t.Constant.X > 0f && t.Exponent.X > 0f, $"terms {t}");
    }
}
