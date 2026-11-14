// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The engine-free placement maths: node lookup, world matrices, rigid attachments, joint position overrides and the
/// palette of a rigged mesh. The skeleton here is a small one written for the tests. Each expected value is worked by hand
/// from the rule named in the test (right-handed rotations, the row-vector convention, and the official viewer's behaviour
/// as <see cref="JointOverrides"/> and <see cref="AttachmentPlacement"/> describe it). Pure, runs in parallel.
/// </summary>
public class AttachmentPlacementTests
{
    // mPelvis (0,0,1) > mTorso (0,0,.1) > mChest (0,0,.2) > mWingsRoot (extended); mPelvis > mHipLeft (.1,0,-.05) > mKneeLeft (0,0,-.5); one collision volume.
    private const string SkeletonXml = """
        <linden_skeleton version="2.0">
          <bone name="mPelvis" pos="0 0 1" rot="0 0 0" scale="1 1 1" support="base" aliases="hip avatar_mPelvis">
            <bone name="mTorso" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base" aliases="abdomen">
              <bone name="mChest" pos="0 0 0.2" rot="0 0 0" scale="1 1 1" support="base" aliases="chest">
                <bone name="mWingsRoot" pos="0 -0.1 0" rot="0 0 0" scale="1 1 1" support="extended"/>
              </bone>
            </bone>
            <bone name="mHipLeft" pos="0.1 0 -0.05" rot="0 0 0" scale="1 1 1" support="base">
              <bone name="mKneeLeft" pos="0 0 -0.5" rot="0 0 0" scale="1 1 1" support="base"/>
            </bone>
            <collision_volume name="PELVIS" pos="0 0 -0.1" rot="0 0 0" scale="0.2 0.3 0.4" support="base"/>
          </bone>
        </linden_skeleton>
        """;

    private const string DefinitionXml = """
        <linden_avatar>
          <attachment_point id="1" group="6" name="Chest" joint="mChest" position="0.15 0 -0.1" rotation="0 90 90"/>
          <attachment_point id="5" group="0" name="Left Hand" joint="mKneeLeft" position="0 0 0" rotation="0 0 0"/>
          <attachment_point id="40" group="0" name="Avatar Center" joint="mRoot" position="0 0 0.5" rotation="0 0 0"/>
          <attachment_point id="35" group="0" name="Center" joint="mScreen" position="0 0 0" rotation="0 0 0"/>
        </linden_avatar>
        """;

    private static AvatarRig Rig() => new(CharacterSkeleton.Parse(Encoding.UTF8.GetBytes(SkeletonXml), Encoding.UTF8.GetBytes(DefinitionXml)));

    private static Matrix4x4[] Rest(AvatarRig rig)
    {
        var w = new Matrix4x4[rig.NodeCount];
        JointWorlds.Rest(rig, w);
        return w;
    }

    private static void Near(Vector3 expected, Vector3 actual, float tol = 1e-4f) =>
        Assert.True((expected - actual).Length() <= tol, $"expected {expected}, got {actual}");

    // ---- which point an object names ----

    [Fact]
    public void The_point_an_object_names_is_found_with_the_add_flag_taken_off_and_an_unknown_one_falls_back_to_the_chest()
    {
        // the simulator sets bit 0x80 on the point id of an object worn in addition to what is on the point already;
        // the official viewer clears it, and takes the chest for an id the skeleton does not have
        var rig = Rig();
        int chest = rig.PointNode(1), hand = rig.PointNode(5);
        Assert.Equal(hand, AttachmentPlacement.TargetNode(rig, 5));
        Assert.Equal(hand, AttachmentPlacement.TargetNode(rig, 5 | 0x80));
        Assert.Equal(chest, AttachmentPlacement.TargetNode(rig, 54));
        Assert.Equal(chest, AttachmentPlacement.TargetNode(rig, 0));
        Assert.Equal(chest, AttachmentPlacement.TargetNode(rig, 200));
        Assert.True(chest >= 0 && hand >= 0 && chest != hand);
    }

