// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Map;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The world map model: the window (centre, zoom, pan), the tiles and map blocks it needs, the regions it learns, search, the chosen
/// spot and the teleport it offers, and friends found on the map. Fixture regions with neutral names; no grid, no engine. Every member
/// builds its own model, so the class runs in parallel.
/// </summary>
public class WorldMapShellTests
{
    private const float W = 800, H = 600;
    private static readonly Guid Friend = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Stranger = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abce");

    private sealed class Rig
    {
        public readonly MapLinks Links = new();
        public readonly List<(int, int, int, int)> Blocks = new();
        public readonly List<string> Names = new();
        public readonly List<Guid> FriendAsks = new();
        public readonly List<(ulong Handle, Vector3 Position)> Teleports = new();
        public readonly List<MapTileKey> TileAsks = new();
        public int Homes;
        public HashSet<Guid> Granting = new();
        public (ulong, Vector3)? Own = null;
        public readonly WorldMapModel Map;
        public int Changes;

        public Rig(double scale = WorldMapModel.DefaultScale)
        {
            Links.RequestBlocks = (a, b, c, d) => Blocks.Add((a, b, c, d));
            Links.RequestName = Names.Add;
            Links.RequestFriend = FriendAsks.Add;
            Links.FriendGrantsMap = Granting.Contains;
            Links.Teleport = (h, p) => { Teleports.Add((h, p)); return true; };
            Links.Home = () => { Homes++; return true; };
            Links.Own = () => Own;
            Links.RequestTile = k => { TileAsks.Add(k); return MapTileState.Loading; };
            Map = new WorldMapModel(Links, scale);
            Map.Changed += () => Changes++;
        }

        public void Region(string name, int gx, int gy, int size = 256, int access = 13) =>
            Map.Receive(new MapBlockSize(gx, gy, size, size, name, Guid.NewGuid(), access));
    }

    private static (double X, double Y) CentreOfCell(int gx, int gy) => (gx * 256.0 + 128, gy * 256.0 + 128);

    // ---------------- the window ----------------

    [Fact]
    public void The_map_starts_at_the_official_default_zoom_and_a_new_zoom_is_kept_within_the_limits_and_moves_the_level()
    {
        var r = new Rig();
        Assert.Equal(128, r.Map.PixelsPerRegion);
        Assert.Equal(2, r.Map.Level);
        r.Map.SetScale(32);
        Assert.Equal(4, r.Map.Level);
        r.Map.SetScale(1);
        Assert.Equal(WorldMapModel.MinScale, r.Map.PixelsPerRegion);
        r.Map.SetScale(1_000_000);
        Assert.Equal(WorldMapModel.MaxScale, r.Map.PixelsPerRegion);
        Assert.Equal(1, r.Map.Level);
        r.Map.SetScale(double.NaN);
        Assert.Equal(WorldMapModel.MaxScale, r.Map.PixelsPerRegion);   // not a number: unchanged
        Assert.Equal(40, new Rig(40).Map.PixelsPerRegion);
        Assert.Equal(WorldMapModel.MaxScale, new Rig(1e9).Map.PixelsPerRegion);
    }

    [Fact]
    public void Screen_and_world_agree_north_is_up_and_the_centre_is_the_middle_of_the_window()
    {
        var r = new Rig();
        var (cx, cy) = CentreOfCell(1000, 1000);
        r.Map.CentreOnMetres(cx, cy);
        var mid = r.Map.ToWorld(W / 2, H / 2, W, H);
        Assert.Equal(cx, mid.X, 6);
        Assert.Equal(cy, mid.Y, 6);
        var east = r.Map.ToWorld(W / 2 + 128, H / 2, W, H);     // 128 pixels is one region (256 m) at the default zoom
        Assert.Equal(cx + 256, east.X, 6);
        var up = r.Map.ToWorld(W / 2, H / 2 - 64, W, H);        // up is north
        Assert.Equal(cy + 128, up.Y, 6);
        var (px, py) = r.Map.ToScreen(cx + 256, cy + 128, W, H);
        Assert.Equal(W / 2 + 128, px, 3);
        Assert.Equal(H / 2 - 64, py, 3);
    }

