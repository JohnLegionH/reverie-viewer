// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Viewer.Core.Imaging;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// A level decode's own allocations stay within one
/// level-sized buffer beyond what the decoder (CoreJ2K) allocates, pooled buffers never carry an earlier texture's bytes,
/// the pool keeps to its cap, and the partial fetcher's held bytes keep to their bound (evicting to the content store).
/// </summary>
public sealed class DecodeMemoryTests(ITestOutputHelper output)
{
    /// <summary>What a level decode may allocate beyond the decoder's own and one level-sized buffer: small objects (the
    /// result records, the guard's reading of the header).</summary>
    private const long Slack = 64 * 1024;

    /// <summary>A bake-shaped codestream (our own content): 1024x1024, 5 components, CoreJ2K's default quality layers
    /// (more than 8: the case that used to take a full decode and a downscale for every reduced level).</summary>
    private static readonly Lazy<byte[]> Bake = new(() =>
    {
        var rgba = TextureLevelDecodeTests.Smooth(1024, 1024, true);
        var rng = new Random(7);
        for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)Math.Clamp(rgba[i] + rng.Next(-40, 41), 0, 255);   // detail: many layers
        var planes = Enumerable.Range(0, 5).Select(c => Enumerable.Range(0, 1024 * 1024).Select(i => c == 4 ? (byte)255 : rgba[i * 4 + c]).ToArray()).ToList();
        return LmvTexture.EncodePlanes(1024, 1024, planes);
    });

    /// <summary>The decoder's own allocation for the level (measured warm, on this thread): what any use of CoreJ2K's output
    /// costs whatever we do with it.</summary>
    private static long DecoderAlone(byte[] b, int level)
    {
        Assert.Null(J2kGuard.Validate(b, null, out var h));
        void Run()
        {
            bool reduced = level > 0;
            if (!reduced && h!.Components <= 4)
            {
                new LibreMetaverse.Assets.AssetTexture(LibreMetaverse.UUID.Zero, b).Decode();
                return;
            }
            var cfg = new CoreJ2K.Configuration.J2KDecoderConfiguration();
            if (reduced) cfg = cfg.WithResolutionLevel(h!.DecompositionLevels - level);
            using var img = CoreJ2K.J2kImage.FromBytes(b, cfg);
            for (int c = 0; c < Math.Min(img.NumberOfComponents, 4); c++) img.GetComponentBytes(c);
        }
        Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    public static TheoryData<string, int> Levels() => new()
    {
        { "texture", 0 }, { "texture", 1 }, { "texture", 3 }, { "bake", 1 }, { "bake", 3 },
    };

    [Theory]
    [MemberData(nameof(Levels))]
    public void A_level_decode_allocates_at_most_one_level_sized_buffer_beyond_the_decoders_own(string kind, int level)
    {
        var b = kind == "bake" ? Bake.Value : PartialFetchTests.Codestream.Value;
        long decoder = DecoderAlone(b, level);
        Assert.Null(J2kGuard.Validate(b, J2kLimits.Default, out var h));
        Assert.True(kind != "bake" || h!.Layers > TextureByteRange.MaxLayers, $"the bake declares {h!.Layers} layers: not a many-layer codestream");
        var pool = new PixelPool(PixelPool.DefaultCapBytes);
        // the viewer's GPU path: the level with its mip chain; the texture source releases it once its engine image exists
        for (int i = 0; i < 2; i++) LmvTexture.DecodeLevel(b, level, mipmaps: true, pool: pool).Texture!.ReleasePixels();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var d = LmvTexture.DecodeLevel(b, level, mipmaps: true, pool: pool).Texture!;
        long ours = GC.GetAllocatedBytesForCurrentThread() - before - decoder;
        long levelBytes = 4L * d.Width * d.Height;
        d.ReleasePixels();
        output.WriteLine($"{kind} L{level} {d.Width}x{d.Height}: decoder {decoder / 1024} KiB, ours {ours / 1024} KiB, level {levelBytes / 1024} KiB");
        Assert.True(ours <= levelBytes + Slack, $"{kind} L{level}: {ours / 1024} KiB of our own allocations, more than one level-sized buffer ({levelBytes / 1024} KiB)");
    }

    [Fact]
    public void A_pooled_buffer_never_carries_an_earlier_textures_bytes_into_a_new_image()
    {
        var a = PartialFetchTests.Codestream.Value;
        // B: another image of the same size, fetched in part (a prefix: its finer detail missing), and a smaller one
        var other = TextureLevelDecodeTests.Smooth(1024, 1024, false);
        var rng = new Random(9);
        for (int i = 0; i < other.Length; i += 4) other[i] = (byte)Math.Clamp(255 - other[i] + rng.Next(-40, 41), 0, 255);
        var bWhole = LmvTexture.EncodeLayered(1024, 1024, other, "0.0625 0.25 0.5 1.0 2.0", 2.0);
        var bPrefix = bWhole[..(int)Math.Min(TextureByteRange.BytesFor(1024, 1024, 2)!.Value, bWhole.Length / 2)];
        output.WriteLine($"B: {bWhole.Length} bytes, prefix {bPrefix.Length}");
        var small = LmvTexture.EncodeLayered(512, 512, TextureLevelDecodeTests.Smooth(512, 512, false), "0.0625 0.25 0.5 1.0 2.0", 2.0);
        foreach (var (name, b, level, prefix) in new[] { ("truncated, same size", bPrefix, 2, true), ("smaller", small, 1, false), ("same size, whole", bWhole, 1, false) })
        {
            var fresh = LmvTexture.DecodeLevel(b, level, mipmaps: true, prefix: prefix, pool: new PixelPool(PixelPool.DefaultCapBytes)).Texture!;
            var pool = new PixelPool(PixelPool.DefaultCapBytes);
            for (int l = 0; l <= 3; l++) LmvTexture.DecodeLevel(a, l, mipmaps: true, pool: pool).Texture!.ReleasePixels();   // A's buffers, back in the pool
            long reused = pool.Stats.Reused;
            var d = LmvTexture.DecodeLevel(b, level, mipmaps: true, prefix: prefix, pool: pool).Texture!;
            output.WriteLine($"{name}: {d.Width}x{d.Height}, buffers reused from A's {pool.Stats.Reused - reused}");
            Assert.Equal(fresh.Rgba, d.Rgba);
            Assert.Equal(fresh.Mips, d.Mips);
            if (name != "smaller") Assert.True(pool.Stats.Reused > reused, $"{name}: no buffer of A's was reused, so the check proves nothing");
        }
    }

    [Fact]
    public void The_pool_keeps_at_most_its_cap_and_a_buffer_returned_twice_once()
    {
        const int size = 256 * 1024;
        var pool = new PixelPool(3L * size);
        var rented = Enumerable.Range(0, 5).Select(_ => pool.Rent(size)).ToList();
        foreach (var r in rented) pool.Return(r);
        pool.Return(rented[0]);   // twice
        var s = pool.Stats;
        Assert.True(s.HeldBytes <= pool.CapBytes, $"{s.HeldBytes} bytes held, cap {pool.CapBytes}");
        Assert.Equal(3, s.Returned);
        Assert.Equal(2, s.LetGo);
        var again = Enumerable.Range(0, 4).Select(_ => pool.Rent(size)).ToList();
        Assert.Equal(3, pool.Stats.Reused);
        Assert.Equal(4, again.Distinct(ReferenceEqualityComparer.Instance).Count());   // never the same buffer twice
        Assert.Equal(0, pool.Stats.HeldBytes);
        Assert.NotSame(pool.Rent(size - 4), pool.Rent(size - 4));
        pool.Return(new byte[size + 4]);
        Assert.Equal(size + 4, pool.Rent(size + 4).Length);   // exact lengths only
        Assert.NotEqual(size + 4, pool.Rent(size).Length);
    }

    // ----: the partial fetcher's held bytes ----

    private static Guid Id(int i) => new($"22222222-0000-0000-0000-{i:D12}");

    /// <summary>Exact range answers from one file for every id, after a short wait (so many are in flight at once).</summary>
    private static (PartialTextureFetcher Fetcher, List<(Guid Id, long From)> Calls) Fetcher(long bound, ContentView? store = null)
    {
        var file = PartialFetchTests.Codestream.Value;
        var calls = new List<(Guid, long)>();
        Func<Guid, long, long?, int, CancellationToken, Task<RangeAnswer>> range = async (id, from, to, cap, ct) =>
        {
            lock (calls) calls.Add((id, from));
            await Task.Delay(5, ct);
            long end = Math.Min(to ?? file.Length - 1, file.Length - 1);
            var body = file[(int)from..(int)(end + 1)];
            return new RangeAnswer(end == file.Length - 1 && from == 0 ? RangeAnswerKind.Whole : RangeAnswerKind.Part, 206, body, from, file.Length, null, body.Length);
        };
        return (new PartialTextureFetcher(range, (_, _) => Task.FromResult(AssetFetchResult.Ok(file)), store, maxHeldBytes: bound), calls);
    }

    [Fact]
    public async Task Under_many_textures_in_flight_the_partial_fetchers_held_bytes_never_exceed_its_bound()
    {
        const long bound = 512 * 1024;
        var (f, _) = Fetcher(bound);
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => f.FetchLevelAsync(Id(i), 4, false, CancellationToken.None)));
        Assert.All(results, r => Assert.True(r.Status == AssetFetchStatus.Ok && r.Partial));
        output.WriteLine($"64 textures at level 4 ({results[0].Bytes.Length} bytes each): peak held {f.PeakHeldBytes}, bound {bound}");
        Assert.True(f.PeakHeldBytes <= bound, $"peak held {f.PeakHeldBytes} bytes, bound {bound}");
        Assert.True(f.HeldBytes > 0);
        Assert.Equal(PartialTextureFetcher.MaxHeldBytes, Fetcher(PartialTextureFetcher.MaxHeldBytes).Fetcher.HeldBound);
    }

    [Fact]
    public async Task A_texture_evicted_at_the_bound_continues_from_its_content_store_entry()
    {
        var root = TestTemp.NewFolderPath("heldbound");
        try
        {
            var view = new ContentStore(new ContentStoreOptions { Root = root, CapBytes = 1L << 30 }).View("grid", "s");
            long one = TextureByteRange.FirstRequest(4)!.Value;
            var (f, calls) = Fetcher(2 * one + one / 2, view);   // room for two textures' first requests
            var first = await f.FetchLevelAsync(Id(0), 4, false, CancellationToken.None);
            for (int i = 1; i <= 3; i++) await f.FetchLevelAsync(Id(i), 4, false, CancellationToken.None);   // Id(0) is the oldest: evicted
            Assert.True(f.HeldBytes <= 2 * one + one / 2);
            long fromStore = f.Stats.FromStore;
            var finer = await f.FetchLevelAsync(Id(0), 2, false, CancellationToken.None);
            Assert.Equal(AssetFetchStatus.Ok, finer.Status);
            Assert.Equal(PartialFetchTests.Codestream.Value[..finer.Bytes.Length], finer.Bytes.ToArray());
            Assert.Equal(fromStore + 1, f.Stats.FromStore);   // read back from its partial entry
            Assert.Equal(first.Bytes.Length, calls.Last(c => c.Id == Id(0)).From);   // and continued where it stopped
            // without a store, an evicted texture starts again from its first byte
            var (bare, bareCalls) = Fetcher(2 * one + one / 2);
            await bare.FetchLevelAsync(Id(0), 4, false, CancellationToken.None);
            for (int i = 1; i <= 3; i++) await bare.FetchLevelAsync(Id(i), 4, false, CancellationToken.None);
            var again = await bare.FetchLevelAsync(Id(0), 2, false, CancellationToken.None);
            Assert.Equal(PartialFetchTests.Codestream.Value[..again.Bytes.Length], again.Bytes.ToArray());
            Assert.Equal(0, bareCalls.Where(c => c.Id == Id(0)).Skip(1).First().From);
        }
        finally { TestTemp.Delete(root); }
    }
}
