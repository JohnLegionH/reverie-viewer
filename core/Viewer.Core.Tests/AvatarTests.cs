// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>L1 avatar assertions on small synthetic files written here: the skeleton, the binary body mesh, the skin
/// binding, the animation asset and the pose. The class runs alone: <see cref="Posing_allocates_nothing"/> reads the
/// runtime's per-thread allocation counter, and nothing else in the process runs beside it.</summary>
[Collection(nameof(AvatarTests))]
public class AvatarTests
{
    // pelvis (base) -> spine1, spine2 (extended) -> torso (base) -> chest (base) -> neck, collar (base); a collision volume
    // under the pelvis; the extended joints undo each other's offset, as the real file's spine joints do
    internal const string SkeletonXml = """
        <linden_skeleton num_bones="7" num_collision_volumes="1" version="2.0">
          <bone name="mPelvis" aliases="hip avatar_mPelvis" pos="0 0 1" rot="0 0 0" scale="1 1 1" support="base">
            <collision_volume name="PELVIS" pos="-0.01 0 -0.02" rot="0 8 0" scale="0.12 0.16 0.17" support="base"/>
            <bone name="mSpine1" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="extended">
              <bone name="mSpine2" pos="0 0 -0.1" rot="0 0 0" scale="1 1 1" support="extended">
                <bone name="mTorso" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base">
                  <bone name="mChest" pos="0 0 0.2" rot="0 0 0" scale="1 1 1" support="base">
                    <bone name="mNeck" pos="0 0 0.25" rot="0 0 0" scale="1 1 1" support="base"/>
                    <bone name="mCollarLeft" pos="0 0.1 0.2" rot="0 0 0" scale="1 1 1" support="base"/>
                  </bone>
                </bone>
              </bone>
            </bone>
          </bone>
        </linden_skeleton>
        """;

    internal const string DefinitionXml = """
        <linden_avatar version="2.0">
          <skeleton file_name="avatar_skeleton.xml">
            <attachment_point id="1" group="6" name="Chest" joint="mChest" position="0.15 0 -0.1" rotation="0 90 90"/>
            <attachment_point id="2" group="2" name="Skull" joint="mNeck" position="0 0 0.1" rotation="0 0 0"/>
          </skeleton>
          <mesh type="upperBodyMesh" lod="0" file_name="upper.llm" min_pixel_width="320"/>
          <mesh type="upperBodyMesh" lod="1" file_name="upper_1.llm" min_pixel_width="160"/>
          <mesh type="eyeBallLeftMesh" lod="0" file_name="eye.llm" min_pixel_width="320"/>
        </linden_avatar>
        """;

    internal static CharacterSkeleton Skeleton() => CharacterSkeleton.Parse(Encoding.UTF8.GetBytes(SkeletonXml), Encoding.UTF8.GetBytes(DefinitionXml));

    [Fact]
    public void The_skeleton_reads_bones_collision_volumes_parents_aliases_and_attachment_points()
    {
        var s = Skeleton();
        Assert.Equal(8, s.Joints.Count);
        Assert.Equal(7, s.BoneCount);
        Assert.Equal(1, s.CollisionVolumeCount);
        Assert.Equal(["mPelvis", "PELVIS", "mSpine1", "mSpine2", "mTorso", "mChest", "mNeck", "mCollarLeft"], s.Joints.Select(j => j.Name));
        Assert.Equal(-1, s.Joints[0].Parent);
        Assert.Equal(s.IndexOf("mSpine2"), s.Joints[s.IndexOf("mTorso")].Parent);
        Assert.Equal(0, s.IndexOf("hip"));   // an alias
        Assert.Equal(-1, s.IndexOf("mHead"));
        Assert.False(s.Joints[s.IndexOf("mSpine1")].Base);
        Assert.True(s.Joints[s.IndexOf("mTorso")].Base);
        Assert.Equal(JointKind.CollisionVolume, s.Joints[1].Kind);
        Assert.Equal(new Vector3(0.12f, 0.16f, 0.17f), s.Joints[1].Scale);
        Assert.Equal(2, s.AttachmentPoints.Count);
        Assert.Equal(("Chest", "mChest"), (s.AttachmentPoints[0].Name, s.AttachmentPoints[0].Joint));
        Assert.Equal(new Vector3(0.15f, 0, -0.1f), s.AttachmentPoints[0].Position);
    }

