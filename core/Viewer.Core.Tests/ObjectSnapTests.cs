// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
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
/// Snap to objects: the pure rules on boxes, then the Move drag that uses them. Pure arithmetic and an in-memory world; each test builds its
/// own world, so the class runs in parallel.
/// </summary>
public class ObjectSnapTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Me = Guid.Parse("7b1d3f50-2c4e-4a6b-9d8f-0e1f2a3b4c01");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;
    private static Quaternion Z(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);

    private static void Near(Vector3 want, Vector3 got, float tol = 1e-3f) => Assert.True(Vector3.Distance(want, got) <= tol, $"want {want}, got {got}");

    private static SnapBox Box(Vector3 middle, Vector3 size) => new(middle - size / 2f, middle + size / 2f);
    private static Vector3 Along(int axis, float v) => axis switch { 0 => new(v, 0, 0), 1 => new(0, v, 0), _ => new(0, 0, v) };

    // ---------------- the rules ----------------

    [Theory]
    [InlineData(0, 1f)]
    [InlineData(0, -1f)]
    [InlineData(1, 1f)]
    [InlineData(1, -1f)]
    [InlineData(2, 1f)]
    [InlineData(2, -1f)]
    public void A_face_within_the_distance_goes_flush_on_each_axis_from_either_side(int axis, float side)
    {
        // unit boxes 3 m apart along the axis: flush after 2 m of travel; the proposed move stops 8 cm short
        var moving = Box(Vector3.Zero, Vector3.One);
        var other = Box(Along(axis, 3 * side), Vector3.One);
        var r = ObjectSnap.Snap(moving, Along(axis, 1.92f * side), [other], 0.1f, letGo: false);
        Near(Along(axis, 2f * side), r.Move);
        var g = Assert.Single(r.Guides);
        Assert.Equal((axis, SnapKind.Face), (g.Axis, g.Kind));
        Assert.Equal(2.5f * side, g.At, 3);
    }

    [Fact]
    public void A_face_pushed_slightly_into_the_other_box_comes_back_flush()
    {
        var r = ObjectSnap.Snap(Box(Vector3.Zero, Vector3.One), new Vector3(2.07f, 0, 0), [Box(new(3, 0, 0), Vector3.One)], 0.1f, false);
        Near(new Vector3(2, 0, 0), r.Move);
    }

    [Fact]
    public void A_turned_target_is_caught_at_the_box_round_its_turned_corners()
    {
        // a 2 x 2 m target turned 45 degrees reaches 1.41421 m from its middle
        var (min, max) = ArrangeRules.BoxOf([new PivotBox(new(5, 0, 0), Z(45), new(2, 2, 1))], Quaternion.Identity);
        var r = ObjectSnap.Snap(Box(Vector3.Zero, Vector3.One), new Vector3(3.05f, 0, 0), [new SnapBox(min, max)], 0.1f, false);
        Near(new Vector3(5 - MathF.Sqrt(2) - 0.5f, 0, 0), r.Move);
    }

    [Fact]
    public void Of_two_candidates_the_nearest_wins()
    {
        var far = Box(new(3.00f, 0.9f, 0), Vector3.One);
        var near = Box(new(2.95f, -0.9f, 0), Vector3.One);
        var r = ObjectSnap.Snap(Box(Vector3.Zero, Vector3.One), new Vector3(1.93f, 0, 0), [far, near], 0.1f, false);
        Near(new Vector3(1.95f, 0, 0), r.Move);
        Assert.Equal(near, Assert.Single(r.Guides).Target);
    }

    [Fact]
    public void Nothing_within_the_distance_leaves_the_move_alone()
    {
        var r = ObjectSnap.Snap(Box(Vector3.Zero, Vector3.One), new Vector3(1.8f, 0, 0), [Box(new(3, 0, 0), Vector3.One)], 0.1f, false);
        Near(new Vector3(1.8f, 0, 0), r.Move);
        Assert.Empty(r.Guides);
    }

    [Fact]
    public void A_box_far_off_to_one_side_does_not_catch_even_when_a_side_would_line_up()
    {
        var r = ObjectSnap.Snap(Box(Vector3.Zero, Vector3.One), new Vector3(2.95f, 0, 0), [Box(new(3, 8, 0), Vector3.One)], 0.1f, false);
        Near(new Vector3(2.95f, 0, 0), r.Move);
        Assert.Empty(r.Guides);
    }

    [Fact]
    public void Letting_go_of_the_snap_leaves_the_move_alone()
    {
        var r = ObjectSnap.Snap(Box(Vector3.Zero, Vector3.One), new Vector3(1.95f, 0, 0), [Box(new(3, 0, 0), Vector3.One)], 0.1f, letGo: true);
        Near(new Vector3(1.95f, 0, 0), r.Move);
        Assert.Empty(r.Guides);
    }

    [Fact]
    public void Like_sides_line_up_beside_a_box()
    {
        // the target (2 m) stands just beside along y; dragging along x, the low sides line up
        var r = ObjectSnap.Snap(Box(Vector3.Zero, Vector3.One), new Vector3(4.05f, 0, 0), [Box(new(4.6f, 1.5f, 0), new(2, 2, 1))], 0.1f, false);
        Near(new Vector3(4.1f, 0, 0), r.Move);
        Assert.Equal(SnapKind.Edge, Assert.Single(r.Guides).Kind);
    }

    [Fact]
    public void Middles_line_up_beside_a_box()
    {
        var r = ObjectSnap.Snap(Box(Vector3.Zero, Vector3.One), new Vector3(4.55f, 0, 0), [Box(new(4.6f, 1.5f, 0), new(3, 2, 1))], 0.1f, false);
        Near(new Vector3(4.6f, 0, 0), r.Move);
        Assert.Equal(SnapKind.Centre, Assert.Single(r.Guides).Kind);
    }

    [Fact]
    public void Only_the_axes_being_moved_catch()
    {
        // 5 cm off in y from a flush side, but the drag is along x alone
        var r = ObjectSnap.Snap(Box(new(0, 0.05f, 0), Vector3.One), new Vector3(1.95f, 0, 0), [Box(new(3, 0, 0), Vector3.One)], 0.1f, false);
        Near(new Vector3(2, 0, 0), r.Move);
        Assert.Equal(0, Assert.Single(r.Guides).Axis);
    }

    [Fact]
    public void A_plane_drag_catches_on_both_its_axes()
    {
        var r = ObjectSnap.Snap(Box(Vector3.Zero, Vector3.One), new Vector3(1.96f, 0.05f, 0), [Box(new(3, 0, 0), Vector3.One)], 0.1f, false);
        Near(new Vector3(2, 0, 0), r.Move);
        Assert.Equal(2, r.Guides.Count);
    }

    [Fact]
    public void A_guide_outlines_the_caught_side_of_the_target_in_the_grids_frame()
    {
        var target = new SnapBox(new(2.5f, -1, -2), new(3.5f, 1, 2));
        var lines = ObjectSnap.Outline(new SnapGuide(0, 2.5f, SnapKind.Face, target), Z(90), target, 0.1f);
        Assert.Equal(4, lines.Count);
        // in a frame turned 90 degrees the frame's x = 2.5 is the region's y = 2.5
        Assert.All(lines, l => { Assert.Equal(2.5f, l.From.Y, 3); Assert.Equal(2.5f, l.To.Y, 3); });
    }

    // ---------------- the Move drag ----------------

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

        /// <summary>Drags the selection by <paramref name="delta"/> and gives where object 1 was last sent.</summary>
        public Vector3 Drag(Vector3 delta, float distance = 0.1f, bool letGo = false, uint local = 1)
        {
            Assert.True(C.BeginManip());
            C.ManipMove(delta, distance, letGo);
            return Sent.OfType<UpdateObjectIntent>().Last(i => i.LocalId == local).Position!.Value;
        }
    }

    /// <summary>A unit box at x 10 and another at x 13.13 (flush when the first stands at 12.13, which is on no grid line).</summary>
    private static Rig Pair()
    {
        var r = new Rig();
        r.Prim(1, new(10, 10, 30), Vector3.One);
        r.Prim(2, new(13.13f, 10, 30), Vector3.One);
        r.C.SelectMany([1], shift: false);
        return r;
    }

    [Fact]
    public void Snap_to_objects_starts_off_and_a_drag_then_goes_by_the_grid_alone()
    {
        var r = Pair();
        Assert.False(r.C.SnapToObjects);
        Near(new(12, 10, 30), r.Drag(new(2.1f, 0, 0)));   // the 0.5 m grid
        Assert.Empty(r.C.SnapLines);
    }

    [Fact]
    public void With_the_tick_on_a_drag_goes_flush_over_the_grid_and_shows_a_guide()
    {
        var r = Pair();
        r.C.SnapToObjects = true;
        Near(new(12.13f, 10, 30), r.Drag(new(2.1f, 0, 0)));
        Assert.Equal(4, r.C.SnapLines.Count);
        Near(new(12.13f, 10, 30), r.C.ManipCentre!.Value);   // the handles go with it
        r.C.EndManip();
        Assert.Empty(r.C.SnapLines);
    }

    [Fact]
    public void The_drags_guide_stands_out_past_both_boxes_by_the_margin_the_view_asks_for()
    {
        var r = Pair();
        r.C.SnapToObjects = true;
        Assert.True(r.C.BeginManip());
        r.C.ManipMove(new(2.1f, 0, 0), 0.1f, false, guideMargin: 0.3f);
        // two unit boxes meeting on a face at x = 12.63: the frame runs 0.3 m outside their 1 m cross-section
        var ends = r.C.SnapLines.SelectMany(l => new[] { l.From, l.To }).ToArray();
        Assert.Equal(8, ends.Length);
        Assert.Equal(9.2f, ends.Min(p => p.Y), 3);
        Assert.Equal(10.8f, ends.Max(p => p.Y), 3);
        Assert.Equal(29.2f, ends.Min(p => p.Z), 3);
        Assert.Equal(30.8f, ends.Max(p => p.Z), 3);
    }

    [Fact]
    public void Letting_go_during_a_drag_goes_back_to_the_grid()
    {
        var r = Pair();
        r.C.SnapToObjects = true;
        Near(new(12.13f, 10, 30), r.Drag(new(2.1f, 0, 0)));
        r.C.ManipMove(new(2.1f, 0, 0), 0.1f, letGo: true);
        Near(new(12, 10, 30), r.Sent.OfType<UpdateObjectIntent>().Last().Position!.Value);
        Assert.Empty(r.C.SnapLines);
    }

    [Fact]
    public void The_snap_distance_never_goes_past_a_quarter_metre()
    {
        var r = Pair();
        r.C.SnapToObjects = true;
        r.C.SnapEnabled = false;
        Near(new(11.8f, 10, 30), r.Drag(new(1.8f, 0, 0), distance: 5f));
    }

    [Fact]
    public void A_linkset_catches_by_the_box_of_all_its_prims_and_never_on_itself()
    {
        var r = new Rig();
        r.Prim(1, new(10, 10, 30), Vector3.One);
        r.Prim(2, new(1, 0, 0), Vector3.One, parent: 1);   // reaches x 11.5
        r.Prim(3, new(14, 10, 30), Vector3.One);           // low side 13.5
        r.C.SelectMany([1], shift: false);
        r.C.SnapToObjects = true;
        r.C.SnapEnabled = false;
        Near(new(12, 10, 30), r.Drag(new(1.95f, 0, 0)));
    }

    [Fact]
    public void A_part_moved_alone_catches_on_its_own_linksets_other_parts()
    {
        var r = new Rig();
        r.Prim(1, new(10, 10, 30), Vector3.One);
        r.Prim(2, new(3, 0, 0), Vector3.One, parent: 1);   // at x 13
        r.C.EditLinked = true;
        r.C.SelectMany([2], shift: false);
        r.C.SnapToObjects = true;
        r.C.SnapEnabled = false;
        Near(new(1, 0, 0), r.Drag(new(-1.95f, 0, 0), local: 2));   // flush against the root, sent in the root's frame
    }

    [Fact]
    public void Two_selected_objects_do_not_catch_on_each_other()
    {
        var r = Pair();
        r.C.SelectMany([1, 2], shift: false);
        r.C.SnapToObjects = true;
        r.C.SnapEnabled = false;
        Near(new(10.05f, 10, 30), r.Drag(new(0.05f, 0, 0)));
        Assert.Empty(r.C.SnapLines);
    }

    [Fact]
    public void Only_objects_within_ten_metres_of_the_selection_are_candidates()
    {
        var r = new Rig();
        r.Prim(1, new(10, 10, 30), Vector3.One);
        r.Prim(2, new(25, 10, 30), Vector3.One);   // flush at 24, 13 m from where the drag begins
        r.C.SelectMany([1], shift: false);
        r.C.SnapToObjects = true;
        r.C.SnapEnabled = false;
        Near(new(23.95f, 10, 30), r.Drag(new(13.95f, 0, 0)));
    }

    [Fact]
    public void A_copy_that_arrives_during_the_drag_can_be_caught_on_once_the_scene_is_read_again()
    {
        var r = new Rig();
        r.Prim(1, new(10, 10, 30), Vector3.One);
        r.C.SelectMany([1], shift: false);
        r.C.SnapToObjects = true;
        r.C.SnapEnabled = false;
        Assert.True(r.C.BeginManip());
        r.Prim(9, new(10, 10, 30), Vector3.One);   // the Shift-drag's copy, standing where the drag began
        r.C.ReadSnapTargetsAgain();
        r.C.ManipMove(new(0.95f, 0, 0), 0.1f);
        Near(new(11, 10, 30), r.Sent.OfType<UpdateObjectIntent>().Last().Position!.Value);   // flush against the copy
    }

    // ---------------- the Build window ----------------

    [Fact]
    public void The_window_has_a_findable_row_and_the_closed_placement_line_says_when_it_is_on()
    {
        var s = StringTable.English();
        Assert.Contains(BuildLayout.Search("snap to objects", s), r => r.Id == "snap_objects" && r.Section == BuildSection.Placement);
        var facts = new BuildFacts(Position: new(1, 2, 3), Size: Vector3.One);
        string off = BuildLayout.Summary(BuildSection.Placement, facts, s);
        Assert.Equal(off + ", snaps to objects", BuildLayout.Summary(BuildSection.Placement, facts with { SnapToObjects = true }, s));
        Assert.DoesNotContain("objects", off);
    }
}
