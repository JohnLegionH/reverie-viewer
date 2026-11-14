// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using System.Text;
using Viewer.Core.Assets;
using Viewer.Core.Imaging;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Partial texture fetch over the network against a local test server on loopback
///</summary>
public sealed class PartialFetchTests : IDisposable
{
    private static readonly Guid Tex = new("11111111-2222-3333-4444-555555555555");
    private readonly string _root = TestTemp.NewFolderPath("partial");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public void Dispose()
    {
        _http.Dispose();
        TestTemp.Delete(_root);
    }

    private static byte[] Detailed()
    {
        var rgba = TextureLevelDecodeTests.Smooth(1024, 1024, false);
        var rng = new Random(5);
        for (int i = 0; i < rgba.Length; i++) if (i % 4 != 3) rgba[i] = (byte)Math.Clamp(rgba[i] + rng.Next(-40, 41), 0, 255);
        return rgba;
    }

    /// <summary>A 1024x1024 codestream with detail (our own content), large enough that levels 3-5 need less than the file:
    /// five quality layers, layer-progressive, like the grid's textures (1-7 layers measured).</summary>
    internal static readonly Lazy<byte[]> Codestream = new(() => LmvTexture.EncodeLayered(1024, 1024, Detailed(), "0.0625 0.25 0.5 1.0 2.0", 2.0));

    /// <summary>The same image through LibreMetaverse's own encoder: 27 thin quality layers (not the grid's layout; CoreJ2K's
    /// reduced-resolution decode of it is not repeatable, measured).</summary>
    internal static readonly Lazy<byte[]> ManyLayers = new(() => LmvTexture.Encode(1024, 1024, Detailed(), false));

    private LoopbackTextureServer Server(RangeServerMode mode = RangeServerMode.Honour) => new(id => id == Tex ? Codestream.Value : null, mode);

    private (PartialTextureFetcher Fetcher, Func<int> WholeCalls) Fetcher(LoopbackTextureServer server, ContentView? store = null, Func<Guid, CancellationToken, Task<AssetFetchResult>>? whole = null)
    {
        int calls = 0;
        whole ??= async (id, ct) =>
        {
            var a = await RangeHttp.GetAsync(_http, server.UriFor(id), 0, null, J2kLimits.Default.MaxBytes, ct);
            return a.Kind is RangeAnswerKind.Whole or RangeAnswerKind.Part ? AssetFetchResult.Ok(a.Body!) : AssetFetchResult.Of(AssetFetchStatus.NotFound);
        };
        var counted = new Func<Guid, CancellationToken, Task<AssetFetchResult>>((id, ct) => { Interlocked.Increment(ref calls); return whole(id, ct); });
        return (new PartialTextureFetcher((id, from, to, cap, ct) => RangeHttp.GetAsync(_http, server.UriFor(id), from, to, cap, ct), counted, store), () => calls);
    }