    [Fact]
    public void A_skeleton_with_no_chest_has_no_fallback()
    {
        const string definition = "<linden_avatar><attachment_point id=\"5\" group=\"0\" name=\"Left Hand\" joint=\"mKneeLeft\" position=\"0 0 0\" rotation=\"0 0 0\"/></linden_avatar>";
        var skeleton = CharacterSkeleton.Parse(Encoding.UTF8.GetBytes(SkeletonXml), Encoding.UTF8.GetBytes(definition));
        Assert.Equal(-1, AttachmentPlacement.TargetNode(new AvatarRig(skeleton), 54));
    }

    // ---- names ----

    [Fact]
    public void Names_resolve_as_the_official_viewer_resolves_them()
    {
        var rig = Rig();
        Assert.Equal(rig.Skeleton.IndexOf("mChest"), rig.Find("mChest"));
        Assert.Equal(rig.Find("mChest"), rig.Find("chest"));             // an alias the skeleton file lists
        Assert.Equal(rig.Find("mPelvis"), rig.Find("avatar_mPelvis"));
        Assert.Equal(rig.PointNode(5), rig.Find("Left Hand"));            // an attachment point is a node of its own
        Assert.Equal(rig.PointNode(5), rig.Find("Left_Hand"));            // its name with underscores for spaces
        Assert.True(rig.Find("PELVIS") >= 0 && !rig.IsAttachmentPoint(rig.Find("PELVIS")));   // a collision volume
        Assert.Equal(AvatarRig.Root, rig.Find("mRoot"));
        Assert.Equal(AvatarRig.Unknown, rig.Find("mNoSuchJoint"));
        Assert.Equal(AvatarRig.Unknown, rig.Find("mchest"));             // matching is case-sensitive
    }

    [Fact]
    public void An_unknown_name_in_a_skin_binding_goes_to_the_pelvis_but_the_root_stays_the_root()
    {
        var rig = Rig();
        Assert.Equal(rig.Find("mPelvis"), rig.FindForSkinning("mNoSuchJoint"));
        Assert.Equal(AvatarRig.Root, rig.FindForSkinning("mRoot"));
    }

    [Fact]
    public void Attachment_points_are_nodes_under_their_joint_and_hud_points_hang_off_the_screen()
    {
        var rig = Rig();
        Assert.Equal(rig.Find("mChest"), rig.Parent(rig.PointNode(1)));
        Assert.Equal(-1, rig.Parent(rig.PointNode(40)));    // under the avatar root
        Assert.True(rig.IsScreen[rig.PointNode(35)]);
        Assert.False(rig.IsScreen[rig.PointNode(1)]);
        Assert.Equal(-1, rig.PointNode(2));                 // an id the skeleton does not define
        Assert.Equal(-1, rig.PointNode(500));
    }

    // ---- world matrices ----

    [Fact]
    public void The_rest_pose_stacks_the_default_offsets_from_the_root()
    {
        var rig = Rig();
        var w = Rest(rig);
        Near(new Vector3(0, 0, 1.0f), Vector3.Transform(Vector3.Zero, w[rig.Find("mPelvis")]));
        Near(new Vector3(0, 0, 1.3f), Vector3.Transform(Vector3.Zero, w[rig.Find("mChest")]));
        Near(new Vector3(0.1f, 0, 0.95f), Vector3.Transform(Vector3.Zero, w[rig.Find("mHipLeft")]));
        Near(new Vector3(0.1f, 0, 0.45f), Vector3.Transform(Vector3.Zero, w[rig.Find("mKneeLeft")]));
        Near(new Vector3(0, -0.1f, 1.3f), Vector3.Transform(Vector3.Zero, w[rig.Find("mWingsRoot")]));
        // a collision volume has a scale of its own: a point at (1,1,1) in its frame is scaled by (.2,.3,.4) before the offset
        Near(new Vector3(0.2f, 0.3f, 0.9f + 0.4f), Vector3.Transform(Vector3.One, w[rig.Find("PELVIS")]));
    }

