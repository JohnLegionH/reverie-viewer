// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Picking: a ray against prims (their generated faces), avatars and the ground, with the surface data a touch sends.
/// Pure geometry on an in-memory world; every member is independent, so the class runs in parallel.
/// </summary>
public class ObjectPickerTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly RegionInfo Region = new("Here", 1000, 1000, 256, 256, false);
    private static WorldState NewWorld() { var w = new WorldState(new CoreLog(new ListSink(), "test")); w.BeginRegion(Sim, Region); return w; }
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);

    private static PrimData Box(uint local, Vector3 pos, Vector3 scale, Quaternion? rot = null, uint parent = 0) =>
        new(local, G(local), parent, 0, pos, rot ?? Quaternion.Identity, scale);

    private static ObjectState State(WorldState w, uint local) => w.Object(new ObjectId(w.Epoch, local))!;

    // ---------------- one prim ----------------

    [Fact]
    public void A_ray_down_onto_a_box_hits_its_top_face_at_the_top_with_the_surface_facing_up()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Box(1, new Vector3(10, 10, 20), new Vector3(2, 2, 2)));
        var hit = ObjectPicker.PickPrim(State(w, 1), new Vector3(10, 10, 20), Quaternion.Identity, new PickRay(new Vector3(10, 10, 30), new Vector3(0, 0, -1)));
        Assert.NotNull(hit);
        Assert.Equal(0, hit!.Face);   // the viewer's numbering: a box's top is face 0
        Assert.Equal(9f, hit.Distance, 3);
        Assert.Equal(21f, hit.Position.Z, 3);
        Assert.Equal(1f, hit.Normal.Z, 3);
        Assert.Equal(1f, hit.Normal.Length(), 3);
        Assert.Equal(1f, hit.Binormal.Length(), 3);
        Assert.Equal(0f, Vector3.Dot(hit.Normal, hit.Binormal), 3);
    }

    [Fact]
    public void The_hit_at_the_middle_of_a_face_has_surface_coordinates_in_the_middle()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Box(1, new Vector3(10, 10, 20), new Vector3(2, 2, 2)));
        var hit = ObjectPicker.PickPrim(State(w, 1), new Vector3(10, 10, 20), Quaternion.Identity, new PickRay(new Vector3(10, 10, 30), new Vector3(0, 0, -1)))!;
        Assert.Equal(0.5f, hit.St.X, 2);
        Assert.Equal(0.5f, hit.St.Y, 2);
        Assert.Equal(0.5f, hit.Uv.X, 2);
        Assert.Equal(0.5f, hit.Uv.Y, 2);
    }

    [Fact]
    public void Surface_coordinates_follow_the_hit_across_a_face()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Box(1, new Vector3(10, 10, 20), new Vector3(2, 2, 2)));
        var near = ObjectPicker.PickPrim(State(w, 1), new Vector3(10, 10, 20), Quaternion.Identity, new PickRay(new Vector3(9.5f, 10, 30), new Vector3(0, 0, -1)))!;
        var far = ObjectPicker.PickPrim(State(w, 1), new Vector3(10, 10, 20), Quaternion.Identity, new PickRay(new Vector3(10.5f, 10, 30), new Vector3(0, 0, -1)))!;
        Assert.True(Math.Abs(near.St.X - far.St.X) > 0.4f || Math.Abs(near.St.Y - far.St.Y) > 0.4f, "moving the ray 1 m across a 2 m face must move the surface coordinates by about half");
    }

    [Fact]
    public void A_ray_from_the_side_hits_a_side_face_with_a_sideways_normal()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Box(1, new Vector3(10, 10, 20), new Vector3(2, 2, 2)));
        var hit = ObjectPicker.PickPrim(State(w, 1), new Vector3(10, 10, 20), Quaternion.Identity, new PickRay(new Vector3(0, 10, 20), new Vector3(1, 0, 0)))!;
        Assert.InRange(hit.Face, 1, 4);
        Assert.Equal(-1f, hit.Normal.X, 3);
        Assert.Equal(9f, hit.Position.X, 3);
    }

    [Fact]
    public void A_ray_beside_the_box_misses()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Box(1, new Vector3(10, 10, 20), new Vector3(2, 2, 2)));
        Assert.Null(ObjectPicker.PickPrim(State(w, 1), new Vector3(10, 10, 20), Quaternion.Identity, new PickRay(new Vector3(14, 10, 30), new Vector3(0, 0, -1))));
    }

    [Fact]
    public void A_ray_pointing_away_misses()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Box(1, new Vector3(10, 10, 20), new Vector3(2, 2, 2)));
        Assert.Null(ObjectPicker.PickPrim(State(w, 1), new Vector3(10, 10, 20), Quaternion.Identity, new PickRay(new Vector3(10, 10, 30), new Vector3(0, 0, 1))));
    }

    [Fact]
    public void A_turned_prim_is_hit_where_it_actually_is()
    {
        var w = NewWorld();
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4);   // a 4 m by 0.5 m slab turned a quarter of a right angle
        w.ApplyObject(Sim, Box(1, new Vector3(10, 10, 20), new Vector3(4, 0.5f, 1), turn));
        // Along the turned long axis, 1.4 m from the centre: inside the turned slab, outside the unturned one's thin side.
        var inside = new Vector3(10 + 1.4f, 10 + 1.4f, 30);
        Assert.NotNull(ObjectPicker.PickPrim(State(w, 1), new Vector3(10, 10, 20), turn, new PickRay(inside, new Vector3(0, 0, -1))));
        var outside = new Vector3(10 + 1.4f, 10 - 1.4f, 30);
        Assert.Null(ObjectPicker.PickPrim(State(w, 1), new Vector3(10, 10, 20), turn, new PickRay(outside, new Vector3(0, 0, -1))));
    }

    // ---------------- the world ----------------

    [Fact]
    public void The_nearest_of_two_prims_wins()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Box(1, new Vector3(10, 10, 20), new Vector3(2, 2, 2)));
        w.ApplyObject(Sim, Box(2, new Vector3(10, 10, 25), new Vector3(2, 2, 2)));
        var r = ObjectPicker.Pick(w, new PickRay(new Vector3(10, 10, 40), new Vector3(0, 0, -1)), null);
        Assert.Equal(PickKind.Object, r.Kind);
        Assert.Equal(2u, r.Hit!.LocalId);
        Assert.Equal(14f, r.Distance, 3);
    }

    [Fact]
    public void A_child_prim_is_placed_in_its_roots_frame_and_reports_the_root()
    {
        var w = NewWorld();
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        w.ApplyObject(Sim, Box(1, new Vector3(50, 50, 20), new Vector3(1, 1, 1), turn));
        // 3 m along the child's own +x, which the quarter turn sends to the region's +y
        w.ApplyObject(Sim, Box(2, new Vector3(3, 0, 0), new Vector3(1, 1, 1), null, parent: 1));
        var r = ObjectPicker.Pick(w, new PickRay(new Vector3(50, 53, 30), new Vector3(0, 0, -1)), null);
        Assert.Equal(PickKind.Object, r.Kind);
        Assert.Equal(2u, r.Hit!.LocalId);
        Assert.Equal(1u, r.Hit.RootLocalId);
    }

    [Fact]
    public void The_world_pose_of_a_child_is_its_roots_pose_plus_its_own_offset_turned_with_the_root()
    {
        var w = NewWorld();
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        w.ApplyObject(Sim, Box(1, new Vector3(50, 50, 20), new Vector3(1, 1, 1), turn));
        w.ApplyObject(Sim, Box(2, new Vector3(3, 0, 0), new Vector3(1, 1, 1), null, parent: 1));
        Assert.True(ObjectPicker.WorldPose(w, State(w, 2), out var pos, out var rot));
        Assert.Equal(50f, pos.X, 3);
        Assert.Equal(53f, pos.Y, 3);
        Assert.Equal(20f, pos.Z, 3);
        Assert.Equal(turn.Z, rot.Z, 3);
    }

    [Fact]
    public void A_prim_with_an_unknown_parent_has_no_world_pose()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Box(2, new Vector3(3, 0, 0), new Vector3(1, 1, 1), null, parent: 99));
        Assert.False(ObjectPicker.WorldPose(w, State(w, 2), out _, out _));
    }

    [Fact]
    public void An_attachment_is_not_picked_as_part_of_the_world()
    {
        var w = NewWorld();
        w.ApplyAvatar(Sim, new AvatarData(7, G(900), "Someone", new Vector3(50, 50, 25), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
        w.ApplyObject(Sim, new PrimData(8, G(8), 7, 2, new Vector3(0, 0, 0), Quaternion.Identity, new Vector3(5, 5, 5)), eventIsAttachment: true);
        var r = ObjectPicker.Pick(w, new PickRay(new Vector3(50, 50, 60), new Vector3(0, 0, -1)), null, ignoreAgent: G(900));
        Assert.NotEqual(PickKind.Object, r.Kind);
    }

    [Fact]
    public void An_avatar_is_picked_when_nearer_than_what_is_behind_it()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Box(1, new Vector3(50, 50, 10), new Vector3(10, 10, 1)));
        var id = G(900);
        w.ApplyAvatar(Sim, new AvatarData(7, id, "Someone", new Vector3(50, 50, 12), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
        var r = ObjectPicker.Pick(w, new PickRay(new Vector3(50, 50, 30), new Vector3(0, 0, -1)), null);
        Assert.Equal(PickKind.Avatar, r.Kind);
        Assert.Equal(id, r.Avatar!.AgentId);
    }

    [Fact]
    public void The_own_avatar_is_ignored_when_asked()
    {
        var w = NewWorld();
        var me = G(901);
        w.ApplyAvatar(Sim, new AvatarData(7, me, "Me", new Vector3(50, 50, 12), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
        var r = ObjectPicker.Pick(w, new PickRay(new Vector3(50, 50, 30), new Vector3(0, 0, -1)), null, ignoreAgent: me);
        Assert.Equal(PickKind.None, r.Kind);
    }

    [Fact]
    public void Land_is_found_where_the_ray_meets_the_ground()
    {
        var w = NewWorld();
        var r = ObjectPicker.Pick(w, new PickRay(new Vector3(100, 100, 40), new Vector3(0, 1, -1)), (x, y) => 20f);
        Assert.Equal(PickKind.Land, r.Kind);
        Assert.Equal(20f, r.Point.Z, 1);
        Assert.Equal(120f, r.Point.Y, 0);
    }

    [Fact]
    public void A_prim_above_the_ground_beats_the_ground()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Box(1, new Vector3(100, 100, 30), new Vector3(2, 2, 2)));
        var r = ObjectPicker.Pick(w, new PickRay(new Vector3(100, 100, 50), new Vector3(0, 0, -1)), (x, y) => 20f);
        Assert.Equal(PickKind.Object, r.Kind);
    }

    [Fact]
    public void Nothing_is_picked_when_the_ray_goes_up_into_the_sky()
    {
        var w = NewWorld();
        var r = ObjectPicker.Pick(w, new PickRay(new Vector3(100, 100, 40), new Vector3(0, 1, 1)), (x, y) => 20f);
        Assert.Equal(PickKind.None, r.Kind);
    }
}
