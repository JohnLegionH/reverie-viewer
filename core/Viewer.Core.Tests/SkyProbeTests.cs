// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The sky probe's irradiance map (the default reflection probe of the 26.4 viewer), checked against an independent transcription of the official chain written
/// out here in double precision: the capture of the sky at four times the probe resolution (updateProbeFace: the sky pass of the irradiance pass, with the sky's
/// ambient set to zero and its blue horizon and blue density replaced by grey of their lightness, llsettingsvo.cpp draw_color), the nine-tap Gaussian blur of
/// gaussianF.glsl in each direction, the bilinear fetch down to 128 per face and the box mip chain (reflectionmipF.glsl), then irradianceGenF.glsl: 32 cosine-weighted
/// Hammersley samples per output texel, each read at mip 6 (the 2 x 2 level, seamless across faces), averaged. The code under test evaluates the capture directly at
/// the 128 texel centres, so the comparison also bounds what leaving out the supersample and the blur changes.
/// </summary>
public class SkyProbeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly SkyFrame Midday = new(0.5f, Vector3.UnitZ, new(0.73421f, 0.78158f, 0.9f), -Vector3.UnitZ, 0.5f, new(1.05f, 1.05f, 1.05f),
        new(0.49548f, 0.49548f, 0.64f), new(0.24476f, 0.44872f, 0.76f), 0.19f, 0.7f, 0.00018f, 0.8f, 1605f, 0.27f, new Vector3(5f, 0.001f, -0.48f), null, 0.5f);
    private static readonly SkyFrame Afternoon = Midday with { SunDirection = Vector3.Normalize(new(0.6f, 0.2f, 0.5f)), Ambient = new(0.5f, 0.45f, 0.4f), CloudShadow = 0.1f };

    public static IEnumerable<object[]> Frames() { yield return [nameof(Midday)]; yield return [nameof(Afternoon)]; }
    private static SkyFrame Frame(string n) => n == nameof(Midday) ? Midday : Afternoon;

    // ---- the independent transcription (double precision, plain arrays; directions in the viewer's y-up frame) ----

    private static (double x, double y, double z) Dir(int face, double s, double t)
    {
        double sc = 2 * s - 1, tc = 2 * t - 1;
        return face switch
        {
            0 => (1, -tc, -sc), 1 => (-1, -tc, sc), 2 => (sc, 1, tc), 3 => (sc, -1, -tc), 4 => (sc, -tc, 1), _ => (-sc, -tc, -1),
        };
    }

    private static (int face, double s, double t) ToFace(double x, double y, double z)
    {
        double ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
        int face; double sc, tc, ma;
        if (ax >= ay && ax >= az) { ma = ax; if (x > 0) { face = 0; sc = -z; tc = -y; } else { face = 1; sc = z; tc = -y; } }
        else if (ay >= az) { ma = ay; if (y > 0) { face = 2; sc = x; tc = z; } else { face = 3; sc = x; tc = -z; } }
        else { ma = az; if (z > 0) { face = 4; sc = x; tc = -y; } else { face = 5; sc = -x; tc = -y; } }
        return (face, (sc / ma + 1) / 2, (tc / ma + 1) / 2);
    }

    /// <summary>The sky colour in a direction on the irradiance pass: the frame with no ambient and grey blue horizon and blue density.</summary>
    private static double[] SkyAt(SkyFrame f, double gx, double gy, double gz, Func<double, double, double, double[]?>? ground = null)
    {
        if (ground?.Invoke(gx, gy, gz) is { } hit) return hit;
        float Lightness(Vector3 c) => (Math.Max(c.X, Math.Max(c.Y, c.Z)) + Math.Min(c.X, Math.Min(c.Y, c.Z))) / 2f;
        var g = f with { Ambient = Vector3.Zero, BlueHorizon = new Vector3(Lightness(f.BlueHorizon)), BlueDensity = new Vector3(Lightness(f.BlueDensity)) };
        double len = Math.Sqrt(gx * gx + gy * gy + gz * gz);
        double sx = gx / len, sy = -gz / len, sz = gy / len;   // the sky's frame: z up
        double elev = Math.Max(Math.Asin(Math.Clamp(sz, -1, 1)) * 180 / Math.PI, SkyLighting.DomeFloorDegrees);
        bool sunUp = f.SunDirection.Z >= 0f, moonUp = f.MoonDirection.Z >= 0f;
        var ld = sunUp ? f.SunDirection : moonUp ? f.MoonDirection : -Vector3.UnitZ;
        double d = Math.Atan2(sy, sx) - Math.Atan2(ld.Y, ld.X);
        d = Math.Abs(Math.IEEERemainder(d, 2 * Math.PI));
        var c = SkyLighting.DomeColourHdr(g, (float)elev, (float)(d * 180 / Math.PI));
        return [c.X, c.Y, c.Z];
    }

    private static double[][][] Official512(SkyFrame f, Func<double, double, double, double[]?>? ground = null)
    {
        const int big = 512;
        var cap = new double[6][][];
        Parallel.For(0, 6, face =>
        {
            cap[face] = new double[big * big][];
            for (int y = 0; y < big; y++)
                for (int x = 0; x < big; x++)
                {
                    var (dx, dy, dz) = Dir(face, (x + 0.5) / big, (y + 0.5) / big);
                    cap[face][y * big + x] = SkyAt(f, dx, dy, dz, ground);
                }
        });
        return cap;
    }

    private static readonly double[] Gauss = [0.0002, 0.0060, 0.0606, 0.2417, 0.3829, 0.2417, 0.0606, 0.0060, 0.0002];

    private static double[][] Blur(double[][] img, int size, bool horizontal)
    {
        var o = new double[size * size][];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var acc = new double[3];
                for (int i = 0; i < 9; i++)
                {
                    int xx = horizontal ? Math.Clamp(x + i - 4, 0, size - 1) : x, yy = horizontal ? y : Math.Clamp(y + i - 4, 0, size - 1);
                    var p = img[yy * size + xx];
                    for (int k = 0; k < 3; k++) acc[k] += p[k] * Gauss[i];
                }
                o[y * size + x] = acc;
            }
        return o;
    }

    /// <summary>One bilinear fetch of the previous level at each texel centre of a level half its size: the average of the 2 x 2 block (exact for a centred fetch).</summary>
    private static double[][] Half(double[][] img, int size)
    {
        int h = size / 2;
        var o = new double[h * h][];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < h; x++)
            {
                var acc = new double[3];
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                        for (int k = 0; k < 3; k++) acc[k] += img[(2 * y + dy) * size + 2 * x + dx][k] / 4;
                o[y * h + x] = acc;
            }
        return o;
    }

    private static double[] Fetch(double[][][] level, int size, double x, double y, double z)
    {
        var (face, s, t) = ToFace(x, y, z);
        double u = s * size - 0.5, v = t * size - 0.5;
        int u0 = (int)Math.Floor(u), v0 = (int)Math.Floor(v);
        double fu = u - u0, fv = v - v0;
        var acc = new double[3];
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                int tu = u0 + i, tv = v0 + j;
                double w = (i == 0 ? 1 - fu : fu) * (j == 0 ? 1 - fv : fv);
                int fc = face; int px = tu, py = tv;
                if (tu < 0 || tu >= size || tv < 0 || tv >= size)
                {   // across an edge: the texel centre's direction on the extended face, seen from the neighbour
                    var (ex, ey, ez) = Dir(face, (tu + 0.5) / size, (tv + 0.5) / size);
                    var (nf, ns, nt) = ToFace(ex, ey, ez);
                    fc = nf; px = Math.Clamp((int)Math.Floor(ns * size), 0, size - 1); py = Math.Clamp((int)Math.Floor(nt * size), 0, size - 1);
                }
                var p = level[fc][py * size + px];
                for (int k = 0; k < 3; k++) acc[k] += p[k] * w;
            }
        return acc;
    }

    // the base-2 Van der Corput value of i: its binary digits read after the point in reverse order (sum of digit_k / 2^(k+1))
    private static double Radical(uint i)
    {
        double value = 0, weight = 0.5;
        for (; i != 0; i >>= 1, weight /= 2) if ((i & 1) != 0) value += weight;
        return value;
    }

    private static double[][][] OfficialIrradiance(SkyFrame f, Func<double, double, double, double[]?>? ground = null)
    {
        var cap = Official512(f, ground);
        // blur and fetch down to 128, then the mip chain to 2 x 2
        var mips = new double[7][][][];
        for (int level = 0; level < 7; level++) mips[level] = new double[6][][];
        for (int face = 0; face < 6; face++)
        {
            var b = Blur(Blur(cap[face], 512, true), 512, false);
            var m = Half(Half(b, 512), 256);   // 128
            mips[0][face] = m;
            int size = 128;
            for (int level = 1; level < 7; level++) { m = Half(m, size); size /= 2; mips[level][face] = m; }
        }
        var irr = new double[6][][];
        for (int face = 0; face < 6; face++)
        {
            irr[face] = new double[256][];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    var (nx, ny, nz) = Dir(face, (x + 0.5) / 16, (y + 0.5) / 16);
                    double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz); nx /= nl; ny /= nl; nz /= nl;
                    // a frame around the normal built from the world's up axis
                    double tx = 1 * nz - 0 * ny, ty = 0 * nx - 0 * nz, tz = 0 * ny - 1 * nx;   // cross((0,1,0), n)
                    double tl = Math.Sqrt(tx * tx + ty * ty + tz * tz); tx /= tl; ty /= tl; tz /= tl;
                    double bx = ny * tz - nz * ty, by = nz * tx - nx * tz, bz = nx * ty - ny * tx;   // cross(n, tangent)
                    var acc = new double[3];
                    for (int i = 0; i < 32; i++)
                    {
                        double xi1 = i / 32.0, xi2 = Radical((uint)i);
                        double cosT = Math.Sqrt(1 - xi2), sinT = Math.Sqrt(xi2), phi = 2 * Math.PI * xi1;
                        double lx = sinT * Math.Cos(phi), ly = sinT * Math.Sin(phi), lz = cosT, ll = Math.Sqrt(lx * lx + ly * ly + lz * lz);
                        lx /= ll; ly /= ll; lz /= ll;
                        double hx = tx * lx + bx * ly + nx * lz, hy = ty * lx + by * ly + ny * lz, hz = tz * lx + bz * ly + nz * lz;
                        double pdf = cosT / Math.PI;
                        double lod = 0.5 * Math.Log2(6.0 * 64 * 64 / (32 * pdf)) + 2.0;
                        lod = Math.Clamp(lod, 0, 6);
                        Assert.Equal(6.0, lod, 9);   // every sample reads the 2 x 2 level
                        var c = Fetch(mips[6], 2, hx, hy, hz);
                        for (int k = 0; k < 3; k++) acc[k] += c[k] / 32;
                    }
                    irr[face][y * 16 + x] = acc;
                }
        }
        return irr;
    }

    private static readonly Dictionary<string, double[][][]> Cache = [];
    private static double[][][] Official(string frame)
    {
        lock (Cache) { if (!Cache.TryGetValue(frame, out var v)) Cache[frame] = v = OfficialIrradiance(Frame(frame)); return v; }
    }

    [Theory, MemberData(nameof(Frames))]
    public void The_generated_irradiance_map_matches_the_official_chain_texel_by_texel(string frame)
    {
        var want = Official(frame);
        var got = SkyProbe.Irradiance(Frame(frame));
        Assert.Equal(6, got.Length);
        double worst = 0; string where = "";
        for (int face = 0; face < 6; face++)
        {
            Assert.Equal(256, got[face].Length);
            for (int i = 0; i < 256; i++)
            {
                float[] g = [got[face][i].X, got[face][i].Y, got[face][i].Z];
                for (int k = 0; k < 3; k++)
                {
                    double err = Math.Abs(g[k] - want[face][i][k]) / Math.Max(want[face][i][k], 0.02);
                    if (err > worst) { worst = err; where = $"face {face} texel {i} channel {k}: want {want[face][i][k]:F5}, got {g[k]:F5}"; }
                }
            }
        }
        output.WriteLine($"{frame}: worst relative error {worst:P3}");
        Assert.True(worst <= 0.01, $"worst relative error {worst:P2} ({where})");
    }

    [Theory, MemberData(nameof(Frames))]
    public void Sampling_the_map_in_a_direction_matches_the_official_map_sampled_the_same_way(string frame)
    {
        var want = Official(frame);
        var got = SkyProbe.Irradiance(Frame(frame));
        var rng = new Random(7);
        double worst = 0; string where = "";
        for (int n = 0; n < 200; n++)
        {
            double x = rng.NextDouble() * 2 - 1, y = rng.NextDouble() * 2 - 1, z = rng.NextDouble() * 2 - 1;
            double l = Math.Sqrt(x * x + y * y + z * z); if (l < 0.1) continue;
            x /= l; y /= l; z /= l;
            var level = new double[6][][]; for (int f = 0; f < 6; f++) level[f] = want[f];
            var w = Fetch(level, 16, x, y, z);
            var g = SkyProbe.SampleIrradiance(got, new Vector3((float)x, (float)y, (float)z));
            float[] gv = [g.X, g.Y, g.Z];
            for (int k = 0; k < 3; k++)
            {
                double err = Math.Abs(gv[k] - w[k]) / Math.Max(w[k], 0.02);
                if (err > worst) { worst = err; where = $"dir ({x:F3},{y:F3},{z:F3}) channel {k}: want {w[k]:F5}, got {gv[k]:F5}"; }
            }
        }
        output.WriteLine($"{frame}: worst relative error sampling {worst:P3}");
        Assert.True(worst <= 0.011, $"worst relative error {worst:P2} ({where})");
    }

    [Fact]
    public void The_ambient_mixes_the_atmospheric_ambient_with_the_probe_by_the_ambiance_and_the_probe_term_is_scaled_by_it()
    {
        var amb = new Vector3(0.2f);
        var irr = new Vector3(0.6f);
        // ambiance 0.5: mix(0.2, 0.6 * 0.5, 0.5) = 0.25
        Assert.Equal(0.25f, SkyProbe.Ambient(amb, irr, 0.5f).X, 5);
        // ambiance 0: the atmospheric ambient alone
        Assert.Equal(0.2f, SkyProbe.Ambient(amb, irr, 0f).X, 5);
        // ambiance 2: the mix is capped at 1 but the probe term is still times 2
        Assert.Equal(1.2f, SkyProbe.Ambient(amb, irr, 2f).X, 5);
    }

    [Fact]
    public void The_lookup_direction_is_the_normal_tilted_by_the_probe_being_64_m_above_the_camera()
    {
        // a surface at the camera facing east: the probe's centre is 64 m above, so the direction to the 4096 m sphere along the normal sits 64 m below the horizon plane of the centre
        var d = SkyProbe.LookupDirection(new Vector3(10, 2, 5), Vector3.UnitX, new Vector3(10, 2, 5));
        var n = Vector3.Normalize(d);
        Assert.True(n.X > 0.9998f);
        Assert.Equal(-64f / 4096f, n.Y, 3);
        Assert.Equal(0f, n.Z, 4);
        // far from the probe's centre the tilt follows: a surface 1000 m to the north of the camera, normal up
        var up = Vector3.Normalize(SkyProbe.LookupDirection(new Vector3(10, 2, -995), Vector3.UnitY, new Vector3(10, 2, 5)));
        Assert.Equal(-1000f / 4096f * 0.97f, up.Z, 1);
    }

    // ---- a world under the sky: terrain and water drawn over it, as the official capture of the default probe holds them ----

    private const float GroundHeight = 64f, GroundFar = 512f;
    private static readonly Vector3 GroundRadiance = new(0.30f, 0.22f, 0.12f);

    private static double[]? GroundModel(double gx, double gy, double gz)
    {
        double len = Math.Sqrt(gx * gx + gy * gy + gz * gz);
        double ny = gy / len;
        if (ny >= -1e-9) return null;
        double depth = GroundHeight / -ny * Math.Max(Math.Abs(gx / len), Math.Max(Math.Abs(ny), Math.Abs(gz / len)));   // the camera's depth along the face's axis
        return depth <= GroundFar ? [GroundRadiance.X, GroundRadiance.Y, GroundRadiance.Z] : null;
    }

    private static int Level(double linear)
    {
        double v = Math.Clamp(linear, 0, 1);
        return (int)Math.Round((v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055) * 255);
    }

    [Theory, MemberData(nameof(Frames))]
    public void A_flat_ground_of_known_colour_under_the_sky_gives_the_official_irradiance_within_one_level(string frame)
    {
        var f = Frame(frame);
        var want = OfficialIrradiance(f, GroundModel);
        var got = SkyProbe.Irradiance(f, 1f, SkyProbe.FlatGround(GroundRadiance, GroundHeight, GroundFar));
        int worst = 0; string where = "";
        for (int face = 0; face < 6; face++)
            for (int i = 0; i < 256; i++)
            {
                float[] g = [got[face][i].X, got[face][i].Y, got[face][i].Z];
                for (int k = 0; k < 3; k++)
                {
                    int e = Math.Abs(Level(g[k]) - Level(want[face][i][k]));
                    if (e > worst) { worst = e; where = $"face {face} texel {i} channel {k}: want {want[face][i][k]:F5}, got {g[k]:F5}"; }
                }
            }
        output.WriteLine($"{frame}: worst difference {worst} levels");
        Assert.True(worst <= 1, $"worst difference {worst} levels ({where})");
    }

    [Fact]
    public void The_ground_pulls_the_irradiance_of_a_normal_that_looks_down_towards_its_colour_and_leaves_one_that_looks_up_alone()
    {
        var without = SkyProbe.Irradiance(Midday);
        var with = SkyProbe.Irradiance(Midday, 1f, SkyProbe.FlatGround(GroundRadiance, GroundHeight, GroundFar));
        var w0 = SkyProbe.SampleIrradiance(without, -Vector3.UnitY);
        var w1 = SkyProbe.SampleIrradiance(with, -Vector3.UnitY);
        // the sky below the horizon is a pale blue and the ground a warm brown: with the ground in view the downward ambient is warmer, and every channel is nearer the ground's
        Assert.True(w1.X / w1.Z > w0.X / w0.Z * 1.3f, $"the downward irradiance did not turn warmer: {w0} to {w1}");
        Assert.True(MathF.Abs(w1.X - GroundRadiance.X) < MathF.Abs(w0.X - GroundRadiance.X) && MathF.Abs(w1.Y - GroundRadiance.Y) < MathF.Abs(w0.Y - GroundRadiance.Y)
            && MathF.Abs(w1.Z - GroundRadiance.Z) < MathF.Abs(w0.Z - GroundRadiance.Z), $"not nearer the ground's colour: {w0} to {w1}, ground {GroundRadiance}");
        var up = SkyProbe.SampleIrradiance(with, Vector3.UnitY) - SkyProbe.SampleIrradiance(without, Vector3.UnitY);
        Assert.True(MathF.Abs(up.X) < 0.02f && MathF.Abs(up.Y) < 0.02f && MathF.Abs(up.Z) < 0.02f, $"the upward irradiance moved by {up}");
    }

    [Fact]
    public void Ground_lit_by_the_sun_alone_has_the_suns_colour_times_the_albedo_times_the_angle()
    {
        var pass = SkyProbe.IrradiancePassFrame(Midday);
        var sun = SkyLighting.Eep(pass).Sun;
        var r = SkyProbe.SunLitRadiance(Midday, new Vector3(0.5f, 0.4f, 0.2f));
        Assert.True(sun.X > 0.1f);
        Assert.Equal(0.5f * sun.X * Midday.SunDirection.Z, r.X, 5);
        Assert.Equal(0.2f * sun.Z * Midday.SunDirection.Z, r.Z, 5);
        // a sun below the horizon lights nothing
        var night = SkyProbe.SunLitRadiance(Midday with { SunDirection = -Vector3.UnitZ, MoonDirection = -Vector3.UnitZ }, new Vector3(0.5f));
        Assert.Equal(0f, night.X, 6);
    }
}