    [Fact]
    public void Dragging_moves_the_map_with_the_pointer_and_a_zoom_keeps_the_point_under_the_pointer_fixed()
    {
        var r = new Rig();
        var (cx, cy) = CentreOfCell(1000, 1000);
        r.Map.CentreOnMetres(cx, cy);
        r.Map.Pan(100, 50);                                       // the content follows the pointer: right and down
        Assert.Equal(cx - 100 * 2, r.Map.CentreX, 6);             // 2 m a pixel at the default zoom
        Assert.Equal(cy + 50 * 2, r.Map.CentreY, 6);

        var pivot = (X: 300f, Y: 200f);
        var before = r.Map.ToWorld(pivot.X, pivot.Y, W, H);
        r.Map.ZoomBy(2, pivot.X, pivot.Y, W, H);
        Assert.Equal(256, r.Map.PixelsPerRegion);
        var after = r.Map.ToWorld(pivot.X, pivot.Y, W, H);
        Assert.Equal(before.X, after.X, 4);
        Assert.Equal(before.Y, after.Y, 4);
        r.Map.ZoomBy(1e6, pivot.X, pivot.Y, W, H);
        Assert.Equal(WorldMapModel.MaxScale, r.Map.PixelsPerRegion);
        var end = r.Map.ToWorld(pivot.X, pivot.Y, W, H);
        Assert.Equal(before.X, end.X, 3);
        r.Map.ZoomBy(0, pivot.X, pivot.Y, W, H);                 // a zero or negative factor changes nothing
        Assert.Equal(WorldMapModel.MaxScale, r.Map.PixelsPerRegion);
    }

    [Fact]
    public void The_map_centres_on_the_own_position_when_asked_and_does_nothing_when_it_is_not_known()
    {
        var r = new Rig();
        r.Map.CentreOnOwn();
        Assert.Equal(0, r.Map.CentreX);
        r.Own = (MapMath.HandleOf(1002, 1003), new Vector3(10, 20, 30));
        r.Map.CentreOnOwn();
        Assert.Equal(1002 * 256 + 10, r.Map.CentreX, 6);
        Assert.Equal(1003 * 256 + 20, r.Map.CentreY, 6);
    }

    // ---------------- tiles ----------------

    [Fact]
    public void The_tiles_of_the_right_level_cover_the_window_and_each_is_asked_for()
    {
        var r = new Rig();
        var (cx, cy) = CentreOfCell(1001, 1001);
        r.Map.CentreOnMetres(cx, cy);
        r.Map.Update(W, H);
        Assert.NotEmpty(r.Map.Tiles);
        Assert.All(r.Map.Tiles, t =>
        {
            Assert.Equal(2, t.Key.Level);
            Assert.Equal(0, t.Key.X % 2);
            Assert.Equal(0, t.Key.Y % 2);
            Assert.Equal(256, t.Size, 3);                           // two regions of 128 pixels
        });
        Assert.True(r.Map.Tiles.Min(t => t.X) <= 0);
        Assert.True(r.Map.Tiles.Min(t => t.Y) <= 0);
        Assert.True(r.Map.Tiles.Max(t => t.X + t.Size) >= W);
        Assert.True(r.Map.Tiles.Max(t => t.Y + t.Size) >= H);
        Assert.Equal(r.Map.Tiles.Select(t => t.Key).Distinct().Count(), r.Map.Tiles.Count);
        Assert.Equal(r.Map.Tiles.Select(t => t.Key).ToHashSet(), r.TileAsks.ToHashSet());
        // the region at the centre is inside the tile that holds it
        var centreTile = MapMath.TileFor(1001, 1001, 2);
        var placed = r.Map.Tiles.Single(t => t.Key == centreTile);
        var (rx, ry) = r.Map.ToScreen(1001 * 256.0, 1001 * 256.0 + 256, W, H);   // the region's top-left corner
        Assert.InRange(rx, placed.X - 0.01f, placed.X + placed.Size);
        Assert.InRange(ry, placed.Y - 0.01f, placed.Y + placed.Size);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(1024)]
    [InlineData(4096)]
    public void However_far_the_zoom_the_number_of_tiles_for_the_window_stays_small(double scale)
    {
        var r = new Rig(scale);
        r.Map.CentreOnMetres(1001 * 256 + 100, 1001 * 256 + 100);
        r.Map.Update(W, H);
        Assert.InRange(r.Map.Tiles.Count, 1, 24);
        Assert.True(r.Map.Tiles.Min(t => t.X) <= 0 && r.Map.Tiles.Max(t => t.X + t.Size) >= W);
        Assert.True(r.Map.Tiles.Min(t => t.Y) <= 0 && r.Map.Tiles.Max(t => t.Y + t.Size) >= H);
    }

