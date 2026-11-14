// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using Viewer.Core.Avatars;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A test on the real character folder and a reference file (local input, never in the repo): skipped with its reason unless NV_CHARACTER_DIR names the
/// character folder and NV_SHAPE_REFERENCE names a JSON file written by an independent script that works the shape out from the official viewer's rules.</summary>
public sealed class ShapeReferenceFactAttribute : FactAttribute
{
    public const string Variable = "NV_SHAPE_REFERENCE";

    public ShapeReferenceFactAttribute()
    {
        if (CharacterFactAttribute.Folder is null) Skip = $"local-only character data: {CharacterFactAttribute.Variable} is not set";
        else if (File == null) Skip = $"local-only reference: {Variable} is not set";
    }

    public static string? File => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } v && System.IO.File.Exists(v) ? v : null;
}

/// <summary>
/// The default body's morphs and the shaped skeleton against a reference worked out separately from the official viewer's rules (quantised weights, drivers, the
/// sex rule, the morph targets with their normals softened, skeletal scale and offset deltas, joint positions with the parent's scale applied to the child's
/// offset). For every vector in the reference file the head, upper body, lower body and eyeball vertex positions and the eye, head, neck and skull joints must agree to 1e-4 m.
/// </summary>
public class OfficialShapeReferenceTests
{
    private const float Tolerance = 1e-4f;

    private static Vector3 V(JsonElement a, int i) => new(a[i].GetSingle(), a[i + 1].GetSingle(), a[i + 2].GetSingle());

    [ShapeReferenceFact]
    public void Morphed_vertices_and_joints_match_the_reference_for_every_vector()
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(ShapeReferenceFactAttribute.File!));
        var body = AvatarBody.Load(CharacterFactAttribute.Folder!);
        var def = body.Shape!;
        var rig = new AvatarRig(body.Skeleton);
        var order = doc.RootElement.GetProperty("meta").GetProperty("transmitOrder").EnumerateArray().Select(e => e.GetInt32()).ToImmutableArray();
        var worst = new Dictionary<string, float>();
        var bad = new List<string>();
        foreach (var vec in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            int index = vec.GetProperty("index").GetInt32();
            var bytes = new byte[order.Length];
            var given = vec.GetProperty("bytes").EnumerateArray().ToDictionary(p => p[0].GetInt32(), p => (byte)p[1].GetInt32());
            for (int i = 0; i < order.Length; i++) bytes[i] = given[order[i]];
            var shape = AvatarShape.From(rig, def, new VisualParamSet(bytes, order));
            Assert.Equal(vec.GetProperty("male").GetBoolean(), shape.Male);
            var morphed = BodyMorph.Compute(body, def, shape);
            foreach (var part in vec.GetProperty("parts").EnumerateObject())
            {
                int pi = body.Parts.ToList().FindIndex(p => p.Type == part.Name);
                Assert.True(pi >= 0, $"the body has no part {part.Name}");
                var ours = morphed?[pi].Positions ?? body.Parts[pi].Positions;
                var rigid = body.Parts[pi].Rigid;   // an eyeball is moved from its joint's frame to the root frame after morphing; the reference stays in the joint frame
                Matrix4x4 back = Matrix4x4.Identity;
                if (rigid is { } m) Assert.True(Matrix4x4.Invert(m, out back));
                var want = part.Value.GetProperty("positions");
                Assert.Equal(want.GetArrayLength(), ours.Length * 3);
                float max = 0;
                string worstAt = "";
                for (int v = 0; v < ours.Length; v++)
                {
                    var o = rigid is null ? ours[v] : Vector3.Transform(ours[v], back);
                    float d = (o - V(want, v * 3)).Length();
                    if (d > max) { max = d; worstAt = $" (vertex {v}: ours {o}, reference {V(want, v * 3)})"; }
                }
                worst[part.Name] = MathF.Max(worst.GetValueOrDefault(part.Name), max);
                if (max > Tolerance) bad.Add($"vector {index} {part.Name}: vertex positions differ by up to {max:G4} m{worstAt}");
            }
            var joints = new AvatarJoints(rig) { Shape = shape };
            joints.Begin(null, 0);
            foreach (var j in vec.GetProperty("joints").EnumerateObject())
            {
                int node = rig.Find(j.Name);
                Assert.True(node >= 0, j.Name);
                float dLocal = j.Name == "mPelvis" ? 0 : (shape.Position[node] - V(j.Value.GetProperty("localPosition"), 0)).Length();
                float dScale = (shape.Scale[node] - V(j.Value.GetProperty("localScale"), 0)).Length();
                float dWorld = (joints.World(node).Translation - V(j.Value.GetProperty("worldPosition"), 0)).Length();
                worst["joint " + j.Name] = MathF.Max(worst.GetValueOrDefault("joint " + j.Name), MathF.Max(dLocal, MathF.Max(dScale, dWorld)));
                if (dLocal > Tolerance || dScale > Tolerance || dWorld > Tolerance)
                    bad.Add($"vector {index} {j.Name}: local position {dLocal:G4}, scale {dScale:G4}, world position {dWorld:G4} off");
            }
        }
        Assert.True(bad.Count == 0, $"{bad.Count} differences (worst per item: {string.Join(", ", worst.Select(w => $"{w.Key} {w.Value:G3}"))}); first: {string.Join(" | ", bad.Take(8))}");
    }
}
