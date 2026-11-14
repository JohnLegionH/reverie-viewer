// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Interaction;
using Viewer.Core.Meshing;
using Viewer.Core.Protocol;
using Xunit;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Viewer.Core.Tests;

/// <summary>
/// The edit tools without an engine or a grid: each message's contents against the official layout, the limits and clamps, the grid,
/// rotation and scale snapping, the scale handles, the root a link gets, who may change what, the shape parameters per prim type and
/// their encoding. Pure functions only, so the class runs in parallel.
/// </summary>
public class EditToolsTests
{
    private static readonly UUID Agent = new("6b1e2c3d-4f50-4a61-8b72-9c83d4e5f601"), Session = new("6b1e2c3d-4f50-4a61-8b72-9c83d4e5f602"),
        Group = new("6b1e2c3d-4f50-4a61-8b72-9c83d4e5f603");

    private static float F(byte[] d, int at) => BitConverter.ToSingle(d, at);
    private static void Near(float want, float got, float tol = 1e-4f) => Assert.True(MathF.Abs(want - got) <= tol, $"want {want}, got {got}");
    private static void Near(Vector3 want, Vector3 got, float tol = 1e-4f) => Assert.True(Vector3.Distance(want, got) <= tol, $"want {want}, got {got}");

    // ---------------- the messages ----------------

    [Fact]
    public void ObjectName_and_ObjectDescription_carry_the_object_and_its_text()
    {
        var n = LmvEdit.ObjectName(Agent, Session, 42, "Floor");
        Assert.Equal(Agent, n.AgentData.AgentID);
        Assert.Equal(Session, n.AgentData.SessionID);
        Assert.Equal(42u, Assert.Single(n.ObjectData).LocalID);
        Assert.Equal("Floor", Utils.BytesToString(n.ObjectData[0].Name));
        var d = LmvEdit.ObjectDescription(Agent, Session, 42, "A plank floor");
        Assert.Equal(42u, Assert.Single(d.ObjectData).LocalID);
        Assert.Equal("A plank floor", Utils.BytesToString(d.ObjectData[0].Description));
    }

    [Fact]
    public void ObjectPermissions_sets_one_field_and_mask_on_every_root_and_never_asks_for_the_god_override()
    {
        var p = LmvEdit.ObjectPermissions(Agent, Session, [7, 9], LmvEdit.FieldNextOwner, true, LmvBuild.PermCopy);
        Assert.Equal(Agent, p.AgentData.AgentID);
        Assert.False(p.HeaderData.Override);
        Assert.Equal([7u, 9u], p.ObjectData.Select(o => o.ObjectLocalID).ToArray());
        Assert.All(p.ObjectData, o => { Assert.Equal(0x10, o.Field); Assert.Equal(1, o.Set); Assert.Equal(0x8000u, o.Mask); });
    }

    [Fact]
    public void Locked_takes_the_owners_move_and_modify_away_and_unlocking_gives_them_back()
    {
        var lk = LmvEdit.Lock(Agent, Session, [5], true);
        Assert.Equal(5u, Assert.Single(lk.ObjectData).ObjectLocalID);
        Assert.Equal(0x02, lk.ObjectData[0].Field);
        Assert.Equal(0, lk.ObjectData[0].Set);
        Assert.Equal(0x00084000u, lk.ObjectData[0].Mask);
        var un = LmvEdit.Lock(Agent, Session, [5], false);
        Assert.Equal(1, Assert.Single(un.ObjectData).Set);
        Assert.Equal(0x00084000u, un.ObjectData[0].Mask);
    }

