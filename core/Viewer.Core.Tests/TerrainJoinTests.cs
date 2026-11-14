// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Regions of different sizes meet in one surface: a 256 m region beside a 512 m one (and a corner neighbour) share their edge heights, so
/// there is no step, no hole, and the border normals are those of the ground running through. The official viewer joins neighbouring
/// surfaces by copying edge heights (llsurface.cpp connectNeighbor, llsurfacepatch.cpp updateEastEdge and updateNorthEdge); Firestorm's
/// Aurora Sim changes walk each edge in 256 m steps so regions of other sizes connect (llworld.cpp addRegion).
/// </summary>
public class TerrainJoinTests
{
    private static float Ground(double x, double y) => (float)(20 + 0.1 * x + 0.05 * y);   // a plane through every region, in the home region's frame

    private sealed class Rig
    {
        public RegionWorld[] All = null!;
        public RegionWorld Home = null!, Big = null!, Diagonal = null!, North = null!;
        public TerrainGrid HomeGrid = null!, BigGrid = null!, DiagGrid = null!;
        public int HomeSize = 256, BigSize = 512;
    }

    private static RegionWorld Make(string name, int gx, int gy, int size, Vector3 offset, bool anchor, bool plane = true)
    {
        var log = new CoreLog(new ListSink(), "t");
        var sim = new SimRef((ulong)(gx * 256L) << 32 | (ulong)(gy * 256L), name);
        var info = new RegionInfo(name, gx, gy, size, size, true);
        var w = new WorldState(log);
        w.SetOriginOffset(offset);
        w.BeginRegion(sim, info);
        if (plane)
            for (int py = 0; py < size / 16; py++)
                for (int px = 0; px < size / 16; px++)
                {
                    var h = new float[256];
                    for (int y = 0; y < 16; y++)
                        for (int x = 0; x < 16; x++)
                            h[y * 16 + x] = Ground(offset.X + px * 16 + x, offset.Y + py * 16 + y);
                    w.ApplyPatch(sim, px, py, 16, h);
                }
        return new RegionWorld(sim, info, w, anchor, offset, w.OriginOffset);
    }

    private static Rig Build(bool diagonal = true)
    {
        var r = new Rig();
        r.Home = Make("Home", 1000, 1000, 256, Vector3.Zero, true);
        r.Big = Make("Big", 1001, 999, 512, new Vector3(256, -256, 0), false);   // east of Home, reaching 256 m further south
        r.Diagonal = Make("Diag", 1001, 1001, 256, new Vector3(256, 256, 0), false);   // beyond Home's north-east corner
        r.North = Make("North", 1000, 1001, 256, new Vector3(0, 256, 0), false);
        r.All = diagonal ? [r.Home, r.Big, r.Diagonal, r.North] : [r.Home, r.Big];
        TerrainGrid G(RegionWorld rw) => new(rw.Region.SizeX, rw.Region.SizeY, 16, rw.World.TryGetPatch)
        {
            OutsideHeight = (gx, gy) => OuterTerrain.HeightAt(r.All, rw, gx, gy),
            OriginX = rw.OriginOffset.X, OriginY = rw.OriginOffset.Y,
        };
        r.HomeGrid = G(r.Home); r.BigGrid = G(r.Big); r.DiagGrid = G(r.Diagonal);
        return r;
    }

    private static float Z(TerrainChunk c, int i, int j, int w) => c.Positions[(j * w + i) * 3 + 1];
    private static (float X, float Y, float Z) N(TerrainChunk c, int i, int j, int w) { int v = (j * w + i) * 3; return (c.Normals[v], c.Normals[v + 1], c.Normals[v + 2]); }

