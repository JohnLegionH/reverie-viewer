// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The official lighting of a sky that takes the tone-mapped path (a reflection-probe ambiance other than 0, so classic_mode is 0 in the 26.4 shaders),
/// written out in double precision from calcAtmosphericVars and calcAtmosphericVarsLinear (atmosphericsFuncs.glsl) and the non-classic branch of
/// softenLightF.glsl, independently of <see cref="SkyLighting"/>: the ambient is the sky's ambient raised to 0.9 times 0.57, eased by the face's angle, taken to
/// linear light and reduced to its luminance (a grey); the sun is the sky's light colour thinned along its path (no factor 1.35), taken to linear light; the diffuse
/// factor is the grey ambient plus the sun times the angle (not raised to a power, not summed in display encoding); no final factor 1.1.
/// The ambient taken from a reflection probe's irradiance map instead of the sky is scene-dependent and is not part of these numbers.
/// </summary>
public class EepLightTests
{
    private static double S2L(double c) => c < 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static readonly SkyFrame Midday = new(0.5f, Vector3.UnitZ, new(0.73421f, 0.78158f, 0.9f), -Vector3.UnitZ, 0.5f, new(1.05f, 1.05f, 1.05f),
        new(0.49548f, 0.49548f, 0.64f), new(0.24476f, 0.44872f, 0.76f), 0.19f, 0.7f, 0.00018f, 0.8f, 1605f, 0.27f, new Vector3(5f, 0.001f, -0.48f), null, 0.5f);
    private static readonly SkyFrame Afternoon = Midday with { SunDirection = Vector3.Normalize(new(0.6f, 0.2f, 0.5f)), Ambient = new(0.5f, 0.45f, 0.4f), CloudShadow = 0.1f };

    public static IEnumerable<object[]> Frames() { yield return [nameof(Midday)]; yield return [nameof(Afternoon)]; }
    private static SkyFrame Frame(string n) => n == nameof(Midday) ? Midday : Afternoon;

    // the ambient before the face's easing, per channel (pow(ambient, 0.9) * 0.57), and the sun as linear light
    private static (double[] amb, double[] sun) Terms(SkyFrame f)
    {
        double lighty = Math.Max(f.SunDirection.Z, -0.1);
        float[] sun = [f.SunColor.X, f.SunColor.Y, f.SunColor.Z], amb = [f.Ambient.X, f.Ambient.Y, f.Ambient.Z], bd = [f.BlueDensity.X, f.BlueDensity.Y, f.BlueDensity.Z];
        var a = new double[3]; var s = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double atten = (bd[i] + f.HazeDensity * 0.25) * (f.DensityMultiplier * f.MaxY);
            double tmp = amb[i] + (1 - amb[i]) * f.CloudShadow * 0.5;
            a[i] = Math.Pow(Math.Max(tmp, 0), 0.9) * 0.57;
            s[i] = S2L(sun[i] * Math.Exp(-atten / Math.Max(1e-6, lighty)));
        }
        return (a, s);
    }

    private static double[] Diffuse(SkyFrame f, double nl, double shadow)
    {
        var (a, s) = Terms(f);
        double ease = 1 - Math.Pow(Math.Min(Math.Abs(nl), 1) * 0.5, 2);
        double grey = 0.2126 * S2L(a[0] * ease) + 0.7152 * S2L(a[1] * ease) + 0.0722 * S2L(a[2] * ease);
        double d = Math.Min(Math.Clamp(nl, 0, 1), shadow);
        return [grey + d * s[0], grey + d * s[1], grey + d * s[2]];
    }

    private static void Near(double[] want, Vector3 got, double tol, string what)
    {
        float[] g = [got.X, got.Y, got.Z];
        for (int i = 0; i < 3; i++) Assert.True(Math.Abs(g[i] - want[i]) <= tol * Math.Abs(want[i]) + 1e-4, $"{what}[{i}]: want {want[i]:F5}, got {g[i]:F5}");
    }

    [Theory, MemberData(nameof(Frames))]
    public void The_diffuse_factor_is_the_grey_ambient_plus_the_sun_times_the_angle_over_every_angle(string frame)
    {
        var f = Frame(frame);
        for (int k = -20; k <= 20; k++)
            Near(Diffuse(f, k / 20.0, 1), SkyLighting.EepDiffuse(f, k / 20f), 0.002, $"{frame} nl {k / 20.0}");
    }

    [Theory, MemberData(nameof(Frames))]
    public void A_shadow_caps_the_sun_term_as_the_official_min_of_the_angle_and_the_shadow_does(string frame)
    {
        var f = Frame(frame);
        foreach (double shadow in new[] { 0.0, 0.2, 0.6 })
            Near(Diffuse(f, 0.9, shadow), SkyLighting.EepDiffuse(f, 0.9f, (float)shadow), 0.002, $"{frame} shadow {shadow}");
    }

    [Theory, MemberData(nameof(Frames))]
    public void The_constants_are_the_ambient_before_its_easing_and_the_sun_as_linear_light_without_the_classic_factor(string frame)
    {
        var f = Frame(frame);
        var (a, s) = Terms(f);
        var t = SkyLighting.Eep(f);
        Near(a, t.Ambient, 0.002, "ambient");
        Near(s, t.Sun, 0.002, "sun");
    }

    [Fact]
    public void The_ambient_is_grey_so_a_face_turned_from_the_light_shows_no_tint_of_the_sky_ambient()
    {
        var d = SkyLighting.EepDiffuse(Afternoon, 0f);
        Assert.InRange(d.X, 0.0001f, 10f);
        Assert.Equal(d.X, d.Y, 5);
        Assert.Equal(d.Y, d.Z, 5);
    }

    private static double Lobe(double nh, double gloss)
    {
        double n = gloss * gloss * 368.0;
        return Math.Pow(nh, n) * ((n + 2) * (n + 4)) / (8 * Math.PI * (Math.Pow(2, -n / 2) + n));
    }

    [Theory, MemberData(nameof(Frames))]
    public void The_specular_is_lit_with_the_suns_linear_colour_and_has_no_final_factor(string frame)
    {
        var f = Frame(frame);
        var (_, s) = Terms(f);
        double[][] cases = [[1, 1, 1, 1, 0.5], [0.8, 0.97, 0.9, 0.95, 0.5], [0.4, 0.9, 0.7, 0.8, 0.75]];
        foreach (var c in cases)
        {
            double nl = c[0], nh = c[1], nv = c[2], vh = c[3], gloss = c[4];
            double lit = Math.Min(nl * 6.0, 1.0), fres = Math.Pow(1 - vh, 5) * 0.4 + 0.5, gt = Math.Max(0, Math.Min(2 * nh * nv / vh, 2 * nh * nl / vh));
            double scol = fres * Lobe(nh, gloss) * gt / (nh * nl);
            double[] want = [lit * scol * s[0] * 0.8, lit * scol * s[1] * 0.8, lit * scol * s[2] * 0.8];
            Near(want, SkyLighting.EepSpecular(f, (float)nl, (float)nh, (float)nv, (float)vh, (float)gloss, new Vector3(0.8f)), 0.004, $"{frame} nl {nl} gloss {gloss}");
        }
    }
}