    [Fact]
    public void Turning_a_joint_carries_its_children()
    {
        var rig = Rig();
        var rot = rig.DefaultRotation.ToArray();
        rot[rig.Find("mTorso")] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);   // 90 degrees about z: (1,0,0) becomes (0,1,0)
        var w = new Matrix4x4[rig.NodeCount];
        JointWorlds.Compute(rig, rig.DefaultPosition, rot, w);
        Near(new Vector3(0, 0, 1.3f), Vector3.Transform(Vector3.Zero, w[rig.Find("mChest")]));            // on the axis: stays
        Near(new Vector3(0.1f, 0, 1.3f), Vector3.Transform(Vector3.Zero, w[rig.Find("mWingsRoot")]));   // (0,-.1,0) turned to (.1,0,0)
    }

    // ---- rigid attachments ----

    [Fact]
    public void A_rigid_attachment_sits_in_its_points_frame_and_the_point_in_its_joints()
    {
        var rig = Rig();
        var w = Rest(rig);
        // Chest point: offset (.15,0,-.1) and rotation Euler(0,90,90) (x, then y, then z) on mChest, whose origin is at (0,0,1.3).
        var m = AttachmentPlacement.Rigid(rig, 1, w, Vector3.Zero, Quaternion.Identity)!.Value;
        Near(new Vector3(0.15f, 0, 1.2f), Vector3.Transform(Vector3.Zero, m));
        // the object's own +z (0,0,1): turned by y 90 to (1,0,0), then by z 90 to (0,1,0), then moved to the point
        Near(new Vector3(0.15f, 1, 1.2f), Vector3.Transform(new Vector3(0, 0, 1), m));
        // its +x: y 90 sends (1,0,0) to (0,0,-1), which z 90 leaves alone
        Near(new Vector3(0.15f, 0, 0.2f), Vector3.Transform(new Vector3(1, 0, 0), m));
    }

    [Fact]
    public void The_objects_own_offset_and_rotation_are_applied_before_the_points()
    {
        var rig = Rig();
        var w = Rest(rig);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var m = AttachmentPlacement.Rigid(rig, 1, w, new Vector3(0, 0.5f, 0), turn)!.Value;
        // object point (1,0,0): its rotation z 90 makes (0,1,0), its offset adds (0,.5,0) = (0,1.5,0);
        // the point's rotation then sends (0,1.5,0) to (-1.5,0,0) (y 90 leaves y; z 90 sends y to -x); + (.15,0,-.1) + (0,0,1.3)
        Near(new Vector3(-1.35f, 0, 1.2f), Vector3.Transform(new Vector3(1, 0, 0), m));
    }

    [Fact]
    public void Several_attachments_on_one_point_share_it_and_follow_the_joint_together()
    {
        var rig = Rig();
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(0, 0, 0.3f);
        var rot = rig.DefaultRotation.ToArray();
        var w = new Matrix4x4[rig.NodeCount];
        JointWorlds.Compute(rig, rig.DefaultPosition, rot, w);
        var a0 = Vector3.Transform(a, AttachmentPlacement.Rigid(rig, 1, w, a, Quaternion.Identity)!.Value);
        var b0 = Vector3.Transform(a, AttachmentPlacement.Rigid(rig, 1, w, b, Quaternion.Identity)!.Value);
        Assert.NotEqual(a0, b0);   // different offsets on the same point land apart
        rot[rig.Find("mTorso")] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        JointWorlds.Compute(rig, rig.DefaultPosition, rot, w);
        var a1 = Vector3.Transform(a, AttachmentPlacement.Rigid(rig, 1, w, a, Quaternion.Identity)!.Value);
        var b1 = Vector3.Transform(a, AttachmentPlacement.Rigid(rig, 1, w, b, Quaternion.Identity)!.Value);
        Near(new Vector3(0, 0.15f, 1.2f), a1);   // worked: point at (.15,0,.1) in the torso frame, turned to (0,.15,.1), + (0,0,1.1)
        Assert.Equal((a0 - b0).Length(), (a1 - b1).Length(), 4);   // the pair moved rigidly together
    }

    [Fact]
    public void Points_on_the_root_follow_the_root_and_hud_and_unknown_points_are_not_placed()
    {
        var rig = Rig();
        var w = Rest(rig);
        Near(new Vector3(0, 0, 0.5f), Vector3.Transform(Vector3.Zero, AttachmentPlacement.Rigid(rig, 40, w, Vector3.Zero, Quaternion.Identity)!.Value));
        Assert.Null(AttachmentPlacement.Rigid(rig, 35, w, Vector3.Zero, Quaternion.Identity));
        Assert.Null(AttachmentPlacement.Rigid(rig, 99, w, Vector3.Zero, Quaternion.Identity));
    }

    // ---- joint position overrides ----

    private static MeshSkinBlock Skin(string[] names, Vector3?[] overrides, float pelvis = 0f, bool lockScale = false, bool withAlt = true)
    {
        var inv = names.Select(_ => Matrix4x4.Identity).ToArray();
        var alt = withAlt ? names.Select((_, i) => overrides[i] is { } p ? Matrix4x4.CreateTranslation(p) : Matrix4x4.Identity).ToArray() : [];
        return new MeshSkinBlock(names, inv, Matrix4x4.Identity, alt, pelvis, lockScale, false);
    }

    private static readonly Guid Low = new("00000001-0000-0000-0000-000000000000");
    private static readonly Guid High = new("00000100-0000-0000-0000-000000000000");   // greater in the textual order; its in-memory bytes (little-endian first group) compare smaller

    [Fact]
    public void A_joint_override_is_the_translation_of_its_alternate_matrix_and_replaces_the_default_position()
    {
        var rig = Rig();
        var o = new JointOverrides(rig);
        int chest = rig.Find("mChest");
        Assert.True(o.Add(Low, Skin(["mChest"], [new Vector3(0, 0, 0.3f)])));
        Assert.Equal(new Vector3(0, 0, 0.3f), o.PositionOf(chest));
        var pos = new Vector3[rig.NodeCount];
        o.Apply(pos);
        Assert.Equal(new Vector3(0, 0, 0.3f), pos[chest]);
        Assert.Equal(rig.DefaultPosition[rig.Find("mTorso")], pos[rig.Find("mTorso")]);
        var w = new Matrix4x4[rig.NodeCount];
        JointWorlds.Compute(rig, pos, rig.DefaultRotation, w);
        Near(new Vector3(0, 0, 1.4f), Vector3.Transform(Vector3.Zero, w[chest]));   // chest 0.1 higher
        var m = AttachmentPlacement.Rigid(rig, 1, w, Vector3.Zero, Quaternion.Identity)!.Value;
        Near(new Vector3(0.15f, 0, 1.3f), Vector3.Transform(Vector3.Zero, m));       // an attachment on it moved with it
    }

    [Fact]
    public void An_override_within_a_tenth_of_a_millimetre_of_the_default_does_not_count()
    {
        var rig = Rig();
        var o = new JointOverrides(rig);
        Assert.False(o.Add(Low, Skin(["mChest"], [new Vector3(0, 0, 0.2f + 0.00005f)])));
        Assert.Null(o.WinnerOf(rig.Find("mChest")));
        Assert.True(o.Add(High, Skin(["mChest"], [new Vector3(0, 0, 0.2f + 0.0002f)])));
        Assert.Equal(High, o.WinnerOf(rig.Find("mChest")));
    }

    [Fact]
    public void With_several_meshes_on_one_joint_the_greatest_asset_id_wins_in_the_textual_order()
    {
        var rig = Rig();
        int chest = rig.Find("mChest");
        var o = new JointOverrides(rig);
        o.Add(High, Skin(["mChest"], [new Vector3(0, 0, 0.5f)]));
        o.Add(Low, Skin(["mChest"], [new Vector3(0, 0, 0.4f)]));
        Assert.Equal(High, o.WinnerOf(chest));
        Assert.Equal(0.5f, o.PositionOf(chest).Z);
        // the order they were worn in does not matter
        var o2 = new JointOverrides(rig);
        o2.Add(Low, Skin(["mChest"], [new Vector3(0, 0, 0.4f)]));
        o2.Add(High, Skin(["mChest"], [new Vector3(0, 0, 0.5f)]));
        Assert.Equal(0.5f, o2.PositionOf(chest).Z);
        // comparing the in-memory bytes (as ToByteArray gives them) would pick the other one; the rule is the textual order
        Assert.True(High.ToByteArray().AsSpan().SequenceCompareTo(Low.ToByteArray()) < 0);
    }

    [Fact]
    public void Removing_the_winner_hands_the_joint_to_the_next_and_removing_all_restores_the_default()
    {
        var rig = Rig();
        int chest = rig.Find("mChest");
        var o = new JointOverrides(rig);
        o.Add(Low, Skin(["mChest"], [new Vector3(0, 0, 0.4f)]));
        o.Add(High, Skin(["mChest"], [new Vector3(0, 0, 0.5f)]));
        Assert.True(o.Remove(High));
        Assert.Equal(0.4f, o.PositionOf(chest).Z);
        Assert.True(o.Remove(Low));
        Assert.Equal(rig.DefaultPosition[chest], o.PositionOf(chest));
        Assert.False(o.Remove(Low));
        Assert.Equal(0, o.MeshCount);
    }

    [Fact]
    public void A_mesh_taken_twice_counts_once_and_a_zero_id_is_ignored()
    {
        var rig = Rig();
        var o = new JointOverrides(rig);
        Assert.True(o.Add(Low, Skin(["mChest"], [new Vector3(0, 0, 0.4f)])));
        Assert.False(o.Add(Low, Skin(["mChest"], [new Vector3(0, 0, 0.9f)])));
        Assert.Equal(0.4f, o.PositionOf(rig.Find("mChest")).Z);
        Assert.False(o.Add(Guid.Empty, Skin(["mChest"], [new Vector3(0, 0, 0.9f)])));
    }

    [Fact]
    public void Without_one_alternate_matrix_per_joint_a_mesh_overrides_nothing_and_its_pelvis_offset_is_ignored()
    {
        var rig = Rig();
        var o = new JointOverrides(rig);
        Assert.False(o.Add(Low, Skin(["mChest"], [new Vector3(0, 0, 0.4f)], pelvis: 0.2f, withAlt: false)));
        var shortAlt = new MeshSkinBlock(["mChest", "mTorso"], [Matrix4x4.Identity, Matrix4x4.Identity], Matrix4x4.Identity, [Matrix4x4.CreateTranslation(0, 0, 0.4f)], 0.2f, false, false);
        Assert.False(o.Add(High, shortAlt));
        Assert.Equal(0f, o.PelvisFixup);
        Assert.Equal(rig.DefaultPosition[rig.Find("mChest")], o.PositionOf(rig.Find("mChest")));
    }

    [Fact]
    public void Unknown_joint_names_are_skipped_for_overrides_and_the_rest_still_apply()
    {
        var rig = Rig();
        var o = new JointOverrides(rig);
        Assert.True(o.Add(Low, Skin(["mNoSuchJoint", "chest", "mRoot"], [new Vector3(9, 9, 9), new Vector3(0, 0, 0.4f), new Vector3(1, 1, 1)])));
        Assert.Equal(0.4f, o.PositionOf(rig.Find("mChest")).Z);   // found through its alias
        var pos = new Vector3[rig.NodeCount];
        o.Apply(pos);
        Assert.DoesNotContain(new Vector3(9, 9, 9), pos);
        Assert.DoesNotContain(new Vector3(1, 1, 1), pos);
    }

    [Fact]
    public void The_pelvis_offset_raises_the_avatar_and_the_greatest_id_wins_even_without_joint_overrides()
    {
        var rig = Rig();
        var o = new JointOverrides(rig);
        // alternate matrices equal to the defaults: no joint override, but the mesh still counts for the pelvis offset
        Assert.True(o.Add(Low, Skin(["mChest"], [new Vector3(0, 0, 0.2f)], pelvis: 0.1f)));
        Assert.Equal(0.1f, o.PelvisFixup);
        o.Add(High, Skin(["mChest"], [new Vector3(0, 0, 0.2f)], pelvis: -0.3f));
        Assert.Equal(-0.3f, o.PelvisFixup);
        o.Remove(High);
        Assert.Equal(0.1f, o.PelvisFixup);
        o.Remove(Low);
        Assert.Equal(0f, o.PelvisFixup);
        // a zero offset adds nothing
        o.Add(High, Skin(["mChest"], [new Vector3(0, 0, 0.2f)], pelvis: 0f));
        Assert.Equal(0f, o.PelvisFixup);
    }

    [Fact]
    public void The_lock_scale_flag_pins_the_joints_a_mesh_overrides_while_it_is_worn()
    {
        var rig = Rig();
        int chest = rig.Find("mChest"), torso = rig.Find("mTorso");
        var o = new JointOverrides(rig);
        o.Add(Low, Skin(["mChest", "mTorso"], [new Vector3(0, 0, 0.4f), new Vector3(0, 0, 0.1f)], lockScale: true));
        Assert.True(o.ScaleLocked(chest));
        Assert.False(o.ScaleLocked(torso));   // only joints that are actually overridden
        o.Remove(Low);
        Assert.False(o.ScaleLocked(chest));
    }

    // ---- the palette and skinning ----

    [Fact]
    public void At_the_bind_pose_every_joint_matrix_is_the_identity_and_the_bind_shape_is_all_that_moves_a_vertex()
    {
        var rig = Rig();
        var w = Rest(rig);
        string[] names = ["mPelvis", "mChest", "mKneeLeft"];
        var inv = names.Select(n => { Matrix4x4.Invert(w[rig.Find(n)], out var i); return i; }).ToArray();
        var skin = new MeshSkinBlock(names, inv, Matrix4x4.CreateScale(2), [], 0f, false, false);
        var nodes = new int[3];
        Assert.Equal(3, AttachmentPlacement.ResolveJoints(rig, skin, nodes));
        var palette = new Matrix4x4[3];
        AttachmentPlacement.Palette(skin, nodes, w, palette);
        foreach (var m in palette) Assert.True(Near(Matrix4x4.Identity, m), m.ToString());
        var p = AttachmentPlacement.SkinPosition(new Vector3(1, 2, 3), skin.BindShape, palette, new byte[] { 0, 1, 2, 0 }, new[] { 0.5f, 0.25f, 0.25f, 0f });
        Near(new Vector3(2, 4, 6), p);
    }

    [Fact]
    public void A_vertex_follows_the_weighted_sum_of_its_joints_matrices_after_the_bind_shape()
    {
        var rig = Rig();
        var skin = new MeshSkinBlock(["mPelvis", "mChest"], [Matrix4x4.Identity, Matrix4x4.Identity], Matrix4x4.CreateScale(2), [], 0f, false, false);
        var world = new Matrix4x4[rig.NodeCount];
        world[rig.Find("mPelvis")] = Matrix4x4.CreateTranslation(1, 0, 0);
        world[rig.Find("mChest")] = Matrix4x4.CreateTranslation(0, 2, 0);
        var nodes = new int[2];
        AttachmentPlacement.ResolveJoints(rig, skin, nodes);
        var palette = new Matrix4x4[2];
        AttachmentPlacement.Palette(skin, nodes, world, palette);
        // (1,1,1) scaled to (2,2,2); then 0.25 of (+1,0,0) and 0.75 of (0,+2,0): (2,2,2) + (.25,1.5,0)
        Near(new Vector3(2.25f, 3.5f, 2), AttachmentPlacement.SkinPosition(Vector3.One, skin.BindShape, palette, new byte[] { 0, 1, 0, 0 }, new[] { 0.25f, 0.75f, 0f, 0f }));
    }

    [Fact]
    public void A_binding_to_an_unknown_joint_follows_the_pelvis_and_one_to_the_root_follows_nothing()
    {
        var rig = Rig();
        var skin = new MeshSkinBlock(["mNoSuchJoint", "mRoot"], [Matrix4x4.Identity, Matrix4x4.Identity], Matrix4x4.Identity, [], 0f, false, false);
        var world = Rest(rig);
        var nodes = new int[2];
        AttachmentPlacement.ResolveJoints(rig, skin, nodes);
        Assert.Equal(rig.Find("mPelvis"), nodes[0]);
        Assert.Equal(AvatarRig.Root, nodes[1]);
        var palette = new Matrix4x4[2];
        AttachmentPlacement.Palette(skin, nodes, world, palette);
        Near(new Vector3(0, 0, 1), Vector3.Transform(Vector3.Zero, palette[0]));   // the pelvis' world position
        Assert.Equal(Matrix4x4.Identity, palette[1]);
    }

    [Fact]
    public void A_mesh_that_overrides_a_joint_moves_its_own_skin_by_the_same_amount()
    {
        // The mesh binds fully to mChest at the default rest pose and asks for the chest 0.1 higher: its vertex rises 0.1.
        var rig = Rig();
        var rest = Rest(rig);
        Matrix4x4.Invert(rest[rig.Find("mChest")], out var inv);
        var skin = new MeshSkinBlock(["mChest"], [inv], Matrix4x4.Identity, [Matrix4x4.CreateTranslation(0, 0, 0.3f)], 0f, false, false);
        var o = new JointOverrides(rig);
        Assert.True(o.Add(Low, skin));
        var pos = new Vector3[rig.NodeCount];
        o.Apply(pos);
        var w = new Matrix4x4[rig.NodeCount];
        JointWorlds.Compute(rig, pos, rig.DefaultRotation, w);
        var nodes = new int[1];
        AttachmentPlacement.ResolveJoints(rig, skin, nodes);
        var palette = new Matrix4x4[1];
        AttachmentPlacement.Palette(skin, nodes, w, palette);
        Near(new Vector3(0.5f, 0.5f, 1.6f), AttachmentPlacement.SkinPosition(new Vector3(0.5f, 0.5f, 1.5f), skin.BindShape, palette, new byte[] { 0, 0, 0, 0 }, new[] { 1f, 0, 0, 0 }));
    }

    [Fact]
    public void Reading_a_real_asset_and_placing_it_agree()
    {
        // The reader's output feeds the placement maths directly: names, matrices and normalised weights from an asset built by the test encoder.
        var rig = Rig();
        var inv = new[] { Matrix4x4.Identity, Matrix4x4.Identity };
        var asset = SkinAssetBuilder.Asset(new SkinAssetBuilder.Skin(["mPelvis", "mChest"], inv, Alt: [Matrix4x4.CreateTranslation(0, 0, 1), Matrix4x4.CreateTranslation(0, 0, 0.5f)], Pelvis: 0.05f),
            (3, [new SkinAssetBuilder.Face(1, SkinAssetBuilder.Vertex((0, 16384), (1, 49151)))]));
        var read = MeshSkinReader.Read(asset);
        var o = new JointOverrides(rig);
        Assert.True(o.Add(Low, read.Block!));
        Assert.Equal(0.5f, o.PositionOf(rig.Find("mChest")).Z);
        Assert.Equal(0.05f, o.PelvisFixup);
        Assert.Null(o.WinnerOf(rig.Find("mPelvis")));   // (0,0,1) is the pelvis' own default: not an override
    }

    private static bool Near(Matrix4x4 a, Matrix4x4 b, float tol = 1e-4f)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                if (MathF.Abs(Get(a, r, c) - Get(b, r, c)) > tol) return false;
        return true;
        static float Get(Matrix4x4 m, int r, int c) => r switch
        {
            0 => c switch { 0 => m.M11, 1 => m.M12, 2 => m.M13, _ => m.M14 },
            1 => c switch { 0 => m.M21, 1 => m.M22, 2 => m.M23, _ => m.M24 },
            2 => c switch { 0 => m.M31, 1 => m.M32, 2 => m.M33, _ => m.M34 },
            _ => c switch { 0 => m.M41, 1 => m.M42, 2 => m.M43, _ => m.M44 },
        };
    }
}


