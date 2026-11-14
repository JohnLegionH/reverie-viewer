// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Texture requests go through the transport's own bounded texture client, not
/// LibreMetaverse's caps client. That client queues every request to an unregistered URL (ours carry ?texture_id=) in its
/// Default rate-limit bucket (20 burst, 10 per second, queue 30; CapsRateLimiter, LibreMetaverse 3.1.6): on the grid a
/// texture waited 1.9 s (p50) for its response headers against 105 ms straight from the capability. The texture
/// client keeps at most 8 requests in flight (the official viewer's texture-fetch concurrency per host), applies the
/// outbound policy, and ends every request: a whole-file fetch is a plain bounded GET (no Range) with a deadline, a 404 is
/// NotFound at once, and a range request that never answers fails at its deadline.
/// The class runs in its own collection with parallelisation off, alone, after the parallel ones.
/// </summary>
[Collection(nameof(TextureHttpTests))]
public sealed class TextureHttpTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly Guid Tex = new("11111111-2222-3333-4444-555555555555");

    private static LoopbackTextureServer Server(RangeServerMode mode = RangeServerMode.Honour) =>
        new(id => id.ToString().StartsWith("11111111", StringComparison.Ordinal) ? PartialFetchTests.Codestream.Value : null, mode);

    private static Guid Id(int i) => new($"11111111-0000-0000-0000-{i:D12}");

    private static LmvTransport Transport(LoopbackTextureServer server, TimeSpan? deadline = null)
    {
        var t = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "tex"));
        t.TextureCapabilityOverride = () => server.Base;
        if (deadline is { } d) t.TextureDeadline = d;
        return t;
    }

    /// <summary>Two things, neither against a wall-clock limit that the suite's load can break (it did,
    /// three times:). (1) At most 8 in flight: from the server's own start and end of every request, never
    /// more than 8 overlap, and it does reach 8 (the 9th starts only after one of the first 8 has ended). (2) Not rate-limited:
    /// the burst takes about ceil(48/8) = 6 answer times, each answer time as the server measured it (the median of its own
    /// request durations, which the same load stretches), and at most 3 times that (a client-side gate of 1 takes 48 answer
    /// times; LibreMetaverse's Default bucket, 20 at once and then 10 per second, about 28 at 100 ms per answer). The test runs
    /// in its own collection, alone, so the rest of the suite does not share the machine with it while it counts.</summary>
    [Fact]
    public async Task A_burst_of_texture_range_requests_is_not_rate_limited_and_keeps_to_eight_in_flight()
    {
        const int Burst = 48, Limit = 8;   // the official viewer's texture-fetch concurrency per host
        using var server = Server();
        server.ResponseDelay = TimeSpan.FromMilliseconds(100);
        server.RecordTimes = true;
        using var t = Transport(server);
        var answers = await Task.WhenAll(Enumerable.Range(0, Burst).Select(i => t.GetTextureRangeAsync(Id(i), 0, 599, 1 << 20, CancellationToken.None)));
        Assert.All(answers, a => Assert.Equal(RangeAnswerKind.Part, a.Kind));
        var times = server.RequestTimes;
        Assert.Equal(Burst, times.Count);
        var b = BurstShape.Of(times);
        output.WriteLine($"max in flight {b.MaxOverlap}; span {b.SpanAnswers:F2} answer times (median answer {b.MedianAnswerMs:F1} ms, span {b.SpanMs:F0} ms)");
        Assert.True(b.MaxOverlap <= Limit, $"{b.MaxOverlap} requests in flight at once at the server (at most {Limit})");
        Assert.True(b.MaxOverlap == Limit, $"never {Limit} in flight at once at the server (at most {b.MaxOverlap})");
        int waves = (Burst + Limit - 1) / Limit;
        Assert.True(b.SpanAnswers <= 3.0 * waves, $"the burst took {b.SpanAnswers:F1} answer times ({b.SpanMs:F0} ms at {b.MedianAnswerMs:F1} ms each); {waves} waves at most {Limit} in flight take about {waves}: held back by something other than the in-flight limit");
        Assert.Equal(Limit, TextureHttp.MaxInFlight);
    }

    /// <summary>A burst's shape from the server's own request times (Stopwatch ticks): the most requests overlapping
    /// at once (an end at the same tick as a start frees its slot first), the median request duration, and the whole span
    /// from the first start to the last end, also in units of that median.</summary>
    internal readonly record struct BurstShape(int MaxOverlap, double MedianAnswerMs, double SpanMs)
    {
        public double SpanAnswers => SpanMs / MedianAnswerMs;

        public static BurstShape Of(IReadOnlyList<(long Start, long End)> times)
        {
            var events = times.SelectMany(x => new[] { (At: x.Start, Delta: 1), (At: x.End, Delta: -1) }).OrderBy(e => e.At).ThenBy(e => e.Delta).ToList();
            int now = 0, max = 0;
            foreach (var e in events) max = Math.Max(max, now += e.Delta);
            var durations = times.Select(x => (x.End - x.Start) * 1000.0 / Stopwatch.Frequency).OrderBy(d => d).ToList();
            double median = durations[durations.Count / 2];
            double span = (times.Max(x => x.End) - times.Min(x => x.Start)) * 1000.0 / Stopwatch.Frequency;
            return new BurstShape(max, median, span);
        }
    }

    [Fact]
    public void The_burst_shape_counts_overlap_and_span_in_answer_times()
    {
        long ms = Stopwatch.Frequency / 1000;
        // two waves of 2, 100 ms each, the second starting as the first ends: 2 at once, 2 answer times
        var two = BurstShape.Of([(0, 100 * ms), (0, 100 * ms), (100 * ms, 200 * ms), (100 * ms, 200 * ms)]);
        Assert.Equal(2, two.MaxOverlap);
        Assert.Equal(2.0, two.SpanAnswers, 3);
        // one at a time, spaced by a limiter: never more than 1, 4 answers take 7 answer times
        var gated = BurstShape.Of([(0, 100 * ms), (200 * ms, 300 * ms), (400 * ms, 500 * ms), (600 * ms, 700 * ms)]);
        Assert.Equal(1, gated.MaxOverlap);
        Assert.Equal(7.0, gated.SpanAnswers, 3);
        // a third starting before either of two ends: 3 at once
        Assert.Equal(3, BurstShape.Of([(0, 100 * ms), (10 * ms, 110 * ms), (20 * ms, 120 * ms)]).MaxOverlap);
    }

    /// <summary>The server's in-flight count must never see the client's next request before the
    /// answer that freed its slot has ended. Rounds of 12 requests (8, then 4 as slots free; 10 ms per answer: few connections, so
    /// that 300 rounds stay far inside the machine's ephemeral ports, whose closed connections linger); NV_N99_LOOPS rounds
    /// (default 5; the proof ran 300), and with NV_N99_LOAD=1 every logical core also spins meanwhile. Under load the
    /// server's continuation after its last write can run late; the server's LingerAfterAnswer makes that happen on
    /// every request (NV_N99_LINGER_MS, default 5 ms), so a count that ends after the answer is written sees the client's
    /// next request every time.</summary>
    [Fact]
    public async Task Bursts_never_show_more_than_eight_in_flight_at_the_server()
    {
        int loops = int.TryParse(Environment.GetEnvironmentVariable("NV_N99_LOOPS"), out int n) && n > 0 ? n : 5;
        int linger = int.TryParse(Environment.GetEnvironmentVariable("NV_N99_LINGER_MS"), out int l) && l >= 0 ? l : 5;
        using var stop = new CancellationTokenSource();
        var load = Environment.GetEnvironmentVariable("NV_N99_LOAD") == "1"
            ? Enumerable.Range(0, Environment.ProcessorCount).Select(_ => { var th = new Thread(() => { while (!stop.IsCancellationRequested) Thread.SpinWait(1000); }) { IsBackground = true }; th.Start(); return th; }).ToList()
            : [];
        int over = 0, worst = 0, failed = 0;
        string? firstFailure = null;
        try
        {
            for (int i = 0; i < loops; i++)
            {
                using var server = Server();
                server.ResponseDelay = TimeSpan.FromMilliseconds(10);
                server.LingerAfterAnswer = TimeSpan.FromMilliseconds(linger);
                using var t = Transport(server);
                var answers = await Task.WhenAll(Enumerable.Range(0, 12).Select(k => t.GetTextureRangeAsync(Id(k), 0, 599, 1 << 20, CancellationToken.None)));
                failed += answers.Count(a => a.Kind != RangeAnswerKind.Part);
                if (failed > 0 && firstFailure is null) firstFailure = $"round {i}: {answers.First(a => a.Kind != RangeAnswerKind.Part).Why}";
                if (server.MaxConcurrent > TextureHttp.MaxInFlight) over++;
                worst = Math.Max(worst, server.MaxConcurrent);
            }
        }
        finally
        {
            stop.Cancel();
            foreach (var th in load) th.Join();
        }
        output.WriteLine($"{loops} bursts, linger {linger} ms{(load.Count > 0 ? $" under load ({load.Count} spinning threads)" : "")}: {over} showed more than {TextureHttp.MaxInFlight} in flight at the server (most {worst}); {failed} requests failed{(firstFailure is null ? "" : $" (first: {firstFailure})")}");
        Assert.Equal(0, failed);
        Assert.Equal(0, over);
    }

    [Fact]
    public async Task A_whole_file_fetch_is_a_plain_get_without_a_range()
    {
        using var server = Server();
        using var t = Transport(server);
        var r = await t.FetchTextureAsync(Tex, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Ok, r.Status);
        Assert.Equal(PartialFetchTests.Codestream.Value.Length, r.Bytes.Length);
        Assert.Equal(1, server.PlainRequests);
        Assert.Equal(0, server.RangeRequests);
    }

    [Fact]
    public async Task A_404_ends_at_once_as_NotFound()
    {
        using var server = Server();
        using var t = Transport(server, deadline: TimeSpan.FromSeconds(30));
        var sw = Stopwatch.StartNew();
        var r = await t.FetchTextureAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(AssetFetchStatus.NotFound, r.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task A_whole_file_fetch_from_a_server_that_never_answers_ends_at_the_deadline()
    {
        using var server = Server(RangeServerMode.NoAnswer);
        using var t = Transport(server, deadline: TimeSpan.FromMilliseconds(400));
        var sw = Stopwatch.StartNew();
        var r = await t.FetchTextureAsync(Tex, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Failed, r.Status);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(350), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_range_request_that_never_answers_fails_at_the_deadline()
    {
        using var server = Server(RangeServerMode.NoAnswer);
        using var t = Transport(server, deadline: TimeSpan.FromMilliseconds(400));
        var a = await t.GetTextureRangeAsync(Tex, 0, 599, 1 << 20, CancellationToken.None);
        Assert.Equal(RangeAnswerKind.Failed, a.Kind);
        Assert.Contains("deadline", a.Why);
    }

    [Fact]
    public async Task The_partial_paths_fallback_uses_the_same_bounded_whole_file_fetch()
    {
        using var server = Server(RangeServerMode.Always416);
        using var t = Transport(server);
        var f = new PartialTextureFetcher(t.GetTextureRangeAsync, t.FetchTextureAsync);
        var r = await f.FetchLevelAsync(Tex, 4, false, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Ok, r.Status);
        Assert.False(r.Partial);
        Assert.Equal(1, f.Stats.WholeFallbacks);
        Assert.Equal(1, server.PlainRequests);   // the fallback: one plain GET on the capability, not the library's fetch
    }

    [Fact]
    public async Task Without_a_capability_the_library_fetch_is_bounded_by_the_deadline_too()
    {
        var never = new TaskCompletionSource<AssetFetchResult>();
        var sw = Stopwatch.StartNew();
        var r = await TextureHttp.WithDeadline(_ => never.Task, TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Failed, r.Status);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(AssetFetchStatus.Cancelled, (await TextureHttp.WithDeadline(_ => never.Task, TimeSpan.FromSeconds(30), cts.Token)).Status);
    }

    [Fact]
    public void The_whole_file_deadline_is_sixty_seconds()
    {
        // The official viewer's texture HTTP transfer timeout: 30 s connect, doubled for its pipelined texture class
        // (llcorehttp, behaviour only); LibreMetaverse's caps timeout is also 60 s. See.
        Assert.Equal(TimeSpan.FromSeconds(60), TextureHttp.Deadline);
    }
}

/// <summary>TextureHttpTests run alone (no other test class in parallel with them).</summary>
[CollectionDefinition(nameof(TextureHttpTests), DisableParallelization = true)]
public sealed class TextureHttpTestsCollection;
