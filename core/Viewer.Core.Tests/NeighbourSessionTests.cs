// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A session with neighbouring regions, against the fake transport: a world for each connected region, events routed to the
/// right one, offsets and reach from the regions' handles, a crossing that keeps what is drawn, and the option off behaving as
/// before. No engine and no network.
/// </summary>
public class NeighbourSessionTests
{
    // Regions on a row: Home at cell (1000, 1000), East next to it, FarEast two cells on; North above Home.
    private static SimRef Cell(int x, int y, string name) => new((ulong)(x * 256L) << 32 | (ulong)(y * 256L), name);

    private static readonly SimRef Home = Cell(1000, 1000, "Home");
    private static readonly SimRef East = Cell(1001, 1000, "East");
    private static readonly SimRef FarEast = Cell(1003, 1000, "FarEast");
    private static readonly SimRef North = Cell(1000, 1001, "North");

    private static readonly Guid Agent = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static LoginCredentials Creds => new("Test", "Resident", new Secret("pw", SensitiveKind.PasswordDigest));

    private static async Task<(ViewerSession S, FakeTransport T)> InWorld(bool neighbours = true)
    {
        var t = new FakeTransport();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example:8002/", Neighbours = neighbours }, t, new CoreLog(new ListSink(), "test"));
        await s.LoginAsync(Creds, CancellationToken.None);
        t.DriveToInWorld(Home);
        return (s, t);
    }

    private static void Connect(FakeTransport t, SimRef sim, int size = 256)
    {
        t.ConnectedSimulatorCount++;
        t.Raise(new SimConnected(sim, size, size));
    }

    private static PrimData Prim(uint local) => new(local, Guid.NewGuid(), 0, 0, new Vector3(10, 20, 30), Quaternion.Identity, Vector3.One);

    private static AvatarData Av(uint local, Guid agent) => new(local, agent, "Someone", new Vector3(5, 6, 7), Quaternion.Identity, Vector3.One);

    private static RegionWorld Of(ViewerSession s, SimRef sim)
    {
        var found = s.Worlds.FirstOrDefault(w => w.Sim == sim);
        Assert.NotNull(found);
        return found!;
    }

    private static float[] Flat(int size, float h) => Enumerable.Repeat(h, size * size).ToArray();

    // ---------------- placement ----------------

    [Fact]
    public void Offset_is_the_difference_of_the_handles()
    {
        Assert.Equal(new Vector3(256, 0, 0), RegionPlacement.Offset(Home, East));
        Assert.Equal(new Vector3(0, 256, 0), RegionPlacement.Offset(Home, North));
        Assert.Equal(new Vector3(-256, 0, 0), RegionPlacement.Offset(East, Home));
        Assert.Equal(new Vector3(768, 0, 0), RegionPlacement.Offset(Home, FarEast));
    }

    [Fact]
    public void Distance_is_to_the_nearest_edge_and_zero_inside()
    {
        var east = new Vector3(256, 0, 0);
        Assert.Equal(0f, RegionPlacement.DistanceTo(new Vector3(300, 100, 20), east, 256, 256));
        Assert.Equal(6f, RegionPlacement.DistanceTo(new Vector3(250, 100, 20), east, 256, 256), 3);
        Assert.Equal(5f, RegionPlacement.DistanceTo(new Vector3(252, 259, 20), east, 256, 256), 1.0);   // 5 m left of the edge, 4 m above it: 3-4-5 on the corner
        Assert.Equal(256 + 10f, RegionPlacement.DistanceTo(new Vector3(-10, 100, 20), east, 256, 256), 3);
    }

    [Fact]
    public void Distance_uses_a_variable_regions_size()
    {
        var east = new Vector3(256, 0, 0);
        // a 512 m wide region reaches 512 m east of its corner; a point 400 m inside it is inside
        Assert.Equal(0f, RegionPlacement.DistanceTo(new Vector3(256 + 400, 100, 0), east, 512, 256));
        Assert.Equal(0f, RegionPlacement.DistanceTo(new Vector3(256 + 400, 100, 0), east, 256, 256) - 144f);   // the same point is 144 m past a 256 m region
    }

