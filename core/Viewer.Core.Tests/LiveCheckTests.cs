// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Imaging;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Residency;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>What the live app counts and logs so a login against a grid
/// can be judged: range answers by kind, fallbacks with their reasons, bytes fetched, budget changes, the environment
/// answer, the region's map image id, decode refusals by reason and the quality layers a codestream declares.</summary>
public sealed class LiveCheckTests : IDisposable
{
    private static readonly Guid Tex = new("11111111-2222-3333-4444-555555555555");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public void Dispose() => _http.Dispose();

    private PartialTextureFetcher Fetcher(LoopbackTextureServer server, Func<Guid, CancellationToken, Task<AssetFetchResult>>? whole = null) =>
        new((id, from, to, cap, ct) => RangeHttp.GetAsync(_http, server.UriFor(id), from, to, cap, ct),
            whole ?? ((_, _) => Task.FromResult(AssetFetchResult.Ok(PartialFetchTests.Codestream.Value))));

    private static LoopbackTextureServer Server(RangeServerMode mode = RangeServerMode.Honour) =>
        new(id => id == Tex ? PartialFetchTests.Codestream.Value : null, mode);

    [Fact]
    public async Task Range_answers_are_counted_by_kind_206_200_404_and_other()
    {
        using (var server = Server())
        {
            var f = Fetcher(server);
            await f.FetchLevelAsync(Tex, 4, false, CancellationToken.None);   // level 4 in one request: one 206
            await f.FetchLevelAsync(Tex, 3, false, CancellationToken.None);   // refined to level 3: a second 206
            await f.FetchLevelAsync(Guid.NewGuid(), 4, false, CancellationToken.None);   // 404
            Assert.Equal(2, f.Answers["206"]);
            Assert.Equal(1, f.Answers["404"]);
            Assert.False(f.Answers.ContainsKey("200"));
            Assert.False(f.Answers.ContainsKey("other"));
        }
        using (var server = Server(RangeServerMode.IgnoreRange))
        {
            var f = Fetcher(server);
            await f.FetchLevelAsync(Tex, 4, false, CancellationToken.None);
            Assert.Equal(1, f.Answers["200"]);
            Assert.False(f.Answers.ContainsKey("206"));
        }
    }

    [Theory]
    [InlineData(RangeServerMode.Always416, "answered 416")]
    [InlineData(RangeServerMode.WrongContentRange, "does not match the request")]
    [InlineData(RangeServerMode.NoContentRange, "without a usable Content-Range")]
    public async Task A_fallback_is_counted_with_its_reason(RangeServerMode mode, string reason)
    {
        using var server = Server(mode);
        var f = Fetcher(server);
        var r = await f.FetchLevelAsync(Tex, 4, false, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Ok, r.Status);
        Assert.Equal(1, f.Answers["other"]);
        var (why, count) = Assert.Single(f.FallbackReasons);
        Assert.Equal(1, count);
        Assert.StartsWith("first: ", why);
        Assert.Contains(reason, why);
        Assert.DoesNotContain("0-", why.Replace("first: ", ""), StringComparison.Ordinal);   // byte offsets are left out, so reasons group
    }

    [Fact]
    public async Task A_refinement_that_goes_wrong_is_a_refine_fallback()
    {
        using var server = Server();
        var f = Fetcher(server);
        await f.FetchLevelAsync(Tex, 4, false, CancellationToken.None);
        server.Mode = RangeServerMode.WrongContentRange;
        await f.FetchLevelAsync(Tex, 1, false, CancellationToken.None);
        var (why, _) = Assert.Single(f.FallbackReasons);
        Assert.StartsWith("refine: ", why);
    }

