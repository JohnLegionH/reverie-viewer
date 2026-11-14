// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Diagnostics;
using System.Security.Cryptography;
using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Imaging;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A level whose partial codestream does not decode: CoreJ2K 2.3.3 threw IndexOutOfRangeException when the bytes ended
/// inside a packet header (about 1 % of all cut points of a layer-progressive codestream, measured); 2.4.1 no longer does
/// (CoreJ2K_2_4_1_decodes_the_cuts_that_made_2_3_3_throw below), so the tests that need a failing prefix inject the fault at
/// the cut instead of waiting for the decoder to produce it. The bytes the estimate asks for can still be undecodable
/// for some codestream. The texture source then fetches more (the next level's
/// range, up to the whole file) and decodes again, logs it once per texture and counts it by reason.
/// </summary>
public sealed class PrefixDecodeFailureTests : IDisposable
{
    private static readonly Guid Tex = new("7c1e5a90-3b2d-4f6e-9a81-0d4c2b7e6f13");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public void Dispose() => _http.Dispose();

    private PartialTextureFetcher Fetcher(LoopbackTextureServer server, Func<int, long?>? firstRequest = null) =>
        new((id, from, to, cap, ct) => RangeHttp.GetAsync(_http, server.UriFor(id), from, to, cap, ct),
            (_, _) => Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.Failed)), firstRequest: firstRequest);

    private static TextureSource Source(PartialTextureFetcher f, ListSink sink) =>
        new(new SessionAssets(new SharedAssetStore(), _ => true, (_, _) => Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.Failed)), fetchLevel: f.FetchLevelAsync))
        { Log = new CoreLog(sink, "test") };

    private static async Task<(MeshAvailability, DecodedTexture?)> Wait(TextureSource src, Guid id, int level)
    {
        DecodedTexture? t = null;
        MeshAvailability a;
        var sw = Stopwatch.StartNew();
        while ((a = src.TryGetLevel(id, level, out t)) == MeshAvailability.Pending && sw.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(10);
        return (a, t);
    }

    /// <summary>The cuts, from <paramref name="from"/> on, where CoreJ2K's reduced decode of our own codestream throws (the
    /// bytes end inside a packet header).</summary>
    private static List<int> ThrowingCuts(byte[] cs, int from, int count, int level)
    {
        var cuts = new List<int>();
        for (int n = from; n < from + count && n < cs.Length; n++)
            if (LmvTexture.DecodeLevel(cs[..n], level, prefix: true).Refusal == "decoder failed: IndexOutOfRangeException")
                cuts.Add(n);
        return cuts;
    }

    /// <summary>The defect that made the prefix fallback necessary (2.3.3 threw on about 1 % of the cuts) did not show in 2.4.1 on the
    /// 3000 cuts sampled here, starting at level 5's estimate, where this test used to look for the first throwing cut. A sample, not a proof
    /// for every codestream: the fallback stays, and the tests above exercise it with an injected fault.</summary>
    [Fact]
    public void CoreJ2K_2_4_1_decodes_the_cuts_that_made_2_3_3_throw()
    {
        var cs = PartialFetchTests.Codestream.Value;
        int five = (int)TextureByteRange.BytesFor(1024, 1024, 5)!.Value;
        Assert.Empty(ThrowingCuts(cs, five, 3000, 5));
    }

    private static int Entries(ListSink sink) => sink.Entries.Count(e => e.Event == "texture.prefix.undecodable");

    [Fact]
    public async Task A_level_whose_prefix_makes_the_decoder_throw_is_decoded_from_the_next_levels_range()
    {
        // a decoder fault on the first request's bytes, on our own codestream (injected: CoreJ2K 2.4.1 does not produce one)
        var cs = PartialFetchTests.Codestream.Value;
        int five = (int)TextureByteRange.BytesFor(1024, 1024, 5)!.Value, four = (int)TextureByteRange.BytesFor(1024, 1024, 4)!.Value;
        int cut = five;
        Assert.True(LmvTexture.DecodeLevel(cs[..four], 5, prefix: true).Ok, "precondition: level 4's range decodes");
        using var server = new LoopbackTextureServer(id => id == Tex ? cs : null);
        var sink = new ListSink();
        using var src = Source(Fetcher(server, l => l == 5 ? cut : TextureByteRange.FirstRequest(l)), sink);
        src.PrefixDecodeFault = n => n == cut ? "decoder failed: IndexOutOfRangeException" : null;

        var (a, t) = await Wait(src, Tex, 5);

        Assert.Equal(MeshAvailability.Ready, a);
        Assert.Equal(5, t!.Level);
        Assert.Equal(1, src.MoreFetches);
        Assert.Equal(four, server.BodyBytes);              // the next level's range, not the whole file, and nothing twice
        Assert.Equal(1, src.PrefixFailures()["decoder failed: IndexOutOfRangeException"]);
        Assert.Equal(1, Entries(sink));
        Assert.Null(src.WhyUnavailable(Tex, 5));
    }

    [Fact]
    public async Task When_no_prefix_decodes_the_level_comes_from_the_whole_file_and_the_texture_is_logged_once()
    {
        var cs = PartialFetchTests.Codestream.Value;
        using var server = new LoopbackTextureServer(id => id == Tex ? cs : null);
        var sink = new ListSink();
        using var src = Source(Fetcher(server), sink);
        src.PrefixDecodeFault = _ => "test fault";          // every decode of a partial codestream fails

        var (coarsest, c) = await Wait(src, Tex, int.MaxValue);   // the coarsest level: the steps stay bounded
        var (four, f) = await Wait(src, Tex, 4);

        Assert.Equal(MeshAvailability.Ready, coarsest);
        Assert.Equal(MeshAvailability.Ready, four);
        Assert.Equal(5, c!.Level);
        Assert.Equal(4, f!.Level);
        Assert.Equal(cs.Length, server.BodyBytes);          // up to the whole file, nothing twice
        Assert.Equal(1, src.MoreFetches);                   // the second level found the whole file already
        Assert.Equal(1, src.PrefixFailures()["test fault"]);
        Assert.Equal(1, Entries(sink));                     // once per texture
        Assert.Contains("outcome=image", sink.Text);
    }

    [Fact]
    public async Task A_fault_only_below_a_size_stops_at_the_first_range_that_decodes()
    {
        var cs = PartialFetchTests.Codestream.Value;
        int three = (int)TextureByteRange.BytesFor(1024, 1024, 3)!.Value;
        Assert.True(three < cs.Length, "precondition: level 3's range is less than the file");
        using var server = new LoopbackTextureServer(id => id == Tex ? cs : null);
        var sink = new ListSink();
        using var src = Source(Fetcher(server), sink);
        src.PrefixDecodeFault = n => n < three ? "test fault" : null;

        var (a, t) = await Wait(src, Tex, 5);

        Assert.Equal(MeshAvailability.Ready, a);
        Assert.Equal(5, t!.Level);
        Assert.Equal(three, server.BodyBytes);              // levels 4's and 3's ranges, then it decodes
        Assert.Equal(1, src.PrefixFailures()["test fault"]);
        Assert.Equal(1, Entries(sink));
    }

    [Fact]
    public async Task Without_a_fault_nothing_more_is_fetched_and_nothing_is_logged()
    {
        // negative control for the three above: the same codestream, the same requests, no fault
        var cs = PartialFetchTests.Codestream.Value;
        using var server = new LoopbackTextureServer(id => id == Tex ? cs : null);
        var sink = new ListSink();
        using var src = Source(Fetcher(server), sink);

        var (a, _) = await Wait(src, Tex, 5);

        Assert.Equal(MeshAvailability.Ready, a);
        Assert.Equal(0, src.MoreFetches);
        Assert.Empty(src.PrefixFailures());
        Assert.Equal(0, Entries(sink));
        Assert.Equal(TextureByteRange.FirstRequest(5), server.BodyBytes);
    }

    /// <summary>The cached textures that 2.3.3 could not read at their estimate: 2.4.1 reads them, so the level comes from the
    /// first request alone.</summary>
    [LocalFixturesFact("assets")]
    public async Task The_cached_textures_that_2_3_3_could_not_read_at_their_estimate_decode_from_it_in_2_4_1()
    {
        var known = KnownPrefixDefects.Present();
        Assert.NotEmpty(known);
        foreach (var (file, bytes, level) in known)
        {
            Assert.True(LmvTexture.DecodeLevel(bytes[..(int)TextureByteRange.BytesFor(512, 512, level)!.Value], level, prefix: true).Ok, $"{file}: estimate");
            using var server = new LoopbackTextureServer(id => id == Tex ? bytes : null);
            var sink = new ListSink();
            using var src = Source(Fetcher(server), sink);
            var (a, t) = await Wait(src, Tex, level);
            Assert.True(a == MeshAvailability.Ready, $"{file}: {a}, {src.WhyUnavailable(Tex, level)}");
            Assert.Equal(level, t!.Level);
            Assert.Empty(src.PrefixFailures());
            Assert.Equal(0, Entries(sink));
        }
    }

    /// <summary>The same textures with the fault injected at the estimate: the level still arrives, from more bytes.</summary>
    [LocalFixturesFact("assets")]
    public async Task The_cached_textures_whose_estimate_fails_to_decode_still_get_their_level()
    {
        var known = KnownPrefixDefects.Present();
        Assert.NotEmpty(known);
        foreach (var (file, bytes, level) in known)
        {
            int estimate = (int)TextureByteRange.BytesFor(512, 512, level)!.Value;
            using var server = new LoopbackTextureServer(id => id == Tex ? bytes : null);
            var sink = new ListSink();
            using var src = Source(Fetcher(server), sink);
            src.PrefixDecodeFault = n => n == estimate ? "decoder failed: IndexOutOfRangeException" : null;
            var (a, t) = await Wait(src, Tex, level);
            Assert.True(a == MeshAvailability.Ready, $"{file}: {a}, {src.WhyUnavailable(Tex, level)}");
            Assert.Equal(level, t!.Level);
            Assert.Equal(1, src.PrefixFailures()["decoder failed: IndexOutOfRangeException"]);
            Assert.Equal(1, Entries(sink));
        }
    }
}

