// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using Viewer.Core.Avatars;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A test on the real character folder and a local reference file named by an environment variable (skipped, with its reason, when either is missing).</summary>
public sealed class LocalReferenceFactAttribute : FactAttribute
{
    public LocalReferenceFactAttribute(string variable)
    {
        if (CharacterFactAttribute.Folder is null) Skip = $"local-only character data: {CharacterFactAttribute.Variable} is not set";
        else if (Path(variable) is null) Skip = $"local-only reference: {variable} is not set";
    }

    public static string? Path(string variable) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v && File.Exists(v) ? v : null;
}

/// <summary>
/// The shaped body drawn the way the official viewer draws it, against a reference worked out separately from the official sources (local input, never in the repo).
/// Two references: every joint of the skeleton, one shape parameter at a time at both ends of its range (so a scale or offset applied in the wrong axis moves a
/// joint that another parameter leaves alone), and the head and the upper body after skinning (the skeleton file's pivots as the bind offsets, the mesh file's weights
/// and joint list, the joint's own scale and rotation in the matrix, a joint's offset scaled by its parent's scale).
/// </summary>
public class OfficialSkinnedShapeTests
{
    /// <summary>The official bind offsets are the skeleton file's pivots, which differ from the joints' positions by up to a millimetre; our bind uses the positions.</summary>
    private const float SkinTolerance = 1.5e-3f;
    private const float JointTolerance = 1e-4f;

    private static Vector3 V(JsonElement a, int i) => new(a[i].GetSingle(), a[i + 1].GetSingle(), a[i + 2].GetSingle());

    [LocalReferenceFact("NV_SHAPE_REFERENCE_JOINTS")]
    public void Every_joint_matches_the_reference_one_parameter_at_a_time()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(LocalReferenceFactAttribute.Path("NV_SHAPE_REFERENCE_JOINTS")!));
        var body = AvatarBody.Load(CharacterFactAttribute.Folder!);
        var rig = new AvatarRig(body.Skeleton);
        var order = doc.RootElement.GetProperty("meta").GetProperty("transmitOrder").EnumerateArray().Select(e => e.GetInt32()).ToImmutableArray();
        int checkedJoints = 0, vectors = 0;
        var bad = new List<string>();
        foreach (var vec in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var given = vec.GetProperty("bytes").EnumerateArray().ToDictionary(p => p[0].GetInt32(), p => (byte)p[1].GetInt32());
            var shape = AvatarShape.From(rig, body.Shape!, new VisualParamSet(order.Select(i => given[i]).ToArray(), order));
            var joints = new AvatarJoints(rig) { Shape = shape };
            joints.Begin(null, 0);
            vectors++;
            foreach (var j in vec.GetProperty("joints").EnumerateObject())
            {
                int node = rig.Find(j.Name);
                Assert.True(node >= 0, j.Name);
                float dLocal = j.Name == "mPelvis" ? 0 : (shape.Position[node] - V(j.Value.GetProperty("localPosition"), 0)).Length();
                float dScale = (shape.Scale[node] - V(j.Value.GetProperty("localScale"), 0)).Length();
                float dWorld = (joints.World(node).Translation - V(j.Value.GetProperty("worldPosition"), 0)).Length();
                checkedJoints++;
                if (dLocal > JointTolerance || dScale > JointTolerance || dWorld > JointTolerance)
                    bad.Add($"vector {vec.GetProperty("index").GetInt32()} {j.Name}: local position {dLocal:G4}, scale {dScale:G4}, world position {dWorld:G4} off");
            }
        }
        Assert.True(vectors >= 100 && checkedJoints >= vectors * 100, $"the reference is too small to prove anything: {vectors} vectors, {checkedJoints} joint checks");
        Assert.True(bad.Count == 0, $"{bad.Count} differences; first: {string.Join(" | ", bad.Take(8))}");
    }

    [LocalReferenceFact("NV_SKINNED_REFERENCE")]
    public void The_skinned_head_and_upper_body_match_the_reference()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(LocalReferenceFactAttribute.Path("NV_SKINNED_REFERENCE")!));
        var body = AvatarBody.Load(CharacterFactAttribute.Folder!);
        var def = body.Shape!;
        var rig = new AvatarRig(body.Skeleton);
        var order = doc.RootElement.GetProperty("meta").GetProperty("transmitOrder").EnumerateArray().Select(e => e.GetInt32()).ToImmutableArray();
        var worst = new Dictionary<string, float>();
        int vectors = 0;
        foreach (var vec in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var given = vec.GetProperty("bytes").EnumerateArray().ToDictionary(p => p[0].GetInt32(), p => (byte)p[1].GetInt32());
            var shape = AvatarShape.From(rig, def, new VisualParamSet(order.Select(i => given[i]).ToArray(), order));
            var morphed = BodyMorph.Compute(body, def, shape);
            var pose = new AvatarPose(body, null);
            var skin = new Matrix4x4[pose.PaletteSize];
            pose.Evaluate(0, skin, shape, null);
            vectors++;
            foreach (var p in vec.GetProperty("parts").EnumerateObject())
            {
                int pi = body.Parts.ToList().FindIndex(x => x.Type == p.Name);
                Assert.True(pi >= 0, p.Name);
                var part = body.Parts[pi];
                var positions = morphed?[pi].Positions ?? part.Positions;
                Assert.Equal(p.Value.GetArrayLength(), positions.Length * 3);
                float max = 0;
                for (int i = 0; i < positions.Length; i++)
                {
                    var a = Vector3.Transform(positions[i], skin[part.BoneA[i]]);
                    var b = Vector3.Transform(positions[i], skin[part.BoneB[i]]);
                    var ours = a * (1 - part.WeightB[i]) + b * part.WeightB[i];
                    max = MathF.Max(max, (ours - V(p.Value, i * 3)).Length());
                }
                worst[p.Name] = MathF.Max(worst.GetValueOrDefault(p.Name), max);
            }
        }
        Assert.True(vectors >= 20, $"the reference is too small to prove anything: {vectors} vectors");
        var over = worst.Where(w => w.Value > SkinTolerance).ToList();
        Assert.True(over.Count == 0, $"skinned vertices off the reference by more than {SkinTolerance} m: {string.Join(", ", over.Select(w => $"{w.Key} {w.Value:G3}"))}");
    }
}
