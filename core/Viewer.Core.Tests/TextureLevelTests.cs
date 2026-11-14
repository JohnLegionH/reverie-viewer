// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Discard levels decoded from whole codestreams.</summary>
public class TextureLevelDecodeTests
{
    /// <summary>A smooth, deterministic test image (our own content), so a downscale is a fair reference.</summary>
    internal static byte[] Smooth(int w, int h, bool alpha, int seed = 1)
    {
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                double u = (double)x / w, v = (double)y / h;
                b[i] = (byte)(127 + 120 * Math.Sin(6.28 * (u * 2 + seed * 0.1)));
                b[i + 1] = (byte)(127 + 120 * Math.Cos(6.28 * (v * 3 + u)));
                b[i + 2] = (byte)(40 + 200 * u * v);
                b[i + 3] = alpha ? (byte)(255 * Math.Clamp(1.2 - Math.Sqrt((u - .5) * (u - .5) + (v - .5) * (v - .5)) * 2, 0, 1)) : (byte)255;
            }
        return b;
    }

    /// <summary>A box downscale of <paramref name="rgba"/> by 2^level (the reference a reduced level should stay close to).
    /// Measured: the reduced levels carry no bias against it (signed mean within 0.2); the mean absolute difference, 7-10
    /// at the coarsest levels, is the wavelet low-pass filter and lossy coding, not a box filter.</summary>
    private static byte[] Box(byte[] rgba, int w, int h, int level)
    {
        int f = 1 << level, dw = TextureLevels.Size(w, level), dh = TextureLevels.Size(h, level);
        var o = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0, n = 0;
                    for (int yy = y * f; yy < Math.Min(h, y * f + f); yy++)
                        for (int xx = x * f; xx < Math.Min(w, x * f + f); xx++) { sum += rgba[(yy * w + xx) * 4 + c]; n++; }
                    o[(y * dw + x) * 4 + c] = (byte)(sum / n);
                }
        return o;
    }

    private static double MeanAbs(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average();

    /// <summary>The declared closeness tolerance: mean absolute difference per channel (0-255).</summary>
    internal const double Tolerance = 8;

    /// <summary>How far a reduced level is from level 0 downscaled to its size. JPEG 2000's low-pass sample x at level n
    /// sits at source position 2^n * x (measured: an ordinary box over [2^n x, 2^n (x+1)) is shifted by half a block and
    /// misses by 10-19 on smooth images, with no bias), so the reference is a box of 2^n + 1 texels centred there. A different
    /// image scores far above the tolerance (negative control).</summary>
    private static double Close(DecodedTexture level, DecodedTexture l0, int n)
    {
        int f = 1 << n, half = f / 2;
        double sum = 0;
        for (int y = 0; y < level.Height; y++)
            for (int x = 0; x < level.Width; x++)
                for (int c = 0; c < 4; c++)
                {
                    int acc = 0, k = 0;
                    for (int yy = Math.Max(0, y * f - half); yy <= Math.Min(l0.Height - 1, y * f + half); yy++)
                        for (int xx = Math.Max(0, x * f - half); xx <= Math.Min(l0.Width - 1, x * f + half); xx++) { acc += l0.Rgba[(yy * l0.Width + xx) * 4 + c]; k++; }
                    sum += Math.Abs(level.Rgba[(y * level.Width + x) * 4 + c] - (double)acc / k);
                }
        return sum / (level.Width * level.Height * 4);
    }

    [Theory]
    [InlineData(64, 64, 2)]
    [InlineData(512, 512, 5)]
    [InlineData(1024, 1024, 6)]
    [InlineData(1024, 256, 6)]
    [InlineData(40, 40, 1)]
    [InlineData(2048, 2048, 7)]
    public void Level_count_goes_down_to_about_32_px_on_the_longest_side(int w, int h, int expected)
    {
        Assert.Equal(expected, TextureLevels.Count(w, h, 32));
        Assert.True(Math.Max(TextureLevels.Size(w, expected - 1), TextureLevels.Size(h, expected - 1)) >= TextureLevels.CoarsestSide);
        Assert.Equal(Math.Min(expected, 3), TextureLevels.Count(w, h, 2));   // fewer resolution levels in the codestream: fewer levels
    }

    [Theory]
    [InlineData(256, 256, false)]
    [InlineData(256, 128, true)]
    [InlineData(200, 136, false)]   // not a power of two: ceil(size / 2^level)
    public void Each_level_decodes_to_its_size_and_stays_close_to_a_downscale_of_level_0(int w, int h, bool alpha)
    {
        var src = Smooth(w, h, alpha);
        var cs = LmvTexture.Encode(w, h, src, alpha);
        var full = LmvTexture.DecodeLevel(cs, 0);
        Assert.True(full.Ok, full.Refusal);
        var l0 = full.Texture!;
        Assert.Equal(LmvTexture.Decode(cs).Texture!.Rgba, l0.Rgba);   // level 0 is exactly the decode (goldens unchanged)
        int count = l0.LevelCount;
        Assert.Equal(TextureLevels.Count(w, h, J2kGuardHeader(cs).DecompositionLevels), count);
        Assert.True(count >= 3);
        for (int level = 1; level < count; level++)
        {
            var d = LmvTexture.DecodeLevel(cs, level);
            Assert.True(d.Ok, d.Refusal);
            var t = d.Texture!;
            Assert.Equal((TextureLevels.Size(w, level), TextureLevels.Size(h, level)), (t.Width, t.Height));
            Assert.Equal((level, count, w, h), (t.Level, t.LevelCount, t.FullWidth, t.FullHeight));
            Assert.Equal(l0.SourceComponents, t.SourceComponents);
            double diff = Close(t, l0, level);
            Assert.True(diff < Tolerance, $"level {level}: mean difference {diff:F2} from a downscale of level 0");
            Assert.Equal(alpha ? AlphaClass.Blended : AlphaClass.Opaque, t.Alpha);
        }
        // asking beyond the coarsest gives the coarsest
        Assert.Equal(count - 1, LmvTexture.DecodeLevel(cs, int.MaxValue).Texture!.Level);
    }

    private static J2kHeader J2kGuardHeader(byte[] cs)
    {
        Assert.Null(J2kGuard.Validate(cs, null, out var h));
        return h!;
    }

    [Fact]
    public void A_negative_control_a_different_image_is_not_close()
    {
        var cs = LmvTexture.Encode(128, 128, Smooth(128, 128, false, seed: 1), false);
        var inverted = Smooth(128, 128, false).Select((v, i) => i % 4 == 3 ? v : (byte)(255 - v)).ToArray();
        var other = LmvTexture.DecodeLevel(LmvTexture.Encode(128, 128, inverted, false), 0).Texture!;
        var l1 = LmvTexture.DecodeLevel(cs, 1).Texture!;
        Assert.True(Close(l1, other, 1) > 4 * Tolerance);   // the tolerance sees a wrong image
    }

    [Fact]
    public void The_guard_still_refuses_every_hostile_file_at_every_level()
    {
        foreach (var (name, bytes) in TextureFuzzTests.Corpus())
            foreach (int level in new[] { 0, 1, 3, int.MaxValue })
                Assert.False(LmvTexture.DecodeLevel(bytes, level).Ok, $"{name} at level {level} decoded");
    }

    [Fact]
    public void Mipmaps_are_built_with_the_decode_when_asked()
    {
        var cs = LmvTexture.Encode(96, 64, Smooth(96, 64, false), false);
        var d = LmvTexture.DecodeLevel(cs, 1, mipmaps: true).Texture!;
        Assert.NotNull(d.Mips);
        Assert.Equal(MipChain.Bytes(48, 32), d.Mips!.Length);
        Assert.Equal(d.Rgba, d.Mips[..d.Rgba.Length]);
        Assert.Null(LmvTexture.DecodeLevel(cs, 1).Texture!.Mips);
    }

    [LocalFixturesFact("assets")]
    public void The_cached_real_textures_decode_at_every_level()
    {
        var files = Directory.GetFiles(LiveCaptures.PathOf("assets"), "*.j2k").OrderBy(f => f).ToList();
        Assert.NotEmpty(files);
        int checkedLevels = 0;
        foreach (var f in files.Where(f => new FileInfo(f).Length < 400_000).Take(12))
        {
            var cs = File.ReadAllBytes(f);
            var full = LmvTexture.DecodeLevel(cs, 0);
            if (!full.Ok) continue;
            var l0 = full.Texture!;
            for (int level = 1; level < l0.LevelCount; level++)
            {
                var t = LmvTexture.DecodeLevel(cs, level).Texture!;
                Assert.Equal((TextureLevels.Size(l0.Width, level), TextureLevels.Size(l0.Height, level)), (t.Width, t.Height));
                double diff = Close(t, l0, level);
                Assert.True(diff < Tolerance, $"{Path.GetFileName(f)} level {level}: {diff:F2}");
                checkedLevels++;
            }
        }
        Assert.True(checkedLevels > 0);
    }
}

