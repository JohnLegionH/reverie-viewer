// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// Reduced-resolution decodes of a codestream that is cut into several tiles and carries many quality layers, which is
/// what the LibreMetaverse encoder writes for any picture larger than 512 x 512 (tiles of 512 x 512, 27 layers). The
/// pictures in fixtures/textures/tiled/ were made with that encoder (LmvTexture.Encode, withAlpha as the name says) from
/// the seeded content of <see cref="Content"/>, and are committed so that the tests do not depend on the encoder.
///
/// What the decoder must do at levels 1 to 3 (CoreJ2K with the wavelet stage answering for the level returned):
///   - give the picture, not an unrelated one: close to the full decode scaled down to the level;
///   - give it every time (before, every tile after the first fell outside the reduced output and the pixels there were
///     whatever the pooled buffer held);
///   - allocate for the picture returned, not for the whole picture.
/// </summary>
public class TiledReducedDecodeTests(ITestOutputHelper output)
{
    public static TheoryData<string, int> FilesAndLevels()
    {
        var d = new TheoryData<string, int>();
        foreach (var file in new[] { "tiled-1024x1024-rgb.j2c", "tiled-1024x512-rgba.j2c" })
            foreach (int level in new[] { 1, 2, 3 })
                d.Add(file, level);
        return d;
    }

    private static byte[] Load(string file) => File.ReadAllBytes(Path.Combine(TestPaths.RepoRoot, "fixtures", "textures", "tiled", file));

