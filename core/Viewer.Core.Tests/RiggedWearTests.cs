// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Rigged meshes worn by an avatar, engine-free: the matrices the vertex shader reads (an avatar's row of joint matrices, a
/// mesh's table of inverse binds and joint nodes), a C# model of that shader checked against the placement maths
/// (<see cref="AttachmentPlacement"/>) on a worked pose, joint position overrides reaching the pose, and which override wins
/// when several worn meshes ask for the same joint. Expected values are worked by hand from the rule each test names
/// (the engine's axes are SL's (x, y, z) turned to (x, z, -y), as the surface encoder turns vertices; right-handed rotations;
/// the row-vector convention of System.Numerics; the official viewer's override rules as <see cref="JointOverrides"/> records
/// them). Pure, runs in parallel.
/// </summary>
public class RiggedWearTests
{
    // mPelvis (0,0,1) > mTorso (0,0,.1) > mChest (0,0,.2); one attachment point on the chest.
    private const string SkeletonXml = """
        <linden_skeleton version="2.0">
          <bone name="mPelvis" pos="0 0 1" rot="0 0 0" scale="1 1 1" support="base" aliases="hip">
            <bone name="mTorso" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base">
              <bone name="mChest" pos="0 0 0.2" rot="0 0 0" scale="1 1 1" support="base"/>
            </bone>
          </bone>
        </linden_skeleton>
        """;

    private const string DefinitionXml = """
        <linden_avatar>
          <attachment_point id="1" group="6" name="Chest" joint="mChest" position="0.15 0 -0.1" rotation="0 90 90"/>
        </linden_avatar>
        """;

    private static AvatarRig Rig() => new(CharacterSkeleton.Parse(Encoding.UTF8.GetBytes(SkeletonXml), Encoding.UTF8.GetBytes(DefinitionXml)));

    private static void Near(Vector3 expected, Vector3 actual, float tol = 1e-4f) =>
        Assert.True((expected - actual).Length() <= tol, $"expected {expected}, got {actual}");

    private static MeshSkinBlock Skin(string[] names, Matrix4x4[] inverse, Matrix4x4? bindShape = null, Matrix4x4[]? alternate = null, float pelvis = 0f, bool lockScale = false) =>
        new(names, inverse, bindShape ?? Matrix4x4.Identity, alternate ?? [], pelvis, lockScale, false);

