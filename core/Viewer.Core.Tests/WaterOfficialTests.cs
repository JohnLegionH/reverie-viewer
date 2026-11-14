// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The official water's numbers: the two layers of wave coordinates, the fresnel factors of three wave layers, the refraction offset
/// and the sun's glint, against values computed in double precision by an independent script from the official shader text; the
/// region's water settings and their official defaults; the normal map the water ripples are made from. Plain .NET.
/// </summary>
public class WaterOfficialTests
{
    private static void Near(double want, double got, string what, double rel = 1e-4, double abs = 1e-5) =>
        Assert.True(Math.Abs(want - got) <= Math.Max(abs, rel * Math.Abs(want)), $"{what}: want {want}, got {got}");

    [Theory]
    [InlineData(0f, 0f, 0f, 0.24, 0.0, 2.7, 0.0, 0.6, 0.0)]
    [InlineData(100f, 50f, 10f, 4.744533028239827, 1.769, 48.32212156769806, 43.492, 11.467582570599571, 9.58)]
    [InlineData(-37.5f, 220f, 3.25f, -1.7782951969175909, 8.724925, -21.648330340322897, 197.5099, -4.573706742293977, 43.8635)]
    public void Wave_coordinates_follow_the_official_vertex_shader(float x, float y, float t, double bx, double by, double l1x, double l1y, double l2x, double l2y)
    {
        var (big, little1, little2) = WaterWaves.Coordinates(x, y, t, new Vector2(1.05f, -0.42f), new Vector2(1.11f, -1.16f));
        Near(bx, big.X, "big x", 1e-4, 1e-4); Near(by, big.Y, "big y", 1e-4, 1e-4);
        Near(l1x, little1.X, "little1 x", 1e-4, 1e-3); Near(l1y, little1.Y, "little1 y", 1e-4, 1e-3);
        Near(l2x, little2.X, "little2 x", 1e-4, 1e-3); Near(l2y, little2.Y, "little2 y", 1e-4, 1e-3);
    }

    [Fact]
    public void The_wave_clock_runs_at_half_the_elapsed_time()
        => Near(7.5, WaterWaves.Phase(15.0), "phase");

    [Fact]
    public void Fresnel_factors_of_three_wave_layers_follow_the_official_fragment_shader()
    {
        var w1 = new Vector3(0.10037206624579437f, 0.05018603312289718f, 0.9936834558333641f);
        var w2 = new Vector3(-0.20091625822630407f, 0.10045812911315204f, 0.9744438523975747f);
        var w3 = new Vector3(0.05036904600660999f, -0.15110713801982997f, 0.9872333017295558f);
        var wf = new Vector3(0.025113495279619362f, -0.0001474990218699873f, 0.9879004889150638f);
        var f = WaterShading.FresnelFactors(new Vector3(0.6f, 0f, -0.8f), w1, w2, w3, wf, 0.4f, 0.5f);
        Near(0.10921195700115496, f.Weight, "reflection weight (sum of the three squares)");
        Near(0.18989908241428816, f.RadianceScale, "reflected light factor");
        Near(0.04248157532346865, f.Layers.X, "layer 1"); Near(0.028246176952090765, f.Layers.Y, "layer 2"); Near(0.03848420472559555, f.Layers.Z, "layer 3");
    }

    [Theory]
    [InlineData(100.0, 0.0299, 0.501794, 0.498804)]
    [InlineData(4.0, 0.0299, 0.50897, 0.49402)]
    public void The_refraction_offset_shrinks_with_the_square_root_of_the_distance(double dist, double refScale, double wantX, double wantY)
    {
        var d = WaterShading.Refract(new Vector2(0.5f, 0.5f), new Vector2(0.3f, -0.2f), (float)refScale, (float)dist);
        Near(wantX, d.X, "x"); Near(wantY, d.Y, "y");
    }

    [Fact]
    public void The_refraction_coordinates_are_kept_inside_the_screen()
    {
        var d = WaterShading.Refract(new Vector2(0.99f, 0.01f), new Vector2(0.9f, -0.9f), 0.2f, 1f);
        Near(0.999, d.X, "x"); Near(0.0, d.Y, "y");
    }

    [Theory]
    [InlineData(40, 40, 10.0)]
    [InlineData(40, 30, 0.1117739844462791)]
    [InlineData(40, 20, 0.010452834119748632)]
    [InlineData(10, 10, 10.0)]
    public void The_suns_glint_follows_the_official_punctual_light(double lightElev, double viewElev, double want)
    {
        var l = new Vector3((float)Math.Cos(lightElev * Math.PI / 180), 0f, (float)Math.Sin(lightElev * Math.PI / 180));
        var v = new Vector3(-(float)Math.Cos(viewElev * Math.PI / 180), 0f, (float)Math.Sin(viewElev * Math.PI / 180));
        Near(want, WaterShading.Glint(Vector3.UnitZ, v, l, 0.08f), "glint", 2e-3, 1e-4);
    }

