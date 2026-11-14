// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;

namespace Viewer.Core.Net;

/// <summary>
/// A session's texture HTTP, on a client of its own (the outbound
/// policy's handler) instead of LibreMetaverse's caps client, whose rate limiter queued every texture request in its
/// Default bucket.
///   - At most <see cref="MaxInFlight"/> requests in flight, over at most that many connections: the official viewer's
///     texture-fetch concurrency per host (TextureFetchConcurrency 8, llappcorehttp.cpp; behaviour only). Requests past
///     it wait in <see cref="Queue"/>: faces' own textures, then PBR base colours, then detail maps, each class in order.
///   - Every request ends: a range request or a whole-file fetch past <see cref="Deadline"/> fails (the texture keeps its
///     placeholder; the session remembers the failure for a while, <see cref="SessionAssets"/>).
///   - A whole-file fetch is a plain bounded GET (no Range): a 404 is NotFound at once; the body is bounded by the cap
///     before it is held (<see cref="BoundedHttp"/>).
/// Cancellation (the caller's token: logout, disconnect, region change, dispose) stops a request at once and is reported
/// as cancelled, never as a failure.
/// </summary>
public sealed class TextureHttp : IDisposable
{
    public const int MaxInFlight = 8;

    /// <summary>60 s: the official viewer's texture transfer timeout (30 s connect timeout, doubled for its pipelined texture
    /// policy class; llcorehttp, behaviour only), and LibreMetaverse's own caps timeout. A 2 MiB texture at 0.5 Mbit/s still
    /// arrives in time; a server that never answers costs one slot for a minute, not forever.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private readonly HttpClient _client;
    private readonly FetchQueue _gate = new(MaxInFlight);

    /// <summary>The request slots and their waits.</summary>
    public FetchQueue Queue => _gate;

    public TextureHttp(OutboundPolicy policy, CoreLog log, CertificateRefusals? refusals = null) =>
        _client = OutboundHttp.CreateClient(policy, log, Timeout.InfiniteTimeSpan, maxConnectionsPerServer: MaxInFlight, refusals: refusals);   // deadlines are per request

    /// <summary>The deadline of each request (tests shorten it).</summary>
    public TimeSpan RequestDeadline { get; set; } = Deadline;

    /// <param name="begin">Called when the request is actually sent (after any wait here), for its timing.</param>
    /// <param name="cls">The texture's fetch class (the order it waits in).</param>
    public async Task<RangeAnswer> RangeAsync(Uri uri, long from, long? to, int capBytes, CancellationToken ct, Func<IHttpTiming?>? begin = null,
        TextureFetchClass cls = TextureFetchClass.BaseColour)
    {
        await _gate.EnterAsync(cls, ct).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(RequestDeadline);
            try { return await RangeHttp.GetAsync(_client, uri, from, to, capBytes, deadline.Token, begin?.Invoke()).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return RangeAnswer.Fail(null, $"no answer within the {RequestDeadline.TotalSeconds:0.#} s deadline");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>The whole file by a plain GET: Ok, NotFound (404), Failed (any other answer, a body over the cap, a transport
    /// error, the deadline) or Cancelled (the caller's token).</summary>
    public async Task<AssetFetchResult> WholeAsync(Uri uri, int capBytes, CancellationToken ct, Func<IHttpTiming?>? begin = null,
        TextureFetchClass cls = TextureFetchClass.BaseColour)
    {
        try { await _gate.EnterAsync(cls, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return AssetFetchResult.Of(AssetFetchStatus.Cancelled); }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(RequestDeadline);
            var timing = begin?.Invoke();
            long read = 0;
            try
            {
                var r = await BoundedHttp.GetBoundedAsync(_client, uri, capBytes, deadline.Token, timing).ConfigureAwait(false);
                read = r.BytesRead;
                if (r.Status == 404) return AssetFetchResult.Of(AssetFetchStatus.NotFound);
                return r.Body is { Length: > 0 } b ? AssetFetchResult.Ok(b) : AssetFetchResult.Of(AssetFetchStatus.Failed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return AssetFetchResult.Of(AssetFetchStatus.Cancelled); }
            catch (OperationCanceledException) { return AssetFetchResult.Of(AssetFetchStatus.Failed); }   // the deadline
            catch (Exception e) when (e is HttpRequestException or IOException) { return AssetFetchResult.Of(AssetFetchStatus.Failed); }
            finally { timing?.Done(read); }
        }
        finally { _gate.Release(); }
    }

    /// <summary>A fetch that is not ours to bound (LibreMetaverse's, used only when the region has no texture capability),
    /// bounded all the same: past <paramref name="deadline"/> it is Failed whether or not it honours its token; the
    /// caller's token makes it Cancelled.</summary>
    public static async Task<AssetFetchResult> WithDeadline(Func<CancellationToken, Task<AssetFetchResult>> fetch, TimeSpan deadline, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);
        try { return await fetch(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return AssetFetchResult.Of(ct.IsCancellationRequested ? AssetFetchStatus.Cancelled : AssetFetchStatus.Failed); }
    }

    public void Dispose() => _client.Dispose();
}
