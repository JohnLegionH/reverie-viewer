// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;
using Viewer.Core.Links;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell.Travel;
using Viewer.Shell;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Travel from the interface: the location field (the "Go to" box) and the world map's search take every address form; a place on
/// another grid is found through the region's map lookup of "host:port:Region" and then teleported to like any place; the location
/// shows the visited grid's host; teleport history keeps the grid; the map starts afresh on another grid; a landmark opens.
/// </summary>
public class HypergridTravelShellTests
{
    private const string Home = "home.example.org:9000";
    private const string Visit = "visit.example.org:9100";

    private static (TeleportModel Model, List<ActionIntent> Sent) Model()
    {
        var sent = new List<ActionIntent>();
        var t = new FakeTransport();
        var pipeline = new ActionPipeline(() => t, () => true, new CoreLog(new ListSink(), "test"));
        var m = new TeleportModel(Guid.NewGuid(), new NameCache(), i => { sent.Add(i); return pipeline.Submit(i); }, () => DateTimeOffset.UnixEpoch);
        return (m, sent);
    }

    // ---------------- the location field ----------------

    [Theory]
    [InlineData("hop://visit.example.org:9100/Visit%20Landing/10/20/30")]
    [InlineData("visit.example.org:9100:Visit Landing/10/20/30")]
    [InlineData("http://visit.example.org:9100/Visit Landing/10/20/30")]
    public void The_location_field_keeps_the_grid_of_a_hypergrid_address(string text)
    {
        var a = LocationField.Parse(text)!;
        Assert.Equal("Visit Landing", a.Region);
        Assert.Equal(new Vector3(10, 20, 30), a.Position);
        Assert.Equal(Visit, a.Grid);
    }

    [Fact]
    public void Host_port_space_region_is_read_by_the_location_field()
    {
        var a = LocationField.Parse("visit.example.org:9100 Visit Landing")!;
        Assert.Equal(("Visit Landing", Visit), (a.Region, a.Grid));
        Assert.Null(a.Position);
    }

    [Fact]
    public void A_hop_address_with_no_region_is_refused_by_the_location_field() => Assert.Null(LocationField.Parse("hop://visit.example.org:9100/"));

    [Fact]
    public void While_visiting_the_location_shows_the_region_with_its_host()
    {
        var parts = LocationField.Display("Visit Landing", null, new Vector3(10, 20, 30), 0, Visit)!;
        Assert.Equal($"Visit Landing ({Visit})", parts.Place);
        Assert.Equal("Alder", LocationField.Display("Alder", null, Vector3.Zero, 0)!.Region);
    }

    // ---------------- going there ----------------

    [Fact]
    public void A_place_on_another_grid_is_looked_up_by_host_port_region_with_its_grid()
    {
        var (m, sent) = Model();
        Assert.True(m.ToAddress(TravelAddressParser.Parse("hop://visit.example.org:9100/Visit%20Landing/10/20/30")!, Home));
        var tp = Assert.IsType<TeleportRegionIntent>(Assert.Single(sent));
        Assert.Equal("visit.example.org:9100:Visit Landing", tp.RegionName);
        Assert.Equal(Visit, tp.Grid);
        Assert.Equal(new Vector3(10, 20, 30), tp.Position);
        Assert.Equal(TeleportStage.Requested, m.Stage);
    }

    [Fact]
    public void A_place_on_the_grid_the_agent_is_on_goes_by_its_plain_name()
    {
        var (m, sent) = Model();
        Assert.True(m.ToAddress(TravelAddressParser.Parse($"hop://{Home}/Alder")!, Home));
        var tp = Assert.IsType<TeleportRegionIntent>(Assert.Single(sent));
        Assert.Equal("Alder", tp.RegionName);
        Assert.Null(tp.Grid);
        Assert.Equal(new Vector3(128, 128, 30), tp.Position);   // no coordinates: the middle, above ground, as the location field did
    }

    [Fact]
    public void The_grids_own_refusal_text_is_the_failure_shown()
    {
        var (m, _) = Model();
        m.ToAddress(TravelAddressParser.Parse("visit.example.org:9100:Nowhere")!, Home);
        m.Receive(new TeleportProgressed(TeleportPhase.Failed, "No regions found with that name.", true));
        Assert.Equal(TeleportStage.Failed, m.Stage);
        Assert.Equal(("teleport.error.raw", "No regions found with that name."), m.Failure);
    }

