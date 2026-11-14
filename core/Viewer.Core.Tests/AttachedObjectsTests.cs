// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What the world state holds for attached objects: each worn object against its wearer by local id with its point, in any
/// arrival order; an avatar that leaves takes its attachments with it; a detach removes them; the pose of a linked child is
/// carried through its root to the attachment point's frame. Expected values are worked by hand from the rules in each test.
/// Pure, runs in parallel.
/// </summary>
public class AttachedObjectsTests
{
    private const uint Wearer = 900, OtherWearer = 901;
    private const int Head = 2, LeftHand = 5, Chest = 1, Hud = 31;
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly RegionInfo Region = new("Here", 1000, 1000, 256, 256, false);

    private static WorldState NewWorld()
    {
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.BeginRegion(Sim, Region);
        return w;
    }

    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);

    private static AvatarData Av(uint local) =>
        new(local, G(9000 + local), "Av " + local, new Vector3(50, 50, 25), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f));

    private static PrimData Worn(uint local, uint wearer, int point, Vector3? pos = null, Quaternion? rot = null) =>
        new(local, G(local), wearer, point, pos ?? Vector3.Zero, rot ?? Quaternion.Identity, Vector3.One);

    /// <summary>A child of a worn root: the simulator states the same attachment point on every prim of the link set.</summary>
    private static PrimData Child(uint local, uint root, int point, Vector3 pos, Quaternion? rot = null) =>
        new(local, G(local), root, point, pos, rot ?? Quaternion.Identity, Vector3.One);

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True((expected - actual).Length() < 1e-5f, $"expected {expected}, got {actual}");

    private static Quaternion Yaw90 => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

    // ---------------- held against the wearer ----------------

    [Fact]
    public void Attachments_are_held_by_their_wearer_with_their_points()
    {
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyAvatar(Sim, Av(OtherWearer));
        w.ApplyObject(Sim, Worn(601, Wearer, Head), eventIsAttachment: true);
        w.ApplyObject(Sim, Worn(602, Wearer, LeftHand), eventIsAttachment: true);
        w.ApplyObject(Sim, Worn(603, OtherWearer, Chest), eventIsAttachment: true);

        var mine = w.AttachmentRoots(Wearer);
        Assert.Equal([601u, 602u], mine.Select(o => o.Id.LocalId));
        Assert.Equal([Head, LeftHand], mine.Select(o => o.AttachmentPoint!.Value));
        var theirs = Assert.Single(w.AttachmentRoots(OtherWearer));
        Assert.Equal(603u, theirs.Id.LocalId);
        Assert.Empty(w.AttachmentRoots(12345));
    }

    [Fact]
    public void Several_objects_on_one_point_are_all_held()
    {
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyObject(Sim, Worn(601, Wearer, Head), eventIsAttachment: true);
        w.ApplyObject(Sim, Worn(602, Wearer, Head, new Vector3(0, 0, 0.2f)), eventIsAttachment: true);

        var roots = w.AttachmentRoots(Wearer);
        Assert.Equal(2, roots.Count);
        Assert.All(roots, o => Assert.Equal(Head, o.AttachmentPoint));
        Near(new Vector3(0, 0, 0.2f), w.Attachment(roots[1].Id)!.Position);
    }

    [Fact]
    public void A_root_states_its_own_pose_in_the_points_frame()
    {
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyObject(Sim, Worn(601, Wearer, Chest, new Vector3(0.1f, 0.2f, 0.3f), Yaw90), eventIsAttachment: true);

        var a = w.Attachment(new ObjectId(w.Epoch, 601))!;
        Assert.Equal(Wearer, a.WearerLocalId);
        Assert.Equal(G(9000 + Wearer), a.WearerAgentId);
        Assert.Equal(Chest, a.Point);
        Assert.Equal(new ObjectId(w.Epoch, 601), a.Root);
        Near(new Vector3(0.1f, 0.2f, 0.3f), a.Position);
        Assert.True(Quaternion.Dot(Yaw90, a.Rotation) > 0.99999f);
    }

    [Fact]
    public void A_linked_child_is_carried_through_its_root_to_the_points_frame()
    {
        // root: (0.1, 0, 0) turned 90 degrees about up; child: (0.2, 0, 0) in the root's frame, turned another 90.
        // The child's offset (0.2, 0, 0) turned by the root's 90 degrees is (0, 0.2, 0); added to the root's (0.1, 0, 0).
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyObject(Sim, Worn(601, Wearer, Head, new Vector3(0.1f, 0, 0), Yaw90), eventIsAttachment: true);
        w.ApplyObject(Sim, Child(602, 601, Head, new Vector3(0.2f, 0, 0), Yaw90), eventIsAttachment: true);

        var a = w.Attachment(new ObjectId(w.Epoch, 602))!;
        Assert.Equal(new ObjectId(w.Epoch, 601), a.Root);
        Assert.Equal(Wearer, a.WearerLocalId);
        Assert.Equal(Head, a.Point);
        Near(new Vector3(0.1f, 0.2f, 0), a.Position);
        var yaw180 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);
        Assert.True(MathF.Abs(Quaternion.Dot(yaw180, a.Rotation)) > 0.99999f);
    }

    [Fact]
    public void A_world_prim_is_not_an_attachment()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, new PrimData(700, G(700), 0, 0, new Vector3(5, 5, 5), Quaternion.Identity, Vector3.One));
        w.ApplyObject(Sim, new PrimData(701, G(701), 700, 0, Vector3.One, Quaternion.Identity, Vector3.One));
        Assert.Null(w.Attachment(new ObjectId(w.Epoch, 700)));
        Assert.Null(w.Attachment(new ObjectId(w.Epoch, 701)));
    }

    // ---------------- arrival order ----------------

    [Fact]
    public void An_attachment_that_arrives_before_its_avatar_is_held_and_placed_when_the_avatar_arrives()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Worn(601, Wearer, Head, new Vector3(0, 0, 0.1f)), eventIsAttachment: true);
        w.ApplyObject(Sim, Child(602, 601, Head, new Vector3(0.1f, 0, 0)), eventIsAttachment: true);
        Assert.Empty(w.AttachmentRoots(Wearer));
        Assert.Null(w.Attachment(new ObjectId(w.Epoch, 601)));
        Assert.Null(w.Attachment(new ObjectId(w.Epoch, 602)));

        w.ApplyAvatar(Sim, Av(Wearer));

        Assert.Equal(601u, Assert.Single(w.AttachmentRoots(Wearer)).Id.LocalId);
        var root = w.Attachment(new ObjectId(w.Epoch, 601))!;
        var child = w.Attachment(new ObjectId(w.Epoch, 602))!;
        Near(new Vector3(0, 0, 0.1f), root.Position);
        Near(new Vector3(0.1f, 0, 0.1f), child.Position);
    }

    [Fact]
    public void A_child_that_arrives_before_its_root_leaves_nothing_waiting()
    {
        // the child names the root as its parent, not an avatar: when the root arrives (a prim), nothing waits for it
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyObject(Sim, Child(602, 601, Head, new Vector3(0.1f, 0, 0)), eventIsAttachment: true);
        w.ApplyObject(Sim, Worn(601, Wearer, Head), eventIsAttachment: true);

        Assert.Equal(0, w.PendingAttachmentRoots);
        Assert.NotNull(w.Attachment(new ObjectId(w.Epoch, 602)));
    }

    [Fact]
    public void An_avatar_killed_before_it_arrived_takes_the_attachments_waiting_for_it()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Worn(601, Wearer, Head), eventIsAttachment: true);
        w.ApplyObject(Sim, Child(602, 601, Head, Vector3.One), eventIsAttachment: true);
        w.Kill(Sim, [Wearer]);

        Assert.Null(w.Object(new ObjectId(w.Epoch, 601)));
        Assert.Null(w.Object(new ObjectId(w.Epoch, 602)));
        Assert.Equal(0, w.PendingAttachmentRoots);
    }

    // ---------------- leaving, detaching ----------------

    [Fact]
    public void An_avatar_that_leaves_takes_its_attachments_and_their_linked_children_with_it()
    {
        var w = NewWorld();
        var changes = new List<WorldChange>();
        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyAvatar(Sim, Av(OtherWearer));
        w.ApplyObject(Sim, Worn(601, Wearer, Head), eventIsAttachment: true);
        w.ApplyObject(Sim, Child(602, 601, Head, Vector3.One), eventIsAttachment: true);
        w.ApplyObject(Sim, Child(603, 601, Head, Vector3.One), eventIsAttachment: true);
        w.ApplyObject(Sim, Worn(604, Wearer, LeftHand), eventIsAttachment: true);
        w.ApplyObject(Sim, Worn(605, OtherWearer, Head), eventIsAttachment: true);
        w.ApplyObject(Sim, new PrimData(700, G(700), 0, 0, new Vector3(5, 5, 5), Quaternion.Identity, Vector3.One));
        w.Changed += changes.Add;

        w.Kill(Sim, [Wearer]);

        foreach (uint gone in new uint[] { 601, 602, 603, 604 })
            Assert.Null(w.Object(new ObjectId(w.Epoch, gone)));
        Assert.NotNull(w.Object(new ObjectId(w.Epoch, 605)));   // another avatar's, and a world prim, stay
        Assert.NotNull(w.Object(new ObjectId(w.Epoch, 700)));
        Assert.Empty(w.AttachmentRoots(Wearer));
        Assert.Equal(4, changes.Count(c => c.Kind == WorldChangeKind.ObjectRemoved));
        Assert.Equal(1, changes.Count(c => c.Kind == WorldChangeKind.AvatarRemoved));
        Assert.Equal([601u, 602u, 603u, 604u], changes.Where(c => c.Kind == WorldChangeKind.ObjectRemoved).Select(c => c.Id.LocalId).Order());
    }

    [Fact]
    public void An_avatar_that_comes_back_gets_its_attachments_again()
    {
        // the attachments left with the avatar without being killed themselves, so their updates are not refused when they return
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyObject(Sim, Worn(601, Wearer, Head), eventIsAttachment: true);
        w.Kill(Sim, [Wearer]);

        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyObject(Sim, Worn(601, Wearer, Head), eventIsAttachment: true);

        Assert.Equal(601u, Assert.Single(w.AttachmentRoots(Wearer)).Id.LocalId);
    }

    [Fact]
    public void A_detach_to_the_ground_removes_the_attachment_and_its_children_stay_with_the_object()
    {
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyObject(Sim, Worn(601, Wearer, Head), eventIsAttachment: true);
        w.ApplyObject(Sim, Child(602, 601, Head, Vector3.One), eventIsAttachment: true);

        // dropped: the simulator now states it with no parent, no point, a place in the region
        w.ApplyObject(Sim, new PrimData(601, G(601), 0, 0, new Vector3(60, 60, 25), Quaternion.Identity, Vector3.One));
        w.ApplyObject(Sim, new PrimData(602, G(602), 601, 0, Vector3.One, Quaternion.Identity, Vector3.One));

        Assert.Empty(w.AttachmentRoots(Wearer));
        Assert.Null(w.Attachment(new ObjectId(w.Epoch, 601)));
        Assert.Null(w.Attachment(new ObjectId(w.Epoch, 602)));
    }

    [Fact]
    public void A_killed_attachment_is_removed_and_its_wearer_stays()
    {
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyObject(Sim, Worn(601, Wearer, Head), eventIsAttachment: true);
        w.Kill(Sim, [601]);

        Assert.Empty(w.AttachmentRoots(Wearer));
        Assert.NotNull(w.Avatar(new ObjectId(w.Epoch, Wearer)));
    }

    [Fact]
    public void An_attachment_moved_to_another_point_or_another_wearer_is_updated()
    {
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyAvatar(Sim, Av(OtherWearer));
        w.ApplyObject(Sim, Worn(601, Wearer, Head), eventIsAttachment: true);

        w.ApplyObject(Sim, Worn(601, Wearer, LeftHand), eventIsAttachment: true);
        Assert.Equal(LeftHand, w.Attachment(new ObjectId(w.Epoch, 601))!.Point);

        w.ApplyObject(Sim, Worn(601, OtherWearer, LeftHand), eventIsAttachment: true);
        Assert.Empty(w.AttachmentRoots(Wearer));
        Assert.Equal(OtherWearer, w.Attachment(new ObjectId(w.Epoch, 601))!.WearerLocalId);
    }

    [Fact]
    public void A_region_change_drops_every_attachment()
    {
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyObject(Sim, Worn(601, Wearer, Head), eventIsAttachment: true);
        var next = new SimRef(257000UL << 32 | 256000UL, "There");
        w.BeginRegion(next, Region with { Name = "There" });

        Assert.Empty(w.AttachmentRoots(Wearer));
        Assert.Null(w.Attachment(new ObjectId(w.Epoch - 1, 601)));
    }

    [Fact]
    public void An_attachment_that_names_its_point_with_the_add_flag_is_held_on_that_point()
    {
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyObject(Sim, Worn(601, Wearer, Head | 0x80), eventIsAttachment: true);

        Assert.Equal(Head, Assert.Single(w.AttachmentRoots(Wearer)).AttachmentPoint);
        Assert.Equal(Head, w.Attachment(new ObjectId(w.Epoch, 601))!.Point);
    }

    // ---------------- HUD ----------------

    [Fact]
    public void A_hud_attachment_is_not_held()
    {
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyObject(Sim, Worn(601, Wearer, Hud), eventIsAttachment: true);
        w.ApplyObject(Sim, Child(602, 601, Hud, Vector3.One), eventIsAttachment: true);

        Assert.Empty(w.AttachmentRoots(Wearer));
        Assert.Empty(w.Snapshot().Objects);
        Assert.Equal(2, w.Dropped(WorldDrop.Hud));
    }

    [Fact]
    public void A_hud_point_with_the_add_flag_is_not_held_either()
    {
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(Wearer));
        w.ApplyObject(Sim, Worn(601, Wearer, Hud | 0x80), eventIsAttachment: true);

        Assert.Empty(w.Snapshot().Objects);
        Assert.Equal(1, w.Dropped(WorldDrop.Hud));
    }
}
