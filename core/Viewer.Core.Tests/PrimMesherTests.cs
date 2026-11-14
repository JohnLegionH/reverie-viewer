// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Viewer.Core.Meshing;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// L1 cross-language parity (the test strategy 3.2): one fixture set, two implementations (the web viewer's TS PrimMesher
/// and this C# port). See fixtures/primmesher/PROVENANCE.md.
///   - Topology EXACT: triangle count, every triangle's prim face number and coordinate indices (the index buffer), the
///     grouped face list (face numbering); a SHA-256 over the canonical topology serialisation must match.
///   - Geometry within <see cref="Tolerance"/> (positions, normals, UVs), compared triangle by triangle in the canonical
///     (generation) order.
/// </summary>
public class PrimMesherParityTests
{
    /// <summary>Declared geometry tolerance (absolute, unit-cube coordinates): the dumps are rounded to 1e-6.</summary>
    public const double Tolerance = 1e-5;

    private static string Dir => Path.Combine(TestPaths.RepoRoot, "fixtures", "primmesher");

    public static IEnumerable<object[]> Fixtures() =>
        Directory.EnumerateFiles(Path.Combine(Dir, "web-viewer"), "*.json").Select(f => new object[] { Path.GetFileNameWithoutExtension(f) }).OrderBy(o => (string)o[0]);

    internal static PrimShape Shape(JsonElement s) => new()
    {
        ProfileCurve = s.GetProperty("profileCurve").GetInt32(), ProfileHole = s.GetProperty("profileHole").GetInt32(),
        ProfileBegin = s.GetProperty("profileBegin").GetDouble(), ProfileEnd = s.GetProperty("profileEnd").GetDouble(),
        ProfileHollow = s.GetProperty("profileHollow").GetDouble(), PathCurve = s.GetProperty("pathCurve").GetInt32(),
        PathBegin = s.GetProperty("pathBegin").GetDouble(), PathEnd = s.GetProperty("pathEnd").GetDouble(),
        PathScaleX = s.GetProperty("pathScaleX").GetDouble(), PathScaleY = s.GetProperty("pathScaleY").GetDouble(),
        PathShearX = s.GetProperty("pathShearX").GetDouble(), PathShearY = s.GetProperty("pathShearY").GetDouble(),
        PathTwist = s.GetProperty("pathTwist").GetDouble(), PathTwistBegin = s.GetProperty("pathTwistBegin").GetDouble(),
        PathRadiusOffset = s.GetProperty("pathRadiusOffset").GetDouble(), PathTaperX = s.GetProperty("pathTaperX").GetDouble(),
        PathTaperY = s.GetProperty("pathTaperY").GetDouble(), PathRevolutions = s.GetProperty("pathRevolutions").GetDouble(),
        PathSkew = s.GetProperty("pathSkew").GetDouble(),
    };

