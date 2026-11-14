// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The haze that fades distance into the sky, against the official atmospherics (class1/windlight/atmosphericsFuncs.glsl,
/// calcAtmosphericVars): what survives a ray is exp(-(blue_density + haze_density) * length * density_multiplier * distance_multiplier),
/// so both densities thin the distance, not the haze density alone. The scene's single fog density is the mean of the three channels'
/// exponents. Each test builds its own values: no shared state, so the class runs in parallel.</summary>
public class SkyHazeTests
{
    private static SkyFrame Frame(Vector3 blueDensity, float hazeDensity, float densityMultiplier, float distanceMultiplier) =>
        new(0.5f, Vector3.UnitZ, Vector3.One, -Vector3.UnitZ, 0.5f, Vector3.One, new Vector3(0.25f, 0.45f, 0.75f), blueDensity, 0.19f, hazeDensity, densityMultiplier, distanceMultiplier);

    [Fact]
    public void The_fog_density_counts_the_blue_density_and_the_haze_density_together()
    {
        var f = Frame(new Vector3(0.2f, 0.3f, 0.6f), 0.7f, 0.0003f, 1.0f);
        // channels: 0.9, 1.0, 1.3 -> mean 1.0667
        Assert.Equal(1.0666667f * 0.0003f, SkyLighting.FogDensity(f), 8);
    }

    [Fact]
    public void The_fog_density_follows_the_density_and_distance_multipliers()
    {
        var f = Frame(new Vector3(0.2447f, 0.4487f, 0.7599f), 0.699f, 0.0001f, 0.8f);
        float mean = ((0.2447f + 0.699f) + (0.4487f + 0.699f) + (0.7599f + 0.699f)) / 3f;
        Assert.Equal(mean * 0.0001f * 0.8f, SkyLighting.FogDensity(f), 8);
    }

    [Fact]
    public void Nothing_survives_the_haze_by_the_official_curve_at_the_distance_the_density_gives()
    {
        // at 128 m the surviving fraction is exp(-density * 128) for the mean channel
        var f = Frame(new Vector3(0.2447f, 0.4487f, 0.7599f), 0.699f, 0.0001f, 0.8f);
        float survive = MathF.Exp(-SkyLighting.FogDensity(f) * 128f);
        Assert.InRange(survive, 0.985f, 0.995f);
    }
}

/// <summary>The probe ambiance of a sky (reflection_probe_ambiance): the official viewer shades a sky that sets it (non-zero) with the
/// tone-mapped path and a sky that does not with the classic one (pipeline.cpp, llsettingsvo.cpp), so the record's value is read.</summary>
public class SkyProbeAmbianceTests
{
    private static string Synthetic => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "fixtures", "environment", "synthetic-environment.llsd.xml"));

    [Fact]
    public void A_sky_that_names_a_probe_ambiance_carries_it()
    {
        var text = Synthetic.Replace("<key>type</key><string>sky</string>", "<key>type</key><string>sky</string><key>reflection_probe_ambiance</key><real>1</real>");
        var e = RegionEnvironment.FromLlsd(text, out var why);
        Assert.True(e is not null, why);
        Assert.All(e!.Sky, s => Assert.Equal(1f, s.ProbeAmbiance));
    }

    [Fact]
    public void A_sky_that_names_none_has_none()
    {
        var e = RegionEnvironment.FromLlsd(Synthetic, out var why);
        Assert.True(e is not null, why);
        Assert.All(e!.Sky, s => Assert.Equal(0f, s.ProbeAmbiance));
    }
}