    [Fact]
    public void A_window_with_no_area_needs_no_tiles_and_asks_for_nothing()
    {
        var r = new Rig();
        r.Map.Update(0, 0);
        r.Map.Update(float.NaN, 100);
        Assert.Empty(r.Map.Tiles);
        Assert.Empty(r.TileAsks);
        Assert.Empty(r.Blocks);
    }

    // ---------------- regions ----------------

    [Fact]
    public void Map_blocks_are_asked_for_once_in_aligned_blocks_of_four_and_only_where_region_details_are_drawn()
    {
        var r = new Rig();                                          // level 2: details are drawn
        r.Map.CentreOnMetres(1001 * 256 + 100, 1001 * 256 + 100);
        r.Map.Update(W, H);
        Assert.NotEmpty(r.Blocks);
        Assert.All(r.Blocks, b =>
        {
            Assert.Equal(0, b.Item1 % 4);
            Assert.Equal(0, b.Item2 % 4);
            Assert.Equal(b.Item1 + 3, b.Item3);
            Assert.Equal(b.Item2 + 3, b.Item4);
        });
        Assert.Contains(r.Blocks, b => b.Item1 <= 1001 && 1001 <= b.Item3 && b.Item2 <= 1001 && 1001 <= b.Item4);
        Assert.Equal(r.Blocks.Distinct().Count(), r.Blocks.Count);
        int asked = r.Blocks.Count;
        Assert.Equal(asked, r.Map.BlockRequests);
        r.Map.Update(W, H);
        Assert.Equal(asked, r.Blocks.Count);                        // nothing is asked for twice

        var far = new Rig(16);                                      // level 5: tiles only
        far.Map.CentreOnMetres(1001 * 256, 1001 * 256);
        far.Map.Update(W, H);
        Assert.Empty(far.Blocks);
        Assert.NotEmpty(far.Map.Tiles);
    }

    [Fact]
    public void A_region_reply_is_remembered_with_its_name_size_picture_and_access_and_a_cell_with_no_region_is_not()
    {
        var r = new Rig();
        var picture = Guid.NewGuid();
        r.Map.Receive(new MapBlockSize(1000, 1001, 512, 512, "Example Region", picture, 21));
        r.Map.Receive(new MapBlockSize(1004, 1001, 0, 0, "Old Region", null, 13));         // a legacy grid states no size
        r.Map.Receive(new MapBlockSize(1005, 1001, 256, 256, "", null, 255));              // a cell with no region
        r.Map.Receive(new MapBlockSize(1006, 1001, 256, 256, null, null, 0));
        Assert.Equal(2, r.Map.Regions.Count);
        var big = r.Map.RegionAt(1000, 1001)!;
        Assert.Equal(new MapRegion("Example Region", 1000, 1001, 512, 512, picture, 21), big);
        Assert.Equal(256, r.Map.RegionAt(1004, 1001)!.SizeX);
        Assert.Null(r.Map.RegionAt(1005, 1001));
        Assert.Null(r.Map.RegionAt(1006, 1001));
        Assert.Equal(big, r.Map.RegionAt(1001, 1002));                                     // a cell inside a larger region belongs to it
        r.Region("Renamed", 1000, 1001);
        Assert.Equal("Renamed", r.Map.RegionAt(1000, 1001)!.Name);                         // a newer reply replaces the old one
        Assert.True(r.Changes > 0);
    }