    [Fact]
    public void Budget_changes_are_kept_with_the_non_texture_figure_they_came_from()
    {
        const long MiB = TextureBudget.MiB;
        var b = new LiveTextureBudget(16384 * MiB);
        var changes = new List<BudgetChange>();
        b.Changed += changes.Add;
        b.Update(300 * MiB, TimeSpan.FromSeconds(1));
        b.Update(310 * MiB, TimeSpan.FromSeconds(3));   // inside the deadband: no change
        b.Update(600 * MiB, TimeSpan.FromSeconds(5));
        Assert.Equal(2, changes.Count);
        Assert.Equal(2, b.History.Count);
        Assert.Equal(300 * MiB, changes[0].NonTexture);
        Assert.Equal(TextureBudget.For(16384 * MiB, 300 * MiB), changes[0].Budget);
        Assert.Equal(TextureBudget.For(16384 * MiB, 600 * MiB), changes[1].Budget);
        Assert.Equal(changes[0].Budget, changes[1].Previous);
        Assert.Equal(TimeSpan.FromSeconds(5), changes[1].At);
    }

    [Fact]
    public void Negative_control_a_configured_budget_never_reports_a_change()
    {
        var b = new LiveTextureBudget(16384 * TextureBudget.MiB, 1000 * TextureBudget.MiB);
        int n = 0;
        b.Changed += _ => n++;
        b.Update(100 * TextureBudget.MiB, TimeSpan.FromSeconds(1));
        Assert.Equal(0, n);
        Assert.Empty(b.History);
    }

