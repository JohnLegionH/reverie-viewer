// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A region is known by its grid and its map spot, not by its map handle alone. Two grids have their own maps, so a region visited
/// over Hypergrid can sit on the same map spot as a region at home: here a 1024 m home region at 1000,1000 (with a 256 m neighbour
/// east of it) and a 256 m visited region at 1000,1000 on another grid. Landing there drops the home region and its neighbour, sizes
/// the visited region from its own messages, draws nothing of the home region, and ignores the home grid's late map answers.
/// </summary>
public class HypergridOverlapTests
{
    private const string HomeGrid = "home.example.org:9000";
    private const string VisitGrid = "visit.example.org:9100";

    private static ulong Handle(int x, int y) => (ulong)(x * 256L) << 32 | (ulong)(y * 256L);

    private static (ViewerSession S, FakeTransport T, SimRef Alder, SimRef Birch) AtHome()
    {
        var t = new FakeTransport { OnLogin = (_, _) => Task.FromResult(FakeTransport.Ok(Guid.NewGuid(), 1024, 1024)) };
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://home.example.org:9000/", Neighbours = true }, t, new CoreLog(new ListSink(), "test"));
        Assert.True(s.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        var alder = new SimRef(Handle(1000, 1000), "Alder", HomeGrid);
        var birch = new SimRef(Handle(1004, 1000), "Birch", HomeGrid);
        t.DriveToInWorld(alder);   // the library reports 256 for a var region; the login reply says 1024
        t.Raise(new SimConnected(birch, 256, 256));
        t.Raise(new ObjectUpdated(alder, new PrimData(77, Guid.NewGuid(), 0, 0, new Vector3(10, 20, 30), Quaternion.Identity, Vector3.One), false, null));
        return (s, t, alder, birch);
    }

    private static SimRef Land(FakeTransport t, string grid = VisitGrid, string name = "Visit Landing")
    {
        var visit = new SimRef(Handle(1000, 1000), name, grid);
        t.CurrentSim = visit;
        t.Raise(new SimConnected(visit, 256, 256));
        t.Raise(new MovementComplete(visit));
        return visit;
    }

    [Fact]
    public void At_home_the_login_region_is_sized_from_the_login_reply_and_its_neighbour_is_held()
    {
        var (s, _, alder, birch) = AtHome();
        Assert.Equal(1024, s.Region!.SizeX);
        Assert.Contains(s.Worlds, w => ReferenceEquals(w.Sim, birch));
        Assert.Contains(s.Worlds, w => ReferenceEquals(w.Sim, alder) && w.IsCurrent);
    }

    [Fact]
    public void A_visited_region_on_the_home_regions_map_spot_is_sized_from_its_own_message_not_the_login_reply()
    {
        var (s, t, _, _) = AtHome();
        Land(t);
        Assert.Equal("Visit Landing", s.Region!.Name);
        Assert.Equal(256, s.Region.SizeX);
        Assert.Equal(256, s.Region.SizeY);
        Assert.Equal(VisitGrid, s.Region.Grid);
    }

    [Fact]
    public void The_home_region_and_its_neighbour_are_dropped_and_none_of_their_objects_are_drawn()
    {
        var (s, t, alder, birch) = AtHome();
        var visit = Land(t);
        var only = Assert.Single(s.Worlds);
        Assert.Same(visit, only.Sim);
        Assert.DoesNotContain(s.Worlds, w => ReferenceEquals(w.Sim, birch) || ReferenceEquals(w.Sim, alder));
        Assert.DoesNotContain(77u, s.World.Snapshot().Objects.Keys);
        // a late update from the home region (same map handle) is not drawn in the visited one
        t.Raise(new ObjectUpdated(alder, new PrimData(78, Guid.NewGuid(), 0, 0, new Vector3(1, 2, 3), Quaternion.Identity, Vector3.One), false, null));
        Assert.DoesNotContain(78u, s.World.Snapshot().Objects.Keys);
        Assert.False(s.IsEnded);
    }

    [Fact]
    public void A_late_map_answer_from_the_home_grid_does_not_resize_or_rename_the_visited_region()
    {
        var (s, t, _, _) = AtHome();
        Land(t);
        t.Raise(new MapBlockSize(1000, 1000, 1024, 1024, "Alder", Grid: HomeGrid));
        Assert.Equal(256, s.Region!.SizeX);
        Assert.False(s.Region.SizeAuthoritative);
        Assert.Equal("Visit Landing", s.Region.Name);
        // the visited grid's own answer is taken
        t.Raise(new MapBlockSize(1000, 1000, 256, 256, "Visit Landing", Grid: VisitGrid));
        Assert.True(s.Region!.SizeAuthoritative);
    }

    [Fact]
    public void Coming_home_to_the_login_region_sizes_it_from_the_login_reply_again()
    {
        var (s, t, _, _) = AtHome();
        Land(t);
        var back = new SimRef(Handle(1000, 1000), "Alder", HomeGrid);
        t.CurrentSim = back;
        t.Raise(new SimConnected(back, 256, 256));
        Assert.Equal(1024, s.Region!.SizeX);
        Assert.Equal(HomeGrid, s.Region.Grid);
    }

    [Fact]
    public void A_map_block_from_another_grid_is_not_applied_to_a_region()
    {
        var region = RegionSize.AtConnect(new SimRef(Handle(1000, 1000), "Visit Landing", VisitGrid), 256, 256, 0, 0);
        Assert.Same(region, RegionSize.FromMapBlock(region, new MapBlockSize(1000, 1000, 1024, 1024, "Alder", Grid: HomeGrid)));
        Assert.True(RegionSize.FromMapBlock(region, new MapBlockSize(1000, 1000, 512, 512, "Visit Landing", Grid: VisitGrid)).SizeAuthoritative);
        Assert.True(RegionSize.FromMapBlock(region, new MapBlockSize(1000, 1000, 512, 512, "Visit Landing")).SizeAuthoritative);   // unknown grid: as before
    }

    // ---------------- the transport's grid keys ----------------

    [Fact]
    public void The_grid_moves_with_a_teleport_toward_another_grid_and_back_when_it_fails()
    {
        var g = new GridTracker(HomeGrid);
        Assert.Equal(HomeGrid, g.Current);
        g.Leaving(VisitGrid);
        Assert.Equal(VisitGrid, g.Current);
        g.Failed();
        Assert.Equal(HomeGrid, g.Current);
        g.Leaving(VisitGrid);
        g.Arrived();
        g.Failed();   // nothing pending: stays
        Assert.Equal(VisitGrid, g.Current);
        g.Leaving(null);   // a teleport inside the grid the agent is on
        Assert.Equal(VisitGrid, g.Current);
        g.Leaving(g.Home);
        g.Arrived();
        Assert.Equal(HomeGrid, g.Current);
        Assert.False(g.Visiting);
    }

    [Fact]
    public void The_grid_the_region_says_it_is_on_corrects_the_current_grid()
    {
        var g = new GridTracker(HomeGrid);
        g.Learned("http://hg.home.example.org:8002/");   // the home region names its gatekeeper: another name for home
        Assert.False(g.Visiting);
        Assert.True(g.IsHome("hg.home.example.org:8002"));
        g.Leaving(null);   // an offer: where it leads is not known when it is accepted
        g.Arrived();
        g.Learned("http://visit.example.org:9100/");
        Assert.Equal(VisitGrid, g.Current);
        Assert.True(g.Visiting);
        g.Learned("");   // nothing said: unchanged
        Assert.Equal(VisitGrid, g.Current);
        g.Leaving("hg.home.example.org:8002");   // going home by the home grid's other name
        g.Arrived();
        Assert.Equal(HomeGrid, g.Current);
    }
}