    private static double Mad(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average();

    [Fact]
    public async Task A_coarse_level_fetches_only_its_estimate_and_decodes_within_tolerance()
    {
        using var server = Server();
        var (f, wholeCalls) = Fetcher(server);
        var cs = Codestream.Value;
        long estimate = TextureByteRange.BytesFor(1024, 1024, 4)!.Value;
        Assert.True(cs.Length > estimate, $"precondition: the file ({cs.Length}) is larger than level 4's estimate ({estimate})");
        var r = await f.FetchLevelAsync(Tex, 4, more: false, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Ok, r.Status);
        Assert.True(r.Partial);
        Assert.Equal(estimate, r.Bytes.Length);
        Assert.Equal(cs[..(int)estimate], r.Bytes.ToArray());
        Assert.Equal(estimate, server.BodyBytes);          // nothing twice
        Assert.Equal(1, server.RangeRequests);             // the first request is sized for the level (was 600 bytes, then the rest)
        Assert.Equal(0, wholeCalls());
        var fromPrefix = LmvTexture.DecodeLevel(r.Bytes.ToArray(), 4, prefix: true);
        Assert.True(fromPrefix.Ok, fromPrefix.Refusal);
        Assert.True(Mad(fromPrefix.Texture!.Rgba, LmvTexture.DecodeLevel(cs, 4).Texture!.Rgba) <= 8);
    }

    /// <summary>A texture asked for at a level gets its first image from ONE request (before, 600 bytes
    /// and then the level's bytes: two of the grid's ~105 ms waits in series), with the same bytes as before.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(4)]
    [InlineData(3)]
    [InlineData(2)]
    public async Task A_texture_at_the_level_asked_gets_its_first_image_from_one_request(int level)
    {
        using var server = Server();
        var (f, wholeCalls) = Fetcher(server);
        var cs = Codestream.Value;
        var r = await f.FetchLevelAsync(Tex, level, more: false, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Ok, r.Status);
        Assert.Equal(1, server.RangeRequests);
        Assert.Equal(0, wholeCalls());
        long want = Math.Min(cs.Length, TextureByteRange.BytesFor(1024, 1024, level)!.Value);
        Assert.Equal(want, r.Bytes.Length);                // the bytes the level needed before: no more, no less
        Assert.Equal(want, server.BodyBytes);
        var image = LmvTexture.DecodeLevel(r.Bytes.ToArray(), level, prefix: r.Partial);
        Assert.True(image.Ok, image.Refusal);
        Assert.True(Mad(image.Texture!.Rgba, LmvTexture.DecodeLevel(cs, level).Texture!.Rgba) <= 8);
    }

    [Fact]
    public async Task Negative_control_the_partial_path_fetches_fewer_bytes_than_the_whole_file_path()
    {
        using var partial = Server();
        await Fetcher(partial).Fetcher.FetchLevelAsync(Tex, 4, false, CancellationToken.None);
        using var whole = Server();
        await Fetcher(whole).Fetcher.FetchLevelAsync(Tex, 0, false, CancellationToken.None);
        Assert.Equal(Codestream.Value.Length, whole.BodyBytes);
        Assert.True(partial.BodyBytes < whole.BodyBytes / 2, $"partial {partial.BodyBytes} vs whole {whole.BodyBytes}");
    }

    [Fact]
    public async Task Refining_asks_for_the_rest_from_where_it_stopped_and_appends_until_the_file_is_whole()
    {
        using var server = Server();
        var (f, wholeCalls) = Fetcher(server);
        var cs = Codestream.Value;
        Assert.True(cs.Length > TextureByteRange.BytesFor(1024, 1024, 3), $"precondition: the file ({cs.Length}) is larger than level 3's estimate");
        await f.FetchLevelAsync(Tex, 4, false, CancellationToken.None);
        long after4 = server.BodyBytes;
        var r3 = await f.FetchLevelAsync(Tex, 3, false, CancellationToken.None);
        Assert.True(r3.Partial);
        Assert.Equal(cs[..r3.Bytes.Length], r3.Bytes.ToArray());
        Assert.Equal(r3.Bytes.Length, server.BodyBytes);      // level 3 asked only for the bytes after level 4's
        var r0 = await f.FetchLevelAsync(Tex, 0, false, CancellationToken.None);
        Assert.False(r0.Partial);
        Assert.Equal(cs, r0.Bytes.ToArray());
        Assert.Equal(cs.Length, server.BodyBytes);            // every byte exactly once
        Assert.True(server.RangeRequests >= 3);
        Assert.True(after4 < cs.Length);
        Assert.Equal(0, wholeCalls());
        Assert.Equal(0, f.Stats.WholeFallbacks);
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_gives_the_whole_file_as_complete()
    {
        using var server = Server(RangeServerMode.IgnoreRange);
        var (f, wholeCalls) = Fetcher(server);
        var r = await f.FetchLevelAsync(Tex, 4, false, CancellationToken.None);
        Assert.False(r.Partial);
        Assert.Equal(Codestream.Value, r.Bytes.ToArray());
        Assert.Equal(1, server.Requests);
        Assert.Equal(0, wholeCalls());
    }

    [Theory]
    [InlineData(RangeServerMode.Always416)]
    [InlineData(RangeServerMode.WrongContentRange)]
    [InlineData(RangeServerMode.NoContentRange)]
    [InlineData(RangeServerMode.ShortBody)]
    public async Task A_failed_partial_fetch_falls_back_to_one_whole_file_fetch(RangeServerMode mode)
    {
        using var server = Server(mode);
        // the fallback is the transport's ordinary fetch: here the file itself, counted
        var (f, wholeCalls) = Fetcher(server, whole: (_, _) => Task.FromResult(AssetFetchResult.Ok(Codestream.Value)));
        var r = await f.FetchLevelAsync(Tex, 4, false, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Ok, r.Status);
        Assert.False(r.Partial);
        Assert.Equal(Codestream.Value, r.Bytes.ToArray());
        Assert.Equal(1, wholeCalls());
        Assert.Equal(1, f.Stats.WholeFallbacks);
        Assert.Equal(1, server.Requests);   // one failed range request, no retry of it
    }

    [Fact]
    public async Task When_the_fallback_fails_too_the_texture_fails_without_a_loop()
    {
        using var server = Server(RangeServerMode.Always416);
        var (f, wholeCalls) = Fetcher(server, whole: (_, _) => Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.NotFound)));
        var r = await f.FetchLevelAsync(Tex, 4, false, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.NotFound, r.Status);
        Assert.Equal(1, wholeCalls());
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task A_texture_the_server_does_not_have_is_not_found_without_a_fallback()
    {
        using var server = Server();
        var (f, wholeCalls) = Fetcher(server);
        var r = await f.FetchLevelAsync(Guid.NewGuid(), 4, false, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.NotFound, r.Status);
        Assert.Equal(0, wholeCalls());
    }

    [Fact]
    public async Task A_refinement_whose_answer_goes_wrong_falls_back_once()
    {
        using var server = Server();
        var (f, wholeCalls) = Fetcher(server, whole: (_, _) => Task.FromResult(AssetFetchResult.Ok(Codestream.Value)));
        await f.FetchLevelAsync(Tex, 4, false, CancellationToken.None);
        server.Mode = RangeServerMode.WrongContentRange;
        var r = await f.FetchLevelAsync(Tex, 1, false, CancellationToken.None);
        Assert.False(r.Partial);
        Assert.Equal(Codestream.Value, r.Bytes.ToArray());
        Assert.Equal(1, wholeCalls());
    }

    /// <summary>How long a cancelled fetch may take to end: far inside the client's 20 s timeout, so an end by that timeout
    /// (or a fetch that never ends: the stalled body has no timeout of its own) fails here, and the suite's load cannot.</summary>
    private static readonly TimeSpan CancelEnds = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_slow_body_is_cancelled_by_the_session_promptly()
    {
        using var server = Server(RangeServerMode.SlowBody);
        var (f, wholeCalls) = Fetcher(server);
        var assets = new SessionAssets(new SharedAssetStore(), _ => true, (_, _) => Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.Failed)), fetchLevel: f.FetchLevelAsync);
        var task = assets.GetLevelAsync(Tex, 4, false, CancellationToken.None);
        await server.BodyStalled.WaitAsync(TimeSpan.FromSeconds(30));   // the server has sent the first bytes and stalls
        Assert.False(task.IsCompleted);   // stalled in the body
        assets.Dispose();                 // logout / region change / disconnect all end in the session's dispose or the transport's cancel
        var r = await task.WaitAsync(CancelEnds);
        Assert.Equal(AssetFetchStatus.Cancelled, r.Status);   // cancelled: not failed, not found, not a timeout's answer
        Assert.Equal(16, server.BodyBytes);                   // only the bytes sent before the stall
        Assert.Equal(1, server.Requests);
        Assert.Equal(0, wholeCalls());                        // a cancel is not a failure: no whole-file fallback
    }

    [Fact]
    public async Task A_cancelled_token_stops_a_range_request_in_the_body()
    {
        using var server = Server(RangeServerMode.SlowBody);
        using var cts = new CancellationTokenSource();
        var fetch = RangeHttp.GetAsync(_http, server.UriFor(Tex), 0, 599, J2kLimits.Default.MaxBytes, cts.Token);
        await server.BodyStalled.WaitAsync(TimeSpan.FromSeconds(30));   // the server has sent the first bytes and stalls
        Assert.False(fetch.IsCompleted);
        cts.Cancel();
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(CancelEnds));
        Assert.Equal(cts.Token, e.CancellationToken);       // our token's cancel, not the client's timeout
        Assert.IsNotType<TimeoutException>(e.InnerException);
        Assert.Equal(16, server.BodyBytes);
    }

    [Fact]
    public async Task The_byte_cap_holds_for_range_answers()
    {
        using var server = Server();
        // a file whose total is over the cap: refused from the Content-Range, before its body
        var a = await RangeHttp.GetAsync(_http, server.UriFor(Tex), 0, 599, capBytes: 1000, CancellationToken.None);
        Assert.Equal(RangeAnswerKind.Failed, a.Kind);
        Assert.Equal(0, a.BytesRead);
        server.Mode = RangeServerMode.IgnoreRange;   // a 200 over the cap is refused too
        var b = await RangeHttp.GetAsync(_http, server.UriFor(Tex), 0, 599, capBytes: 1000, CancellationToken.None);
        Assert.Equal(RangeAnswerKind.Failed, b.Kind);
    }

    [Fact]
    public async Task A_codestream_with_many_thin_layers_is_fetched_whole()
    {
        var many = ManyLayers.Value;
        Assert.Null(J2kGuard.Validate(many, null, out var h));
        Assert.True(h!.Layers > TextureByteRange.MaxLayers, $"{h.Layers} layers");
        using var server = new LoopbackTextureServer(id => id == Tex ? many : null);
        var (f, _) = Fetcher(server);
        var r = await f.FetchLevelAsync(Tex, 4, false, CancellationToken.None);
        Assert.False(r.Partial);
        Assert.Equal(many, r.Bytes.ToArray());
        Assert.Equal(many.Length, server.BodyBytes);   // the first chunk and then the rest: the whole file, once
    }

    // ---- content store: partial entries ----

    private ContentStore Store(long cap = 1L << 30) => new(new ContentStoreOptions { Root = _root, CapBytes = cap });

    [Fact]
    public void A_partial_entry_round_trips_survives_a_restart_and_is_never_served_as_complete()
    {
        var bytes = Codestream.Value[..5000];
        Store().View("grid", "s").PutPartial("texture", Tex, bytes, Codestream.Value.Length);
        var v = Store().View("grid", "s");   // a new instance over the same directory: a restart
        Assert.True(v.TryGetPartial("texture", Tex, out var back, out var total));
        Assert.Equal(bytes, back);
        Assert.Equal(Codestream.Value.Length, total);
        Assert.False(v.TryGet("texture", Tex, out _));   // never the complete entry
        // encrypted at rest: no run of the codestream's bytes appears in any file
        var needle = bytes[1000..1032];
        foreach (var file in Directory.GetFiles(_root))
            Assert.Equal(-1, File.ReadAllBytes(file).AsSpan().IndexOf(needle));
    }

    [Fact]
    public void A_shorter_partial_entry_never_replaces_a_longer_one()
    {
        var v = Store().View("grid", "s");
        v.PutPartial("texture", Tex, Codestream.Value[..8000], null);
        v.PutPartial("texture", Tex, Codestream.Value[..600], null);
        Assert.True(v.TryGetPartial("texture", Tex, out var back, out var total));
        Assert.Equal(8000, back.Length);
        Assert.Null(total);
    }

    [Fact]
    public async Task After_a_restart_a_finer_request_extends_the_stored_entry_and_a_complete_one_becomes_ordinary()
    {
        using var server = Server();
        var cs = Codestream.Value;
        await Fetcher(server, Store().View("grid", "s")).Fetcher.FetchLevelAsync(Tex, 4, false, CancellationToken.None);
        long first = server.BodyBytes;
        Assert.True(Store().View("grid", "s").TryGetPartial("texture", Tex, out var stored, out _));
        Assert.Equal(first, stored.Length);

        var (again, wholeCalls) = Fetcher(server, Store().View("grid", "s"));   // restart: nothing in memory
        var r2 = await again.FetchLevelAsync(Tex, 3, false, CancellationToken.None);
        Assert.True(r2.Partial);
        Assert.Equal(r2.Bytes.Length, server.BodyBytes);   // only the new bytes were fetched: the stored ones were not asked for again
        Assert.True(Store().View("grid", "s").TryGetPartial("texture", Tex, out var extended, out _));
        Assert.Equal(r2.Bytes.Length, extended.Length);

        var r0 = await again.FetchLevelAsync(Tex, 0, false, CancellationToken.None);
        Assert.Equal(cs, r0.Bytes.ToArray());
        var v = Store().View("grid", "s");
        Assert.True(v.TryGet("texture", Tex, out var complete));
        Assert.Equal(cs, complete);
        Assert.False(v.TryGetPartial("texture", Tex, out _, out _));   // the partial entry is gone
        Assert.Equal(cs.Length, server.BodyBytes);
        Assert.Equal(0, wholeCalls());
    }

    [Fact]
    public void Partial_entries_count_against_the_cap_and_are_evicted_like_any_other()
    {
        var s = Store(cap: 40_000);
        var v = s.View("grid", "s");
        for (int i = 0; i < 10; i++)
            v.PutPartial("texture", new Guid(i, 0, 0, new byte[8]), Codestream.Value[..8000], null);
        Assert.True(s.SizeBytes <= 40_000, $"{s.SizeBytes}");
        Assert.True(s.EntryCount < 10);
    }

    [Fact]
    public void A_damaged_partial_entry_is_a_miss()
    {
        var v = Store().View("grid", "s");
        v.PutPartial("texture", Tex, Codestream.Value[..5000], null);
        var file = Directory.GetFiles(_root, "*.nvc").Single();
        var b = File.ReadAllBytes(file);
        b[^5] ^= 0x40;
        File.WriteAllBytes(file, b);
        Assert.False(v.TryGetPartial("texture", Tex, out _, out _));
    }

    // ---- through SessionAssets and TextureSource ----

    [Fact]
    public async Task Session_assets_hand_back_a_partial_result_but_share_only_complete_files()
    {
        using var server = Server();
        var shared = new SharedAssetStore();
        using var assets = new SessionAssets(shared, _ => true, (_, _) => Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.Failed)), fetchLevel: Fetcher(server).Fetcher.FetchLevelAsync);
        var r4 = await assets.GetLevelAsync(Tex, 4, false, CancellationToken.None);
        Assert.True(r4.Partial);
        Assert.Equal(0, shared.Count);
        var r0 = await assets.GetLevelAsync(Tex, 0, false, CancellationToken.None);
        Assert.False(r0.Partial);
        Assert.Equal(1, shared.Count);
        using var denied = new SessionAssets(shared, _ => false, (_, _) => Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.Failed)), fetchLevel: Fetcher(server).Fetcher.FetchLevelAsync);
        Assert.Equal(AssetFetchStatus.Denied, (await denied.GetLevelAsync(Tex, 4, false, CancellationToken.None)).Status);   // authorisation first
    }

    [Fact]
    public async Task The_texture_source_decodes_a_level_from_its_prefix()
    {
        using var server = Server();
        var (f, _) = Fetcher(server);
        using var src = new TextureSource(new SessionAssets(new SharedAssetStore(), _ => true, (_, _) => Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.Failed)), fetchLevel: f.FetchLevelAsync));
        DecodedTexture? t = null;
        var sw = Stopwatch.StartNew();
        while (src.TryGetLevel(Tex, 4, out t) == MeshAvailability.Pending && sw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(10);
        Assert.NotNull(t);
        Assert.Equal(4, t!.Level);
        Assert.Equal(1, src.PrefixDecodes);
        Assert.Equal(0, src.MoreFetches);
        Assert.True(server.BodyBytes < Codestream.Value.Length);
    }

    [Fact]
    public async Task When_a_prefix_does_not_decode_the_texture_source_fetches_the_rest_once()
    {
        // a codestream whose main header (a 60,000-byte comment) is longer than level 4's estimate: the prefix is refused
        // (no complete main header), so the rest of the file is fetched once and the level decodes from it
        var cs = Codestream.Value;
        int sot = cs.AsSpan().IndexOf(new byte[] { 0xFF, 0x90 });
        var com = new byte[4 + 60_000];
        com[0] = 0xFF; com[1] = 0x64; com[2] = (byte)((60_002 >> 8) & 0xFF); com[3] = (byte)(60_002 & 0xFF);   // COM, Lcom = 2 + Rcom + data
        com[5] = 1;   // Rcom = 1 (text)
        for (int i = 6; i < com.Length; i++) com[i] = (byte)'x';
        byte[] big = [.. cs[..sot], .. com, .. cs[sot..]];
        Assert.True(sot + com.Length > TextureByteRange.BytesFor(1024, 1024, 4));
        Assert.True(LmvTexture.DecodeLevel(big, 4).Ok);
        using var server = new LoopbackTextureServer(id => id == Tex ? big : null);
        var (f, wholeCalls) = Fetcher(server);
        using var src = new TextureSource(new SessionAssets(new SharedAssetStore(), _ => true, (_, _) => Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.Failed)), fetchLevel: f.FetchLevelAsync));
        DecodedTexture? t = null;
        var sw = Stopwatch.StartNew();
        MeshAvailability a;
        while ((a = src.TryGetLevel(Tex, 4, out t)) == MeshAvailability.Pending && sw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(10);
        Assert.Equal(MeshAvailability.Ready, a);
        Assert.Equal(1, src.MoreFetches);
        Assert.Equal(0, src.PrefixDecodes);
        Assert.Equal(0, wholeCalls());                      // the rest came by range, not by the fallback
        Assert.Equal(big.Length, server.BodyBytes);         // and nothing twice
    }
}

