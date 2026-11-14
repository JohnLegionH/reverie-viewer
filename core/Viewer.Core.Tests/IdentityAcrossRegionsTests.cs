// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// An object is known by its full id across the regions of one session: when another region sends an update for an id the session
/// already holds, the object moves (new local id, new region, children kept) and is not removed and made again; a kill the old
/// region sends later for the old local id finds nothing and changes nothing; a child that arrives before its parent waits for it.
/// The official viewer does the same (llviewerobjectlist.cpp processObjectUpdate, findOrphans; llviewermessage.cpp process_kill_object).
/// The sequences are the ones a crossing produces: the new region sends the avatar and what it wears, the old region sends kills.
/// </summary>
public class IdentityAcrossRegionsTests
{
    private static SimRef Cell(int x, int y, string name) => new((ulong)(x * 256L) << 32 | (ulong)(y * 256L), name);

    private static readonly SimRef Home = Cell(1000, 1000, "Home");
    private static readonly SimRef East = Cell(1001, 1000, "East");

    private static LoginCredentials Creds => new("Test", "Resident", new Secret("pw", SensitiveKind.PasswordDigest));

    // what the agent wears: a rigged piece and an unrigged piece at attachment points, and a prim linked below the unrigged one
    private static readonly Guid RiggedPiece = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid PlainPiece = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid LinkedChild = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private sealed class Rig : IDisposable
    {
        public ViewerSession S = null!;
        public FakeTransport T = null!;
        public Guid Self;
        public WorldState HomeWorld = null!;
        public WorldState EastWorld = null!;
        public readonly List<WorldChange> HomeChanges = new();
        public readonly List<WorldChange> EastChanges = new();

        public static async Task<Rig> Start()
        {
            var t = new FakeTransport();
            var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example:8002/", Neighbours = true }, t, new CoreLog(new ListSink(), "test"));
            await s.LoginAsync(Creds, CancellationToken.None);
            t.DriveToInWorld(Home);
            t.ConnectedSimulatorCount++;
            t.Raise(new SimConnected(East, 256, 256));   // a child circuit: East becomes a neighbour
            var rig = new Rig { S = s, T = t, Self = s.AgentId, HomeWorld = s.World };
            rig.EastWorld = s.Worlds.First(w => w.Sim == East).World;
            rig.HomeWorld.Changed += rig.HomeChanges.Add;
            rig.EastWorld.Changed += rig.EastChanges.Add;
            Assert.NotEqual(Guid.Empty, rig.Self);
            return rig;
        }

        /// <summary>A region sends the agent's avatar and what it wears, with that region's own local ids.</summary>
        public void Send(SimRef sim, uint avatar, uint rigged, uint plain, uint child, bool avatarFirst = true)
        {
            void Av() => T.Raise(new AvatarUpdated(sim, new AvatarData(avatar, Self, "Test Resident", new Vector3(5, 6, 7), Quaternion.Identity, Vector3.One)));
            if (avatarFirst) Av();
            T.Raise(new ObjectUpdated(sim, Piece(rigged, RiggedPiece, avatar, 10), true, null));
            T.Raise(new ObjectUpdated(sim, Piece(plain, PlainPiece, avatar, 15), true, null));
            T.Raise(new ObjectUpdated(sim, Piece(child, LinkedChild, plain, 15), true, null));
            if (!avatarFirst) Av();
        }

        public void Kill(SimRef sim, params uint[] locals) => T.Raise(new ObjectsKilled(sim, locals));

        public void Cross(SimRef to)
        {
            T.CurrentSim = to;
            T.Raise(new SimConnected(to, 256, 256));
        }

        public void Dispose() => S.Dispose();
    }

    private static PrimData Piece(uint local, Guid full, uint parent, int point) =>
        new(local, full, parent, point, new Vector3(0.1f, 0, 0), Quaternion.Identity, Vector3.One);

    private static int RemovedForGood(IEnumerable<WorldChange> changes) =>
        changes.Count(c => c.Kind is WorldChangeKind.ObjectRemoved or WorldChangeKind.AvatarRemoved && !c.Handoff);

