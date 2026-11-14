// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The mini-map model: the local area around the agent, other avatars as dots (friends told apart), north and the rotation with the
/// camera, zoom, and the official defaults. Pure arithmetic over synthetic coarse locations; no grid, no engine. Every member builds its
/// own model, so the class runs in parallel.
/// </summary>
public class MiniMapShellTests
{
    private const float Size = 200;                                        // a 200 x 200 window: the centre is (100, 100)
    private const float HalfPi = MathF.PI / 2;
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Friend = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Stranger = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abce");

    private static MiniMapInput Input(float heading, params CoarseAvatar[] avatars) =>
        new(Self, new Vector3(128, 128, 30), heading, 1.0f, 256, 256, avatars, id => id == Friend);

    private static MiniMapModel Model(bool rotate) => new() { Rotate = rotate };

    private static MiniMapDot One(MiniMapFrame f) => Assert.Single(f.Dots);

    // ---------------- defaults and zoom ----------------

    [Fact]
    public void The_defaults_are_the_official_ones_128_pixels_a_region_turning_with_the_camera()
    {
        var m = new MiniMapModel();
        Assert.Equal(128, m.PixelsPerRegion);
        Assert.True(m.Rotate);
        Assert.Equal(32, MiniMapModel.Far);
        Assert.Equal(128, MiniMapModel.DefaultScale);
        Assert.Equal(256, MiniMapModel.Close);
        Assert.Equal(1024, MiniMapModel.VeryClose);
    }

    [Fact]
    public void Zoom_scales_four_percent_a_click_and_stays_within_32_to_4096()
    {
        var m = new MiniMapModel();
        int changes = 0;
        m.Changed += () => changes++;
        m.ZoomClicks(1);
        Assert.Equal(128 * 1.04, m.PixelsPerRegion, 6);
        m.ZoomClicks(-1);
        Assert.Equal(128, m.PixelsPerRegion, 6);
        m.ZoomClicks(3);
        Assert.Equal(128 * Math.Pow(1.04, 3), m.PixelsPerRegion, 6);
        m.ZoomClicks(1000);
        Assert.Equal(MiniMapModel.MaxScale, m.PixelsPerRegion);
        m.ZoomClicks(-100000);
        Assert.Equal(MiniMapModel.MinScale, m.PixelsPerRegion);
        m.ZoomClicks(double.NaN);
        Assert.Equal(MiniMapModel.MinScale, m.PixelsPerRegion);
        Assert.True(changes >= 4);
        m.SetScale(MiniMapModel.Close);
        Assert.Equal(256, m.PixelsPerRegion);
        int before = changes;
        m.SetScale(256);
        Assert.Equal(before, changes);                                      // no change, no news
        m.SetScale(1);
        Assert.Equal(32, m.PixelsPerRegion);
        m.SetScale(1e9);
        Assert.Equal(4096, m.PixelsPerRegion);
    }

    [Fact]
    public void Turning_rotation_on_or_off_is_news_only_when_it_changes()
    {
        var m = new MiniMapModel();
        int changes = 0;
        m.Changed += () => changes++;
        m.Rotate = true;
        Assert.Equal(0, changes);
        m.Rotate = false;
        Assert.Equal(1, changes);
        Assert.False(m.Rotate);
    }

    // ---------------- the frame, north up ----------------

    [Fact]
    public void With_rotation_off_north_is_up_and_the_region_sits_around_the_agent_at_the_window_centre()
    {
        var m = Model(rotate: false);
        var f = m.Build(Input(0), Size, Size);
        Assert.False(f.Rotated);
        Assert.Equal(100, f.CentreX);
        Assert.Equal(100, f.CentreY);
        Assert.Equal(128, f.PixelsPerRegion);
        // the agent stands in the middle of the region: its corners are 64 pixels away (0.5 pixel a metre), north-east at the top right
        Assert.Equal(4, f.RegionCorners.Count);
        var xs = f.RegionCorners.Select(c => c.X).OrderBy(x => x).ToArray();
        var ys = f.RegionCorners.Select(c => c.Y).OrderBy(y => y).ToArray();
        Assert.Equal(36, xs[0], 3);
        Assert.Equal(164, xs[3], 3);
        Assert.Equal(36, ys[0], 3);
        Assert.Equal(164, ys[3], 3);
        Assert.Contains(f.RegionCorners, c => Math.Abs(c.X - 164) < 0.01 && Math.Abs(c.Y - 36) < 0.01);   // north-east: right, up
        // north is up
        Assert.Equal(HalfPi, f.NorthAngle, 4);
        Assert.Equal(100, f.NorthX, 2);
        Assert.True(f.NorthY < 100);
    }