    [Fact]
    public void Teleport_history_keeps_the_grid_so_back_returns_to_the_same_place()
    {
        var h = new TeleportHistory();
        h.Departed(new LocationAddress("Alder", new Vector3(1, 2, 3), Home));
        var back = h.Back()!;
        Assert.Equal(Home, back.Grid);
        var (m, sent) = Model();
        m.ToAddress(back.Travel, Visit);   // from the visited grid, Back goes home by the home grid's address
        var tp = Assert.IsType<TeleportRegionIntent>(Assert.Single(sent));
        Assert.Equal($"{Home}:Alder", tp.RegionName);
        Assert.Equal(new Vector3(1, 2, 3), tp.Position);
    }

    // ---------------- the world map ----------------

    [Fact]
    public void The_world_maps_search_teleports_to_a_hypergrid_address_through_the_lookup()
    {
        TravelAddress? asked = null;
        var names = new List<string>();
        var map = new WorldMapModel(new MapLinks { TeleportAddress = a => { asked = a; return true; }, RequestName = names.Add });
        Assert.True(map.Search("visit.example.org:9100:Visit Landing"));
        Assert.Equal(Visit, asked!.Grid);
        Assert.Equal("Visit Landing", asked.Region);
        Assert.Empty(names);   // not a name search on the grid the agent is on
        Assert.Equal(("map.search.hypergrid", (string?)$"Visit Landing ({Visit})"), map.Status);
    }

    [Fact]
    public void The_world_map_starts_afresh_on_another_grid_and_ignores_the_old_grids_late_answers()
    {
        var map = new WorldMapModel(new MapLinks());
        map.ShowGrid(Home);
        map.Receive(new MapBlockSize(1000, 1000, 1024, 1024, "Alder", Grid: Home));
        Assert.Contains(map.Regions, r => r.Name == "Alder");
        map.ShowGrid(Visit);
        Assert.Empty(map.Regions);
        map.Receive(new MapBlockSize(1000, 1000, 1024, 1024, "Alder", Grid: Home));   // late, from home
        Assert.Empty(map.Regions);
        map.Receive(new MapBlockSize(1000, 1000, 256, 256, "Visit Landing", Grid: Visit));
        Assert.Equal("Visit Landing", Assert.Single(map.Regions).Name);
        map.ShowGrid(Visit);   // the same grid again changes nothing
        Assert.Single(map.Regions);
    }

    [Fact]
    public void A_hypergrid_link_the_grid_made_for_a_lookup_is_not_a_region_on_the_map()
    {
        var map = new WorldMapModel(new MapLinks());
        map.Receive(new MapBlockSize(23456, 0, 256, 256, "visit.example.org:9100:Visit Landing"));
        Assert.Empty(map.Regions);
    }

    [Fact]
    public void A_hop_link_to_another_grid_that_cannot_be_read_is_not_shown_as_a_place_here()
    {
        var router = new Viewer.Shell.Links.LinkRouter(new Viewer.Core.Links.LinkGate(() => DateTimeOffset.UnixEpoch), () => "http://home.example.org:9000/");
        var a = router.Click("hop://visit.example.org:9100/Bad;Name/1/2/3")!;
        Assert.Equal(Viewer.Shell.Links.LinkActionKind.Note, a.Kind);
    }

    // ---------------- landmarks ----------------

    [Fact]
    public void A_landmark_opens_on_double_click() => Assert.True(InventoryOpening.Opens(ItemKind.Landmark));

    [Fact]
    public void A_landmark_teleport_carries_the_grid_the_landmark_names()
    {
        var (m, sent) = Model();
        var lm = Guid.NewGuid();
        Assert.True(m.ToLandmark(lm, Visit));
        Assert.Equal(new TeleportLandmarkIntent(lm, Visit), Assert.Single(sent));
    }

    [Fact]
    public void A_landmark_is_made_in_the_landmarks_folder_named_for_the_place()
    {
        var folder = Guid.NewGuid();
        var intent = LandmarkMaker.For(folder, "Visit Landing", "Beach", new Vector3(10.4f, 20.6f, 30f))!;
        Assert.Equal(folder, intent.FolderId);
        Assert.Equal("Beach", intent.Name);   // the parcel's name when it has one, as the official viewer names a new landmark
        Assert.Equal("Visit Landing (10, 21, 30)", intent.Description);
        Assert.Equal("Visit Landing", LandmarkMaker.For(folder, "Visit Landing", null, Vector3.Zero)!.Name);
        Assert.Null(LandmarkMaker.For(Guid.Empty, "Visit Landing", null, Vector3.Zero));
        Assert.Empty(intent.Validate());
    }
}