    private static void AssertWorn(WorldState world, Guid self, uint avatar)
    {
        Assert.Equal(avatar, world.AvatarOf(self)!.Id.LocalId);
        var roots = world.AttachmentRoots(avatar);
        Assert.Equal(new[] { RiggedPiece, PlainPiece }.Order(), roots.Select(r => r.FullId).Order());
        Assert.All(roots, r => Assert.Equal(self, r.WearerAgentId));
        var plain = roots.Single(r => r.FullId == PlainPiece);
        Assert.Equal(LinkedChild, Assert.Single(world.Linkset(plain.Id)).FullId);   // the linked prim kept its place under its root
    }

    // ---------------- a crossing out and back ----------------

    [Fact]
    public async Task A_crossing_there_and_back_moves_the_same_avatar_and_pieces_and_removes_none()
    {
        using var r = await Rig.Start();
        r.Send(Home, avatar: 100, rigged: 201, plain: 202, child: 203);
        AssertWorn(r.HomeWorld, r.Self, 100);
        r.HomeChanges.Clear();

        // out: the agent walks into East; East sends the avatar and its pieces, then Home kills what it had
        r.Cross(East);
        r.Send(East, avatar: 900, rigged: 901, plain: 902, child: 903);
        r.Kill(Home, 100, 201, 202, 203);

        AssertWorn(r.EastWorld, r.Self, 900);
        Assert.Null(r.HomeWorld.AvatarOf(r.Self));
        Assert.Empty(r.HomeWorld.Snapshot().Objects);
        Assert.Equal(0, RemovedForGood(r.HomeChanges));   // everything that left Home left by being handed over
        Assert.Equal(0, RemovedForGood(r.EastChanges));   // and the old region's kills took nothing from East
        Assert.Equal(4, r.EastChanges.Count(c => c.Handoff && c.Kind is WorldChangeKind.AvatarAdded or WorldChangeKind.ObjectAdded));
        Assert.Equal(0, r.EastChanges.Count(c => !c.Handoff && c.Kind is WorldChangeKind.AvatarAdded or WorldChangeKind.ObjectAdded));

        // back: Home sends them again with new local ids, East kills what it had
        r.HomeChanges.Clear();
        r.EastChanges.Clear();
        r.Cross(Home);
        r.Send(Home, avatar: 110, rigged: 211, plain: 212, child: 213);
        r.Kill(East, 900, 901, 902, 903);

        AssertWorn(r.HomeWorld, r.Self, 110);
        Assert.Null(r.EastWorld.AvatarOf(r.Self));
        Assert.Equal(0, RemovedForGood(r.HomeChanges));
        Assert.Equal(0, RemovedForGood(r.EastChanges));
    }

    [Fact]
    public async Task The_new_region_sending_the_pieces_before_the_avatar_still_moves_them_all()
    {
        using var r = await Rig.Start();
        r.Send(Home, avatar: 100, rigged: 201, plain: 202, child: 203);
        r.HomeChanges.Clear();

        r.Cross(East);
        r.Send(East, avatar: 900, rigged: 901, plain: 902, child: 903, avatarFirst: false);
        r.Kill(Home, 100, 201, 202, 203);

        AssertWorn(r.EastWorld, r.Self, 900);
        Assert.Equal(0, r.EastWorld.PendingAttachmentRoots);
        Assert.Equal(0, RemovedForGood(r.HomeChanges));
        Assert.Equal(0, RemovedForGood(r.EastChanges));
    }

    // ---------------- orphans ----------------

