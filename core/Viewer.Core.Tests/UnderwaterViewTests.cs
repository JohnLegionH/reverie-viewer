// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The view from below the water against the official viewer: when the camera counts as under water, the water fog density used there, the
/// water fog along a ray from an eye under the surface, what the haze leaves of the scene, and the surface seen from below. The expected
/// values come from an independent transcription of those formulas in double precision (below).
/// </summary>
public class UnderwaterViewTests
{
    private static readonly Vector3 FogColour = new(0.01569f, 0.14902f, 0.25098f);

    private static double Dec(double c) => c < 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    // the official water fog for an eye under the surface: the eye's depth e0 dims the fog's light, up is the ray's height component
    private static (double[] Light, double Survives) Official(double kd, double ks, double e0, double up, double distance)
    {
        double es = -up, l = Math.Max(distance, 0.1);
        double t1 = -kd * Math.Pow(0.98, ks * e0), t2 = kd + ks * es, t3 = Math.Pow(0.98, t2 * l) - 1.0;
        double lit = Math.Pow(Math.Min(t1 / t2 * t3, 1.0), 1.0 / 1.7);
        return ([Dec(FogColour.X) * lit, Dec(FogColour.Y) * lit, Dec(FogColour.Z) * lit], Math.Pow(0.98, l * kd));
    }

    [Theory]
    [InlineData(19.9f, 20f, true)]
    [InlineData(20f, 20f, false)]
    [InlineData(25f, 20f, false)]
    public void The_camera_is_under_water_only_below_the_surface(float eye, float water, bool under)
        => Assert.Equal(under, UnderwaterView.EyeBelow(eye, water));

    [Fact]
    public void With_no_water_the_camera_is_never_under_it() => Assert.False(UnderwaterView.EyeBelow(-50f, null));

    [Fact]
    public void The_water_height_is_that_of_the_region_the_camera_is_over()
    {
        WaterRect[] others = [new(256, 0, 512, 256, 29f)];
        Assert.Equal(29f, UnderwaterView.HeightAt(300f, 10f, 16f, others));
        Assert.Equal(16f, UnderwaterView.HeightAt(10f, 10f, 16f, others));
        Assert.Equal(16f, UnderwaterView.HeightAt(-500f, 10f, 16f, others));   // off every region: the agent's region
    }

    [Theory]
    [InlineData(16f, 0.25f, 2.0)]          // the density to the power of the modifier
    [InlineData(2f, 0.25f, 1.189207)]
    [InlineData(5f, 0f, 5.0)]              // no modifier: unchanged
    [InlineData(5f, -1f, 5.0)]
    [InlineData(2f, 20f, 1024.0)]          // the modifier stops at 10
    [InlineData(-4f, 2f, 16.0)]            // a negative density with a whole modifier is raised as it is
    [InlineData(-4f, 0.5f, 1.0)]           // and with a part modifier is taken as 1
    public void Under_water_the_fog_density_is_raised_to_the_underwater_modifier(float density, float mod, double want)
    {
        Assert.Equal(want, UnderwaterView.FogDensity(density, mod, underwater: true), 3);
        Assert.Equal(density, UnderwaterView.FogDensity(density, mod, underwater: false));
    }

    public static IEnumerable<object[]> Rays()
    {
        foreach (var up in new[] { -1.0, -0.4, 0.0, 0.2, 0.7, 1.0 })
            foreach (var d in new[] { 0.02, 1.0, 6.0, 40.0, 500.0 })
                foreach (var depth in new[] { 0.5, 6.0 })
                    yield return [up, d, depth];
    }

