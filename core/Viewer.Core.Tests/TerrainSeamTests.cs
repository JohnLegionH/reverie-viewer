// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Two regions side by side meet in one surface. Each region's terrain grid ends in an extra column (and row) of vertices and takes
/// its normals from heights one step either side; without the neighbour's heights those clamp to the region's own last cell, so the
/// slope across the border is flat on one side and one-sided on the other: a faint seam of shading. With the neighbour's heights the
/// border vertex is the neighbour's first one and the normal is the interior's.
/// </summary>
public class TerrainSeamTests
{
    private const int Size = 256;

    /// <summary>A plane rising <paramref name="slopeX"/> m per metre east and <paramref name="slopeY"/> m per metre north, as patches.</summary>
    private static PatchSource Plane(float slopeX, float slopeY, int originX, int originY) =>
        (int px, int py, out float[] h) =>
        {
            h = null!;
            if (px < 0 || py < 0 || px >= Size / 16 || py >= Size / 16) return false;
            h = new float[16 * 16];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    h[y * 16 + x] = 20f + slopeX * (originX + px * 16 + x) + slopeY * (originY + py * 16 + y);
            return true;
        };

    private static (float X, float Y, float Z) NormalAt(TerrainChunk c, int i, int j, int w = 65)
    {
        int v = (j * w + i) * 3;
        return (c.Normals[v], c.Normals[v + 1], c.Normals[v + 2]);
    }

    private static float HeightAt(TerrainChunk c, int i, int j, int w = 65) => c.Positions[(j * w + i) * 3 + 1];

    [Fact]
    public void Without_a_neighbour_the_border_normal_is_flat_where_the_slope_is_not()
    {
        var west = new TerrainGrid(Size, Size, 16, Plane(0.1f, 0, 0, 0));
        var c = west.Build(3, 1)!;
        var edge = NormalAt(c, 64, 20);
        var inner = NormalAt(c, 30, 20);
        Assert.NotEqual(inner.X, edge.X, 3);   // the baseline the seam comes from
    }

    [Fact]
    public void The_east_border_normal_equals_the_interior_one_when_the_east_neighbour_is_known()
    {
        var east = Plane(0.1f, 0, Size, 0);
        var west = new TerrainGrid(Size, Size, 16, Plane(0.1f, 0, 0, 0))
        {
            OutsideHeight = (gx, gy) => gx >= Size && gy >= 0 && gy < Size ? 20f + 0.1f * gx : null,
        };
        var c = west.Build(3, 1)!;
        var edge = NormalAt(c, 64, 20);
        var inner = NormalAt(c, 30, 20);
        Assert.Equal(inner.X, edge.X, 4);
        Assert.Equal(inner.Y, edge.Y, 4);
        Assert.Equal(inner.Z, edge.Z, 4);
        _ = east;
    }

    [Fact]
    public void The_border_vertex_has_the_east_neighbours_first_column_height()
    {
        var west = new TerrainGrid(Size, Size, 16, Plane(0.1f, 0, 0, 0))
        {
            OutsideHeight = (gx, gy) => gx >= Size && gy >= 0 && gy < Size ? 20f + 0.1f * gx : null,
        };
        var c = west.Build(3, 1)!;
        Assert.Equal(20f + 0.1f * Size, HeightAt(c, 64, 20), 4);
    }

    [Fact]
    public void The_west_border_normal_equals_the_interior_one_when_the_west_neighbour_is_known()
    {
        var east = new TerrainGrid(Size, Size, 16, Plane(0.1f, 0, 0, 0))
        {
            OutsideHeight = (gx, gy) => gx < 0 && gy >= 0 && gy < Size ? 20f + 0.1f * gx : null,
        };
        var c = east.Build(0, 1)!;
        var edge = NormalAt(c, 0, 20);
        var inner = NormalAt(c, 30, 20);
        Assert.Equal(inner.X, edge.X, 4);
        Assert.Equal(inner.Z, edge.Z, 4);
    }

    [Fact]
    public void The_north_border_normal_equals_the_interior_one_when_the_north_neighbour_is_known()
    {
        var south = new TerrainGrid(Size, Size, 16, Plane(0, 0.1f, 0, 0))
        {
            OutsideHeight = (gx, gy) => gy >= Size && gx >= 0 && gx < Size ? 20f + 0.1f * gy : null,
        };
        var c = south.Build(1, 3)!;
        var edge = NormalAt(c, 20, 64);
        var inner = NormalAt(c, 20, 30);
        Assert.Equal(inner.Z, edge.Z, 4);
        Assert.Equal(inner.Y, edge.Y, 4);
    }

    [Fact]
    public void A_region_with_no_neighbour_builds_the_same_chunk_with_or_without_an_empty_source()
    {
        var plain = new TerrainGrid(Size, Size, 16, Plane(0.1f, 0.05f, 0, 0)).Build(3, 3)!;
        var empty = new TerrainGrid(Size, Size, 16, Plane(0.1f, 0.05f, 0, 0)) { OutsideHeight = (_, _) => null }.Build(3, 3)!;
        Assert.Equal(plain.Positions, empty.Positions);
        Assert.Equal(plain.Normals, empty.Normals);
    }

    [Fact]
    public void A_snapshot_of_an_edge_chunk_builds_what_the_live_grid_builds_with_the_neighbours_heights()
    {
        Func<int, int, float?> outside = (gx, gy) => gx >= Size && gy >= 0 && gy < Size ? 20f + 0.1f * gx : null;
        var grid = new TerrainGrid(Size, Size, 16, Plane(0.1f, 0, 0, 0)) { OutsideHeight = outside };
        var live = grid.Build(3, 1)!;
        var prepared = grid.Snapshot(3, 1).Prepare();
        Assert.NotNull(prepared.Surface);
        // the snapshot's chunk carries the same border normal: encode the live chunk and compare the engine data
        var expected = TerrainSurface.Prepare(live);
        Assert.Equal(expected.Aabb, prepared.Surface!.Aabb);
        Assert.True(expected.VertexData.AsSpan().SequenceEqual(prepared.Surface.VertexData), "the snapshot's border vertices equal the live grid's");
        Assert.True(expected.AttributeData.AsSpan().SequenceEqual(prepared.Surface.AttributeData), "the snapshot's border normals equal the live grid's");
    }

    [Fact]
    public void A_snapshot_reads_the_outside_only_for_chunks_on_the_border()
    {
        int calls = 0;
        var grid = new TerrainGrid(Size, Size, 16, Plane(0.1f, 0, 0, 0)) { OutsideHeight = (gx, gy) => { calls++; return null; } };
        grid.Snapshot(1, 1);   // an inner chunk: nothing outside is read at all
        Assert.Equal(0, calls);
        grid.Snapshot(3, 1);   // an east-border chunk: the outside cells its vertices and normals read are copied
        Assert.True(calls > 0, "an edge chunk's snapshot asks for the cells outside the region");
        Assert.True(calls < 4 * 70, "and only the few it reads");
    }
}
