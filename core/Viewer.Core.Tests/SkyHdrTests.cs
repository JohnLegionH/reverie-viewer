// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The sky's colour on the tone-mapped path, and the fixture of a sky that sets a reflection probe ambiance.</summary>
public class SkyHdrTests
{
    private static readonly SkyFrame Frame = RegionEnvironment.Default.Sky[0];
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "fixtures", "environment", name));

    [Theory]
    [InlineData(10f, 0f)]
    [InlineData(10f, 90f)]
    [InlineData(25f, 40f)]
    [InlineData(45f, 120f)]
    [InlineData(70f, 170f)]
    public void The_tone_mapped_dome_is_the_classic_one_rescaled_where_the_classic_one_is_not_clipped(float elevation, float azimuth)
    {
        var classic = SkyLighting.DomeColour(Frame, elevation, azimuth);
        var hdr = SkyLighting.DomeColourHdr(Frame, elevation, azimuth);
        float k = ToneMap.HdrScale(1f) / 1.1f;   // the sky's scale instead of the classic final scale
        // a channel the classic dome clips at 1 says nothing of the unclipped value, so only the unclipped channels are compared (at least two of the three)
        int compared = 0;
        foreach (var (c, h) in new[] { (classic.X, hdr.X), (classic.Y, hdr.Y), (classic.Z, hdr.Z) })
        {
            if (c >= 0.95f) continue;
            Assert.Equal(c * k, h, 4);
            compared++;
        }
        Assert.True(compared >= 2, $"only {compared} channels are unclipped in the classic dome here: {classic}");
    }

    [Fact]
    public void The_tone_mapped_dome_can_pass_one_and_never_passes_the_sky_scale()
    {
        float most = 0;
        for (float elevation = 0; elevation <= 90; elevation += 7.5f)
            for (float azimuth = 0; azimuth <= 180; azimuth += 22.5f)
            {
                var c = SkyLighting.DomeColourHdr(Frame, elevation, azimuth);
                most = MathF.Max(most, MathF.Max(c.X, MathF.Max(c.Y, c.Z)));
                Assert.True(c.X <= 2.0001f && c.Y <= 2.0001f && c.Z <= 2.0001f, $"{c} at {elevation}, {azimuth}");
            }
        Assert.True(most > 1.05f, $"the brightest sky colour is {most}: nothing above 1 is kept");
    }

    [Fact]
    public void The_sky_scale_follows_the_gamma()
    {
        var one = SkyLighting.DomeColourHdr(Frame, 25f, 40f, 1f);
        var more = SkyLighting.DomeColourHdr(Frame, 25f, 40f, 2.25f);   // a scale of 3 instead of 2
        Assert.True(one.X > 0f);
        Assert.Equal(one.X * 1.5f, more.X, 4); Assert.Equal(one.Y * 1.5f, more.Y, 4); Assert.Equal(one.Z * 1.5f, more.Z, 4);
    }

    [Fact]
    public void The_tone_mapped_profile_holds_the_dome_colour_at_each_of_its_places()
    {
        var p = SkyLighting.DomeProfileHdr(Frame);
        Assert.Equal(SkyLighting.DomeElevations * SkyLighting.DomeAzimuths, p.Length);
        foreach (var (k, j) in new[] { (0, 0), (3, 2), (SkyLighting.DomeElevations / 2, SkyLighting.DomeAzimuths / 2), (SkyLighting.DomeElevations - 1, SkyLighting.DomeAzimuths - 1) })
        {
            var want = SkyLighting.DomeColourHdr(Frame, SkyLighting.DomeElevation(k, SkyLighting.DomeElevations), SkyLighting.DomeAzimuth(j, SkyLighting.DomeAzimuths));
            var got = p[j * SkyLighting.DomeElevations + k];
            Assert.True(want.LengthSquared() > 0f);
            Assert.Equal(want.X, got.X, 5); Assert.Equal(want.Y, got.Y, 5); Assert.Equal(want.Z, got.Z, 5);
        }
    }

    [Fact]
    public void The_probe_ambiance_fixture_puts_every_sky_frame_on_the_tone_mapped_path_and_the_plain_one_does_not()
    {
        var plain = RegionEnvironment.FromLlsd(Fixture("synthetic-environment.llsd.xml"), out _)!;
        Assert.All(plain.Sky, f => Assert.False(ColourPath.IsHdr(f.ProbeAmbiance)));
        var hdr = RegionEnvironment.FromLlsd(Fixture("synthetic-environment-hdr.llsd.xml"), out var why);
        Assert.True(hdr is not null, why);
        Assert.Equal(plain.Sky.Length, hdr!.Sky.Length);
        Assert.All(hdr.Sky, f => Assert.True(ColourPath.IsHdr(f.ProbeAmbiance), $"probe ambiance {f.ProbeAmbiance}"));
        // everything else is the plain fixture's
        for (int i = 0; i < hdr.Sky.Length; i++) Assert.Equal(plain.Sky[i] with { ProbeAmbiance = hdr.Sky[i].ProbeAmbiance }, hdr.Sky[i]);
    }
}
