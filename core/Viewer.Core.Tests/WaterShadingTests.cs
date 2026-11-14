// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The water surface colour against the official viewer's water for a sky without a probe ambiance: the water fog along the ray
/// (the fog's light and what survives of the bottom), the fresnel weights from the frame's scale and offset, and the blend of what is seen
/// through the water with the reflected sky. The expected values come from an independent transcription of those formulas in double precision
/// (below). The reflection carries the sky's colour times the fresnel factor and nothing else: the official shader has no fitted constant.
/// </summary>
public class WaterShadingTests
{
    private static readonly WaterLook Look = new(new Vector3(0.01569f, 0.14902f, 0.25098f), 16f, new(1.05f, -0.42f), new(1.11f, -1.16f), 0.4f, 0.5f);

    private static double Dec(double c) => c < 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double[] Official(WaterLook w, double ks, double cos, double l, double[] behind, double[] sky)
    {
        double kd = w.FogDensity, thick = Math.Max(l, 0.1);
        double t1 = -kd, t2 = kd + ks * cos, t3 = Math.Pow(0.98, t2 * thick) - 1.0;
        double lit = Math.Pow(Math.Min(t1 / t2 * t3, 1.0), 1.0 / 1.7), survive = Math.Pow(0.98, thick * kd);
        double d = Math.Max(0, w.FresnelOffset - w.FresnelScale * cos);
        double weight = Math.Min(1, 3 * d * d);
        double[] fog = [Dec(w.FogColor.X), Dec(w.FogColor.Y), Dec(w.FogColor.Z)];
        var o = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double through = behind[i] * survive + fog[i] * lit;
            double refl = d * sky[i];   // waterF.glsl: the probe's radiance times the fresnel factor, no other constant
            o[i] = through + (refl - through) * weight;
        }
        return o;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var cos in new[] { 0.0, 0.05, 0.12, 0.35, 0.7, 1.0 })
            foreach (var l in new[] { 0.05, 1.0, 4.0, 40.0, 4000.0 })
                yield return [cos, l];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_surface_matches_the_official_formulas(double cos, double thickness)
    {
        double[] behind = [0.3, 0.25, 0.1], sky = [0.79, 0.92, 1.0];
        var want = Official(Look, 1.0, cos, thickness, behind, sky);
        var got = WaterShading.Surface(Look, WaterShading.FogSlant(1f), (float)cos, (float)thickness, new Vector3((float)behind[0], (float)behind[1], (float)behind[2]), new Vector3((float)sky[0], (float)sky[1], (float)sky[2]));
        float[] g = [got.X, got.Y, got.Z];
        for (int i = 0; i < 3; i++)
            Assert.True(Math.Abs(g[i] - want[i]) <= 0.01 * want[i] + 0.0005, $"cos {cos} thickness {thickness} [{i}]: want {want[i]:F5}, got {g[i]:F5}");
    }

    [Fact]
    public void The_reflection_adds_the_skys_colour_times_weight_times_fresnel_factor_with_no_fitted_constant()
    {
        // along the surface (cosine 0) with scale 0.4 and offset 0.5: weight 0.75, factor 0.5, so a white sky adds 0.375 to every channel
        var black = WaterShading.Surface(Look, WaterShading.FogSlant(1f), 0f, 4000f, Vector3.Zero, Vector3.Zero);
        var white = WaterShading.Surface(Look, WaterShading.FogSlant(1f), 0f, 4000f, Vector3.Zero, Vector3.One);
        Assert.Equal(0.375f, white.X - black.X, 4);
        Assert.Equal(0.375f, white.Y - black.Y, 4);
        Assert.Equal(0.375f, white.Z - black.Z, 4);
    }

    [Fact]
    public void Along_the_surface_the_reflection_has_three_quarters_of_the_weight_and_looking_down_almost_none()
    {
        Assert.Equal(0.75f, WaterShading.Fresnel(0f, 0.4f, 0.5f).Weight, 4);
        Assert.Equal(0.5f, WaterShading.Fresnel(0f, 0.4f, 0.5f).RadianceScale, 4);
        Assert.True(WaterShading.Fresnel(1f, 0.4f, 0.5f).Weight < 0.05f);
        Assert.Equal(1f, WaterShading.Fresnel(0f, 0.4f, 1.0f).Weight, 4);   // an offset of 1 reflects fully along the surface
    }

    [Fact]
    public void Deep_water_looking_down_is_the_fog_colour_and_shallow_water_shows_the_bottom()
    {
        var deep = WaterShading.Surface(Look, 1f, 1f, 4000f, new Vector3(0.5f), new Vector3(1f));
        Assert.True(deep.Y > 0.015f && deep.Y < 0.04f && deep.X < 0.01f, $"deep water looking down {deep}");   // the fog colour's linear value is (0.0012, 0.0185, 0.0499) plus a little sky
        var shallow = WaterShading.Surface(Look, 1f, 1f, 0.1f, new Vector3(0.5f), new Vector3(1f));
        Assert.True(shallow.X > 0.3f, $"shallow water looking down {shallow}");
    }

    [Fact]
    public void The_fog_slant_follows_the_light_height_down_to_the_clamp()
    {
        Assert.Equal(1f, WaterShading.FogSlant(1f), 5);
        Assert.Equal(2f, WaterShading.FogSlant(0.5f), 5);
        Assert.Equal(1f / 0.3f, WaterShading.FogSlant(0.05f), 5);
        Assert.Equal(1f / 0.3f, WaterShading.FogSlant(-0.5f), 5);
    }
}
