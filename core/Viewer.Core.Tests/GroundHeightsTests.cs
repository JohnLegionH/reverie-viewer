// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The ground height Set Home uses comes from the land patches the viewer itself received, not from the protocol library's own
/// terrain array (which this viewer switches off, so it answers nothing). Patches are 16 m squares, row-major (y * size + x), in any
/// region size. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class GroundHeightsTests
{
    private const int Size = 16;

    /// <summary>A patch whose every metre has its own height, so a wrong patch or a wrong index inside it shows.</summary>
    private static float[] Patch(int px, int py)
    {
        var h = new float[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                h[y * Size + x] = 20f + px * 0.5f + py * 0.25f + y * 0.01f + x * 0.001f;
        return h;
    }

    private static float Expect(int x, int y) => 20f + (x / Size) * 0.5f + (y / Size) * 0.25f + (y % Size) * 0.01f + (x % Size) * 0.001f;

    private static GroundHeights Filled(object region, int metres)
    {
        var g = new GroundHeights();
        for (int py = 0; py < metres / Size; py++)
            for (int px = 0; px < metres / Size; px++)
                g.Put(region, px, py, Size, Patch(px, py));
        return g;
    }

    [Fact]
    public void A_metre_inside_a_patch_reads_that_patchs_own_height()
    {
        var region = new object();
        var g = Filled(region, 256);
        Assert.Equal(Expect(37, 5), g.HeightAt(region, 37, 5));
        Assert.Equal(Expect(0, 0), g.HeightAt(region, 0, 0));
        Assert.Equal(Expect(255, 255), g.HeightAt(region, 255, 255));
    }

    [Fact]
    public void A_point_beyond_256_metres_in_a_1024_metre_region_reads_its_patch()
    {
        var region = new object();
        var g = Filled(region, 1024);
        Assert.Equal(Expect(700, 300), g.HeightAt(region, 700, 300));
        Assert.Equal(Expect(1023, 1023), g.HeightAt(region, 1023, 1023));
        Assert.Equal(Expect(256, 0), g.HeightAt(region, 256, 0));
    }

    [Fact]
    public void A_metre_whose_patch_has_not_arrived_has_no_height_and_so_does_a_point_outside_any_patch()
    {
        var region = new object();
        var g = new GroundHeights();
        g.Put(region, 0, 0, Size, Patch(0, 0));
        Assert.NotNull(g.HeightAt(region, 3, 3));
        Assert.Null(g.HeightAt(region, 20, 3));
        Assert.Null(g.HeightAt(region, -1, 3));
        Assert.Null(g.HeightAt(new object(), 3, 3));   // another region's patches are not this region's
    }

    [Fact]
    public void A_later_patch_for_the_same_cell_replaces_the_earlier_and_a_bad_patch_is_ignored()
    {
        var region = new object();
        var g = new GroundHeights();
        g.Put(region, 1, 1, Size, Patch(1, 1));
        var edited = Patch(1, 1);
        edited[0] = 99f;
        g.Put(region, 1, 1, Size, edited);
        Assert.Equal(99f, g.HeightAt(region, 16, 16));
        g.Put(region, 2, 2, Size, new float[3]);     // too short for a 16 x 16 patch
        Assert.Null(g.HeightAt(region, 32, 32));
    }

    [Fact]
    public void Set_Home_in_a_big_region_is_lifted_to_the_real_ground_where_a_zero_fallback_would_leave_it_below()
    {
        var region = new object();
        var g = Filled(region, 1024);
        var here = new Vector3(700.4f, 300.7f, 10f);   // standing below the ground height the patch says (a tall hill)
        var viaPatches = HomePosition.For(here, 1024f, (x, y) => g.HeightAt(region, x, y) ?? 0f, 4096f);
        Assert.Equal(Expect(700, 300), viaPatches.Z);
        var viaFallback = HomePosition.For(here, 1024f, (_, _) => 0f, 4096f);
        Assert.Equal(10f, viaFallback.Z);
        Assert.True(viaPatches.Z > viaFallback.Z);
    }

    [Fact]
    public void The_transport_reads_the_ground_from_the_viewers_own_patches_not_the_libraries_array()
    {
        // The library's Simulator cannot be built here, so the call site is checked in the source: the library lookup is gone and the
        // land patch handler feeds the viewer's own store.
        string text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "core", "Viewer.Core", "Protocol", "LmvTransport.cs"));
        Assert.DoesNotContain("TerrainHeightAtPoint", text);
        Assert.Contains("_ground.HeightAt(", text);
        Assert.Contains("_ground.Put(", text);
    }
}
