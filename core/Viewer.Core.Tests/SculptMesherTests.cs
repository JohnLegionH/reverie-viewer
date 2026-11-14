// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using Viewer.Core.Meshing;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Sculpted prims (the test strategy 3.2). The drawing path is the official sampler, written from the
/// behaviour spec in Docs/SCULPT-SAMPLER-SPEC.md; the expected grids and sample positions below are derived by hand from
/// that spec. The web viewer's port is kept as a documented REFERENCE (fixtures/sculpts): its 208 cases still match the
/// port, and <see cref="How_the_official_sampler_differs_from_the_web_viewer_reference"/> records where the two agree.
/// </summary>
public class SculptMesherTests
{
    private static string Dir => Path.Combine(TestPaths.RepoRoot, "fixtures", "sculpts");

    private static readonly Lazy<JsonDocument> Reference = new(() =>
    {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(Dir, "web-viewer-oracle.json.gz")), CompressionMode.Decompress);
        return JsonDocument.Parse(gz);
    });

    private static (int W, int H, byte[] Rgba) Map(string name)
    {
        var m = Reference.Value.RootElement.GetProperty("maps").GetProperty(name);
        int w = m.GetProperty("width").GetInt32(), h = m.GetProperty("height").GetInt32();
        var rgb = Convert.FromBase64String(m.GetProperty("rgb").GetString()!);
        var rgba = new byte[w * h * 4];
        for (int p = 0; p < w * h; p++) { rgba[p * 4] = rgb[p * 3]; rgba[p * 4 + 1] = rgb[p * 3 + 1]; rgba[p * 4 + 2] = rgb[p * 3 + 2]; rgba[p * 4 + 3] = 255; }
        return (w, h, rgba);
    }

    private static byte[] Solid(int w, int h, byte r, byte g, byte b) => Gen(w, h, (_, _) => (r, g, b));

    private static byte[] Gen(int w, int h, Func<int, int, (byte, byte, byte)> f)
    {
        var a = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) { var (r, g, b) = f(x, y); int i = (y * w + x) * 4; a[i] = r; a[i + 1] = g; a[i + 2] = b; a[i + 3] = 255; }
        return a;
    }

    private static Vector3 P(MeshFace f, int v) => new(f.Positions[v * 3], f.Positions[v * 3 + 1], f.Positions[v * 3 + 2]);

    /// <summary>Signed volume (divergence theorem): positive when the surface faces outward.</summary>
    private static double SignedVolume(MeshFace f)
    {
        double v = 0;
        for (int t = 0; t < f.Indices.Length; t += 3)
            v += Vector3.Dot(P(f, f.Indices[t]), Vector3.Cross(P(f, f.Indices[t + 1]), P(f, f.Indices[t + 2]))) / 6.0;
        return v;
    }

    // ---- the reference (web viewer port) ----

    [Fact]
    public void The_reference_port_still_matches_the_web_viewer_fixtures()
    {
        int n = 0;
        var seen = new HashSet<(int Type, bool Mirror, bool Invert)>();
        foreach (var c in Reference.Value.RootElement.GetProperty("cases").EnumerateArray())
        {
            string map = c.GetProperty("map").GetString()!;
            int type = c.GetProperty("type").GetInt32(), lod = c.GetProperty("lod").GetInt32();
            bool mirror = c.GetProperty("mirror").GetBoolean(), invert = c.GetProperty("invert").GetBoolean();
            string at = $"{map} type {type} lod {lod} mirror {mirror} invert {invert}";
            var (w, h, rgba) = Map(map);
            var f = SculptMesher.BuildReference(rgba, w, h, type, mirror, invert, lod);
            Assert.True(f is not null, at);
            Assert.Equal(c.GetProperty("indices").EnumerateArray().Select(e => e.GetInt32()).ToArray(), f!.Indices);
            Near(c.GetProperty("positions"), f.Positions, 2e-6, at + " positions");
            Near(c.GetProperty("uvs"), f.Uvs, 2e-6, at + " uvs");
            Near(c.GetProperty("normals"), f.Normals, 2e-5, at + " normals");
            seen.Add((type, mirror, invert));
            n++;
        }
        Assert.Equal(208, n);
        foreach (int t in new[] { 1, 2, 3, 4 })
            foreach (var (m, i) in new[] { (false, false), (true, false), (false, true), (true, true) })
                Assert.Contains((t, m, i), seen);
    }

    private static void Near(JsonElement expected, float[] actual, double tol, string at)
    {
        var e = expected.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        Assert.True(e.Length == actual.Length, $"{at}: length {actual.Length}, reference {e.Length}");
        for (int i = 0; i < e.Length; i++)
            Assert.True(Math.Abs(e[i] - actual[i]) <= tol, $"{at}[{i}]: {actual[i]} vs reference {e[i]}");
    }

    /// <summary>
    /// The official sampler against the reference at the same side budget (reference lod 6/8/16/32 = LOD 0-3). Recorded
    /// result: 207 cases compared, 42 with the same vertex count (square 64 and 256 maps at LOD 1-3, the 2:1 noise map at
    /// LOD 2). found 3 identical planes; (SL reads the map bottom-up and winds the other
    /// way) none is identical: every case differs in grid size, row order, winding or several.
    /// </summary>
    [Fact]
    public void How_the_official_sampler_differs_from_the_web_viewer_reference()
    {
        var sameSize = new List<string>();
        var identical = new List<string>();
        int compared = 0;
        foreach (var c in Reference.Value.RootElement.GetProperty("cases").EnumerateArray())
        {
            int sides = c.GetProperty("lod").GetInt32();
            if (sides == 64) continue;   // not an official budget
            string map = c.GetProperty("map").GetString()!;
            int type = c.GetProperty("type").GetInt32();
            bool mirror = c.GetProperty("mirror").GetBoolean(), invert = c.GetProperty("invert").GetBoolean();
            var (w, h, rgba) = Map(map);
            var official = SculptMesher.BuildOfficial(rgba, w, h, type, mirror, invert, sides);
            var reference = SculptMesher.BuildReference(rgba, w, h, type, mirror, invert, sides)!;
            compared++;
            string key = $"{map}/{type}/{sides}/{(mirror ? "m" : "")}{(invert ? "i" : "")}";
            if (official.Vertices != reference.Vertices) continue;
            sameSize.Add(key);
            if (official.Indices.SequenceEqual(reference.Indices) && official.Positions.Zip(reference.Positions).All(p => Math.Abs(p.First - p.Second) < 1e-6))
                identical.Add(key);
        }
        Assert.Equal(207, compared);
        Assert.Equal(42, sameSize.Count);
        // identical only for planes on maps of exactly 4 x sides^2 pixels (the reference 2x-samples them without
        // resizing, the official sampler reads every second pixel too) with no net column reversal
        // with SL's bottom-up row order and winding, no case is identical any more (it was 3 before)
        Assert.Empty(identical);
    }

    // ---- the official sampler (spec sections 1-3), hand-derived ----

    [Theory]
    // square: sides x sides
    [InlineData(64, 64, 0, 6, 6)]
    [InlineData(64, 64, 1, 8, 8)]
    [InlineData(64, 64, 2, 16, 16)]
    [InlineData(64, 64, 3, 32, 32)]
    // 2:1 - V = 1024: s = floor(sqrt(512)) = 22, t = 1024 / 22 = 46, s = 1024 / 46 = 22
    [InlineData(128, 64, 3, 22, 46)]
    // 2:1 at LOD 0 - V = 36: s = floor(sqrt(18)) = 4, t = 9, s = 4
    [InlineData(128, 64, 0, 4, 9)]
    // 4:1 at LOD 2 - V = 256: s = floor(sqrt(64)) = 8, t = 32, s = 8
    [InlineData(64, 16, 2, 8, 32)]
    // 1:2 (tall) at LOD 3 - V = 1024: s = floor(sqrt(2048)) = 45, t = 1024 / 45 = 22, s = 1024 / 22 = 46
    [InlineData(64, 128, 3, 46, 22)]
    // long thin: V = 1024 (4096 / 4 = 1024 does not reduce it): s = floor(sqrt(0.25)) = 0 -> 4, t = 256, s = 4
    [InlineData(4096, 1, 3, 4, 256)]
    [InlineData(4096, 1, 0, 4, 9)]
    // small: 8 x 8 / 4 = 16 < 1024: V = 16, s = t = 4
    [InlineData(8, 8, 3, 4, 4)]
    // 1 x 1: 1 / 4 = 0 keeps the full budget
    [InlineData(1, 1, 0, 6, 6)]
    public void The_grid_follows_the_spec(int w, int h, int lod, int s, int t)
    {
        Assert.Equal((s, t), SculptMesher.OfficialGrid(w, h, SculptMesher.SidesForLod(lod)));
        var f = SculptMesher.BuildOfficial(Solid(w, h, 10, 200, 30), w, h, SculptMesher.Plane, false, false, SculptMesher.SidesForLod(lod));
        Assert.Equal((s + 1) * (t + 1), f.Vertices);
    }

    [Fact]
    public void The_segment_budget_never_exceeds_the_official_grid()
    {
        foreach (int w in new[] { 1, 2, 3, 7, 16, 33, 64, 100, 128, 256, 1024, 4096 })
            foreach (int h in new[] { 1, 2, 5, 16, 64, 128, 512, 4096 })
                for (int lod = 0; lod < 4; lod++)
                {
                    int sides = SculptMesher.SidesForLod(lod);
                    var (s, t) = SculptMesher.OfficialGrid(w, h, sides);
                    Assert.True(s * t <= sides * sides, $"{w}x{h} LOD {lod}: {s}x{t}");
                    Assert.True(s >= 1 && t >= 4, $"{w}x{h} LOD {lod}: {s}x{t}");
                }
    }

    /// <summary>An 8x8 map whose pixel at (x, y) encodes x and y; LOD 3 gives the 4 x 4 grid (spec: 8 x 8 / 4 = 16).</summary>
    private static readonly Func<int, int, (byte, byte, byte)> Coded = (x, y) => ((byte)(x * 30 + 10), (byte)(y * 30 + 10), 128);

    private static (int X, int Y) Pixel(MeshFace f, int v, bool mirrored = false)
    {
        float px = f.Positions[v * 3], py = f.Positions[v * 3 + 1];
        if (mirrored) px = -px;
        return ((int)Math.Round(((px + 0.5) * 255 - 10) / 30), (int)Math.Round(((py + 0.5) * 255 - 10) / 30));
    }

    [Theory]
    [InlineData(SculptMesher.Plane)]
    [InlineData(SculptMesher.Sphere)]
    [InlineData(SculptMesher.Torus)]
    [InlineData(SculptMesher.Cylinder)]
    public void Samples_fall_where_the_spec_puts_them(int type)
    {
        var f = SculptMesher.BuildOfficial(Gen(8, 8, Coded), 8, 8, type, false, false, 32);
        Assert.Equal(25, f.Vertices);   // 5 x 5
        for (int i = 0; i <= 4; i++)
            for (int j = 0; j <= 4; j++)
            {
                // x = floor(j / 4 * 8) = 2j, y = 2i; then the edges by type
                int x = 2 * j, y = 2 * i;
                if (y == 0 && type == SculptMesher.Sphere) x = 4;
                if (y == 8) { y = type == SculptMesher.Torus ? 0 : 7; if (type == SculptMesher.Sphere) x = 4; }
                if (x == 8) x = type == SculptMesher.Plane ? 7 : 0;
                Assert.Equal((x, 7 - y), Pixel(f, i * 5 + j));   // spec row y is image row 7 - y (row 0 is the bottom)
            }
    }

    [Fact]
    public void Invert_reverses_the_columns_mirror_also_negates_x()
    {
        var map = Gen(8, 8, Coded);
        var plain = SculptMesher.BuildOfficial(map, 8, 8, SculptMesher.Cylinder, false, false, 32);
        var inverted = SculptMesher.BuildOfficial(map, 8, 8, SculptMesher.Cylinder, false, true, 32);
        var mirrored = SculptMesher.BuildOfficial(map, 8, 8, SculptMesher.Cylinder, true, false, 32);
        var both = SculptMesher.BuildOfficial(map, 8, 8, SculptMesher.Cylinder, true, true, 32);
        for (int i = 0; i <= 4; i++)
            for (int j = 0; j <= 4; j++)
            {
                int v = i * 5 + j, reversed = i * 5 + (4 - j);
                // reversed column j' = 4 - j reads x = 2j' (8 -> wraps to 0)
                int xr = 2 * (4 - j) == 8 ? 0 : 2 * (4 - j);
                Assert.Equal(xr, Pixel(inverted, v).X);
                Assert.Equal(xr, Pixel(mirrored, v, mirrored: true).X);
                Assert.Equal(Pixel(plain, v), Pixel(both, v, mirrored: true));
                Assert.Equal(-plain.Positions[v * 3], both.Positions[v * 3]);
                _ = reversed;
            }
        Assert.Equal(plain.Indices, inverted.Indices);   // one triangulation; the column order does the flipping
    }

    [Fact]
    public void A_sphere_faces_out_inverted_faces_in_and_mirror_keeps_it_facing_out()
    {
        var (w, h, rgba) = Map("sphere-64");
        double V(bool m, bool i) => SignedVolume(SculptMesher.BuildOfficial(rgba, w, h, SculptMesher.Sphere, m, i, 32));
        Assert.True(V(false, false) > 0.3, "plain");   // a radius-0.5 sphere: about 0.52
        Assert.True(V(true, false) > 0.3, "mirror");
        Assert.True(V(false, true) < -0.3, "invert");
        Assert.True(V(true, true) < -0.3, "mirror + invert");
        // through the flags on the type byte
        var viaFlags = SculptMesher.Mesh(rgba, w, h, SculptMesher.Sphere | SculptMesher.FlagMirror, 3);
        Assert.Equal(SculptMesher.BuildOfficial(rgba, w, h, SculptMesher.Sphere, true, false, 32).Positions, viaFlags.Face!.Positions);
    }

    [Fact]
    public void Each_type_stitches_as_the_spec_describes()
    {
        var (w, h, rgba) = Map("noise-32x16");
        int sides = 16;
        var (s, t) = SculptMesher.OfficialGrid(w, h, sides);
        int across = t + 1;
        var sphere = SculptMesher.BuildOfficial(rgba, w, h, SculptMesher.Sphere, false, false, sides);
        for (int j = 0; j < across; j++)
        {
            Assert.Equal(P(sphere, 0), P(sphere, j));                              // top pole
            Assert.Equal(P(sphere, s * across), P(sphere, s * across + j));        // bottom pole
        }
        var torus = SculptMesher.BuildOfficial(rgba, w, h, SculptMesher.Torus, false, false, sides);
        for (int j = 0; j < across; j++) Assert.Equal(P(torus, j), P(torus, s * across + j));   // wraps top to bottom
        foreach (int type in new[] { SculptMesher.Sphere, SculptMesher.Torus, SculptMesher.Cylinder })
        {
            var f = SculptMesher.BuildOfficial(rgba, w, h, type, false, false, sides);
            for (int i = 0; i <= s; i++) Assert.Equal(P(f, i * across), P(f, i * across + t));   // wraps round
        }
        var cyl = SculptMesher.BuildOfficial(rgba, w, h, SculptMesher.Cylinder, false, false, sides);
        Assert.NotEqual(P(cyl, 0), P(cyl, s * across));
        var plane = SculptMesher.BuildOfficial(rgba, w, h, SculptMesher.Plane, false, false, sides);
        Assert.NotEqual(P(plane, 0), P(plane, t));
        Assert.Equal(cyl.Vertices, plane.Vertices);
    }

    // ---- LOD ----

    [Theory]
    [InlineData(1.0f, 6)]
    [InlineData(1.0001f, 8)]
    [InlineData(1.5f, 8)]
    [InlineData(2.0f, 8)]
    [InlineData(2.0001f, 16)]
    [InlineData(2.5f, 16)]
    [InlineData(3.0f, 16)]
    [InlineData(3.0001f, 32)]
    [InlineData(4.0f, 32)]
    public void Side_budget_by_detail(float detail, int sides) => Assert.Equal(sides, SculptMesher.Sides(detail));

    [Theory]
    [InlineData(0, 6)]
    [InlineData(1, 8)]
    [InlineData(2, 16)]
    [InlineData(3, 32)]
    public void Each_lod_uses_the_official_detail_scale(int lod, int sides) => Assert.Equal(sides, SculptMesher.SidesForLod(lod));

    [Fact]
    public void Vertex_count_grows_with_lod()
    {
        var (w, h, rgba) = Map("sphere-256");
        int last = 0;
        for (int lod = 0; lod < 4; lod++)
        {
            var r = SculptMesher.Mesh(rgba, w, h, SculptMesher.Sphere, lod);
            Assert.Equal(SculptOutcome.Map, r.Outcome);
            int s = SculptMesher.SidesForLod(lod);
            Assert.Equal((s + 1) * (s + 1), r.Face!.Vertices);
            Assert.True(r.Face.Vertices > last);
            last = r.Face.Vertices;
        }
    }

    [Fact]
    public void The_lod_radius_bias_matches_the_official_volume_rule()
    {
        Assert.Equal(new Vector3(0.6f, 0.6f, 0.6f), SculptMesher.LodBias(PrimShape.Box with { PathCurve = 32, ProfileCurve = 0, SculptType = 1 }));
        Assert.Equal(new Vector3(0.6f, 0.6f, 0f), SculptMesher.LodBias(PrimShape.Box with { PathCurve = 16, ProfileCurve = 0, SculptType = 1 }));
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), SculptMesher.LodBias(PrimShape.Box with { SculptType = 1 }));
    }

    // ---- hostile maps and fallbacks ----

    public static IEnumerable<object[]> Hostile() =>
    [
        ["1x1", 1, 1], ["2x1", 2, 1], ["1x4096", 1, 4096], ["4096x1", 4096, 1], ["3x3", 3, 3], ["256x4", 256, 4], ["huge 2048", 2048, 2048], ["odd 33x17", 33, 17],
    ];

    [Theory]
    [MemberData(nameof(Hostile))]
    public void A_hostile_map_never_crashes_and_stays_within_the_official_grid(string name, int w, int h)
    {
        var rgba = Gen(w, h, (x, y) => ((byte)(x * 7 + y * 3), (byte)(x * 5 + y * 11), (byte)(x ^ y)));
        for (int type = 1; type <= 4; type++)
            for (int lod = 0; lod < 4; lod++)
                foreach (int flags in new[] { 0, SculptMesher.FlagMirror, SculptMesher.FlagInvert })
                {
                    var r = SculptMesher.Mesh(rgba, w, h, type | flags, lod);
                    if (r.Face is not { } f) continue;
                    Assert.All(f.Positions.Concat(f.Normals).Concat(f.Uvs), v => Assert.True(float.IsFinite(v), name));
                    Assert.All(f.Indices, i => Assert.InRange(i, 0, f.Vertices - 1));
                    int sides = SculptMesher.SidesForLod(lod);
                    if (r.Outcome == SculptOutcome.Map)
                    {
                        var (s, t) = SculptMesher.OfficialGrid(w, h, sides);
                        Assert.Equal((s + 1) * (t + 1), f.Vertices);
                        Assert.True(s * t <= sides * sides, name);
                    }
                }
    }

    [Fact]
    public void A_single_pixel_map_draws_nothing_at_the_lowest_lod_and_the_sphere_above()
    {
        var one = Solid(1, 1, 200, 100, 50);
        Assert.Equal(SculptOutcome.Empty, SculptMesher.Mesh(one, 1, 1, SculptMesher.Sphere, 0).Outcome);   // zero area, not checked
        Assert.Equal(SculptOutcome.SpherePlaceholder, SculptMesher.Mesh(one, 1, 1, SculptMesher.Sphere, 2).Outcome);
    }

    [Fact]
    public void A_flat_map_is_the_sphere_above_the_lowest_lod()
    {
        var flat = Solid(64, 64, 128, 128, 128);
        Assert.Equal(SculptOutcome.Empty, SculptMesher.Mesh(flat, 64, 64, SculptMesher.Plane, 0).Outcome);
        var r = SculptMesher.Mesh(flat, 64, 64, SculptMesher.Plane, 1);
        Assert.Equal(SculptOutcome.SpherePlaceholder, r.Outcome);
        Assert.Contains("area", r.Reason);
    }

    [Fact]
    public void A_missing_map_is_the_visible_sphere_placeholder_and_no_data_is_empty()
    {
        var missing = SculptMesher.Mesh([], 0, 0, SculptMesher.Torus, 3, missing: true);
        Assert.Equal(SculptOutcome.SpherePlaceholder, missing.Outcome);
        Assert.Equal(33 * 33, missing.Face!.Vertices);
        Assert.Equal(SculptOutcome.Empty, SculptMesher.Mesh([], 0, 0, SculptMesher.Torus, 3).Outcome);
        Assert.Equal(SculptOutcome.Empty, SculptMesher.Mesh(new byte[10], 64, 64, SculptMesher.Torus, 3).Outcome);   // short buffer
    }

    [Fact]
    public void The_sphere_placeholder_is_radius_0_3_and_faces_outward()
    {
        var f = SculptMesher.SpherePlaceholder(16);
        for (int i = 0; i < f.Vertices; i++)
            Assert.InRange(P(f, i).Length(), 0.2999f, 0.3001f);
        Assert.True(SignedVolume(f) > 0.1, "outward");   // about 4/3 pi 0.3^3 = 0.113
    }

    [Fact]
    public void A_map_the_decode_guard_refuses_is_the_empty_placeholder_and_a_missing_one_the_sphere()
    {
        var refused = Viewer.Core.Protocol.LmvTexture.Decode([0xFF, 0x4F, 0xFF, 0x51, 1, 2, 3]);   // a truncated codestream
        Assert.False(refused.Ok);
        Assert.Equal(SculptOutcome.Empty, SculptMesher.FromMap(refused.Texture, missing: false, SculptMesher.Sphere, 2).Outcome);
        Assert.Equal(SculptOutcome.SpherePlaceholder, SculptMesher.FromMap(null, missing: true, SculptMesher.Sphere, 2).Outcome);
        var (w, h, rgba) = Map("torus-64");
        var ok = new Viewer.Core.Imaging.DecodedTexture(w, h, rgba, Viewer.Core.Imaging.AlphaClass.Opaque, 3);
        Assert.Equal(SculptOutcome.Map, SculptMesher.FromMap(ok, false, SculptMesher.Torus, 2).Outcome);
    }

    [Fact]
    public void The_adapter_keeps_the_mirror_and_invert_flags()
    {
        var data = new byte[17];
        Guid.NewGuid().ToByteArray().CopyTo(data, 0);
        data[16] = (byte)(SculptMesher.Sphere | SculptMesher.FlagMirror | SculptMesher.FlagInvert);
        var shape = Viewer.Core.Protocol.LmvTransport.ShapeFromSculptBlock(data);
        Assert.Equal(SculptMesher.Sphere, shape.SculptType);
        Assert.Equal(SculptMesher.FlagMirror | SculptMesher.FlagInvert, shape.SculptFlags);
        Assert.True(shape.IsSculpt);
    }
}