    [Fact]
    public void A_variable_size_region_answers_for_every_cell_it_covers()
    {
        var r = new Rig();
        r.Region("Big Region", 1000, 1000, 512);
        Assert.NotNull(r.Map.RegionAt(1001, 1001));
        Assert.Equal("Big Region", r.Map.RegionAt(1001, 1001)!.Name);
        Assert.Equal("Big Region", r.Map.RegionAt(1000, 1001)!.Name);
        Assert.Null(r.Map.RegionAt(1002, 1000));
        Assert.Null(r.Map.RegionAt(999, 1000));
    }

    [Fact]
    public void Names_are_drawn_only_when_a_region_is_at_least_96_pixels_wide_and_only_for_regions_in_the_window()
    {
        var r = new Rig();
        r.Region("Near Region", 1001, 1001);
        r.Region("Far Region", 1100, 1100);
        r.Map.CentreOnMetres(1001 * 256 + 128, 1001 * 256 + 128);
        var labels = r.Map.Labels(W, H);
        var near = Assert.Single(labels);
        Assert.Equal("Near Region", near.Region.Name);
        Assert.Equal(W / 2, near.X, 2);                              // the label sits at the region's middle
        Assert.Equal(H / 2, near.Y, 2);
        r.Map.SetScale(64);
        Assert.Empty(r.Map.Labels(W, H));
        r.Map.SetScale(96);
        Assert.Single(r.Map.Labels(W, H));
    }

    // ---------------- search ----------------

    [Fact]
    public void A_search_asks_the_grid_by_name_and_lists_the_regions_that_match_what_was_typed()
    {
        var r = new Rig();
        Assert.False(r.Map.Search("   "));
        Assert.Equal(("map.search.empty", null), r.Map.Status);
        Assert.Empty(r.Names);
        Assert.True(r.Map.Search("  Exam "));
        Assert.Equal(["Exam"], r.Names);
        Assert.Null(r.Map.Status);
        r.Region("Zebra Example", 1002, 1000);                       // a reply for something else (an area request) is not a result
        r.Region("Example South", 1001, 1000);
        r.Region("example north", 1000, 1000);
        Assert.Equal(["example north", "Example South"], r.Map.Results.Select(x => x.Name).ToArray());
        Assert.Equal(3, r.Map.Regions.Count);                         // but it is remembered for the map
        Assert.True(r.Map.Search("Zebra"));
        Assert.Empty(r.Map.Results);                                  // a new search starts a new list
        r.Region("Zebra Example", 1002, 1000);
        Assert.Single(r.Map.Results);
    }

    [Fact]
    public void A_search_keeps_at_most_twenty_results()
    {
        var r = new Rig();
        r.Map.Search("R");
        for (int i = 0; i < 30; i++)
            r.Region("R" + i.ToString("00"), 1000 + i, 1000);
        Assert.Equal(20, r.Map.Results.Count);
    }

    [Fact]
    public void Choosing_a_result_centres_the_map_on_it_and_selects_its_middle_without_teleporting()
    {
        var r = new Rig();
        r.Region("Example Region", 1002, 1003, 512);
        var region = r.Map.RegionAt(1002, 1003)!;
        Assert.True(r.Map.Choose(region));
        Assert.Equal(1002 * 256 + 256, r.Map.CentreX, 6);            // the middle of a 512 m region
        Assert.Equal(1003 * 256 + 256, r.Map.CentreY, 6);
        Assert.Equal(new MapSpot(MapMath.HandleOf(1002, 1003), "Example Region", new Vector3(256, 256, 0)), r.Map.Selected);
        Assert.Empty(r.Teleports);
        Assert.Equal(0, r.Homes);
    }

    // ---------------- the chosen spot ----------------

    [Fact]
    public void A_click_on_a_known_region_chooses_that_spot_and_changes_nothing_else()
    {
        var r = new Rig();
        r.Region("Example Region", 1001, 1001);
        r.Map.CentreOnMetres(1001 * 256 + 128, 1001 * 256 + 128);
        Assert.True(r.Map.Click(W / 2 + 32, H / 2 - 32, W, H));      // 64 m east and 64 m north of the middle
        Assert.Equal(new MapSpot(MapMath.HandleOf(1001, 1001), "Example Region", new Vector3(192, 192, 0)), r.Map.Selected);
        Assert.Empty(r.Teleports);                                    // choosing a spot has no effect until the button
        Assert.Equal(0, r.Homes);
    }

