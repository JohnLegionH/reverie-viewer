// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;

namespace Viewer.Shell;

/// <summary>
/// When a dirty terrain chunk may start its preparation off the main thread. A chunk starts
/// only once none of the patches its snapshot reads (its 4 x 4 and the ring around them, <see cref="TerrainGrid.Snapshot"/>)
/// has changed for <see cref="WindowMs"/>, so a chunk whose patches are still arriving is built once they have come, not
/// once per arrival. A chunk dirty for <see cref="CapMs"/> starts anyway (a live terrain edit that keeps changing is never
/// starved). A chunk marked dirty with no patch change (the catch-up, detail textures, a grid resize) is held only by the
/// patch changes it has seen. A chunk also waits while the region's detail textures are still being fetched (their arrival
/// marks every chunk dirty again: on region B v3 that came at 714 ms, and every chunk built before it was built twice).
/// Engine-free; main-thread calls only.
/// </summary>
public sealed class TerrainSettle
{
    /// <summary>The quiet time a chunk's patches need before it starts. From the patch arrival times of the live grid's
    /// captures: the gaps between arrivals to one chunk's read set were p95 168.7 ms, p99 239.1 ms on region B v3
    /// (4,096 patches over 14.2 s) and at most 41.6 ms on region A v5 (256 over 90 ms); 250 ms waits out all but the longest.</summary>
    public const double DefaultWindowMs = 250;
    /// <summary>The longest a dirty chunk waits: a live edit that keeps changing, or detail textures that
    /// are slow or never come, delay a chunk by at most this. On region B v3 the textures arrived at 714 ms (the harness's
    /// texture server, 105 ms per answer).</summary>
    public const double DefaultCapMs = 1000;

    private readonly Func<long> _now;
    private readonly double _msPerTick;
    private readonly long _windowTicks, _capTicks;
    private readonly Dictionary<(int X, int Y), long> _lastChange = new();
    private readonly Dictionary<(int X, int Y), long> _dirtySince = new();
    private readonly List<double> _settleDelayMs = new();
    private readonly List<(double Ms, int PatchX, int PatchY)> _changes = new();
    private readonly List<(double Ms, string Cause)> _allDirty = new();
    private long _origin;

    /// <param name="windowMs">Quiet time needed (0: every chunk starts at once, the earlier behaviour).</param>
    /// <param name="capMs">Longest wait.</param>
    /// <param name="now">Test clock (ticks); null: <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="ticksPerSecond">The test clock's ticks per second; ignored without <paramref name="now"/>.</param>
    public TerrainSettle(double windowMs = DefaultWindowMs, double capMs = DefaultCapMs, Func<long>? now = null, long ticksPerSecond = 0)
    {
        _now = now ?? Stopwatch.GetTimestamp;
        long f = now is null ? Stopwatch.Frequency : ticksPerSecond;
        if (f <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
        _msPerTick = 1000.0 / f;
        WindowMs = Math.Max(0, windowMs);
        CapMs = Math.Max(WindowMs, capMs);
        _windowTicks = (long)(WindowMs / _msPerTick);
        _capTicks = (long)(CapMs / _msPerTick);
        _origin = _now();
    }

    public double WindowMs { get; }
    public double CapMs { get; }
    /// <summary>Chunks started because they waited <see cref="CapMs"/>, their patches still changing (measurement).</summary>
    public int StartedByCap { get; private set; }
    /// <summary>Chunks started (measurement).</summary>
    public int Started { get; private set; }
    /// <summary>Each start's wait, from the chunk's first dirty mark to its start, in ms (the first 8192; measurement).</summary>
    public IReadOnlyList<double> SettleDelayMs => _settleDelayMs;
    /// <summary>Every patch change seen, ms from the region's start (the first 8192; measurement).</summary>
    public IReadOnlyList<(double Ms, int PatchX, int PatchY)> Changes => _changes;
    /// <summary>Chunks waiting now.</summary>
    public int Waiting => _dirtySince.Count;

    /// <summary>Patch (px, py) changed: every chunk whose snapshot reads it waits from now.</summary>
    public void PatchChanged(int px, int py)
    {
        long now = _now();
        if (_changes.Count < 8192) _changes.Add(((now - _origin) * _msPerTick, px, py));
        const int p = TerrainGrid.ChunkPatches;
        // chunk cx reads patches cx*p-1.. cx*p+p (the ring), so patch px is read by chunks (px-1)/p.. (px+1)/p
        for (int cy = Math.Max(0, (py - 1) / p); cy <= (py + 1) / p; cy++)
            for (int cx = Math.Max(0, (px - 1) / p); cx <= (px + 1) / p; cx++)
                _lastChange[(cx, cy)] = now;
    }

    /// <summary>Every chunk is marked dirty for <paramref name="cause"/> (measurement: when and why; the first 256).</summary>
    public void AllDirty(string cause)
    {
        if (_allDirty.Count < 256) _allDirty.Add(((_now() - _origin) * _msPerTick, cause));
    }

    /// <summary>Every whole-terrain dirty mark, ms from the region's start, and its cause (measurement).</summary>
    public IReadOnlyList<(double Ms, string Cause)> AllDirtyMarks => _allDirty;

    /// <summary>Chunk <paramref name="key"/> is dirty (from now, if it was not already waiting).</summary>
    public void Dirty((int X, int Y) key) => _dirtySince.TryAdd(key, _now());

    /// <summary>The chunk may start now: its patches have been quiet for the window and nothing else it is built with is
    /// still arriving (<paramref name="texturesPending"/>: the region's detail textures, whose arrival marks every chunk
    /// dirty again), or it has waited the cap (<paramref name="byCap"/>).</summary>
    public bool Ready((int X, int Y) key, bool texturesPending, out bool byCap)
    {
        byCap = false;
        if (_windowTicks == 0) return true;
        long now = _now();
        bool patchesQuiet = !_lastChange.TryGetValue(key, out long last) || now - last >= _windowTicks;
        if (patchesQuiet && !texturesPending) return true;
        if (_dirtySince.TryGetValue(key, out long since) && now - since >= _capTicks) { byCap = true; return true; }
        return false;
    }

    /// <summary>The chunk started (measurement: its wait; it is no longer waiting).</summary>
    public void Start((int X, int Y) key, bool byCap)
    {
        long now = _now();
        if (_dirtySince.Remove(key, out long since) && _settleDelayMs.Count < 8192) _settleDelayMs.Add((now - since) * _msPerTick);
        Started++;
        if (byCap) StartedByCap++;
    }

    /// <summary>A region change or a new grid: nothing waits, no change is remembered (the measurement goes on).</summary>
    public void Clear(bool newRegion)
    {
        _lastChange.Clear();
        _dirtySince.Clear();
        if (newRegion) _origin = _now();
    }
}