    private static (ViewerSession, FakeTransport, ListSink) InWorld(SimRef sim)
    {
        var t = new FakeTransport();
        var sink = new ListSink();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, t, new CoreLog(sink, "test"));
        Assert.True(s.LoginAsync(new LoginCredentials("A", "B", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        t.DriveToInWorld(sim);
        return (s, t, sink);
    }

    private static readonly SimRef Sim = new(1099511628032000UL, "Alpha");

    [Fact]
    public void The_environment_answer_is_logged_with_its_size_and_the_cap()
    {
        var (s, t, sink) = InWorld(Sim);
        t.Raise(new EnvironmentReceived(Sim, "<llsd><map /></llsd>", null));
        var e = Assert.Single(sink.Entries, x => x.Event == "environment.received");
        Assert.Contains(e.Fields, f => f.Key == "bytes" && f.Value == "20");
        Assert.Contains(e.Fields, f => f.Key == "cap_bytes" && f.Value == EnvironmentFetcher<object>.CapBytes.ToString());
        Assert.DoesNotContain(sink.Entries, x => x.Event == "environment.missing");
        s.LogoutAsync(CancellationToken.None).Wait();
    }

    [Fact]
    public void A_missing_environment_is_logged_with_its_reason()
    {
        var (s, t, sink) = InWorld(Sim);
        t.Raise(new EnvironmentReceived(Sim, null, "no ExtEnvironment capability"));
        var e = Assert.Single(sink.Entries, x => x.Event == "environment.missing");
        Assert.Contains(e.Fields, f => f.Key == "why" && f.Value == "no ExtEnvironment capability");
        s.LogoutAsync(CancellationToken.None).Wait();
    }

    [Fact]
    public void The_map_image_id_of_our_cell_is_kept_and_another_cells_is_not()
    {
        var map = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var (s, t, _) = InWorld(Sim);
        t.Raise(new MapBlockSize(Sim.GridX + 1, Sim.GridY, 256, 256, "Neighbour", Guid.NewGuid()));
        Assert.Null(s.Region!.MapImageId);
        t.Raise(new MapBlockSize(Sim.GridX, Sim.GridY, 256, 256, "Alpha", map));
        Assert.Equal(map, s.Region!.MapImageId);
        s.LogoutAsync(CancellationToken.None).Wait();
    }

    [Fact]
    public void Layer_counts_are_read_from_the_main_header_even_from_a_prefix()
    {
        var five = PartialFetchTests.Codestream.Value;
        var many = PartialFetchTests.ManyLayers.Value;
        Assert.Equal(5, TextureLayers.Read(five).Header!.Layers);
        Assert.Equal(27, TextureLayers.Read(many).Header!.Layers);
        Assert.Equal(5, TextureLayers.Read(five.AsSpan(0, TextureByteRange.FirstChunk)).Header!.Layers);   // the first chunk alone
        var junk = TextureLayers.Read(new byte[] { 1, 2, 3, 4 });
        Assert.Null(junk.Header);
        Assert.NotNull(junk.Refusal);
    }

    /// <summary>The same codestream with one more component entry in its SIZ (5 components, as the grid's bakes declare).</summary>
    private static byte[] FiveComponents()
    {
        var cs = PartialFetchTests.Codestream.Value;
        int lsiz = (cs[4] << 8) | cs[5], csiz = (cs[40] << 8) | cs[41];
        var extra = new byte[3 * (5 - csiz)];
        for (int i = 0; i < extra.Length; i += 3) { extra[i] = 7; extra[i + 1] = 1; extra[i + 2] = 1; }
        byte[] d = [.. cs[..(4 + lsiz)], .. extra, .. cs[(4 + lsiz)..]];
        int nl = lsiz + extra.Length;
        d[4] = (byte)(nl >> 8); d[5] = (byte)nl; d[40] = 0; d[41] = 5;
        return d;
    }

    [Fact]
    public void A_measurement_reads_the_layers_of_a_five_component_codestream()
    {
        var five = FiveComponents();
        // the viewer's guard takes 5 components now (a bake's morph mask is the fifth)
        Assert.Equal(5, TextureLayers.Read(five).Header!.Components);
        var m = TextureLayers.Measure(five);
        Assert.Equal(5, m.Header!.Layers);
        Assert.Equal(5, m.Header.Components);
        Assert.Equal("j2c", m.Container);
    }

    [Fact]
    public void A_measurement_finds_the_codestream_inside_a_jp2_file()
    {
        var cs = PartialFetchTests.Codestream.Value;
        byte[] sig = [0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];
        byte[] ftyp = [0, 0, 0, 20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'j', (byte)'p', (byte)'2', (byte)' ', 0, 0, 0, 0, (byte)'j', (byte)'p', (byte)'2', (byte)' '];
        int len = 8 + cs.Length;
        byte[] jp2c = [(byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len, (byte)'j', (byte)'p', (byte)'2', (byte)'c'];
        byte[] file = [.. sig, .. ftyp, .. jp2c, .. cs];
        Assert.Contains("no SOC", TextureLayers.Read(file).Refusal);
        var m = TextureLayers.Measure(file.AsSpan(0, 2000));
        Assert.Equal(5, m.Header!.Layers);
        Assert.Equal("jp2", m.Container);
        var junk = TextureLayers.Measure(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 });
        Assert.Null(junk.Header);
        Assert.Equal("89504E4701020304", junk.Magic);
    }

    [Fact]
    public void Layer_histograms_group_counts_and_flag_those_over_the_threshold()
    {
        var h = TextureLayers.Histogram(new int?[] { 5, 5, 1, 27, null });
        Assert.Equal(2, h[5]);
        Assert.Equal(1, h[1]);
        Assert.Equal(1, h[27]);
        Assert.Equal(1, h[-1]);   // unreadable
        Assert.Equal(1, TextureLayers.OverThreshold(new int?[] { 5, 8, 9, null }));
    }

    [Fact]
    public async Task Whole_fetches_are_counted_with_their_bytes()
    {
        var counter = new FetchCounter();
        var fetch = counter.Wrap((_, _) => Task.FromResult(AssetFetchResult.Ok(new byte[1000])));
        await fetch(Guid.NewGuid(), CancellationToken.None);
        await counter.Wrap((_, _) => Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.NotFound)))(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(2, counter.Requests);
        Assert.Equal(1000, counter.Bytes);
        Assert.Equal(1, counter.Ok);
    }

    [Fact]
    public async Task Decode_refusals_are_counted_by_reason_and_the_requested_textures_are_listed()
    {
        using var src = new TextureSource(new SessionAssets(new SharedAssetStore(), _ => true,
            (_, _) => Task.FromResult(AssetFetchResult.Ok(new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 0, 0 }))));
        var sw = Stopwatch.StartNew();
        while (src.TryGetLevel(Tex, 2, out _) == MeshAvailability.Pending && sw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(10);
        var (why, count) = Assert.Single(src.Refusals());
        Assert.Equal(1, count);
        Assert.False(string.IsNullOrEmpty(why));
        Assert.Contains(Tex, src.Requested());
    }
}