    internal static (string Source, PrimShape Shape, JsonElement Expected) Summary(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "web-viewer", name + ".json")));
        var r = doc.RootElement.Clone();
        return (r.GetProperty("source").GetString()!, Shape(r.GetProperty("shape")), r.GetProperty("expected"));
    }

    /// <summary>The web viewer test's summarize: counts, per-face counts, bbox, every stride-th triangle.</summary>
    internal static (int Triangles, Dictionary<string, int> PerFace, double[] Min, double[] Max, List<(int Index, int Face, double[] V, double[] Uv1)> Samples)
        Summarize(PrimMesh mesh, bool dropBottom)
    {
        var vfs = dropBottom && mesh.BottomFaceNumber >= 0 ? mesh.ViewerFaces.Where(v => v.PrimFaceNumber != mesh.BottomFaceNumber).ToList() : mesh.ViewerFaces;
        var perFace = vfs.GroupBy(v => v.PrimFaceNumber.ToString()).ToDictionary(g => g.Key, g => g.Count());
        int stride = Math.Max(1, vfs.Count / 8);
        var samples = new List<(int, int, double[], double[])>();
        for (int i = 0; i < vfs.Count; i += stride)
        {
            var v = vfs[i];
            samples.Add((i, v.PrimFaceNumber, [v.V1.X, v.V1.Y, v.V1.Z, v.V2.X, v.V2.Y, v.V2.Z, v.V3.X, v.V3.Y, v.V3.Z], [v.Uv1.U, v.Uv1.V]));
        }
        var all = vfs.SelectMany(v => new[] { v.V1, v.V2, v.V3 }).ToList();
        return (vfs.Count, perFace, [all.Min(c => c.X), all.Min(c => c.Y), all.Min(c => c.Z)], [all.Max(c => c.X), all.Max(c => c.Y), all.Max(c => c.Z)], samples);
    }

    /// <summary>True when the mesh reproduces the web viewer fixture (the web viewer test's own checks: counts and per-face
    /// counts exact, bbox and samples to toBeCloseTo(3), i.e. within 5e-4).</summary>
    internal static bool MatchesSummary(PrimMesh mesh, string source, JsonElement e, out string why)
    {
        var got = Summarize(mesh, source.StartsWith("csharp", StringComparison.Ordinal));
        why = "";
        if (got.Triangles != e.GetProperty("triangles").GetInt32()) { why = $"triangles {got.Triangles} != {e.GetProperty("triangles").GetInt32()}"; return false; }
        var pf = e.GetProperty("perFace").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        if (!pf.OrderBy(k => k.Key).SequenceEqual(got.PerFace.OrderBy(k => k.Key))) { why = "perFace differs: " + string.Join(",", got.PerFace.OrderBy(k => k.Key)); return false; }
        const double d = 5e-4;
        var bmin = e.GetProperty("bbox").GetProperty("min").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var bmax = e.GetProperty("bbox").GetProperty("max").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        for (int i = 0; i < 3; i++)
            if (Math.Abs(got.Min[i] - bmin[i]) > d || Math.Abs(got.Max[i] - bmax[i]) > d) { why = "bbox differs"; return false; }
        foreach (var s in e.GetProperty("samples").EnumerateArray())
        {
            int index = s.GetProperty("index").GetInt32();
            var g = got.Samples.FirstOrDefault(x => x.Index == index);
            if (g.V is null) { why = $"no sample {index}"; return false; }
            if (g.Face != s.GetProperty("face").GetInt32()) { why = $"sample {index} face {g.Face}"; return false; }
            var exp = new[] { "v1", "v2", "v3" }.SelectMany(k => s.GetProperty(k).EnumerateArray().Select(x => x.GetDouble())).ToArray();
            var uv = s.GetProperty("uv1").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            for (int i = 0; i < 9; i++) if (Math.Abs(g.V[i] - exp[i]) > d) { why = $"sample {index} vertex differs"; return false; }
            for (int i = 0; i < 2; i++) if (Math.Abs(g.Uv1[i] - uv[i]) > d) { why = $"sample {index} uv differs"; return false; }
        }
        return true;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_web_viewer_golden_fixture_is_reproduced(string name)
    {
        var (source, shape, expected) = Summary(name);
        Assert.True(MatchesSummary(PrimGeometry.Generate(shape), source, expected, out var why), name + ": " + why);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Topology_is_exact_and_geometry_within_tolerance_of_the_ts_port(string name)
    {
        var (_, shape, _) = Summary(name);
        using var gz = new GZipStream(File.OpenRead(Path.Combine(Dir, "ts-full", name + ".full.json.gz")), CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gz);
        var ts = doc.RootElement.GetProperty("triangles").EnumerateArray().Select(t => t.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToList();
        var mesh = PrimGeometry.Generate(shape);
        var cs = mesh.ViewerFaces.Select(Row).ToList();

        Assert.Equal(ts.Count, cs.Count);
        Assert.Equal(TopologyHash(ts), TopologyHash(cs));   // face number + index buffer, canonical order
        Assert.Equal(doc.RootElement.GetProperty("bottomFaceNumber").GetInt32(), mesh.BottomFaceNumber);
        Assert.Equal(doc.RootElement.GetProperty("numPrimFaces").GetInt32(), mesh.NumPrimFaces);
        var groups = PrimGeometry.GroupFaces(mesh.ViewerFaces).Select(g => (g.Face, g.Triangles)).ToList();
        var tsGroups = doc.RootElement.GetProperty("groups").EnumerateArray().Select(g => (g.GetProperty("face").GetInt32(), g.GetProperty("triangles").GetInt32())).ToList();
        Assert.Equal(tsGroups, groups);
        double worst = 0;
        for (int t = 0; t < ts.Count; t++)
            for (int i = 4; i < ts[t].Length; i++)
                worst = Math.Max(worst, Math.Abs(ts[t][i] - cs[t][i]));
        File.AppendAllText(Path.Combine(TestPaths.TestOutputDir, "primmesher-parity.txt"), $"{name}\ttriangles={cs.Count}\tgroups={groups.Count}\tmax_abs_diff={worst:E2}\ttopology={TopologyHash(cs)[..12]}\n");
        Assert.True(worst <= Tolerance, $"{name}: max geometry difference {worst:E3} > {Tolerance:E0}");
    }

    internal static double[] Row(ViewerFace v) =>
    [
        v.PrimFaceNumber, v.CoordIndex1, v.CoordIndex2, v.CoordIndex3,
        v.V1.X, v.V1.Y, v.V1.Z, v.V2.X, v.V2.Y, v.V2.Z, v.V3.X, v.V3.Y, v.V3.Z,
        v.N1.X, v.N1.Y, v.N1.Z, v.N2.X, v.N2.Y, v.N2.Z, v.N3.X, v.N3.Y, v.N3.Z,
        v.Uv1.U, v.Uv1.V, v.Uv2.U, v.Uv2.V, v.Uv3.U, v.Uv3.V,
    ];

    /// <summary>Canonical topology serialisation: one "face,i1,i2,i3" line per triangle in generation order.</summary>
    internal static string TopologyHash(IEnumerable<double[]> rows) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Concat(rows.Select(r => $"{(int)r[0]},{(int)r[1]},{(int)r[2]},{(int)r[3]}\n")))));
}