/// <summary>The placement code on the real character folder (local input, skipped without NV_CHARACTER_DIR).</summary>
public class RigOnTheRealSkeletonTests
{
    private static AvatarRig Real()
    {
        string dir = CharacterFactAttribute.Folder!;
        return new AvatarRig(CharacterSkeleton.Parse(File.ReadAllBytes(Path.Combine(dir, "avatar_skeleton.xml")), File.ReadAllBytes(Path.Combine(dir, "avatar_lad.xml"))));
    }

    [Fact]
    public void Euler_rotations_turn_about_x_first_then_y_then_z()
    {
        // The skeleton file's order (llquaternion.cpp mayaQ XYZ): (0,1,0) turned 90 about x is (0,0,1), which a turn about z leaves alone.
        var q = CharacterSkeleton.Euler(new Vector3(90, 0, 90));
        var v = Vector3.Transform(new Vector3(0, 1, 0), q);
        Assert.True((v - new Vector3(0, 0, 1)).Length() < 1e-5f, v.ToString());
        // and (0,0,1) turned 90 about y is (1,0,0), which 90 about z makes (0,1,0)
        v = Vector3.Transform(new Vector3(0, 0, 1), CharacterSkeleton.Euler(new Vector3(0, 90, 90)));
        Assert.True((v - new Vector3(0, 1, 0)).Length() < 1e-5f, v.ToString());
    }

