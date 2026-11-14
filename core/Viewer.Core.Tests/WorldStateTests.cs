// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// World state: create/update/delete, linksets, avatars, terrain completeness (incl. a var region), epochs and
/// tombstones, with reordered and duplicated delivery. Driven directly and through a session on the fake transport.
/// </summary>
public class WorldStateTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly RegionInfo Region256 = new("Here", 1000, 1000, 256, 256, false);

    private static WorldState NewWorld() => new(new CoreLog(new ListSink(), "test"));

    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);

    private static PrimData Prim(uint local, uint parent = 0, float x = 10, uint? full = null) =>
        new(local, G(full ?? local), parent, 0, new Vector3(x, 20, 30), Quaternion.Identity, Vector3.One);

    private static AvatarData Av(uint local, float x = 50) =>
        new(local, G(9000 + local), "Av " + local, new Vector3(x, 50, 25), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f));

    // ---------------- create / update / delete ----------------

    [Fact]
    public void Create_update_delete()
    {
        var w = NewWorld();
        var changes = new List<WorldChange>();
        w.Changed += changes.Add;

        w.ApplyObject(Sim, Prim(1, x: 10));
        w.ApplyObject(Sim, Prim(1, x: 11));
        w.ApplyMove(Sim, 1, false, new Vector3(12, 20, 30), Quaternion.Identity, new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 0, 2));
        var id = new ObjectId(w.Epoch, 1);
        var o = w.Object(id)!;
        Assert.Equal(12, o.Position.X);
        Assert.Equal(new Vector3(1, 0, 0), o.Velocity);
        Assert.Equal(new Vector3(0, 0, -1), o.Acceleration);
        Assert.Equal(new Vector3(0, 0, 2), o.AngularVelocity);

        w.Kill(Sim, [1]);
        Assert.Null(w.Object(id));
        Assert.Equal(
            [WorldChangeKind.ObjectAdded, WorldChangeKind.ObjectChanged, WorldChangeKind.ObjectMoved, WorldChangeKind.ObjectRemoved],
            changes.Select(c => c.Kind));
    }

    [Fact]
    public void Duplicated_delivery_changes_nothing_and_raises_nothing()
    {
        var w = NewWorld();
        var changes = new List<WorldChange>();
        w.Changed += changes.Add;

        w.ApplyObject(Sim, Prim(1));
        w.ApplyObject(Sim, Prim(1));
        w.ApplyAvatar(Sim, Av(7));
        w.ApplyAvatar(Sim, Av(7));
        w.ApplyMove(Sim, 1, false, new Vector3(5, 5, 5), Quaternion.Identity, Vector3.Zero);
        w.ApplyMove(Sim, 1, false, new Vector3(5, 5, 5), Quaternion.Identity, Vector3.Zero);
        w.Kill(Sim, [1]);
        w.Kill(Sim, [1]);

        // an avatar stated again where it was is still an update: the
        // official viewer takes every update, putting an avatar carried on since back where the region says (llviewerobject.cpp processUpdateMessage)
        Assert.Equal(
            [WorldChangeKind.ObjectAdded, WorldChangeKind.AvatarAdded, WorldChangeKind.AvatarMoved, WorldChangeKind.ObjectMoved, WorldChangeKind.ObjectRemoved],
            changes.Select(c => c.Kind));
    }

    [Fact]
    public void A_terse_update_for_an_object_never_received_in_full_is_dropped()
    {
        var w = NewWorld();
        w.ApplyMove(Sim, 42, false, Vector3.One, Quaternion.Identity, Vector3.Zero);
        w.ApplyMove(Sim, 43, true, Vector3.One, Quaternion.Identity, Vector3.Zero);
        Assert.Empty(w.Snapshot().Objects);
        Assert.Equal(2, w.Dropped(WorldDrop.UnknownObject));
    }

    // ---------------- tombstones ----------------

    [Fact]
    public void A_late_full_update_after_delete_never_resurrects_the_object()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Prim(1));
        w.Kill(Sim, [1]);
        w.ApplyObject(Sim, Prim(1, x: 99));            // late, same object
        w.ApplyMove(Sim, 1, false, Vector3.One, Quaternion.Identity, Vector3.Zero);
        Assert.Empty(w.Snapshot().Objects);
        Assert.Equal(1, w.Dropped(WorldDrop.Tombstoned));
    }

    [Fact]
    public void A_kill_that_arrives_before_its_update_still_holds()
    {
        var w = NewWorld();
        w.Kill(Sim, [5]);
        w.ApplyObject(Sim, Prim(5));
        Assert.Empty(w.Snapshot().Objects);
        Assert.Equal(1, w.Dropped(WorldDrop.Tombstoned));
    }

    [Fact]
    public void A_different_object_under_a_reused_local_id_is_a_new_object()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Prim(1, full: 100));
        w.Kill(Sim, [1]);
        w.ApplyObject(Sim, Prim(1, full: 200));
        Assert.Equal(G(200), w.Snapshot().Objects[1].FullId);
        w.ApplyObject(Sim, Prim(1, full: 100));        // the killed one, late: it must not take over the new object's slot
        Assert.Equal(G(200), w.Snapshot().Objects[1].FullId);
        Assert.Equal(1, w.Dropped(WorldDrop.Tombstoned));
    }

    // ---------------- linksets ----------------

    [Fact]
    public void A_linkset_resolves_in_any_arrival_order()
    {
        var events = new List<Action<WorldState>>
        {
            w => w.ApplyObject(Sim, Prim(10)),
            w => w.ApplyObject(Sim, Prim(11, parent: 10)),
            w => w.ApplyObject(Sim, Prim(12, parent: 10)),
            w => w.ApplyObject(Sim, Prim(13, parent: 12)),
        };
        foreach (var order in Permutations(events.Count))
        {
            var w = NewWorld();
            foreach (var i in order)
                events[i](w);
            Assert.Equal([11u, 12u, 13u], w.Linkset(new ObjectId(w.Epoch, 10)).Select(o => o.Id.LocalId));
        }
    }

    [Fact]
    public void Link_and_unlink_change_the_linkset()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Prim(10));
        w.ApplyObject(Sim, Prim(20));
        var root = new ObjectId(w.Epoch, 10);
        Assert.Empty(w.Linkset(root));

        w.ApplyObject(Sim, Prim(20, parent: 10));      // linked
        Assert.Equal([20u], w.Linkset(root).Select(o => o.Id.LocalId));

        w.ApplyObject(Sim, Prim(20, parent: 0));       // unlinked
        Assert.Empty(w.Linkset(root));

        w.ApplyObject(Sim, Prim(20, parent: 10));
        w.Kill(Sim, [10]);                             // root deleted: its linkset is gone with it
        Assert.Empty(w.Linkset(root));
    }

    // ---------------- avatars and attachments ----------------

    [Fact]
    public void Avatars_arrive_move_and_leave()
    {
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(7, x: 50));
        var id = new ObjectId(w.Epoch, 7);
        w.ApplyMove(Sim, 7, true, new Vector3(60, 50, 25), Quaternion.Identity, new Vector3(3, 0, 0), default, new Vector3(0, 0, 1));
        var a = w.Avatar(id)!;
        Assert.Equal(60, a.Position.X);
        Assert.Equal(new Vector3(3, 0, 0), a.Velocity);
        Assert.Equal(new Vector3(0, 0, 1), a.AngularVelocity);
        Assert.Equal(G(9007), a.AgentId);

        w.Kill(Sim, [7]);
        Assert.Null(w.Avatar(id));
    }

    [Fact]
    public void An_attachment_linkset_follows_its_root_and_the_root_its_wearer_in_any_order()
    {
        var root = new PrimData(501, G(501), 7, 5, Vector3.Zero, Quaternion.Identity, Vector3.One);
        var child = new PrimData(502, G(502), 501, 5, Vector3.One, Quaternion.Identity, Vector3.One);  // point on every prim of the set
        var events = new List<Action<WorldState>>
        {
            w => w.ApplyAvatar(Sim, Av(7)),
            w => w.ApplyObject(Sim, root, eventIsAttachment: true),
            w => w.ApplyObject(Sim, child, eventIsAttachment: true),
        };
        foreach (var order in Permutations(events.Count))
        {
            var w = NewWorld();
            foreach (var i in order)
                events[i](w);
            var s = w.Snapshot();
            Assert.Equal(5, s.Objects[501].AttachmentPoint);
            Assert.Equal(G(9007), s.Objects[501].WearerAgentId);
            Assert.Null(s.Objects[502].AttachmentPoint);          // a child is not a root attachment
            Assert.Equal([502u], w.Linkset(s.Objects[501].Id).Select(o => o.Id.LocalId));
        }
    }

    [Fact]
    public void HUD_attachments_never_enter_the_world()
    {
        var w = NewWorld();
        w.ApplyAvatar(Sim, Av(7));
        for (int point = 31; point <= 38; point++)
            w.ApplyObject(Sim, new PrimData((uint)(600 + point), G((uint)(600 + point)), 7, point, Vector3.Zero, Quaternion.Identity, Vector3.One), eventIsAttachment: true);
        w.ApplyObject(Sim, new PrimData(700, G(700), 7, 30, Vector3.Zero, Quaternion.Identity, Vector3.One), eventIsAttachment: true);  // not a HUD point
        Assert.Equal([700u], w.Snapshot().Objects.Keys);
        Assert.Equal(8, w.Dropped(WorldDrop.Hud));
    }

    // ---------------- epochs ----------------

    [Fact]
    public void A_region_change_starts_a_new_epoch_and_old_ids_and_late_events_do_not_reach_it()
    {
        var w = NewWorld();
        w.BeginRegion(Sim, Region256);
        w.ApplyObject(Sim, Prim(1));
        var oldId = new ObjectId(w.Epoch, 1);

        var next = new SimRef(257000UL << 32 | 256000UL, "There");
        w.BeginRegion(next, Region256 with { Name = "There", GridX = 1004 });
        Assert.Equal(oldId.Epoch + 1, w.Epoch);
        Assert.Empty(w.Snapshot().Objects);

        w.ApplyObject(next, Prim(1, full: 777));               // same local id, the new region's object
        w.ApplyObject(Sim, Prim(2));                            // late, from the region we left
        w.Kill(Sim, [1]);                                       // late kill from the old region: must not hit the new object
        Assert.Null(w.Object(oldId));                           // an old-epoch id addresses nothing
        Assert.Equal(G(777), w.Object(new ObjectId(w.Epoch, 1))!.FullId);
        Assert.Equal([1u], w.Snapshot().Objects.Keys);
        Assert.Equal(2, w.Dropped(WorldDrop.StaleEpoch));
    }

    [Fact]
    public void The_login_burst_before_SimConnected_is_kept_when_that_sim_connects()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Prim(1));
        w.BeginRegion(Sim, Region256);
        Assert.Single(w.Snapshot().Objects);
        Assert.Equal(0, w.Epoch);
    }

    // ---------------- terrain ----------------

    [Fact]
    public void Terrain_of_a_256_region_is_complete_with_256_patches_in_any_order_and_duplicates_change_nothing()
    {
        var w = NewWorld();
        w.BeginRegion(Sim, Region256);
        var cells = (from x in Enumerable.Range(0, 16) from y in Enumerable.Range(0, 16) select (x, y)).ToList();
        var rng = new Random(1234);
        foreach (var (x, y) in cells.OrderBy(_ => rng.Next()))
        {
            Assert.False(w.TerrainComplete);
            w.ApplyPatch(Sim, x, y, 16, Heights(x, y));
            w.ApplyPatch(Sim, x, y, 16, Heights(x, y));
        }
        Assert.True(w.TerrainComplete);
        Assert.Equal(256, w.TerrainPatchCount);
        Assert.True(w.TryGetPatch(3, 4, out var h));
        Assert.Equal(3 * 100 + 4, h[0]);
    }

    [Fact]
    public void A_short_height_map_is_refused()
    {
        var w = NewWorld();
        w.ApplyPatch(Sim, 0, 0, 16, new float[10]);
        Assert.Equal(0, w.TerrainPatchCount);
        Assert.Equal(1, w.Dropped(WorldDrop.BadPatch));
    }

    /// <summary>
    /// The var-region workaround end to end through the session: LibreMetaverse reports 256, the login reply 512; the
    /// terrain is complete only when all 32x32 = 1024 patches of the 512 m region have arrived.
    /// </summary>
    [Fact]
    public async Task Terrain_of_a_var_region_is_complete_only_with_every_patch_of_its_real_size()
    {
        var t = new FakeTransport { OnLogin = (_, _) => Task.FromResult(FakeTransport.Ok(Guid.NewGuid(), 512, 512)) };
        using var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example/" }, t, new CoreLog(new ListSink(), "t"));
        Assert.True((await s.LoginAsync(new("A", "B", new Secret("p", SensitiveKind.PasswordDigest)), CancellationToken.None)).Ok);
        t.DriveToInWorld(Sim, simSizeX: 256, simSizeY: 256);
        Assert.Equal(512, s.World.Region!.SizeX);
        Assert.Equal(1024, s.World.TerrainExpected);

        for (int x = 0; x < 32; x++)
            for (int y = 0; y < 32; y++)
            {
                if (x < 16 && y < 16)
                    continue;
                t.Raise(new LandPatch(Sim, x, y, 16, Heights(x, y)));
            }
        Assert.Equal(768, s.World.TerrainPatchCount);
        Assert.False(s.World.TerrainComplete);   // a 256 m view of the region would call this complete long ago

        for (int x = 0; x < 16; x++)
            for (int y = 0; y < 16; y++)
                t.Raise(new LandPatch(Sim, x, y, 16, Heights(x, y)));
        Assert.True(s.World.TerrainComplete);
    }

    [Fact]
    public async Task Patches_beyond_the_reported_size_grow_the_region_and_count_toward_completeness()
    {
        var t = new FakeTransport();   // neither the sim nor the login reply says anything: 256
        using var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example/" }, t, new CoreLog(new ListSink(), "t"));
        await s.LoginAsync(new("A", "B", new Secret("p", SensitiveKind.PasswordDigest)), CancellationToken.None);
        t.DriveToInWorld(Sim);
        for (int x = 0; x < 48; x++)
            for (int y = 0; y < 16; y++)
                t.Raise(new LandPatch(Sim, x, y, 16, Heights(x, y)));
        Assert.Equal(768, s.World.Region!.SizeX);
        Assert.True(s.World.TerrainComplete);
        t.Raise(new MapBlockSize(Sim.GridX, Sim.GridY, 1024, 256));   // authoritative: 1024 wide, so not complete
        Assert.False(s.World.TerrainComplete);
    }

    // ---------------- through the session ----------------

    [Fact]
    public async Task The_session_applies_only_its_own_regions_world_and_disposal_empties_it()
    {
        var t = new FakeTransport();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example/" }, t, new CoreLog(new ListSink(), "t"));
        await s.LoginAsync(new("A", "B", new Secret("p", SensitiveKind.PasswordDigest)), CancellationToken.None);
        t.DriveToInWorld(Sim);
        var neighbour = new SimRef(257000UL << 32 | 256000UL, "Neighbour");

        t.Raise(new ObjectUpdated(Sim, Prim(1), false, null));
        t.Raise(new ObjectUpdated(neighbour, Prim(2), false, null));
        t.Raise(new AvatarUpdated(Sim, Av(7)));
        t.Raise(new ObjectMoved(Sim, 1, false, new Vector3(1, 2, 3), Quaternion.Identity, Vector3.Zero, Vector3.Zero, Vector3.Zero));
        t.Raise(new ObjectsKilled(neighbour, [1]));
        var snap = s.World.Snapshot();
        Assert.Equal([1u], snap.Objects.Keys);
        Assert.Equal(new Vector3(1, 2, 3), snap.Objects[1].Position);
        Assert.Equal([7u], snap.Avatars.Keys);

        // A teleport: a new simulator connects, the old region's state is gone.
        var there = new SimRef(300000UL << 32 | 300000UL, "There");
        int epoch = s.World.Epoch;
        t.Raise(new SimConnected(there, 256, 256));
        t.CurrentSim = there;
        Assert.Equal(epoch + 1, s.World.Epoch);
        Assert.Empty(s.World.Snapshot().Objects);

        t.Raise(new ObjectUpdated(there, Prim(3), false, null));
        s.Dispose();
        Assert.Empty(s.World.Snapshot().Objects);
        t.Raise(new ObjectUpdated(there, Prim(4), false, null));
        Assert.Empty(s.World.Snapshot().Objects);
    }

    // ---------------- helpers ----------------

    private static float[] Heights(int x, int y)
    {
        var h = new float[16 * 16];
        Array.Fill(h, x * 100 + y);
        return h;
    }

    internal static IEnumerable<int[]> Permutations(int n)
    {
        var a = Enumerable.Range(0, n).ToArray();
        return Permute(a, 0);

        static IEnumerable<int[]> Permute(int[] a, int k)
        {
            if (k == a.Length)
            {
                yield return (int[])a.Clone();
                yield break;
            }
            for (int i = k; i < a.Length; i++)
            {
                (a[k], a[i]) = (a[i], a[k]);
                foreach (var p in Permute(a, k + 1))
                    yield return p;
                (a[k], a[i]) = (a[i], a[k]);
            }
        }
    }
}
