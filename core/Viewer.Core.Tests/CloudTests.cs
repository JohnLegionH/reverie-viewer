// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text.RegularExpressions;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The cloud layer: the frame's cloud settings (parsed, defaulted, interpolated), the scroll clock, the noise picture, and the cloud
/// colour and opacity against an independent transcription of the official cloud vertex and fragment shaders (double precision, the
/// oracle below shares no code with CloudLighting). The expected behaviour comes from the official viewer's cloud shaders
/// (cloudsV.glsl, cloudsF.glsl), its dome mesh (planar texture coordinates), and its scroll accumulation.
/// </summary>
public class CloudTests
{
    private static string Synthetic => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "fixtures", "environment", "synthetic-environment.llsd.xml"));

    private const string CloudKeys =
        "<key>cloud_color</key><array><real>0.3</real><real>0.35</real><real>0.4</real></array>" +
        "<key>cloud_scale</key><real>0.7</real>" +
        "<key>cloud_pos_density1</key><array><real>1.5</real><real>0.25</real><real>0.9</real></array>" +
        "<key>cloud_pos_density2</key><array><real>2.5</real><real>0.75</real><real>0.125</real></array>" +
        "<key>cloud_scroll_rate</key><array><real>0.4</real><real>0.03</real></array>" +
        "<key>cloud_variance</key><real>0.2</real>" +
        "<key>cloud_id</key><uuid>0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0</uuid>";

    // ---- the settings ----

    [Fact]
    public void The_defaults_are_the_standard_eep_cloud_settings()
    {
        var d = CloudSettings.Default;
        Assert.Equal(new Vector3(0.4099f), d.Color);
        Assert.Equal(0.4199f, d.Scale);
        Assert.Equal(new Vector3(1f, 0.5260f, 1f), d.PosDensity1);
        Assert.Equal(new Vector3(1f, 0.5260f, 1f), d.PosDensity2);
        Assert.Equal(new Vector2(0.2f, 0.01f), d.ScrollRate);
        Assert.Equal(0f, d.Variance);
        Assert.Equal(new Guid("1dc1368f-e8fe-f02d-a08d-9d9f11c1af6b"), d.TextureId);
        Assert.Same(CloudSettings.Default, new SkyFrame(0, Vector3.UnitZ, Vector3.One, -Vector3.UnitZ, 0.5f, Vector3.One, Vector3.One, Vector3.One, 0.2f, 0.7f, 0.0002f, 0.8f).Cloud);
    }

    [Fact]
    public void The_cloud_keys_of_a_frame_are_read()
    {
        var llsd = Regex.Replace(Synthetic, "(<key>type</key><string>sky</string>)", "$1" + CloudKeys);
        var e = RegionEnvironment.FromLlsd(llsd, out var why);
        Assert.True(e is not null, why);
        var c = e!.Sky[0].Cloud;
        Assert.Equal(new Vector3(0.3f, 0.35f, 0.4f), c.Color);
        Assert.Equal(0.7f, c.Scale);
        Assert.Equal(new Vector3(1.5f, 0.25f, 0.9f), c.PosDensity1);
        Assert.Equal(new Vector3(2.5f, 0.75f, 0.125f), c.PosDensity2);
        Assert.Equal(new Vector2(0.4f, 0.03f), c.ScrollRate);
        Assert.Equal(0.2f, c.Variance);
        Assert.Equal(new Guid("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"), c.TextureId);
    }

    [Fact]
    public void A_frame_without_cloud_keys_takes_the_defaults()
    {
        var e = RegionEnvironment.FromLlsd(Synthetic, out _)!;
        Assert.Equal(CloudSettings.Default, e.Sky[0].Cloud);
    }

    [Fact]
    public void Between_keyframes_the_cloud_settings_are_interpolated()
    {
        var a = Frame(0f) with { Clouds = new(new(0.2f), 0.4f, new(1f, 0.2f, 1f), new(2f, 0.4f, 0.1f), new(0.2f, 0.0f), 0f, CloudSettings.DefaultTextureId) };
        var b = Frame(0.5f) with { Clouds = new(new(0.6f), 0.8f, new(3f, 0.6f, 0.5f), new(4f, 0.8f, 0.3f), new(0.6f, 0.4f), 0.4f, CloudSettings.DefaultTextureId) };
        var env = RegionEnvironment.Default with { Sky = [a, b] };
        var m = env.SkyAt(0.125).Cloud;   // a quarter of the way
        Assert.Equal(0.3f, m.Color.X, 4);
        Assert.Equal(0.5f, m.Scale, 4);
        Assert.Equal(1.5f, m.PosDensity1.X, 4);
        Assert.Equal(0.5f, m.PosDensity2.Y, 4);
        Assert.Equal(0.875f, m.PosDensity1.Z, 4);
        Assert.Equal(0.3f, m.ScrollRate.X, 4);
        Assert.Equal(0.1f, m.Variance, 4);
    }

    // ---- the scroll clock ----

    [Fact]
    public void The_scroll_accumulates_the_rate_over_one_hundred_per_second_and_x_is_negated_when_applied()
    {
        var s = CloudScroll.Advance(default, new Vector2(0.2f, 0.011f), 10);
        Assert.Equal(0.02f, s.X, 6); Assert.Equal(0.0011f, s.Y, 6);
        s = CloudScroll.Advance(s, new Vector2(0.2f, 0.011f), 5);
        Assert.Equal(0.03f, s.X, 6); Assert.Equal(0.00165f, s.Y, 6);
        var o = CloudScroll.Offset(new Vector3(1f, 0.526f, 1f), s);
        Assert.Equal(1f - 0.03f, o.X, 6); Assert.Equal(0.526f + 0.00165f, o.Y, 6);
    }

    // ---- the noise picture ----

    private static string NoiseFile => Path.Combine(TestPaths.RepoRoot, "core", "Viewer.Core", "World", "Data", "cloud-noise.tga");

    [Fact]
    public void The_noise_picture_is_the_bundled_file_decoded_through_its_palette()
    {
        var b = File.ReadAllBytes(NoiseFile);
        // independent reading: header 18 bytes, an id of b[0] bytes, a 24-bit palette of b[5] x 3 entries stored blue, green, red, then one byte per pixel
        int idLen = b[0], entries = b[5] | (b[6] << 8), w = b[12] | (b[13] << 8), h = b[14] | (b[15] << 8);
        Assert.Equal((1, 1, 8, 24), ((int)b[1], (int)b[2], (int)b[16], (int)b[7]));
        int pal = 18 + idLen, pix = pal + entries * 3;
        var n = CloudNoise.Default;
        Assert.Equal((w, h), (n.Width, n.Height));
        foreach (var (x, y) in new[] { (0, 0), (17, 3), (255, 300), (511, 511), (100, 480) })
        {
            float want = b[pal + b[pix + y * w + x] * 3 + 2] / 255f;   // row y counted from the first row stored (the bottom row)
            Assert.Equal(want, n.Sample(new Vector2((x + 0.5f) / w, (y + 0.5f) / h)), 4);
        }
        // the picture is not flat
        float min = 1, max = 0;
        for (int i = 0; i < 64; i++) { var v = n.Sample(new Vector2(i / 64f, (i * 7 % 64) / 64f)); min = MathF.Min(min, v); max = MathF.Max(max, v); }
        Assert.True(max - min > 0.3f, $"range {min}..{max}");
    }

    [Fact]
    public void The_noise_picture_wraps_and_blends_neighbours()
    {
        var n = CloudNoise.Default;
        Assert.Equal(512, n.Width);
        var p = new Vector2(0.3f, 0.7f);
        Assert.Equal(n.Sample(p), n.Sample(p + new Vector2(1, -2)), 5);
        float a = n.Sample(new Vector2(10.5f / 512, 20.5f / 512)), c = n.Sample(new Vector2(11.5f / 512, 20.5f / 512));
        Assert.Equal((a + c) / 2, n.Sample(new Vector2(11f / 512, 20.5f / 512)), 4);
        // across the seam: the last column blends with the first
        float l = n.Sample(new Vector2(511.5f / 512, 5.5f / 512)), f = n.Sample(new Vector2(0.5f / 512, 5.5f / 512));
        Assert.Equal((l + f) / 2, n.Sample(new Vector2(1f, 5.5f / 512)), 4);
    }

    // ---- the dome mapping ----

    [Theory]
    [InlineData(90, 0)]
    [InlineData(45, 0)]
    [InlineData(45, 90)]
    [InlineData(10, 200)]
    [InlineData(3, 315)]
    public void A_direction_maps_to_the_dome_point_it_hits(double elevation, double bearing)
    {
        // bearing counted from east towards north
        double e = elevation * Math.PI / 180, b = bearing * Math.PI / 180;
        var dir = new Vector3((float)(Math.Cos(e) * Math.Cos(b)), (float)(Math.Cos(e) * Math.Sin(b)), (float)Math.Sin(e));
        // the ray from 14400 above the centre of a sphere of radius 15000
        double H = 14400, R = 15000, bq = H * Math.Sin(e), r = -bq + Math.Sqrt(bq * bq - (H * H - R * R));
        double east = r * Math.Cos(e) * Math.Cos(b), north = r * Math.Cos(e) * Math.Sin(b);
        var uv = CloudLighting.Planar(dir);
        Assert.Equal((east / R + 1) / 2, uv.X, 4);
        Assert.Equal((north / R + 1) / 2, uv.Y, 4);
    }

    // ---- the cloud colours and opacity ----

    private static SkyFrame Frame(float key) => new(key, Vector3.UnitZ, new(0.73421f, 0.78158f, 0.9f), -Vector3.UnitZ, 0.5f, new(1.05f, 1.05f, 1.05f),
        new(0.49548f, 0.49548f, 0.64f), new(0.24476f, 0.44872f, 0.76f), 0.19f, 0.7f, 0.00018f, 0.8f, 1605f, 0.27f, new Vector3(5f, 0.001f, -0.48f));

    private static SkyFrame Midday => Frame(0.5f) with { SunDirection = Vector3.Normalize(new(0.5f, 0.2f, 0.84f)) };
    private static SkyFrame Sunset => RegionEnvironment.DefaultCycle.Sky[3] with { CloudShadow = 0.4f };
    private static SkyFrame Moonlit => RegionEnvironment.DefaultCycle.Sky[0];

    public static IEnumerable<object[]> Frames() { yield return [Midday]; yield return [Sunset]; yield return [Moonlit]; }

    [Theory]
    [MemberData(nameof(Frames))]
    public void The_vertex_terms_match_the_independent_transcription(SkyFrame f)
    {
        foreach (float e in new[] { 3f, 10f, 30f, 70f })
        foreach (float az in new[] { 0f, 45f, 120f, 180f })
        {
            var want = CloudOracle.Vertex(f, e, az);
            var got = CloudLighting.VertexTerms(f, e, az);
            Near(want.Sun, got.Sun, $"sun e{e} az{az}"); Near(want.Ambient, got.Ambient, $"ambient e{e} az{az}");
            Assert.Equal(want.Altitude, got.AltitudeBlend, 4);
            Assert.Equal(want.Density, got.Density, 5);
        }
    }

    [Fact]
    public void Below_the_horizon_the_altitude_blend_is_zero()
    {
        Assert.Equal(0f, CloudLighting.VertexTerms(Midday, -2f, 30f).AltitudeBlend);
        Assert.True(CloudLighting.VertexTerms(Midday, 40f, 30f).AltitudeBlend > 0.5f);
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void The_cloud_colour_and_opacity_match_the_independent_transcription(SkyFrame f)
    {
        var noise = CloudNoise.Default;
        int covered = 0, clear = 0, total = 0;
        foreach (var scrollSeconds in new[] { 0.0, 3600.0 })
        {
            var scroll = CloudScroll.Advance(default, f.Cloud.ScrollRate, scrollSeconds);
            for (int el = 8; el <= 80; el += 9)
            for (int br = 0; br < 360; br += 40)
            {
                double e = el * Math.PI / 180, b = br * Math.PI / 180;
                var dir = new Vector3((float)(Math.Cos(e) * Math.Cos(b)), (float)(Math.Cos(e) * Math.Sin(b)), (float)Math.Sin(e));
                var want = CloudOracle.Pixel(f, dir, scroll, noise);
                var got = CloudLighting.Evaluate(f, dir, scroll, noise);
                total++; if (want.Alpha > 0.05) covered++; if (want.Alpha == 0) clear++;
                Assert.True(Math.Abs(want.Alpha - got.Alpha) < 2e-3, $"alpha el{el} br{br}: want {want.Alpha}, got {got.Alpha}");
                Near(want.Colour, got.Colour, $"colour el{el} br{br}", 2e-3);
            }
        }
        // not vacuous: the sample holds both cloud and clear sky
        Assert.True(covered > total / 10, $"covered {covered} of {total}");
        Assert.True(clear > total / 10, $"clear {clear} of {total}");
    }

    [Fact]
    public void A_scroll_moves_the_pattern()
    {
        var noise = CloudNoise.Default;
        int differ = 0;
        for (int br = 0; br < 360; br += 30)
        {
            double e = 40 * Math.PI / 180, b = br * Math.PI / 180;
            var dir = new Vector3((float)(Math.Cos(e) * Math.Cos(b)), (float)(Math.Cos(e) * Math.Sin(b)), (float)Math.Sin(e));
            var a = CloudLighting.Evaluate(Midday, dir, default, noise); var c = CloudLighting.Evaluate(Midday, dir, new Vector2(0.15f, 0.1f), noise);
            if (Math.Abs(a.Alpha - c.Alpha) > 0.05f) differ++;
        }
        Assert.True(differ >= 2, $"{differ} directions changed");
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void The_shown_colour_over_the_sky_matches_the_independent_transcription_and_the_sky_where_there_is_no_cloud(SkyFrame f)
    {
        var noise = CloudNoise.Default;
        int mixed = 0;
        for (int el = 8; el <= 80; el += 9)
        for (int br = 0; br < 360; br += 40)
        {
            double e = el * Math.PI / 180, b = br * Math.PI / 180;
            var dir = new Vector3((float)(Math.Cos(e) * Math.Cos(b)), (float)(Math.Cos(e) * Math.Sin(b)), (float)Math.Sin(e));
            var want = CloudOracle.Shown(f, dir, default, noise);
            var got = CloudLighting.ShownOverSky(f, dir, default, noise);
            Near(want.Linear, got, $"shown el{el} br{br}", 3e-3);
            if (want.Alpha == 0)
            {
                var sky = SkyLighting.DomeColour(f, el, CloudOracle.AzimuthFromLight(f, dir));
                Near(new[] { (double)sky.X, sky.Y, sky.Z }, got, $"clear el{el} br{br}", 1e-5);
            }
            else if (want.Alpha > 0.5) mixed++;
        }
        Assert.True(mixed > 0, "no cloud pixel in the sample");
    }

    private static void Near(double[] want, Vector3 got, string what, double tol = 3e-4)
    {
        double[] g = [got.X, got.Y, got.Z];
        for (int i = 0; i < 3; i++) Assert.True(Math.Abs(g[i] - want[i]) <= tol + 1e-3 * Math.Abs(want[i]), $"{what}[{i}]: want {want[i]}, got {g[i]}");
    }
}

/// <summary>The official cloud shaders worked in double precision, line by line from cloudsV.glsl, cloudsF.glsl and the dome mesh. Shares no code with CloudLighting.</summary>
internal static class CloudOracle
{
    private const double R = 15000, H = 14400;

    private static (double X, double Y, double Z) LightEnu(SkyFrame f)
    {
        var d = f.SunDirection.Z >= 0 ? f.SunDirection : f.MoonDirection.Z >= 0 ? f.MoonDirection : -Vector3.UnitZ;
        return (d.X, d.Y, d.Z);
    }

    public static float AzimuthFromLight(SkyFrame f, Vector3 dir)
    {
        var l = LightEnu(f);
        double a = Math.Atan2(dir.Y, dir.X) - Math.Atan2(l.Y, l.X);
        double deg = Math.Abs(a * 180 / Math.PI) % 360; if (deg > 180) deg = 360 - deg;
        return (float)deg;
    }

    public static (double[] Sun, double[] Ambient, double Altitude, double Density) Vertex(SkyFrame f, double eDeg, double azDeg)
    {
        double e = eDeg * Math.PI / 180, az = azDeg * Math.PI / 180;
        double ux = Math.Cos(e) * Math.Cos(az), uy = Math.Sin(e), uz = Math.Cos(e) * Math.Sin(az);
        double bq = H * uy, r = -bq + Math.Sqrt(bq * bq - (H * H - R * R));
        double[] rel = [r * ux, r * uy + 50, r * uz];
        double maxY = f.MaxY;
        double alt = Math.Clamp((rel[1] + 512) / maxY, 0, 1);
        if (rel[1] > 0) { double k = maxY / rel[1]; for (int i = 0; i < 3; i++) rel[i] *= k; }
        if (rel[1] < 0) { alt = 0; double k = -32000.0 / rel[1]; for (int i = 0; i < 3; i++) rel[i] *= k; }
        double len = Math.Sqrt(rel[0] * rel[0] + rel[1] * rel[1] + rel[2] * rel[2]);
        double nx = rel[0] / len, ny = rel[1] / len;
        var l = LightEnu(f);
        double lx = Math.Sqrt(l.X * l.X + l.Y * l.Y), ly = Math.Max(l.Z, -0.1);
        bool sunUp = f.SunDirection.Z >= 0, moonUp = f.MoonDirection.Z >= 0;
        double factor = sunUp ? 1.0 : moonUp ? f.MoonBrightness * 0.25 : 0.0;
        var sc = sunUp ? f.SunColor : f.SunColor * 0.7f;
        double[] sunC = [sc.X, sc.Y, sc.Z], amb = [f.Ambient.X, f.Ambient.Y, f.Ambient.Z], bh = [f.BlueHorizon.X, f.BlueHorizon.Y, f.BlueHorizon.Z], bd = [f.BlueDensity.X, f.BlueDensity.Y, f.BlueDensity.Z];
        double[] cc = [f.Cloud.Color.X, f.Cloud.Color.Y, f.Cloud.Color.Z];
        double hd = f.HazeDensity, hh = f.HazeHorizon, dm = f.DensityMultiplier, cs = f.CloudShadow;
        double glow = Math.Max(1 - (nx * lx + ny * ly), 0.001) * f.Glow.X;
        glow = Math.Pow(glow, f.Glow.Z) * factor;
        glow = factor < 1 ? 0 : glow + 0.25;
        double offAxisSky = 1.0 / Math.Max(1e-6, Math.Max(0, ny) + ly), offAxisCloud = 1.0 / Math.Max(1e-6, ly * 2);
        var sun = new double[3]; var ambient = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double atten = (bd[i] + hd * 0.25) * (dm * maxY);
            double combined = Math.Abs(bd[i]) + Math.Abs(hd), bw = bd[i] / combined, hw = hd / combined;
            double sl = sunC[i] * Math.Exp(-atten * offAxisSky);
            double trans = Math.Exp(-combined * (len * dm));
            double tmpAmb = amb[i] + (1 - amb[i]) * cs * 0.5;
            sl *= 1 - cs;
            double below = bh[i] * bw * (sl + tmpAmb) + hh * hw * (sl * glow + tmpAmb);
            double cloudSun = sunC[i] * Math.Exp(-atten * offAxisCloud);
            double cSun = cloudSun * glow * cc[i], cAmb = tmpAmb * cc[i];
            double t = Math.Sqrt(trans);
            cSun *= t; cAmb *= t;
            cAmb += below * (1 - t);
            sun[i] = cSun; ambient[i] = cAmb;
        }
        return (sun, ambient, alt, 2 * (cs - 0.25));
    }

    private static double Lerp01(double v) => Math.Min(Math.Max(v, 0), 1);

    public static (double[] Colour, double Alpha) Pixel(SkyFrame f, Vector3 dir, Vector2 scroll, CloudNoise noise)
    {
        double e = Math.Asin(dir.Z), az = AzimuthFromLight(f, dir);
        var v = Vertex(f, e * 180 / Math.PI, az);
        var c = f.Cloud;
        // the dome point the view ray meets
        double bq = H * dir.Z, r = -bq + Math.Sqrt(bq * bq - (H * H - R * R));
        double u = ((r * dir.X) / R + 1) / 2, w = ((r * dir.Y) / R + 1) / 2;
        // cloudsV: flip x, centre, divide by the scale
        double[] t0 = [(-u - 0.5) / c.Scale + 0.5, (w - 0.5) / c.Scale + 0.5];
        var l = LightEnu(f);
        // the light in the dome's axes: x is minus north, z is minus east
        double[] t1 = [t0[0] + (-l.Y) * 0.0125, t0[1] + (-l.X) * 0.0125];
        double[] t2 = [t0[0] * 16, t0[1] * 16], t3 = [t1[0] * 16, t1[1] * 16];
        double d1x = c.PosDensity1.X - scroll.X, d1y = c.PosDensity1.Y + scroll.Y;
        double N(double x, double y) => noise.Sample(new Vector2((float)x, (float)y));
        if (c.Scale < 0.001) return ([0, 0, 0], 0);
        double[] uv1 = [t0[0], t0[1]], uv2 = [t1[0], t1[1]], uv3 = [t2[0], t2[1]], uv4 = [t3[0], t3[1]];
        double k = c.Variance * (1.0 - c.Scale * 0.25);
        double[] dist = [N(uv1[0] / 8, uv1[1] / 8) * k, N((uv3[0] + uv1[0]) / 16, (uv3[1] + uv1[1]) / 16) * k];
        double[] dist2 = [N((uv1[0] + uv3[0]) / 4, (uv1[1] + uv3[1]) / 4) * k, N((uv4[0] + uv2[0]) / 8, (uv4[1] + uv2[1]) / 8) * k];
        uv1[0] += d1x + dist[0] * 0.2; uv1[1] += d1y + dist[1] * 0.2;
        uv2[0] += d1x; uv2[1] += d1y;
        uv3[0] += c.PosDensity2.X; uv3[1] += c.PosDensity2.Y;
        uv4[0] += c.PosDensity2.X; uv4[1] += c.PosDensity2.Y;
        double variance = Math.Min(1.0, (dist[0] * 2 + dist[1] * 2 + dist2[0] + dist2[1]) * 4);
        double density = v.Density * (1 - variance * variance);
        double a1 = (N(uv1[0], uv1[1]) - 0.5) + (N(uv3[0], uv3[1]) - 0.5) * c.PosDensity2.Z;
        a1 = Math.Min(Math.Max(a1 + density, 0) * 10 * c.PosDensity1.Z, 1);
        a1 = 1 - a1 * a1; a1 = 1 - a1 * a1;
        a1 *= v.Altitude; a1 = Lerp01(a1);
        double a2 = N(uv2[0], uv2[1]) - 0.5;
        a2 = Math.Min(Math.Max(a2 + density, 0) * 2.5 * c.PosDensity1.Z, 1);
        a2 = 1 - a2; a2 = 1 - a2 * a2;
        var col = new double[3];
        for (int i = 0; i < 3; i++) col[i] = Lerp01(v.Sun[i] * (1 - a2) + v.Ambient[i]) * 2.0;
        // the stored value: eight bits, so clamped to one
        for (int i = 0; i < 3; i++) col[i] = Math.Min(col[i], 1.0);
        return (col, a1);
    }

    private static double Decode(double s) => s < 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);

    public static (double[] Linear, double Alpha) Shown(SkyFrame f, Vector3 dir, Vector2 scroll, CloudNoise noise)
    {
        double e = Math.Asin(dir.Z) * 180 / Math.PI, az = AzimuthFromLight(f, dir);
        var p = Pixel(f, dir, scroll, noise);
        // the sky's stored number: the vertex colour times two, clamped (skyF.glsl), the sky vertex term is the existing dome transcription
        var sky = SkyLighting.DomeVertexColour(f, (float)e, (float)az);
        double[] s = [sky.X, sky.Y, sky.Z];
        var lin = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double skyNumber = Math.Min(Math.Max(s[i] * 2, 0), 1);
            double mixed = skyNumber * (1 - p.Alpha) + p.Colour[i] * p.Alpha;
            lin[i] = Math.Min(Math.Max(Decode(mixed) * 1.1, 0), 1);
        }
        return (lin, p.Alpha);
    }
}
