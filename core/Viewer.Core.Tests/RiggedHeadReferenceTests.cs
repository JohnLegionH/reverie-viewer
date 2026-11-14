// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Viewer.Core.Protocol;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A rigged mesh (a mesh head) on a shaped avatar, drawn the way the skinning shader draws it, against a reference worked out separately
/// from the official sources (local input, never in the repo; two environment variables name the mesh asset and the reference file).
/// The reference shapes the skeleton from the visual parameters, adds the collision volumes as joints, takes the mesh's joint position
/// overrides, builds each joint's skinning matrix (inverse bind matrix times the joint's world matrix: LLSkinningUtil::initSkinningMatrixPalette)
/// and skins the chosen faces (bind shape first, then the weighted blend: LLRiggedVolume::update).
/// </summary>
public class RiggedHeadReferenceTests
{
    private const string AssetVariable = "NV_MESHHEAD_ASSET";
    private const string ReferenceVariable = "NV_MESHHEAD_REFERENCE";

    /// <summary>Metres a joint's skinning matrix may differ in any entry that moves a point (translation row), and unitless in the rest.</summary>
    private const float PaletteTolerance = 2e-4f;
    private const float VertexTolerance = 3e-4f;

    private sealed record Ours(string[] Joints, Matrix4x4[] Palette, Matrix4x4 BindShape, Vector3[] Skinned, IReadOnlyList<string> Overridden);

    private static Matrix4x4 M(JsonElement a, int o) => new(
        a[o].GetSingle(), a[o + 1].GetSingle(), a[o + 2].GetSingle(), a[o + 3].GetSingle(),
        a[o + 4].GetSingle(), a[o + 5].GetSingle(), a[o + 6].GetSingle(), a[o + 7].GetSingle(),
        a[o + 8].GetSingle(), a[o + 9].GetSingle(), a[o + 10].GetSingle(), a[o + 11].GetSingle(),
        a[o + 12].GetSingle(), a[o + 13].GetSingle(), a[o + 14].GetSingle(), a[o + 15].GetSingle());

    private static float MaxDifference(Matrix4x4 a, Matrix4x4 b)
    {
        float worst = 0;
        foreach (var (x, y) in Entries(a).Zip(Entries(b))) worst = MathF.Max(worst, MathF.Abs(x - y));
        return worst;
    }

    private static IEnumerable<float> Entries(Matrix4x4 m)
    {
        yield return m.M11; yield return m.M12; yield return m.M13; yield return m.M14;
        yield return m.M21; yield return m.M22; yield return m.M23; yield return m.M24;
        yield return m.M31; yield return m.M32; yield return m.M33; yield return m.M34;
        yield return m.M41; yield return m.M42; yield return m.M43; yield return m.M44;
    }

    // SL point -> engine point and back, as the skinning tables use (x, z, -y)
    private static Vector3 ToEngine(Vector3 v) => new(v.X, v.Z, -v.Y);
    private static Vector3 FromEngine(Vector3 v) => new(v.X, -v.Z, v.Y);

