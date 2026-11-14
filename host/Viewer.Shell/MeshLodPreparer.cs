// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using Viewer.Core.Diagnostics;
using Viewer.Core.Meshing;

namespace Viewer.Shell;

/// <summary>
/// Mesh LOD surface data prepared off the main thread (<see cref="MeshSurfaceData"/>, on the shared
/// thread pool), one preparation per (asset, LOD) at a time. The main thread asks (<see cref="Request"/>), learns what is
/// ready from <see cref="TryTakeArrival"/>, and takes it (<see cref="TryTake"/>) to hand to the engine, or lets it go
/// (<see cref="Drop"/>) when no object wants that LOD any more. <see cref="Clear"/> (a region change, dispose) forgets
/// everything asked for and ready at once; a preparation still running then throws its result away. Engine-free.
/// Main-thread calls only, except the preparations themselves.
/// At most <see cref="MaxConcurrent"/> preparations run at once, so they cannot crowd the texture
/// fetches off the shared thread pool; the others wait in a queue and start in the order asked, each as one finishes.
/// </summary>
public sealed class MeshLodPreparer : IDisposable
{
    /// <summary>Preparations running at once. Sized by the measurement (region A v5, 3 runs
    /// each at 1, 2, 4 and no cap): no cap ran up to 30 at once, on the pool the texture fetches use; 2 kept mesh decode to
    /// first draw as without a cap (p95 47 against 38 ms, ranges overlapping) where 1 lengthened it (p50 23 against 15 ms);
    /// first image did not measurably change with any cap.</summary>
    public const int MaxConcurrent = 2;

    /// <summary>Test only (the render harness's --tr-mesh-prep-cap=): the cap every new preparer takes instead of
    /// <see cref="MaxConcurrent"/>; 0 is no cap. Null in the viewer.</summary>
    public static int? CapOverride { get; set; }

    private readonly object _lock = new();
    private readonly int _cap;
    private readonly HashSet<(Guid Asset, int Lod)> _inFlight = new();
    private readonly Queue<((Guid Asset, int Lod) Key, Func<PreparedMeshLod?> Prepare, int Generation, CancellationToken Token)> _queued = new();
    private readonly Dictionary<(Guid Asset, int Lod), PreparedMeshLod?> _ready = new();
    private readonly ConcurrentQueue<(Guid Asset, int Lod)> _arrived = new();
    private CancellationTokenSource _life = new();
    private int _generation, _running, _peakRunning;
    private long _heldBytes, _peakHeldBytes, _prepared, _dropped, _failed, _discarded;

    /// <param name="cap">Preparations running at once; 0 is no cap. Default: <see cref="CapOverride"/>, else <see cref="MaxConcurrent"/>.</param>
    public MeshLodPreparer(int? cap = null) => _cap = cap ?? CapOverride ?? MaxConcurrent;

    /// <summary>This preparer's cap (0: none).</summary>
    public int Cap => _cap;
    /// <summary>Asked for and waiting for a free place (part of <see cref="InFlight"/>).</summary>
    public int Queued { get { lock (_lock) return _queued.Count; } }
    /// <summary>The most preparations that ran at once.</summary>
    public int PeakRunning => Volatile.Read(ref _peakRunning);

    /// <summary>Test only: awaited on the worker before each preparation (a gate the test holds), with the token that
    /// <see cref="Clear"/> cancels. Null in the viewer.</summary>
    public Func<CancellationToken, Task>? Gate { get; set; }

    /// <summary>Test only: called on the worker right after a finished preparation is recorded, with its key, before the
    /// worker gives up its place (a test holds the worker there). Null in the viewer.</summary>
    public Action<(Guid Asset, int Lod)>? AfterRecord { get; set; }

    /// <summary>Asked for and not ready yet.</summary>
    public int InFlight { get { lock (_lock) return _inFlight.Count; } }
    /// <summary>Ready and not taken or dropped yet, and their bytes (now and at the peak).</summary>
    public int Held { get { lock (_lock) return _ready.Count; } }
    public long HeldBytes { get { lock (_lock) return _heldBytes; } }
    public long PeakHeldBytes { get { lock (_lock) return _peakHeldBytes; } }
    /// <summary>Preparations running on a worker now (including ones <see cref="Clear"/> has disowned: they keep their
    /// place until they finish, so the cap holds across a region change).</summary>
    public int Running { get { lock (_lock) return _running; } }
    /// <summary>Preparations finished, ready ones dropped unwanted, failed ones (the host builds those itself), and results
    /// thrown away because a <see cref="Clear"/> came first.</summary>
    public long Prepared => Interlocked.Read(ref _prepared);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Failed => Interlocked.Read(ref _failed);
    public long Discarded => Interlocked.Read(ref _discarded);

    /// <summary>Asked for or ready, not yet taken.</summary>
    public bool IsPending((Guid Asset, int Lod) key) { lock (_lock) return _inFlight.Contains(key) || _ready.ContainsKey(key); }

