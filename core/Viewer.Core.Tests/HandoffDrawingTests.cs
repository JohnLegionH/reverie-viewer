// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What is drawn when an object changes region: the region left keeps drawing it until the region entered draws it (make before break),
/// as the official viewer's one drawable per object is never missing from a frame (llviewerobject.cpp setRegion, lldrawable.cpp movePartition).
/// </summary>
public class HandoffDrawingTests
{
    private static readonly Guid Avatar = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid PieceA = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid PieceB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");

    [Fact]
    public void An_object_is_held_until_the_region_entered_draws_it()
    {
        double now = 0;
        var ledger = new HandoffLedger(() => now);
        ledger.Hold(PieceA, piece: true);
        Assert.False(ledger.TryRelease(PieceA));
        ledger.Entered(PieceA);
        Assert.True(ledger.TryRelease(PieceA));
        Assert.Equal(0, ledger.Held);
    }

    [Fact]
    public void An_object_the_region_entered_has_not_drawn_yet_stays_held_however_long_it_takes()
    {
        double now = 0;
        var ledger = new HandoffLedger(() => now);
        ledger.Hold(PieceA, piece: true);
        now = 3600;   // a mesh that takes a minute to arrive, or an hour
        Assert.False(ledger.TryRelease(PieceA));
    }

    [Fact]
    public void An_object_that_no_region_has_any_more_is_let_go_of()
    {
        bool exists = true;
        var ledger = new HandoffLedger(() => 0) { Exists = _ => exists };
        ledger.Hold(PieceA, piece: true);
        Assert.False(ledger.TryRelease(PieceA));
        exists = false;   // killed, or taken off, in the region that had it
        Assert.True(ledger.TryRelease(PieceA));
    }

    [Fact]
    public void A_piece_that_came_back_to_the_region_that_held_it_no_longer_blocks_the_avatar()
    {
        var ledger = new HandoffLedger(() => 0);
        ledger.Hold(Avatar, piece: false);
        ledger.Hold(PieceA, piece: true);
        ledger.Entered(Avatar);
        Assert.False(ledger.TryRelease(Avatar));
        ledger.Forget(PieceA);   // the piece is drawn by this region again
        Assert.True(ledger.TryRelease(Avatar));
    }

    [Fact]
    public void An_avatar_the_world_announces_again_is_no_longer_held()
    {
        var (scene, from, to, hub, changes) = Rig(hold: true);
        foreach (var c in changes) scene.Apply(c);
        var id = Assert.Single(scene.Held).Id;
        hub.Current = from;   // the agent came back and the region sends the avatar again, under the id it used before
        var back = new List<WorldChange>();
        from.Changed += back.Add;
        from.ApplyAvatar(SimFrom, new AvatarData(id.LocalId, Avatar, "Test User", new(250, 128, 21), Quaternion.Identity, new(0.45f, 0.6f, 1.9f), default, 0));
        foreach (var c in back) scene.Apply(c);
        Assert.Empty(scene.Held);
        Assert.False(scene.Release(id));   // nothing to let go of: the avatar is a live one again
        Assert.Equal(1, scene.Count);
    }

    [Fact]
    public void A_held_avatar_is_drawn_where_its_agent_is_now()
    {
        double clock = 0;
        var (scene, _, to, hub, changes) = Rig(hold: true, () => clock);
        foreach (var c in changes) scene.Apply(c);
        var held = Assert.Single(scene.Held);
        var now = hub.CurrentAvatar(Avatar);
        Assert.NotNull(now);
        Assert.Equal(to.AvatarOf(Avatar)!.Position, now!.Position);
        scene.Follow(held, now with { Position = new System.Numerics.Vector3(700, 300, 40) });
        clock = 30;   // the smoothing of a moved avatar has run its course
        Assert.True(scene.Place(held, out var soles, out _));
        Assert.True(System.Numerics.Vector3.Distance(new(700, 300, 40), soles) < 3f, $"drawn at {soles}");
    }

    [Fact]
    public void The_hub_knows_whether_an_object_still_exists_in_some_region()
    {
        var (_, from, to, hub, _) = Rig(hold: true);
        Assert.True(hub.Holds(Avatar));
        Assert.Equal(to.AvatarOf(Avatar)!.Id, hub.CurrentAvatar(Avatar)!.Id);
        Assert.False(hub.Holds(Guid.NewGuid()));
    }
    [Fact]
    public void The_avatar_is_held_while_a_worn_piece_is_still_held()
    {
        double now = 0;
        var ledger = new HandoffLedger(() => now);
        ledger.Hold(Avatar, piece: false);
        ledger.Hold(PieceA, piece: true);
        ledger.Hold(PieceB, piece: true);
        ledger.Entered(Avatar);
        ledger.Entered(PieceA);
        Assert.True(ledger.TryRelease(PieceA));
        Assert.False(ledger.TryRelease(Avatar));   // PieceB is still drawn by the region left, on this avatar
        ledger.Entered(PieceB);
        Assert.True(ledger.TryRelease(PieceB));
        Assert.True(ledger.TryRelease(Avatar));
    }

