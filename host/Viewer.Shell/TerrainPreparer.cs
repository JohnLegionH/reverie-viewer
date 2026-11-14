// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using Viewer.Core.Diagnostics;
using System.Diagnostics;

namespace Viewer.Shell;

/// <summary>A terrain chunk made off the main thread: its surface in the engine's layout (null when
/// none of its patches is present: the chunk is removed), its vertices, and whether it carries a composition value.</summary>
public sealed record PreparedTerrainChunk(int ChunkX, int ChunkY, PreparedSurface? Surface, int Vertices, bool Composed);

/// <summary>
/// Terrain chunks built and encoded off the main thread (on the shared thread pool), as
/// <see cref="MeshLodPreparer"/> does for mesh LODs. The main thread takes an immutable snapshot of what a chunk's build
/// needs and hands the work in (<see cref="Start"/>); a worker runs it; the main thread takes what arrived
/// (<see cref="TryTake"/>) and hands it to the engine. Engine-free: the work makes plain C# data only.
/// Versions: every chunk carries one, moved on by <see cref="Touch"/> when its patches change. A result whose version is
/// no longer the chunk's is discarded on arrival (<see cref="Stale"/>); the chunk, dirty again, is prepared again from a
/// new snapshot. <see cref="Clear"/> (a region change, dispose) drops everything in flight and waiting: nothing from an
/// old region is ever handed over. At most <see cref="Cap"/> preparations run at once, each chunk at most once at a time.
/// Main-thread calls only, except the preparations themselves.
/// </summary>
public sealed class TerrainPreparer : IDisposable
{
    /// <summary>Preparations running at once: the same as the mesh cap, for the same reason (they share the thread pool
    /// with the texture fetches and decodes and the mesh preparations; measured that no cap crowded them out). A
    /// chunk takes about 5-12 ms to build, so two keep a 256 m region's 16 chunks within a few frames.</summary>
    public const int MaxConcurrent = 2;

    private readonly object _lock = new();
    private readonly int _cap;
    private readonly Dictionary<(int X, int Y), int> _versions = new();
    private readonly HashSet<(int X, int Y)> _inFlight = new();
    private readonly ConcurrentQueue<((int X, int Y) Key, int Version, int Generation, PreparedTerrainChunk? Result, bool Failed)> _arrived = new();
    private readonly List<double> _prepareMs = new();
    private CancellationTokenSource _life = new();
    private int _generation, _running, _peakRunning;
    private long _prepared, _stale, _discarded, _failed, _inFlightAtClear;

    /// <param name="cap">Preparations running at once (at least 1). Default <see cref="MaxConcurrent"/>.</param>
    public TerrainPreparer(int cap = MaxConcurrent) => _cap = Math.Max(1, cap);

    public int Cap => _cap;

    /// <summary>Test only: awaited on the worker before each preparation (a gate the test holds), with the token that
    /// <see cref="Clear"/> cancels. Null in the viewer.</summary>
    public Func<CancellationToken, Task>? Gate { get; set; }

    /// <summary>Chunks handed in and not taken yet (running, or arrived and waiting for the main thread).</summary>
    public int InFlight { get { lock (_lock) return _inFlight.Count; } }
    /// <summary>Preparations running on a worker now, including ones <see cref="Clear"/> has disowned (they keep their place
    /// until they finish, so the cap holds across a region change).</summary>
    public int Running { get { lock (_lock) return _running; } }
    public int PeakRunning { get { lock (_lock) return _peakRunning; } }
    /// <summary>Results taken fresh; stale ones discarded (their chunk changed meanwhile); ones thrown away because a
    /// <see cref="Clear"/> came first; failed preparations (the host builds those itself); and how many chunks were in
    /// flight at the region changes.</summary>
    public long Prepared => Interlocked.Read(ref _prepared);
    public long Stale => Interlocked.Read(ref _stale);
    public long Discarded => Interlocked.Read(ref _discarded);
    public long Failed => Interlocked.Read(ref _failed);
    public long InFlightAtClear => Interlocked.Read(ref _inFlightAtClear);

    /// <summary>Every preparation's own time on its worker, in ms (the first 8192).</summary>
    public double[] PrepareMs { get { lock (_lock) return _prepareMs.ToArray(); } }