    /// <summary>Starts preparing <paramref name="key"/>'s surfaces unless that is already asked for or ready; when
    /// the cap is reached, queues it (it starts when a running one finishes, in the order asked).</summary>
    public void Request((Guid Asset, int Lod) key, IReadOnlyList<MeshFace> faces) => Request(key, () => MeshSurfaceData.Prepare(faces));

    /// <summary>As <see cref="Request((Guid, int), IReadOnlyList{MeshFace})"/>, for work other than a plain mesh's surfaces (a rigged mesh's, with its table):
    /// <paramref name="prepare"/> runs on a worker and must only read; a throw is a failed preparation (null).</summary>
    public void Request((Guid Asset, int Lod) key, Func<PreparedMeshLod?> prepare)
    {
        int generation;
        CancellationToken token;
        lock (_lock)
        {
            if (_inFlight.Contains(key) || _ready.ContainsKey(key)) return;
            _inFlight.Add(key);
            generation = _generation;
            token = _life.Token;
            if (_cap > 0 && _running >= _cap)
            {
                _queued.Enqueue((key, prepare, generation, token));
                return;
            }
            _peakRunning = Math.Max(_peakRunning, ++_running);
        }
        Start(key, prepare, generation, token);
    }

    /// <summary>Runs one preparation on the thread pool (its place already counted in <see cref="Running"/>); when it
    /// finishes, the next queued one of the current generation takes its place.</summary>
    private void Start((Guid Asset, int Lod) key, Func<PreparedMeshLod?> prepare, int generation, CancellationToken token)
    {
        var gate = Gate;
        _ = Task.Run(async () =>
        {
            PreparedMeshLod? prepared = null;
            bool cancelled = false;
            try
            {
                if (gate is not null) await gate(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var alloc = AllocationMeter.Shared.Begin(AllocKind.MeshPrepare);   // (measurement only)
                try { prepared = prepare(); }
                finally { alloc.End(); }
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception) { prepared = null; }
            bool arrived = false;
            lock (_lock)
            {
                if (generation == _generation && !cancelled && _inFlight.Remove(key))
                {
                    _ready[key] = prepared;
                    if (prepared is not null)
                    {
                        _heldBytes += prepared.Bytes;
                        _peakHeldBytes = Math.Max(_peakHeldBytes, _heldBytes);
                        Interlocked.Increment(ref _prepared);
                    }
                    else Interlocked.Increment(ref _failed);
                    // announced in the same step it is recorded: whatever Held counts has arrived, and a Clear
                    // (which empties both under this lock) can never be followed by an arrival from before it
                    _arrived.Enqueue(key);
                    arrived = true;
                }
                else Interlocked.Increment(ref _discarded);
            }
            if (arrived) AfterRecord?.Invoke(key);
            ((Guid Asset, int Lod) Key, Func<PreparedMeshLod?> Prepare, int Generation, CancellationToken Token) next = default;
            bool more = false;
            lock (_lock)
            {
                _running--;
                if (_queued.Count > 0 && (_cap == 0 || _running < _cap))
                {
                    next = _queued.Dequeue();
                    _peakRunning = Math.Max(_peakRunning, ++_running);
                    more = true;
                }
            }
            if (more) Start(next.Key, next.Prepare!, next.Generation, next.Token);
        });
    }

    /// <summary>A preparation that finished since the last call (it may have been dropped or cleared since).</summary>
    public bool TryTakeArrival(out (Guid Asset, int Lod) key) => _arrived.TryDequeue(out key);

    /// <summary>Takes <paramref name="key"/>'s prepared surfaces: true when it was ready (null when its preparation failed:
    /// the host then builds the LOD itself).</summary>
    public bool TryTake((Guid Asset, int Lod) key, out PreparedMeshLod? prepared)
    {
        lock (_lock)
        {
            if (!_ready.Remove(key, out prepared)) return false;
            if (prepared is not null) _heldBytes -= prepared.Bytes;
            return true;
        }
    }

    /// <summary>Lets <paramref name="key"/>'s prepared surfaces go (no object wants that LOD any more).</summary>
    public void Drop((Guid Asset, int Lod) key)
    {
        lock (_lock)
        {
            if (!_ready.Remove(key, out var prepared)) return;
            if (prepared is not null) _heldBytes -= prepared.Bytes;
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Forgets everything asked for and ready (a region change): nothing is in flight or held afterwards, and a
    /// preparation still running throws its result away.</summary>
    public void Clear()
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            _generation++;
            _inFlight.Clear();
            _queued.Clear();   // nothing waiting outlives its region either
            _ready.Clear();
            _heldBytes = 0;
            while (_arrived.TryDequeue(out _)) { }
            old = _life;
            _life = new CancellationTokenSource();
        }
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