    /// <summary>The seeded content the fixtures were made from (a smooth colour pattern with noise of +-12 and a radial alpha).</summary>
    internal static byte[] Content(int w, int h, int noise = 12, int seed = 5)
    {
        var rng = new Random(seed);
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                double u = (double)x / w, v = (double)y / h;
                int n() => rng.Next(-noise, noise + 1);
                b[i] = (byte)Math.Clamp(127 + 100 * Math.Sin(6.28 * (u * 2 + seed * 0.1)) + n(), 0, 255);
                b[i + 1] = (byte)Math.Clamp(127 + 100 * Math.Cos(6.28 * (v * 3 + u)) + n(), 0, 255);
                b[i + 2] = (byte)Math.Clamp(40 + 190 * u * v + n(), 0, 255);
                b[i + 3] = (byte)(255 * Math.Clamp(1.2 - Math.Sqrt((u - .5) * (u - .5) + (v - .5) * (v - .5)) * 2, 0, 1));
            }
        return b;
    }

    /// <summary>Mean absolute difference per channel (0-255) of a reduced level from the full decode scaled down to it.
    /// 8 is the bound the viewer's other level tests use (TextureLevelDecodeTests.Tolerance); a decode that puts the wrong
    /// tile's pixels, or none, in the picture is far above it (60 to 92 before the decoder was fixed).</summary>
    private const double GreyLevelBound = 8;

    [Fact]
    public void The_fixtures_are_what_they_are_meant_to_be_several_tiles_and_many_layers()
    {
        foreach (var file in new[] { "tiled-1024x1024-rgb.j2c", "tiled-1024x512-rgba.j2c" })
        {
            Assert.Null(J2kGuard.Validate(Load(file), null, out var h));
            Assert.True(h!.TilesX * h.TilesY > 1, $"{file}: {h.TilesX} x {h.TilesY} tiles");
            Assert.True(h.Layers > TextureByteRange.MaxLayers, $"{file}: {h.Layers} layers");
        }
    }

    [Theory]
    [MemberData(nameof(FilesAndLevels))]
    public void A_reduced_level_is_close_to_the_full_decode_scaled_down(string file, int level)
    {
        var bytes = Load(file);
        var full = LmvTexture.DecodeLevel(bytes, 0);
        var reduced = LmvTexture.DecodeLevel(bytes, level);
        Assert.True(full.Ok && reduced.Ok, full.Refusal ?? reduced.Refusal);
        var l0 = full.Texture!;
        var t = reduced.Texture!;
        Assert.Equal((TextureLevels.Size(l0.Width, level), TextureLevels.Size(l0.Height, level), level), (t.Width, t.Height, t.Level));
        var scaled = LevelDownscale.ToLevel(l0.Width, l0.Height, l0.Rgba, level, out int w, out int h);
        Assert.Equal((w, h), (t.Width, t.Height));
        double sum = 0;
        for (int i = 0; i < w * h * 4; i++) sum += Math.Abs(scaled[i] - t.Rgba[i]);
        double mean = sum / (w * h * 4);
        output.WriteLine($"{file} level {level}: {w}x{h}, mean difference {mean:F2} from the full decode scaled down");
        Assert.True(mean <= GreyLevelBound, $"{file} level {level}: mean difference {mean:F2} grey levels (bound {GreyLevelBound})");
    }

    [Theory]
    [MemberData(nameof(FilesAndLevels))]
    public void A_reduced_level_gives_the_same_pixels_every_time_also_after_other_pictures_went_through_the_pool(string file, int level)
    {
        var bytes = Load(file);
        var pool = new PixelPool(PixelPool.DefaultCapBytes);
        var firstTexture = LmvTexture.DecodeLevel(bytes, level, pool: pool).Texture!;
        byte[] first = firstTexture.Rgba.AsSpan(0, firstTexture.Width * firstTexture.Height * 4).ToArray();   // a pooled buffer may be longer than the picture
        firstTexture.ReleasePixels();
        for (int round = 0; round < 3; round++)
        {
            // other pictures, other levels, through the same pool and the decoder's own buffers
            foreach (var other in new[] { "tiled-1024x1024-rgb.j2c", "tiled-1024x512-rgba.j2c" })
                for (int l = 1; l <= 3; l++)
                    LmvTexture.DecodeLevel(Load(other), l, pool: pool).Texture!.ReleasePixels();
            var again = LmvTexture.DecodeLevel(bytes, level, pool: pool).Texture!;
            Assert.True(first.AsSpan().SequenceEqual(again.Rgba.AsSpan(0, first.Length)), $"{file} level {level}: round {round} differs");
            again.ReleasePixels();
        }
    }

    [Theory]
    [MemberData(nameof(FilesAndLevels))]
    public void A_reduced_level_allocates_for_the_picture_it_returns_not_for_the_whole_picture(string file, int level)
    {
        var bytes = Load(file);
        Assert.Null(J2kGuard.Validate(bytes, null, out var hd));
        var pool = new PixelPool(PixelPool.DefaultCapBytes);
        long Allocated(Action decode)
        {
            decode();   // warm: the decoder's tables and the pool
            long before = GC.GetAllocatedBytesForCurrentThread();
            decode();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        long full = Allocated(() => LmvTexture.DecodeLevel(bytes, 0, pool: pool).Texture!.ReleasePixels());
        long got = Allocated(() => LmvTexture.DecodeLevel(bytes, level, pool: pool).Texture!.ReleasePixels());
        // The bound of the decoder's own tests: 1.5 x the picture returned (4-byte samples, every component) plus 2 MiB of
        // bookkeeping for the packets of the whole picture (valid up to 1024 x 1024). The viewer's copy of the planes into
        // RGBA adds one byte plane per component (at most four).
        int w = TextureLevels.Size(hd!.Width, level), h = TextureLevels.Size(hd.Height, level);
        long picture = (long)w * h * hd.Components * 4;
        long bound = picture * 3 / 2 + 2L * 1024 * 1024 + (long)w * h * Math.Min(hd.Components, 4);
        output.WriteLine($"{file} level {level}: {got / 1024} KiB allocated (bound {bound / 1024} KiB); level 0 {full / 1024} KiB; picture returned {picture / 1024} KiB");
        Assert.True(got <= bound, $"{file} level {level}: allocated {got / 1024} KiB, bound {bound / 1024} KiB");
    }

    [Theory]
    [InlineData("tiled-1024x1024-rgb.j2c")]
    [InlineData("tiled-1024x512-rgba.j2c")]
    public void Each_coarser_level_allocates_less_than_the_one_above(string file)
    {
        var bytes = Load(file);
        var pool = new PixelPool(PixelPool.DefaultCapBytes);
        var perLevel = new long[4];
        for (int level = 0; level <= 3; level++)
        {
            int l = level;
            LmvTexture.DecodeLevel(bytes, l, pool: pool).Texture!.ReleasePixels();   // warm
            long before = GC.GetAllocatedBytesForCurrentThread();
            LmvTexture.DecodeLevel(bytes, l, pool: pool).Texture!.ReleasePixels();
            perLevel[l] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        output.WriteLine($"{file}: allocated KiB by level: {string.Join(", ", perLevel.Select(x => x / 1024))}");
        for (int level = 1; level <= 3; level++)
            Assert.True(perLevel[level] < perLevel[level - 1], $"{file}: level {level} allocated {perLevel[level]} bytes, level {level - 1} {perLevel[level - 1]}");
    }
}
