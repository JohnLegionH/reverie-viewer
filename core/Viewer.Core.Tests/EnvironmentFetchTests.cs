// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using Viewer.Core.Net;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The environment fetch offline, with a fake handler that counts every body byte it serves.
/// The cap is enforced while reading (never after holding the answer), and an answer that arrives after logout or after a
/// region change is dropped.
/// </summary>
public class EnvironmentFetchTests
{
    private const int Cap = EnvironmentFetcher<object>.CapBytes;
    private static readonly Uri Env = new("http://env.example.test/cap/env");

    /// <summary>A body stream that counts what it hands out; endless when <see cref="Length"/> is null.</summary>
    private sealed class CountingStream(long? length) : Stream
    {
        public long Served;
        public long? Length0 => length;
        public override int Read(byte[] buffer, int offset, int count)
        {
            long left = length is { } l ? l - Served : long.MaxValue;
            int n = (int)Math.Min(count, left);
            buffer.AsSpan(offset, n).Fill((byte)'a');
            Served += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeHandler(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(answer());
        }
    }

    private static (HttpClient Client, CountingStream Body) Serving(long? bodyLength, long? declaredLength)
    {
        var body = new CountingStream(bodyLength);
        var content = new StreamContent(body, 4096);
        if (declaredLength is { } d) content.Headers.ContentLength = d;
        return (new HttpClient(new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content })), body);
    }

    [Fact]
    public async Task An_answer_declaring_more_than_the_cap_is_refused_without_reading_its_body()
    {
        var (client, body) = Serving(Cap + 100_000, Cap + 100_000);
        var r = await BoundedHttp.GetBoundedAsync(client, Env, Cap, CancellationToken.None);
        Assert.Null(r.Body);
        Assert.Contains("declared", r.Why);
        Assert.Equal(0, body.Served);   // not one body byte read
    }

    [Fact]
    public async Task Negative_control_the_old_buffered_read_takes_the_whole_oversized_body_before_any_check()
    {
        var (client, body) = Serving(Cap + 100_000, Cap + 100_000);
        using var resp = await client.GetAsync(Env);   // what HttpCapsClient.GetAsync does: buffer, then hand over
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(Cap + 100_000, bytes.Length);
        Assert.Equal(Cap + 100_000, body.Served);
    }

    [Fact]
    public async Task An_endless_chunked_answer_is_refused_after_at_most_the_cap_plus_one_buffer()
    {
        var (client, body) = Serving(null, null);   // no Content-Length, never ends
        var r = await BoundedHttp.GetBoundedAsync(client, Env, Cap, CancellationToken.None);
        Assert.Null(r.Body);
        Assert.Contains("larger than", r.Why);
        Assert.True(body.Served <= Cap + BoundedHttp.BufferBytes, $"served {body.Served}, bound {Cap + BoundedHttp.BufferBytes}");
        Assert.True(body.Served > Cap);
        Assert.Equal(body.Served, r.BytesRead);
    }

    [Fact]
    public async Task A_normal_answer_passes_whole()
    {
        var (client, body) = Serving(3000, 3000);
        var r = await BoundedHttp.GetBoundedAsync(client, Env, Cap, CancellationToken.None);
        Assert.NotNull(r.Body);
        Assert.Equal(3000, r.Body!.Length);
        Assert.All(r.Body, b => Assert.Equal((byte)'a', b));
        Assert.Null(r.Why);
        // exactly at the cap, undeclared: accepted too
        var (client2, _) = Serving(Cap, null);
        Assert.Equal(Cap, (await BoundedHttp.GetBoundedAsync(client2, Env, Cap, CancellationToken.None)).Body!.Length);
    }

    [Fact]
    public async Task A_refused_status_is_reported_with_the_status()
    {
        var client = new HttpClient(new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var r = await BoundedHttp.GetBoundedAsync(client, Env, Cap, CancellationToken.None);
        Assert.Equal(404, r.Status);
        Assert.Null(r.Body);
    }

    private sealed class Region(string name) { public override string ToString() => name; }

    /// <summary>A GET that answers only when released, IGNORING cancellation (the worst case: the answer comes anyway).</summary>
    private sealed class Gate
    {
        public readonly TaskCompletionSource<BoundedResponse> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BoundedResponse> Get(Uri _, CancellationToken __) => Answer.Task;
        public void Release(string text) => Answer.SetResult(new BoundedResponse(200, System.Text.Encoding.UTF8.GetBytes(text), null, text.Length));
    }

    [Fact]
    public async Task An_answer_arriving_after_logout_is_dropped()
    {
        var raised = new List<(Region, string?, string?)>();
        using var f = new EnvironmentFetcher<Region>((r, l, w) => raised.Add((r, l, w)));
        var a = new Region("a");
        var gate = new Gate();
        var fetch = f.Start(a, Env, gate.Get);
        f.CancelAll();   // logout
        gate.Release("<llsd/>");
        await fetch;
        Assert.Empty(raised);
        Assert.Equal(1, f.Dropped);
        // and nothing starts after logout
        await f.Start(a, Env, (_, _) => Task.FromResult(new BoundedResponse(200, [1], null, 1)));
        Assert.Empty(raised);
    }

    [Fact]
    public async Task A_cancelled_request_after_logout_raises_nothing_either()
    {
        var raised = new List<(Region, string?, string?)>();
        using var f = new EnvironmentFetcher<Region>((r, l, w) => raised.Add((r, l, w)));
        var fetch = f.Start(new Region("a"), Env, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; });
        f.CancelAll();
        await fetch;
        Assert.Empty(raised);
    }

    [Fact]
    public async Task An_answer_arriving_after_a_region_change_is_dropped_and_does_not_touch_the_new_region()
    {
        var raised = new List<(Region Region, string? Llsd, string? Why)>();
        using var f = new EnvironmentFetcher<Region>((r, l, w) => raised.Add((r, l, w)));
        Region a = new("a"), b = new("b");
        var gateA = new Gate();
        var fetchA = f.Start(a, Env, gateA.Get);
        f.CancelRegion();   // the transport saw a different simulator connect
        await f.Start(b, Env, (_, _) => Task.FromResult(new BoundedResponse(200, System.Text.Encoding.UTF8.GetBytes("<llsd>b</llsd>"), null, 14)));
        gateA.Release("<llsd>a</llsd>");   // region A's answer comes late
        await fetchA;
        Assert.Single(raised);
        Assert.Same(b, raised[0].Region);
        Assert.Equal("<llsd>b</llsd>", raised[0].Llsd);
        Assert.Same(b, f.CurrentRegion);
        Assert.Equal(1, f.Dropped);
    }

    [Fact]
    public async Task Negative_control_without_the_region_change_the_late_answer_would_be_delivered()
    {
        var raised = new List<(Region Region, string? Llsd, string? Why)>();
        using var f = new EnvironmentFetcher<Region>((r, l, w) => raised.Add((r, l, w)));
        var gate = new Gate();
        var fetch = f.Start(new Region("a"), Env, gate.Get);
        gate.Release("<llsd>a</llsd>");
        await fetch;
        Assert.Single(raised);
        Assert.Equal("<llsd>a</llsd>", raised[0].Llsd);
    }

    [Fact]
    public async Task No_capability_and_a_timeout_are_reported_for_the_live_region()
    {
        var raised = new List<(Region Region, string? Llsd, string? Why)>();
        using var f = new EnvironmentFetcher<Region>((r, l, w) => raised.Add((r, l, w)), TimeSpan.FromMilliseconds(50));
        await f.Start(new Region("a"), null, (_, _) => throw new InvalidOperationException());
        Assert.Equal("no ExtEnvironment capability", raised[0].Why);
        await f.Start(new Region("b"), Env, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; });
        Assert.Equal(2, raised.Count);
        Assert.Contains("timed out", raised[1].Why);
    }

    [Fact]
    public async Task Through_the_bounded_get_an_oversized_environment_is_delivered_as_refused_not_as_text()
    {
        var raised = new List<(Region Region, string? Llsd, string? Why)>();
        using var f = new EnvironmentFetcher<Region>((r, l, w) => raised.Add((r, l, w)));
        var (client, body) = Serving(null, null);
        await f.Start(new Region("a"), Env, (u, ct) => BoundedHttp.GetBoundedAsync(client, u, Cap, ct));
        Assert.Single(raised);
        Assert.Null(raised[0].Llsd);
        Assert.Contains("larger than", raised[0].Why);
        Assert.True(body.Served <= Cap + BoundedHttp.BufferBytes);
    }
}