    [Fact]
    public async Task A_piece_that_arrives_before_its_wearer_waits_and_is_reunited_when_the_wearer_arrives()
    {
        using var r = await Rig.Start();
        r.Send(Home, avatar: 100, rigged: 201, plain: 202, child: 203);
        r.HomeChanges.Clear();
        r.Cross(East);

        // East sends one piece first: its wearer (900) is not known there yet, and Home still holds the same piece
        r.T.Raise(new ObjectUpdated(East, Piece(901, RiggedPiece, 900, 10), true, null));

        Assert.Equal(1, r.EastWorld.PendingAttachmentRoots);                  // held as an orphan, keyed by its parent's local id
        Assert.Contains(RiggedPiece, r.EastWorld.Snapshot().Objects.Values.Select(o => o.FullId));
        Assert.DoesNotContain(RiggedPiece, r.HomeWorld.Snapshot().Objects.Values.Select(o => o.FullId));   // it moved, it is not in both

        r.T.Raise(new AvatarUpdated(East, new AvatarData(900, r.Self, "Test Resident", new Vector3(5, 6, 7), Quaternion.Identity, Vector3.One)));

        Assert.Equal(0, r.EastWorld.PendingAttachmentRoots);
        var piece = Assert.Single(r.EastWorld.AttachmentRoots(900));
        Assert.Equal(RiggedPiece, piece.FullId);
        Assert.Equal(10, piece.AttachmentPoint);
        Assert.NotNull(r.EastWorld.Attachment(piece.Id));                     // placed on its wearer now
        Assert.Equal(0, RemovedForGood(r.HomeChanges));
        Assert.Equal(0, RemovedForGood(r.EastChanges));
    }

    // ---------------- kills ----------------

    [Fact]
    public async Task A_kill_for_a_local_id_whose_object_has_moved_changes_nothing_in_either_region()
    {
        using var r = await Rig.Start();
        r.Send(Home, avatar: 100, rigged: 201, plain: 202, child: 203);
        r.HomeChanges.Clear();
        r.Cross(East);
        r.Send(East, avatar: 900, rigged: 901, plain: 902, child: 903);
        var before = r.EastWorld.Snapshot();
        Assert.Equal(0, RemovedForGood(r.HomeChanges));   // crossing and being sent the pieces took nothing from Home for good
        r.HomeChanges.Clear();
        r.EastChanges.Clear();

        r.Kill(Home, 100, 201, 202, 203);
        r.Kill(Home, 100, 201);   // the same ids twice: still nothing

        var after = r.EastWorld.Snapshot();
        Assert.Equal(before.Objects.Count, after.Objects.Count);
        Assert.Equal(before.Avatars.Count, after.Avatars.Count);
        Assert.Empty(r.HomeChanges);
        Assert.Empty(r.EastChanges);
    }

    [Fact]
    public async Task The_agents_own_avatar_is_never_killed_by_a_region_it_has_left()
    {
        using var r = await Rig.Start();
        r.Send(Home, avatar: 100, rigged: 201, plain: 202, child: 203);
        r.Cross(East);
        r.HomeChanges.Clear();

        r.Kill(Home, 100);   // the old region kills the avatar before the new region has sent it

        Assert.NotNull(r.HomeWorld.AvatarOf(r.Self));   // the official viewer never kills its own avatar (process_kill_object)
        Assert.Equal(0, RemovedForGood(r.HomeChanges));

        r.Send(East, avatar: 900, rigged: 901, plain: 902, child: 903);
        AssertWorn(r.EastWorld, r.Self, 900);
        Assert.Null(r.HomeWorld.AvatarOf(r.Self));
    }

    [Fact]
    public async Task A_kill_that_comes_first_takes_the_old_pieces_and_the_new_regions_pieces_are_then_new_objects()
    {
        using var r = await Rig.Start();
        r.Send(Home, avatar: 100, rigged: 201, plain: 202, child: 203);
        r.HomeChanges.Clear();
        r.Cross(East);

        r.Kill(Home, 201, 202, 203);                              // the old region kills the pieces before East sends them
        r.Send(East, avatar: 900, rigged: 901, plain: 902, child: 903);

        AssertWorn(r.EastWorld, r.Self, 900);                     // official: a killed object is gone, the new region's update makes it
        Assert.Equal(3, RemovedForGood(r.HomeChanges.Where(c => c.Kind == WorldChangeKind.ObjectRemoved)));   // Home's pieces were killed, not handed over
        Assert.DoesNotContain(r.EastChanges, c => c.Handoff && c.Kind == WorldChangeKind.ObjectAdded);        // so East's pieces are new objects
        Assert.Contains(r.EastChanges, c => c.Handoff && c.Kind == WorldChangeKind.AvatarAdded);               // the avatar was never killed, it moved
    }