    [CharacterFact]
    public void The_skeleton_holds_every_joint_and_attachment_point_the_definition_names()
    {
        var rig = Real();
        var sk = rig.Skeleton;
        Assert.Equal(133, sk.BoneCount);
        Assert.Equal(26, sk.CollisionVolumeCount);
        Assert.Equal(107, sk.Joints.Count(j => !j.Base));                  // every extended (Bento) joint
        Assert.Equal(159, rig.JointCount);
        Assert.Equal(55, sk.AttachmentPoints.Count);
        for (int id = 1; id <= 55; id++) Assert.True(rig.PointNode(id) >= 0, $"attachment point {id}");
        Assert.All(Enumerable.Range(31, 8), id => Assert.True(rig.IsScreen[rig.PointNode(id)], $"HUD point {id}"));
        Assert.Equal(sk.Joints.Count + 55, rig.NodeCount);
        // 26 fitted-mesh collision volumes, by their names
        string[] cv = ["PELVIS", "BUTT", "BELLY", "LEFT_HANDLE", "RIGHT_HANDLE", "LOWER_BACK", "CHEST", "LEFT_PEC", "RIGHT_PEC", "UPPER_BACK", "NECK", "HEAD",
            "L_CLAVICLE", "L_UPPER_ARM", "L_LOWER_ARM", "L_HAND", "R_CLAVICLE", "R_UPPER_ARM", "R_LOWER_ARM", "R_HAND", "R_UPPER_LEG", "R_LOWER_LEG", "R_FOOT", "L_UPPER_LEG", "L_LOWER_LEG", "L_FOOT"];
        Assert.All(cv, n => Assert.True(rig.Find(n) >= 0 && sk.Joints[rig.Find(n)].Kind == JointKind.CollisionVolume, n));
        // the Bento families
        foreach (var n in new[] { "mSpine1", "mSpine4", "mFaceRoot", "mFaceJaw", "mFaceTongueTip", "mHandThumb3Left", "mHandPinky3Right", "mWing4FanLeft", "mTail6", "mGroin", "mHindLimb4Right", "mSkull", "mEyeLeft", "mToeRight", "mFootLeft" })
            Assert.True(rig.Find(n) >= 0, n);
    }