    [Fact]
    public void A_click_where_there_is_no_known_region_chooses_nothing_and_says_so()
    {
        var r = new Rig();
        r.Region("Example Region", 1001, 1001);
        r.Map.CentreOnMetres(1001 * 256 + 128, 1001 * 256 + 128);
        Assert.True(r.Map.Click(W / 2, H / 2, W, H));
        var chosen = r.Map.Selected;
        Assert.False(r.Map.Click(W / 2 + 400, H / 2, W, H));          // the cell next door: not known
        Assert.Equal(("map.no_region", null), r.Map.Status);
        Assert.Equal(chosen, r.Map.Selected);                          // the earlier choice stays
        Assert.False(r.Map.Click(float.NaN, 5, W, H));
    }

    [Fact]
    public void The_teleport_button_sends_the_chosen_spot_by_handle_and_position_and_never_without_a_choice()
    {
        var r = new Rig();
        Assert.False(r.Map.TeleportToSelected());
        Assert.Equal(("map.nothing_selected", null), r.Map.Status);
        Assert.Empty(r.Teleports);
        r.Region("Example Region", 1001, 1001);
        r.Map.CentreOnMetres(1001 * 256 + 128, 1001 * 256 + 128);
        r.Map.Click(W / 2, H / 2, W, H);
        Assert.True(r.Map.TeleportToSelected());
        var sent = Assert.Single(r.Teleports);
        Assert.Equal(MapMath.HandleOf(1001, 1001), sent.Handle);
        Assert.Equal(128, sent.Position.X, 1);
        Assert.Equal(128, sent.Position.Y, 1);
        r.Links.Teleport = (_, _) => false;                            // the model refuses (a teleport is going on)
        Assert.False(r.Map.TeleportToSelected());
    }

    [Fact]
    public void Home_in_the_map_window_sends_home_through_the_teleport()
    {
        var r = new Rig();
        Assert.True(r.Map.TeleportHome());
        Assert.Equal(1, r.Homes);
    }

    // ---------------- markers and friends ----------------

    [Fact]
    public void The_own_position_and_the_chosen_spot_are_marked_where_they_are()
    {
        var r = new Rig();
        r.Region("Example Region", 1001, 1001);
        r.Own = (MapMath.HandleOf(1001, 1001), new Vector3(128, 128, 30));
        r.Map.CentreOnMetres(1001 * 256 + 128, 1001 * 256 + 128);
        var own = Assert.Single(r.Map.Markers(W, H));
        Assert.Equal(MapMarkerKind.Own, own.Kind);
        Assert.Equal(W / 2, own.X, 2);
        Assert.Equal(H / 2, own.Y, 2);
        r.Map.Click(W / 2 + 32, H / 2, W, H);
        var kinds = r.Map.Markers(W, H).Select(m => m.Kind).ToArray();
        Assert.Contains(MapMarkerKind.Selected, kinds);
        Assert.Contains(MapMarkerKind.Own, kinds);
        r.Own = null;
        Assert.DoesNotContain(MapMarkerKind.Own, r.Map.Markers(W, H).Select(m => m.Kind));
    }

    [Fact]
    public void A_friend_who_does_not_share_their_place_is_not_asked_for()
    {
        var r = new Rig();
        Assert.False(r.Map.ShowFriend(Friend));
        Assert.Equal(("map.friend_not_shared", null), r.Map.Status);
        Assert.Empty(r.FriendAsks);
        Assert.False(r.Map.ShowFriend(Guid.Empty));
        Assert.Empty(r.FriendAsks);
    }

