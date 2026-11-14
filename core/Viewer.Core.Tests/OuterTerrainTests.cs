// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The ground outside a region, read from the region that lies there: the answer a region's terrain grid takes for its border.</summary>
public class OuterTerrainTests
{
    private static SimRef Cell(int x, int y, string name) => new((ulong)(x * 256L) << 32 | (ulong)(y * 256L), name);

    private static readonly SimRef Home = Cell(1000, 1000, "Home");
    private static readonly SimRef East = Cell(1001, 1000, "East");
    private static readonly SimRef North = Cell(1000, 1001, "North");
    private static readonly SimRef FarEast = Cell(1003, 1000, "FarEast");

    private static float[] Patch(float h) => Enumerable.Repeat(h, 16 * 16).ToArray();

    private static async Task<(ViewerSession S, FakeTransport T)> InWorld()
    {
        var t = new FakeTransport();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example:8002/", Neighbours = true }, t, new CoreLog(new ListSink(), "test"));
        await s.LoginAsync(new LoginCredentials("Test", "Resident", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None);
        t.DriveToInWorld(Home);
        foreach (var sim in new[] { East, North, FarEast })
        {
            t.ConnectedSimulatorCount++;
            t.Raise(new SimConnected(sim, 256, 256));
        }
        return (s, t);
    }

    private static RegionWorld Of(ViewerSession s, SimRef sim) => s.Worlds.Single(w => ReferenceEquals(w.Sim, sim));

    [Fact]
    public async Task A_point_east_of_the_region_is_the_east_neighbours_first_column()
    {
        var (s, t) = await InWorld();
        using var _ = s;
        t.Raise(new LandPatch(East, 0, 1, 16, Patch(33f)));   // East's patch (0, 1): x 0..15, y 16..31
        Assert.Equal(33f, OuterTerrain.HeightAt(s.Worlds, Of(s, Home), 256, 20));
        Assert.Equal(33f, OuterTerrain.HeightAt(s.Worlds, Of(s, Home), 257, 20));   // the second tap of the normal
    }

    [Fact]
    public async Task A_point_west_of_the_east_region_is_the_home_regions_last_column()
    {
        var (s, t) = await InWorld();
        using var _ = s;
        t.Raise(new LandPatch(Home, 15, 1, 16, Patch(44f)));   // Home's patch (15, 1): x 240..255
        Assert.Equal(44f, OuterTerrain.HeightAt(s.Worlds, Of(s, East), -1, 20));
    }

    [Fact]
    public async Task A_point_north_of_the_region_is_the_north_neighbours_first_row()
    {
        var (s, t) = await InWorld();
        using var _ = s;
        t.Raise(new LandPatch(North, 2, 0, 16, Patch(55f)));
        Assert.Equal(55f, OuterTerrain.HeightAt(s.Worlds, Of(s, Home), 40, 256));
    }

    [Fact]
    public async Task A_patch_that_has_not_arrived_or_no_region_there_gives_nothing()
    {
        var (s, t) = await InWorld();
        using var _ = s;
        Assert.Null(OuterTerrain.HeightAt(s.Worlds, Of(s, Home), 256, 20));   // East has sent no terrain yet
        Assert.Null(OuterTerrain.HeightAt(s.Worlds, Of(s, Home), -1, 20));     // nothing lies west of Home
        Assert.Null(OuterTerrain.HeightAt(s.Worlds, Of(s, Home), 100, 100));  // inside the region itself: not outside
    }

    [Fact]
    public async Task Regions_that_share_an_edge_or_a_corner_touch_and_regions_apart_do_not()
    {
        var (s, _) = await InWorld();
        using var _ = s;
        Assert.True(OuterTerrain.Touches(Of(s, Home), Of(s, East)));
        Assert.True(OuterTerrain.Touches(Of(s, East), Of(s, Home)));
        Assert.True(OuterTerrain.Touches(Of(s, Home), Of(s, North)));
        Assert.False(OuterTerrain.Touches(Of(s, Home), Of(s, FarEast)));
        Assert.False(OuterTerrain.Touches(Of(s, Home), Of(s, Home)));
    }

    [Fact]
    public void Two_worlds_with_a_continuous_ground_give_a_grid_whose_border_normal_equals_its_interior_one()
    {
        var log = new Viewer.Core.Diagnostics.CoreLog(new ListSink(), "test");
        var homeSim = Home; var eastSim = East;
        var homeInfo = new RegionInfo("Home", 1000, 1000, 256, 256, true);
        var eastInfo = new RegionInfo("East", 1001, 1000, 256, 256, true);
        var home = new Viewer.Core.World.WorldState(log);
        home.BeginRegion(homeSim, homeInfo);
        var east = new Viewer.Core.World.WorldState(log);
        east.SetOriginOffset(new System.Numerics.Vector3(256, 0, 0));
        east.BeginRegion(eastSim, eastInfo);
        for (int py = 0; py < 16; py++)
            for (int px = 0; px < 16; px++)
            {
                var a = new float[256]; var b = new float[256];
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        a[y * 16 + x] = 20f + 0.1f * (px * 16 + x);
                        b[y * 16 + x] = 20f + 0.1f * (256 + px * 16 + x);
                    }
                home.ApplyPatch(homeSim, px, py, 16, a);
                east.ApplyPatch(eastSim, px, py, 16, b);
            }
        var homeRw = new RegionWorld(homeSim, homeInfo, home, true, System.Numerics.Vector3.Zero, home.OriginOffset);
        var eastRw = new RegionWorld(eastSim, eastInfo, east, false, new System.Numerics.Vector3(256, 0, 0), east.OriginOffset);
        var both = new[] { homeRw, eastRw };
        Assert.Equal(20f + 0.1f * 256, OuterTerrain.HeightAt(both, homeRw, 256, 20)!.Value, 3);
        var grid = new Viewer.Shell.TerrainGrid(256, 256, 16, home.TryGetPatch) { OutsideHeight = (gx, gy) => OuterTerrain.HeightAt(both, homeRw, gx, gy) };
        var c = grid.Build(3, 1)!;
        int edge = (20 * 65 + 64) * 3, inner = (20 * 65 + 30) * 3;
        Assert.Equal(c.Normals[inner], c.Normals[edge], 4);
    }
}