/// <summary>The mip chain built off the main thread (the engine's RGBA8 box filter, checked on the GPU path by --tr-mipcheck).</summary>
/// <summary>The number of quality layers does not decide how a level is decoded, and the downscale itself (the reference
/// the tests compare a reduced decode with).</summary>
public class ManyLayerLevelPathTests
{
    private static byte[] Img(int w, int h, Func<int, int, byte> v)
    {
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++) b[(y * w + x) * 4 + c] = c == 3 ? (byte)255 : v(x, y);
        return b;
    }

    /// <summary>A ramp with seeded noise: LibreMetaverse's encoder writes its many layers only for detailed content (a
    /// plain ramp gets 3).</summary>
    private static byte[] Detailed(int size)
    {
        var rng = new Random(3);
        return Img(size, size, (x, y) => (byte)Math.Clamp((x + y) * 255 / (2 * size) + rng.Next(-40, 41), 0, 255));
    }

    [Fact]
    public void Eight_layers_or_fewer_and_more_both_take_the_reduced_decode_and_stay_close_to_level_0_scaled_down()
    {
        var src = Detailed(256);
        var few = LmvTexture.EncodeLayered(256, 256, src, "0.0625 0.25 0.5 1.0 2.0", 2.0);
        var many = LmvTexture.Encode(256, 256, src, withAlpha: false);
        Assert.Null(J2kGuard.Validate(few, null, out var hf));
        Assert.Null(J2kGuard.Validate(many, null, out var hm));
        Assert.True(hf!.Layers <= TextureByteRange.MaxLayers, $"{hf.Layers}");
        Assert.True(hm!.Layers > TextureByteRange.MaxLayers, $"{hm.Layers}");
        foreach (var (name, cs) in new[] { ("few", few), ("many", many) })
        {
            var l0 = LmvTexture.DecodeLevel(cs, 0).Texture!;
            for (int level = 1; level < 3; level++)
            {
                var t = LmvTexture.DecodeLevel(cs, level).Texture!;
                Assert.Equal((TextureLevels.Size(256, level), level, 256), (t.Width, t.Level, t.FullWidth));
                var scaled = LevelDownscale.ToLevel(256, 256, l0.Rgba, level, out _, out _);
                double mean = Enumerable.Range(0, t.Width * t.Height * 4).Average(i => (double)Math.Abs(scaled[i] - t.Rgba[i]));
                Assert.True(mean < TextureLevelDecodeTests.Tolerance, $"{name} at level {level}: {mean:F2} from level 0 scaled down");
            }
        }
    }

    [Fact]
    public void A_partial_many_layer_codestream_decodes_at_a_reduced_level_like_any_other_prefix()
    {
        var many = LmvTexture.Encode(256, 256, Detailed(256), withAlpha: false);
        Assert.Null(J2kGuard.Validate(many, null, out var h));
        Assert.True(h!.Layers > TextureByteRange.MaxLayers, $"{h.Layers}");
        var prefix = many[..(many.Length / 2)];
        var r = LmvTexture.DecodeLevel(prefix, 2, prefix: true);
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal((TextureLevels.Size(256, 2), 2), (r.Texture!.Width, r.Texture.Level));
        // the same pixels again, and a prefix is never full resolution
        Assert.Equal(r.Texture.Rgba, LmvTexture.DecodeLevel(prefix, 2, prefix: true).Texture!.Rgba);
        Assert.False(LmvTexture.DecodeLevel(prefix, 0, prefix: true).Ok);
    }

    [Theory]
    [InlineData(200, 136, 3)]
    [InlineData(5, 3, 2)]
    [InlineData(1, 4, 2)]
    public void The_downscale_gives_the_jpeg_2000_level_sizes_and_keeps_a_flat_image_flat(int w, int h, int level)
    {
        var d = LevelDownscale.ToLevel(w, h, Img(w, h, (_, _) => 77), level, out int dw, out int dh);
        Assert.Equal((TextureLevels.Size(w, level), TextureLevels.Size(h, level)), (dw, dh));
        Assert.Equal(dw * dh * 4, d.Length);
        Assert.All(Enumerable.Range(0, d.Length), i => Assert.Equal(i % 4 == 3 ? 255 : 77, d[i]));
    }

    [Fact]
    public void The_downscale_is_centred_on_the_even_texel()
    {
        // a single bright column at x = 4: level 1 texel 2 (source 4) gets half of it, texels 1 and 3 nothing
        var d = LevelDownscale.ToLevel(8, 2, Img(8, 2, (x, _) => x == 4 ? (byte)200 : (byte)0), 1, out int w, out _);
        Assert.Equal(4, w);
        Assert.Equal(new byte[] { 0, 0, 100, 0 }, Enumerable.Range(0, 4).Select(x => d[x * 4]).ToArray());
    }
}