    private static Guid Id(byte last) => new(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, last });

    // ---- the packing ----

    [Fact]
    public void A_matrix_is_packed_in_the_engines_axes_three_texels_of_rows_with_the_translation_last()
    {
        // SL (x, y, z) is the engine's (x, z, -y): a move of (1, 2, 3) in SL is a move of (1, 3, -2) in the engine. Each texel is one
        // row of the column-vector matrix: (m00 m01 m02 tx), so the three translations are the fourth channels.
        var texels = new float[SkinTables.FloatsPerMatrix];
        SkinTables.PackMatrix(Matrix4x4.CreateTranslation(1, 2, 3), texels);
        Assert.Equal(new[] { 1f, 0f, 0f, 1f, 0f, 1f, 0f, 3f, 0f, 0f, 1f, -2f }, texels);
    }

    [Fact]
    public void A_turn_about_SL_up_is_a_turn_about_the_engines_up_the_other_way_round_of_the_axes()
    {
        // 90 degrees counter-clockwise about SL z takes SL (1,0,0) to (0,1,0). In engine axes that is (1,0,0) to (0,0,-1).
        var texels = new float[SkinTables.FloatsPerMatrix];
        SkinTables.PackMatrix(Matrix4x4.CreateRotationZ(MathF.PI / 2), texels);
        Vector3 Apply(Vector3 v) => new(
            texels[0] * v.X + texels[1] * v.Y + texels[2] * v.Z,
            texels[4] * v.X + texels[5] * v.Y + texels[6] * v.Z,
            texels[8] * v.X + texels[9] * v.Y + texels[10] * v.Z);
        Near(new Vector3(0, 0, -1), Apply(new Vector3(1, 0, 0)));
        Near(new Vector3(0, 1, 0), Apply(new Vector3(0, 1, 0)));   // up stays up
    }

    [Fact]
    public void A_table_holds_each_binds_matrix_with_the_bind_shape_folded_in_and_the_node_it_binds_to()
    {
        // vertex * bindShape * (blend of inverse binds * joint worlds) equals the blend of (bindShape * inverse bind) * joint worlds
        // because the weights add up to one, so the table keeps one matrix a joint. mChest is node 2 here; a name nothing matches
        // binds to mPelvis (node 0) as the official viewer repairs it; mRoot is the entry after the last node.
        var rig = Rig();
        var skin = Skin(["mChest", "nonsense", "mRoot"],
            [Matrix4x4.CreateTranslation(0, 0, -1.3f), Matrix4x4.CreateTranslation(0, 0, -1f), Matrix4x4.Identity],
            bindShape: Matrix4x4.CreateTranslation(0.5f, 0, 0));
        var matrices = new float[skin.UsableJoints * SkinTables.FloatsPerMatrix];
        var nodes = new float[skin.UsableJoints];
        SkinTables.BuildTable(rig, skin, matrices, nodes);
        Assert.Equal(new float[] { 2, 0, rig.NodeCount }, nodes);
        // joint 0: bind shape (+0.5 in x) then inverse bind (-1.3 in z) = a move of (0.5, 0, -1.3) in SL = (0.5, -1.3, 0) in the engine
        Assert.Equal(new[] { 1f, 0f, 0f, 0.5f, 0f, 1f, 0f, -1.3f, 0f, 0f, 1f, 0f }, matrices.AsSpan(0, 12).ToArray());
    }

    // ---- the shader's maths against the placement maths ----

    [Fact]
    public void The_shader_model_puts_a_vertex_where_the_placement_maths_does_on_a_worked_pose()
    {
        // mChest turned 90 degrees about up. The vertex (0.1, 0, 1.2) in the mesh's own (root) frame, bind shape +0.01 in x, weights .25 mTorso /
        // .75 mChest. After the bind shape (0.11, 0, 1.2); mTorso is not moved, so that point stays; mChest's joint is at (0, 0, 1.3) in the root
        // frame, the point is (0.11, 0, -0.1) from it and the turn takes it to (0, 0.11, -0.1), so (0, 0.11, 1.2). The body's frame has the
        // pelvis at the origin (a pelvis height of 1 lower): (0.11, 0, 0.2) and (0, 0.11, 0.2). The blend is (0.0275, 0.0825, 0.2), which is
        // (0.0275, 0.2, -0.0825) in the engine's axes.
        var rig = Rig();
        var skin = Skin(["mTorso", "mChest"], [Matrix4x4.CreateTranslation(0, 0, -1.1f), Matrix4x4.CreateTranslation(0, 0, -1.3f)],
            bindShape: Matrix4x4.CreateTranslation(0.01f, 0, 0));
        var clip = new AnimationClip
        {
            Duration = 1f, Loop = true, LoopIn = 0, LoopOut = 1, Priority = 1, EaseIn = 0, EaseOut = 0, Emote = "",
            Tracks = [new JointTrack("mChest", 1, [0f, 1f], [Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)], [], [])],
        };
        var joints = new AvatarJoints(rig);
        joints.Begin(AvatarJoints.TracksFor(rig, clip), 0.5f);
        var worlds = new Matrix4x4[rig.NodeCount + 1];
        joints.Fill(worlds);
        var row = new float[(rig.NodeCount + 1) * SkinTables.FloatsPerMatrix];
        SkinTables.PackBones(worlds, row);
        var matrices = new float[skin.UsableJoints * SkinTables.FloatsPerMatrix];
        var nodes = new float[skin.UsableJoints];
        SkinTables.BuildTable(rig, skin, matrices, nodes);

        var engineVertex = new Vector3(0.1f, 1.2f, 0f);   // (0.1, 0, 1.2) in SL
        var modelled = SkinTables.SkinEngine(engineVertex, [0, 1, 0, 0], [0.25f, 0.75f, 0, 0], row, matrices, nodes);
        Near(new Vector3(0.0275f, 0.2f, -0.0825f), modelled);

        // and the placement maths (palette from the same pose, bind shape first, then the weighted sum) gives the same point in SL axes
        var resolved = new int[2];
        AttachmentPlacement.ResolveJoints(rig, skin, resolved);
        var palette = new Matrix4x4[2];
        AttachmentPlacement.Palette(skin, resolved, worlds.AsSpan(0, rig.NodeCount), palette);
        var sl = AttachmentPlacement.SkinPosition(new Vector3(0.1f, 0, 1.2f), skin.BindShape, palette, [0, 1, 0, 0], [0.25f, 0.75f, 0, 0]);
        Near(new Vector3(sl.X, sl.Z, -sl.Y), modelled);
    }

    [Fact]
    public void A_vertex_bound_to_the_root_sits_a_pelvis_height_below_the_pelvis_frames_origin()
    {
        // mRoot's world is the identity in the root frame; in the body's frame (pelvis at the origin) the root is the pelvis's rest height below it.
        var rig = Rig();
        var skin = Skin(["mRoot"], [Matrix4x4.Identity]);
        var joints = new AvatarJoints(rig);
        joints.Begin(null, 0);
        var worlds = new Matrix4x4[rig.NodeCount + 1];
        joints.Fill(worlds);
        var row = new float[(rig.NodeCount + 1) * SkinTables.FloatsPerMatrix];
        SkinTables.PackBones(worlds, row);
        var matrices = new float[SkinTables.FloatsPerMatrix];
        var nodes = new float[1];
        SkinTables.BuildTable(rig, skin, matrices, nodes);
        // SL (0, 0, 1) -> (0, 0, 0) in the body's frame; engine (0, 1, 0) -> (0, 0, 0)
        Near(Vector3.Zero, SkinTables.SkinEngine(new Vector3(0, 1, 0), [0, 0, 0, 0], [1, 0, 0, 0], row, matrices, nodes));
    }

    // ---- overrides reach the pose ----

    [Fact]
    public void A_joint_position_override_moves_the_joint_and_what_hangs_from_it_and_it_wins_over_the_clips_position()
    {
        // mChest's override is (0, 0, 0.5) from mTorso instead of (0, 0, 0.2): in the body's frame mTorso is at (0,0,.1) so mChest is at (0,0,.6).
        // The official viewer's setPosition replaces the requested position (an animation's too) with the active override.
        var rig = Rig();
        int chest = rig.Find("mChest");
        var overrides = new JointOverrides(rig);
        var alternate = new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 0, 0.5f) };
        Assert.True(overrides.Add(Id(1), Skin(["mTorso", "mChest"], [Matrix4x4.Identity, Matrix4x4.Identity], alternate: [Matrix4x4.CreateTranslation(0, 0, 0.1f), alternate[1]])));
        var moved = new Vector3(0, 0, 0.9f);
        var clip = new AnimationClip
        {
            Duration = 1f, Loop = true, LoopIn = 0, LoopOut = 1, Priority = 1, EaseIn = 0, EaseOut = 0, Emote = "",
            Tracks = [new JointTrack("mChest", 1, [], [], [0f, 1f], [moved, moved])],
        };
        var joints = new AvatarJoints(rig);
        joints.Begin(AvatarJoints.TracksFor(rig, clip), 0.5f);
        Near(moved + new Vector3(0, 0, 0.1f), joints.World(chest).Translation);   // before: the clip's position
        joints.Overrides = overrides;
        joints.Begin(AvatarJoints.TracksFor(rig, clip), 0.5f);
        Near(new Vector3(0, 0, 0.6f), joints.World(chest).Translation);
        overrides.Remove(Id(1));
        joints.Invalidate();
        Near(moved + new Vector3(0, 0, 0.1f), joints.World(chest).Translation);   // removed: the clip's again
    }

    // ---- who wins ----

    private static MeshSkinBlock Overriding(float torsoZ, float pelvis = 0f) =>
        Skin(["mTorso"], [Matrix4x4.Identity], alternate: [Matrix4x4.CreateTranslation(0, 0, torsoZ)], pelvis: pelvis);

    [Fact]
    public void Of_several_worn_meshes_overriding_a_joint_the_greatest_asset_id_wins_and_detaching_it_hands_over_to_the_next()
    {
        var rig = Rig();
        int torso = rig.Find("mTorso");
        var wear = new RiggedWear(rig);
        Assert.True(wear.Add(1, Id(0x0a), Overriding(0.3f)));
        Assert.Equal(0.3f, wear.Overrides.PositionOf(torso).Z, 5);
        Assert.True(wear.Add(2, Id(0x0b), Overriding(0.4f)));       // the greater id wins
        Assert.Equal(0.4f, wear.Overrides.PositionOf(torso).Z, 5);
        Assert.False(wear.Add(3, Id(0x01), Overriding(0.5f)));      // a lesser id: the winner does not change, and the call says so
        Assert.Equal(0.4f, wear.Overrides.PositionOf(torso).Z, 5);
        Assert.True(wear.Remove(2));                                // the winner leaves: the next greatest, 0x0a
        Assert.Equal(0.3f, wear.Overrides.PositionOf(torso).Z, 5);
        Assert.True(wear.Remove(1));
        Assert.Equal(0.5f, wear.Overrides.PositionOf(torso).Z, 5);
        Assert.True(wear.Remove(3));
        Assert.Equal(rig.DefaultPosition[torso].Z, wear.Overrides.PositionOf(torso).Z, 5);   // nothing left: the default
        Assert.Equal(0, wear.Objects);
    }

    [Fact]
    public void Two_objects_wearing_the_same_mesh_keep_its_overrides_until_the_last_of_them_goes()
    {
        var rig = Rig();
        int torso = rig.Find("mTorso");
        var wear = new RiggedWear(rig);
        wear.Add(1, Id(7), Overriding(0.3f));
        wear.Add(2, Id(7), Overriding(0.3f));
        Assert.Equal(2, wear.Objects);
        wear.Remove(1);
        Assert.Equal(0.3f, wear.Overrides.PositionOf(torso).Z, 5);
        wear.Remove(2);
        Assert.Equal(rig.DefaultPosition[torso].Z, wear.Overrides.PositionOf(torso).Z, 5);
        Assert.False(wear.Remove(2));   // already gone
    }

    [Fact]
    public void The_greatest_asset_ids_pelvis_offset_raises_the_avatar_and_goes_when_its_mesh_does()
    {
        var rig = Rig();
        var wear = new RiggedWear(rig);
        wear.Add(1, Id(2), Overriding(0.3f, pelvis: 0.05f));
        wear.Add(2, Id(9), Overriding(0.3f, pelvis: 0.08f));
        Assert.Equal(0.08f, wear.PelvisFixup, 5);
        wear.Remove(2);
        Assert.Equal(0.05f, wear.PelvisFixup, 5);
        wear.Remove(1);
        Assert.Equal(0f, wear.PelvisFixup, 5);
    }

    [Fact]
    public void A_mesh_without_a_full_set_of_alternate_matrices_changes_nothing()
    {
        // the official viewer ignores a mesh's overrides, and its pelvis offset, unless it lists one alternate matrix per joint name
        var rig = Rig();
        var wear = new RiggedWear(rig);
        var plain = Skin(["mTorso", "mChest"], [Matrix4x4.Identity, Matrix4x4.Identity], pelvis: 0.2f);
        Assert.False(wear.Add(1, Id(1), plain));
        Assert.Equal(0f, wear.PelvisFixup, 5);
        Assert.Equal(1, wear.Objects);   // still worn: it is only that it overrides nothing
        Assert.True(wear.Remove(1));
        Assert.Equal(0, wear.Objects);
    }

    // ---- the decoder keeps the full skin ----

    [Fact]
    public void The_decoder_keeps_the_full_skin_of_a_rigged_mesh_next_to_its_geometry()
    {
        var weights = SkinAssetBuilder.Join(SkinAssetBuilder.Vertex((1, 16384), (2, 49151)), SkinAssetBuilder.Vertex((3, 65535)));
        var skin = new SkinAssetBuilder.Skin(["mPelvis", "mTorso", "mChest", "mHead"], [SkinAssetBuilder.Mat(1), SkinAssetBuilder.Mat(2), SkinAssetBuilder.Mat(3), SkinAssetBuilder.Mat(4)]);
        var asset = SkinAssetBuilder.Asset(skin, (3, [new SkinAssetBuilder.Face(2, weights)]));
        var decoded = Viewer.Core.Protocol.LmvMesh.Decode(Guid.NewGuid(), asset);
        Assert.True(decoded.Ok, decoded.Refusal?.ToString());
        var data = decoded.Geometry!.Skin!.Data;
        Assert.NotNull(data);
        Assert.Equal(["mPelvis", "mTorso", "mChest", "mHead"], data!.Block!.JointNames);
        Assert.Equal(2, data.Lods[3]![0].Vertices);
    }
}