    [Theory]
    [MemberData(nameof(Rays))]
    public void The_fog_along_a_ray_from_under_water_matches_the_official_formulas(double up, double distance, double depth)
    {
        var want = Official(2.0, 1.25, depth, up, distance);
        var got = UnderwaterView.Fog(FogColour, 2f, 1.25f, (float)depth, (float)up, (float)distance);
        float[] g = [got.Light.X, got.Light.Y, got.Light.Z];
        for (int i = 0; i < 3; i++)
            Assert.True(Math.Abs(g[i] - want.Light[i]) <= 0.01 * want.Light[i] + 0.0005, $"light[{i}]: want {want.Light[i]:F5}, got {g[i]:F5}");
        Assert.True(Math.Abs(got.Survives - want.Survives) <= 0.001, $"survives: want {want.Survives:F5}, got {got.Survives:F5}");
    }

    /// <summary>Where the fog density and the slant times the ray's rise cancel, the official expression is 0/0; the value is its limit.</summary>
    [Fact]
    public void The_fog_stays_finite_where_the_density_and_the_slant_cancel()
    {
        var got = UnderwaterView.Fog(FogColour, 1f, 2f, 3f, 0.5f, 10f);
        var near = Official(1.0, 2.0, 3.0, 0.5 - 1e-5, 10.0);
        Assert.True(float.IsFinite(got.Light.Z));
        Assert.Equal(near.Light[2], got.Light.Z, 3);
    }

    [Fact]
    public void The_haze_leaves_the_scene_the_transmittance_to_the_power_1_7_and_adds_the_fog_light()
    {
        var got = UnderwaterView.Haze(new Vector3(0.8f, 0.6f, 0.4f), new Vector3(0.01f, 0.02f, 0.03f), 0.5f);
        double left = Math.Pow(0.5, 1.7);
        Assert.Equal(0.8 * left + 0.01, got.X, 4);
        Assert.Equal(0.6 * left + 0.02, got.Y, 4);
        Assert.Equal(0.4 * left + 0.03, got.Z, 4);
    }

    [Fact]
    public void The_surface_from_below_is_the_scene_above_dimmed_by_the_water_with_no_mirror_reflection()
    {
        var got = UnderwaterView.SurfaceFromBelow(new Vector3(0.8f, 0.6f, 0.4f), new Vector3(0.01f, 0.02f, 0.03f), 0.5f);
        Assert.Equal(0.41f, got.X, 4);
        Assert.Equal(0.32f, got.Y, 4);
        Assert.Equal(0.23f, got.Z, 4);
    }

    [Fact]
    public void The_view_through_the_surface_is_moved_by_the_waves_times_the_below_scale_at_any_distance()
    {
        var got = UnderwaterView.Distort(new Vector2(0.5f, 0.5f), new Vector3(3f, 0f, 4f), 0.2f);
        Assert.Equal(0.62f, got.X, 4);   // the sum made a unit vector (0.6, 0, 0.8), its sideways part times the scale
        Assert.Equal(0.5f, got.Y, 4);
    }

    [Fact]
    public void The_underwater_fog_modifier_is_read_from_the_water_settings_with_the_official_default()
    {
        Assert.Equal(0.25f, new WaterLook(FogColour, 16f, default, default, 0.4f, 0.5f).FogMod);
        const string llsd = """
            <llsd><map><key>environment</key><map><key>day_cycle</key><map>
              <key>frames</key><map>
                <key>w</key><map><key>type</key><string>water</string><key>water_fog_color</key><array><real>0.1</real><real>0.2</real><real>0.3</real></array><key>underwater_fog_mod</key><real>0.75</real></map>
                <key>s</key><map><key>type</key><string>sky</string></map>
              </map>
              <key>tracks</key><array><array><map><key>key_keyframe</key><real>0</real><key>key_name</key><string>w</string></map></array><array><map><key>key_keyframe</key><real>0</real><key>key_name</key><string>s</string></map></array></array>
            </map></map></map></llsd>
            """;
        var env = RegionEnvironment.FromLlsd(llsd, out var why);
        Assert.True(env is not null, why);
        Assert.Equal(0.75f, env!.Water.FogMod);
    }
}