public class MipChainTests
{
    [Fact]
    public void Levels_halve_to_one_by_one_and_each_texel_is_the_rounded_mean_of_its_block()
    {
        // 3x2: level 1 is 1x1 from the top-left 2x2 block (odd columns: the last column is not read)
        byte[] img =
        [
            10, 0, 0, 255,  20, 0, 0, 255,  200, 0, 0, 255,
            30, 0, 0, 255,  41, 0, 0, 255,  200, 0, 0, 255,
        ];
        var chain = MipChain.Build(3, 2, img);
        Assert.Equal(2, MipChain.Levels(3, 2));
        Assert.Equal(3 * 2 * 4 + 4, chain.Length);
        Assert.Equal((byte)((10 + 20 + 30 + 41 + 2) >> 2), chain[24]);
        Assert.Equal(255, chain[27]);
        // a side of length 1 repeats its texel: 1x4 -> 1x2 -> 1x1
        byte[] col = [0, 0, 0, 0, 100, 0, 0, 0, 7, 0, 0, 0, 9, 0, 0, 0];
        var c = MipChain.Build(1, 4, col);
        Assert.Equal(3, MipChain.Levels(1, 4));
        Assert.Equal(new byte[] { 50, 8, 29 }, new[] { c[16], c[20], c[24] });   // (0+0+100+100+2)>>2, (7+7+9+9+2)>>2, (50+50+8+8+2)>>2
    }