    // ---- the water's settings

    [Fact]
    public void A_water_with_no_settings_has_the_official_defaults()
    {
        var w = RegionEnvironment.Default.Water;
        Near(0.04, w.BlurMultiplier, "blur multiplier");
        Near(0.0299, w.ScaleAbove, "scale above"); Near(0.2, w.ScaleBelow, "scale below");
        Assert.Equal(new Vector3(2f, 2f, 2f), w.NormalScale);
    }

    [Fact]
    public void The_regions_water_settings_are_read_from_the_environment()
    {
        const string xml = """
            <llsd><map><key>environment</key><map><key>day_cycle</key><map><key>frames</key><map>
              <key>w</key><map><key>type</key><string>water</string>
                <key>water_fog_color</key><array><real>0.1</real><real>0.2</real><real>0.3</real></array>
                <key>water_fog_density</key><real>3</real>
                <key>blur_multiplier</key><real>0.11</real>
                <key>normal_scale</key><array><real>1.5</real><real>2.5</real><real>3.5</real></array>
                <key>scale_above</key><real>0.07</real><key>scale_below</key><real>0.4</real>
                <key>wave1_direction</key><array><real>1</real><real>0</real></array>
                <key>wave2_direction</key><array><real>0</real><real>1</real></array>
              </map>
              <key>s</key><map><key>type</key><string>sky</string>
                <key>sun_rotation</key><array><real>0</real><real>-0.7071</real><real>0</real><real>0.7071</real></array>
                <key>moon_rotation</key><array><real>0</real><real>0.7071</real><real>0</real><real>0.7071</real></array>
                <key>sunlight_color</key><array><real>1</real><real>1</real><real>1</real><real>1</real></array>
                <key>legacy_haze</key><map><key>ambient</key><array><real>1</real><real>1</real><real>1</real></array></map>
              </map></map>
              <key>tracks</key><array><array><map><key>key_keyframe</key><real>0</real><key>key_name</key><string>w</string></map></array><array><map><key>key_keyframe</key><real>0</real><key>key_name</key><string>s</string></map></array></array>
            </map></map></map></llsd>
            """;
        var env = RegionEnvironment.FromLlsd(xml, out var why);
        Assert.True(env is not null, why);
        Near(0.11, env!.Water.BlurMultiplier, "blur"); Near(0.07, env.Water.ScaleAbove, "above"); Near(0.4, env.Water.ScaleBelow, "below");
        Assert.Equal(new Vector3(1.5f, 2.5f, 3.5f), env.Water.NormalScale);
    }

    // ---- the normal map

    [Fact]
    public void The_normal_map_is_a_unit_normal_per_texel_that_points_up_and_is_not_flat()
    {
        const int n = 128;
        var map = WaterNormalMap.Generate(n);
        Assert.Equal(n * n * 4, map.Length);
        double sum = 0, sumSq = 0; int count = 0;
        for (int i = 0; i < n * n; i++)
        {
            double x = map[i * 4] / 255.0 * 2 - 1, y = map[i * 4 + 1] / 255.0 * 2 - 1, z = map[i * 4 + 2] / 255.0 * 2 - 1;
            Assert.InRange(Math.Sqrt(x * x + y * y + z * z), 0.97, 1.03);
            Assert.True(z > 0.5, $"texel {i} points sideways (z {z:F2})");
            sum += x; sumSq += x * x; count++;
        }
        double sd = Math.Sqrt(sumSq / count - (sum / count) * (sum / count));
        Assert.InRange(sd, 0.04, 0.45);
    }

    [Fact]
    public void The_normal_map_tiles_without_a_seam()
    {
        const int n = 128;
        var map = WaterNormalMap.Generate(n);
        Assert.Equal(n * n * 4, map.Length);
        double Step(int ax, int ay, int bx, int by)
        {
            double d = 0;
            for (int c = 0; c < 2; c++) d += Math.Abs(map[(ay * n + ax) * 4 + c] - map[(by * n + bx) * 4 + c]);
            return d;
        }
        double across = 0, inside = 0;
        for (int i = 0; i < n; i++)
        {
            across += Step(n - 1, i, 0, i) + Step(i, n - 1, i, 0);
            inside += Step(n / 2, i, n / 2 + 1, i) + Step(i, n / 2, i, n / 2 + 1);
        }
        Assert.True(across <= inside * 1.6 + 1, $"the seam steps {across} against {inside} inside the tile");
    }

    [Fact]
    public void The_normal_map_is_the_same_every_time()
    {
        var a = WaterNormalMap.Generate(64); var b = WaterNormalMap.Generate(64);
        Assert.Equal(64 * 64 * 4, a.Length);
        Assert.Equal(a, b);
    }
}
