// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Viewer.Core.Imaging;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Where a texture's seconds go on the way to the screen. Per texture, the first time each
/// stage happens (asked, fetch started, fetched, decode queued / started / ended, uploaded); per HTTP request, the time to
/// its response headers and to the end of its body, with bytes and status; requests in flight over time.</summary>
public sealed class TextureTimelineTests : IDisposable
{
    private static readonly Guid A = new("aaaaaaaa-0000-0000-0000-000000000001"), B = new("bbbbbbbb-0000-0000-0000-000000000002");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private long _now;
    private TextureTimeline Timeline() => new(() => _now, ticksPerSecond: 1000);   // one tick = 1 ms
    private void At(long ms) => _now = ms;

    public void Dispose() => _http.Dispose();

    [Fact]
    public void Stage_durations_are_measured_from_the_first_time_each_stage_happens()
    {
        var t = Timeline();
        At(-40); t.Mark(A, TextureStage.Wanted);
        At(0); t.Mark(A, TextureStage.Asked);
        At(10); t.Mark(A, TextureStage.FetchStarted);
        At(20); t.Mark(A, TextureStage.Asked);   // a second ask changes nothing
        At(110); t.Mark(A, TextureStage.Fetched);
        At(111); t.Mark(A, TextureStage.DecodeQueued);
        At(115); t.Mark(A, TextureStage.DecodeStarted);
        At(135); t.Mark(A, TextureStage.Decoded);
        At(185); t.Mark(A, TextureStage.Uploaded);
        At(1000); t.Mark(B, TextureStage.Asked);
        At(1300); t.Mark(B, TextureStage.FetchStarted);
        At(2300); t.Mark(B, TextureStage.Fetched);
        At(2300); t.Mark(B, TextureStage.DecodeQueued);
        At(2310); t.Mark(B, TextureStage.DecodeStarted);
        At(2320); t.Mark(B, TextureStage.Decoded);

        var s = t.Summary();
        Assert.Equal(2, s.Textures);
        Assert.Equal(new Dist(1, 40, 40, 40), s.Stages["grant-wait"]);         // wanted -> asked (B never waited for a grant)
        Assert.Equal(new Dist(2, 10, 300, 300), s.Stages["queue"]);            // asked -> fetch started
        Assert.Equal(new Dist(2, 100, 1000, 1000), s.Stages["fetch"]);         // fetch started -> fetched
        Assert.Equal(new Dist(2, 4, 10, 10), s.Stages["decode-queue"]);        // decode queued -> started
        Assert.Equal(new Dist(2, 10, 20, 20), s.Stages["decode"]);             // started -> ended
        Assert.Equal(new Dist(1, 50, 50, 50), s.Stages["upload-wait"]);        // decoded -> uploaded (B not uploaded)
        Assert.Equal(new Dist(2, 135, 1320, 1320), s.Stages["first-image"]);   // asked -> decoded
        Assert.Equal(new Dist(1, 185, 185, 185), s.Stages["to-screen"]);       // asked -> uploaded
    }

    /// <summary>Each texture's stages timed from a moment on the timeline's own clock
    /// (the path's start), not from its own request; stages not reached are null; a stage before the moment is negative.</summary>
    [Fact]
    public void Stage_times_since_a_moment_are_measured_on_the_timeline_clock()
    {
        var t = Timeline();
        At(-30); t.Mark(A, TextureStage.Asked);
        At(500);
        long start = t.Now;
        At(600); t.Mark(A, TextureStage.Decoded);
        At(650); t.Mark(A, TextureStage.Uploaded);
        At(700); t.Mark(B, TextureStage.Asked);
        At(900); t.Mark(B, TextureStage.Decoded);

        Assert.Equal(500, start);
        var since = t.Since(start).ToDictionary(x => x.Id);
        Assert.Equal(2, since.Count);
        Assert.Equal((A, -530.0, 100.0, 150.0), (since[A].Id, since[A].AskedMs, since[A].DecodedMs, since[A].UploadedMs));
        Assert.Equal((B, 200.0, 400.0, (double?)null), (since[B].Id, since[B].AskedMs, since[B].DecodedMs, since[B].UploadedMs));
    }

    [Fact]
    public void Http_requests_are_timed_to_headers_and_to_the_end_of_the_body_with_bytes_status_and_in_flight()
    {
        var t = Timeline();
        At(0);
        var r1 = t.BeginRequest(A, "range");
        At(5);
        var r2 = t.BeginRequest(B, "range");
        Assert.Equal(2, t.InFlight);
        At(40); r1.Headers(206);
        At(60); r1.Done(600);
        At(70); r2.Headers(404);
        At(70); r2.Done(0);
        r2.Done(0);   // twice: counted once
        Assert.Equal(0, t.InFlight);

        var s = t.Summary();
        Assert.Equal(2, s.Requests);
        Assert.Equal(600, s.Bytes);
        Assert.Equal(2, s.MaxInFlight);
        Assert.Equal(1, s.Statuses["206"]);
        Assert.Equal(1, s.Statuses["404"]);
        Assert.Equal(new Dist(2, 40, 65, 65), s.Http["headers"]);
        Assert.Equal(new Dist(2, 0, 20, 20), s.Http["body"]);
        Assert.Equal(new Dist(2, 60, 65, 65), s.Http["total"]);
    }

    [Fact]
    public void A_request_that_never_got_headers_is_counted_as_failed_with_its_total_time()
    {
        var t = Timeline();
        At(0);
        var r = t.BeginRequest(A, "whole");
        At(900); r.Done(0);
        var s = t.Summary();
        Assert.Equal(1, s.Statuses["none"]);
        Assert.Equal(new Dist(1, 900, 900, 900), s.Http["total"]);
        Assert.Equal(0, s.Http["headers"].Count);
    }