    [Fact]
    public void A_region_entered_that_drew_it_first_lets_the_hold_go_at_once()
    {
        double now = 0;
        var ledger = new HandoffLedger(() => now);
        ledger.Entered(PieceA);
        ledger.Hold(PieceA, piece: true);
        Assert.True(ledger.TryRelease(PieceA));
    }

    [Fact]
    public void A_mark_from_an_earlier_crossing_does_not_release_the_next_one()
    {
        double now = 0;
        var ledger = new HandoffLedger(() => now);
        ledger.Entered(PieceA);
        ledger.Expect(PieceA);   // the object is given to a region again
        ledger.Hold(PieceA, piece: true);
        Assert.False(ledger.TryRelease(PieceA));
    }

    [Fact]
    public void An_object_that_is_not_held_is_let_go_of()
    {
        var ledger = new HandoffLedger(() => 0);
        Assert.True(ledger.TryRelease(PieceA));
    }

    private static readonly SimRef SimFrom = new(256000UL << 32 | 256000UL, "From");

    private static (AvatarScene Scene, WorldState From, WorldState To, IdentityHub Hub, List<WorldChange> FromChanges) Rig(bool hold, Func<double>? clock = null)
    {
        var hub = new IdentityHub { SelfAgent = Avatar };
        var from = new WorldState(new CoreLog(new ListSink(), "test"));
        var to = new WorldState(new CoreLog(new ListSink(), "test"));
        from.AttachIdentity(hub);
        to.AttachIdentity(hub);
        var simFrom = SimFrom;
        var simTo = new SimRef(256256UL << 32 | 256000UL, "To");
        from.BeginRegion(simFrom, new RegionInfo("from", 1000, 1000, 256, 256, true));
        to.BeginRegion(simTo, new RegionInfo("to", 1001, 1000, 256, 256, true));
        hub.Current = from;
        var scene = new AvatarScene(from, Avatar, clock ?? (() => 0)) { HoldHandoffs = hold };
        var changes = new List<WorldChange>();
        from.Changed += changes.Add;
        from.ApplyAvatar(simFrom, new AvatarData(900, Avatar, "Test User", new(128, 128, 21), Quaternion.Identity, new(0.45f, 0.6f, 1.9f), default, 0));
        foreach (var c in changes) scene.Apply(c);
        changes.Clear();
        hub.Current = to;
        to.ApplyAvatar(simTo, new AvatarData(1900, Avatar, "Test User", new(2, 128, 21), Quaternion.Identity, new(0.45f, 0.6f, 1.9f), default, 0));
        return (scene, from, to, hub, changes);
    }

    [Fact]
    public void A_scene_that_holds_handoffs_keeps_the_avatar_another_region_took_over_until_it_is_released()
    {
        var (scene, _, _, _, changes) = Rig(hold: true);
        Assert.Contains(changes, c => c.Kind == WorldChangeKind.AvatarRemoved && c.Handoff);
        foreach (var c in changes) scene.Apply(c);
        Assert.Equal(1, scene.Count);
        Assert.Single(scene.Held);
        Assert.False(scene.TakeDeparted(out _));
        Assert.True(scene.Release(scene.Held.First().Id));
        Assert.Equal(0, scene.Count);
        Assert.True(scene.TakeDeparted(out _));
        Assert.Empty(scene.Held);
    }

    [Fact]
    public void A_scene_that_does_not_hold_handoffs_lets_the_avatar_go_at_once()
    {
        var (scene, _, _, _, changes) = Rig(hold: false);
        foreach (var c in changes) scene.Apply(c);
        Assert.Equal(0, scene.Count);
        Assert.True(scene.TakeDeparted(out _));
    }

    [Fact]
    public void A_removal_that_is_not_a_handoff_is_never_held()
    {
        var (scene, from, _, _, _) = Rig(hold: true);
        var id = new ObjectId(from.Epoch, 900);
        scene.Apply(new WorldChange(WorldChangeKind.AvatarRemoved, id));
        Assert.Equal(0, scene.Count);
        Assert.Empty(scene.Held);
    }
}
