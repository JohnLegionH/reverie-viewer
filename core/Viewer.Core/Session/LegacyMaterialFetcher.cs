// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;

namespace Viewer.Core.Session;

/// <summary>
/// Asks the region for the legacy materials its objects' texture entries name, the way the official viewer's material
/// manager does: ids are gathered for a moment, then requested in batches of at most <see cref="MaxPerRequest"/> through
/// the region's RenderMaterials capability, one request at a time. Each id is asked for once per region (an id the region
/// does not answer is not asked again). A region change drops the previous region's queue and cancels its request, and
/// an answer for a region that is no longer current is DROPPED; logout and dispose cancel everything. Engine- and
/// library-free; one per transport.
/// </summary>
/// <typeparam name="TRegion">The transport's region key (compared by reference).</typeparam>
public sealed class LegacyMaterialFetcher<TRegion> : IDisposable where TRegion : class
{
    /// <summary>Ids per request (the official viewer's limit for a RenderMaterials request).</summary>
    public const int MaxPerRequest = 50;
    public const int TimeoutSeconds = 20;

    private readonly Func<TRegion, IReadOnlyList<Guid>, CancellationToken, Task<IReadOnlyList<LegacyMaterial>>> _fetch;
    private readonly Action<TRegion, IReadOnlyList<LegacyMaterial>> _deliver;
    private readonly TimeSpan _gather;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _session = new();
    private readonly object _lock = new();
    private readonly HashSet<Guid> _asked = new();
    private readonly Queue<Guid> _queue = new();
    private CancellationTokenSource? _regionCts;
    private TRegion? _region;
    private long _generation;
    private bool _running;
    private bool _ended;

    /// <param name="fetch">(region, ids, token): the materials the region answers for those ids (may be fewer).</param>
    /// <param name="deliver">(region, materials): called only for the current region, never after a cancel.</param>
    /// <param name="gather">How long ids are gathered before a request (default 250 ms).</param>
    public LegacyMaterialFetcher(Func<TRegion, IReadOnlyList<Guid>, CancellationToken, Task<IReadOnlyList<LegacyMaterial>>> fetch,
        Action<TRegion, IReadOnlyList<LegacyMaterial>> deliver, TimeSpan? gather = null, TimeSpan? timeout = null)
    {
        _fetch = fetch;
        _deliver = deliver;
        _gather = gather ?? TimeSpan.FromMilliseconds(250);
        _timeout = timeout ?? TimeSpan.FromSeconds(TimeoutSeconds);
    }

    /// <summary>Requests made, materials delivered, answers dropped (late or for an old region), requests failed.</summary>
    public int Requests { get; private set; }
    public int Delivered { get; private set; }
    public int Dropped { get; private set; }
    public int Failed { get; private set; }

    /// <summary>The ids queued and not yet requested (for tests).</summary>
    public int Queued { get { lock (_lock) return _queue.Count; } }

    /// <summary>Queues the ids not yet asked for in <paramref name="region"/>; a different region than the current one starts afresh.</summary>
    public void Want(TRegion region, IEnumerable<Guid> ids)
    {
        bool start = false;
        lock (_lock)
        {
            if (_ended)
                return;
            if (!ReferenceEquals(_region, region))
                ResetRegionLocked(region);
            foreach (var id in ids)
                if (id != Guid.Empty && _asked.Add(id))
                    _queue.Enqueue(id);
            if (_queue.Count > 0 && !_running)
                _running = start = true;
        }
        if (start)
            _ = Task.Run(RunAsync);
    }

    /// <summary>A region change: the previous region's queue and request are dropped.</summary>
    public void CancelRegion()
    {
        lock (_lock)
            if (!_ended)
                ResetRegionLocked(null);
    }

    /// <summary>Logout or disconnect: nothing more is requested or delivered.</summary>
    public void CancelAll()
    {
        lock (_lock)
        {
            if (_ended)
                return;
            _ended = true;
            ResetRegionLocked(null);
        }
        _session.Cancel();
    }

    private void ResetRegionLocked(TRegion? region)
    {
        _regionCts?.Cancel();
        _regionCts?.Dispose();
        _regionCts = region is null ? null : CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
        _region = region;
        _generation++;
        _asked.Clear();
        _queue.Clear();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            if (_gather > TimeSpan.Zero)
            {
                try { await Task.Delay(_gather, _session.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { lock (_lock) _running = false; return; }
            }
            TRegion region;
            long generation;
            CancellationToken regionToken;
            var batch = new List<Guid>(MaxPerRequest);
            lock (_lock)
            {
                if (_ended || _region is null || _queue.Count == 0)
                {
                    _running = false;
                    return;
                }
                while (batch.Count < MaxPerRequest && _queue.Count > 0)
                    batch.Add(_queue.Dequeue());
                region = _region;
                generation = _generation;
                regionToken = _regionCts!.Token;
                Requests++;
            }
            IReadOnlyList<LegacyMaterial>? answer = null;
            try
            {
                using var one = CancellationTokenSource.CreateLinkedTokenSource(regionToken);
                one.CancelAfter(_timeout);
                answer = await _fetch(region, batch, one.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lock (_lock) Failed++;
            }
            if (answer is { Count: > 0 })
            {
                bool live;
                lock (_lock)
                {
                    live = !_ended && generation == _generation;
                    if (live) Delivered += answer.Count; else Dropped++;
                }
                if (live)
                    _deliver(region, answer);
            }
        }
    }

    public void Dispose()
    {
        CancelAll();
        _session.Dispose();
    }
}