    [Fact]
    public void Requests_before_the_first_image_are_counted_per_texture()
    {
        var t = Timeline();
        At(0); t.Mark(A, TextureStage.Asked);
        t.BeginRequest(A, "range").Done(600);
        t.BeginRequest(A, "range").Done(3000);
        At(50); t.Mark(A, TextureStage.Decoded);
        At(60); t.BeginRequest(A, "range").Done(9000);   // a later refinement: after the first image
        t.Mark(B, TextureStage.Asked);
        t.BeginRequest(B, "range").Done(600);
        t.Mark(B, TextureStage.Decoded);
        var s = t.Summary();
        Assert.Equal(1, s.RequestsBeforeFirstImage[1]);
        Assert.Equal(1, s.RequestsBeforeFirstImage[2]);
    }

    [Fact]
    public void In_flight_is_kept_as_the_most_per_second()
    {
        var t = Timeline();
        At(100); var a = t.BeginRequest(A, "range");
        At(200); var b = t.BeginRequest(B, "range");
        At(1500); a.Done(1); b.Done(1);
        At(2100); t.BeginRequest(A, "range").Done(1);
        var s = t.Summary();
        Assert.Equal([2, 2, 1], s.InFlightPerSecond);   // second 0: 2; second 1: still 2 until 1.5 s; second 2: 1
    }

    [Fact]
    public async Task RangeHttp_reports_its_timing_headers_and_body()
    {
        var t = new TextureTimeline();
        using var server = new LoopbackTextureServer(id => id == A ? PartialFetchTests.Codestream.Value : null);
        var ok = await RangeHttp.GetAsync(_http, server.UriFor(A), 0, 599, 1 << 20, CancellationToken.None, t.BeginRequest(A, "range"));
        var missing = await RangeHttp.GetAsync(_http, server.UriFor(B), 0, 599, 1 << 20, CancellationToken.None, t.BeginRequest(B, "range"));
        Assert.Equal(RangeAnswerKind.Part, ok.Kind);
        Assert.Equal(RangeAnswerKind.NotFound, missing.Kind);
        var s = t.Summary();
        Assert.Equal(2, s.Requests);
        Assert.Equal(1, s.Statuses["206"]);
        Assert.Equal(1, s.Statuses["404"]);
        Assert.Equal(600, s.Bytes);
        Assert.Equal(0, t.InFlight);
        Assert.Equal(2, s.Http["headers"].Count);
    }

    [Fact]
    public async Task TextureSource_marks_every_stage_from_the_ask_to_the_upload()
    {
        var t = new TextureTimeline();
        var bytes = LmvTexture.Encode(64, 64, Enumerable.Range(0, 64 * 64 * 4).Select(i => (byte)i).ToArray(), false);
        using var src = new TextureSource(new SessionAssets(new SharedAssetStore(), _ => true, (_, _) => Task.FromResult(AssetFetchResult.Ok(bytes)))) { Timeline = t };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (src.TryGetLevel(A, 0, out _) == MeshAvailability.Pending && sw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(5);
        src.NoteUploaded(A);
        var s = t.Summary();
        foreach (var stage in new[] { "queue", "fetch", "decode-queue", "decode", "upload-wait", "first-image", "to-screen" })
            Assert.True(s.Stages[stage].Count == 1, stage);
    }

    [Fact]
    public void The_summary_log_fields_are_valid_log_names()
    {
        var t = Timeline();
        At(0); t.Mark(A, TextureStage.Asked);
        At(10); t.Mark(A, TextureStage.Decoded);
        foreach (var f in t.Summary().LogFields())
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", f.Name);
    }
}

/// <summary>The direct probe fetches the same textures straight from the texture capability with
/// a plain bounded client, at a given concurrency (never above the official viewer's 8), each request timed.</summary>
public sealed class TextureProbeTests : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    public void Dispose() => _http.Dispose();

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task The_probe_keeps_to_its_concurrency_and_times_every_request(int concurrency)
    {
        var ids = Enumerable.Range(1, 12).Select(i => new Guid(i, 0, 0, new byte[8])).ToList();
        using var server = new LoopbackTextureServer(id => ids.Contains(id) ? PartialFetchTests.Codestream.Value : null);
        var r = await TextureProbe.RunAsync(_http, server.UriFor, ids, concurrency, to: TextureByteRange.FirstChunk - 1, CancellationToken.None);
        Assert.Equal(12, r.Timing.Requests);
        Assert.Equal(12, r.Timing.Statuses["206"]);
        Assert.True(r.Timing.MaxInFlight <= concurrency, $"max in flight {r.Timing.MaxInFlight}");
        Assert.Equal(concurrency, r.Concurrency);
        Assert.True(r.WallMs > 0);
    }

    [Fact]
    public async Task The_probe_never_runs_above_the_official_viewers_texture_concurrency()
    {
        var ids = Enumerable.Range(1, 3).Select(i => new Guid(i, 0, 0, new byte[8])).ToList();
        using var server = new LoopbackTextureServer(_ => PartialFetchTests.Codestream.Value);
        var r = await TextureProbe.RunAsync(_http, server.UriFor, ids, 64, null, CancellationToken.None);
        Assert.Equal(TextureProbe.MaxConcurrency, r.Concurrency);
        Assert.Equal(8, TextureProbe.MaxConcurrency);
    }
}