/// <summary>
/// The three LibreMetaverse 3.1.4 PrimMesher regressions (ADR-007 patch layer), each with its own test and a
/// NEGATIVE CONTROL: the old behaviour, switched back on, fails the same check.
/// </summary>
public class PrimMesherRegressionTests
{
    // (1) hollow/cut flags never set: a cut, hollow box loses its cut faces and its hollow numbering.
    private static bool CutAndHollowFacesPresent(PrimMesherRegression r)
    {
        var (source, shape, expected) = PrimMesherParityTests.Summary("box-cut-hollow");
        var mesh = PrimGeometry.Generate(shape, PrimDetail.High, r);
        return mesh.HasProfileCut && mesh.HasHollow && mesh.ProfileHollowFaceNumber > 0 && PrimMesherParityTests.MatchesSummary(mesh, source, expected, out _);
    }

    [Fact]
    public void Fix1_cut_and_hollow_flags_are_computed() => Assert.True(CutAndHollowFacesPresent(PrimMesherRegression.None));

    [Fact]
    public void Fix1_negative_control_flags_never_set_fails() => Assert.False(CutAndHollowFacesPresent(PrimMesherRegression.ProfileFlagsNeverSet));

    // (2) twist degrees used as radians: a box with pathTwist 0.5 (90 degrees on a linear path) must end 90 degrees turned.
    private static bool TopIsTurned90Degrees(PrimMesherRegression r)
    {
        var shape = PrimShape.Box with { PathTwist = 0.5 };
        var mesh = PrimGeometry.Generate(shape, PrimDetail.High, r);
        var top = mesh.ViewerFaces.Where(v => v.PrimFaceNumber == 0).SelectMany(v => new[] { v.V1, v.V2, v.V3 }).ToList();
        var bottom = mesh.ViewerFaces.Where(v => v.PrimFaceNumber == mesh.BottomFaceNumber).SelectMany(v => new[] { v.V1, v.V2, v.V3 }).ToList();
        // Every top corner is a bottom corner rotated +90 degrees about z (x, y) -> (-y, x), up 1 m.
        bool turned = top.All(t => bottom.Any(b => Math.Abs(-b.Y - t.X) < 1e-4 && Math.Abs(b.X - t.Y) < 1e-4)) && top.All(t => Math.Abs(t.Z - 0.5) < 1e-4);
        var (source, fixShape, expected) = PrimMesherParityTests.Summary("box-twisted");
        return turned && shape == fixShape && PrimMesherParityTests.MatchesSummary(mesh, source, expected, out _);
    }