    [Fact]
    public void A_friend_who_shares_their_place_is_asked_for_and_the_answer_marks_them_and_centres_the_map()
    {
        var r = new Rig();
        r.Granting.Add(Friend);
        r.Region("Friend Region", 1005, 1006);
        Assert.True(r.Map.ShowFriend(Friend));
        Assert.Equal([Friend], r.FriendAsks);
        r.Map.Receive(new FriendLocated(Stranger, MapMath.HandleOf(1009, 1009), new Vector3(1, 1, 0)));   // not asked for: ignored
        Assert.Empty(r.Map.Markers(W, H));
        r.Map.Receive(new FriendLocated(Friend, MapMath.HandleOf(1005, 1006), new Vector3(100, 50, 0)));
        Assert.Equal(1005 * 256 + 100, r.Map.CentreX, 6);
        Assert.Equal(1006 * 256 + 50, r.Map.CentreY, 6);
        var marker = Assert.Single(r.Map.Markers(W, H));
        Assert.Equal(MapMarkerKind.Friend, marker.Kind);
        Assert.Equal(Friend, marker.Id);
        Assert.Equal(W / 2, marker.X, 2);
        Assert.Equal(H / 2, marker.Y, 2);
        Assert.Equal(new MapSpot(MapMath.HandleOf(1005, 1006), "Friend Region", new Vector3(100, 50, 0)), r.Map.Selected);
        Assert.Empty(r.Teleports);                                     // finding a friend does not teleport
        // a second answer without a new ask moves the marker no more
        r.Map.Receive(new FriendLocated(Friend, MapMath.HandleOf(1007, 1007), new Vector3(1, 1, 0)));
        Assert.Equal(1005 * 256 + 100, r.Map.CentreX, 6);
    }

    // ---------------- opening on the agent's own place ----------------

    private static bool AsksForTileAt(Rig r, int gx, int gy) =>
        r.TileAsks.Any(k => gx >= k.X && gx < k.X + MapMath.RegionsPerTile(k.Level) && gy >= k.Y && gy < k.Y + MapMath.RegionsPerTile(k.Level));

    [Fact]
    public void A_map_that_has_not_been_moved_opens_on_the_agents_own_region_and_position_with_its_tiles()
    {
        var r = new Rig { Own = (MapMath.HandleOf(1000, 1001), new Vector3(10, 20, 30)) };
        r.Map.Update(W, H);
        Assert.Equal(1000 * 256 + 10, r.Map.CentreX, 6);
        Assert.Equal(1001 * 256 + 20, r.Map.CentreY, 6);
        Assert.True(AsksForTileAt(r, 1000, 1001), "the tile that holds the agent's region is asked for");
    }

    [Fact]
    public void The_map_waits_for_the_agents_place_and_centres_on_it_at_the_first_update_after_it_is_known()
    {
        var r = new Rig();
        r.Map.Update(W, H);
        Assert.Equal((0.0, 0.0), (r.Map.CentreX, r.Map.CentreY));   // no place yet: nothing to open on
        r.Own = (MapMath.HandleOf(1000, 1001), new Vector3(10, 20, 30));
        r.Map.Update(W, H);
        Assert.Equal(1000 * 256 + 10, r.Map.CentreX, 6);
    }

    [Fact]
    public void A_map_the_person_has_moved_or_zoomed_or_centred_is_not_taken_back_to_the_agent()
    {
        foreach (var move in new Action<Rig>[]
                 {
                     rig => rig.Map.Pan(50, 50),
                     rig => rig.Map.ZoomBy(2, 100, 100, W, H),
                     rig => rig.Map.CentreOnMetres(5000, 6000),
                 })
        {
            var r = new Rig();
            move(r);
            double x = r.Map.CentreX, y = r.Map.CentreY;
            r.Own = (MapMath.HandleOf(1000, 1001), new Vector3(10, 20, 30));
            r.Map.Update(W, H);
            Assert.Equal((x, y), (r.Map.CentreX, r.Map.CentreY));
        }
    }

    [Fact]
    public void The_map_opens_on_the_agent_once_and_later_updates_leave_it_where_the_person_put_it()
    {
        var r = new Rig { Own = (MapMath.HandleOf(1000, 1001), new Vector3(10, 20, 30)) };
        r.Map.Update(W, H);
        r.Map.Pan(100, 0);
        double x = r.Map.CentreX;
        r.Map.Update(W, H);
        Assert.Equal(x, r.Map.CentreX, 6);
    }

    // ---------------- words ----------------

    [Fact]
    public void Every_status_the_map_can_show_exists_in_the_string_table()
    {
        var en = StringTable.English();
        foreach (var key in new[] { "map.search.empty", "map.no_region", "map.nothing_selected", "map.friend_not_shared" })
            Assert.True(en.Has(key), key);
    }
}
