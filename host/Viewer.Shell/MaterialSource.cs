// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// The presentation's non-blocking view of one session's PBR material assets. <see cref="TryGet"/> starts a fetch the
/// first time an id is asked for and returns Pending until it completes; the asset is read (its LLSD wrapper and glTF,
/// bounded by <see cref="LmvMaterials.MaterialCapBytes"/>) off the main thread, and completions are queued for the main
/// thread (<see cref="TryTakeArrival"/>), which then re-presents the faces using that material. A refused, missing or
/// unreadable asset is Unavailable: its faces keep the legacy look. Main-thread calls only, except the completions.
/// </summary>
public sealed class MaterialSource(Func<Guid, CancellationToken, Task<AssetFetchResult>> fetch, CoreLog? log = null) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, PbrMaterial?> _done = new();
    private readonly HashSet<Guid> _started = new();
    private readonly ConcurrentQueue<Guid> _arrived = new();
    private readonly CancellationTokenSource _life = new();
    private int _pending;

    public int Pending => Volatile.Read(ref _pending);
    public int Ready => _done.Values.Count(m => m is not null);
    public int Unavailable => _done.Values.Count(m => m is null);

    public MeshAvailability TryGet(Guid id, out PbrMaterial? material)
    {
        material = null;
        if (_done.TryGetValue(id, out var m))
        {
            material = m;
            return m is null ? MeshAvailability.Unavailable : MeshAvailability.Ready;
        }
        if (_started.Add(id))
        {
            Interlocked.Increment(ref _pending);
            _ = Task.Run(async () =>
            {
                PbrMaterial? read = null;
                try
                {
                    var r = await fetch(id, _life.Token).ConfigureAwait(false);
                    if (r.Status == AssetFetchStatus.Ok && LmvMaterials.GltfOfMaterialAsset(r.Bytes.ToArray(), why => log?.Warn("material.refused", CoreLog.F("reason", why))) is { } gltf)
                        read = PbrMaterial.FromGltf(gltf);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { read = null; }
                _done[id] = read;
                Interlocked.Decrement(ref _pending);
                _arrived.Enqueue(id);
                _feeds.Arrived(id);
            });
        }
        return MeshAvailability.Pending;
    }

    public bool TryTakeArrival(out Guid id) => _arrived.TryDequeue(out id);

    /// <summary>Who uses which material: users report the ids they draw, and <see cref="Flush"/> drops only the others, on <see cref="MaterialFlush"/>'s schedule.</summary>
    public UseLedger Uses { get; } = new();
    private readonly MaterialFlush _flush = new();

    /// <summary>One step of the flush at <paramref name="nowSeconds"/> (any steadily rising clock). Main thread; returns how many were dropped. A dropped material is fetched again when it is asked for.</summary>
    public int Flush(double nowSeconds)
    {
        var keys = _done.Keys.OrderBy(k => k).ToList();
        int dropped = 0;
        foreach (var id in _flush.Scan(nowSeconds, keys, Uses.InUse))
            if (_done.TryRemove(id, out _))
            {
                _started.Remove(id);   // asked for again when a face needs it
                dropped++;
            }
        return dropped;
    }

    /// <summary>A layer's own queue of the arrivals from now on (each layer that shares this source is told of each arrival).</summary>
    public ArrivalFeed Subscribe() => _feeds.Subscribe();
    private readonly ArrivalFeeds _feeds = new();

    public void Dispose()
    {
        _life.Cancel();
        _life.Dispose();
    }
}
