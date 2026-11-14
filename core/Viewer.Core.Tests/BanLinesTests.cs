// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Session;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The ban lines: the wall's segments from a parcel's cell bitmap, its height, fade, pattern, geometry and the times it is on show.
/// Plain values, no engine and no grid; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class BanLinesTests
{
    private const int Cells = BanWall.CellsPerEdge;
    private const int Stride = Cells + 1;

    private static byte[] Bitmap(params (int x, int y)[] set)
    {
        var b = new byte[Cells * Cells / 8];
        foreach (var (x, y) in set) b[(x + y * Cells) / 8] |= (byte)(1 << ((x + y * Cells) % 8));
        return b;
    }

    private static int Count(byte[] seg, byte mask) => seg.Count(s => (s & mask) != 0);

    [Fact]
    public void One_cell_gives_its_four_edges()
    {
        var seg = BanWall.SegmentsFromBitmap(Bitmap((3, 5)));
        Assert.Equal(Stride * Stride, seg.Length);
        Assert.NotEqual(0, seg[3 + 5 * Stride] & BanWall.SouthMask);   // its south edge
        Assert.NotEqual(0, seg[3 + 6 * Stride] & BanWall.SouthMask);   // its north edge is the south edge of the row above
        Assert.NotEqual(0, seg[3 + 5 * Stride] & BanWall.WestMask);    // its west edge
        Assert.NotEqual(0, seg[4 + 5 * Stride] & BanWall.WestMask);    // its east edge is the west edge of the next column
        Assert.Equal(2, Count(seg, BanWall.SouthMask));
        Assert.Equal(2, Count(seg, BanWall.WestMask));
    }

    [Fact]
    public void An_edge_two_cells_share_is_not_drawn()
    {
        var seg = BanWall.SegmentsFromBitmap(Bitmap((3, 5), (4, 5)));
        Assert.Equal(0, seg[4 + 5 * Stride] & BanWall.WestMask);       // the shared edge
        Assert.Equal(2, Count(seg, BanWall.WestMask));                 // the outline's two ends
        Assert.Equal(4, Count(seg, BanWall.SouthMask));                // two cells wide: two on the bottom, two on the top
    }

    [Fact]
    public void An_empty_bitmap_gives_no_wall() =>
        Assert.Equal(0, BanWall.SegmentsFromBitmap(Bitmap()).Count(b => b != 0));

    [Theory]
    [InlineData(ParcelDenial.Banned, 5000f)]
    [InlineData(ParcelDenial.NotOnList, 50f)]
    [InlineData(ParcelDenial.NotInGroup, 50f)]
    public void A_ban_is_a_very_tall_wall_and_a_list_or_group_refusal_a_short_one(ParcelDenial denial, float height) =>
        Assert.Equal(height, BanWall.HeightFor(denial));

    [Theory]
    [InlineData(0f, 0.95f)]
    [InlineData(24.9f, 0.95f)]
    [InlineData(100f, 0.3f)]
    [InlineData(169f, 30f / 169f)]
    [InlineData(169.5f, 0f)]
    [InlineData(1000f, 0f)]
    public void The_wall_fades_with_squared_distance(float distanceSquared, float alpha) =>
        Assert.Equal(alpha, BanWall.Alpha(distanceSquared), 3);

    private static WallQuad OneSouthEdge(Vector2 agent)
    {
        // a single cell at (10, 20): its south edge runs from (40, 80) to (44, 80); flat ground at 10 m
        var seg = BanWall.SegmentsFromBitmap(Bitmap((10, 20)));
        var quads = BanWall.Quads(seg, Cells, agent, (_, _) => 10f, 50f);
        Assert.NotEmpty(quads);
        return quads.Single(q => q.Y1 == q.Y2 && MathF.Abs(q.Y1 - 80f) < 0.5f);
    }

    [Fact]
    public void A_piece_of_wall_stands_on_the_ground_and_reaches_its_height_above_the_higher_end()
    {
        var seg = BanWall.SegmentsFromBitmap(Bitmap((10, 20)));
        var quads = BanWall.Quads(seg, Cells, new Vector2(42f, 100f), (x, y) => x < 41f ? 10f : 12f, 50f);
        Assert.Equal(4, quads.Count);
        Assert.NotEmpty(quads);
        var south = quads.Single(q => q.Y1 == q.Y2 && q.Y1 < 81f);
        Assert.Equal(10f, south.Z1);
        Assert.Equal(12f, south.Z2);
        Assert.Equal(62f, south.Top);   // the higher end plus 50 m, at both ends
    }

    [Fact]
    public void A_piece_is_shifted_a_tenth_of_a_metre_against_depth_fighting()
    {
        var q = OneSouthEdge(new Vector2(42f, 100f));
        Assert.Equal(40.1f, q.X1, 3);
        Assert.Equal(44.1f, q.X2, 3);
        Assert.Equal(80.1f, q.Y1, 3);
    }

    [Fact]
    public void A_piece_turns_toward_the_side_the_agent_is_on()
    {
        Assert.Equal(WallFacing.North, OneSouthEdge(new Vector2(42f, 100f)).Facing);   // agent north of the south edge
        Assert.Equal(WallFacing.South, OneSouthEdge(new Vector2(42f, 60f)).Facing);    // agent south of it
    }

    [Fact]
    public void A_piece_is_opaque_when_the_agent_is_near_and_gone_when_far()
    {
        // the official viewer offsets the distance by 5 m across the wall: 5 m south of a south edge counts as touching it
        Assert.Equal(0.95f, OneSouthEdge(new Vector2(42f, 75f)).Alpha, 3);
        Assert.Equal(0f, OneSouthEdge(new Vector2(42f, 200f)).Alpha, 3);
        Assert.Equal(0.3f, OneSouthEdge(new Vector2(42f, 85f)).Alpha, 3);   // 10 m on the offset side: squared 100
    }

    [Fact]
    public void The_distance_along_the_wall_counts_from_the_nearer_end()
    {
        // agent 5 m south of the wall's line (offset 0) but 6 m past its east end: squared distance 36 -> 30/36
        Assert.Equal(30f / 36f, OneSouthEdge(new Vector2(50f, 75f)).Alpha, 3);
    }

    [Fact]
    public void Heights_are_read_a_tenth_of_a_metre_inside_the_regions_edge()
    {
        var seen = new List<(float, float)>();
        var seg = BanWall.SegmentsFromBitmap(Bitmap((63, 63)));
        BanWall.Quads(seg, Cells, new Vector2(250f, 250f), (x, y) => { seen.Add((x, y)); return 0f; }, 50f);
        Assert.NotEmpty(seen);
        Assert.All(seen, p => { Assert.True(p.Item1 <= 255.9f + 1e-3f); Assert.True(p.Item2 <= 255.9f + 1e-3f); });
    }

    // ---- when it is shown ----

    private static ParcelCollision Banned() => new(ParcelDenial.Banned, Bitmap((3, 5)));

    [Fact]
    public void On_collision_a_collision_message_alone_draws_nothing_and_a_denial_draws_for_ten_seconds()
    {
        var s = new BanLineState { Mode = BanLineMode.OnCollision };
        s.Collision(Banned(), 100);
        Assert.False(s.Drawing(100.5));
        Assert.Equal(ParcelDenial.Banned, s.Denial);
        Assert.NotNull(s.Segments);
        s.Denied(101);
        Assert.True(s.Drawing(101));
        Assert.True(s.Drawing(110.9));
        Assert.False(s.Drawing(111.1));
    }

    [Fact]
    public void On_proximity_every_collision_message_draws_for_one_second()
    {
        var s = new BanLineState { Mode = BanLineMode.OnProximity };
        s.Collision(Banned(), 100);
        Assert.True(s.Drawing(100.9));
        Assert.False(s.Drawing(101.1));
        s.Collision(Banned(), 101.5);
        Assert.True(s.Drawing(102.4));
        s.Denied(105);   // a denial notice starts nothing in this mode
        Assert.False(s.Drawing(105.5));
    }

    [Fact]
    public void Hidden_means_never_drawn()
    {
        var s = new BanLineState { Mode = BanLineMode.OnCollision };
        s.Collision(Banned(), 100);
        s.Denied(100);
        Assert.True(s.Drawing(100));   // on show ...
        s.Mode = BanLineMode.Hide;
        Assert.False(s.Drawing(100));  // ... until the setting is off
    }

    [Fact]
    public void Nothing_is_drawn_before_any_collision_message_has_given_a_wall()
    {
        var s = new BanLineState();
        s.Denied(100);
        Assert.False(s.Drawing(100.1));
    }

    [Fact]
    public void A_new_collision_message_replaces_the_wall_and_clear_removes_it()
    {
        var s = new BanLineState();
        s.Collision(new ParcelCollision(ParcelDenial.NotOnList, Bitmap((1, 1)), UsePassList: true), 100);
        Assert.True(s.UsePass);
        var first = s.Segments;
        s.Collision(new ParcelCollision(ParcelDenial.Banned, Bitmap((9, 9), (10, 9))), 101);
        Assert.False(s.UsePass);
        Assert.NotSame(first, s.Segments);
        s.Denied(101);
        s.Clear();
        Assert.False(s.Drawing(101.1));
        Assert.Null(s.Segments);
        Assert.Null(s.Denial);
    }

    [Fact]
    public void A_collision_message_without_a_bitmap_keeps_the_state_but_draws_nothing()
    {
        var s = new BanLineState { Mode = BanLineMode.OnProximity };
        s.Collision(new ParcelCollision(ParcelDenial.Banned), 100);
        Assert.Equal(ParcelDenial.Banned, s.Denial);
        Assert.False(s.Drawing(100.1));
    }

    // ---- the pattern ----

    private static int Pixel(byte[] tile, int x, int y, int channel) => tile[(y * BanWall.TileSize + x) * 4 + channel];

    [Fact]
    public void The_tile_is_a_band_of_stripes_across_the_middle_and_clear_elsewhere()
    {
        var t = BanWall.Tile(pass: false);
        Assert.Equal(BanWall.TileSize * BanWall.TileSize * 4, t.Length);
        for (int x = 0; x < BanWall.TileSize; x += 7)
        {
            Assert.Equal(0, Pixel(t, x, 4, 3));                       // near the top: clear
            Assert.Equal(0, Pixel(t, x, BanWall.TileSize - 4, 3));    // near the bottom: clear
        }
        int opaque = Enumerable.Range(0, BanWall.TileSize).Count(x => Pixel(t, x, BanWall.TileSize / 2, 3) == 255);
        Assert.Equal(BanWall.TileSize, opaque);   // the middle row is solid
    }

    [Fact]
    public void The_band_has_two_alternating_yellows()
    {
        var t = BanWall.Tile(pass: false);
        var colours = Enumerable.Range(0, BanWall.TileSize).Select(x => (Pixel(t, x, BanWall.TileSize / 2, 0), Pixel(t, x, BanWall.TileSize / 2, 1), Pixel(t, x, BanWall.TileSize / 2, 2))).Distinct().ToList();
        Assert.Equal(2, colours.Count);
        Assert.All(colours, c => { Assert.True(c.Item1 > 180 && c.Item2 > 180); Assert.True(c.Item3 < c.Item1); });   // yellowish: red and green high, blue lower
    }

    [Fact]
    public void The_stripes_run_diagonally()
    {
        var t = BanWall.Tile(pass: false);
        int y = BanWall.TileSize / 2;
        // the same stripe colour shifts sideways from one row of the band to the next
        int first(int row) => Enumerable.Range(0, BanWall.TileSize).Select(x => (int?)x).FirstOrDefault(x => Pixel(t, x!.Value, row, 0) != Pixel(t, 0, row, 0)) ?? -1;
        Assert.NotEqual(first(y), first(y + 6));
    }

    [Fact]
    public void The_pass_tile_is_green_where_the_plain_one_is_yellow()
    {
        var plain = BanWall.Tile(pass: false);
        var pass = BanWall.Tile(pass: true);
        int y = BanWall.TileSize / 2;
        Assert.True(Pixel(pass, 0, y, 1) > Pixel(pass, 0, y, 0) && Pixel(pass, 0, y, 1) > Pixel(pass, 0, y, 2));   // green
        Assert.False(Pixel(plain, 0, y, 1) > Pixel(plain, 0, y, 0) + 20);                                          // not green
        Assert.Equal(Pixel(plain, 5, 4, 3), Pixel(pass, 5, 4, 3));                                                   // the clear part is the same
    }
}