    // ---------------- a copy next door is not the object ----------------

    [Fact]
    public async Task A_neighbours_copy_of_the_agent_does_not_take_the_avatar_from_the_region_the_agent_is_in()
    {
        using var r = await Rig.Start();
        r.Send(Home, avatar: 100, rigged: 201, plain: 202, child: 203);
        r.HomeChanges.Clear();

        r.Send(East, avatar: 900, rigged: 901, plain: 902, child: 903);   // East tells about the agent before the agent has crossed

        AssertWorn(r.HomeWorld, r.Self, 100);                              // the agent is still here, with everything
        Assert.Empty(r.HomeChanges);
        AssertWorn(r.EastWorld, r.Self, 900);                              // and East keeps its own copy
    }

    [Fact]
    public async Task Another_agents_avatar_that_a_neighbour_sends_moves_from_the_region_the_agent_is_in_to_the_neighbour()
    {
        using var r = await Rig.Start();
        var other = Guid.Parse("0c4f0c4f-1111-4222-8333-944444444444");
        r.T.Raise(new AvatarUpdated(Home, new AvatarData(310, other, "Other Resident", new Vector3(250, 6, 7), Quaternion.Identity, Vector3.One)));
        r.HomeChanges.Clear();

        r.T.Raise(new AvatarUpdated(East, new AvatarData(910, other, "Other Resident", new Vector3(2, 6, 7), Quaternion.Identity, Vector3.One)));   // it walked across: East is where it is

        Assert.Null(r.HomeWorld.AvatarOf(other));
        Assert.Equal(910u, r.EastWorld.AvatarOf(other)!.Id.LocalId);
        var gone = Assert.Single(r.HomeChanges, c => c.Kind == WorldChangeKind.AvatarRemoved);
        Assert.True(gone.Handoff);
        Assert.Contains(r.EastChanges, c => c.Kind == WorldChangeKind.AvatarAdded && c.Handoff);
    }

