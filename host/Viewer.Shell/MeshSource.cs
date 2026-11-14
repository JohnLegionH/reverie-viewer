// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using Viewer.Core.Assets;
using Viewer.Core.Meshing;
using Viewer.Core.Residency;

namespace Viewer.Shell;

public enum MeshAvailability { Ready, Pending, Unavailable }

/// <summary>
/// The presentation's non-blocking view of one session's meshes (<see cref="SessionMeshes"/>: per-session
/// authorisation, bounded decode). <see cref="TryGet"/> starts a fetch the first time an id is asked for and returns
/// Pending until it completes; completions are queued for the main thread (<see cref="TryTakeArrival"/>), which then
/// re-presents the objects using that mesh. Main-thread calls only, except the completion continuations.
/// The host lets an asset's decoded geometry go (<see cref="Release"/>) once no LOD
/// build needs it; the asset's LODs stay known (<see cref="TryGetLods"/>), and asked for again it is fetched and decoded
/// again off the main thread as a first decode is (the shared asset store, else the content store, before the network).
/// </summary>
public sealed class MeshSource(SessionMeshes meshes) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, MeshFetchResult> _done = new();
    private readonly HashSet<Guid> _started = new();
    private readonly ConcurrentQueue<Guid> _arrived = new();
    private readonly ConcurrentDictionary<Guid, long> _arrivedAt = new();
    private readonly CancellationTokenSource _life = new();
    private int _pending;
    private long _decodedBytes, _peakDecodedBytes;
    private int _decodedEntries;
    // the LODs each decoded asset has (bit d: LOD d), kept after its geometry is let go; when each was let go (the
    // host's clock); the re-decodes in flight (started at, within the keep of their let-go); what the re-decodes did
    private readonly ConcurrentDictionary<Guid, byte> _lods = new();
    private readonly Dictionary<Guid, TimeSpan> _letGoAt = new();
    private readonly ConcurrentDictionary<Guid, (long Started, bool Soon)> _reDecoding = new();
    private readonly object _statsLock = new();
    private readonly List<double> _reDecodeMs = new();
    private readonly Dictionary<string, int> _reDecodeSources = new();
    private long _letGo, _letGoAtReset, _reDecodes, _reDecodesSoon, _networkReDecodes;

    public int Pending => Volatile.Read(ref _pending);
    public int Ready => _done.Values.Count(r => r.Geometry is not null);
    public int Unavailable => _done.Values.Count(r => r.Geometry is null);

    /// <summary>The decoded meshes this source keeps (one per asset, every LOD), and their array bytes
    /// (<see cref="MeshFootprint.DecodedBytes"/>), now and at their peak. They go down when the host lets
    /// an asset's geometry go (<see cref="Release"/>, <see cref="ReleaseAll"/>).</summary>
    public int DecodedEntries => Volatile.Read(ref _decodedEntries);
    public long DecodedBytes => Interlocked.Read(ref _decodedBytes);
    public long PeakDecodedBytes => Interlocked.Read(ref _peakDecodedBytes);

    /// <summary>The host's clock (the one its keeper uses), for "decoded again within <see cref="ReDecodeSoon"/> of
    /// being let go". Null: re-decodes are counted, none as soon.</summary>
    public Func<TimeSpan>? Clock { get; set; }
    public TimeSpan ReDecodeSoon { get; set; } = DecodedMeshKeeper.DefaultKeep;

    /// <summary>Entries let go by the host's keeper (not counting a region change's), entries let go at region changes,
    /// re-decodes started, re-decodes started within <see cref="ReDecodeSoon"/> of their let-go, re-decodes whose bytes
    /// came from the network (neither store had them).</summary>
    public long LetGo => Interlocked.Read(ref _letGo);
    public long LetGoAtReset => Interlocked.Read(ref _letGoAtReset);
    public long ReDecodes => Interlocked.Read(ref _reDecodes);
    public long ReDecodesSoon => Interlocked.Read(ref _reDecodesSoon);
    public long NetworkReDecodes => Interlocked.Read(ref _networkReDecodes);

    /// <summary>Every finished re-decode's time (ms, from the request to the decoded geometry) and where each one's
    /// bytes came from (shared, content, network; failed ones as failed).</summary>
    public double[] ReDecodeMs { get { lock (_statsLock) return _reDecodeMs.ToArray(); } }
    public IReadOnlyDictionary<string, int> ReDecodeSources { get { lock (_statsLock) return new Dictionary<string, int>(_reDecodeSources); } }

    public MeshAvailability TryGet(Guid id, out MeshGeometry? geometry)
    {
        geometry = null;
        if (Viewer.Core.World.TreeMesh.TryGet(id, out var tree, out _, out _)) { geometry = tree; _lods[id] = tree!.LodMask; return MeshAvailability.Ready; }   // a tree is made here, not fetched
        if (_done.TryGetValue(id, out var r))
        {
            geometry = r.Geometry;
            return geometry is null ? MeshAvailability.Unavailable : MeshAvailability.Ready;
        }
        if (_started.Add(id))
        {
            if (_letGoAt.Remove(id, out var at))
            {
                // asked for again after it was let go: decoded again, counted
                bool soon = Clock is { } clock && clock() - at < ReDecodeSoon;
                Interlocked.Increment(ref _reDecodes);
                if (soon) Interlocked.Increment(ref _reDecodesSoon);
                _reDecoding[id] = (System.Diagnostics.Stopwatch.GetTimestamp(), soon);
            }
            Interlocked.Increment(ref _pending);
            _ = meshes.GetAsync(id, _life.Token).ContinueWith(t =>
            {
                var r = t.IsCompletedSuccessfully ? t.Result : new MeshFetchResult(AssetFetchStatus.Failed, null);
                if (r.Geometry is { } g)
                {
                    // counted before it is published, so a let-go never sees it uncounted
                    Interlocked.Increment(ref _decodedEntries);
                    long now = Interlocked.Add(ref _decodedBytes, MeshFootprint.DecodedBytes(g));
                    for (long peak; now > (peak = Interlocked.Read(ref _peakDecodedBytes)) && Interlocked.CompareExchange(ref _peakDecodedBytes, now, peak) != peak;) { }
                    _lods[id] = g.LodMask;
                }
                if (_reDecoding.TryRemove(id, out var rd))
                {
                    string source = r.Geometry is null ? "failed" : r.Source ?? "network";
                    if (source == "network") Interlocked.Increment(ref _networkReDecodes);
                    lock (_statsLock)
                    {
                        _reDecodeMs.Add((System.Diagnostics.Stopwatch.GetTimestamp() - rd.Started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                        _reDecodeSources[source] = _reDecodeSources.GetValueOrDefault(source) + 1;
                    }
                }
                _done[id] = r;
                _arrivedAt[id] = System.Diagnostics.Stopwatch.GetTimestamp();
                Interlocked.Decrement(ref _pending);
                _arrived.Enqueue(id);
                _feeds.Arrived(id);
            }, TaskScheduler.Default);
        }
        return MeshAvailability.Pending;
    }

    /// <summary>The LODs the asset has (bit d: LOD d), known once it has been decoded, also after its geometry was let
    /// go, so the LOD rule can be resolved (<see cref="MeshGeometry.Resolve(byte, int)"/>) without decoding it again. Starts
    /// nothing.</summary>
    public bool TryGetLods(Guid id, out byte lods) => _lods.TryGetValue(id, out lods);

    /// <summary>The asset was decoded and its geometry let go since (the next <see cref="TryGet"/> decodes it again).</summary>
    public bool IsLetGo(Guid id) => _letGoAt.ContainsKey(id);

    /// <summary>
    /// Lets the asset's decoded geometry go, if it is decoded (not in flight, not failed): the source
    /// and the session's meshes drop it, the decoded counters go down, its LODs stay known. <paramref name="now"/> is the
    /// host's clock. The caller makes sure nothing still needs it (no preparation in flight, no object waiting).
    /// </summary>
    public bool Release(Guid id, TimeSpan now)
    {
        if (!LetGoOne(id)) return false;
        _letGoAt[id] = now;
        Interlocked.Increment(ref _letGo);
        return true;
    }

    /// <summary>A region change or dispose lets every decoded geometry go; nothing is remembered of it
    /// (a later request is a first decode). Returns how many entries were let go.</summary>
    public int ReleaseAll()
    {
        int n = 0;
        foreach (var id in _done.Keys.ToList())
            if (LetGoOne(id)) n++;
        _letGoAt.Clear();
        _lods.Clear();
        Interlocked.Add(ref _letGoAtReset, n);
        return n;
    }

    private bool LetGoOne(Guid id)
    {
        if (!_done.TryGetValue(id, out var r) || r.Geometry is not { } g || !_done.TryRemove(id, out _))
            return false;
        _started.Remove(id);
        meshes.Forget(id);
        Interlocked.Decrement(ref _decodedEntries);
        Interlocked.Add(ref _decodedBytes, -MeshFootprint.DecodedBytes(g));
        return true;
    }

    public bool TryTakeArrival(out Guid id) => _arrived.TryDequeue(out id);

    /// <summary>A layer's own queue of the arrivals from now on (every layer that shares this source subscribes, so each is told of each arrival).</summary>
    public ArrivalFeed Subscribe() => _feeds.Subscribe();
    private readonly ArrivalFeeds _feeds = new();

    /// <summary>When the mesh's fetch and decode ended (Stopwatch ticks), null before.</summary>
    public long? ArrivedAt(Guid id) => _arrivedAt.TryGetValue(id, out var t) ? t : null;

    public void Dispose()
    {
        _life.Cancel();
        meshes.Dispose();
    }
}
