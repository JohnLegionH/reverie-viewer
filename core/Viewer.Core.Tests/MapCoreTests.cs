// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Viewer.Core.Map;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The map's arithmetic and its tile cache, with no grid: fake fetches the test completes by hand and a test clock. Every
/// member builds its own cache, so the class runs in parallel.
/// </summary>
public class MapCoreTests
{
    // ---------------- arithmetic ----------------

    [Theory]
    [InlineData(1024.0, 1)]   // closer than one tile pixel per metre: the finest level
    [InlineData(256.0, 1)]
    [InlineData(200.0, 1)]
    [InlineData(128.0, 2)]    // the default zoom reads level 2
    [InlineData(100.0, 2)]
    [InlineData(64.0, 3)]
    [InlineData(32.0, 4)]
    [InlineData(16.0, 5)]
    [InlineData(8.0, 6)]
    [InlineData(1.0, 8)]      // never beyond the coarsest level
    [InlineData(0.0, 8)]
    public void The_detail_level_follows_the_pixels_per_region(double pixelsPerRegion, int level) =>
        Assert.Equal(level, MapMath.LevelFor(pixelsPerRegion));

    [Fact]
    public void A_regions_tile_is_the_one_whose_south_west_cell_is_the_region_rounded_down_to_the_levels_size()
    {
        Assert.Equal(new MapTileKey(1, 1003, 1002), MapMath.TileFor(1003, 1002, 1));
        Assert.Equal(new MapTileKey(2, 1002, 1002), MapMath.TileFor(1003, 1002, 2));
        Assert.Equal(new MapTileKey(3, 1000, 1000), MapMath.TileFor(1003, 1002, 3));
        Assert.Equal(new MapTileKey(8, 896, 896), MapMath.TileFor(1003, 1002, 8));   // 128 regions per tile
        Assert.Equal(1, MapMath.RegionsPerTile(1));
        Assert.Equal(4, MapMath.RegionsPerTile(3));
        Assert.Equal(128, MapMath.RegionsPerTile(8));
    }

    [Fact]
    public void Region_handles_pack_the_south_west_corner_in_metres_and_unpack_to_the_cell()
    {
        Assert.Equal(1000UL * 256 << 32 | 1001UL * 256, MapMath.HandleOf(1000, 1001));
        Assert.Equal((1000, 1001), MapMath.CellOf(MapMath.HandleOf(1000, 1001)));
        Assert.Equal(new SimHandleCheck(1099511628032256UL).Cell, MapMath.CellOf(1099511628032256UL));
    }

    private readonly record struct SimHandleCheck(ulong Handle)
    {
        public (int, int) Cell => ((int)((Handle >> 32) / 256), (int)((Handle & 0xFFFFFFFF) / 256));
    }