/// <summary>
/// Locally cached real textures where CoreJ2K 2.3.3 throws on the bytes the estimate asks for (the cut lands inside a
/// packet header), by the SHA-256 of the file and the level. Grid content stays local: only the fingerprint is here.
/// CoreJ2K 2.4.1 reads them (the first cached-texture test); the second one uses them as real inputs with the fault injected.
/// </summary>
internal static class KnownPrefixDefects
{
    /// <summary>Two 512x512 three-component maps, 5 quality layers, layer-progressive, 9/7: level 4.</summary>
    internal static readonly IReadOnlyDictionary<string, int> Levels = new Dictionary<string, int>
    {
        ["5ccaa65953cbdd0d6b573fd696b300bab1462134f63f43c62f885877623b8a4b"] = 4,
        ["4fa18598869beb4d5175655cd248607556ead8cbf8ef03dc7a74e5e576361a60"] = 4,
    };

    internal static string Fingerprint(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The listed textures present in the local cache: file name, bytes, level.</summary>
    internal static List<(string File, byte[] Bytes, int Level)> Present() =>
        Directory.GetFiles(LiveCaptures.PathOf("assets"), "*.j2k").OrderBy(f => f)
            .Select(f => (File: Path.GetFileName(f), Bytes: File.ReadAllBytes(f)))
            .Where(x => Levels.ContainsKey(Fingerprint(x.Bytes)))
            .Select(x => (x.File, x.Bytes, Levels[Fingerprint(x.Bytes)])).ToList();
}