    [Fact]
    public void Sizes_match_the_footprint_the_residency_manager_counts()
    {
        foreach (var (w, h) in new[] { (1024, 1024), (512, 512), (64, 32) })
            Assert.InRange(MipChain.Bytes(w, h), TextureFootprint.Bytes(w, h, false), TextureFootprint.Bytes(w, h, true) + 16);
    }
}

/// <summary>Residency in levels. Deterministic: the host is simulated.</summary>
public class LevelResidencyTests
{
    private const long KiB = 1024;
    private static readonly uint Avg = PlaceholderColour.Of(90, 100, 110, 255);

    private static ResidencyCamera Cam(Vector3? at = null, Vector3? forward = null) =>
        new(at ?? Vector3.Zero, Vector3.Normalize(forward ?? Vector3.UnitX), Vector3.UnitZ, MathF.PI / 3, 16f / 9, 0.1f, 512f, 360);
    private static TextureUser Face(uint id, int face = 0) => new(TextureUserKind.Face, id, face);
    private static Guid G(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
    private static long At(int size, int level) => TextureFootprint.Bytes(TextureLevels.Size(size, level), TextureLevels.Size(size, level), true);

    /// <summary>A host that uploads every load and level change at once (square textures of the given sizes).</summary>
    private sealed class Host(TextureResidency r, Func<Guid, int> size)
    {
        public readonly List<ResidencyAction> All = new();
        public List<ResidencyAction> Frame(ResidencyCamera cam, long frame)
        {
            var acts = r.Update(cam, TestClock.Frames(frame)).ToList();
            All.AddRange(acts);
            foreach (var a in acts.Where(a => a.Kind != ResidencyActionKind.Evict))
            {
                int s = size(a.Texture), count = TextureLevels.Count(s, s, 5), level = Math.Min(a.Level, count - 1);
                if (a.Kind == ResidencyActionKind.Level && r.PendingLevelOf(a.Texture) != a.Level) continue;   // superseded in the same frame
                r.Uploaded(a.Texture, level, count, s, s, At(s, level), Avg, TestClock.Frames(frame));
            }
            return acts;
        }
    }

    [Fact]
    public void Desired_level_comes_from_the_size_on_screen_and_the_repeats()
    {
        var r = new TextureResidency(new() { BudgetBytes = 1L << 30 });
        var host = new Host(r, _ => 1024);
        r.SetUser(Face(1), G(1), new Bounds(new(3, 0, 0), 6f));      // near and large: over 512 px across
        r.SetUser(Face(2), G(2), new Bounds(new(200, 0, 0), 1.5f));    // far: about 8 px across
        r.SetUser(Face(3), G(3), new Bounds(new(-20, 0, 0), 3f));      // behind the camera
        r.SetUser(Face(4), G(4), new Bounds(new(8, 0, 0), 1.5f), repeats: 16);   // nearer, but its face repeats 16 times
        for (int f = 0; f < 4; f++) host.Frame(Cam(), f);
        double near = Cam().ScreenDiameter(new Bounds(new(3, 0, 0), 6f));
        Assert.InRange(near, 512, 1024 * 4);
        Assert.Equal(0, r.DesiredLevelOf(G(1)));   // 1024 px has enough, 512 does not
        Assert.Equal(5, r.DesiredLevelOf(G(2)));   // the coarsest (32 px) is enough
        Assert.Equal(5, r.DesiredLevelOf(G(3)));   // out of view: the coarsest
        Assert.Equal(3, r.DesiredLevelOf(G(4)));   // repeated: 128 texels are enough (unrepeated at 8 m it would want level 1)
        foreach (int i in new[] { 1, 2, 3, 4 }) Assert.True(r.AtDesired(G(i)), $"texture {i} at {r.LevelOf(G(i))}, desired {r.DesiredLevelOf(G(i))}");
    }

    [Fact]
    public void A_new_texture_uploads_its_coarsest_level_first_then_refines_in_a_later_frame()
    {
        var r = new TextureResidency(new() { BudgetBytes = 1L << 30 });
        var host = new Host(r, _ => 1024);
        r.SetUser(Face(1), G(1), new Bounds(new(3, 0, 0), 6f));
        var first = host.Frame(Cam(), 0);
        Assert.Equal(new ResidencyAction(ResidencyActionKind.Load, G(1), TextureResidency.Coarsest), Assert.Single(first));
        Assert.Equal(5, r.LevelOf(G(1)));
        Assert.Equal(At(1024, 5), r.ResidentBytes);   // the footprint is the current level's
        var second = host.Frame(Cam(), 1);
        Assert.Equal(new ResidencyAction(ResidencyActionKind.Level, G(1), 0), Assert.Single(second));   // straight to the desired level
        Assert.Equal(0, r.LevelOf(G(1)));
        Assert.Equal(At(1024, 0), r.ResidentBytes);
        Assert.Empty(host.Frame(Cam(), 2));
        // negative control: an-style host (no levels) never sees a level change
        var old = new TextureResidency(new() { BudgetBytes = 1L << 30 });
        old.SetUser(Face(1), G(1), new Bounds(new(3, 0, 0), 6f));
        foreach (var a in old.Update(Cam(), TestClock.Frames(0))) old.Uploaded(a.Texture, At(1024, 0), Avg, TestClock.Frames(0));
        Assert.Empty(old.Update(Cam(), TestClock.Frames(1)));
    }

    /// <summary>Six near textures, all wanting full resolution (1024 px), under a budget that holds only part of that.</summary>
    private static (TextureResidency R, Host H) Crowd(long budget, int minResidence = 0)
    {
        var r = new TextureResidency(new() { BudgetBytes = budget, MinResidence = TestClock.Frames(minResidence), LowWater = 0.9, MaxLoadsPerFrame = 64 });
        var host = new Host(r, _ => 1024);
        for (int i = 1; i <= 6; i++) r.SetUser(Face((uint)i), G(i), new Bounds(new(3 + i * 0.5f, (i - 3.5f) * 0.5f, 0), 6f));
        return (r, host);
    }

    [Fact]
    public void Under_pressure_textures_step_down_before_any_goes_to_its_placeholder()
    {
        var (r, host) = Crowd(budget: 12 * 1024 * KiB);   // six at full resolution would be 32 MiB
        for (int f = 0; f < 30; f++) host.Frame(Cam(), f);
        Assert.True(r.ResidentBytes <= r.BudgetBytes);
        Assert.Equal(0, r.OnPlaceholders);
        Assert.Equal(0, r.Evictions);
        Assert.True(r.StepsDown + r.All.Count(id => r.LevelOf(id) > 0) > 0);
        Assert.Contains(r.All, id => r.LevelOf(id) > 0);                       // some textures below their desired level...
        Assert.All(r.All, id => Assert.True(r.LevelOf(id) >= 0));              // ... and every one on the GPU at some level
        // the highest priority ones got the finer levels
        var byPriority = r.All.OrderByDescending(r.PriorityOf).Select(r.LevelOf).ToList();
        Assert.True(byPriority.First() <= byPriority.Last());
    }

    [Fact]
    public void A_texture_goes_to_its_placeholder_only_when_even_its_smallest_level_does_not_fit()
    {
        long coarsest = At(1024, 5);
        var (r, host) = Crowd(budget: 5 * coarsest + coarsest / 2);   // room for five coarsest levels, not six
        for (int f = 0; f < 30; f++) host.Frame(Cam(), f);
        Assert.Equal(1, r.All.Count(id => r.StateOf(id) != ResidencyState.Resident));
        Assert.All(r.All.Where(id => r.StateOf(id) == ResidencyState.Resident), id => Assert.Equal(5, r.LevelOf(id)));
        var lowest = r.All.OrderBy(r.PriorityOf).First();
        Assert.NotEqual(ResidencyState.Resident, r.StateOf(lowest));           // the lowest priority is the one left out
    }

    [Fact]
    public void Under_pressure_textures_finer_than_they_need_step_down_first()
    {
        var r = new TextureResidency(new() { BudgetBytes = 1L << 30, MinResidence = TestClock.Frames(0), MaxLoadsPerFrame = 64 });
        var host = new Host(r, _ => 1024);
        var near = new Bounds(new(3, 0, 0), 6f);
        r.SetUser(Face(1), G(1), near);                                  // high priority, needs full resolution
        r.SetUser(Face(2), G(2), new Bounds(new(4, 1, 0), 6f));        // high priority, full resolution
        for (int f = 0; f < 3; f++) host.Frame(Cam(), f);
        Assert.Equal((0, 0), (r.LevelOf(G(1)), r.LevelOf(G(2))));
        // G(2)'s user walks far away: it no longer needs full resolution but still has it
        r.SetUser(Face(2), G(2), new Bounds(new(150, 0, 0), 1.5f));
        host.Frame(Cam(), 3);
        Assert.True(r.DesiredLevelOf(G(2)) > 0);
        Assert.Equal(0, r.LevelOf(G(2)));                                // no pressure: nothing is dropped for being finer than needed
        r.SetBudget(At(1024, 0) + At(1024, 1));                          // pressure: something must give 3/4 of a full level
        host.Frame(Cam(), 4);
        Assert.Equal(0, r.LevelOf(G(1)));                                // the one that needs it keeps it
        Assert.True(r.LevelOf(G(2)) > 0);
        Assert.True(r.LevelOf(G(2)) <= r.DesiredLevelOf(G(2)));          // stepped only as far as it had to
    }

    [Fact]
    public void When_the_budget_frees_up_the_highest_priority_textures_below_their_desired_level_step_up()
    {
        var (r, host) = Crowd(budget: 8 * 1024 * KiB);
        for (int f = 0; f < 20; f++) host.Frame(Cam(), f);
        var below = r.All.Where(id => r.LevelOf(id) > r.DesiredLevelOf(id)).ToList();
        Assert.NotEmpty(below);
        long upsBefore = r.StepsUp;
        r.SetBudget(8 * 1024 * KiB + (At(1024, 0) - At(1024, 1)));      // room for exactly one more step to full resolution
        var acts = host.Frame(Cam(), 20);
        Assert.True(r.StepsUp > upsBefore);
        var up = acts.Where(a => a.Kind == ResidencyActionKind.Level).Select(a => a.Texture).ToList();
        Assert.NotEmpty(up);
        double best = below.Max(r.PriorityOf);
        Assert.Contains(up, id => r.PriorityOf(id) == best);             // the highest priority one first
        Assert.True(r.ResidentBytes <= r.BudgetBytes);
    }

    [Fact]
    public void The_minimum_residence_applies_to_level_changes()
    {
        var (r, host) = Crowd(budget: 1L << 30, minResidence: 60);
        for (int f = 0; f < 5; f++) host.Frame(Cam(), f);
        Assert.All(r.All, id => Assert.Equal(0, r.LevelOf(id)));
        // pressure only just over the budget, 10 frames after the level changes: nothing young steps down
        r.SetBudget(r.ResidentBytes - 1);
        r.Update(Cam(), TestClock.Frames(10));   // the budget is over; the textures are young, but the budget itself wins
        Assert.True(r.ResidentBytes <= r.BudgetBytes);
        long downs = r.StepsDown;
        Assert.True(downs <= 1, $"{downs} steps for 1 byte over");       // no more than the one step the budget needs
        // with room below the budget but above the low-water mark nothing steps until the textures are old enough
        var (r2, host2) = Crowd(budget: 1L << 30, minResidence: 60);
        for (int f = 0; f < 5; f++) host2.Frame(Cam(), f);
        long used = r2.ResidentBytes;
        r2.SetBudget((long)(used / 0.95));                               // under the budget, over nothing
        host2.Frame(Cam(), 10);
        Assert.Equal(0, r2.StepsDown);
    }

    [Fact]
    public void Hysteresis_and_thrash_a_camera_jittering_at_a_boundary_does_not_flap_levels()
    {
        int Run(int minResidence, double margin)
        {
            var r = new TextureResidency(new() { BudgetBytes = 3 * At(1024, 0), MinResidence = TestClock.Frames(minResidence), DisplaceMargin = margin, LowWater = 0.9, MaxLoadsPerFrame = 64 });
            var host = new Host(r, _ => 1024);
            for (int i = 1; i <= 4; i++) r.SetUser(Face((uint)i), G(i), new Bounds(new(4, (i - 2.5f) * 1.2f, 0), 6f));
            for (int f = 0; f < 600; f++)
            {
                // the camera sways a little left and right: the four textures swap ranks every few frames
                float sway = (f / 7 % 2 == 0 ? 1 : -1) * 0.4f;
                host.Frame(Cam(new Vector3(0, sway, 0)), f);
                Assert.True(r.ResidentBytes <= r.BudgetBytes + At(1024, 0));
            }
            return (int)(r.StepsDown + r.StepsUp + r.Evictions);
        }
        int guarded = Run(60, 1.25), unguarded = Run(0, 1.0);
        Assert.True(guarded < 40, $"guarded: {guarded} level changes");
        Assert.True(unguarded > 2 * guarded, $"negative control: without the guards {unguarded} vs {guarded}");
    }

    [Fact]
    public void A_step_up_soon_after_a_step_down_counts_as_thrash()
    {
        var (r, host) = Crowd(budget: 1L << 30);
        for (int f = 0; f < 3; f++) host.Frame(Cam(), f);
        r.SetBudget(r.ResidentBytes - At(1024, 0) / 2);
        host.Frame(Cam(), 3);
        Assert.True(r.StepsDown > 0);
        long thrash = r.Thrash;
        r.SetBudget(1L << 30);
        host.Frame(Cam(), 10);
        Assert.True(r.Thrash > thrash);
    }

    [Fact]
    public void A_refinement_with_no_step_down_before_it_is_not_thrash()
    {
        // found in the heavy-scene runs: every step up counted as thrash (frame - long.MinValue overflows)
        var (r, host) = Crowd(budget: 1L << 30);
        for (int f = 0; f < 5; f++) host.Frame(Cam(), f);
        Assert.True(r.StepsUp > 0);
        Assert.Equal(0, r.StepsDown);
        Assert.Equal(0, r.Thrash);
    }

    [Fact]
    public void The_same_inputs_give_the_same_level_decisions()
    {
        string Run()
        {
            var (r, host) = Crowd(budget: 9 * 1024 * KiB);
            for (int f = 0; f < 40; f++) host.Frame(Cam(new Vector3(0, MathF.Sin(f * 0.3f), 0)), f);
            return string.Join(";", host.All.Select(a => $"{a.Kind}:{a.Texture}:{a.Level}"));
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void A_failed_level_change_keeps_the_level_on_the_gpu_and_stops_changing()
    {
        var r = new TextureResidency(new() { BudgetBytes = 1L << 30 });
        r.SetUser(Face(1), G(1), new Bounds(new(3, 0, 0), 6f));
        foreach (var a in r.Update(Cam(), TestClock.Frames(0))) r.Uploaded(a.Texture, 5, 6, 1024, 1024, At(1024, 5), Avg, TestClock.Frames(0));
        var up = Assert.Single(r.Update(Cam(), TestClock.Frames(1)));
        Assert.Equal(ResidencyActionKind.Level, up.Kind);
        r.LoadFailed(G(1));
        Assert.Equal(5, r.LevelOf(G(1)));
        Assert.Equal(At(1024, 5), r.ResidentBytes);
        Assert.Equal(0, r.LoadingBytes);
        Assert.Empty(r.Update(Cam(), TestClock.Frames(2)));
    }
}
