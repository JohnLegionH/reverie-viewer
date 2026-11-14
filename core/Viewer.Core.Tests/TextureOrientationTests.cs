// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Meshing;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// Texture orientation on every prim face, against the behaviour spec in
/// Docs/TEXTURE-ORIENTATION-SPEC.md (default texgen, no repeats, offset or rotation). For each triangle the gradients of
/// u and v across its plane (object axes) are compared with the spec's directions.
/// </summary>
public class TextureOrientationTests(ITestOutputHelper output)
{
    private static PrimShape Box => PrimShape.Box;
    private static PrimShape Cyl => new() { ProfileCurve = 0, PathCurve = 16 };
    private static PrimShape Prism => new() { ProfileCurve = 3, PathCurve = 16 };
    private static PrimShape Sphere => new() { ProfileCurve = 5, PathCurve = 32 };
    private static PrimShape Torus => new() { ProfileCurve = 0, PathCurve = 32, PathScaleY = 0.25 };
    private static PrimShape Tube => new() { ProfileCurve = 1, PathCurve = 32, PathScaleY = 0.25 };
    private static PrimShape Ring => new() { ProfileCurve = 3, PathCurve = 32, PathScaleY = 0.25 };

    private readonly record struct Tri(Vector3 Normal, Vector3 GradU, Vector3 GradV, Vector3 Centre);

    private static IEnumerable<(int Face, Tri T)> Triangles(PrimShape shape)
    {
        foreach (var f in PrimGeometry.Build(shape))
            for (int t = 0; t < f.Indices.Length; t += 3)
            {
                Vector3 P(int k) => new(f.Positions[f.Indices[t + k] * 3], f.Positions[f.Indices[t + k] * 3 + 1], f.Positions[f.Indices[t + k] * 3 + 2]);
                Vector2 U(int k) => new(f.Uvs[f.Indices[t + k] * 2], f.Uvs[f.Indices[t + k] * 2 + 1]);
                Vector3 e1 = P(1) - P(0), e2 = P(2) - P(0);
                var n = Vector3.Cross(e1, e2);
                if (n.Length() < 1e-7f) continue;
                float a = Vector3.Dot(e1, e1), b = Vector3.Dot(e1, e2), c = Vector3.Dot(e2, e2), det = a * c - b * b;
                if (MathF.Abs(det) < 1e-12f) continue;
                Vector3 Grad(float d1, float d2) { float x = (c * d1 - b * d2) / det, y = (a * d2 - b * d1) / det; return x * e1 + y * e2; }
                var du = U(1) - U(0); var dv = U(2) - U(0);
                var gu = Grad(du.X, dv.X); var gv = Grad(du.Y, dv.Y);
                if (gu.Length() < 1e-5f || gv.Length() < 1e-5f) continue;   // a collapsed UV (e.g. a pole)
                yield return (f.Face, new Tri(Vector3.Normalize(n), gu, gv, (P(0) + P(1) + P(2)) / 3));
            }
    }

    public static IEnumerable<object[]> LinePathShapes() => [["box"], ["cylinder"], ["prism"]];
    public static IEnumerable<object[]> AllShapes() => [["box"], ["cylinder"], ["prism"], ["sphere"], ["torus"], ["tube"], ["ring"]];
    private static PrimShape ByName(string n) => n switch
    {
        "box" => Box, "cylinder" => Cyl, "prism" => Prism, "sphere" => Sphere, "torus" => Torus, "tube" => Tube, _ => Ring,
    };

    /// <summary>Spec 2-3: on a line path, caps and sides point exactly as the spec says.</summary>
    [Theory]
    [MemberData(nameof(LinePathShapes))]
    public void Line_path_faces_point_as_the_spec_says(string name)
    {
        var bad = new List<string>();
        foreach (var (face, t) in Triangles(ByName(name)))
        {
            string kind;
            bool ok;
            if (t.Normal.Z > 0.99f) { kind = "top cap"; ok = Vector3.Dot(t.GradU, Vector3.UnitX) > 0 && Vector3.Dot(t.GradV, Vector3.UnitY) > 0; }
            else if (t.Normal.Z < -0.99f) { kind = "bottom cap"; ok = Vector3.Dot(t.GradU, Vector3.UnitX) > 0 && Vector3.Dot(t.GradV, Vector3.UnitY) < 0; }
            else
            {
                kind = "side";
                var along = Vector3.Cross(Vector3.UnitZ, t.Normal);   // anticlockwise seen from above
                ok = Vector3.Dot(t.GradV, Vector3.UnitZ) > 0 && Vector3.Dot(t.GradU, along) > 0;
            }
            if (!ok) bad.Add($"face {face} ({kind}): u towards {Dir(t.GradU)}, v towards {Dir(t.GradV)}");
        }
        foreach (var b in bad.Distinct()) output.WriteLine(b);
        Assert.True(bad.Count == 0, $"{name}: {bad.Distinct().Count()} face orientations differ from the spec: {string.Join("; ", bad.Distinct().Take(6))}");
    }

    /// <summary>Spec 1: no face is mirrored: (grad u x grad v) points out of the face, on every shape.</summary>
    [Theory]
    [MemberData(nameof(AllShapes))]
    public void No_face_is_mirrored(string name)
    {
        var bad = Triangles(ByName(name)).Where(x => Vector3.Dot(Vector3.Cross(x.T.GradU, x.T.GradV), x.T.Normal) <= 0).Select(x => x.Face).Distinct().ToList();
        Assert.True(bad.Count == 0, $"{name}: mirrored faces {string.Join(",", bad)}");
    }

    private static string Dir(Vector3 g)
    {
        g = Vector3.Normalize(g);
        var axes = new[] { ("+X", Vector3.UnitX), ("-X", -Vector3.UnitX), ("+Y", Vector3.UnitY), ("-Y", -Vector3.UnitY), ("+Z", Vector3.UnitZ), ("-Z", -Vector3.UnitZ) };
        return axes.MaxBy(a => Vector3.Dot(a.Item2, g)).Item1;
    }
}