/// <summary>The ban lines as the notice model and the transport feed them. Independent members, so the class runs in parallel.</summary>
public class BanLinesWiringTests
{
    private static byte[] OneCell()
    {
        var b = new byte[BanWall.CellsPerEdge * BanWall.CellsPerEdge / 8];
        b[0] = 1;
        return b;
    }

    [Theory]
    [InlineData("/You are banned from parcel")]
    [InlineData("/You are banned from parcel, please leave by your own will")]
    [InlineData("/You do not have access to the parcel")]
    [InlineData("Cannot enter parcel: banned")]
    public void A_denial_notice_starts_the_wall_for_ten_seconds(string alert)
    {
        double now = 100;
        var n = new NoticeModel(() => now);
        n.Receive(new ParcelCollision(ParcelDenial.Banned, OneCell()));
        Assert.False(n.BanLines.Drawing(now));
        n.Receive(new ServerAlert(alert, "", false));
        Assert.True(n.BanLines.Drawing(now));
        now = 109;
        Assert.True(n.BanLines.Drawing(now));
        now = 111;
        Assert.False(n.BanLines.Drawing(now));
    }

    [Fact]
    public void An_alert_that_is_not_a_denial_starts_nothing()
    {
        double now = 100;
        var n = new NoticeModel(() => now);
        n.Receive(new ParcelCollision(ParcelDenial.Banned, OneCell()));
        n.Receive(new ServerAlert("Home position set.", "HomePositionSet", false));
        n.Receive(new ServerAlert("/Something else entirely", "", false));
        Assert.NotNull(n.BanLines.Segments);
        Assert.False(n.BanLines.Drawing(now));
    }

