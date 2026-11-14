// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The official classic lighting's response, written out in double precision from the 26.4 shaders (atmosphericsFuncs.glsl calcAtmosphericVars and
/// calcAtmosphericVarsLinear, softenLightF.glsl's legacy branch, pipeline.cpp's specular table) independently of <see cref="SkyLighting"/>, against
/// <see cref="SkyLighting.ClassicDiffuse"/>, <see cref="SkyLighting.ClassicSpecular"/> and <see cref="SkyLighting.Classic"/>. An avatar's shaders take their
/// constants from <see cref="SkyLighting.Classic"/> and work the response out per pixel, so these are the numbers a rendered pixel is measured against.
/// </summary>
public class ClassicLightTests
{
    private static double S2L(double c) => c < 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    private static double L2S(double c) => c < 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(Math.Max(c, 0), 1 / 2.4) - 0.055;

    // a standard midday frame (the ground-level sky of a stock day cycle): overhead sun, ambient above one
    private static readonly SkyFrame Midday = new(0.5f, Vector3.UnitZ, new(0.73421f, 0.78158f, 0.9f), -Vector3.UnitZ, 0.5f, new(1.05f, 1.05f, 1.05f),
        new(0.49548f, 0.49548f, 0.64f), new(0.24476f, 0.44872f, 0.76f), 0.19f, 0.7f, 0.00018f, 0.8f, 1605f, 0.27f, new Vector3(5f, 0.001f, -0.48f));
    // a lower sun with a tinted ambient, so the channels differ and the sun's path is longer
    private static readonly SkyFrame Afternoon = Midday with { SunDirection = Vector3.Normalize(new(0.6f, 0.2f, 0.5f)), Ambient = new(0.5f, 0.45f, 0.4f), CloudShadow = 0.1f };

    public static IEnumerable<object[]> Frames() { yield return [nameof(Midday)]; yield return [nameof(Afternoon)]; }
    private static SkyFrame Frame(string n) => n == nameof(Midday) ? Midday : Afternoon;