    [CharacterFact]
    public void Every_joint_the_definitions_shape_parameters_name_is_in_the_skeleton()
    {
        var rig = Real();
        var lad = new System.Xml.XmlDocument { XmlResolver = null };
        lad.Load(Path.Combine(CharacterFactAttribute.Folder!, "avatar_lad.xml"));
        var named = lad.SelectNodes("//param_skeleton/bone")!.Cast<System.Xml.XmlElement>().Select(e => e.GetAttribute("name")).Distinct().ToList();
        Assert.True(named.Count >= 100, $"{named.Count} joints named by shape parameters");
        Assert.All(named, n => Assert.True(rig.Find(n) >= 0, "missing: " + n));
    }

    [CharacterFact]
    public void Aliases_and_attachment_point_names_resolve_and_unknown_names_fall_back_to_the_pelvis()
    {
        var rig = Real();
        Assert.Equal(rig.Find("mChest"), rig.Find("avatar_mChest"));
        Assert.Equal(rig.Find("mChest"), rig.Find("chest"));
        Assert.Equal(rig.Find("mHipRight"), rig.Find("rThigh"));
        Assert.Equal(rig.PointNode(5), rig.Find("Left Hand"));
        Assert.Equal(rig.PointNode(5), rig.Find("Left_Hand"));
        Assert.Equal(rig.Find("mPelvis"), rig.FindForSkinning("rThighX"));
        Assert.Equal(AvatarRig.Root, rig.Find("mRoot"));
    }

    [CharacterFact]
    public void The_rest_world_of_the_joints_equals_the_skeletons_own_default_world()
    {
        var rig = Real();
        var w = new Matrix4x4[rig.NodeCount];
        JointWorlds.Rest(rig, w);
        for (int i = 0; i < rig.JointCount; i++)
        {
            var d = rig.Skeleton.DefaultWorld[i];
            Assert.True(Vector3.Distance(w[i].Translation, d.Translation) < 1e-4f && MathF.Abs(w[i].M11 - d.M11) < 1e-4f && MathF.Abs(w[i].M23 - d.M23) < 1e-4f, rig.NodeName(i));
        }
        // the pelvis stands above the root, the head above the pelvis
        Assert.InRange(w[rig.Find("mPelvis")].Translation.Z, 0.9f, 1.2f);
        Assert.True(w[rig.Find("mHead")].Translation.Z > w[rig.Find("mPelvis")].Translation.Z + 0.5f);
    }
}