    [Fact]
    public void Default_joint_positions_add_up_from_the_top_and_base_ancestors_skip_extended_joints()
    {
        var s = Skeleton();
        Near(new Vector3(0, 0, 1.1f), s.DefaultWorld[s.IndexOf("mTorso")].Translation);
        Near(new Vector3(0, 0.1f, 1.5f), s.DefaultWorld[s.IndexOf("mCollarLeft")].Translation);
        Assert.Equal(s.IndexOf("mPelvis"), s.BaseAncestor(s.IndexOf("mTorso")));
        Assert.Equal(-1, s.BaseAncestor(s.IndexOf("mPelvis")));
        // a rotated joint carries its children: 90 degrees about z turns +x into +y
        var q = CharacterSkeleton.Euler(new Vector3(0, 0, 90));
        Near(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, q));
    }

    [Fact]
    public void A_skeleton_file_with_no_bones_or_a_document_type_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => CharacterSkeleton.Parse("<linden_skeleton/>"u8.ToArray()));
        Assert.ThrowsAny<System.Xml.XmlException>(() => CharacterSkeleton.Parse(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a \"b\">]><linden_skeleton><bone name=\"a\"/></linden_skeleton>")));
        Assert.Throws<InvalidDataException>(() => CharacterSkeleton.Parse("<s><bone pos=\"0 0 0\"/></s>"u8.ToArray()));   // a bone with no name
    }

    [Fact]
    public void The_joint_list_adds_each_skin_joints_base_ancestor_unless_the_list_already_ends_with_it()
    {
        var s = Skeleton();
        int P = s.IndexOf("mPelvis"), T = s.IndexOf("mTorso"), C = s.IndexOf("mChest"), N = s.IndexOf("mNeck"), L = s.IndexOf("mCollarLeft");
        // the extended spine joints between pelvis and torso are skipped: torso follows pelvis directly
        Assert.Equal([SkinBinding.Root, P, T, C, L], SkinBinding.JointList(s, ["mTorso", "mCollarLeft", "mPelvis", "mChest"]));
        // the neck comes before the collar (file order), so the collar's ancestor (the chest) is added again before it
        Assert.Equal([SkinBinding.Root, P, T, C, N, C, L], SkinBinding.JointList(s, ["mPelvis", "mTorso", "mChest", "mNeck", "mCollarLeft"]));
        // a joint alone: its ancestor first
        Assert.Equal([C, L], SkinBinding.JointList(s, ["mCollarLeft"]));
        Assert.Throws<InvalidDataException>(() => SkinBinding.JointList(s, ["mHead"]));
    }

    [Fact]
    public void A_weight_names_two_consecutive_entries_and_the_second_ones_share()
    {
        int[] list = [10, 11, 12, 13];
        var b = SkinBinding.Bind([1.0f, 2.25f, 3.0f, 0.5f], list);
        Assert.Equal((11, 11, 0f), b[0]);
        Assert.Equal((12, 13, 0.25f), b[1]);
        Assert.Equal((13, 13, 0f), b[2]);   // the last entry whole
        Assert.Equal((10, 11, 0.5f), b[3]);
        Assert.Throws<InvalidDataException>(() => SkinBinding.Bind([3.5f], list));   // would blend past the end
        Assert.Throws<InvalidDataException>(() => SkinBinding.Bind([-0.5f], list));
        Assert.Throws<InvalidDataException>(() => SkinBinding.Bind([4f], list));
    }

    [Fact]
    public void A_body_mesh_reads_back_what_was_written()
    {
        var pos = new[] { new Vector3(0, 0, 1), new Vector3(0.1f, 0, 1.2f), new Vector3(0, 0.1f, 1.3f) };
        var bytes = Llm(pos, [0, 1, 2], [1f, 1.5f, 2f], ["mPelvis", "mTorso"], detail: true);
        var m = LlmMesh.Parse(bytes);
        Assert.Equal(3, m.VertexCount);
        Assert.Equal(pos, m.Positions);
        Assert.Equal(new Vector2(0.5f, 0.25f), m.Uvs[1]);
        Assert.Equal(Vector3.UnitZ, m.Normals[2]);
        Assert.Equal([1f, 1.5f, 2f], m.Weights);
        Assert.Equal([(ushort)0, 1, 2], m.Indices);
        Assert.Equal(["mPelvis", "mTorso"], m.SkinJoints);
        var plain = LlmMesh.Parse(Llm(pos, [2, 1, 0], null, [], detail: false));
        Assert.False(plain.Weighted);
        Assert.Empty(plain.SkinJoints);
    }

    [Fact]
    public void A_truncated_body_mesh_a_wrong_header_or_an_index_past_the_vertices_is_refused()
    {
        var pos = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        var good = Llm(pos, [0, 1, 2], [1f, 1f, 1f], ["mPelvis"], detail: false);
        int unread = LlmMesh.JointNameBytes + 4;   // the morph end marker and the remap count follow what is read
        for (int cut = 1; cut < good.Length - unread; cut += 7)
            Assert.Throws<InvalidDataException>(() => LlmMesh.Parse(good.AsSpan(0, good.Length - unread - cut)));
        var bad = (byte[])good.Clone();
        bad[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => LlmMesh.Parse(bad));
        Assert.Throws<InvalidDataException>(() => LlmMesh.Parse(Llm(pos, [0, 1, 3], null, [], detail: false)));
    }

    [Fact]
    public void An_animation_reads_back_its_keys_within_the_formats_steps()
    {
        var q = Quaternion.Normalize(new Quaternion(0.2f, -0.3f, 0.1f, 0.9f));
        var bytes = Anim(2f, loop: true, loopIn: 0.5f, loopOut: 1.5f,
            [("mChest", [(0f, Quaternion.Identity), (1f, q), (2f, Quaternion.Identity)], []), ("mPelvis", [], [(0f, Vector3.Zero), (2f, new Vector3(0.5f, 0, -0.1f))])]);
        var c = AnimationClip.Parse(bytes);
        Assert.Equal(2f, c.Duration);
        Assert.True(c.Loop);
        Assert.Equal("emote", c.Emote);
        Assert.Equal(2, c.Tracks.Count);
        var chest = c.Tracks[0];
        Assert.Equal("mChest", chest.Joint);
        Assert.Equal(3, chest.Rotations.Length);
        Assert.InRange(chest.RotationTimes[1], 1f - 1e-4f, 1f + 1e-4f);
        Assert.InRange(Quaternion.Dot(chest.Rotations[1], q), 0.99999f, 1.00001f);
        Assert.Equal(Quaternion.Identity, chest.Rotations[0]);   // zero components come through as exactly zero
        Assert.Equal(Vector3.Zero, c.Tracks[1].Positions[0]);
        Assert.InRange(c.Tracks[1].Positions[1].X, 0.5f - 2e-4f, 0.5f + 2e-4f);
        Assert.InRange(c.Tracks[1].Positions[1].Z, -0.1f - 2e-4f, -0.1f + 2e-4f);
    }

    [Fact]
    public void An_animation_of_another_version_or_cut_short_is_refused()
    {
        var good = Anim(1f, false, 0, 1, [("mChest", [(0f, Quaternion.Identity), (1f, Quaternion.Identity)], [])]);
        var bad = (byte[])good.Clone();
        bad[0] = 2;
        Assert.Throws<InvalidDataException>(() => AnimationClip.Parse(bad));
        for (int cut = 1; cut < good.Length - 4; cut += 3)   // the trailing constraint count is not read
            Assert.Throws<InvalidDataException>(() => AnimationClip.Parse(good.AsSpan(0, good.Length - 4 - cut)));
    }

    [Fact]
    public void A_looping_clip_plays_to_its_loop_out_then_repeats_the_loop_and_one_that_does_not_holds_its_end()
    {
        var loop = AnimationClip.Parse(Anim(2f, true, 0.5f, 1.5f, []));
        Assert.Equal(1.0f, loop.TimeAt(1.0), 4);
        Assert.Equal(1.5f, loop.TimeAt(1.5), 4);
        Assert.Equal(0.75f, loop.TimeAt(1.75), 4);
        Assert.Equal(1.25f, loop.TimeAt(3.25), 4);
        var once = AnimationClip.Parse(Anim(2f, false, 0, 2, []));
        Assert.Equal(2f, once.TimeAt(5));
    }

    [Fact]
    public void Rotations_interpolate_spherically_and_positions_linearly_between_keys()
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var track = new JointTrack("mChest", 0, [0f, 1f], [Quaternion.Identity, turn], [0f, 1f], [Vector3.Zero, new Vector3(1, 0, 0)]);
        var half = AnimationClip.RotationAt(track, 0.5f);
        Near(Vector3.Normalize(new Vector3(1, 1, 0)), Vector3.Transform(Vector3.UnitX, half));
        Assert.Equal(turn, AnimationClip.RotationAt(track, 3f));
        Assert.Equal(Quaternion.Identity, AnimationClip.RotationAt(track, -1f));
        Near(new Vector3(0.25f, 0, 0), AnimationClip.PositionAt(track, 0.25f));
    }

    [Fact]
    public void The_default_pose_hangs_the_body_from_the_root_at_hip_height()
    {
        var body = SyntheticBody();
        var pose = new AvatarPose(body, null);
        var skin = new Matrix4x4[pose.PaletteSize];
        pose.Evaluate(0, skin);
        foreach (var m in skin) Near(new Vector3(0, 0, -1), Vector3.Transform(Vector3.Zero, m));   // every entry: down by the pelvis height
        // a vertex at the collar's default place lands 1 m lower, unrotated
        var collar = body.Parts[0].Positions[2];
        Near(collar - Vector3.UnitZ, Skinned(body, 0, 2, skin));
    }

    [Fact]
    public void A_clip_turning_the_chest_carries_the_collar_and_leaves_the_pelvis()
    {
        var body = SyntheticBody();
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var clip = AnimationClip.Parse(Anim(1f, true, 0, 1, [("mChest", [(0f, turn), (1f, turn)], [])]));
        var pose = new AvatarPose(body, clip);
        Assert.Equal(1, pose.AnimatedJoints);
        var skin = new Matrix4x4[pose.PaletteSize];
        pose.Evaluate(0.5, skin);
        // the collar (0, 0.1, 1.5) sits 0.1 m left of the chest (1.3 m up); turned 90 degrees about z it is 0.1 m behind
        Near(new Vector3(-0.1f, 0, 0.5f), Skinned(body, 0, 2, skin), 2e-3f);
        Near(new Vector3(0, 0, 0f), Skinned(body, 0, 0, skin));   // the pelvis vertex: at the root
    }

    [Fact]
    public void Posing_allocates_nothing()
    {
        var body = SyntheticBody();
        var clip = AnimationClip.Parse(Anim(1f, true, 0, 1, [("mChest", [(0f, Quaternion.Identity), (1f, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1f))], []), ("mPelvis", [], [(0f, Vector3.Zero), (1f, Vector3.UnitX)])]));
        var pose = new AvatarPose(body, clip);
        var skin = new Matrix4x4[pose.PaletteSize];
        // warm-up: the whole measured sequence once (every branch it takes: before the end key, on it, the loop's wrap),
        // so the measured poses are the steady state; then the same sequence must allocate nothing at all
        Pose1000(pose, skin);
        int collections = GC.CollectionCount(0);
        long jitted = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Pose1000(pose, skin);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"{allocated} bytes allocated over 1,000 poses (methods compiled on this thread meanwhile: " +
            $"{System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - jitted}, collections: {GC.CollectionCount(0) - collections})");
    }

    private static void Pose1000(AvatarPose pose, Matrix4x4[] skin)
    {
        for (int i = 0; i < 1000; i++) pose.Evaluate(i * 0.01, skin);
    }

    [Fact]
    public void An_unweighted_part_rides_its_joint_from_the_joints_default_place()
    {
        var s = Skeleton();
        var eye = LlmMesh.Parse(Llm([new Vector3(0.01f, 0, 0), Vector3.Zero, Vector3.UnitY * 0.01f], [0, 1, 2], null, [], detail: false));
        var body = AvatarBody.Build(s, [("eyeBallLeftMesh", "eyes", "mNeck", eye)]);
        Assert.Equal([s.IndexOf("mNeck")], body.Palette);
        Near(new Vector3(0.01f, 0, 1.55f), body.Parts[0].Positions[0]);
        Assert.All(body.Parts[0].BoneA, a => Assert.Equal(0, a));
        Assert.Throws<InvalidDataException>(() => AvatarBody.Build(s, [("eyeBallLeftMesh", "eyes", null, eye)]));
    }

    [Fact]
    public void The_definition_names_each_mesh_types_full_detail_file()
    {
        var files = AvatarBody.MeshFiles(Encoding.UTF8.GetBytes(DefinitionXml));
        Assert.Equal("upper.llm", files["upperBodyMesh"]);
        Assert.Equal("eye.llm", files["eyeBallLeftMesh"]);
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public async Task Animations_are_asked_for_through_the_viewer_asset_capability_and_kept_in_the_local_cache()
    {
        var id = Guid.NewGuid();
        Assert.Equal("https://grid.example.org/CAPS/abc/?animatn_id=" + id.ToString("D"), Viewer.Core.Protocol.LmvMaterials.AnimationUri(new Uri("https://grid.example.org/CAPS/abc/"), id).ToString());
        Assert.EndsWith("?x=1&animatn_id=" + id.ToString("D"), Viewer.Core.Protocol.LmvMaterials.AnimationUri(new Uri("https://grid.example.org/c?x=1"), id).ToString());
        var dir = TestTemp.NewFolder("anim-cache");
        try
        {
            var cache = new Viewer.Core.Assets.LocalAssetCache(dir);
            Assert.Equal(Viewer.Core.Assets.AssetFetchStatus.NotFound, (await cache.FetchAnimationAsync(id, CancellationToken.None)).Status);
            var bytes = Anim(1f, true, 0, 1, []);
            cache.PutAnimation(id, bytes);
            Assert.True(cache.HasAnimation(id));
            Assert.Equal(bytes, (await cache.FetchAnimationAsync(id, CancellationToken.None)).Bytes.ToArray());
        }
        finally { TestTemp.Delete(dir); }
    }

    [Fact]
    public void A_skinned_surface_carries_two_bones_and_their_weights_a_vertex_and_the_bone_and_weight_formats()
    {
        var body = SyntheticBody();
        var part = body.Parts[0];
        var s = Viewer.Shell.AvatarSurfaceData.Encode(part, body.Palette.Length);
        var plain = Viewer.Shell.MeshSurfaceData.Encode(new Viewer.Core.Meshing.MeshFace(
            part.Positions.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray(), part.Normals.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray(),
            part.Mesh.Uvs.SelectMany(v => new[] { v.X, v.Y }).ToArray(), part.Mesh.Indices.Select(i => (int)i).ToArray()));
        Assert.Equal(plain.Format | Viewer.Shell.AvatarSurfaceData.FormatBones | Viewer.Shell.AvatarSurfaceData.FormatWeights, s.Surface.Format);
        Assert.Equal(plain.VertexData, s.Surface.VertexData);
        Assert.Equal(3 * Viewer.Shell.AvatarSurfaceData.SkinStride, s.SkinData.Length);
        var u = new ushort[s.SkinData.Length / 2];
        Buffer.BlockCopy(s.SkinData, 0, u, 0, s.SkinData.Length);
        for (int v = 0; v < 3; v++)
        {
            Assert.Equal((ushort)part.BoneA[v], u[v * 8]);
            Assert.Equal((ushort)part.BoneB[v], u[v * 8 + 1]);
            Assert.Equal(0, u[v * 8 + 2] + u[v * 8 + 3] + u[v * 8 + 6] + u[v * 8 + 7]);
            Assert.Equal((ushort)((1f - part.WeightB[v]) * 65535f), u[v * 8 + 4]);
            Assert.Equal((ushort)(part.WeightB[v] * 65535f), u[v * 8 + 5]);
        }
        Assert.Throws<InvalidDataException>(() => Viewer.Shell.AvatarSurfaceData.Encode(part, 1));   // a bone past the palette
    }

    // ---- the real character folder (local only) ----

    [CharacterFact]
    public void The_character_folders_skeleton_and_body_load_and_bind()
    {
        string dir = CharacterFactAttribute.Folder!;
        var body = AvatarBody.Load(dir);
        var s = body.Skeleton;
        Assert.Equal(133, s.BoneCount);
        Assert.Equal(26, s.CollisionVolumeCount);
        // the body's attachment points name skeleton joints; the HUD points name the screen, which is not one
        var onBody = s.AttachmentPoints.Where(p => p.Joint != "mScreen").ToList();
        Assert.True(onBody.Count >= 40, $"{onBody.Count} attachment points on the body");
        Assert.All(onBody, p => Assert.True(p.Joint == CharacterSkeleton.RootName || s.IndexOf(p.Joint) >= 0, $"attachment point {p.Name} names joint {p.Joint}"));
        Assert.Equal(6, body.Parts.Count);
        Assert.InRange(body.FootHeight, -0.05f, 0.05f);
        Assert.InRange(body.Height, 1.7f, 2.0f);
        Assert.InRange(body.Palette.Length, 15, 32);
        Assert.All(body.Parts, p => Assert.Equal(p.Mesh.VertexCount, p.BoneA.Length));
        var pose = new AvatarPose(body, null);
        var skin = new Matrix4x4[pose.PaletteSize];
        pose.Evaluate(0, skin);
        float pelvis = s.Joints[s.IndexOf("mPelvis")].Position.Z;
        foreach (var m in skin) Near(new Vector3(0, 0, -pelvis), Vector3.Transform(Vector3.Zero, m), 1e-4f);
    }

    [CharacterFact("walk")]
    public void The_grids_walk_animation_drives_the_body_and_loops()
    {
        var body = AvatarBody.Load(CharacterFactAttribute.Folder!);
        var clip = AnimationClip.Parse(File.ReadAllBytes(CharacterFactAttribute.AnimationPath("walk")!));
        Assert.True(clip.Loop);
        Assert.InRange(clip.Duration, 0.5f, 3f);
        var pose = new AvatarPose(body, clip);
        Assert.True(pose.AnimatedJoints >= 10, $"{pose.AnimatedJoints} joints animated");
        var a = new Matrix4x4[pose.PaletteSize];
        var b = new Matrix4x4[pose.PaletteSize];
        pose.Evaluate(clip.LoopOut + 0.1, a);
        pose.Evaluate(clip.LoopOut + 0.1 + (clip.LoopOut - clip.LoopIn), b);
        for (int i = 0; i < a.Length; i++) Assert.True(Matrix4x4Near(a[i], b[i], 1e-3f), $"entry {i} differs one loop later");
        pose.Evaluate(clip.LoopIn + (clip.LoopOut - clip.LoopIn) / 2, b);
        Assert.Contains(Enumerable.Range(0, a.Length), i => !Matrix4x4Near(a[i], b[i], 1e-2f));   // and it moves within the loop
    }

    // ---- helpers ----

    /// <summary>One weighted part over the synthetic skeleton: vertices at the pelvis, the torso and the collar.</summary>
    internal static AvatarBody SyntheticBody()
    {
        var s = Skeleton();
        // joint list for [pelvis, torso, chest, collar]: root, pelvis, torso, chest, collar -> weight 1 = pelvis, 2 = torso, 4 = collar
        var pos = new[] { new Vector3(0, 0, 1), new Vector3(0, 0, 1.1f), new Vector3(0, 0.1f, 1.5f) };
        var mesh = LlmMesh.Parse(Llm(pos, [0, 1, 2], [1f, 2f, 4f], ["mPelvis", "mTorso", "mChest", "mCollarLeft"], detail: false));
        return AvatarBody.Build(s, [("upperBodyMesh", "upper", null, mesh)]);
    }

    private static Vector3 Skinned(AvatarBody body, int part, int v, Matrix4x4[] skin)
    {
        var p = body.Parts[part];
        var x = p.Positions[v];
        return Vector3.Lerp(Vector3.Transform(x, skin[p.BoneA[v]]), Vector3.Transform(x, skin[p.BoneB[v]]), p.WeightB[v]);
    }

    internal static byte[] Llm(Vector3[] pos, ushort[] indices, float[]? weights, string[] joints, bool detail, (string Name, int[] Index, Vector3[] Coords, Vector3[] Normals, Vector2[] Uvs)[]? morphs = null)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes(LlmMesh.Header.PadRight(LlmMesh.HeaderBytes, '\0')));
        w.Write((byte)(weights is null ? 0 : 1));
        w.Write((byte)(detail ? 1 : 0));
        for (int i = 0; i < 6; i++) w.Write(0f);   // position, rotation
        w.Write((byte)0);                          // rotation order
        w.Write(1f); w.Write(1f); w.Write(1f);
        w.Write((ushort)pos.Length);
        foreach (var p in pos) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        foreach (var _ in pos) { w.Write(0f); w.Write(0f); w.Write(1f); }    // normals
        foreach (var _ in pos) { w.Write(1f); w.Write(0f); w.Write(0f); }    // binormals
        for (int i = 0; i < pos.Length; i++) { w.Write(i * 0.5f); w.Write(i * 0.25f); }
        if (detail) foreach (var _ in pos) { w.Write(0f); w.Write(0f); }
        if (weights is not null) foreach (var x in weights) w.Write(x);
        w.Write((ushort)(indices.Length / 3));
        foreach (var i in indices) w.Write(i);
        if (weights is not null)
        {
            w.Write((ushort)joints.Length);
            foreach (var j in joints) w.Write(Encoding.ASCII.GetBytes(j.PadRight(LlmMesh.JointNameBytes, '\0')));
        }
        foreach (var (name, index, coords, normals, uvs) in morphs ?? [])
        {
            w.Write(Encoding.ASCII.GetBytes(name.PadRight(64, '\0')));
            w.Write(index.Length);
            for (int i = 0; i < index.Length; i++)
            {
                w.Write(index[i]);
                w.Write(coords[i].X); w.Write(coords[i].Y); w.Write(coords[i].Z);
                w.Write(normals[i].X); w.Write(normals[i].Y); w.Write(normals[i].Z);
                w.Write(0f); w.Write(0f); w.Write(0f);   // binormal offset
                w.Write(uvs[i].X); w.Write(uvs[i].Y);
            }
        }
        w.Write(Encoding.ASCII.GetBytes("End Morphs".PadRight(64, '\0')));
        w.Write(0);   // no remaps
        return ms.ToArray();
    }

    internal static byte[] Anim(float duration, bool loop, float loopIn, float loopOut,
        IReadOnlyList<(string Joint, (float T, Quaternion Q)[] Rot, (float T, Vector3 P)[] Pos)> joints)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)1); w.Write((ushort)0);
        w.Write(3);
        w.Write(duration);
        w.Write(Encoding.UTF8.GetBytes("emote\0"));
        w.Write(loopIn); w.Write(loopOut);
        w.Write(loop ? 1 : 0);
        w.Write(0.3f); w.Write(0.3f);
        w.Write(0);
        w.Write(joints.Count);
        foreach (var (name, rot, pos) in joints)
        {
            w.Write(Encoding.UTF8.GetBytes(name + "\0"));
            w.Write(4);
            w.Write(rot.Length);
            foreach (var (t, q0) in rot)
            {
                var q = q0.W < 0 ? -q0 : q0;   // the format keeps x, y, z and rebuilds a positive w
                w.Write(Q(t / duration, 0, 1)); w.Write(Q(q.X, -1, 1)); w.Write(Q(q.Y, -1, 1)); w.Write(Q(q.Z, -1, 1));
            }
            w.Write(pos.Length);
            foreach (var (t, p) in pos) { w.Write(Q(t / duration, 0, 1)); w.Write(Q(p.X, -5, 5)); w.Write(Q(p.Y, -5, 5)); w.Write(Q(p.Z, -5, 5)); }
        }
        w.Write(0);   // no constraints
        return ms.ToArray();
        static ushort Q(float v, float lo, float hi) => (ushort)Math.Round((Math.Clamp(v, lo, hi) - lo) / (hi - lo) * 65535);
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    private static bool Matrix4x4Near(Matrix4x4 a, Matrix4x4 b, float tol)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                if (MathF.Abs(a[r, c] - b[r, c]) > tol) return false;
        return true;
    }
}

