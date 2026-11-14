// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using Viewer.Core.Assets;
using Viewer.Core.Avatars;

namespace Viewer.Shell;

/// <summary>
/// The presentation's non-blocking view of one session's animation assets. <see cref="TryGet"/> asks for an id the first
/// time it is wanted and returns Pending until it is read; at most <see cref="MaxInFlight"/> fetches run at a time (the
/// rest wait their turn, in the order asked, and start from <see cref="Pump"/>). Each asset is read
/// (<see cref="AnimationClip.Parse"/>, bounded by its caps) off the main thread. A refused, missing or unreadable asset
/// is Unavailable, and its avatar keeps its other animations or the stand. Main-thread calls only, except the completions.
/// </summary>
public sealed class AnimationSource(Func<Guid, CancellationToken, Task<AssetFetchResult>> fetch, int maxInFlight = AnimationSource.DefaultMaxInFlight) : IDisposable
{
    public const int DefaultMaxInFlight = 4;

    /// <summary>The most clips kept before the ones nothing plays are dropped (llmotioncontroller.cpp MAX_MOTION_INSTANCES).</summary>
    public const int MaxLoaded = 32;

    /// <summary>Who plays which clip: users report the ids they play, and <see cref="Purge"/> drops only the others.</summary>
    public UseLedger Uses { get; } = new();

    private readonly ConcurrentDictionary<Guid, AnimationClip?> _done = new();
    private readonly HashSet<Guid> _asked = new();
    private readonly Queue<Guid> _waiting = new();
    private readonly ConcurrentQueue<Guid> _arrived = new();
    private readonly CancellationTokenSource _life = new();
    private int _inFlight, _peak;
    private bool _disposed;

    public int MaxInFlight { get; } = maxInFlight;
    public int InFlight => Volatile.Read(ref _inFlight);
    /// <summary>The most fetches that ever ran at once.</summary>
    public int PeakInFlight => Volatile.Read(ref _peak);
    public int Waiting => _waiting.Count;
    public int Ready => _done.Values.Count(c => c is not null);
    public int Unavailable => _done.Values.Count(c => c is null);
    public int Started { get; private set; }

    public MeshAvailability TryGet(Guid id, out AnimationClip? clip)
    {
        clip = null;
        if (_done.TryGetValue(id, out var c))
        {
            clip = c;
            return c is null ? MeshAvailability.Unavailable : MeshAvailability.Ready;
        }
        if (_asked.Add(id))
        {
            _waiting.Enqueue(id);
            Pump();
        }
        return MeshAvailability.Pending;
    }

    /// <summary>The clip if it is at hand, else null (asks for it as <see cref="TryGet"/> does).</summary>
    public AnimationClip? Clip(Guid id) => TryGet(id, out var c) == MeshAvailability.Ready ? c : null;

    /// <summary>Starts waiting fetches while fewer than <see cref="MaxInFlight"/> run. Main thread, once a frame.</summary>
    public void Pump()
    {
        while (!_disposed && _waiting.Count > 0 && Volatile.Read(ref _inFlight) < MaxInFlight)
            Start(_waiting.Dequeue());
    }

    private void Start(Guid id)
    {
        int now = Interlocked.Increment(ref _inFlight);
        int peak;
        while (now > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, peak) != peak) { }
        Started++;
        var token = _life.Token;
        _ = Task.Run(async () =>
        {
            AnimationClip? read = null;
            try
            {
                var r = await fetch(id, token).ConfigureAwait(false);
                if (r.Status == AssetFetchStatus.Ok) read = AnimationClip.Parse(r.Bytes.Span);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { read = null; }
            _done[id] = read;
            Interlocked.Decrement(ref _inFlight);
            _arrived.Enqueue(id);
        });
    }

    public bool TryTakeArrival(out Guid id) => _arrived.TryDequeue(out id);

    /// <summary>
    /// Drops the clips nobody plays once more than <see cref="MaxLoaded"/> are loaded (llmotioncontroller.cpp purgeExcessMotions: past the limit every
    /// motion that is not playing is removed, and the shared keyframe data goes with it). A clip any user lists in <see cref="Uses"/> stays. An asset
    /// that could not be read stays as a marker, so it is not fetched again and again. A dropped clip is fetched again when it is asked for.
    /// Main thread; returns how many were dropped.
    /// </summary>
    public int Purge()
    {
        if (Ready <= MaxLoaded) return 0;
        int dropped = 0;
        foreach (var (id, clip) in _done.ToArray())
        {
            if (clip is null || Uses.InUse(id)) continue;
            if (_done.TryRemove(id, out _))
            {
                _asked.Remove(id);   // asked for again when something needs it
                dropped++;
            }
        }
        return dropped;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _waiting.Clear();
        _life.Cancel();
        _life.Dispose();
    }
}
