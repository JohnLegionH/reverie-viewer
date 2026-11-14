// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// No cracks inside a region: the official viewer draws patches at different detail levels and stitches the shared edge so the finer patch follows the
/// coarser one (llsurfacepatch.cpp, getGeometry's edge strides). Our ground draws every patch at the full 1 m grid, so the only edges that can crack
/// are the ones between chunks; there both chunks must hold the same vertex positions and the same normals along the shared line, on rough ground.
/// </summary>
public class TerrainChunkEdgeTests
{
    private const int Size = 256, Width = 65;

    private static float Rough(int x, int y) => 20f + 4f * MathF.Sin(x * 0.37f) + 3f * MathF.Cos(y * 0.51f) + 0.02f * x * y % 7f;

    private static PatchSource Source(Func<int, int, bool>? present = null) => (int px, int py, out float[] h) =>
    {
        h = new float[16 * 16];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                h[y * 16 + x] = Rough(px * 16 + x, py * 16 + y);
        return present?.Invoke(px, py) ?? true;
    };

    private static (float X, float Y, float Z) Pos(TerrainChunk c, int i, int j) => (c.Positions[(j * Width + i) * 3], c.Positions[(j * Width + i) * 3 + 1], c.Positions[(j * Width + i) * 3 + 2]);
    private static (float X, float Y, float Z) Nrm(TerrainChunk c, int i, int j) => (c.Normals[(j * Width + i) * 3], c.Normals[(j * Width + i) * 3 + 1], c.Normals[(j * Width + i) * 3 + 2]);

    [Fact]
    public void Two_chunks_side_by_side_hold_the_same_positions_and_normals_along_their_shared_column()
    {
        var grid = new TerrainGrid(Size, Size, 16, Source());
        var west = grid.Build(1, 2)!;
        var east = grid.Build(2, 2)!;
        for (int j = 0; j < Width; j++)
        {
            var a = Pos(west, Width - 1, j);
            var b = Pos(east, 0, j);
            Assert.Equal(a.X, b.X, 4);
            Assert.Equal(a.Y, b.Y, 4);
            Assert.Equal(a.Z, b.Z, 4);
            var na = Nrm(west, Width - 1, j);
            var nb = Nrm(east, 0, j);
            Assert.Equal(na.X, nb.X, 4);
            Assert.Equal(na.Y, nb.Y, 4);
            Assert.Equal(na.Z, nb.Z, 4);
        }
    }

    [Fact]
    public void Two_chunks_one_above_the_other_hold_the_same_positions_and_normals_along_their_shared_row()
    {
        var grid = new TerrainGrid(Size, Size, 16, Source());
        var south = grid.Build(2, 1)!;
        var north = grid.Build(2, 2)!;
        for (int i = 0; i < Width; i++)
        {
            var a = Pos(south, i, Width - 1);
            var b = Pos(north, i, 0);
            Assert.Equal(a.Y, b.Y, 4);
            Assert.Equal(a.X, b.X, 4);
            Assert.Equal(a.Z, b.Z, 4);
            var na = Nrm(south, i, Width - 1);
            var nb = Nrm(north, i, 0);
            Assert.Equal(na.X, nb.X, 4);
            Assert.Equal(na.Y, nb.Y, 4);
            Assert.Equal(na.Z, nb.Z, 4);
        }
    }

    [Fact]
    public void A_chunk_beside_a_patch_that_has_not_arrived_still_ends_on_the_ground_it_has_and_the_missing_patch_leaves_no_triangle()
    {
        // the patch east of chunk (1,2)'s last column is missing: the chunk keeps its quads and takes edge heights from its own patches
        var grid = new TerrainGrid(Size, Size, 16, Source((px, py) => !(px == 8 && py >= 8 && py < 12)));
        var west = grid.Build(1, 2)!;
        Assert.Equal(64 * 64 * 2, west.TriangleCount);
        Assert.True(grid.Build(2, 2)!.TriangleCount < 64 * 64 * 2);   // the chunk that holds the missing patches has holes, not spikes
        for (int j = 0; j < Width; j++)
            Assert.True(float.IsFinite(Pos(west, Width - 1, j).Y));
    }
}