    [Fact]
    public void A_dot_is_placed_by_its_metres_from_the_agent_east_to_the_right_and_north_up()
    {
        var m = Model(rotate: false);
        var f = m.Build(Input(0, new CoarseAvatar(Stranger, new Vector3(138, 148, 30))), Size, Size);   // 10 m east, 20 m north
        var dot = Assert.Single(f.Dots);
        Assert.Equal(Stranger, dot.Id);
        Assert.Equal(105, dot.X, 3);
        Assert.Equal(90, dot.Y, 3);
        Assert.False(dot.Friend);
        Assert.False(dot.UnknownHeight);
        Assert.Equal(0, dot.RelativeHeight, 3);
    }

    [Fact]
    public void Friends_are_told_apart_the_agent_is_never_a_dot_and_heights_are_relative_or_unknown()
    {
        var m = Model(rotate: false);
        var f = m.Build(Input(0,
            new CoarseAvatar(Self, new Vector3(128, 128, 30)),
            new CoarseAvatar(Friend, new Vector3(130, 128, 55)),
            new CoarseAvatar(Stranger, new Vector3(126, 128, 1020))), Size, Size);
        Assert.Equal(2, f.Dots.Count);
        Assert.DoesNotContain(f.Dots, d => d.Id == Self);
        var friend = f.Dots.Single(d => d.Id == Friend);
        Assert.True(friend.Friend);
        Assert.Equal(25, friend.RelativeHeight, 3);
        Assert.False(friend.UnknownHeight);
        var stranger = f.Dots.Single(d => d.Id == Stranger);
        Assert.False(stranger.Friend);
        Assert.True(stranger.UnknownHeight);                               // a reported height of 1,020 m or more says nothing
    }

    [Fact]
    public void An_avatar_outside_the_window_is_not_drawn_and_one_that_left_the_list_is_gone()
    {
        var m = Model(rotate: false);
        var far = new CoarseAvatar(Stranger, new Vector3(128 + 400, 128, 30));      // 400 m east = 200 px: outside a 200 px window
        var near = new CoarseAvatar(Friend, new Vector3(128 + 40, 128, 30));        // 20 px
        var f = m.Build(Input(0, far, near), Size, Size);
        Assert.Equal(Friend, Assert.Single(f.Dots).Id);
        Assert.Empty(m.Build(Input(0), Size, Size).Dots);                           // no longer in the list: no dot
    }

    [Fact]
    public void Zooming_in_moves_dots_apart_and_the_region_grows()
    {
        var m = Model(rotate: false);
        var a = new CoarseAvatar(Stranger, new Vector3(148, 128, 30));              // 20 m east
        var wide = m.Build(Input(0, a), Size, Size);
        m.SetScale(256);
        var close = m.Build(Input(0, a), Size, Size);
        Assert.Equal(110, One(wide).X, 3);                                       // 20 m * 0.5 px
        Assert.Equal(120, One(close).X, 3);                                      // 20 m * 1 px
        var wideWidth = wide.RegionCorners.Max(c => c.X) - wide.RegionCorners.Min(c => c.X);
        var closeWidth = close.RegionCorners.Max(c => c.X) - close.RegionCorners.Min(c => c.X);
        Assert.Equal(wideWidth * 2, closeWidth, 3);
    }

    // ---------------- rotation with the camera ----------------

    [Fact]
    public void Looking_north_the_rotated_map_is_the_unrotated_one()
    {
        var a = new CoarseAvatar(Stranger, new Vector3(138, 148, 30));
        var plain = Model(rotate: false).Build(Input(0, a), Size, Size);
        var turned = Model(rotate: true).Build(Input(HalfPi, a), Size, Size);       // the camera looks north (90 degrees from east)
        Assert.True(turned.Rotated);
        Assert.Equal(One(plain).X, One(turned).X, 3);
        Assert.Equal(One(plain).Y, One(turned).Y, 3);
        Assert.Equal(plain.NorthAngle, turned.NorthAngle, 4);
    }