    [Fact]
    public void Reach_is_the_draw_distance_to_the_nearest_edge()
    {
        var east = new Vector3(256, 0, 0);
        Assert.True(RegionPlacement.InReach(new Vector3(250, 100, 20), east, 256, 256, 64));
        Assert.False(RegionPlacement.InReach(new Vector3(100, 100, 20), east, 256, 256, 64));
        Assert.True(RegionPlacement.InReach(new Vector3(100, 100, 20), east, 256, 256, 256));
    }

    // ---------------- connecting ----------------

    [Fact]
    public async Task A_connect_of_a_simulator_that_is_not_current_adds_a_neighbour()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        var changes = new List<RegionWorldChange>();
        s.WorldsChanged += changes.Add;
        var homeWorld = s.World;

        Connect(t, East);

        Assert.Same(homeWorld, s.World);
        Assert.Equal(2, s.Worlds.Count);
        Assert.Equal(Home, s.Worlds[0].Sim);
        Assert.True(s.Worlds[0].IsCurrent);
        var east = Of(s, East);
        Assert.False(east.IsCurrent);
        Assert.Equal(new Vector3(256, 0, 0), east.Offset);
        Assert.Equal(new Vector3(256, 0, 0), east.OriginOffset);
        Assert.Equal(new Vector3(256, 0, 0), east.World.OriginOffset);
        Assert.Equal(Vector3.Zero, s.Worlds[0].OriginOffset);
        Assert.Equal(Vector3.Zero, s.World.OriginOffset);
        Assert.Equal("East", east.Region.Name);
        Assert.NotSame(homeWorld, east.World);
        Assert.Equal("Home", s.Region!.Name);
        Assert.Equal(new[] { new RegionWorldChange(RegionWorldChangeKind.Added, East) }, changes);
    }

    [Fact]
    public async Task The_current_world_is_not_reset_by_a_neighbour_connecting()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        s.World.ApplyObject(Home, Prim(1));
        int epoch = s.World.Epoch;

        Connect(t, East);

        Assert.Equal(epoch, s.World.Epoch);
        Assert.Single(s.World.Snapshot().Objects);
    }

    [Fact]
    public async Task With_neighbours_off_a_second_connect_keeps_no_neighbour()
    {
        var (s, t) = await InWorld(neighbours: false);
        using var _s = s;

        Connect(t, East);   // the library opens no child circuits with the option off; a connect is an arrival, as it always was

        Assert.Single(s.Worlds);
    }

    [Fact]
    public async Task The_login_asks_for_neighbours_only_when_the_option_is_on()
    {
        var (on, tOn) = await InWorld(true);
        using var _a = on;
        var (off, tOff) = await InWorld(false);
        using var _b = off;

        Assert.True(Assert.Single(tOn.Logins).Neighbours);
        Assert.False(Assert.Single(tOff.Logins).Neighbours);
    }

    // ---------------- routing ----------------

    [Fact]
    public async Task A_neighbours_objects_and_avatars_go_into_its_own_world()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        Connect(t, East);
        var other = Guid.NewGuid();

        t.Raise(new ObjectUpdated(East, Prim(7), false, null));
        t.Raise(new AvatarUpdated(East, Av(8, other)));
        t.Raise(new ObjectUpdated(Home, Prim(1), false, null));

        var east = Of(s, East).World.Snapshot();
        Assert.Equal(new uint[] { 7 }, east.Objects.Keys.ToArray());
        Assert.Equal(new uint[] { 8 }, east.Avatars.Keys.ToArray());
        var home = s.World.Snapshot();
        Assert.Equal(new uint[] { 1 }, home.Objects.Keys.ToArray());
        Assert.Empty(home.Avatars);
    }

    [Fact]
    public async Task A_look_held_for_a_neighbours_avatar_is_that_neighbours_copy_and_a_new_look_for_it_replaces_only_that_copy()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        t.ConnectedSimulatorCount++;
        t.Raise(new SimConnected(East, 256, 256, true));   // server-baked appearance
        var other = Guid.NewGuid();
        var bake = Guid.NewGuid();
        AvatarAppearanceData Look(byte height, int cof) => new(other, Enumerable.Range(0, 45).Select(i => i == 8 ? bake : Guid.Empty).ToImmutableArray(),
            new VisualParamSet(new byte[] { height }, [33]), 1, cof);

        t.Raise(new AvatarUpdated(East, Av(8, other)));
        t.Raise(new AvatarAppearanceReceived(East, Look(100, 5)));
        var first = Assert.Single(Of(s, East).World.Appearances());
        Assert.Equal((byte)100, LookSummary.Of(first).HeightByte);
        Assert.Empty(s.World.Appearances());   // the current region holds no copy of it

        t.Raise(new AvatarAppearanceReceived(East, Look(30, 6)));   // a rebake the neighbour announces
        var second = Assert.Single(Of(s, East).World.Appearances());
        Assert.Equal((byte)30, LookSummary.Of(second).HeightByte);
        Assert.Equal(6, LookSummary.Of(second).CofVersion);
        Assert.Empty(s.World.Appearances());
    }

    [Fact]
    public async Task A_neighbours_terrain_and_motion_and_kills_go_into_its_own_world()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        Connect(t, East);
        t.Raise(new ObjectUpdated(East, Prim(7), false, null));

        t.Raise(new LandPatch(East, 2, 3, 16, Flat(16, 21f)));
        t.Raise(new ObjectMoved(East, 7, false, new Vector3(50, 60, 70), Quaternion.Identity, Vector3.Zero, Vector3.Zero, Vector3.Zero));

        var east = Of(s, East).World;
        Assert.True(east.TryGetPatch(2, 3, out var heights));
        Assert.Equal(21f, heights[0]);
        Assert.False(s.World.TryGetPatch(2, 3, out _));
        Assert.Equal(new Vector3(306, 60, 70), east.Snapshot().Objects[7].Position);   // placed in the login region's frame: its own 50 plus the 256 m the region lies east

        t.Raise(new ObjectsKilled(East, [7]));
        Assert.Empty(east.Snapshot().Objects);
    }

    [Fact]
    public async Task An_event_from_a_simulator_the_session_does_not_hold_is_dropped()
    {
        var (s, t) = await InWorld();
        using var _s = s;

        t.Raise(new ObjectUpdated(FarEast, Prim(7), false, null));

        Assert.Empty(s.World.Snapshot().Objects);
        Assert.Single(s.Worlds);
    }

    [Fact]
    public async Task A_neighbour_does_not_set_the_current_regions_camera_plane_or_controls()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        Connect(t, East);

        t.Raise(new CameraConstraintReceived(East, new Vector4(0, 0, 1, 5)));
        t.Raise(new ScriptControlChanged(East, [new ScriptControlEntry(true, 1u << 28, false)]));

        Assert.Equal(Vector4.Zero, s.CameraCollidePlane);
        Assert.False(s.ScriptControls.LeftButtonGrabbed(false));
    }

    [Fact]
    public async Task A_neighbours_event_queue_does_not_finish_the_login()
    {
        var t = new FakeTransport();
        using var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example:8002/", Neighbours = true }, t, new CoreLog(new ListSink(), "test"));
        await s.LoginAsync(Creds, CancellationToken.None);
        t.Raise(new LoginProgressed(LoginPhase.ConnectingToSim, ""));
        t.ConnectedSimulatorCount = 1;
        t.Raise(new SimConnected(Home, 256, 256));
        t.CurrentSim = Home;
        Connect(t, East);

        t.Raise(new EventQueueRunning(East));
        t.Raise(new MovementComplete(East));

        Assert.Equal(SessionStage.SimConnected, s.Stage);
    }

    [Fact]
    public async Task A_neighbours_map_block_gives_it_its_size_and_name()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        Connect(t, East);

        t.Raise(new MapBlockSize(1001, 1000, 512, 256, "Eastland"));

        var east = Of(s, East);
        Assert.Equal(512, east.Region.SizeX);
        Assert.Equal(256, east.Region.SizeY);
        Assert.True(east.Region.SizeAuthoritative);
        Assert.Equal(512, east.World.Region!.SizeX);
        Assert.Equal(256, s.Region!.SizeX);   // the current region is not touched
    }

    [Fact]
    public async Task A_neighbours_terrain_beyond_its_size_grows_it_like_the_current_regions()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        Connect(t, East);

        t.Raise(new LandPatch(East, 20, 0, 16, Flat(16, 1f)));   // patch 20 of 16 m starts at 320 m: past 256

        Assert.Equal(512, Of(s, East).Region.SizeX);
    }

    // ---------------- leaving ----------------

    [Fact]
    public async Task A_disconnected_neighbour_is_removed_with_its_world()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        Connect(t, East);
        var eastWorld = Of(s, East).World;
        var changes = new List<RegionWorldChange>();
        s.WorldsChanged += changes.Add;

        t.Raise(new SimDisconnected(East));

        Assert.Single(s.Worlds);
        Assert.Equal(new[] { new RegionWorldChange(RegionWorldChangeKind.Removed, East) }, changes);
        t.Raise(new ObjectUpdated(East, Prim(7), false, null));   // a late event finds nothing to land in
        Assert.Empty(eastWorld.Snapshot().Objects);
    }

    [Fact]
    public async Task A_disconnect_of_the_current_simulator_is_not_a_neighbour_removal()
    {
        var (s, t) = await InWorld();
        using var _s = s;

        t.Raise(new SimDisconnected(Home));

        Assert.Single(s.Worlds);
        Assert.True(s.Worlds[0].IsCurrent);
    }

    // ---------------- crossing ----------------

    [Fact]
    public async Task A_crossing_makes_the_neighbour_current_without_clearing_either_world()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        Connect(t, East);
        s.World.ApplyObject(Home, Prim(1));
        var eastWorld = Of(s, East).World;
        t.Raise(new ObjectUpdated(East, Prim(7), false, null));
        var homeWorld = s.World;
        int homeEpoch = homeWorld.Epoch, eastEpoch = eastWorld.Epoch;
        var changes = new List<RegionWorldChange>();
        s.WorldsChanged += changes.Add;
        var regions = new List<RegionInfo>();
        s.RegionChanged += regions.Add;

        t.CurrentSim = East;
        t.Raise(new SimConnected(East, 256, 256));   // the adapter reports the switch the way it reports an arrival

        Assert.Same(eastWorld, s.World);
        Assert.Equal("East", s.Region!.Name);
        Assert.Equal(East, s.Worlds[0].Sim);
        Assert.True(s.Worlds[0].IsCurrent);
        Assert.Equal(Vector3.Zero, s.Worlds[0].Offset);
        var home = Of(s, Home);
        Assert.False(home.IsCurrent);
        Assert.Same(homeWorld, home.World);
        Assert.Equal(new Vector3(-256, 0, 0), home.Offset);
        Assert.Equal(Vector3.Zero, home.OriginOffset);                          // nothing is re-anchored by walking across
        Assert.Equal(new Vector3(256, 0, 0), s.Worlds[0].OriginOffset);
        Assert.Equal(homeEpoch, homeWorld.Epoch);
        Assert.Equal(eastEpoch, eastWorld.Epoch);
        Assert.Single(homeWorld.Snapshot().Objects);
        Assert.Single(eastWorld.Snapshot().Objects);
        Assert.Equal(new[] { new RegionWorldChange(RegionWorldChangeKind.Crossed, East) }, changes);
        Assert.Equal("East", Assert.Single(regions).Name);
    }

    [Fact]
    public async Task After_a_crossing_the_new_regions_events_are_current_and_the_old_regions_are_a_neighbours()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        Connect(t, East);
        t.CurrentSim = East;
        t.Raise(new SimConnected(East, 256, 256));
        Of(s, Home).World.ApplyObject(Home, Prim(1));   // keep the old world populated for the check below

        t.Raise(new CameraConstraintReceived(East, new Vector4(0, 0, 1, 9)));
        t.Raise(new ObjectUpdated(Home, Prim(2), false, null));

        Assert.Equal(new Vector4(0, 0, 1, 9), s.CameraCollidePlane);
        Assert.Contains(2u, Of(s, Home).World.Snapshot().Objects.Keys);
        Assert.DoesNotContain(2u, s.World.Snapshot().Objects.Keys);
    }

    [Fact]
    public async Task An_event_from_the_new_region_before_the_switch_is_announced_is_not_lost()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        Connect(t, East);

        t.CurrentSim = East;                                  // the library has switched, the announcement has not come yet
        t.Raise(new ObjectUpdated(East, Prim(7), false, null));
        t.Raise(new SimConnected(East, 256, 256));

        Assert.Contains(7u, s.World.Snapshot().Objects.Keys);
    }

    [Fact]
    public async Task A_teleport_to_a_region_that_is_not_a_neighbour_starts_afresh_and_drops_the_neighbours()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        Connect(t, East);
        s.World.ApplyObject(Home, Prim(1));
        var changes = new List<RegionWorldChange>();
        s.WorldsChanged += changes.Add;

        t.CurrentSim = FarEast;
        t.Raise(new SimConnected(FarEast, 256, 256));

        Assert.Single(s.Worlds);
        Assert.Equal(FarEast, s.Worlds[0].Sim);
        Assert.Equal("FarEast", s.Region!.Name);
        Assert.Empty(s.World.Snapshot().Objects);
        Assert.Contains(new RegionWorldChange(RegionWorldChangeKind.Removed, East), changes);
        Assert.DoesNotContain(changes, c => c.Kind == RegionWorldChangeKind.Crossed);
    }

    [Fact]
    public async Task With_neighbours_off_a_teleport_arrival_still_starts_the_region_afresh()
    {
        var (s, t) = await InWorld(neighbours: false);
        using var _s = s;
        s.World.ApplyObject(Home, Prim(1));
        int epoch = s.World.Epoch;

        t.CurrentSim = East;
        t.Raise(new SimConnected(East, 256, 256));

        Assert.Equal(epoch + 1, s.World.Epoch);
        Assert.Empty(s.World.Snapshot().Objects);
        Assert.Single(s.Worlds);
    }

    [Fact]
    public async Task What_a_neighbour_drew_stays_where_it_is_when_the_agent_crosses_into_it()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        Connect(t, East);
        t.Raise(new ObjectUpdated(East, Prim(7), false, null));
        var before = Of(s, East).World.Snapshot().Objects[7].Position;

        t.CurrentSim = East;
        t.Raise(new SimConnected(East, 256, 256));
        t.Raise(new ObjectMoved(East, 7, false, new Vector3(11, 22, 33), Quaternion.Identity, Vector3.Zero, Vector3.Zero, Vector3.Zero));

        Assert.Equal(new Vector3(266, 20, 30), before);
        Assert.Equal(new Vector3(267, 22, 33), s.World.Snapshot().Objects[7].Position);   // still the login region's frame
    }

    [Fact]
    public async Task Walking_on_into_a_second_neighbour_places_it_relative_to_the_login_region()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        Connect(t, East);
        t.CurrentSim = East;
        t.Raise(new SimConnected(East, 256, 256));
        var next = Cell(1002, 1000, "NextEast");
        Connect(t, next);

        t.CurrentSim = next;
        t.Raise(new SimConnected(next, 256, 256));

        Assert.Equal(new Vector3(512, 0, 0), s.World.OriginOffset);
        Assert.Equal(new Vector3(512, 0, 0), s.Worlds[0].OriginOffset);
        Assert.Equal(Vector3.Zero, Of(s, Home).OriginOffset);
        Assert.Equal(new Vector3(-512, 0, 0), Of(s, Home).Offset);
    }

    [Fact]
    public async Task A_teleport_anchors_the_frame_on_the_region_it_lands_in()
    {
        var (s, t) = await InWorld();
        using var _s = s;

        t.CurrentSim = FarEast;
        t.Raise(new SimConnected(FarEast, 256, 256));
        Connect(t, North);   // the arrival region's neighbours are placed from it

        Assert.Equal(Vector3.Zero, s.World.OriginOffset);
        Assert.Equal(Vector3.Zero, s.Worlds[0].OriginOffset);
        Assert.Equal(RegionPlacement.Offset(FarEast, North), Of(s, North).OriginOffset);
    }

    [Fact]
    public async Task A_teleport_after_a_crossing_goes_back_to_the_first_world_and_drops_the_one_crossed_into()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        var anchor = s.World;
        Connect(t, East);
        t.CurrentSim = East;
        t.Raise(new SimConnected(East, 256, 256));
        var crossedInto = s.World;
        Assert.NotSame(anchor, crossedInto);
        int epoch = anchor.Epoch;
        var changes = new List<RegionWorldChange>();
        s.WorldsChanged += changes.Add;

        t.CurrentSim = FarEast;
        t.Raise(new SimConnected(FarEast, 256, 256));

        Assert.Same(anchor, s.World);                     // what the presentation was built on carries on
        Assert.True(anchor.Epoch > epoch);
        Assert.Equal(Vector3.Zero, anchor.OriginOffset);
        Assert.Single(s.Worlds);
        Assert.Equal(FarEast, s.Worlds[0].Sim);
        Assert.Contains(new RegionWorldChange(RegionWorldChangeKind.Removed, East), changes);
        crossedInto.ApplyObject(East, Prim(1));           // it was disposed
        Assert.Empty(crossedInto.Snapshot().Objects);
    }

    [Fact]
    public async Task When_the_library_drops_the_anchor_region_its_world_is_emptied_but_kept_for_the_next_landing()
    {
        var (s, t) = await InWorld();
        using var _s = s;
        var anchor = s.World;
        anchor.ApplyObject(Home, Prim(1));
        Connect(t, East);
        t.CurrentSim = East;
        t.Raise(new SimConnected(East, 256, 256));
        var resets = new List<WorldChange>();
        anchor.Changed += resets.Add;

        t.Raise(new SimDisconnected(Home));

        Assert.DoesNotContain(s.Worlds, w => w.Sim == Home);
        Assert.Empty(anchor.Snapshot().Objects);
        Assert.Contains(resets, c => c.Kind == WorldChangeKind.RegionReset);
        t.CurrentSim = FarEast;
        t.Raise(new SimConnected(FarEast, 256, 256));
        Assert.Same(anchor, s.World);                     // not disposed: it takes the next landing
        anchor.ApplyObject(FarEast, Prim(2));
        Assert.Single(anchor.Snapshot().Objects);
    }

    // ---------------- lifetime ----------------

    [Fact]
    public async Task Disposing_the_session_disposes_every_world()
    {
        var (s, t) = await InWorld();
        Connect(t, East);
        var eastWorld = Of(s, East).World;
        eastWorld.ApplyObject(East, Prim(1));

        s.Dispose();

        eastWorld.ApplyObject(East, Prim(2));
        Assert.Empty(eastWorld.Snapshot().Objects);
    }
}
