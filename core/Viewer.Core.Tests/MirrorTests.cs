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
/// Mirror in the Arrange section: the reflection of each part's place about a plane, the turn that puts a symmetric part onto its image,
/// the refusals, the order and content of the messages the controller sends (root first, then each part in its root's frame), and the
/// Build window's row and summary. Pure arithmetic and an in-memory world; each test builds its own, so the class runs in parallel.
/// </summary>
public class MirrorTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Me = Guid.Parse("2a4c6e80-1b3d-4f5a-8c7e-9d0f1a2b3c01");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;
    private static readonly StringTable S = StringTable.English();

    private static Quaternion Turn(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), degrees * MathF.PI / 180f);
    private static void Near(Vector3 want, Vector3 got, float tol = 1e-4f) => Assert.True(Vector3.Distance(want, got) <= tol, $"want {want}, got {got}");

    /// <summary>The angle in radians between two turns, read in double precision (q and -q are the same turn).</summary>
    private static double Angle(Quaternion a, Quaternion b)
    {
        var d = Quaternion.Normalize(a) * Quaternion.Conjugate(Quaternion.Normalize(b));
        double v = Math.Sqrt((double)d.X * d.X + (double)d.Y * d.Y + (double)d.Z * d.Z);
        return 2.0 * Math.Atan2(v, Math.Abs((double)d.W));
    }

    private static IEnumerable<Vector3> Corners(Vector3 at, Quaternion turn, Vector3 size)
    {
        for (int i = 0; i < 8; i++)
            yield return at + Vector3.Transform(new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f) * size, turn);
    }

    private static bool SameSet(IEnumerable<Vector3> a, IEnumerable<Vector3> b)
    {
        var left = a.ToList();
        var right = b.ToList();
        return left.Count == right.Count && left.All(p => right.Any(q => Vector3.Distance(p, q) < 1e-3f));
    }

    private static Vector3 AxisOf(ArrangeAxis a) => a switch { ArrangeAxis.X => Vector3.UnitX, ArrangeAxis.Y => Vector3.UnitY, _ => Vector3.UnitZ };

    private static MirrorPart[] ThreeParts() =>
    [
        new(1, 0, new Vector3(20, 30, 40), Turn(new Vector3(0.2f, 0.9f, 0.3f), 33)),
        new(2, 1, new Vector3(22, 31, 44), Turn(new Vector3(1, 2, 3), 71)),
        new(3, 1, new Vector3(17, 36, 38), Turn(new Vector3(-2, 1, 0.5f), 154)),
    ];

    // ---------------- the arithmetic ----------------

    [Theory]
    [InlineData(ArrangeAxis.X)]
    [InlineData(ArrangeAxis.Y)]
    [InlineData(ArrangeAxis.Z)]
    public void A_place_goes_to_its_image_on_the_other_side_of_the_plane(ArrangeAxis axis)
    {
        var normal = MirrorRules.Normal(axis, Quaternion.Identity);
        Near(AxisOf(axis), normal);
        var point = new Vector3(10, 20, 30);
        var p = new Vector3(13, 25, 31);
        var image = MirrorRules.Reflect(p, point, normal);
        float along = Vector3.Dot(AxisOf(axis), p - point);
        Near(p - 2f * along * AxisOf(axis), image);
    }

    [Fact]
    public void The_normal_follows_the_grid_frame()
    {
        var frame = Turn(Vector3.UnitZ, 90);
        Near(Vector3.UnitY, MirrorRules.Normal(ArrangeAxis.X, frame));
        Near(-Vector3.UnitX, MirrorRules.Normal(ArrangeAxis.Y, frame));
        Near(Vector3.UnitZ, MirrorRules.Normal(ArrangeAxis.Z, frame));
    }

    [Theory]
    [InlineData(ArrangeAxis.X)]
    [InlineData(ArrangeAxis.Y)]
    [InlineData(ArrangeAxis.Z)]
    public void A_part_not_turned_stays_not_turned(ArrangeAxis axis)
    {
        var turned = MirrorRules.Rotation(Quaternion.Identity, AxisOf(axis));
        Assert.True(Angle(Quaternion.Identity, turned) < 1e-5, $"angle {Angle(Quaternion.Identity, turned)}");
    }

    [Theory]
    [InlineData(ArrangeAxis.X)]
    [InlineData(ArrangeAxis.Y)]
    [InlineData(ArrangeAxis.Z)]
    public void A_box_part_lands_exactly_on_the_image_of_its_box(ArrangeAxis axis)
    {
        var normal = AxisOf(axis);
        var point = new Vector3(5, -3, 8);
        var size = new Vector3(1, 2, 3);
        foreach (var turn in new[] { Turn(new Vector3(0.2f, 0.9f, 0.3f), 33), Turn(new Vector3(1, 2, 3), 71), Turn(new Vector3(-2, 1, 0.5f), 154), Turn(Vector3.UnitZ, 90), Turn(Vector3.UnitX, 180) })
        {
            var at = new Vector3(9, 4, 12);
            var image = MirrorRules.Reflect(at, point, normal);
            var turned = MirrorRules.Rotation(turn, normal);
            Assert.True(SameSet(Corners(image, turned, size), Corners(at, turn, size).Select(c => MirrorRules.Reflect(c, point, normal))), $"axis {axis}, turn {turn}");
        }
    }

    [Fact]
    public void A_box_part_lands_on_the_image_of_its_box_when_the_plane_is_tilted()
    {
        var normal = MirrorRules.Normal(ArrangeAxis.X, Turn(new Vector3(0, 0, 1), 30) * Turn(new Vector3(1, 0, 0), 20));
        var point = new Vector3(1, 2, 3);
        var size = new Vector3(1, 2, 3);
        var turn = Turn(new Vector3(1, 2, 3), 71);
        var at = new Vector3(9, 4, 12);
        var turned = MirrorRules.Rotation(turn, normal);
        Assert.True(SameSet(Corners(MirrorRules.Reflect(at, point, normal), turned, size), Corners(at, turn, size).Select(c => MirrorRules.Reflect(c, point, normal))));
    }

    [Theory]
    [InlineData(ArrangeAxis.X)]
    [InlineData(ArrangeAxis.Y)]
    [InlineData(ArrangeAxis.Z)]
    public void Mirrored_twice_a_linkset_is_back_where_it_started(ArrangeAxis axis)
    {
        var parts = ThreeParts();
        var normal = AxisOf(axis);
        var point = new Vector3(18, 33, 41);
        var back = MirrorRules.Mirror(MirrorRules.Mirror(parts, point, normal), point, normal);
        Assert.Equal(parts.Length, back.Count);
        for (int i = 0; i < parts.Length; i++)
        {
            Assert.Equal(parts[i].Local, back[i].Local);
            Near(parts[i].Position, back[i].Position, 1e-5f);
            Assert.True(Angle(parts[i].Rotation, back[i].Rotation) < 1e-5, $"part {parts[i].Local}: angle {Angle(parts[i].Rotation, back[i].Rotation)}");
        }
    }

    [Fact]
    public void Mirror_moves_every_part_and_keeps_the_ids_and_the_order()
    {
        var parts = ThreeParts();
        var mirrored = MirrorRules.Mirror(parts, new Vector3(18, 33, 41), Vector3.UnitX);
        Assert.Equal(parts.Select(p => (p.Local, p.Parent)), mirrored.Select(p => (p.Local, p.Parent)));
        Assert.All(parts.Zip(mirrored), z => Near(MirrorRules.Reflect(z.First.Position, new Vector3(18, 33, 41), Vector3.UnitX), z.Second.Position));
        Assert.All(mirrored, p => Assert.True(MathF.Abs(Quaternion.Normalize(p.Rotation).Length() - 1f) < 1e-5f));
    }

    // ---------------- the refusals ----------------

    [Fact]
    public void Mirror_is_refused_for_nothing_selected_a_worn_object_or_an_object_you_may_not_move()
    {
        Assert.Equal(ArrangeRefusal.TooFew, MirrorRules.Refusal(0, false, true));
        Assert.Equal(ArrangeRefusal.Attachment, MirrorRules.Refusal(1, true, true));
        Assert.Equal(ArrangeRefusal.NoMove, MirrorRules.Refusal(1, false, false));
        Assert.Equal(ArrangeRefusal.None, MirrorRules.Refusal(1, false, true));
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

        public IEnumerable<UpdateObjectIntent> Updates => Sent.OfType<UpdateObjectIntent>();
    }

    /// <summary>A root at (20, 30, 40) turned about Z, with two children whose places and turns are in the root's frame.</summary>
    private static Rig Linkset()
    {
        var r = new Rig();
        var rootTurn = Turn(Vector3.UnitZ, 40);
        r.Prim(1, new Vector3(20, 30, 40), new Vector3(1, 2, 1), rootTurn);
        r.Prim(2, new Vector3(3, 1, 2), new Vector3(1, 1, 2), Turn(new Vector3(1, 1, 0), 50), parent: 1);
        r.Prim(3, new Vector3(-2, 4, -1), new Vector3(2, 1, 1), Turn(new Vector3(0, 1, 1), 120), parent: 1);
        return r;
    }

    /// <summary>What the region would hold after the intents: a root's group position, a root's own turn, a child's place and turn in its
    /// root's frame; children keep their place in the world when the root alone turns (the server's root-only rotation).</summary>
    private static Dictionary<uint, (Vector3 Position, Quaternion Rotation)> Apply(Rig r, IEnumerable<ActionIntent> intents)
    {
        var world = new Dictionary<uint, (Vector3 Position, Quaternion Rotation)>();
        var snapshot = r.World.Snapshot().Objects.Values.ToList();
        var local = snapshot.ToDictionary(o => o.Id.LocalId, o => (o.Position, o.Rotation, o.ParentLocalId));
        foreach (var o in snapshot)
        {
            if (o.ParentLocalId == 0) { world[o.Id.LocalId] = (o.Position, o.Rotation); continue; }
            var p = local[o.ParentLocalId];
            world[o.Id.LocalId] = (p.Position + Vector3.Transform(o.Position, p.Rotation), p.Rotation * o.Rotation);
        }
        foreach (var u in intents.OfType<UpdateObjectIntent>())
        {
            var parent = local[u.LocalId].ParentLocalId;
            if (parent == 0)
            {
                var (pos, rot) = world[u.LocalId];
                if (u.Rotation is { } q && !u.Linkset)
                    rot = q;   // root only: the children stay where they are in the world
                else if (u.Rotation is { } q2)
                {
                    // the whole linkset turns about the root
                    var delta = q2 * Quaternion.Inverse(rot);
                    foreach (var k in world.Keys.Where(k => k != u.LocalId && local[k].ParentLocalId == u.LocalId).ToList())
                        world[k] = (pos + Vector3.Transform(world[k].Position - pos, delta), delta * world[k].Rotation);
                    rot = q2;
                }
                if (u.Position is { } to)
                {
                    var shift = to - pos;
                    if (u.Linkset)
                        foreach (var k in world.Keys.Where(k => k != u.LocalId && local[k].ParentLocalId == u.LocalId).ToList())
                            world[k] = (world[k].Position + shift, world[k].Rotation);
                    pos = to;
                }
                world[u.LocalId] = (pos, rot);
            }
            else
            {
                var (rp, rr) = world[parent];
                var (cp, cr) = world[u.LocalId];
                if (u.Position is { } lp) cp = rp + Vector3.Transform(lp, rr);
                if (u.Rotation is { } lr) cr = rr * lr;
                world[u.LocalId] = (cp, cr);
            }
        }
        return world;
    }

    [Theory]
    [InlineData(ArrangeAxis.X)]
    [InlineData(ArrangeAxis.Y)]
    [InlineData(ArrangeAxis.Z)]
    public void Mirror_sends_the_root_first_and_every_part_ends_where_the_rules_put_it(ArrangeAxis axis)
    {
        var r = Linkset();
        r.C.SelectMany([1], shift: false);
        Assert.Equal(ArrangeRefusal.None, r.C.MirrorStatus);
        var before = Apply(r, []);
        Assert.True(r.C.Mirror(axis, MirrorPlane.SelectionCentre));
        var order = r.Updates.Select(u => u.LocalId).ToList();
        Assert.Equal(1u, order[0]);   // the root goes first
        Assert.True(order.LastIndexOf(1u) < order.IndexOf(2u), "every message for the root comes before the first part's");
        Assert.Contains(2u, order);
        Assert.Contains(3u, order);
        var after = Apply(r, r.Sent);
        var want = MirrorRules.Mirror([.. before.Select(kv => new MirrorPart(kv.Key, kv.Key == 1 ? 0u : 1u, kv.Value.Position, kv.Value.Rotation)).OrderBy(p => p.Local)],
            PlaneCentre(before), AxisOf(axis));
        foreach (var p in want)
        {
            Near(p.Position, after[p.Local].Position, 1e-3f);
            Assert.True(Angle(p.Rotation, after[p.Local].Rotation) < 1e-3, $"part {p.Local}: angle {Angle(p.Rotation, after[p.Local].Rotation)}");
        }
    }

    /// <summary>The middle of the box that holds every prim's place and turned corners, as the controller takes it for the selection centre.</summary>
    private static Vector3 PlaneCentre(Dictionary<uint, (Vector3 Position, Quaternion Rotation)> world) =>
        PivotRules.Centre([.. world.OrderBy(kv => kv.Key).Select(kv => new PivotBox(kv.Value.Position, kv.Value.Rotation, ScaleOf(kv.Key)))]);

    private static Vector3 ScaleOf(uint local) => local switch { 1 => new Vector3(1, 2, 1), 2 => new Vector3(1, 1, 2), _ => new Vector3(2, 1, 1) };

    [Fact]
    public void Mirror_through_the_pivot_leaves_a_root_standing_on_the_plane_where_it_is()
    {
        var r = Linkset();
        r.C.SelectMany([1], shift: false);
        r.C.SetPivot(PivotMode.Root);
        Assert.True(r.C.Mirror(ArrangeAxis.X, MirrorPlane.Pivot));
        Assert.DoesNotContain(r.Updates, u => u.LocalId == 1 && u.Position is not null);   // the root's place is on the plane: no group move
        var after = Apply(r, r.Sent);
        var before = Apply(r, []);
        Near(new Vector3(20, 30, 40), after[1].Position);
        foreach (var k in new uint[] { 2, 3 })
            Near(MirrorRules.Reflect(before[k].Position, new Vector3(20, 30, 40), Vector3.UnitX), after[k].Position, 1e-3f);
    }

    [Fact]
    public void A_child_picked_with_edit_linked_mirrors_alone_and_its_root_is_not_sent_anything()
    {
        var r = Linkset();
        r.C.EditLinked = true;
        r.C.SelectMany([2], shift: false);
        Assert.Equal(ArrangeRefusal.None, r.C.MirrorStatus);
        var before = Apply(r, []);
        Assert.True(r.C.Mirror(ArrangeAxis.X, MirrorPlane.SelectionCentre));
        var update = Assert.Single(r.Updates);
        Assert.Equal(2u, update.LocalId);
        Assert.False(update.Linkset);
        Assert.NotNull(update.Rotation);   // the part's turn is sent too, in its root's frame
        var after = Apply(r, r.Sent);
        Assert.True(Angle(MirrorRules.Rotation(before[2].Rotation, Vector3.UnitX), after[2].Rotation) < 1e-3, "the part's turn is the image's turn");
        Near(before[1].Position, after[1].Position);
        Near(before[3].Position, after[3].Position);
        var image = MirrorRules.Reflect(before[2].Position, before[2].Position, Vector3.UnitX);   // the plane goes through the part's own middle
        Near(image, after[2].Position, 1e-3f);
    }

    [Fact]
    public void A_child_mirrored_about_a_plane_beside_it_goes_across_the_plane()
    {
        var r = Linkset();
        r.C.EditLinked = true;
        r.C.SelectMany([2, 3], shift: false);
        var before = Apply(r, []);
        Assert.True(r.C.Mirror(ArrangeAxis.Z, MirrorPlane.SelectionCentre));
        var after = Apply(r, r.Sent);
        // the plane stands through the middle of the two parts' boxes, worked out here from the world before the call
        var middle = PivotRules.Centre([new PivotBox(before[2].Position, before[2].Rotation, new Vector3(1, 1, 2)), new PivotBox(before[3].Position, before[3].Rotation, new Vector3(2, 1, 1))]);
        foreach (var k in new uint[] { 2, 3 })
            Near(MirrorRules.Reflect(before[k].Position, middle, Vector3.UnitZ), after[k].Position, 1e-3f);
        Near(before[1].Position, after[1].Position);
    }

    [Theory]
    [InlineData(ArrangeAxis.X, 0)]
    [InlineData(ArrangeAxis.X, 90)]
    [InlineData(ArrangeAxis.X, 180)]
    [InlineData(ArrangeAxis.Y, 90)]
    [InlineData(ArrangeAxis.Y, 270)]
    [InlineData(ArrangeAxis.Z, 90)]
    public void A_single_prim_mirrored_through_its_own_middle_about_a_plane_it_is_symmetric_across_sends_nothing(ArrangeAxis axis, float degrees)
    {
        var r = new Rig();
        r.Prim(1, new Vector3(20, 30, 40), new Vector3(1, 2, 3), Turn(Vector3.UnitZ, degrees));
        r.C.SelectMany([1], shift: false);
        Assert.False(r.C.Mirror(axis, MirrorPlane.SelectionCentre));   // nothing needs to change: no message, however the arithmetic rounds
        Assert.Empty(r.Updates);
    }

    [Fact]
    public void The_reason_Mirror_is_refused_names_what_the_person_can_do()
    {
        var r = Linkset();
        Assert.Equal("edit.mirror.no.few", r.C.MirrorRefusalKey);         // nothing selected
        r.C.SelectMany([1], shift: false);
        Assert.Null(r.C.MirrorRefusalKey);
        r.C.EditLinked = true;                                             // the root alone: with Edit linked parts only parts are mirrored
        r.C.SelectMany([1], shift: false);
        Assert.Equal("edit.mirror.no.parts", r.C.MirrorRefusalKey);
        r.C.SelectMany([2], shift: false);
        Assert.Null(r.C.MirrorRefusalKey);
    }

    [Fact]
    public void Nothing_selected_a_worn_object_or_a_fixed_one_sends_nothing()
    {
        var r = Linkset();
        Assert.Equal(ArrangeRefusal.TooFew, r.C.MirrorStatus);
        Assert.False(r.C.Mirror(ArrangeAxis.X, MirrorPlane.SelectionCentre));
        var worn = Linkset();
        worn.Prim(9, new Vector3(0.3f, 0, 0), Vector3.One, parent: 500, attach: 2);
        worn.C.SelectMany([9], shift: false);
        Assert.Equal(ArrangeRefusal.Attachment, worn.C.MirrorStatus);
        Assert.False(worn.C.Mirror(ArrangeAxis.X, MirrorPlane.SelectionCentre));
        var fixedOne = Linkset();
        fixedOne.Prim(8, new Vector3(5, 5, 5), Vector3.One, flags: ObjectFlagBits.Modify);
        fixedOne.C.SelectMany([8], shift: false);
        Assert.Equal(ArrangeRefusal.NoMove, fixedOne.C.MirrorStatus);
        Assert.False(fixedOne.C.Mirror(ArrangeAxis.X, MirrorPlane.SelectionCentre));
        Assert.DoesNotContain(r.Sent, i => i is UpdateObjectIntent);   // selecting asks the region for properties; nothing is moved
        Assert.DoesNotContain(worn.Sent, i => i is UpdateObjectIntent);   // selecting asks the region for properties; nothing is moved
        Assert.DoesNotContain(fixedOne.Sent, i => i is UpdateObjectIntent);   // selecting asks the region for properties; nothing is moved
    }

    // ---------------- the window ----------------

    [Fact]
    public void Mirror_has_its_rows_in_the_arrange_section_and_is_found_by_search()
    {
        Assert.Contains(BuildLayout.Rows, r => r.Section == BuildSection.Arrange && r.Id == "mirror_axis");
        Assert.Contains(BuildLayout.Rows, r => r.Section == BuildSection.Arrange && r.Id == "mirror_plane");
        Assert.Contains(BuildLayout.Search("mirror", S), h => h.Id == "mirror_axis");
    }

    [Fact]
    public void The_arrange_summary_lists_mirror_and_says_what_needs_more()
    {
        Assert.Equal("3 linksets: align, distribute, mirror", BuildLayout.Summary(BuildSection.Arrange, new BuildFacts(Linksets: 3), S));
        Assert.Equal("2 linksets: align, mirror", BuildLayout.Summary(BuildSection.Arrange, new BuildFacts(Linksets: 2), S));
        Assert.Equal("1 linkset: mirror", BuildLayout.Summary(BuildSection.Arrange, new BuildFacts(Linksets: 1), S));
    }
}