    private static (double[] amb, double[] sunTerm, double[] sunLinear) Terms(SkyFrame f)
    {
        double lighty = Math.Max(f.SunDirection.Z, -0.1);
        float[] sun = [f.SunColor.X, f.SunColor.Y, f.SunColor.Z], amb = [f.Ambient.X, f.Ambient.Y, f.Ambient.Z], bd = [f.BlueDensity.X, f.BlueDensity.Y, f.BlueDensity.Z];
        var a = new double[3]; var s = new double[3]; var l = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double atten = (bd[i] + f.HazeDensity * 0.25) * (f.DensityMultiplier * f.MaxY);
            double sunlit = sun[i] * Math.Exp(-atten / Math.Max(1e-6, lighty)) * 1.35;
            double tmp = amb[i] + (1 - amb[i]) * f.CloudShadow * 0.5;
            a[i] = Math.Pow(Math.Max(tmp, 0), 0.9) * 0.57 * 0.9;
            s[i] = sunlit * 0.7;
            l[i] = S2L(sunlit);
        }
        return (a, s, l);
    }

    private static double[] Diffuse(SkyFrame f, double nl, double shadow)
    {
        var (a, s, _) = Terms(f);
        double ease = 1 - Math.Pow(Math.Min(Math.Abs(nl), 1) * 0.5, 2);
        double d = Math.Min(Math.Pow(Math.Clamp(nl, 0, 1), 1.2), shadow);
        var o = new double[3];
        for (int i = 0; i < 3; i++) o[i] = 1.1 * S2L(a[i] * ease + L2S(d) * s[i]);
        return o;
    }

    private static void Near(double[] want, Vector3 got, double tol, string what)
    {
        float[] g = [got.X, got.Y, got.Z];
        for (int i = 0; i < 3; i++) Assert.True(Math.Abs(g[i] - want[i]) <= tol * Math.Abs(want[i]) + 1e-4, $"{what}[{i}]: want {want[i]:F5}, got {g[i]:F5}");
    }

    [Theory, MemberData(nameof(Frames))]
    public void The_diffuse_factor_follows_the_official_sum_over_every_angle_including_faces_turned_away(string frame)
    {
        var f = Frame(frame);
        for (int k = -20; k <= 20; k++)
            Near(Diffuse(f, k / 20.0, 1), SkyLighting.ClassicDiffuse(f, k / 20f), 0.002, $"{frame} nl {k / 20.0}");
    }

    [Theory, MemberData(nameof(Frames))]
    public void A_shadow_caps_the_sun_term_as_the_official_min_of_the_angle_and_the_shadow_does(string frame)
    {
        var f = Frame(frame);
        foreach (double shadow in new[] { 0.0, 0.2, 0.6 })
            Near(Diffuse(f, 0.9, shadow), SkyLighting.ClassicDiffuse(f, 0.9f, (float)shadow), 0.002, $"{frame} shadow {shadow}");
    }

    [Theory, MemberData(nameof(Frames))]
    public void The_constants_are_the_official_sums_terms_and_the_surface_ambient_is_the_face_turned_away(string frame)
    {
        var f = Frame(frame);
        var (a, s, l) = Terms(f);
        var t = SkyLighting.Classic(f);
        Near(a, t.Ambient, 0.002, "ambient term");
        Near(s, t.Sun, 0.002, "sun term");
        Near(l, t.SunLinear, 0.002, "sun linear");
        Near(Diffuse(f, 0, 1), t.Surface, 0.002, "surface");
        Near(Diffuse(f, 0, 1), SkyLighting.SurfaceAmbient(f), 0.002, "surface ambient");
    }

    // pipeline.cpp createLUTBuffers: the table's value at (nh, glossiness), analytic: n = gloss^2 * 368, nh^n times the normalisation
    private static double Lobe(double nh, double gloss)
    {
        double n = gloss * gloss * 368.0;
        return Math.Pow(nh, n) * ((n + 2) * (n + 4)) / (8 * Math.PI * (Math.Pow(2, -n / 2) + n));
    }

    private static double[] Specular(SkyFrame f, double nl, double nh, double nv, double vh, double gloss, double[] spec, double shadow)
    {
        var (_, _, l) = Terms(f);
        var o = new double[3];
        if (!(nl > 0 && nh > 0)) return o;
        double lit = Math.Min(nl * 6.0, 1.0);
        double fres = Math.Pow(1 - vh, 5) * 0.4 + 0.5;
        double gt = Math.Max(0, Math.Min(2 * nh * nv / vh, 2 * nh * nl / vh));
        double scol = shadow * fres * Lobe(nh, gloss) * gt / (nh * nl);
        for (int i = 0; i < 3; i++) o[i] = 1.1 * lit * scol * l[i] * spec[i];
        return o;
    }

    [Theory, MemberData(nameof(Frames))]
    public void The_specular_follows_the_official_fresnel_geometry_and_normalised_lobe(string frame)
    {
        var f = Frame(frame);
        double[][] cases = [[1, 1, 1, 1, 0.5], [0.8, 0.97, 0.9, 0.95, 0.5], [0.4, 0.9, 0.7, 0.8, 0.75], [0.05, 0.5, 0.9, 0.6, 0.25]];
        foreach (var c in cases)
            Near(Specular(f, c[0], c[1], c[2], c[3], c[4], [0.8, 0.8, 0.8], 1), SkyLighting.ClassicSpecular(f, (float)c[0], (float)c[1], (float)c[2], (float)c[3], (float)c[4], new Vector3(0.8f), 1f), 0.004,
                $"{frame} nl {c[0]} nh {c[1]} gloss {c[4]}");
    }

    [Fact]
    public void A_face_turned_from_the_light_or_not_lit_gets_no_specular()
    {
        Assert.Equal(Vector3.Zero, SkyLighting.ClassicSpecular(Midday, -0.2f, 0.9f, 0.8f, 0.9f, 0.5f, Vector3.One));
        Assert.Equal(Vector3.Zero, SkyLighting.ClassicSpecular(Midday, 0.5f, -0.1f, 0.8f, 0.9f, 0.5f, Vector3.One));
    }
}
