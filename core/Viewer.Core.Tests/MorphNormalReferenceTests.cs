// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using Viewer.Core.Avatars;
using Viewer.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// The shaped body's per-vertex normals against a reference worked out separately from the official viewer's rules (local input, never in the repo; skipped with
/// its reason without NV_CHARACTER_DIR and NV_NORMAL_REFERENCE). The official morph adds each morph's normal offset times the weight times 0.65 to a running
/// sum that starts at the mesh's normal, and the vertex's normal is that sum normalised, for every vertex a morph touches. The angle between our normal and the
/// reference's must stay under a hundredth of a degree for every vertex of the head, upper body, lower body and eyeballs, in every vector.
/// </summary>
public class MorphNormalReferenceTests(ITestOutputHelper output)
{
    public const string Variable = "NV_NORMAL_REFERENCE";
    private const double ToleranceDegrees = 0.01;

    private static Vector3 V(JsonElement a, int i) => new(a[i].GetSingle(), a[i + 1].GetSingle(), a[i + 2].GetSingle());

    private static double Angle(Vector3 a, Vector3 b)
    {
        // in double, by atan2 of the cross and dot products: a float acos near 1 cannot resolve angles under about 0.03 degrees
        double ax = a.X, ay = a.Y, az = a.Z, bx = b.X, by = b.Y, bz = b.Z;
        double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
        return Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), ax * bx + ay * by + az * bz) * 180.0 / Math.PI;
    }

    [LocalReferenceFact(Variable)]
    public void Morphed_normals_match_the_reference_for_every_vector()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(LocalReferenceFactAttribute.Path(Variable)!));
        var body = AvatarBody.Load(CharacterFactAttribute.Folder!);
        var def = body.Shape!;
        var rig = new AvatarRig(body.Skeleton);
        var order = doc.RootElement.GetProperty("meta").GetProperty("transmitOrder").EnumerateArray().Select(e => e.GetInt32()).ToImmutableArray();
        var bad = new List<string>();
        int vectors = 0;
        foreach (var vec in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            vectors++;
            int index = vec.GetProperty("index").GetInt32();
            var given = vec.GetProperty("bytes").EnumerateArray().ToDictionary(p => p[0].GetInt32(), p => (byte)p[1].GetInt32());
            var shape = AvatarShape.From(rig, def, new VisualParamSet(order.Select(i => given[i]).ToArray(), order));
            Assert.Equal(vec.GetProperty("male").GetBoolean(), shape.Male);
            var morphed = BodyMorph.Compute(body, def, shape);
            foreach (var part in vec.GetProperty("parts").EnumerateObject())
            {
                int pi = body.Parts.ToList().FindIndex(p => p.Type == part.Name);
                Assert.True(pi >= 0, $"the body has no part {part.Name}");
                var ours = morphed?[pi].Normals ?? body.Parts[pi].Normals;
                var rigid = body.Parts[pi].Rigid;   // an eyeball's normals are turned into the root frame after morphing; the reference stays in the joint frame
                Matrix4x4 back = Matrix4x4.Identity;
                if (rigid is { } m) Assert.True(Matrix4x4.Invert(m, out back));
                var want = part.Value.GetProperty("normals");
                Assert.Equal(want.GetArrayLength(), ours.Length * 3);
                double max = 0;
                int at = -1;
                for (int v = 0; v < ours.Length; v++)
                {
                    var o = rigid is null ? ours[v] : Vector3.TransformNormal(ours[v], back);
                    double a = Angle(o, V(want, v * 3));
                    if (a > max) { max = a; at = v; }
                }
                output.WriteLine($"vector {index} {part.Name}: largest angle {max:F6} degrees (vertex {at})");
                if (max > ToleranceDegrees) bad.Add($"vector {index} {part.Name}: normals differ by up to {max:G4} degrees at vertex {at}");
            }
        }
        Assert.True(vectors >= 3, $"the reference is too small to prove anything: {vectors} vectors");
        Assert.True(bad.Count == 0, $"{bad.Count} differences; first: {string.Join(" | ", bad.Take(8))}");
    }
}
