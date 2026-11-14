// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// An avatar bake (five components, as the server's are) decoded straight to the resolution level its size on screen asks for: the pixels are what the decoder
/// defines for that level (close to the full decode scaled down to it), and what the decode allocates falls with the level instead of being a full decode's.
/// Measured warm, on the calling thread.
/// </summary>
public class BakeDecodeLevelTests(ITestOutputHelper output)
{
    private static byte[] Bake(int size = 1024) => CrowdBakeImages.Make(8, "upper", size, 1, false);

    private static long Allocated(Action decode)
    {
        decode();   // warm: the decoder's tables and the pool
        long before = GC.GetAllocatedBytesForCurrentThread();
        decode();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_bake_decoded_at_a_reduced_level_matches_the_full_decode_scaled_down_to_that_level(int level)
    {
        var bytes = Bake(512);
        var full = LmvTexture.DecodeLevel(bytes, 0);
        var reduced = LmvTexture.DecodeLevel(bytes, level);
        Assert.True(full.Ok && reduced.Ok, full.Refusal ?? reduced.Refusal);
        var scaled = LevelDownscale.ToLevel(full.Texture!.Width, full.Texture.Height, full.Texture.Rgba, level, out int w, out int h);
        Assert.Equal((w, h), (reduced.Texture!.Width, reduced.Texture.Height));
        Assert.Equal(level, reduced.Texture.Level);
        // the reduced decode is the wavelet's low band, the scaled-down full decode a box average: the same picture to within a few levels of 255 on average
        long diff = 0;
        int worst = 0;
        for (int i = 0; i < w * h * 4; i++)
        {
            int d = Math.Abs(scaled[i] - reduced.Texture.Rgba[i]);
            diff += d;
            worst = Math.Max(worst, d);
        }
        double mean = (double)diff / (w * h * 4);
        output.WriteLine($"level {level}: {w}x{h}, mean difference {mean:F2}, worst {worst}");
        Assert.True(mean <= 4.0, $"mean difference {mean:F2} of 255 at level {level}");
    }

    [Theory]
    [InlineData(false, 1024)]
    [InlineData(true, 512)]
    public void A_bake_of_any_layer_count_takes_the_reduced_decode_at_every_coarse_level(bool thirtyLayers, int size)
    {
        // five layers, as the grid's textures have, and about thirty thin ones, the encoder's default: both decode at the level
        // asked for and stay close to the full decode scaled down (the layer count no longer sends a level through a full decode)
        var bytes = CrowdBakeImages.Make(8, "upper", size, 1, false, thirtyLayers: thirtyLayers);
        J2kGuard.Validate(bytes, null, out var header);
        Assert.Equal(thirtyLayers, header!.Layers > TextureByteRange.MaxLayers);
        var full = LmvTexture.DecodeLevel(bytes, 0).Texture!;
        for (int level = 1; level <= 3; level++)
        {
            var t = LmvTexture.DecodeLevel(bytes, level).Texture!;
            var scaled = LevelDownscale.ToLevel(full.Width, full.Height, full.Rgba, level, out int w, out int h);
            Assert.Equal((w, h, level), (t.Width, t.Height, t.Level));
            double mean = Enumerable.Range(0, w * h * 4).Average(i => (double)Math.Abs(scaled[i] - t.Rgba[i]));
            output.WriteLine($"{header.Layers} layers, level {level}: mean difference {mean:F2}");
            Assert.True(mean <= 4.0, $"{header.Layers} layers, level {level}: mean difference {mean:F2}");
        }
    }

    [Fact]
    public void A_bake_can_be_asked_for_with_a_chosen_number_of_quality_layers_as_the_grids_own_bakes_have()
    {
        // the bakes of a real capture declare 15 to 19 layers; the test bakes can carry any number, so a run can use what real bakes use
        foreach (int wanted in new[] { 5, 15, 19 })
        {
            var bytes = CrowdBakeImages.Make(8, "upper", 1024, 1, false, layers: wanted);
            J2kGuard.Validate(bytes, null, out var header);
            Assert.Equal(wanted, header!.Layers);
        }
        // the default stays the grid's five
        J2kGuard.Validate(CrowdBakeImages.Make(8, "upper", 1024, 1, false), null, out var dflt);
        Assert.Equal(5, dflt!.Layers);
    }

    [Fact]
    public void A_decode_at_a_coarse_level_allocates_no_more_than_a_full_decode_and_the_wrapper_adds_none_of_its_own_beyond_the_level()
    {
        // For a 1024 x 1024 bake of five components the decoder allocates for the picture it returns, so the allocation falls with the level
        // (with the decoder before its fix it was about 85 % of a full decode at level 4).
        var bytes = Bake(1024);
        J2kGuard.Validate(bytes, null, out var header);
        output.WriteLine($"header: {header!.Width}x{header.Height}, {header.Components} components, {header.Layers} layers, {header.DecompositionLevels} decomposition levels, {bytes.Length} bytes");
        long full = Allocated(() => LmvTexture.DecodeLevel(bytes, 0).Texture!.ReleasePixels());
        var perLevel = new long[5];
        for (int level = 1; level < 5; level++)
        {
            int l = level;
            perLevel[l] = Allocated(() => LmvTexture.DecodeLevel(bytes, l).Texture!.ReleasePixels());
        }
        output.WriteLine($"allocated, KiB: level 0 {full / 1024}; level 1 {perLevel[1] / 1024}; level 2 {perLevel[2] / 1024}; level 3 {perLevel[3] / 1024}; level 4 {perLevel[4] / 1024}");
        for (int level = 1; level < 5; level++)
            Assert.True(perLevel[level] <= full + 256 * 1024, $"level {level} allocated {perLevel[level] / 1024} KiB against {full / 1024} KiB for a full decode");
        for (int level = 2; level < 5; level++)
            Assert.True(perLevel[level] < full / 2, $"level {level} allocated {perLevel[level] / 1024} KiB, more than half of the {full / 1024} KiB of a full decode");
    }
}