    [Fact]
    public void A_new_region_clears_the_wall()
    {
        double now = 100;
        var n = new NoticeModel(() => now);
        n.Receive(new ParcelCollision(ParcelDenial.Banned, OneCell()));
        n.Receive(new ServerAlert("/You are banned from parcel", "", false));
        Assert.True(n.BanLines.Drawing(now));
        n.Receive(new Disconnected("test"));
        Assert.False(n.BanLines.Drawing(now));
        Assert.Null(n.BanLines.Segments);
    }

    [Fact]
    public void The_transport_hands_on_the_region_a_collision_message_came_from()
    {
        var sim = new SimRef(256256UL << 32 | 256000UL, "North");
        var c = Viewer.Core.Protocol.LmvTransport.ToParcelCollision(-40000, OneCell(), false, sim);
        Assert.Equal(sim, c!.Sim);
        Assert.Null(Viewer.Core.Protocol.LmvTransport.ToParcelCollision(-40000, OneCell())!.Sim);
    }

    [Fact]
    public void The_wall_remembers_the_region_of_its_last_collision_and_forgets_it_when_cleared()
    {
        var north = new SimRef(256256UL << 32 | 256000UL, "North");
        var east = new SimRef(256000UL << 32 | 256256UL, "East");
        var s = new BanLineState();
        Assert.Null(s.Sim);
        s.Collision(new ParcelCollision(ParcelDenial.Banned, OneCell(), Sim: north), 100);
        Assert.Equal(north, s.Sim);
        s.Collision(new ParcelCollision(ParcelDenial.Banned, OneCell(), Sim: east), 101);
        Assert.Equal(east, s.Sim);
        s.Clear();
        Assert.Null(s.Sim);
    }

    [Fact]
    public void The_transport_hands_on_the_bitmap_and_the_pass_flag()
    {
        var bitmap = OneCell();
        var c = Viewer.Core.Protocol.LmvTransport.ToParcelCollision(-40000, bitmap, true);
        Assert.NotNull(c);
        Assert.Equal(ParcelDenial.NotOnList, c!.Kind);
        Assert.Same(bitmap, c.Bitmap);
        Assert.True(c.UsePassList);
        Assert.Null(Viewer.Core.Protocol.LmvTransport.ToParcelCollision(5, bitmap, true));
    }
}