/// <summary>A test on the real character folder (local input, never in the repo): skipped with its reason unless
/// NV_CHARACTER_DIR names that folder; a named animation is read from the local fixtures' asset cache.</summary>
public sealed class CharacterFactAttribute : FactAttribute
{
    public const string Variable = "NV_CHARACTER_DIR";
    public static readonly IReadOnlyDictionary<string, Guid> Animations = new Dictionary<string, Guid>
    {
        ["walk"] = Guid.Parse("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0"),
        ["stand"] = Guid.Parse("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f"),
    };

    public CharacterFactAttribute(string? animation = null)
    {
        if (Folder is null)
            Skip = $"local-only character data: {Variable} is not set (the character files are not in the repo)";
        else if (animation is not null && AnimationPath(animation) is not { } p)
            Skip = $"local-only animation '{animation}' is absent from the local fixtures' asset cache (grid content, never committed)";
    }

    /// <summary>The character folder, or null when the variable is unset; a set value that is not an existing absolute
    /// folder fails the run (no silent skips).</summary>
    public static string? Folder
    {
        get
        {
            var v = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(v)) return null;
            if (!Path.IsPathFullyQualified(v) || !Directory.Exists(v)) throw new InvalidOperationException($"{Variable} must name an existing absolute folder: '{v}'");
            return v;
        }
    }

    public static string? AnimationPath(string name)
    {
        var p = new Viewer.Core.Assets.LocalAssetCache(Path.Combine(TestPaths.FixturesLocal, "assets")).AnimationPath(Animations[name]);
        return File.Exists(p) ? p : null;
    }
}

/// <summary>AvatarTests run alone (no other test class in parallel with them).</summary>
[CollectionDefinition(nameof(AvatarTests), DisableParallelization = true)]
public sealed class AvatarTestsCollection;
