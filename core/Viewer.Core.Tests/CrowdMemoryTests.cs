// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Viewer.Core.Avatars;
using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Memory of a crowd: the viewer's runtime keeps its garbage collector from holding on to freed memory, the test crowd's bake pictures are made once
/// and read back, and a body bake's alpha decides the body's hiding from one fixed resolution, whatever level of the bake is on the GPU. Pure; parallel
/// (the cache tests each use a folder of their own).</summary>
public class CrowdMemoryTests
{
    private const int Seed = 8;

    // ---- the runtime's memory setting ----

    [Fact]
    public void The_viewer_host_sets_the_garbage_collectors_memory_conservation()
    {
        string csproj = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "Viewer.Godot.csproj"));
        var m = Regex.Match(csproj, "<RuntimeHostConfigurationOption\\s+Include=\"System\\.GC\\.ConserveMemory\"\\s+Value=\"(\\d+)\"");
        Assert.True(m.Success, "Viewer.Godot.csproj sets no System.GC.ConserveMemory runtime option");
        Assert.InRange(int.Parse(m.Groups[1].Value), 1, 9);
    }

    /// <summary>Above 3 the collector turns most of its collections into full ones when large decode buffers come and go (measured on a region visit: about 29
    /// full collections at the default setting, 40 at 3, 65 at 5 and 115 at 7), and a region's total collection pause grows with them (about +5 % at 3,
    /// +10 % at 4, +16 to +29 % at 5, +70 to +85 % at 7). A region without avatars must not pay that, so the setting stays at 3 or below.</summary>
    [Fact]
    public void The_garbage_collectors_memory_conservation_is_low_enough_to_leave_a_regions_collection_pause_alone()
    {
        string csproj = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "Viewer.Godot.csproj"));
        var m = Regex.Match(csproj, "<RuntimeHostConfigurationOption\\s+Include=\"System\\.GC\\.ConserveMemory\"\\s+Value=\"(\\d+)\"");
        Assert.True(m.Success, "Viewer.Godot.csproj sets no System.GC.ConserveMemory runtime option");
        Assert.InRange(int.Parse(m.Groups[1].Value), 1, 3);
    }

    // ---- the test crowd's bake pictures, made once ----

    private static string FreshCacheFolder() => TestTemp.NewFolderPath("crowd-bake-cache");

    [Fact]
    public void A_bake_pool_is_encoded_once_and_read_back_from_its_cache_folder()
    {
        string folder = FreshCacheFolder();
        var looks = CrowdLooks.MakeAll(Seed, 12);
        var first = CrowdBakeImages.MakePool(Seed, looks, folder);
        Assert.True(first.Pictures.Count > 4);
        Assert.Equal(first.Pictures.Count, first.Encoded);
        Assert.Equal(0, first.Loaded);
        Assert.Equal(first.Pictures.Count, Directory.GetFiles(folder).Length);
        var second = CrowdBakeImages.MakePool(Seed, looks, folder);
        Assert.Equal(0, second.Encoded);
        Assert.Equal(first.Pictures.Count, second.Loaded);
        Assert.Equal(first.Pictures.Keys.Order(), second.Pictures.Keys.Order());
        foreach (var (key, bytes) in first.Pictures) Assert.Equal(bytes, second.Pictures[key]);
    }

    [Fact]
    public void A_cached_pool_is_the_one_made_without_a_cache_and_another_seed_does_not_read_it()
    {
        string folder = FreshCacheFolder();
        var looks = CrowdLooks.MakeAll(Seed, 8);
        var cached = CrowdBakeImages.MakePool(Seed, looks, folder);
        var plain = CrowdBakeImages.MakePool(Seed, looks);
        foreach (var (key, bytes) in plain) Assert.Equal(bytes, cached.Pictures[key]);
        var otherLooks = CrowdLooks.MakeAll(Seed + 1, 8);
        var other = CrowdBakeImages.MakePool(Seed + 1, otherLooks, folder);
        Assert.Equal(0, other.Loaded);
        Assert.Equal(other.Pictures.Count, other.Encoded);
    }

    // ---- the body bake's alpha mask ----

    /// <summary>A 1024 square bake picture: clear everywhere but a drawn square of <paramref name="patch"/> pixels at (96, 96); all clear when <paramref name="patch"/> is 0.</summary>
    private static byte[] SparseAlphaPicture(int patch)
    {
        const int size = 1024;
        var planes = new byte[5][];
        for (int c = 0; c < 5; c++) planes[c] = new byte[size * size];
        for (int i = 0; i < size * size; i++) { planes[0][i] = 120; planes[1][i] = 90; planes[2][i] = 70; planes[4][i] = 128; }
        for (int y = 96; y < 96 + patch; y++)
            for (int x = 96; x < 96 + patch; x++) planes[3][y * size + x] = 255;
        return LmvTexture.EncodePlanes(size, size, planes);
    }

    private static DecodedTexture Level(byte[] codestream, int level)
    {
        var r = LmvTexture.DecodeLevel(codestream, level, mipmaps: false);
        Assert.NotNull(r.Texture);
        return r.Texture!;
    }

    /// <summary>What an avatar's presenter does for one bake slot: the level that arrives first is offered; if the mask asks for another level it is decoded and offered.</summary>
    private static BakeAlphaMask Decide(byte[] codestream, int firstLevel)
    {
        var mask = new BakeAlphaMask();
        var first = Level(codestream, firstLevel);
        mask.Offer(first.Width, first.Height, first.Alpha, BakeAlphaMask.HidesAt(first.Rgba, first.Width, first.Height, first.FullWidth, first.FullHeight));
        if (mask.LevelNeeded(first.FullWidth, first.FullHeight, first.LevelCount) is { } need)
        {
            var finer = Level(codestream, need);
            mask.Offer(finer.Width, finer.Height, finer.Alpha, BakeAlphaMask.HidesAt(finer.Rgba, finer.Width, finer.Height, finer.FullWidth, finer.FullHeight));
        }
        return mask;
    }

    [Fact]
    public void The_resident_level_alone_misjudges_a_sparse_alpha_bake()
    {
        // the reason for the mask: the same bake judged from two levels gives two answers
        var bytes = SparseAlphaPicture(6);
        Assert.False(BakeAlpha.HidesRegion(Level(bytes, 0).Rgba));
        Assert.True(BakeAlpha.HidesRegion(Level(bytes, 4).Rgba));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void A_sparse_alpha_bake_shows_its_region_whatever_level_arrives_first(int firstLevel)
    {
        var mask = Decide(SparseAlphaPicture(6), firstLevel);
        Assert.True(mask.Known);
        Assert.False(mask.Hides);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    public void A_wholly_clear_alpha_bake_hides_its_region_whatever_level_arrives_first(int firstLevel)
    {
        var mask = Decide(SparseAlphaPicture(0), firstLevel);
        Assert.True(mask.Known);
        Assert.True(mask.Hides);
    }

    [Fact]
    public void The_mask_is_taken_once_and_later_levels_do_not_change_it()
    {
        var bytes = SparseAlphaPicture(6);
        var mask = Decide(bytes, 2);
        Assert.False(mask.Hides);
        var coarse = Level(bytes, 4);
        mask.Offer(coarse.Width, coarse.Height, coarse.Alpha, true);   // what the old rule would say of the coarse level
        Assert.False(mask.Hides);
        Assert.Null(mask.LevelNeeded(1024, 1024, 6));
    }

    [Fact]
    public void An_opaque_bake_needs_no_mask_level()
    {
        var mask = new BakeAlphaMask();
        mask.Offer(64, 64, AlphaClass.Opaque, null);   // a coarse level, but with no clear pixel at all
        Assert.True(mask.Known);
        Assert.False(mask.Hides);
        Assert.Null(mask.LevelNeeded(1024, 1024, 6));
    }

    [Theory]
    [InlineData(1024, 6, 2)]
    [InlineData(512, 5, 1)]
    [InlineData(256, 4, 0)]
    [InlineData(128, 3, 0)]
    [InlineData(1024, 2, 1)]   // a codestream with fewer levels: the coarsest it has
    public void The_mask_level_is_the_coarsest_that_still_has_the_masks_resolution(int full, int levels, int expected)
    {
        Assert.Equal(expected, BakeAlphaMask.LevelFor(full, full, levels));
    }

    // ---- what a bake slot needs on the GPU ----

    [Fact]
    public void A_bake_slot_a_worn_mesh_hides_or_whose_alpha_hides_it_needs_no_texture()
    {
        var unknown = new BakeAlphaMask();
        var shows = new BakeAlphaMask();
        shows.Offer(256, 256, AlphaClass.Blended, false);
        var hides = new BakeAlphaMask();
        hides.Offer(256, 256, AlphaClass.Blended, true);
        Assert.True(BakeAlphaMask.NeedsGpuTexture(false, unknown));
        Assert.True(BakeAlphaMask.NeedsGpuTexture(false, shows));
        Assert.False(BakeAlphaMask.NeedsGpuTexture(false, hides));
        Assert.False(BakeAlphaMask.NeedsGpuTexture(true, unknown));
        Assert.False(BakeAlphaMask.NeedsGpuTexture(true, shows));
    }
}