    [Fact]
    public void Along_a_shared_edge_between_a_256_and_a_512_region_there_is_no_step()
    {
        var r = Build();
        var home = r.HomeGrid.Build(3, 1)!;   // the east column of chunks: x 192..256, y 64..128, 65 x 65 vertices
        var big = r.BigGrid.Build(0, 5)!;     // the west column of chunks: x 0..64, y 320..384 in Big's frame, the same ground
        for (int j = 0; j <= 64; j++)
        {
            float homeEdge = Z(home, 64, j, 65);
            float bigEdge = Z(big, 0, j, 65);   // Big's local y = Home's y + 256, so chunk row 5 of Big is Home's chunk row 1
            Assert.Equal(bigEdge, homeEdge, 3);
            Assert.Equal(Ground(256, 64 + j), homeEdge, 2);
        }
    }

    [Fact]
    public void Border_normals_on_both_sides_are_the_ground_running_through_for_regions_of_different_sizes()
    {
        var r = Build();
        var home = r.HomeGrid.Build(3, 1)!;
        var big = r.BigGrid.Build(0, 5)!;
        var inner = N(home, 30, 20, 65);
        for (int j = 2; j < 63; j++)
        {
            foreach (var n in new[] { N(home, 64, j, 65), N(big, 0, j, 65) })
            {
                Assert.Equal(inner.X, n.X, 4);
                Assert.Equal(inner.Y, n.Y, 4);
                Assert.Equal(inner.Z, n.Z, 4);
            }
        }
    }

    [Fact]
    public void The_corner_vertex_reads_the_diagonal_neighbour_and_its_normal_is_the_ground_running_through()
    {
        var r = Build();
        var home = r.HomeGrid.Build(3, 3)!;   // the north-east chunk: x 192..256, y 192..256
        Assert.Equal(Ground(256, 256), Z(home, 64, 64, 65), 3);
        var inner = N(home, 30, 30, 65);
        var corner = N(home, 64, 64, 65);
        Assert.Equal(inner.X, corner.X, 4);
        Assert.Equal(inner.Z, corner.Z, 4);
    }

    [Fact]
    public void Without_the_diagonal_neighbour_the_corner_is_flat_and_does_not_throw()
    {
        var r = Build(diagonal: false);
        var home = r.HomeGrid.Build(3, 3)!;
        Assert.Equal(Ground(255, 255), Z(home, 64, 64, 65), 3);   // the region's own last cell, as the official viewer ends a surface
    }

    [Fact]
    public void Where_the_larger_region_has_no_neighbour_its_edge_ends_on_its_own_last_row_and_column()
    {
        var r = Build();
        var big = r.BigGrid.Build(0, 0)!;   // Big's south-west chunk: nothing lies south or west of it (Home covers only Big's northern half)
        Assert.Equal(Ground(256, -256), Z(big, 0, 0, 65), 3);
        Assert.Equal(Ground(256, -256 + 64), Z(big, 0, 64, 65), 3);
    }

    [Fact]
    public void Every_quad_along_the_border_is_drawn_so_there_is_no_hole()
    {
        var r = Build();
        var home = r.HomeGrid.Build(3, 1)!;
        var big = r.BigGrid.Build(0, 5)!;
        Assert.Equal(64 * 64 * 2, home.TriangleCount);
        Assert.Equal(64 * 64 * 2, big.TriangleCount);
    }

    [Fact]
    public void A_neighbour_whose_patches_arrive_later_changes_the_border_vertices_once_they_are_known()
    {
        var r = Build();
        var late = Make("Late", 1001, 1000, 256, new Vector3(256, 0, 0), false, plane: false);
        var all = new[] { r.Home, late };
        var grid = new TerrainGrid(256, 256, 16, r.Home.World.TryGetPatch) { OutsideHeight = (gx, gy) => OuterTerrain.HeightAt(all, r.Home, gx, gy) };
        Assert.Equal(Ground(255, 80), Z(grid.Build(3, 1)!, 64, 16, 65), 3);   // nothing known: its own last column
        late.World.ApplyPatch(late.Sim, 0, 5, 16, Enumerable.Repeat(77f, 256).ToArray());   // Late's patch (0, 5): x 0..15, y 80..95
        Assert.Equal(77f, Z(grid.Build(3, 1)!, 64, 16, 65), 3);
    }
}