/// <summary>The guard with truncated codestreams.</summary>
[Collection("TimingTests")]
public class TruncatedCodestreamTests
{
    private static double Mad(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average();

    [Fact]
    public void A_prefix_is_accepted_only_when_asked_for_and_marked_truncated()
    {
        var cs = PartialFetchTests.Codestream.Value;
        var prefix = cs[..5000];
        Assert.NotNull(J2kGuard.Validate(prefix, null, out _));   // refused as before without the option (negative control)
        Assert.Null(J2kGuard.Validate(prefix, null, out var h, allowTruncated: true));
        Assert.True(h!.Truncated);
        Assert.Null(J2kGuard.Validate(cs, null, out var whole, allowTruncated: true));
        Assert.False(whole!.Truncated);
        Assert.False(LmvTexture.DecodeLevel(prefix, 0, prefix: true).Ok);   // full resolution never comes from a prefix
    }

    [Fact]
    public void A_prefix_without_its_whole_main_header_is_refused()
    {
        var cs = PartialFetchTests.Codestream.Value;
        int sot = cs.AsSpan().IndexOf(new byte[] { 0xFF, 0x90 });
        foreach (int cut in new[] { 2, 10, 45, sot - 1, sot, sot + 6 })
            Assert.NotNull(J2kGuard.Validate(cs[..cut], null, out _, allowTruncated: true));
    }

    [Fact]
    public void Everything_refused_today_is_still_refused_except_a_missing_tail()
    {
        string[] tailReasons = ["tile-part length beyond the data", "SOT truncated"];
        foreach (var (name, bytes) in TextureFuzzTests.Corpus())
        {
            var today = J2kGuard.Validate(bytes, null, out _);
            Assert.NotNull(today);
            var prefix = J2kGuard.Validate(bytes, null, out _, allowTruncated: true);
            if (prefix is null)
            {
                Assert.Contains(today, tailReasons);   // accepted only because its tail is missing
                var sw = Stopwatch.StartNew();
                LmvTexture.DecodeLevel(bytes, 1, prefix: true);   // bounded by the header limits like any decode
                Assert.True(sw.ElapsedMilliseconds < 2000, $"{name}: {sw.ElapsedMilliseconds} ms");
            }
        }
    }

    [Fact]
    public void A_corrupted_header_is_refused_as_a_prefix_too()
    {
        var cs = PartialFetchTests.Codestream.Value[..5000];
        var bad = (byte[])cs.Clone();
        bad[3] = 0x50;                                      // SIZ marker damaged
        Assert.NotNull(J2kGuard.Validate(bad, null, out _, allowTruncated: true));
        var big = (byte[])cs.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(big.AsSpan(8), 65535);   // 65535 wide
        Assert.NotNull(J2kGuard.Validate(big, null, out _, allowTruncated: true));
    }

    /// <summary>Every level of every codestream decoded from the estimate's prefix matches the level from the whole file.</summary>
    private static (int Levels, List<string> Failures) CheckAll(IEnumerable<(string Name, byte[] Bytes)> files)
    {
        int levels = 0;
        var failures = new List<string>();
        foreach (var (name, cs) in files)
        {
            if (J2kGuard.Validate(cs, null, out var h) is not null) continue;
            int count = TextureLevels.Count(h!.Width, h.Height, h.DecompositionLevels);
            for (int level = 1; level < count; level++)
            {
                long n = Math.Min(cs.Length, TextureByteRange.BytesFor(h, level) ?? cs.Length);
                var full = LmvTexture.DecodeLevel(cs, level);
                if (!full.Ok) continue;
                levels++;
                var part = LmvTexture.DecodeLevel(cs[..(int)n], level, prefix: n < cs.Length);
                if (!part.Ok) { failures.Add($"{name} L{level}: {part.Refusal}"); continue; }
                double d = Mad(part.Texture!.Rgba, full.Texture!.Rgba);
                if (d > 8) failures.Add($"{name} L{level}: {d:F2} ({h.Layers} layers, {n} of {cs.Length} bytes)");
            }
        }
        return (levels, failures);
    }

    [Fact]
    public void Levels_from_a_prefix_match_the_whole_file_on_the_texture_zoo_and_our_own_codestream()
    {
        var dir = Path.Combine(TestPaths.RepoRoot, "fixtures", "textures");
        var files = Directory.GetFiles(dir, "*.j2c").Select(f => (Path.GetFileName(f), File.ReadAllBytes(f)))
            .Append(("own-1024-5-layers", PartialFetchTests.Codestream.Value));
        var (levels, failures) = CheckAll(files);
        Assert.True(levels >= 4, $"{levels} levels checked");
        Assert.Empty(failures);
        // also at a cut well inside the estimate: every level below full resolution still decodes (at lower quality)
        var cs = PartialFetchTests.Codestream.Value;
        Assert.True(LmvTexture.DecodeLevel(cs[..3000], 3, prefix: true).Ok);
    }

    /// <summary>Strict, with nothing excused: CoreJ2K 2.3.3 threw on two of these (<see cref="KnownPrefixDefects"/>), 2.4.1 reads them; fails on any
    /// level that does not match.</summary>
    [LocalFixturesFact("assets")]
    public void Levels_from_a_prefix_match_the_whole_file_on_the_cached_real_textures()
    {
        var files = Directory.GetFiles(LiveCaptures.PathOf("assets"), "*.j2k").OrderBy(f => f).Select(f => (Path.GetFileName(f), File.ReadAllBytes(f))).ToList();
        Assert.NotEmpty(files);
        var (levels, failures) = CheckAll(files);
        Assert.True(levels > 100, $"{levels} levels checked");
        Assert.True(failures.Count == 0, "not matching: " + string.Join("; ", failures));
    }
}
