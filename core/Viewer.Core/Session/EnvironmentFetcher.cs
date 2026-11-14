// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Net;

namespace Viewer.Core.Session;

/// <summary>
/// The region environment fetch, tied to the session's lifetime. Before, the fetch was
/// fire-and-forget with only its own 20 s timeout: an answer could land after logout, or after the agent had moved to
/// another region and be raised as if it were the new region's. Here every fetch runs under a token linked to the
/// session (cancelled by <see cref="CancelAll"/>: logout, disconnect, dispose) and to its region (cancelled by
/// <see cref="CancelRegion"/> or by the next <see cref="Start"/>: a region change), plus the timeout; each fetch has a
/// generation, and an answer is delivered only while its generation is still the live one. A late answer is DROPPED,
/// even one that arrives although its request ignored the cancellation. Engine- and library-free; one per transport.
/// </summary>
/// <typeparam name="TRegion">The transport's region key (compared by reference).</typeparam>
public sealed class EnvironmentFetcher<TRegion> : IDisposable where TRegion : class
{
    /// <summary>The address of a parcel's environment: the capability with the parcel's local id as its query.</summary>
    public static Uri ParcelUri(Uri capability, int parcelId)
    {
        string text = capability.AbsoluteUri;
        return new Uri(text + (text.Contains('?') ? "&" : "?") + "parcelid=" + parcelId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The largest environment answer accepted (bytes on the wire, after decompression).</summary>
    public const int CapBytes = 512 * 1024;
    public const int TimeoutSeconds = 20;

    private readonly Action<TRegion, string?, string?> _deliver;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _session = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _region;
    private TRegion? _current;
    private long _generation;
    private bool _ended;

    /// <param name="deliver">(region, llsd, why): called at most once per fetch, only for the live generation.</param>
    public EnvironmentFetcher(Action<TRegion, string?, string?> deliver, TimeSpan? timeout = null)
    {
        _deliver = deliver;
        _timeout = timeout ?? TimeSpan.FromSeconds(TimeoutSeconds);
    }

    /// <summary>The region whose fetch is live (null when none, or after a cancel).</summary>
    public TRegion? CurrentRegion { get { lock (_lock) return _current; } }

    /// <summary>Answers delivered and answers dropped because they arrived for a cancelled generation (for tests).</summary>
    public int Delivered { get; private set; }
    public int Dropped { get; private set; }

    /// <summary>
    /// Starts the fetch for <paramref name="region"/> (cancelling any earlier region's). A null <paramref name="uri"/>
    /// (no ExtEnvironment capability) is delivered as such. <paramref name="get"/> is the bounded GET (the session's client).
    /// </summary>
    public async Task Start(TRegion region, Uri? uri, Func<Uri, CancellationToken, Task<BoundedResponse>> get)
    {
        long generation;
        CancellationToken token;
        lock (_lock)
        {
            if (_ended)
                return;
            _region?.Cancel();
            _region?.Dispose();
            _region = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
            _region.CancelAfter(_timeout);
            _current = region;
            generation = ++_generation;
            token = _region.Token;
        }
        if (uri is null) { Deliver(generation, region, null, "no ExtEnvironment capability"); return; }
        string? llsd = null, why;
        try
        {
            var r = await get(uri, token).ConfigureAwait(false);
            if (r.Body is { } body)
            {
                llsd = System.Text.Encoding.UTF8.GetString(body);
                why = null;
            }
            else
                why = r.Status is null ? "ExtEnvironment answered nothing" : "ExtEnvironment " + r.Why;
        }
        catch (OperationCanceledException)
        {
            why = "environment fetch cancelled or timed out";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            why = "environment fetch failed: " + ex.GetType().Name;
        }
        Deliver(generation, region, llsd, why);
    }

    private void Deliver(long generation, TRegion region, string? llsd, string? why)
    {
        // under the lock, so a cancel that has returned can never be followed by a delivery of the cancelled generation
        lock (_lock)
        {
            if (_ended || generation != _generation)
            {
                Dropped++;
                return;
            }
            Delivered++;
            _deliver(region, llsd, why);
        }
    }

    /// <summary>A region change: the live fetch is cancelled and its answer, if any still comes, dropped.</summary>
    public void CancelRegion()
    {
        lock (_lock)
        {
            _region?.Cancel();
            _generation++;
            _current = null;
        }
    }

    /// <summary>Logout, disconnect or dispose: everything is cancelled and nothing is delivered from now on.</summary>
    public void CancelAll()
    {
        lock (_lock)
        {
            if (_ended)
                return;
            _ended = true;
            _session.Cancel();
            _generation++;
            _current = null;
        }
    }

    public void Dispose()
    {
        CancelAll();
        lock (_lock)
        {
            _region?.Dispose();
            _region = null;
        }
        _session.Dispose();
    }
}
