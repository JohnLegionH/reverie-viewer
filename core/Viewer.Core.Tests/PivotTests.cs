// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Avatars;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Build;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The pivot a handle drag turns and stretches about: the middle of the selection's box of extents (not of places), the root prim, and a
/// custom point as percent or metres of the box counted from its minimum corner. Pure arithmetic and an in-memory world; each test builds
/// its own world, so the class runs in parallel.
/// </summary>
public class PivotTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Me = Guid.Parse("2a4c6e80-1b3d-4f5a-8c7e-9d0f1a2b3c01");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;
    private static Quaternion Z(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);

    private sealed class Rig
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public EditController C;

        public Rig()
        {
            World.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
            World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            C = new EditController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, () => EditLimits.Official,
                regions: () => [new(0, 0, 256, 256)]);
            C.SelectTool(BuildTool.Edit);
        }

        public void Prim(uint local, Vector3 pos, Vector3 scale, Quaternion? turn = null, uint parent = 0) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), parent, 0, pos, turn ?? Quaternion.Identity, scale, Interaction: new PrimInteraction(0, Own)));

        public Vector3 Pivot() => C.Pivot ?? new Vector3(float.NaN);
    }

    private static void Near(Vector3 want, Vector3 got, float tol = 1e-3f) => Assert.True(Vector3.Distance(want, got) <= tol, $"want {want}, got {got}");

    // ---------------- Centre: the box of extents ----------------

    [Fact]
    public void Centre_of_two_objects_of_different_sizes_is_the_middle_of_their_extents_not_of_their_places()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new Vector3(1, 1, 1));
        r.Prim(3, new Vector3(14, 10, 30), new Vector3(4, 4, 4));
        r.C.SelectMany([1, 3], shift: false);
        // x 9.5 .. 16 (the middle of the places would be 12 on x, 10, 30)
        Near(new Vector3(12.75f, 10, 30), r.Pivot());
    }

    [Fact]
    public void Centre_counts_a_turned_objects_turned_box()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new Vector3(4, 1, 1), Z(90));   // turned: reaches 2 m each way along y, half a metre along x
        r.Prim(3, new Vector3(13, 12, 30), new Vector3(1, 1, 1));
        r.C.SelectMany([1, 3], shift: false);
        Near(new Vector3(11.5f, 10.25f, 30), r.Pivot());                     // x 9.5 .. 13.5, y 8 .. 12.5 (the middle of the places would be 11.5, 11)
    }

    [Fact]
    public void Centre_of_one_object_is_its_own_place()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 11, 30), new Vector3(1, 3, 5), Z(33));
        r.C.SelectMany([1], shift: false);
        Near(new Vector3(10, 11, 30), r.Pivot());
        Assert.Equal(PivotMode.Centre, r.C.PivotChoice);
    }

    // ---------------- Root ----------------

    [Fact]
    public void Root_puts_the_pivot_on_the_root_prim_when_a_part_is_selected()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new Vector3(2, 2, 2), Z(90));
        r.Prim(2, new Vector3(2, 0, 0), new Vector3(1, 1, 1), parent: 1);   // in the world at (10, 12, 30)
        r.C.EditLinked = true;
        r.C.SelectMany([2], shift: false);
        Near(new Vector3(10, 12, 30), r.Pivot());                             // Centre: the part itself
        r.C.SetPivot(PivotMode.Root);
        Assert.Equal(PivotMode.Root, r.C.PivotChoice);
        Near(new Vector3(10, 10, 30), r.Pivot());
    }

    // ---------------- Custom: percent and metres under a turn ----------------

    [Theory]
    [InlineData(0, 0, 0, -1f, -2f, -3f)]       // the minimum corner of a 2 x 4 x 6 box: a door's hinge
    [InlineData(50, 50, 50, 0f, 0f, 0f)]
    [InlineData(100, 0, 50, 1f, -2f, 0f)]
    [InlineData(25, 75, 100, -0.5f, 1f, 3f)]
    public void Custom_percent_is_counted_from_the_minimum_corner_in_the_objects_own_frame_under_a_45_degree_turn(float px, float py, float pz, float ox, float oy, float oz)
    {
        var r = new Rig();
        var turn = Z(45);
        r.Prim(1, new Vector3(20, 30, 40), new Vector3(2, 4, 6), turn);
        r.C.SelectMany([1], shift: false);
        r.C.SetPivot(PivotMode.Custom);
        r.C.SetCustomPivot(new Vector3(px, py, pz), PivotUnit.Percent);
        Near(new Vector3(20, 30, 40) + Vector3.Transform(new Vector3(ox, oy, oz), turn), r.Pivot());
    }

    [Fact]
    public void Custom_metres_are_counted_from_the_minimum_corner_in_the_objects_own_frame_under_a_45_degree_turn()
    {
        var r = new Rig();
        var turn = Z(45);
        r.Prim(1, new Vector3(20, 30, 40), new Vector3(2, 4, 6), turn);
        r.C.SelectMany([1], shift: false);
        r.C.SetPivot(PivotMode.Custom);
        r.C.SetCustomPivot(new Vector3(0.5f, 0, 1), PivotUnit.Metres);
        Near(new Vector3(20, 30, 40) + Vector3.Transform(new Vector3(-0.5f, -2f, -2f), turn), r.Pivot());
        Assert.Equal(PivotUnit.Metres, r.C.PivotUnitChoice);
        Assert.Equal(new Vector3(0.5f, 0, 1), r.C.PivotOffset);
    }

    [Fact]
    public void Custom_with_several_objects_counts_from_the_minimum_corner_of_their_region_aligned_box()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new Vector3(1, 1, 1));
        r.Prim(3, new Vector3(14, 10, 30), new Vector3(4, 4, 4));
        r.C.SelectMany([1, 3], shift: false);
        r.C.SetPivot(PivotMode.Custom);
        r.C.SetCustomPivot(Vector3.Zero, PivotUnit.Percent);
        Near(new Vector3(9.5f, 8, 28), r.Pivot());
        r.C.SetCustomPivot(new Vector3(100, 100, 100), PivotUnit.Percent);
        Near(new Vector3(16, 12, 32), r.Pivot());
    }

    // ---------------- the pivot stays with the selection ----------------

    [Fact]
    public void The_choice_stays_while_the_selection_stays_and_a_new_selection_starts_at_Centre()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new Vector3(2, 2, 2));
        r.Prim(3, new Vector3(20, 10, 30), new Vector3(2, 2, 2));
        r.C.SelectMany([1], shift: false);
        r.C.SetPivot(PivotMode.Custom);
        r.C.SetCustomPivot(Vector3.Zero, PivotUnit.Percent);
        Near(new Vector3(9, 9, 29), r.Pivot());
        r.C.SelectMany([1], shift: false);                                    // the same selection again keeps it
        Assert.Equal(PivotMode.Custom, r.C.PivotChoice);
        r.C.SelectMany([3], shift: false);
        Assert.Equal(PivotMode.Centre, r.C.PivotChoice);
        Near(new Vector3(20, 10, 30), r.Pivot());
        r.C.SelectMany([1], shift: false);                                    // and coming back does not bring the old choice back
        Assert.Equal(PivotMode.Centre, r.C.PivotChoice);
    }

    // ---------------- a drag about the pivot ----------------

    private static Vector3 MinCorner(UpdateObjectIntent u, Vector3 scale) => u.Position!.Value + Vector3.Transform(-scale / 2, u.Rotation!.Value);

    [Fact]
    public void A_rotate_about_a_custom_pivot_at_the_minimum_corner_leaves_that_corner_where_it_was_and_Centre_moves_it()
    {
        var scale = new Vector3(1, 1, 4);
        var start = new Vector3(20, 30, 40);
        var corner = start + Vector3.Transform(-scale / 2, Quaternion.Identity);
        var r = new Rig();
        r.Prim(1, start, scale);
        r.C.SelectMany([1], shift: false);
        r.C.SetPivot(PivotMode.Custom);
        r.C.SetCustomPivot(Vector3.Zero, PivotUnit.Percent);
        Assert.True(r.C.BeginManip());
        r.Sent.Clear();
        Assert.True(r.C.ManipRotate(Vector3.UnitZ, 90));
        Near(corner, MinCorner(r.Sent.OfType<UpdateObjectIntent>().Single(), scale), 1e-4f);
        r.C.EndManip();

        var c = new Rig();                                                     // the control: Centre turns the box about its middle, so the corner moves
        c.Prim(1, start, scale);
        c.C.SelectMany([1], shift: false);
        Assert.True(c.C.BeginManip());
        c.Sent.Clear();
        Assert.True(c.C.ManipRotate(Vector3.UnitZ, 90));
        Assert.True(Vector3.Distance(corner, MinCorner(c.Sent.OfType<UpdateObjectIntent>().Single(), scale)) > 0.5f);
    }

    [Fact]
    public void A_move_drag_carries_a_custom_pivot_with_the_selection_and_the_handles_stand_on_it()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(20, 30, 40), new Vector3(2, 2, 2));
        r.C.SelectMany([1], shift: false);
        r.C.SetPivot(PivotMode.Custom);
        r.C.SetCustomPivot(Vector3.Zero, PivotUnit.Percent);
        r.C.SnapEnabled = false;
        Assert.True(r.C.BeginManip());
        Near(new Vector3(19, 29, 39), r.C.ManipCentre ?? new Vector3(float.NaN));
        Assert.True(r.C.ManipMove(new Vector3(1, 0, 0)));
        Near(new Vector3(20, 29, 39), r.C.ManipCentre ?? new Vector3(float.NaN));
        r.C.EndManip();
    }

    // ---------------- the handles stand right ----------------

    [Fact]
    public void The_Stretch_handles_stand_on_the_boxs_middle_not_on_the_pivot()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(20, 30, 40), new Vector3(2, 4, 6), Z(45));
        r.C.SelectMany([1], shift: false);
        r.C.SetPivot(PivotMode.Custom);
        r.C.SetCustomPivot(Vector3.Zero, PivotUnit.Percent);
        Assert.True(r.Pivot() != new Vector3(20, 30, 40));
        Near(new Vector3(20, 30, 40), r.C.BoxCentre ?? new Vector3(float.NaN));
        Assert.True(r.C.BeginManip());
        Assert.True(r.C.ManipScaleCorner(2));                                  // one object grows about its own middle
        Near(new Vector3(20, 30, 40), r.C.ManipCentre ?? new Vector3(float.NaN));
        r.C.EndManip();
    }

    [Fact]
    public void Several_objects_stretched_by_a_corner_spread_about_the_pivot_and_the_boxs_middle_follows()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new Vector3(1, 1, 1));
        r.Prim(3, new Vector3(14, 10, 30), new Vector3(1, 1, 1));
        r.C.SelectMany([1, 3], shift: false);
        r.C.SetCustomPivot(Vector3.Zero, PivotUnit.Percent);                  // the box's minimum corner (9.5, 9.5, 29.5)
        Assert.True(r.C.BeginManip());
        Assert.True(r.C.ManipScaleCorner(2));
        // the middle (12, 10, 30) is 2.5 m east, 0.5 north and 0.5 up of the corner: doubled
        Near(new Vector3(14.5f, 10.5f, 30.5f), r.C.ManipCentre ?? new Vector3(float.NaN));
        r.C.EndManip();
    }

    [Fact]
    public void Changing_the_pivot_drops_the_handles_held_after_a_drag()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(20, 30, 40), new Vector3(2, 2, 2));
        r.C.SelectMany([1], shift: false);
        r.C.SetCustomPivot(Vector3.Zero, PivotUnit.Percent);
        r.C.SnapEnabled = false;
        Assert.True(r.C.BeginManip());
        Assert.True(r.C.ManipMove(new Vector3(1, 0, 0)));
        r.C.EndManip();
        Assert.NotNull(r.C.ManipCentre);                                       // held where the drag left them
        r.C.SetPivot(PivotMode.Centre);
        Assert.Null(r.C.ManipCentre);
    }

    // ---------------- the Placement summary ----------------

    [Fact]
    public void The_Placement_summary_names_a_pivot_that_is_not_Centre()
    {
        var s = StringTable.English();
        var facts = new BuildFacts(Position: new Vector3(1, 2, 3), Size: new Vector3(1, 1, 1));
        Assert.DoesNotContain("pivot", BuildLayout.Summary(BuildSection.Placement, facts, s));
        Assert.EndsWith(", pivot root", BuildLayout.Summary(BuildSection.Placement, facts with { Pivot = PivotMode.Root }, s));
        Assert.EndsWith(", pivot custom", BuildLayout.Summary(BuildSection.Placement, facts with { Pivot = PivotMode.Custom }, s));
    }
}
