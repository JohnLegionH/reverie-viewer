// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Assets;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Avatars as the live viewer draws them, without the engine: the animations the region reports, kept per region; each
/// avatar's drawn position smoothed between the region's updates; the scene's arrivals, updates and departures (by
/// removal and by region change) driven by updates replayed through a session; the one animation chosen; the bound on
/// animation fetches; and the follow camera behind the user's own avatar.
/// </summary>
public class AvatarLiveTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly SimRef There = new(256256UL << 32 | 256000UL, "There");
    private static readonly Guid Other = Guid.Parse("0c4f0c4f-1111-4222-8333-944444444444");
    private static readonly Guid Seq1 = Guid.Parse("a1a1a1a1-0000-4000-8000-000000000001"), Seq2 = Guid.Parse("a1a1a1a1-0000-4000-8000-000000000002");

    private static AvatarData Av(uint local, Guid agent, Vector3 pos, Vector3 vel = default, uint seat = 0, string name = "Test User") =>
        new(local, agent, name, pos, Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), vel, seat);

    // ---------------- the world state ----------------

    [Fact]
    public void The_regions_animation_list_is_kept_per_avatar_and_scoped_to_the_region()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var changes = new List<WorldChange>();
        s.World.Changed += changes.Add;
        t.Raise(new AvatarUpdated(Here, Av(900, Other, new(128, 128, 25))));
        t.Raise(new AvatarAnimationsReceived(Here, Other, [new(AnimationChoice.Walk, 7)]));
        Assert.Equal([new PlayingAnimation(AnimationChoice.Walk, 7)], s.World.Animations(Other));
        var c = Assert.Single(changes, x => x.Kind == WorldChangeKind.AnimationsChanged);
        Assert.Equal(Other, c.AgentId);
        Assert.Equal(900u, c.Id.LocalId);

        // the same list again raises nothing; an empty list is kept as empty
        t.Raise(new AvatarAnimationsReceived(Here, Other, [new(AnimationChoice.Walk, 7)]));
        Assert.Single(changes, x => x.Kind == WorldChangeKind.AnimationsChanged);
        t.Raise(new AvatarAnimationsReceived(Here, Other, []));
        Assert.Empty(s.World.Animations(Other));

        // a list may come before the avatar's own update; the region change drops it; a late one from the old region is refused
        t.Raise(new AvatarAnimationsReceived(Here, Guid.NewGuid(), [new(AnimationChoice.Stand, 1)]));
        t.Raise(new AvatarAnimationsReceived(Here, Other, [new(AnimationChoice.Stand, 8)]));
        t.CurrentSim = There;
        t.Raise(new SimConnected(There, 256, 256, true));
        Assert.Empty(s.World.Animations(Other));
        s.World.ApplyAnimations(Here, Other, [new(AnimationChoice.Stand, 9)]);
        Assert.Empty(s.World.Animations(Other));
    }

    [Fact]
    public void A_seated_avatar_keeps_its_seat_and_a_capture_round_trips_animations_and_seats_with_the_agent_aliased()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        t.Raise(new AvatarUpdated(Here, Av(900, Other, new(0, 0, 0.5f), seat: 4242)));
        Assert.Equal(4242u, s.World.Avatar(new ObjectId(s.World.Epoch, 900))!.ParentLocalId);

        string path = TestTemp.NewFilePath("avatar-capture", ".jsonl.gz");
        var aliases = new CaptureFile.Aliases(Guid.Empty);
        using (var w = new CaptureFile.Writer(path, new CaptureHeader(CaptureFile.FormatVersion, "avatars", Guid.Empty, 0, 0, "test")))
        {
            w.Write(new CaptureRecord(1, Here.Handle, 1, CaptureFile.Scrub(new AvatarUpdated(Here, Av(900, Other, new(0, 0, 0.5f), seat: 4242, name: "Real Name")), aliases)));
            w.Write(new CaptureRecord(2, Here.Handle, 1, CaptureFile.Scrub(new AvatarAnimationsReceived(Here, Other, [new(Seq1, 3), new(Seq2, 4)]), aliases)));
        }
        var c = CaptureFile.Read(path);
        var av = Assert.IsType<AvatarUpdated>(c.Records[0].Event);
        var an = Assert.IsType<AvatarAnimationsReceived>(c.Records[1].Event);
        Assert.Equal(4242u, av.Avatar.ParentLocalId);
        Assert.Equal(av.Avatar.AgentId, an.AgentId);   // the same alias in both
        Assert.NotEqual(Other, an.AgentId);
        Assert.Equal([new PlayingAnimation(Seq1, 3), new PlayingAnimation(Seq2, 4)], an.Playing);
        Assert.DoesNotContain("Real Name", CaptureFile.ReadText(path));
        TestTemp.Delete(path);
    }

    [Fact]
    public void The_adapter_reads_an_avatar_animation_packet_with_every_entry()
    {
        var agent = LibreMetaverse.UUID.Random();
        var p = new LibreMetaverse.Packets.AvatarAnimationPacket
        {
            Sender = new() { ID = agent },
            AnimationList =
            [
                new() { AnimID = new LibreMetaverse.UUID(AnimationChoice.Walk), AnimSequenceID = 5 },
                new() { AnimID = new LibreMetaverse.UUID(Seq1), AnimSequenceID = 6 },
            ],
        };
        var e = Viewer.Core.Protocol.LmvTransport.ToAnimations(Here, p);
        Assert.Same(Here, e.Sim);
        Assert.Equal(agent.Guid, e.AgentId);
        Assert.Equal([new PlayingAnimation(AnimationChoice.Walk, 5), new PlayingAnimation(Seq1, 6)], e.Playing);
        var a = new LibreMetaverse.Avatar { LocalID = 7, ID = agent, ParentID = 31 };
        Assert.Equal(31u, Viewer.Core.Protocol.LmvTransport.ToAvatarData(a).ParentLocalId);
    }

    // ---------------- smoothing ----------------

    [Fact]
    public void Between_updates_an_avatar_is_carried_along_its_velocity_for_two_seconds_then_slows_to_a_stop_at_three()
    {
        var m = new AvatarMotion();
        m.Update(new(10, 10, 20), new(2, 0, 0), Quaternion.Identity, 0);
        Assert.Equal(new Vector3(10, 10, 20), m.At(0).Position);
        Assert.Equal(11f, m.At(0.5).Position.X, 4);
        Assert.Equal(12f, m.At(1.0).Position.X, 4);
        Assert.Equal(14f, m.At(2.0).Position.X, 4);   // full speed for two seconds
        Assert.Equal(14.75f, m.At(2.5).Position.X, 4);   // then slowing: half a second at an average of three quarters speed
        Assert.Equal(15f, m.At(3.0).Position.X, 4);   // stopped at three seconds
        Assert.Equal(15f, m.At(5.0).Position.X, 4);   // updates stopped: it waits where it was carried to, no drift
    }

    [Fact]
    public void An_avatar_walking_between_the_regions_five_metre_updates_never_stalls_or_jumps()
    {
        // the region sends a moving avatar's position again only when its velocity changes or it has gone 5 m from the
        // last one it sent: at a walk of 4 m/s that is an update every 1.25 s
        var m = new AvatarMotion();
        const float speed = 4f;
        double next = 0;
        float x = 10;
        Vector3? prev = null;
        float least = float.MaxValue, most = 0;
        for (int frame = 0; frame <= 600; frame++)   // ten seconds at 60 frames a second
        {
            double now = frame / 60.0;
            if (now >= next - 1e-9)
            {
                m.Update(new(x, 10, 20), new(speed, 0, 0), Quaternion.Identity, now);
                x += 5;
                next += 5 / speed;
            }
            var p = m.At(now).Position;
            if (prev is { } q && frame > 1)
            {
                least = MathF.Min(least, p.X - q.X);
                most = MathF.Max(most, p.X - q.X);
            }
            prev = p;
        }
        float step = speed / 60;
        Assert.True(least > 0.9f * step, $"slowest frame step {least:F4} m against {step:F4} m: the avatar stalled between updates");
        Assert.True(most < 1.1f * step, $"fastest frame step {most:F4} m against {step:F4} m: the avatar jumped");
    }

    [Fact]
    public void A_correction_is_blended_out_with_no_jump_and_a_far_one_is_taken_at_once()
    {
        var m = new AvatarMotion();
        m.Update(new(10, 10, 20), new(2, 0, 0), Quaternion.Identity, 0);
        double t = 0.25;
        var before = m.At(t).Position;                       // carried to x 10.5
        m.Update(new(10.8f, 10, 20), new(2, 0, 0), Quaternion.Identity, t);   // the region says 10.8
        Assert.Equal(before, m.At(t).Position);              // no jump at the update
        Assert.True(m.At(t + AvatarMotion.CorrectionSeconds / 2).Position.X is > 10.8f and < 11.15f);
        Assert.Equal(10.8f + 2f * (float)AvatarMotion.CorrectionSeconds, m.At(t + AvatarMotion.CorrectionSeconds).Position.X, 4);   // fully on the region's track

        // the rotation turns over TurnSeconds, not at once
        var face = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        m.Update(m.At(1).Position, Vector3.Zero, face, 1);
        Assert.Equal(0f, AvatarMotion.Yaw(m.At(1).Rotation), 3);
        Assert.Equal(MathF.PI / 4, AvatarMotion.Yaw(m.At(1 + AvatarMotion.TurnSeconds / 2).Rotation), 2);
        Assert.Equal(MathF.PI / 2, AvatarMotion.Yaw(m.At(1 + AvatarMotion.TurnSeconds).Rotation), 3);

        // a correction over the snap distance (a teleport within the region) is taken at once
        int snaps = m.Snaps;
        m.Update(new(200, 200, 30), Vector3.Zero, face, 2);
        Assert.Equal(new Vector3(200, 200, 30), m.At(2).Position);
        Assert.Equal(snaps + 1, m.Snaps);
    }

    // ---------------- the scene ----------------

    private sealed class Clock { public double Now; }

    private static (ViewerSession S, FakeTransport T, AvatarScene Scene, Clock Clock, List<WorldChange> Changes) Scene()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var clock = new Clock();
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => clock.Now);
        var changes = new List<WorldChange>();
        s.World.Changed += changes.Add;   // the coordinator's queue, drained by Pump below
        return (s, t, scene, clock, changes);
    }

    private static void Pump(AvatarScene scene, List<WorldChange> changes)
    {
        foreach (var c in changes) scene.Apply(c);
        changes.Clear();
    }

    private static List<AvatarEntry> Departed(AvatarScene scene)
    {
        var l = new List<AvatarEntry>();
        while (scene.TakeDeparted(out var e)) l.Add(e);
        return l;
    }

    [Fact]
    public void An_avatar_is_found_by_the_local_id_its_attachments_name_as_their_parent()
    {
        var (s, t, scene, _, changes) = Scene();
        t.Raise(new AvatarUpdated(Here, Av(800, ServerAppearanceTests.Me, new(100, 100, 21.95f))));
        t.Raise(new AvatarUpdated(Here, Av(900, Other, new(110, 100, 21.95f))));
        Pump(scene, changes);
        Assert.Equal(Other, scene.ByLocalId(900)!.AgentId);
        Assert.True(scene.ByLocalId(800)!.IsSelf);
        Assert.Null(scene.ByLocalId(901));

        t.Raise(new ObjectsKilled(Here, [900]));
        Pump(scene, changes);
        Assert.Null(scene.ByLocalId(900));
    }

    [Fact]
    public void Avatars_arrive_move_smoothly_and_depart_and_a_region_change_leaves_none()
    {
        var (s, t, scene, clock, changes) = Scene();
        t.Raise(new AvatarUpdated(Here, Av(800, ServerAppearanceTests.Me, new(100, 100, 21.95f), name: "Own Avatar")));
        t.Raise(new AvatarUpdated(Here, Av(900, Other, new(110, 100, 21.95f), new(1, 0, 0))));
        Pump(scene, changes);
        Assert.Equal(2, scene.Count);
        Assert.Equal(2, scene.Arrivals);
        Assert.True(scene.Self!.IsSelf);
        Assert.Equal("Own Avatar", scene.Self.Name);
        var other = scene.Entries.Single(e => !e.IsSelf);
        Assert.True(scene.Place(other, out var soles, out _));
        // the region places the centre: the soles are half the height lower; drawn where the region states it, as the official viewer
        // draws any avatar (LLVOAvatar::idleUpdate: the object path, an update taken where it says)
        Assert.Equal(new Vector3(110, 100, 21.95f - 0.95f), soles);

        // between updates it is carried along its velocity, what is drawn following the object with the drawable's damping
        clock.Now = 0.5;
        scene.Place(other, out var carried, out _);
        Assert.InRange(carried.X, 110.49f, 110.5f);
        // a terse update sets the object at once; what is drawn follows it a frame at a time (a 0.2 m correction is damped, not snapped)
        clock.Now = 0.5 + 1.0 / 60;
        t.Raise(new ObjectMoved(Here, 900, true, new(110.7f, 100, 21.95f), Quaternion.Identity, new(1, 0, 0), Vector3.Zero, Vector3.Zero));
        Pump(scene, changes);
        scene.Place(other, out var after, out _);
        Assert.InRange(after.X, carried.X + 0.01f, 110.7f - 0.01f);
        Assert.Equal(2, other.Motion.Updates);

        // the region removes it: it departs, once
        t.Raise(new ObjectsKilled(Here, [900]));
        Pump(scene, changes);
        Assert.Equal(1, scene.Count);
        Assert.Same(other, Assert.Single(Departed(scene)));

        // a late update for it from the old epoch after a region change does not bring it back; the region change takes the rest
        t.CurrentSim = There;
        t.Raise(new SimConnected(There, 256, 256, true));
        Pump(scene, changes);
        Assert.Equal(0, scene.Count);
        Assert.Single(Departed(scene), e => e.IsSelf);
        scene.Apply(new WorldChange(WorldChangeKind.AvatarAdded, new ObjectId(s.World.Epoch - 1, 900)));
        Assert.Equal(0, scene.Count);
        Assert.Equal(scene.Arrivals, scene.Departures);   // nothing left behind

        // the new region's avatars arrive afresh
        t.Raise(new AvatarUpdated(There, Av(901, Other, new(10, 10, 30))));
        Pump(scene, changes);
        Assert.Equal(1, scene.Count);
        Assert.Equal(s.World.Epoch, scene.Entries.Single().Id.Epoch);
    }

    [Fact]
    public void Bakes_and_animations_reach_each_avatar_in_either_order()
    {
        var (_, t, scene, _, changes) = Scene();
        var head = Guid.NewGuid();
        // the appearance and the animations before the avatar's own update
        t.Raise(new AvatarAppearanceReceived(Here, ServerAppearanceTests.Look(Other, 1, (8, head))));
        t.Raise(new AvatarAnimationsReceived(Here, Other, [new(AnimationChoice.Stand, 1)]));
        t.Raise(new AvatarUpdated(Here, Av(900, Other, new(110, 100, 22))));
        Pump(scene, changes);
        var e = Assert.Single(scene.Entries);
        Assert.Equal(head, e.Bakes["head"]);
        Assert.Equal(AnimationChoice.Stand, Assert.Single(e.Playing).AnimationId);
        int bv = e.BakesVersion, av = e.AnimationsVersion;

        // and after it: each change counted once, a repeat not at all
        var upper = Guid.NewGuid();
        t.Raise(new AvatarAppearanceReceived(Here, ServerAppearanceTests.Look(Other, 2, (8, head), (9, upper))));
        t.Raise(new AvatarAnimationsReceived(Here, Other, [new(AnimationChoice.Walk, 2)]));
        t.Raise(new AvatarAnimationsReceived(Here, Other, [new(AnimationChoice.Walk, 2)]));
        Pump(scene, changes);
        Assert.Equal(bv + 1, e.BakesVersion);
        Assert.Equal(upper, e.Bakes["upper"]);
        Assert.Equal(av + 1, e.AnimationsVersion);
        Assert.Equal(AnimationChoice.Walk, Assert.Single(e.Playing).AnimationId);
    }

    [Fact]
    public void A_local_preview_joins_the_own_avatars_animations_at_once_and_leaves_others_alone()
    {
        var (_, t, scene, _, changes) = Scene();
        t.Raise(new AvatarUpdated(Here, Av(800, ServerAppearanceTests.Me, new(100, 100, 21.95f))));
        t.Raise(new AvatarUpdated(Here, Av(900, Other, new(110, 100, 22))));
        t.Raise(new AvatarAnimationsReceived(Here, ServerAppearanceTests.Me, [new(AnimationChoice.Stand, 1)]));
        t.Raise(new AvatarAnimationsReceived(Here, Other, [new(AnimationChoice.Stand, 1)]));
        Pump(scene, changes);
        var preview = Guid.NewGuid();
        int before = scene.Self!.AnimationsVersion;

        scene.LocalPreview = [preview];

        Assert.Equal([AnimationChoice.Stand, preview], scene.Self.Playing.Select(p => p.AnimationId));
        Assert.Equal(before + 1, scene.Self.AnimationsVersion);
        Assert.Equal([AnimationChoice.Stand], scene.Entries.Single(e => !e.IsSelf).Playing.Select(p => p.AnimationId));
        t.Raise(new AvatarAnimationsReceived(Here, ServerAppearanceTests.Me, [new(AnimationChoice.Walk, 2)]));
        Pump(scene, changes);
        Assert.Equal([AnimationChoice.Walk, preview], scene.Self.Playing.Select(p => p.AnimationId));   // kept across the region's updates

        scene.LocalPreview = [];
        Assert.Equal([AnimationChoice.Walk], scene.Self.Playing.Select(p => p.AnimationId));
    }

    [Fact]
    public void A_seated_avatar_is_placed_on_its_seat_and_hidden_while_the_seat_is_unknown()
    {
        var (s, t, scene, _, changes) = Scene();
        t.Raise(new AvatarUpdated(Here, Av(900, Other, new(0, 0, 0.95f), seat: 77)));
        Pump(scene, changes);
        var e = Assert.Single(scene.Entries);
        Assert.False(scene.Place(e, out _, out _));   // the seat is not known yet

        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        s.World.ApplyObject(Here, new PrimData(70, Guid.NewGuid(), 0, 0, new(50, 60, 20), turned, Vector3.One));
        s.World.ApplyObject(Here, new PrimData(77, Guid.NewGuid(), 70, 0, new(2, 0, 0), Quaternion.Identity, Vector3.One));
        Assert.True(scene.Place(e, out var soles, out float yaw));
        Assert.Equal(50f, soles.X, 4);   // the child seat 2 m along the root's turned X axis: north of the root
        Assert.Equal(62f, soles.Y, 4);
        Assert.Equal(20f, soles.Z, 4);
        Assert.Equal(MathF.PI / 2, yaw, 3);

        // standing up moves it at once into the region's frame (no blend from the seat-relative values)
        t.Raise(new AvatarUpdated(Here, Av(900, Other, new(52, 62, 21))));
        Pump(scene, changes);
        Assert.True(scene.Place(e, out var standing, out _));
        Assert.Equal(new Vector3(52, 62, 21 - 0.95f), standing);
    }

    [Fact]
    public void A_seated_avatar_is_placed_by_its_root_whatever_its_own_body_height_and_a_standing_one_from_the_regions_size()
    {
        // the official seated branch (llvoavatar.cpp updateRootPositionAndRotation) puts the root at the place the region states plus the
        // hover offset; the body's own height plays no part. A standing avatar keeps resting where the region rests it.
        var (s, t, scene, _, changes) = Scene();
        t.Raise(new AvatarUpdated(Here, Av(900, Other, new(0, 0, 1.2f), seat: 77)));
        Pump(scene, changes);
        var e = Assert.Single(scene.Entries);
        s.World.ApplyObject(Here, new PrimData(77, Guid.NewGuid(), 0, 0, new(50, 60, 20), Quaternion.Identity, Vector3.One));
        e.SetPelvisAboveSoles(1.0f);
        e.SetOwnBody(2.3f, 0.2f);
        Assert.True(scene.Place(e, out var tall, out _));
        Assert.Equal(20f + 1.2f + 0.2f - 1.0f, tall.Z, 4);
        e.SetOwnBody(1.6f, 0f);
        Assert.True(scene.Place(e, out var shorter, out _));
        Assert.Equal(20f + 1.2f - 1.0f, shorter.Z, 4);

        t.Raise(new AvatarUpdated(Here, Av(900, Other, new(52, 62, 21))));
        Pump(scene, changes);
        Assert.True(scene.Place(e, out var standing, out _));
        Assert.Equal(21f - 0.95f, standing.Z, 4);
    }

    [Fact]
    public void A_seated_avatar_is_placed_with_its_root_at_the_seat_the_region_states_plus_its_hover()
    {
        // the official seated branch (llvoavatar.cpp updateRootPositionAndRotation): the root is the stated place plus the hover offset;
        // the drawing hangs the body its pelvis-above-soles below the root, so the soles handed to it are the root less that height,
        // whatever the body's own height
        var (s, t, scene, _, changes) = Scene();
        t.Raise(new AvatarUpdated(Here, Av(800, ServerAppearanceTests.Me, new(0, 0, 1.2f), seat: 77)));
        Pump(scene, changes);
        var e = scene.Self!;
        s.World.ApplyObject(Here, new PrimData(77, Guid.NewGuid(), 0, 0, new(50, 60, 20), Quaternion.Identity, Vector3.One));
        e.SetOwnBody(2.085f, 0.2f);
        e.SetPelvisAboveSoles(1.3f);
        Assert.True(scene.Place(e, out var soles, out _));
        Assert.Equal(20f + 1.2f + 0.2f - 1.3f, soles.Z, 4);
        Assert.Equal(50f, soles.X, 4);
        Assert.Equal(60f, soles.Y, 4);
    }

    // ---------------- animations ----------------

    [Fact]
    public void The_highest_priority_animation_at_hand_plays_and_the_stand_when_none_is()
    {
        var priorities = new Dictionary<Guid, int> { [AnimationChoice.Stand] = 0, [AnimationChoice.Walk] = 3, [Seq1] = 3, [Seq2] = 4 };
        int? P(Guid id) => priorities.TryGetValue(id, out var p) ? p : null;
        Assert.Equal(AnimationChoice.Walk, AnimationChoice.Choose([new(AnimationChoice.Stand, 1), new(AnimationChoice.Walk, 2)], P));
        Assert.Equal(Seq2, AnimationChoice.Choose([new(AnimationChoice.Walk, 2), new(Seq2, 1)], P));
        Assert.Equal(Seq1, AnimationChoice.Choose([new(AnimationChoice.Walk, 2), new(Seq1, 3)], P));   // equal priority: the one started last
        Assert.Equal(AnimationChoice.Stand, AnimationChoice.Choose([new(Guid.NewGuid(), 9)], P));        // not at hand: the stand
        Assert.Equal(AnimationChoice.Stand, AnimationChoice.Choose([], P));
        Assert.Null(AnimationChoice.Choose([], _ => null));                                             // no stand either: the default pose
        Assert.Equal([Seq1, AnimationChoice.Stand], AnimationChoice.Wanted([new(Seq1, 1)]));
    }

    [Fact]
    public async Task Animation_fetches_are_bounded_and_each_asset_is_read_once()
    {
        var gates = new System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<AssetFetchResult>>();
        int calls = 0;
        Task<AssetFetchResult> Fetch(Guid id, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return gates.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        using var source = new AnimationSource(Fetch);
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids) Assert.Equal(MeshAvailability.Pending, source.TryGet(id, out _));
        foreach (var id in ids) source.TryGet(id, out _);   // asked again: not fetched again
        await Until(() => Volatile.Read(ref calls) == AnimationSource.DefaultMaxInFlight);
        Assert.Equal(AnimationSource.DefaultMaxInFlight, source.InFlight);
        Assert.Equal(10 - AnimationSource.DefaultMaxInFlight, source.Waiting);

        // complete them one by one (a real clip, a refusal, garbage): each frame's pump starts the next, never more than four
        byte[] clip = MinimalClip();
        int done = 0;
        while (done < ids.Count)
        {
            var open = gates.Where(g => !g.Value.Task.IsCompleted).ToList();
            Assert.True(open.Count <= AnimationSource.DefaultMaxInFlight);
            var (id, gate) = open.First();
            int n = ids.IndexOf(id);
            gate.SetResult(n % 3 == 0 ? new AssetFetchResult(AssetFetchStatus.Ok, clip) : n % 3 == 1 ? new AssetFetchResult(AssetFetchStatus.NotFound, ReadOnlyMemory<byte>.Empty) : new AssetFetchResult(AssetFetchStatus.Ok, new byte[] { 1, 2, 3 }));
            done++;
            await Until(() => source.InFlight < AnimationSource.DefaultMaxInFlight || done == ids.Count);
            source.Pump();
            await Until(() => Volatile.Read(ref calls) == Math.Min(ids.Count, done + AnimationSource.DefaultMaxInFlight));
        }
        await Until(() => source.Ready + source.Unavailable == ids.Count);
        Assert.Equal(ids.Count, calls);
        Assert.Equal(AnimationSource.DefaultMaxInFlight, source.PeakInFlight);
        Assert.Equal(4, source.Ready);   // ids 0, 3, 6, 9
        Assert.Equal(MeshAvailability.Ready, source.TryGet(ids[0], out var c0));
        Assert.Equal(2f, c0!.Duration);
        Assert.Equal(MeshAvailability.Unavailable, source.TryGet(ids[1], out _));
        Assert.Equal(MeshAvailability.Unavailable, source.TryGet(ids[2], out _));
    }

    private static async Task Until(Func<bool> condition)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("condition not reached in 20 s");
            await Task.Delay(20);
        }
    }

    /// <summary>A valid animation asset with no joints: version 1.0, priority 2, 2 s, no emote, looped.</summary>
    internal static byte[] MinimalClip()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)1); w.Write((ushort)0); w.Write(2); w.Write(2f); w.Write((byte)0);
        w.Write(0f); w.Write(2f); w.Write(1); w.Write(0f); w.Write(0f); w.Write(0); w.Write(0); w.Write(0);
        return ms.ToArray();
    }

    // ---------------- the camera ----------------

    [Fact]
    public void The_camera_sits_behind_the_avatar_and_turns_with_it()
    {
        var cam = new FollowCamera();
        var centre = new Vector3(100, 100, 22);
        var (eye, target) = cam.Pose(centre, 0, 0);   // facing east
        Assert.Equal(97f, eye.X, 3);
        Assert.Equal(100f, eye.Y, 3);
        Assert.Equal(23.75f, eye.Z, 3);   // the avatar + the third-person head offset (0, 0, 1) + the rear-view offset (-3, 0, 0.75)
        Assert.Equal(new Vector3(101, 100, 23), target);
        (eye, _) = cam.Pose(centre, MathF.PI / 2, 1);   // facing north (a second later, the smoothing done): the camera is south of it
        Assert.Equal(100f, eye.X, 3);
        Assert.Equal(97f, eye.Y, 3);
    }

    [Fact]
    public void Orbit_and_zoom_move_the_camera_about_the_avatar_within_their_limits()
    {
        var cam = new FollowCamera();
        var centre = new Vector3(100, 100, 22);
        float d0 = FollowCamera.CameraOffset.Length();
        cam.Orbit(MathF.PI / 2, 0);   // a quarter turn about the avatar
        var (eye, _) = cam.Pose(centre, 0, 1);   // a second later: the smoothing is done
        Assert.Equal(100f, eye.X, 3);
        Assert.Equal(97f, eye.Y, 3);
        Assert.Equal(d0, Vector3.Distance(eye, centre + FollowCamera.DefaultHeadOffset), 3);

        cam.Orbit(0, 10f);   // far past the top: held at the limit, the camera above but not overhead
        (eye, _) = cam.Pose(centre, 0, 1);
        float elevation = MathF.Asin((eye.Z - centre.Z - 1f) / d0);
        Assert.Equal(FollowCamera.MaxPitch, elevation, 3);
        cam.Orbit(0, -20f);
        (eye, _) = cam.Pose(centre, 0, 1);
        Assert.Equal(-FollowCamera.MaxPitch, MathF.Asin((eye.Z - centre.Z - 1f) / d0), 3);

        cam.Reset();
        cam.ZoomBy(100);
        Assert.Equal(FollowCamera.MaxZoom, cam.Zoom);
        Assert.Equal(d0 * FollowCamera.MaxZoom, cam.TargetDistance, 3);   // 24.7 m: under the 50 m cap
        cam.ZoomBy(1e-6f);
        Assert.Equal(FollowCamera.MinZoom, cam.Zoom);

        // a new zoom eases in: halfway after one half-life, there after many
        var eased = new FollowCamera();
        eased.Pose(centre, 0, 0);
        eased.SetZoom(2);
        eased.Pose(centre, 0, FollowCamera.ZoomHalfLife);
        Assert.Equal(d0 * 1.5f, eased.Distance, 3);
        eased.Pose(centre, 0, 2);
        Assert.Equal(d0 * 2, eased.Distance, 3);
    }
}