    [Theory]
    [InlineData("http://map.example.org:8002/", "http://map.example.org:8002/map-2-1000-1000-objects.jpg")]
    [InlineData("http://map.example.org:8002", "http://map.example.org:8002/map-2-1000-1000-objects.jpg")]
    [InlineData("https://maps.example.org/tiles/", "https://maps.example.org/tiles/map-2-1000-1000-objects.jpg")]
    public void A_tile_is_addressed_under_the_map_server_the_login_reply_names(string server, string expected) =>
        Assert.Equal(expected, MapMath.TileUri(server, new MapTileKey(2, 1000, 1000))?.ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://map.example.org/")]
    [InlineData("file:///maps/")]
    [InlineData("http://user:pw@map.example.org/")]
    public void A_map_server_that_is_not_a_plain_http_address_gives_no_tile_address(string? server) =>
        Assert.Null(MapMath.TileUri(server, new MapTileKey(1, 1, 1)));

    // ---------------- the cache ----------------

    private sealed class Fetches
    {
        public readonly Dictionary<MapTileKey, TaskCompletionSource<AssetFetchResult>> Pending = new();
        public readonly List<MapTileKey> Started = new();
        public readonly Dictionary<MapTileKey, CancellationToken> Tokens = new();

        public Task<AssetFetchResult> Fetch(MapTileKey key, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<AssetFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Pending)
            {
                Pending[key] = tcs;
                Started.Add(key);
                Tokens[key] = ct;
            }
            return tcs.Task;
        }

        public int Count { get { lock (Pending) return Started.Count; } }
        public void Complete(MapTileKey key, AssetFetchResult r)
        {
            lock (Pending)
            {
                Assert.True(Pending.ContainsKey(key), "the fetch was started");
                Pending[key].SetResult(r);
            }
        }
    }

    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
    }

    private static MapTileKey K(int x, int y = 0) => new(2, x, y);
    private static byte[] Bytes(int n, byte fill = 7) => Enumerable.Repeat(fill, n).ToArray();

    [Fact]
    public void A_tile_asked_for_twice_while_in_flight_is_fetched_once_and_arrives_for_the_next_ask()
    {
        var f = new Fetches();
        using var cache = new MapTileCache(f.Fetch);
        Assert.Equal(MapTileState.Loading, cache.Request(K(1)));
        Assert.Equal(MapTileState.Loading, cache.Request(K(1)));
        Assert.Equal(1, f.Count);
        Assert.Equal(1, cache.InFlight);
        int version = cache.Version;
        f.Complete(K(1), AssetFetchResult.Ok(Bytes(100)));
        Wait.Until(() => cache.InFlight == 0, "the fetch ends", 30000);
        Assert.True(cache.Version > version);
        Assert.Equal(MapTileState.Ready, cache.Request(K(1)));
        Assert.True(cache.TryGet(K(1), out var bytes));
        Assert.Equal(100, bytes.Length);
        Assert.Equal(1, f.Count);                       // not fetched again
        Assert.Equal(1, cache.Fetches);
        Assert.Equal(100, cache.Bytes);
        Assert.False(cache.TryGet(K(2), out _));
    }

    [Fact]
    public void At_most_four_tiles_are_in_flight_and_the_rest_wait_for_a_later_ask()
    {
        var f = new Fetches();
        using var cache = new MapTileCache(f.Fetch);
        for (int i = 0; i < 6; i++)
            cache.Request(K(i));
        Assert.Equal(MapTileCache.MaxInFlight, f.Count);
        Assert.Equal(MapTileCache.MaxInFlight, cache.InFlight);
        Assert.Equal(MapTileState.None, cache.Request(K(5)));   // not started: asked for again later
        f.Complete(K(0), AssetFetchResult.Ok(Bytes(10)));
        Wait.Until(() => cache.InFlight == MapTileCache.MaxInFlight - 1, "a slot is free");
        Assert.Equal(MapTileState.Loading, cache.Request(K(4)));
        Assert.Equal(5, f.Count);
    }

    [Fact]
    public void A_tile_the_server_does_not_have_is_missing_and_is_asked_for_again_only_after_the_long_pause()
    {
        var f = new Fetches();
        var clock = new Clock();
        using var cache = new MapTileCache(f.Fetch, clock.Read);
        cache.Request(K(1));
        f.Complete(K(1), AssetFetchResult.Of(AssetFetchStatus.NotFound));
        Wait.Until(() => cache.InFlight == 0, "the fetch ends", 30000);
        Assert.Equal(MapTileState.Missing, cache.Request(K(1)));
        clock.Now += MapTileCache.RetryAfter + TimeSpan.FromSeconds(1);      // a failure's pause is not enough for a missing tile
        Assert.Equal(MapTileState.Missing, cache.Request(K(1)));
        Assert.Equal(1, f.Count);
        clock.Now += MapTileCache.MissingRetryAfter;
        Assert.Equal(MapTileState.Loading, cache.Request(K(1)));
        Assert.Equal(2, f.Count);
    }

    [Fact]
    public void A_failed_tile_is_not_asked_for_again_before_the_pause_and_then_is()
    {
        var f = new Fetches();
        var clock = new Clock();
        using var cache = new MapTileCache(f.Fetch, clock.Read);
        cache.Request(K(1));
        f.Complete(K(1), AssetFetchResult.Of(AssetFetchStatus.Failed));
        Wait.Until(() => cache.InFlight == 0, "the fetch ends", 30000);
        Assert.Equal(MapTileState.Failed, cache.Request(K(1)));
        clock.Now += MapTileCache.RetryAfter - TimeSpan.FromSeconds(1);
        Assert.Equal(MapTileState.Failed, cache.Request(K(1)));
        Assert.Equal(1, f.Count);
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Equal(MapTileState.Loading, cache.Request(K(1)));
        Assert.Equal(2, f.Count);
    }

    [Fact]
    public void A_fetch_that_throws_is_a_failed_tile_not_a_crash()
    {
        using var cache = new MapTileCache((_, _) => throw new InvalidOperationException("boom"));
        Assert.Equal(MapTileState.Failed, cache.Request(K(1)));
        Assert.Equal(0, cache.InFlight);
        Assert.Equal(MapTileState.Failed, cache.Request(K(1)));
    }

    [Fact]
    public void A_cancelled_fetch_leaves_the_tile_free_to_be_asked_for_again()
    {
        var f = new Fetches();
        using var cache = new MapTileCache(f.Fetch);
        cache.Request(K(1));
        f.Complete(K(1), AssetFetchResult.Of(AssetFetchStatus.Cancelled));
        Wait.Until(() => cache.InFlight == 0, "the fetch ends", 30000);
        Assert.Equal(MapTileState.Loading, cache.Request(K(1)));
        Assert.Equal(2, f.Count);
    }

    [Fact]
    public void The_pictures_are_kept_within_the_budget_and_the_longest_unused_goes_first()
    {
        var f = new Fetches();
        using var cache = new MapTileCache(f.Fetch, maxBytes: 250);
        foreach (int x in new[] { 1, 2 })
        {
            cache.Request(K(x));
            f.Complete(K(x), AssetFetchResult.Ok(Bytes(100, (byte)x)));
            Wait.Until(() => cache.InFlight == 0, "the fetch ends", 30000);
        }
        Assert.True(cache.TryGet(K(1), out _));          // 1 is used again: 2 is now the longest unused
        cache.Request(K(3));
        f.Complete(K(3), AssetFetchResult.Ok(Bytes(100, 3)));
        Wait.Until(() => cache.InFlight == 0, "the fetch ends", 30000);
        Assert.Equal(200, cache.Bytes);
        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGet(K(2), out _));
        Assert.True(cache.TryGet(K(1), out _));
        Assert.True(cache.TryGet(K(3), out _));
        Assert.Equal(MapTileState.Loading, cache.Request(K(2)));   // a tile let go is fetched again when asked for
    }

    [Fact]
    public void A_tile_larger_than_the_whole_budget_is_not_kept()
    {
        var f = new Fetches();
        using var cache = new MapTileCache(f.Fetch, maxBytes: 50);
        cache.Request(K(1));
        f.Complete(K(1), AssetFetchResult.Ok(Bytes(100)));
        Wait.Until(() => cache.InFlight == 0, "the fetch ends", 30000);
        Assert.Equal(0, cache.Bytes);
        Assert.False(cache.TryGet(K(1), out _));
        Assert.Equal(MapTileState.Failed, cache.Request(K(1)));
    }

    [Fact]
    public void Disposing_cancels_what_is_in_flight_and_a_late_answer_changes_nothing()
    {
        var f = new Fetches();
        var cache = new MapTileCache(f.Fetch);
        cache.Request(K(1));
        Assert.True(f.Tokens.ContainsKey(K(1)), "the fetch was started");
        var token = f.Tokens[K(1)];
        cache.Dispose();
        Assert.True(token.IsCancellationRequested);
        f.Complete(K(1), AssetFetchResult.Ok(Bytes(10)));
        Thread.Sleep(50);                                      // a window that proves nothing happened
        Assert.Equal(0, cache.Count);
        Assert.Equal(MapTileState.None, cache.Request(K(2)));  // a disposed cache starts nothing
        Assert.Equal(1, f.Count);
    }
}