    [Fact]
    public void Looking_east_what_is_ahead_is_up_and_north_is_to_the_left()
    {
        var m = Model(rotate: true);
        var ahead = m.Build(Input(0, new CoarseAvatar(Stranger, new Vector3(148, 128, 30))), Size, Size);   // 20 m east, camera looks east
        Assert.Equal(100, One(ahead).X, 3);
        Assert.Equal(90, One(ahead).Y, 3);                                                                // 10 px up
        var north = m.Build(Input(0, new CoarseAvatar(Stranger, new Vector3(128, 148, 30))), Size, Size);   // 20 m north
        Assert.Equal(90, One(north).X, 3);                                                                // 10 px to the left
        Assert.Equal(100, One(north).Y, 3);
        // a dot north-east: ahead 10 m and left 20 m
        var both = m.Build(Input(0, new CoarseAvatar(Stranger, new Vector3(138, 148, 30))), Size, Size);
        Assert.Equal(90, One(both).X, 3);
        Assert.Equal(95, One(both).Y, 3);
        // the north marker follows true north: to the left of the window
        Assert.Equal(MathF.PI, both.NorthAngle, 4);
        Assert.True(both.NorthX < 100);
        Assert.Equal(100, both.NorthY, 2);
    }

    [Fact]
    public void Looking_south_the_map_is_upside_down_and_the_north_marker_is_at_the_bottom()
    {
        var m = Model(rotate: true);
        var f = m.Build(Input(-HalfPi, new CoarseAvatar(Stranger, new Vector3(128, 148, 30))), Size, Size);   // 20 m north, camera looks south
        Assert.Equal(100, One(f).X, 3);
        Assert.Equal(110, One(f).Y, 3);                                                                    // behind: down
        Assert.True(f.NorthY > 100);
    }

    [Fact]
    public void The_north_marker_stays_inside_the_window_whatever_the_heading()
    {
        var m = Model(rotate: true);
        for (int deg = -180; deg <= 180; deg += 15)
        {
            var f = m.Build(Input(deg * MathF.PI / 180), 300, 200);
            Assert.InRange(f.NorthX, 0, 300);
            Assert.InRange(f.NorthY, 0, 200);
            double d = Math.Sqrt((f.NorthX - 150) * (f.NorthX - 150) + (f.NorthY - 100) * (f.NorthY - 100));
            Assert.Equal(92, d, 1);                                         // 8 pixels inside the edge of the nearer side
        }
    }

    [Fact]
    public void The_view_wedge_points_up_when_the_map_turns_and_along_the_heading_when_it_does_not()
    {
        var turned = Model(rotate: true).Build(Input(0.7f), Size, Size);
        Assert.Equal(HalfPi, turned.FrustumCentre, 4);
        Assert.Equal(0.5f, turned.FrustumHalf, 4);                          // half the field of view
        var fixedNorth = Model(rotate: false).Build(Input(0.7f), Size, Size);
        Assert.Equal(0.7f, fixedNorth.FrustumCentre, 4);
    }

    [Fact]
    public void A_bad_window_or_position_gives_an_empty_frame_and_never_throws()
    {
        var m = new MiniMapModel();
        Assert.Empty(m.Build(Input(0, new CoarseAvatar(Stranger, new Vector3(float.NaN, 1, 1))), Size, Size).Dots);
        var f = m.Build(Input(float.NaN), 0, 0);
        Assert.Empty(f.Dots);
        Assert.Empty(m.Build(Input(0), -5, 100).Dots);
    }

    [Fact]
    public void Every_text_the_mini_map_shows_exists_in_the_string_table()
    {
        var en = StringTable.English();
        foreach (var key in new[] { "minimap.title", "minimap.view", "minimap.zoom_in", "minimap.zoom_out", "minimap.rotate", "minimap.north", "menu.world", "menu.world.minimap" })
            Assert.True(en.Has(key), key);
    }
}
