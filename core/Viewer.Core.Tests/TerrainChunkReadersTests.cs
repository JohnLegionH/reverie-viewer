// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A chunk's edge heights and normals read the patches one ring beyond its own 4 x 4, so a patch that arrives or changes after its neighbour chunk was built
/// changes that neighbour's edge too. Every chunk that reads a changed patch must be built again, or a step and a crack are left along the chunk's edge
/// (the official viewer rebuilds a patch's edge from its neighbours whenever either changes: llsurfacepatch.cpp, updateNorthEdge and updateEastEdge).
/// </summary>
public class TerrainChunkReadersTests
{
    private const int Size = 256, Patches = 16;

    private sealed class Ground
    {
        public readonly float[][] Heights = new float[Patches * Patches][];
        public Ground() { for (int i = 0; i < Heights.Length; i++) Heights[i] = Fill(i % Patches, i / Patches, 0f); }
        public static float[] Fill(int px, int py, float bump)
        {
            var h = new float[256];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    h[y * 16 + x] = 20f + 0.3f * (px * 16 + x) + 0.2f * (py * 16 + y) + bump * (x + y);
            return h;
        }
        public bool Get(int px, int py, out float[] h) { h = Heights[py * Patches + px]; return true; }
    }

    private static float[][] BuildAll(TerrainGrid g) =>
        Enumerable.Range(0, g.ChunksX * g.ChunksY).Select(i => g.Build(i % g.ChunksX, i / g.ChunksX) is { } c ? c.Positions.Concat(c.Normals).ToArray() : []).ToArray();

    [Theory]
    [InlineData(5, 5)]    // inside a chunk: only that chunk reads it
    [InlineData(4, 5)]    // first column of chunk 1: chunk 0 reads it too
    [InlineData(7, 5)]    // last column of chunk 1: chunk 2 reads it too
    [InlineData(4, 4)]    // a corner: four chunks
    [InlineData(0, 0)]    // the grid's corner
    [InlineData(15, 15)]
    [InlineData(8, 11)]
    public void Every_chunk_whose_build_changes_when_a_patch_changes_is_among_those_the_patch_reports(int px, int py)
    {
        var ground = new Ground();
        var grid = new TerrainGrid(Size, Size, 16, ground.Get);
        var before = BuildAll(grid);
        ground.Heights[py * Patches + px] = Ground.Fill(px, py, 0.5f);
        var after = BuildAll(grid);
        var readers = grid.ChunksReading(px, py).ToHashSet();
        for (int i = 0; i < before.Length; i++)
        {
            var key = (i % grid.ChunksX, i / grid.ChunksX);
            if (!before[i].SequenceEqual(after[i]))
                Assert.Contains(key, readers);
        }
        Assert.Contains(TerrainGrid.ChunkOf(px, py), readers);
    }

    [Fact]
    public void A_patch_on_a_chunk_edge_is_read_by_the_chunk_across_it()
    {
        var grid = new TerrainGrid(Size, Size, 16, new Ground().Get);
        Assert.Equal(new HashSet<(int, int)> { (0, 1), (1, 1) }, grid.ChunksReading(4, 5).ToHashSet());
        Assert.Equal(new HashSet<(int, int)> { (0, 0), (1, 0), (0, 1), (1, 1) }, grid.ChunksReading(4, 4).ToHashSet());
        Assert.Equal(new HashSet<(int, int)> { (0, 0) }, grid.ChunksReading(0, 0).ToHashSet());
    }
}