    /// <summary>The avatar's joints for the given parameter bytes, the mesh's overrides applied, and the mesh's skinned faces through the shader model.</summary>
    private static Ours Draw(AvatarBody body, byte[] asset, ImmutableArray<int> order, byte[] bytes, int[] faces)
    {
        var rig = new AvatarRig(body.Skeleton);
        var shape = AvatarShape.From(rig, body.Shape!, new VisualParamSet(bytes, order));
        var decoded = LmvMesh.Decode(Guid.NewGuid(), asset);
        Assert.True(decoded.Ok, decoded.Refusal?.Reason);
        var geometry = decoded.Geometry!;
        var skinData = geometry.Skin!.Data!;
        var skin = skinData.Block!;
        var overrides = new JointOverrides(rig);
        overrides.Add(Guid.NewGuid(), skin);
        var joints = new AvatarJoints(rig) { Shape = shape, Overrides = overrides };
        joints.Begin(null, 0);
        var worlds = new Matrix4x4[rig.NodeCount + 1];
        joints.Fill(worlds);
        int n = skin.UsableJoints;
        var palette = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            int node = rig.FindForSkinning(skin.JointNames[i]);
            palette[i] = skin.InverseBind[i] * (node < 0 ? worlds[rig.NodeCount] : worlds[node]);
        }
        // the shader's own tables
        var row = new float[worlds.Length * SkinTables.FloatsPerMatrix];
        SkinTables.PackBones(worlds, row);
        var table = new float[n * SkinTables.FloatsPerMatrix];
        var nodes = new float[n];
        SkinTables.BuildTable(rig, skin, table, nodes);
        var lod = geometry.Lods[MeshGeometry.LodCount - 1]!;
        var influences = skinData.Lods[MeshGeometry.LodCount - 1]!;
        var skinned = new List<Vector3>();
        foreach (int f in faces)
        {
            var face = lod.Faces[f];
            var infl = influences[f];
            if (Environment.GetEnvironmentVariable("NV_MESHHEAD_RAW") is { Length: > 0 } raw)
                File.WriteAllLines(raw, Enumerable.Range(0, Math.Min(6, face.Vertices)).Select(v => $"{v} pos {face.Positions[v * 3]:G7} {face.Positions[v * 3 + 1]:G7} {face.Positions[v * 3 + 2]:G7} joints {infl.Joints[v * 4]},{infl.Joints[v * 4 + 1]},{infl.Joints[v * 4 + 2]},{infl.Joints[v * 4 + 3]} w {infl.Weights[v * 4]:G6},{infl.Weights[v * 4 + 1]:G6},{infl.Weights[v * 4 + 2]:G6},{infl.Weights[v * 4 + 3]:G6}").Append($"count {face.Vertices} bind {skin.BindShape}"));
            Assert.Equal(face.Vertices, infl.Vertices);
            for (int v = 0; v < face.Vertices; v++)
            {
                var p = ToEngine(new Vector3(face.Positions[v * 3], face.Positions[v * 3 + 1], face.Positions[v * 3 + 2]));
                var j = new float[4]; var w = new float[4];
                for (int k = 0; k < 4; k++) { j[k] = infl.Joints[v * 4 + k]; w[k] = infl.Weights[v * 4 + k]; }
                skinned.Add(FromEngine(SkinTables.SkinEngine(p, j, w, row, table, nodes)));
            }
        }
        var names = Enumerable.Range(0, n).Where(i => skin.HasJointOverrides).Select(i => skin.JointNames[i]).ToList();
        return new(skin.JointNames.Take(n).ToArray(), palette, skin.BindShape, skinned.ToArray(), names);
    }

    [LocalReferenceFact(ReferenceVariable)]
    public void A_mesh_head_skins_like_the_reference_joint_by_joint_and_vertex_by_vertex()
    {
        var assetPath = LocalReferenceFactAttribute.Path(AssetVariable);
        Assert.True(assetPath is not null, $"{AssetVariable} names no file");
        var asset = File.ReadAllBytes(assetPath!);
        using var doc = JsonDocument.Parse(File.ReadAllText(LocalReferenceFactAttribute.Path(ReferenceVariable)!));
        var body = AvatarBody.Load(CharacterFactAttribute.Folder!);
        var meta = doc.RootElement.GetProperty("meta");
        var order = meta.GetProperty("transmitOrder").EnumerateArray().Select(e => e.GetInt32()).ToImmutableArray();
        var faces = meta.GetProperty("faces").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var bad = new List<string>();
        var lines = new List<string>();
        float worstPalette = 0, worstVertex = 0;
        int vectors = 0, joints = 0;
        foreach (var vec in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var given = vec.GetProperty("bytes").EnumerateArray().ToDictionary(p => p[0].GetInt32(), p => (byte)p[1].GetInt32());
            var ours = Draw(body, asset, order, order.Select(i => given[i]).ToArray(), faces);
            var palette = vec.GetProperty("palette");
            Assert.Equal(palette.GetArrayLength(), ours.Palette.Length);
            vectors++;
            if (Environment.GetEnvironmentVariable("NV_MESHHEAD_DUMP") is { Length: > 0 } dump)
                File.AppendAllLines(dump, ours.Palette.Select((m, i) => $"{vec.GetProperty("name").GetString()} {ours.Joints[i]} " + string.Join(' ', Entries(m).Select(e => e.ToString("G6")))));
            for (int i = 0; i < ours.Palette.Length; i++)
            {
                float d = MaxDifference(ours.Palette[i], M(palette[i], 0));
                worstPalette = MathF.Max(worstPalette, d);
                joints++;
                if (d > PaletteTolerance) bad.Add($"{vec.GetProperty("name").GetString()} joint {ours.Joints[i]}: matrix entry off by {d:G3}");
            }
            var skinned = vec.GetProperty("skinned");
            Assert.Equal(skinned.GetArrayLength(), ours.Skinned.Length * 3);
            int over = 0; float worstHere = 0;
            for (int v = 0; v < ours.Skinned.Length; v++)
            {
                var r = new Vector3(skinned[v * 3].GetSingle(), skinned[v * 3 + 1].GetSingle(), skinned[v * 3 + 2].GetSingle());
                float d = (ours.Skinned[v] - r).Length();
                worstVertex = MathF.Max(worstVertex, d);
                worstHere = MathF.Max(worstHere, d);
                if (d > VertexTolerance) { over++; if (bad.Count < 40) bad.Add($"{vec.GetProperty("name").GetString()} vertex {v}: {d:G3} m off"); }
            }
            lines.Add($"{vec.GetProperty("name").GetString()}: {over} of {ours.Skinned.Length} vertices over {VertexTolerance} m, worst {worstHere:G3} m");
        }
        if (Environment.GetEnvironmentVariable("NV_MESHHEAD_VERTICES") is { Length: > 0 } vf) File.WriteAllLines(vf, lines);
        Assert.True(vectors >= 2 && joints >= 50, $"the reference is too small to prove anything: {vectors} vectors, {joints} joint checks");
        Assert.True(bad.Count == 0, $"worst skinning-matrix entry {worstPalette:G3}, worst vertex {worstVertex:G3} m; {bad.Count} differences; first: {string.Join(" | ", bad.Take(8))}");
    }

    /// <summary>The drawn face width at fixed vertices: the vertices of widest reach in a cheek band and in a jaw band, chosen on the first vector (the unshaped
    /// avatar) and measured on every vector, ours against the reference. Writes a line per vector to the file named by NV_MESHHEAD_WIDTHS when set.</summary>
    [LocalReferenceFact(ReferenceVariable)]
    public void The_drawn_face_width_matches_the_reference_at_fixed_vertices()
    {
        var assetPath = LocalReferenceFactAttribute.Path(AssetVariable);
        Assert.True(assetPath is not null, $"{AssetVariable} names no file");
        var asset = File.ReadAllBytes(assetPath!);
        using var doc = JsonDocument.Parse(File.ReadAllText(LocalReferenceFactAttribute.Path(ReferenceVariable)!));
        var body = AvatarBody.Load(CharacterFactAttribute.Folder!);
        var meta = doc.RootElement.GetProperty("meta");
        var order = meta.GetProperty("transmitOrder").EnumerateArray().Select(e => e.GetInt32()).ToImmutableArray();
        var faces = meta.GetProperty("faces").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        (int Left, int Right)? cheek = null, jaw = null;
        var lines = new List<string>();
        var bad = new List<string>();
        foreach (var vec in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var given = vec.GetProperty("bytes").EnumerateArray().ToDictionary(p => p[0].GetInt32(), p => (byte)p[1].GetInt32());
            var ours = Draw(body, asset, order, order.Select(i => given[i]).ToArray(), faces);
            var s = vec.GetProperty("skinned");
            var theirs = Enumerable.Range(0, ours.Skinned.Length).Select(v => new Vector3(s[v * 3].GetSingle(), s[v * 3 + 1].GetSingle(), s[v * 3 + 2].GetSingle())).ToArray();
            if (cheek is null)
            {
                // the head part only: above the lowest quarter of the face's height (the neck and chest pieces of the same face sit below it)
                float zMin = theirs.Min(p => p.Z), zMax = theirs.Max(p => p.Z);
                float top = zMax, height = 0.26f;   // a head is about a quarter of a metre tall
                (int, int) Widest(float lo, float hi)
                {
                    int l = -1, r = -1;
                    for (int v = 0; v < theirs.Length; v++)
                    {
                        float z = theirs[v].Z;
                        if (z < top - height * hi || z > top - height * lo) continue;
                        if (l < 0 || theirs[v].Y > theirs[l].Y) l = v;
                        if (r < 0 || theirs[v].Y < theirs[r].Y) r = v;
                    }
                    return (l, r);
                }
                cheek = Widest(0.45f, 0.65f);
                jaw = Widest(0.75f, 0.95f);
                Assert.True(cheek.Value.Left >= 0 && jaw.Value.Left >= 0, $"no vertices in the bands (z {zMin:G3}..{zMax:G3})");
            }
            float OursWidth((int Left, int Right) p) => MathF.Abs(ours.Skinned[p.Left].Y - ours.Skinned[p.Right].Y);
            float TheirsWidth((int Left, int Right) p) => MathF.Abs(theirs[p.Left].Y - theirs[p.Right].Y);
            string name = vec.GetProperty("name").GetString()!;
            lines.Add($"{name}: cheek width ours {OursWidth(cheek.Value) * 1000:F2} mm, reference {TheirsWidth(cheek.Value) * 1000:F2} mm; jaw width ours {OursWidth(jaw!.Value) * 1000:F2} mm, reference {TheirsWidth(jaw.Value) * 1000:F2} mm");
            if (MathF.Abs(OursWidth(cheek.Value) - TheirsWidth(cheek.Value)) > 0.5e-3f || MathF.Abs(OursWidth(jaw.Value) - TheirsWidth(jaw.Value)) > 0.5e-3f)
                bad.Add(lines[^1]);
        }
        if (Environment.GetEnvironmentVariable("NV_MESHHEAD_WIDTHS") is { Length: > 0 } outFile) File.WriteAllLines(outFile, lines);
        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }
}
