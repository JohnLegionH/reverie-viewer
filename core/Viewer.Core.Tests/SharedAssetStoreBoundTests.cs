// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Viewer.Core.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// The shared asset store keeps its complete files within a
/// byte bound, the least recently used let go first. An entry let go comes back from the content store when there is one
/// (never from the network again), and from the network when there is none. A reader holding an entry's bytes is never
/// affected by its eviction; an asset larger than the bound is not held at all; partial fetches never enter the store.
/// </summary>
public sealed class SharedAssetStoreBoundTests(ITestOutputHelper output)
{
    private static Guid Id(int i) => new($"33333333-0000-0000-0000-{i:D12}");

    private static byte[] Bytes(int i, int length)
    {
        var b = new byte[length];
        new Random(i).NextBytes(b);
        return b;
    }

    /// <summary>A "network" serving <paramref name="length"/> distinct bytes per id, counting its fetches per id.</summary>
    private static (Func<Guid, CancellationToken, Task<AssetFetchResult>> Fetch, Dictionary<Guid, int> Calls) Network(int length, Func<Guid, byte[]>? content = null)
    {
        var calls = new Dictionary<Guid, int>();
        return ((id, _) =>
        {
            lock (calls) calls[id] = calls.GetValueOrDefault(id) + 1;
            return Task.FromResult(AssetFetchResult.Ok(content?.Invoke(id) ?? Bytes(id.GetHashCode(), length)));
        }, calls);
    }

    [Fact]
    public async Task Under_many_assets_the_stores_bytes_never_exceed_its_bound_and_the_least_recently_used_go_first()
    {
        const int size = 1000;
        var store = new SharedAssetStore(maxBytes: 3 * size + size / 2);   // room for three
        var (fetch, calls) = Network(size);
        using var a = new SessionAssets(store, _ => true, fetch);
        for (int i = 0; i < 3; i++) await a.GetAsync(Id(i), CancellationToken.None);
        await a.GetAsync(Id(0), CancellationToken.None);                   // 0 used again: 1 is now the least recently used
        for (int i = 3; i < 40; i++)
        {
            await a.GetAsync(Id(i), CancellationToken.None);
            Assert.True(store.Bytes <= store.Bound, $"{store.Bytes} bytes held, bound {store.Bound}");
            if (i == 3)
            {
                Assert.True(store.Contains(Id(0)), "the entry used most recently was let go before an older one");
                Assert.False(store.Contains(Id(1)), "the least recently used entry was kept");
            }
        }
        var s = store.Stats;
        output.WriteLine($"{s}");
        Assert.True(s.PeakBytes <= store.Bound, $"peak {s.PeakBytes}, bound {store.Bound}");
        Assert.Equal(3, store.Count);
        Assert.Equal(37, s.LetGo);
        Assert.Equal(SharedAssetStore.MaxBytes, new SharedAssetStore().Bound);
    }

    [Fact]
    public async Task An_entry_let_go_comes_back_from_the_content_store_byte_for_byte_without_a_network_fetch()
    {
        var root = TestTemp.NewFolderPath("storebound");
        try
        {
            const int size = 4000;
            var view = new ContentStore(new ContentStoreOptions { Root = root, CapBytes = 1L << 30 }).View("grid", "s");
            var store = new SharedAssetStore(maxBytes: 2 * size);
            var (network, calls) = Network(size);
            using var a = new SessionAssets(store, _ => true, view.Caching("texture", network));
            var first = (await a.GetAsync(Id(0), CancellationToken.None)).Bytes.ToArray();
            for (int i = 1; i <= 3; i++) await a.GetAsync(Id(i), CancellationToken.None);
            Assert.False(store.Contains(Id(0)));
            var again = await a.GetAsync(Id(0), CancellationToken.None);
            Assert.Equal(AssetFetchStatus.Ok, again.Status);
            Assert.Equal(first, again.Bytes.ToArray());
            Assert.Equal(1, calls[Id(0)]);                                   // from the content store, not the network
            Assert.True(store.Contains(Id(0)));                              // and held again
            // without a content store, an entry let go is fetched again
            var bare = new SharedAssetStore(maxBytes: 2 * size);
            var (net2, calls2) = Network(size);
            using var b = new SessionAssets(bare, _ => true, net2);
            for (int i = 0; i <= 3; i++) await b.GetAsync(Id(i), CancellationToken.None);
            Assert.Equal(first.Length, (await b.GetAsync(Id(0), CancellationToken.None)).Bytes.Length);
            Assert.Equal(2, calls2[Id(0)]);
        }
        finally { TestTemp.Delete(root); }
    }

    [Fact]
    public async Task An_entry_being_decoded_when_it_is_let_go_is_not_changed_for_its_reader()
    {
        var codestream = LmvTexture.Encode(128, 128, TextureLevelDecodeTests.Smooth(128, 128, true), true);
        var expected = LmvTexture.Decode(codestream).Texture!.Rgba;
        var store = new SharedAssetStore(maxBytes: codestream.Length + 100);
        var (fetch, _) = Network(codestream.Length, id => id == Id(0) ? (byte[])codestream.Clone() : Bytes(id.GetHashCode(), codestream.Length));
        using var a = new SessionAssets(store, _ => true, fetch);
        var held = (await a.GetAsync(Id(0), CancellationToken.None)).Bytes;   // the reader's bytes, straight from the store
        await a.GetAsync(Id(0), CancellationToken.None);
        using var go = new ManualResetEventSlim();
        var evict = Task.Run(async () =>
        {
            go.Wait();
            for (int i = 1; i < 30; i++) await a.GetAsync(Id(i), CancellationToken.None);   // Id(0) is let go meanwhile
        });
        go.Set();
        for (int k = 0; k < 5; k++)
            Assert.Equal(expected, LmvTexture.Decode(held.ToArray()).Texture!.Rgba);
        await evict;
        Assert.False(store.Contains(Id(0)));
        Assert.Equal(codestream, held.ToArray());
        Assert.Equal(expected, LmvTexture.Decode(held.ToArray()).Texture!.Rgba);
    }

    [Fact]
    public async Task An_asset_larger_than_the_bound_is_not_held_and_still_decodes()
    {
        var codestream = LmvTexture.Encode(128, 128, TextureLevelDecodeTests.Smooth(128, 128, false), false);
        var store = new SharedAssetStore(maxBytes: codestream.Length - 1);
        var (fetch, calls) = Network(codestream.Length, _ => codestream);
        using var a = new SessionAssets(store, _ => true, fetch);
        var r = await a.GetAsync(Id(0), CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Ok, r.Status);
        Assert.True(LmvTexture.Decode(r.Bytes.ToArray()).Ok);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.Bytes);
        Assert.Equal(1, store.Stats.TooLarge);
        await a.GetAsync(Id(0), CancellationToken.None);
        Assert.Equal(2, calls[Id(0)]);                                       // not held, so asked of the fetcher again
    }

    [Fact]
    public async Task Partial_fetches_never_enter_the_store_and_the_partial_fetchers_own_bound_is_unchanged()
    {
        var store = new SharedAssetStore(maxBytes: 1 << 20);
        using var a = new SessionAssets(store, _ => true, (_, _) => Task.FromResult(AssetFetchResult.Ok(new byte[1000])),
            fetchLevel: (_, _, _, _) => Task.FromResult(AssetFetchResult.Ok(new byte[500]) with { Partial = true }));
        var r = await a.GetLevelAsync(Id(0), 3, false, CancellationToken.None);
        Assert.True(r.Partial);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.Bytes);
        Assert.Equal(32L << 20, PartialTextureFetcher.MaxHeldBytes);
    }
}