    [Fact]
    public async Task An_object_a_neighbour_sends_for_an_id_the_current_region_holds_moves_to_the_neighbour()
    {
        using var r = await Rig.Start();
        var vehicle = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000f1");
        r.T.Raise(new ObjectUpdated(Home, new PrimData(400, vehicle, 0, 0, new Vector3(250, 6, 7), Quaternion.Identity, Vector3.One), false, null));
        r.HomeChanges.Clear();

        r.T.Raise(new ObjectUpdated(East, new PrimData(950, vehicle, 0, 0, new Vector3(2, 6, 7), Quaternion.Identity, Vector3.One), false, null));

        Assert.Null(r.HomeWorld.Object(new ObjectId(r.HomeWorld.Epoch, 400)));
        Assert.NotNull(r.EastWorld.Object(new ObjectId(r.EastWorld.Epoch, 950)));
        var gone = Assert.Single(r.HomeChanges, c => c.Kind == WorldChangeKind.ObjectRemoved);
        Assert.True(gone.Handoff);
        Assert.Equal(vehicle, gone.FullId);
    }
    [Fact]
    public async Task An_agent_that_stays_away_for_minutes_and_comes_back_has_its_avatar_and_pieces_announced_again()
    {
        using var r = await Rig.Start();
        r.Send(Home, avatar: 100, rigged: 201, plain: 202, child: 203);
        r.Cross(East);
        r.Send(East, avatar: 900, rigged: 901, plain: 902, child: 903);
        r.Kill(Home, 100, 201, 202, 203);
        var away = DateTime.UtcNow + TimeSpan.FromMinutes(4);
        r.S.CheckNeighbours(away);   // minutes pass in East
        r.HomeChanges.Clear();
        r.EastChanges.Clear();

        r.Cross(Home);
        r.Send(Home, avatar: 100, rigged: 201, plain: 202, child: 203);   // Home uses the ids it used before
        r.Kill(East, 900, 901, 902, 903);
        r.S.CheckNeighbours(away + TimeSpan.FromSeconds(30));

        AssertWorn(r.HomeWorld, r.Self, 100);
        Assert.Contains(r.HomeChanges, c => c.Kind == WorldChangeKind.AvatarAdded);
        Assert.Equal(3, r.HomeChanges.Count(c => c.Kind == WorldChangeKind.ObjectAdded));
        Assert.Equal(0, r.HomeChanges.Count(c => c.Kind is WorldChangeKind.AvatarRemoved or WorldChangeKind.ObjectRemoved));   // nothing that arrives is taken away again
    }
    [Fact]
    public async Task What_a_crossing_hands_over_at_once_takes_the_regions_ids_when_it_sends_the_pieces()
    {
        using var r = await Rig.Start();
        r.Send(Home, avatar: 100, rigged: 201, plain: 202, child: 203);
        r.T.Raise(new AvatarUpdated(East, new AvatarData(900, r.Self, "Test Resident", new Vector3(5, 6, 7), Quaternion.Identity, Vector3.One)));   // East already holds the agent's avatar
        r.HomeChanges.Clear();
        r.EastChanges.Clear();

        r.Cross(East);

        Assert.Equal(3, r.EastChanges.Count(c => c.Handoff && c.Kind == WorldChangeKind.ObjectAdded));   // the pieces are in East at once, as the same objects
        Assert.Equal(3, r.HomeChanges.Count(c => c.Handoff && c.Kind == WorldChangeKind.ObjectRemoved));
        Assert.Equal(1, r.HomeChanges.Count(c => c.Handoff && c.Kind == WorldChangeKind.AvatarRemoved));
        Assert.Equal(0, RemovedForGood(r.HomeChanges));
        r.EastChanges.Clear();

        r.T.Raise(new ObjectUpdated(East, Piece(901, RiggedPiece, 900, 10), true, null));   // East sends one under its own id

        Assert.Equal(901u, r.EastWorld.Snapshot().Objects.Values.Single(o => o.FullId == RiggedPiece).Id.LocalId);
        Assert.Equal(3, r.EastWorld.Snapshot().Objects.Count);                               // three pieces, not four
        Assert.All(r.EastChanges.Where(c => c.Kind is WorldChangeKind.ObjectAdded or WorldChangeKind.ObjectRemoved), c => Assert.True(c.Handoff));
    }

    [Fact]
    public async Task An_avatar_the_new_region_never_sends_is_dropped_from_the_world_the_agent_left_after_a_while()
    {
        using var r = await Rig.Start();
        r.Send(Home, avatar: 100, rigged: 201, plain: 202, child: 203);
        r.HomeChanges.Clear();
        var crossedAt = DateTime.UtcNow;
        r.Cross(East);                                                    // East sends nothing

        AssertWorn(r.HomeWorld, r.Self, 100);                             // at the crossing it is still there, as in the official viewer
        r.S.CheckNeighbours(crossedAt + TimeSpan.FromSeconds(2));
        Assert.NotNull(r.HomeWorld.AvatarOf(r.Self));                     // and a little later

        r.S.CheckNeighbours(crossedAt + ViewerSession.LeftAvatarGrace + TimeSpan.FromSeconds(1));

        Assert.Null(r.HomeWorld.AvatarOf(r.Self));                        // but a copy no region claims does not stay for ever
        Assert.Empty(r.HomeWorld.Snapshot().Objects);
        Assert.Equal(4, RemovedForGood(r.HomeChanges));
    }

    [Fact]
    public async Task A_handoff_removal_names_the_full_id_of_what_moved_on()
    {
        using var r = await Rig.Start();
        r.Send(Home, avatar: 100, rigged: 201, plain: 202, child: 203);
        r.HomeChanges.Clear();

        r.Cross(East);
        r.Send(East, avatar: 900, rigged: 901, plain: 902, child: 903);

        var gone = r.HomeChanges.Where(c => c.Handoff && c.Kind is WorldChangeKind.ObjectRemoved or WorldChangeKind.AvatarRemoved).ToList();
        Assert.Equal(4, gone.Count);
        Assert.Equal(new[] { r.Self, RiggedPiece, PlainPiece, LinkedChild }.Order(), gone.Select(c => c.FullId).Order());
    }
}