    /// <summary>The chunk's patches changed: a preparation already running for it will be discarded on arrival.</summary>
    public void Touch((int X, int Y) key)
    {
        lock (_lock) _versions[key] = _versions.GetValueOrDefault(key) + 1;
    }

    /// <summary>A place is free and <paramref name="key"/> is not already being prepared.</summary>
    public bool CanStart((int X, int Y) key) { lock (_lock) return _running < _cap && !_inFlight.Contains(key); }

    /// <summary>A place is free (for any chunk not in flight).</summary>
    public bool HasRoom { get { lock (_lock) return _running < _cap; } }

    public bool IsInFlight((int X, int Y) key) { lock (_lock) return _inFlight.Contains(key); }

    /// <summary>Hands <paramref name="work"/> (a closure over the chunk's snapshot) to a worker, at the chunk's current
    /// version. False when <see cref="CanStart"/> is not true.</summary>
    public bool Start((int X, int Y) key, Func<PreparedTerrainChunk?> work)
    {
        int version, generation;
        CancellationToken token;
        lock (_lock)
        {
            if (_running >= _cap || !_inFlight.Add(key)) return false;
            version = _versions.GetValueOrDefault(key);
            generation = _generation;
            token = _life.Token;
            _peakRunning = Math.Max(_peakRunning, ++_running);
        }
        var gate = Gate;
        _ = Task.Run(async () =>
        {
            PreparedTerrainChunk? result = null;
            bool failed = false, cancelled = false;
            double ms = 0;
            try
            {
                if (gate is not null) await gate(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                long t0 = Stopwatch.GetTimestamp();
                var alloc = AllocationMeter.Shared.Begin(AllocKind.TerrainPrepare);   // (measurement only)
                try { result = work(); }
                finally { alloc.End(); }
                ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception) { failed = true; }
            lock (_lock)
            {
                _running--;
                if (!cancelled && generation == _generation)
                {
                    if (!failed && _prepareMs.Count < 8192) _prepareMs.Add(ms);
                    _arrived.Enqueue((key, version, generation, result, failed));
                }
                else Interlocked.Increment(ref _discarded);
                Monitor.PulseAll(_lock);
            }
        });
        return true;
    }

    /// <summary>Takes the next fresh result that arrived: stale ones (their chunk touched since it started) and ones from
    /// before a <see cref="Clear"/> are discarded on the way. <paramref name="failed"/>: the preparation threw (the host
    /// builds that chunk itself).</summary>
    public bool TryTake(out PreparedTerrainChunk? result, out (int X, int Y) key, out bool failed)
    {
        while (_arrived.TryDequeue(out var a))
        {
            lock (_lock)
            {
                if (a.Generation != _generation) { Interlocked.Increment(ref _discarded); continue; }
                _inFlight.Remove(a.Key);
                if (a.Version != _versions.GetValueOrDefault(a.Key)) { Interlocked.Increment(ref _stale); continue; }
            }
            if (a.Failed) Interlocked.Increment(ref _failed);
            else Interlocked.Increment(ref _prepared);
            result = a.Result;
            key = a.Key;
            failed = a.Failed;
            return true;
        }
        result = null;
        key = default;
        failed = false;
        return false;
    }

    /// <summary>Test renders and measurements only (a flush): blocks until something arrives or nothing runs, at most
    /// <paramref name="timeout"/>.</summary>
    public void WaitForArrival(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        lock (_lock)
            while (_arrived.IsEmpty && _running > 0 && _inFlight.Count > 0)
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(_lock, left)) return;
            }
    }

    /// <summary>Drops everything in flight and waiting (a region change): nothing is in flight afterwards, and a
    /// preparation still running throws its result away.</summary>
    public void Clear()
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            Interlocked.Add(ref _inFlightAtClear, _inFlight.Count);
            _generation++;
            _inFlight.Clear();
            _versions.Clear();
            old = _life;
            _life = new CancellationTokenSource();
        }
        while (_arrived.TryDequeue(out _)) Interlocked.Increment(ref _discarded);
        old.Cancel();
    }

    public void Dispose()
    {
        Clear();
        CancellationTokenSource last;
        lock (_lock) last = _life;
        last.Cancel();
    }
}