    [Fact]
    public void Fix2_twist_is_degrees_converted_to_radians() => Assert.True(TopIsTurned90Degrees(PrimMesherRegression.None));

    [Fact]
    public void Fix2_negative_control_degrees_as_radians_fails() => Assert.False(TopIsTurned90Degrees(PrimMesherRegression.TwistDegreesAsRadians));

    // (3) no bottom cap: a plain box must have six faces, the sixth (5) facing -z at z = -0.5.
    private static bool HasOutwardBottomCap(PrimMesherRegression r)
    {
        var mesh = PrimGeometry.Generate(PrimShape.Box, PrimDetail.High, r);
        var groups = PrimGeometry.GroupFaces(mesh.ViewerFaces);
        if (groups.Count != 6) return false;
        var bottom = mesh.ViewerFaces.Where(v => v.PrimFaceNumber == mesh.BottomFaceNumber).ToList();
        return mesh.BottomFaceNumber >= 0 && bottom.Count == 2 && bottom.All(v => Math.Abs(v.V1.Z + 0.5) < 1e-6 && v.N1.Z < -0.99)
            && groups[5].Positions.Where((_, i) => i % 3 == 2).All(z => Math.Abs(z + 0.5f) < 1e-6);
    }

    [Fact]
    public void Fix3_the_bottom_cap_is_emitted() => Assert.True(HasOutwardBottomCap(PrimMesherRegression.None));

    [Fact]
    public void Fix3_negative_control_no_bottom_cap_fails() => Assert.False(HasOutwardBottomCap(PrimMesherRegression.NoBottomCap));

    [Fact]
    public void Box_faces_follow_the_viewer_convention()
    {
        var g = PrimGeometry.Build(PrimShape.Box);
        Assert.Equal([0, 1, 2, 3, 4, 5], g.Select(f => f.Face));
        Assert.All(g, f => Assert.Equal(2, f.Triangles));
        Assert.All(g[0].Positions.Where((_, i) => i % 3 == 2), z => Assert.Equal(0.5f, z, 5));   // 0 = top
        Assert.Throws<NotSupportedException>(() => PrimGeometry.Build(PrimShape.Box with { SculptType = 1 }));
    }

    [Theory]
    [InlineData(PrimDetail.Low)]
    [InlineData(PrimDetail.Medium)]
    [InlineData(PrimDetail.High)]
    public void Every_lod_meshes_every_type_without_error(PrimDetail lod)
    {
        foreach (var s in PrimZoo.Shapes.Values)
        {
            var mesh = PrimGeometry.Generate(s, lod);
            Assert.Equal("", mesh.ErrorMessage);
            Assert.NotEmpty(mesh.ViewerFaces);
            Assert.All(mesh.ViewerFaces, v => Assert.True(double.IsFinite(v.V1.X + v.V2.Y + v.V3.Z + v.N1.X)));
        }
    }
}
