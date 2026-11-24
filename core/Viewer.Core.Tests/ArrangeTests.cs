// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Avatars;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Build;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Align in the Arrange section: each linkset's box from its prims' sizes and turns, the three edges on each axis, the two targets, the
/// grid frame, the refusals, the controller's one message, and the Build window's section and summary. Pure arithmetic and an in-memory
/// world; each test builds its own, so the class runs in parallel.
/// </summary>
public class ArrangeTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Me = Guid.Parse("2a4c6e80-1b3d-4f5a-8c7e-9d0f1a2b3c01");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;
    private static Quaternion Z(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);
    private static PivotBox Box(Vector3 at, Vector3 size, Quaternion? turn = null) => new(at, turn ?? Quaternion.Identity, size);
    private static ArrangeLinkset One(uint root, Vector3 at, Vector3 size, Quaternion? turn = null) => new(root, at, [Box(at, size, turn)]);
    private static void Near(float want, float got, float tol = 1e-3f) => Assert.True(MathF.Abs(want - got) <= tol, $"want {want}, got {got}");
    private static void Near(Vector3 want, Vector3 got, float tol = 1e-3f) => Assert.True(Vector3.Distance(want, got) <= tol, $"want {want}, got {got}");
    private static Vector3 Go(IReadOnlyList<ArrangeMove> moves, uint root) => moves.Single(m => m.Root == root).Position;

    // ---------------- a linkset's box ----------------

    [Fact]
    public void A_box_prim_not_turned_has_the_box_of_its_size()
    {
        var (min, max) = ArrangeRules.BoxOf([Box(new Vector3(10, 10, 10), new Vector3(2, 2, 2))], Quaternion.Identity);
        Near(new Vector3(9, 9, 9), min);
        Near(new Vector3(11, 11, 11), max);
    }

    [Fact]
    public void A_box_turned_45_degrees_reaches_further_along_the_axes()
    {
        var (min, max) = ArrangeRules.BoxOf([Box(new Vector3(10, 10, 10), new Vector3(2, 2, 2), Z(45))], Quaternion.Identity);
        float reach = MathF.Sqrt(2);   // half the diagonal of a 2 x 2 square
        Near(10 - reach, min.X);
        Near(10 + reach, max.X);
        Near(10 - reach, min.Y);
        Near(9, min.Z);
        Near(11, max.Z);
    }

    [Fact]
    public void A_box_turned_90_degrees_swaps_its_x_and_y_sizes()
    {
        var (min, max) = ArrangeRules.BoxOf([Box(new Vector3(10, 10, 10), new Vector3(2, 4, 2), Z(90))], Quaternion.Identity);
        Near(8, min.X);
        Near(12, max.X);
        Near(9, min.Y);
        Near(11, max.Y);
    }

    [Fact]
    public void A_linkset_with_an_offset_child_has_the_union_of_its_prims_boxes()
    {
        var (min, max) = ArrangeRules.BoxOf([Box(new Vector3(10, 10, 10), Vector3.One), Box(new Vector3(13, 10, 10), Vector3.One)], Quaternion.Identity);
        Near(new Vector3(9.5f, 9.5f, 9.5f), min);
        Near(new Vector3(13.5f, 10.5f, 10.5f), max);
    }

    [Fact]
    public void A_box_in_a_turned_frame_is_measured_along_that_frames_axes()
    {
        // a 2 x 4 prim turned 90 degrees lies with its long side along the region's x; in a frame turned the same way it is 2 by 4 again
        var (min, max) = ArrangeRules.BoxOf([Box(Vector3.Zero, new Vector3(2, 4, 2), Z(90))], Z(90));
        Near(2, max.X - min.X);
        Near(4, max.Y - min.Y);
    }

    // ---------------- align on the selection box ----------------

    private static ArrangeLinkset[] Three() =>
    [
        One(1, new Vector3(10, 10, 10), new Vector3(1, 1, 1)),
        One(2, new Vector3(20, 14, 12), new Vector3(2, 2, 2)),
        One(3, new Vector3(30, 18, 14), new Vector3(4, 4, 4)),
    ];

    [Fact]
    public void Max_x_puts_every_high_edge_on_the_selection_boxs_high_edge_and_changes_only_x()
    {
        var moves = ArrangeRules.Align(Three(), ArrangeAxis.X, ArrangeEdge.Max, ArrangeTarget.SelectionBox, Quaternion.Identity);
        // the box ends at 32 (the 4 m prim at 30); the 1 m prim's centre goes to 31.5, the 2 m prim's to 31; the 4 m prim is already there
        Near(new Vector3(31.5f, 10, 10), Go(moves, 1));
        Near(new Vector3(31, 14, 12), Go(moves, 2));
        Assert.DoesNotContain(moves, m => m.Root == 3);
    }

    [Fact]
    public void Min_x_puts_every_low_edge_on_the_selection_boxs_low_edge()
    {
        var moves = ArrangeRules.Align(Three(), ArrangeAxis.X, ArrangeEdge.Min, ArrangeTarget.SelectionBox, Quaternion.Identity);
        // the box starts at 9.5
        Assert.DoesNotContain(moves, m => m.Root == 1);
        Near(new Vector3(10.5f, 14, 12), Go(moves, 2));
        Near(new Vector3(11.5f, 18, 14), Go(moves, 3));
    }

    [Fact]
    public void Centre_y_puts_every_middle_on_the_middle_of_the_selection_box()
    {
        var moves = ArrangeRules.Align(Three(), ArrangeAxis.Y, ArrangeEdge.Centre, ArrangeTarget.SelectionBox, Quaternion.Identity);
        // y runs 9.5 .. 20: the middle is 14.75
        Near(new Vector3(10, 14.75f, 10), Go(moves, 1));
        Near(new Vector3(20, 14.75f, 12), Go(moves, 2));
        Near(new Vector3(30, 14.75f, 14), Go(moves, 3));
    }

    [Fact]
    public void Max_z_works_on_the_z_axis()
    {
        var moves = ArrangeRules.Align(Three(), ArrangeAxis.Z, ArrangeEdge.Max, ArrangeTarget.SelectionBox, Quaternion.Identity);
        // z tops: 10.5, 13, 16: everything goes to 16
        Near(new Vector3(10, 10, 15.5f), Go(moves, 1));
        Near(new Vector3(20, 14, 15), Go(moves, 2));
        Assert.DoesNotContain(moves, m => m.Root == 3);
    }

    [Fact]
    public void A_turned_prim_lines_up_by_its_turned_corners()
    {
        var sets = new[] { One(1, new Vector3(10, 10, 10), new Vector3(2, 2, 2), Z(45)), One(2, new Vector3(20, 10, 10), new Vector3(2, 2, 2)) };
        var moves = ArrangeRules.Align(sets, ArrangeAxis.X, ArrangeEdge.Min, ArrangeTarget.SelectionBox, Quaternion.Identity);
        // the turned prim's low edge is 10 - sqrt 2; the plain one's centre goes to that + 1
        Near(new Vector3(10 - MathF.Sqrt(2) + 1, 10, 10), Go(moves, 2));
        Assert.DoesNotContain(moves, m => m.Root == 1);
    }

    [Fact]
    public void A_linkset_moves_by_its_root_and_its_whole_box_counts()
    {
        var wide = new ArrangeLinkset(1, new Vector3(10, 10, 10), [Box(new Vector3(10, 10, 10), Vector3.One), Box(new Vector3(13, 10, 10), Vector3.One)]);   // x 9.5 .. 13.5
        var plain = One(2, new Vector3(20, 10, 10), new Vector3(2, 2, 2));                                                                                  // x 19 .. 21
        var moves = ArrangeRules.Align([wide, plain], ArrangeAxis.X, ArrangeEdge.Max, ArrangeTarget.SelectionBox, Quaternion.Identity);
        Near(new Vector3(17.5f, 10, 10), Go(moves, 1));   // its high edge 13.5 goes to 21: the root moves 7.5
        Assert.Single(moves);
    }

    // ---------------- align on the last selected ----------------

    [Fact]
    public void The_last_selected_stays_and_the_others_line_up_to_its_box()
    {
        var moves = ArrangeRules.Align(Three(), ArrangeAxis.X, ArrangeEdge.Min, ArrangeTarget.LastSelected, Quaternion.Identity);
        // the last (id 3) starts at 28: 1 m prim to 28.5, 2 m prim to 29
        Assert.DoesNotContain(moves, m => m.Root == 3);
        Near(new Vector3(28.5f, 10, 10), Go(moves, 1));
        Near(new Vector3(29, 14, 12), Go(moves, 2));
    }

    [Fact]
    public void The_target_is_the_last_in_the_list_not_the_biggest()
    {
        var sets = Three().Reverse().ToArray();   // now the 1 m prim (id 1) is last
        var moves = ArrangeRules.Align(sets, ArrangeAxis.X, ArrangeEdge.Centre, ArrangeTarget.LastSelected, Quaternion.Identity);
        Assert.DoesNotContain(moves, m => m.Root == 1);
        Near(new Vector3(10, 18, 14), Go(moves, 3));
        Near(new Vector3(10, 14, 12), Go(moves, 2));
    }

    [Fact]
    public void Objects_already_in_line_are_left_out()
    {
        var sets = new[] { One(1, new Vector3(10, 10, 10), Vector3.One), One(2, new Vector3(10, 20, 10), Vector3.One) };
        Assert.Empty(ArrangeRules.Align(sets, ArrangeAxis.X, ArrangeEdge.Centre, ArrangeTarget.SelectionBox, Quaternion.Identity));
    }

    [Fact]
    public void A_turned_frame_lines_up_along_the_frames_axis()
    {
        // the frame's x runs along the region's y: Max on the frame's x is Max on the region's y
        var sets = new[] { One(1, new Vector3(10, 10, 10), Vector3.One), One(2, new Vector3(20, 14, 10), new Vector3(2, 2, 2)) };
        var moves = ArrangeRules.Align(sets, ArrangeAxis.X, ArrangeEdge.Max, ArrangeTarget.SelectionBox, Z(90));
        Near(new Vector3(10, 14.5f, 10), Go(moves, 1));   // the high edge on y is 15, so the 1 m prim's centre goes to 14.5
        Assert.DoesNotContain(moves, m => m.Root == 2);
    }

    // ---------------- refusals ----------------

    [Fact]
    public void Align_needs_two_linksets_none_worn_and_all_movable()
    {
        Assert.Equal(ArrangeRefusal.None, ArrangeRules.Refusal(2, false, true));
        Assert.Equal(ArrangeRefusal.TooFew, ArrangeRules.Refusal(1, false, true));
        Assert.Equal(ArrangeRefusal.Attachment, ArrangeRules.Refusal(3, true, true));
        Assert.Equal(ArrangeRefusal.NoMove, ArrangeRules.Refusal(3, false, false));
    }

    // ---------------- the controller ----------------

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

        public void Prim(uint local, Vector3 pos, Vector3 scale, Quaternion? turn = null, uint parent = 0, uint flags = Own, int attach = 0) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), parent, attach, pos, turn ?? Quaternion.Identity, scale, Interaction: new PrimInteraction(0, flags)));
    }

    private static Rig ThreeBoxes()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new Vector3(1, 1, 1));
        r.Prim(2, new Vector3(20, 14, 30), new Vector3(2, 2, 2));
        r.Prim(3, new Vector3(30, 18, 30), new Vector3(4, 4, 4));
        r.C.SelectMany([1, 2, 3], shift: false);
        return r;
    }

    [Fact]
    public void Align_sends_one_message_with_a_group_position_per_root()
    {
        var r = ThreeBoxes();
        Assert.True(r.C.Align(ArrangeAxis.X, ArrangeEdge.Max, ArrangeTarget.SelectionBox));
        var sent = r.Sent.OfType<MoveLinksetsIntent>().Single();
        Assert.Equal([1u, 2u], sent.Moves.Select(m => m.LocalId).Order().ToArray());
        Near(new Vector3(31.5f, 10, 30), sent.Moves.Single(m => m.LocalId == 1).Position);
        Near(new Vector3(31, 14, 30), sent.Moves.Single(m => m.LocalId == 2).Position);
        Assert.DoesNotContain(r.Sent, i => i is UpdateObjectIntent);
    }

    [Fact]
    public void A_part_picked_moves_its_whole_linkset_by_the_root_and_the_child_counts_in_the_box()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Vector3.One);
        r.Prim(2, new Vector3(3, 0, 0), Vector3.One, parent: 1);   // a child's place is relative to its root: world x 13
        r.Prim(3, new Vector3(20, 10, 30), new Vector3(2, 2, 2));
        r.C.SelectMany([2, 3], shift: false);   // the part, not the root
        Assert.Equal(2, r.C.ArrangeLinksets);
        Assert.True(r.C.Align(ArrangeAxis.X, ArrangeEdge.Max, ArrangeTarget.SelectionBox));
        var sent = r.Sent.OfType<MoveLinksetsIntent>().Single();
        var move = Assert.Single(sent.Moves);
        Assert.Equal(1u, move.LocalId);
        Near(new Vector3(17.5f, 10, 30), move.Position);
    }

    [Fact]
    public void The_last_selected_is_the_one_picked_last_in_the_window()
    {
        var r = ThreeBoxes();
        r.C.SelectMany([3, 1, 2], shift: false);   // 2 is last now
        Assert.True(r.C.Align(ArrangeAxis.X, ArrangeEdge.Centre, ArrangeTarget.LastSelected));
        var sent = r.Sent.OfType<MoveLinksetsIntent>().Last();
        Assert.DoesNotContain(sent.Moves, m => m.LocalId == 2);
        Near(new Vector3(20, 10, 30), sent.Moves.Single(m => m.LocalId == 1).Position);
        Near(new Vector3(20, 18, 30), sent.Moves.Single(m => m.LocalId == 3).Position);
    }

    [Fact]
    public void One_linkset_is_refused_and_nothing_is_sent()
    {
        var r = ThreeBoxes();
        r.C.SelectMany([1], shift: false);
        Assert.Equal(ArrangeRefusal.TooFew, r.C.ArrangeStatus);
        Assert.False(r.C.Align(ArrangeAxis.X, ArrangeEdge.Max, ArrangeTarget.SelectionBox));
        Assert.DoesNotContain(r.Sent, i => i is MoveLinksetsIntent);
    }

    [Fact]
    public void An_object_you_may_not_move_refuses_the_whole_align()
    {
        var r = ThreeBoxes();
        r.Prim(4, new Vector3(40, 10, 30), Vector3.One, flags: ObjectFlagBits.Modify);   // not yours to move
        r.C.SelectMany([1, 2, 4], shift: false);
        Assert.Equal(ArrangeRefusal.NoMove, r.C.ArrangeStatus);
        Assert.False(r.C.Align(ArrangeAxis.X, ArrangeEdge.Max, ArrangeTarget.SelectionBox));
        Assert.DoesNotContain(r.Sent, i => i is MoveLinksetsIntent);
    }

    [Fact]
    public void A_worn_object_refuses_the_align()
    {
        var r = ThreeBoxes();
        r.Prim(5, new Vector3(0.3f, 0, 0), Vector3.One, parent: 500, attach: 2);
        r.C.SelectMany([1, 2, 5], shift: false);
        Assert.Equal(ArrangeRefusal.Attachment, r.C.ArrangeStatus);
        Assert.False(r.C.Align(ArrangeAxis.X, ArrangeEdge.Max, ArrangeTarget.SelectionBox));
        Assert.DoesNotContain(r.Sent, i => i is MoveLinksetsIntent);
    }

    [Fact]
    public void Nothing_selected_has_nothing_to_align()
    {
        var r = ThreeBoxes();
        r.C.Deselect();
        Assert.False(r.C.Align(ArrangeAxis.X, ArrangeEdge.Max, ArrangeTarget.SelectionBox));
        Assert.Equal(0, r.C.ArrangeLinksets);
    }

    // ---------------- the message ----------------

    [Fact]
    public void The_message_holds_one_block_per_root_with_a_group_position()
    {
        var agent = new LibreMetaverse.UUID(Guid.NewGuid());
        var session = new LibreMetaverse.UUID(Guid.NewGuid());
        var packets = LmvEdit.MoveLinksets(agent, session, [new LinksetMove(7, new Vector3(1, 2, 3)), new LinksetMove(9, new Vector3(4, 5, 6))]);
        var packet = Assert.Single(packets);
        Assert.Equal(2, packet.ObjectData.Length);
        Assert.Equal(7u, packet.ObjectData[0].ObjectLocalID);
        Assert.Equal(LmvEdit.UpdPosition | LmvEdit.UpdLinkedSets, packet.ObjectData[0].Type);
        Assert.Equal(12, packet.ObjectData[0].Data.Length);
        Assert.Equal(4f, BitConverter.ToSingle(packet.ObjectData[1].Data, 0));
        Assert.Equal(6f, BitConverter.ToSingle(packet.ObjectData[1].Data, 8));
        Assert.Equal(agent, packet.AgentData.AgentID);
    }

    [Fact]
    public void A_long_list_is_split_so_each_message_stays_small()
    {
        var moves = Enumerable.Range(1, 95).Select(i => new LinksetMove((uint)i, new Vector3(i, 0, 0))).ToArray();
        var packets = LmvEdit.MoveLinksets(new LibreMetaverse.UUID(Guid.NewGuid()), new LibreMetaverse.UUID(Guid.NewGuid()), moves);
        Assert.Equal([40, 40, 15], packets.Select(p => p.ObjectData.Length).ToArray());
        Assert.Equal(Enumerable.Range(1, 95).Select(i => (uint)i), packets.SelectMany(p => p.ObjectData).Select(b => b.ObjectLocalID));
    }

    [Fact]
    public void The_intent_needs_a_move_with_finite_numbers()
    {
        Assert.NotEmpty(new MoveLinksetsIntent([]).Validate());
        Assert.NotEmpty(new MoveLinksetsIntent([new LinksetMove(3, new Vector3(float.NaN, 0, 0))]).Validate());
        Assert.Empty(new MoveLinksetsIntent([new LinksetMove(3, new Vector3(1, 2, 3))]).Validate());
    }

    // ---------------- the window ----------------

    private static readonly StringTable S = StringTable.English();

    [Fact]
    public void The_arrange_section_sits_between_placement_and_shape_and_its_rows_are_found_by_search()
    {
        var sections = BuildLayout.Sections.ToList();
        Assert.Equal(sections.IndexOf(BuildSection.Placement) + 1, sections.IndexOf(BuildSection.Arrange));
        Assert.Equal(sections.IndexOf(BuildSection.Arrange) + 1, sections.IndexOf(BuildSection.Shape));
        Assert.Contains(BuildLayout.Rows, r => r.Section == BuildSection.Arrange && r.Id == "align_axis");
        var hits = BuildLayout.Search("align", S);
        Assert.Contains(hits, h => h.Id == "align_axis");
        Assert.Contains(BuildSection.Arrange, BuildLayout.SectionsOf(hits));
    }

    [Fact]
    public void The_arrange_summary_counts_the_linksets_and_says_when_align_needs_more()
    {
        Assert.Equal("3 linksets: align", BuildLayout.Summary(BuildSection.Arrange, new BuildFacts(Linksets: 3), S));
        Assert.Equal("1 linkset: align needs 2 or more", BuildLayout.Summary(BuildSection.Arrange, new BuildFacts(Linksets: 1), S));
        Assert.Equal("Nothing selected", BuildLayout.Summary(BuildSection.Arrange, null, S));
    }

    [Fact]
    public void The_arrange_section_starts_closed()
    {
        Assert.DoesNotContain(BuildSection.Arrange, BuildLayout.OpenByDefault);
    }
}