    [Fact]
    public void MultipleObjectUpdate_packs_position_rotation_and_size_in_that_order_with_their_type_bits()
    {
        var q = Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, -0.9f));   // w < 0: sent as the same rotation with w >= 0
        var u = LmvEdit.Update(Agent, Session, 11, new Vector3(1, 2, 3), q, new Vector3(10, 10, 0.2f), linkset: true);
        var o = Assert.Single(u.ObjectData);
        Assert.Equal(11u, o.ObjectLocalID);
        Assert.Equal(0x01 | 0x02 | 0x04 | 0x08, o.Type);
        Assert.Equal(36, o.Data.Length);
        Near(new Vector3(1, 2, 3), new Vector3(F(o.Data, 0), F(o.Data, 4), F(o.Data, 8)));
        Near(new Vector3(-q.X, -q.Y, -q.Z), new Vector3(F(o.Data, 12), F(o.Data, 16), F(o.Data, 20)));
        Near(new Vector3(10, 10, 0.2f), new Vector3(F(o.Data, 24), F(o.Data, 28), F(o.Data, 32)));
        var pos = Assert.Single(LmvEdit.Update(Agent, Session, 11, new Vector3(4, 5, 6), null, null, linkset: false).ObjectData);
        Assert.Equal(0x01, pos.Type);
        Assert.Equal(12, pos.Data.Length);
        var uni = Assert.Single(LmvEdit.Update(Agent, Session, 11, null, null, new Vector3(2, 2, 2), linkset: true, uniform: true).ObjectData);
        Assert.Equal(0x04 | 0x08 | 0x10, uni.Type);
        Assert.Equal(12, uni.Data.Length);
    }

    [Fact]
    public void ObjectFlagUpdate_carries_physical_temporary_and_phantom_never_casts_shadows_and_no_extra_physics()
    {
        var f = LmvEdit.Flags(Agent, Session, 3, physical: true, temporary: false, phantom: true);
        Assert.Equal(Agent, f.AgentData.AgentID);
        Assert.Equal(3u, f.AgentData.ObjectLocalID);
        Assert.True(f.AgentData.UsePhysics);
        Assert.False(f.AgentData.IsTemporary);
        Assert.True(f.AgentData.IsPhantom);
        Assert.False(f.AgentData.CastsShadows);
        Assert.Empty(f.ExtraPhysics ?? []);
    }

    [Fact]
    public void ObjectShape_carries_the_quantised_block_as_given()
    {
        var b = ShapeRules.Encode(PrimShape.Box with { ProfileHollow = 0.5, PathTwist = 0.5 });
        var s = LmvEdit.Shape(Agent, Session, 21, b);
        var o = Assert.Single(s.ObjectData);
        Assert.Equal(21u, o.ObjectLocalID);
        Assert.Equal(b.ProfileHollow, o.ProfileHollow);
        Assert.Equal(b.PathTwist, o.PathTwist);
        Assert.Equal(b.PathCurve, o.PathCurve);
        Assert.Equal(b.ProfileCurve, o.ProfileCurve);
        Assert.Equal(b.PathScaleX, o.PathScaleX);
        Assert.Equal(b.PathEnd, o.PathEnd);
        Assert.Equal(25000, o.ProfileHollow);
        Assert.Equal(50, o.PathTwist);
    }

    [Fact]
    public void ObjectLink_sends_the_roots_in_the_order_given_and_ObjectDelink_every_prim()
    {
        var l = LmvEdit.Link(Agent, Session, [9, 7, 5]);
        Assert.Equal(Session, l.AgentData.SessionID);
        Assert.Equal([9u, 7u, 5u], l.ObjectData.Select(o => o.ObjectLocalID).ToArray());
        var d = LmvEdit.Delink(Agent, Session, [9, 7]);
        Assert.Equal([9u, 7u], d.ObjectData.Select(o => o.ObjectLocalID).ToArray());
    }

    [Fact]
    public void ObjectDuplicate_for_a_shift_drag_copies_in_place_with_no_flags_so_the_copy_stays_behind()
    {
        var d = LmvEdit.Duplicate(Agent, Session, Group, [12], Vector3.Zero, 0);
        Assert.Equal(Group, d.AgentData.GroupID);
        Assert.Equal(Agent, d.AgentData.AgentID);
        Assert.Equal(0u, d.SharedData.DuplicateFlags);
        Assert.Equal(LibreMetaverse.Vector3.Zero, d.SharedData.Offset);
        Assert.Equal(12u, Assert.Single(d.ObjectData).ObjectLocalID);
        Assert.Equal(2u, LmvEdit.DuplicateCreateSelected);
    }

    // ---------------- limits and clamps ----------------

    [Fact]
    public void The_grids_own_prim_size_and_height_limits_replace_the_official_ones_where_it_sends_them()
    {
        var o = EditLimits.Official;
        Assert.Equal((0.01f, 64f, 4096f, 256), (o.MinScale, o.MaxScale, o.MaxHeight, o.MaxLinkPrims));
        var g = EditLimits.FromExtras(new Dictionary<string, double> { ["MinPrimScale"] = 0.001, ["MaxPrimScale"] = 256, ["MaxPhysPrimScale"] = 10, ["MaxSimHeight"] = 50000, ["MinSimHeight"] = -100 });
        Assert.Equal((0.001f, 256f, 10f, 50000f, -100f), (g.MinScale, g.MaxScale, g.MaxPhysScale, g.MaxHeight, g.MinHeight));
        Assert.Equal(0.01f, g.MinPhysScale);
        // nonsense from a grid is ignored: a minimum above the maximum, or zero
        var bad = EditLimits.FromExtras(new Dictionary<string, double> { ["MinPrimScale"] = 100, ["MaxPrimScale"] = 0 });
        Assert.Equal((0.01f, 64f), (bad.MinScale, bad.MaxScale));
        Assert.Equal(o, EditLimits.FromExtras(null));
    }

    [Fact]
    public void A_typed_size_stays_inside_the_limits_and_a_physical_object_inside_the_physical_ones()
    {
        var g = EditLimits.Official with { MaxPhysScale = 10 };
        Assert.Equal(new Vector3(0.01f, 64f, 10f), EditNumbers.ClampScale(new Vector3(0.001f, 70f, 10f), false, g));
        Assert.Equal(new Vector3(0.01f, 10f, 10f), EditNumbers.ClampScale(new Vector3(0.001f, 70f, 10f), true, g));
        Assert.Equal(new Vector3(10f, 10f, 0.2f), EditNumbers.ClampScale(new Vector3(10f, 10f, 0.2f), false, EditLimits.Official));
    }

    [Fact]
    public void A_typed_position_must_lie_in_a_known_region_and_its_height_stays_above_the_ground_less_half_the_diagonal_and_under_the_limit()
    {
        var o = EditLimits.Official;
        RegionArea[] here = [new(0, 0, 256, 256)], withNorth = [new(0, 0, 256, 256), new(0, 256, 256, 256)], bigger = [new(0, 0, 512, 512)];
        Assert.Null(EditNumbers.TypedPosition(new Vector3(-5, 100, 30), here, 20, 0.5f, o));
        Assert.Null(EditNumbers.TypedPosition(new Vector3(100, 256, 30), here, 20, 0.5f, o));
        Assert.Equal(new Vector3(100, 300, 19.5f), EditNumbers.TypedPosition(new Vector3(100, 300, 5), withNorth, 20, 0.5f, o));
        Assert.Equal(new Vector3(400, 10, 4096), EditNumbers.TypedPosition(new Vector3(400, 10, 5000), bigger, 20, 0.5f, o));
        Assert.Equal(new Vector3(100, 100, 30), EditNumbers.TypedPosition(new Vector3(100, 100, 30), here, 20, 0.5f, o));
    }

    [Fact]
    public void ObjectDeselect_carries_each_local_id()
    {
        var d = LmvEdit.Deselect(Agent, Session, [7, 9]);
        Assert.Equal((Agent, Session), (d.AgentData.AgentID, d.AgentData.SessionID));
        Assert.Equal([7u, 9u], d.ObjectData.Select(b => b.ObjectLocalID).ToArray());
    }

    [Fact]
    public void Rotation_numbers_are_degrees_about_x_then_y_then_z_in_005_degree_steps()
    {
        var z90 = EditNumbers.FromEulerDegrees(new Vector3(0, 0, 90));
        Near(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, z90));
        var xz = EditNumbers.FromEulerDegrees(new Vector3(90, 0, 90));
        Near(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, xz));    // x turned about x stays, then z turns it to y
        Near(Vector3.UnitX, Vector3.Transform(Vector3.UnitZ, xz));    // z turned about x goes to -y, then z turns it to x
        Near(new Vector3(10, 20, 30), EditNumbers.ToEulerDegrees(EditNumbers.FromEulerDegrees(new Vector3(10, 20, 30))), 0.01f);
        Near(12.35f, EditNumbers.ToEulerDegrees(EditNumbers.FromEulerDegrees(new Vector3(12.34f, 0, 0))).X, 0.005f);
    }

    // ---------------- who may change what ----------------

    [Fact]
    public void Own_unlocked_objects_can_be_placed_and_reshaped_others_select_read_only_and_locked_ones_cannot_move()
    {
        uint own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy;
        var a = EditAccess.Of(own, LmvBuild.PermMove | LmvBuild.PermModify | LmvBuild.PermCopy);
        Assert.True(a.CanPlace);
        Assert.True(a.CanReshape);
        Assert.False(a.Locked);
        Assert.False(a.ReadOnly);
        var other = EditAccess.Of(0, null);
        Assert.True(other.ReadOnly);
        Assert.False(other.CanPlace);
        Assert.False(other.CanReshape);
        // locked: the region no longer offers move or modify, and the owner mask has no move
        var locked = EditAccess.Of(ObjectFlagBits.YouOwner | ObjectFlagBits.Copy, LmvBuild.PermCopy);
        Assert.True(locked.Locked);
        Assert.True(locked.Owned);
        Assert.False(locked.CanPlace);
        // someone else's object shared to move only
        var movable = EditAccess.Of(ObjectFlagBits.Move, null);
        Assert.True(movable.CanPlace);
        Assert.False(movable.CanReshape);
    }

    // ---------------- snapping ----------------

    [Fact]
    public void Grid_subdivisions_are_the_largest_power_of_two_that_keeps_ticks_three_pixels_apart()
    {
        Assert.Equal(1f, Snap.Subdivisions(0.5f, 0.001f, subUnits: false));
        Assert.Equal(32f, Snap.Subdivisions(0.5f, 0.001f, subUnits: true));
        Assert.Equal(16f, Snap.Subdivisions(0.5f, 0.01f, subUnits: true));      // 1/32 of 0.5 m is 1.6 px, 1/16 is 3.1 px
        Assert.Equal(1f / 32f, Snap.Subdivisions(0.5f, 10f, subUnits: true));  // from far away, never coarser than 32 units
    }

    [Fact]
    public void Moving_snaps_to_the_grid_from_its_origin()
    {
        Near(1.5f, Snap.ToGrid(1.26f, 0, 0.5f, 1));
        Near(1.1f, Snap.ToGrid(1.26f, 0.1f, 0.5f, 1));
        Near(1.25f, Snap.ToGrid(1.26f, 0, 0.5f, 2));
        Near(-0.5f, Snap.ToGrid(-0.3f, 0, 0.5f, 1));
    }

    [Fact]
    public void Rotation_snaps_in_5625_degree_steps_only_outside_the_ring()
    {
        Assert.Equal(5.625f, Snap.AngleIncrementDegrees);
        Near(5.625f, Snap.Angle(7f, outsideRing: true));
        Near(11.25f, Snap.Angle(8.5f, outsideRing: true));
        Near(-5.625f, Snap.Angle(-3f, outsideRing: true));
        Near(90f, Snap.Angle(91f, outsideRing: true));
        Near(7f, Snap.Angle(7f, outsideRing: false));
    }

    [Fact]
    public void Scaling_snaps_to_the_grid_and_stays_within_the_limits()
    {
        Near(1.5f, Snap.Size(1.26f, 0.5f, 1, true, 0.01f, 64f));
        Near(1.26f, Snap.Size(1.26f, 0.5f, 1, false, 0.01f, 64f));
        Near(0.01f, Snap.Size(0.001f, 0.5f, 1, false, 0.01f, 64f));
        Near(64f, Snap.Size(100f, 0.5f, 1, true, 0.01f, 64f));
        Near(0.01f, Snap.Size(0.1f, 0.5f, 1, true, 0.01f, 64f));   // snapping to 0 is held at the smallest size
    }

    [Fact]
    public void A_corner_handle_scales_every_axis_by_one_ratio_held_so_no_axis_leaves_the_limits()
    {
        Near(new Vector3(2, 4, 1), ScaleHandles.Corner(new Vector3(1, 2, 0.5f), 2, 0.01f, 64f));
        Near(new Vector3(32, 64, 16), ScaleHandles.Corner(new Vector3(1, 2, 0.5f), 100, 0.01f, 64f));
        Near(new Vector3(0.02f, 0.04f, 0.01f), ScaleHandles.Corner(new Vector3(1, 2, 0.5f), 0.001f, 0.01f, 64f));
    }

    [Fact]
    public void A_face_handle_changes_one_axis_and_moves_the_centre_unless_both_sides_stretch()
    {
        var (s, shift) = ScaleHandles.Face(Vector3.One, 0, +1, 3, bothSides: false, 0.01f, 64f);
        Near(new Vector3(3, 1, 1), s);
        Near(new Vector3(1, 0, 0), shift);
        (s, shift) = ScaleHandles.Face(Vector3.One, 2, -1, 3, bothSides: false, 0.01f, 64f);
        Near(new Vector3(1, 1, 3), s);
        Near(new Vector3(0, 0, -1), shift);
        (s, shift) = ScaleHandles.Face(Vector3.One, 1, +1, 3, bothSides: true, 0.01f, 64f);
        Near(new Vector3(1, 3, 1), s);
        Near(Vector3.Zero, shift);
        (s, _) = ScaleHandles.Face(Vector3.One, 0, +1, 100, bothSides: true, 0.01f, 64f);
        Near(new Vector3(64, 1, 1), s);
    }

    // ---------------- link ----------------

    [Fact]
    public void Linking_makes_the_last_selected_object_the_root()
    {
        Assert.Equal([9u, 7u, 5u], LinkRules.Order([5, 7, 9]));
        Assert.Equal([3u, 4u], LinkRules.Order([4, 3]));
    }

    [Fact]
    public void A_link_needs_two_owned_modifiable_roots_and_no_more_prims_than_the_limit()
    {
        uint ok = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move;
        var l = EditLimits.Official;
        Assert.Null(LinkRules.Refusal([(ok, 1), (ok, 3)], l));
        Assert.Equal("link.too_few", LinkRules.Refusal([(ok, 4)], l));
        Assert.Equal("link.not_modifiable", LinkRules.Refusal([(ok, 1), (ObjectFlagBits.YouOwner, 1)], l));
        Assert.Equal("link.not_owned", LinkRules.Refusal([(ok, 1), (ObjectFlagBits.Modify, 1)], l));
        Assert.Equal("link.too_many", LinkRules.Refusal([(ok, 200), (ok, 57)], l));
        Assert.Null(LinkRules.Refusal([(ok, 200), (ok, 56)], l));
    }

    // ---------------- shapes ----------------

    [Fact]
    public void The_prim_type_is_read_from_the_profile_and_the_path()
    {
        PrimShape S(int profile, int path, int sculpt = 0) => new() { ProfileCurve = profile, PathCurve = path, SculptType = sculpt };
        Assert.Equal(PrimKind.Box, ShapeRules.KindOf(S(1, 0x10)));
        Assert.Equal(PrimKind.Cylinder, ShapeRules.KindOf(S(0, 0x10)));
        Assert.Equal(PrimKind.Prism, ShapeRules.KindOf(S(3, 0x10)));
        Assert.Equal(PrimKind.Sphere, ShapeRules.KindOf(S(5, 0x20)));
        Assert.Equal(PrimKind.Torus, ShapeRules.KindOf(S(0, 0x20)));
        Assert.Equal(PrimKind.Tube, ShapeRules.KindOf(S(1, 0x20)));
        Assert.Equal(PrimKind.Ring, ShapeRules.KindOf(S(3, 0x20)));
        Assert.Equal(PrimKind.Sculpt, ShapeRules.KindOf(S(0, 0x20, 1)));
        foreach (var k in new[] { PrimKind.Box, PrimKind.Cylinder, PrimKind.Prism, PrimKind.Sphere, PrimKind.Torus, PrimKind.Tube, PrimKind.Ring })
            Assert.Equal(k, ShapeRules.KindOf(ShapeRules.OfKind(k)));
    }

    [Fact]
    public void Shape_parameters_are_held_to_the_official_ranges_for_their_type()
    {
        var box = ShapeRules.Clamp(PrimShape.Box with { ProfileHollow = 0.99, PathBegin = 0.99, PathEnd = 0.5, PathTwist = 1.5, PathShearX = 0.8, PathScaleX = 2.5 });
        Near(0.95f, (float)box.ProfileHollow);
        Near(0.5f, (float)box.PathEnd);
        Near(0.48f, (float)box.PathBegin);
        Near(1f, (float)box.PathTwist);
        Near(0.5f, (float)box.PathShearX);
        Near(2f, (float)box.PathScaleX);
        var cyl = ShapeRules.Clamp(ShapeRules.OfKind(PrimKind.Cylinder) with { ProfileHole = 0x20, ProfileHollow = 0.9 });
        Near(0.70f, (float)cyl.ProfileHollow);
        var boxSquareHole = ShapeRules.Clamp(PrimShape.Box with { ProfileHole = 0x20, ProfileHollow = 0.9 });
        Near(0.9f, (float)boxSquareHole.ProfileHollow);
        var torus = ShapeRules.Clamp(ShapeRules.OfKind(PrimKind.Torus) with { PathScaleX = 0.01, PathScaleY = 0.9, PathRevolutions = 5 });
        Near(0.05f, (float)torus.PathScaleX);
        Near(0.5f, (float)torus.PathScaleY);
        Near(4f, (float)torus.PathRevolutions);
        Assert.Equal(180, ShapeRules.TwistDegreesRange(PrimShape.Box));
        Assert.Equal(360, ShapeRules.TwistDegreesRange(ShapeRules.OfKind(PrimKind.Torus)));
    }

    [Fact]
    public void The_shape_block_is_quantised_as_the_official_viewer_packs_it()
    {
        var s = PrimShape.Box with
        {
            ProfileHole = 0x10, ProfileHollow = 0.95, PathBegin = 0.25, PathEnd = 1, ProfileBegin = 0.1, ProfileEnd = 0.9,
            PathTwist = 0.5, PathTwistBegin = -0.25, PathScaleX = 1, PathScaleY = 0.5, PathShearX = -0.25, PathTaperX = 0.5, PathRevolutions = 2, PathSkew = -0.1,
        };
        var b = ShapeRules.Encode(s);
        Assert.Equal(0x10, b.PathCurve);
        Assert.Equal(0x11, b.ProfileCurve);
        Assert.Equal(12500, b.PathBegin);
        Assert.Equal(0, b.PathEnd);
        Assert.Equal(5000, b.ProfileBegin);
        Assert.Equal(5000, b.ProfileEnd);
        Assert.Equal(47500, b.ProfileHollow);
        Assert.Equal(50, b.PathTwist);
        Assert.Equal(-25, b.PathTwistBegin);
        Assert.Equal(100, b.PathScaleX);
        Assert.Equal(150, b.PathScaleY);
        Assert.Equal(231, b.PathShearX);   // -25 as a signed byte
        Assert.Equal(50, b.PathTaperX);
        Assert.Equal(67, b.PathRevolutions);
        Assert.Equal(-10, b.PathSkew);
    }

    // ---------------- the intents ----------------

    [Fact]
    public void Edit_intents_refuse_what_cannot_be_sent()
    {
        Assert.Empty(new SetObjectNameIntent(4, "Floor").Validate());
        Assert.Contains(new SetObjectNameIntent(0, "Floor").Validate(), i => i.Code == "edit.no_object");
        Assert.Contains(new SetObjectNameIntent(4, new string('x', 64)).Validate(), i => i.Code == "edit.name_too_long");
        Assert.Contains(new SetObjectDescriptionIntent(4, new string('x', 128)).Validate(), i => i.Code == "edit.description_too_long");
        Assert.Contains(new UpdateObjectIntent(4, new Vector3(float.NaN, 0, 0), null, null, true).Validate(), i => i.Code == "edit.bad_number");
        Assert.Contains(new UpdateObjectIntent(4, null, null, null, true).Validate(), i => i.Code == "edit.nothing");
        Assert.Contains(new LinkObjectsIntent([4]).Validate(), i => i.Code == "link.too_few");
        Assert.Contains(new DelinkObjectsIntent([]).Validate(), i => i.Code == "edit.no_object");
        Assert.Contains(new DuplicateObjectsIntent([], Vector3.Zero).Validate(), i => i.Code == "edit.no_object");
        Assert.Empty(new DuplicateObjectsIntent([4], Vector3.Zero).Validate());
        Assert.Equal(ActionKind.EditObject, new LinkObjectsIntent([4, 5]).Kind);
    }
}
