// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;

namespace Viewer.Shell;

/// <summary>What one drain did.</summary>
public readonly record struct DisposalDrain(int Values, long Bytes, double Ms, bool Behind, bool Forced);

/// <summary>
/// Values an engine call used, held (so never finalized) until the drain after the frame's
/// upload budget disposes them, on the thread that made the queue (the main thread). Oldest first, each exactly once.
/// <para>The bound: <see cref="DrainFrame"/> checks the time before each value and stops once its slice is spent:
/// <see cref="SliceMs"/> (0.5 ms), or <see cref="BehindSliceMs"/> (0.9 ms) while the queue is behind (more than
/// <see cref="BehindBytes"/> or <see cref="BehindCount"/> held). Both are under 1 ms so that the one value started just
/// before the slice ends still leaves the frame's drain under 1 ms. A frame always disposes at least one value, so the
/// queue always moves.</para>
/// <para>What a frame's drain controls is the disposals it chooses to run: the values it starts (none after its slice
/// is spent, <see cref="LateStarts"/>) and their own times (<see cref="MaxFrameValueMs"/>, the largest sum in one frame).
/// Its wall time (<see cref="MaxFrameMs"/>) also holds whatever stalled the thread between values (preemption, a
/// collection), which no drain can bound, so it is reported, with the drains that had a collection in them, not used as
/// the bound.</para>
/// <para>Growth: past <see cref="HardCapBytes"/> held, a drain goes on, whatever its time, until the queue is back under
/// the behind mark; that is counted (<see cref="ForcedDrains"/>) so it is never silent. <see cref="DrainAll"/> (a region
/// change, a layer's dispose, shutdown) empties it.</para>
/// No Godot types (Viewer.Shell); the Godot side passes the disposal.
/// </summary>
public sealed class DeferredDisposals<T>
{
    private readonly Queue<(T Value, long Bytes)> _queue = new();
    private readonly Action<T> _dispose;
    private readonly Func<long> _now;
    private readonly double _tickMs;
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly List<double> _frameMs = new();
    private const int FrameSamples = 1 << 16;

    /// <param name="clock">Ticks (tests pass a fake); default <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="ticksPerSecond">The clock's frequency; default <see cref="Stopwatch.Frequency"/>.</param>
    public DeferredDisposals(Action<T> dispose, Func<long>? clock = null, long ticksPerSecond = 0)
    {
        _dispose = dispose;
        _now = clock ?? Stopwatch.GetTimestamp;
        _tickMs = 1000.0 / (ticksPerSecond > 0 ? ticksPerSecond : Stopwatch.Frequency);
    }

    public double SliceMs { get; set; } = 0.5;
    public double BehindSliceMs { get; set; } = 0.9;
    public long BehindBytes { get; set; } = 32L << 20;
    public int BehindCount { get; set; } = 4096;
    public long HardCapBytes { get; set; } = 256L << 20;

    public int Count => _queue.Count;
    public long HeldBytes { get; private set; }
    public long Queued { get; private set; }
    public long Disposed { get; private set; }
    public long BytesDisposed { get; private set; }
    public int PeakCount { get; private set; }
    public long PeakBytes { get; private set; }
    /// <summary>Frames whose drain disposed something, and the ones among them that ran behind or forced.</summary>
    public long DrainFrames { get; private set; }
    public long FramesBehind { get; private set; }
    public long ForcedDrains { get; private set; }
    public long FullDrains { get; private set; }
    /// <summary>The longest frame drain, the longest single value, and the longest full drain (ms).</summary>
    public double MaxFrameMs { get; private set; }
    public double MaxValueMs { get; private set; }
    public double MaxFullDrainMs { get; private set; }
    /// <summary>The largest sum, in one frame's drain, of the disposals' own times (ms): the frame time the drain
    /// controls.</summary>
    public double MaxFrameValueMs { get; private set; }
    /// <summary>Values a frame's drain started after its slice was spent (0 while the slice is enforced; forced drains and
    /// a frame's first value excepted).</summary>
    public long LateStarts { get; private set; }
    /// <summary>Frame drains whose wall time went over 1 ms while their disposals' own times did not (the thread stalled
    /// between values), and frame drains a garbage collection ran in.</summary>
    public long StalledFrames { get; private set; }
    public long DrainsWithCollection { get; private set; }

    /// <summary>Test only (the render harness's drain controls): run inside each value's timed disposal (a slowed
    /// drain), after each value outside its time (a stalled thread), and a drain that ignores its slice. Off in the viewer.</summary>
    public Action? InsideValueForTest { get; set; }
    public Action? AfterValueForTest { get; set; }
    public bool IgnoreSliceForTest { get; set; }
    public long Errors { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>The 95th percentile of the frame drains that disposed something (the first 65,536 of them).</summary>
    public double FrameMsP95
    {
        get
        {
            if (_frameMs.Count == 0) return 0;
            var s = _frameMs.ToArray();
            Array.Sort(s);
            return s[Math.Min(s.Length - 1, (int)Math.Ceiling(s.Length * 0.95) - 1)];
        }
    }

    /// <summary>Holds <paramref name="value"/> (its native size <paramref name="bytes"/>) until a drain disposes it.</summary>
    public void Add(T value, long bytes)
    {
        Owner();
        _queue.Enqueue((value, bytes));
        Queued++;
        HeldBytes += bytes;
        PeakCount = Math.Max(PeakCount, _queue.Count);
        PeakBytes = Math.Max(PeakBytes, HeldBytes);
    }

    /// <summary>After the frame's upload budget: disposes values until the slice is spent (see the class notes).</summary>
    public DisposalDrain DrainFrame()
    {
        Owner();
        if (_queue.Count == 0) return default;
        long start = _now();
        bool forced = HeldBytes > HardCapBytes;
        bool behind = !forced && (HeldBytes > BehindBytes || _queue.Count > BehindCount);
        double slice = behind ? BehindSliceMs : SliceMs;
        int values = 0;
        long bytes = 0;
        double valueMs = 0;
        int collections = GC.CollectionCount(0);
        while (_queue.Count > 0)
        {
            if (forced)
            {
                if (HeldBytes <= BehindBytes) break;
            }
            else if (values > 0 && (_now() - start) * _tickMs >= slice)
            {
                if (!IgnoreSliceForTest) break;
                LateStarts++;
            }
            bytes += DisposeNext(out double oneMs);
            valueMs += oneMs;
            values++;
            AfterValueForTest?.Invoke();
        }
        double ms = (_now() - start) * _tickMs;
        DrainFrames++;
        if (behind) FramesBehind++;
        if (forced) ForcedDrains++;
        MaxFrameMs = Math.Max(MaxFrameMs, ms);
        MaxFrameValueMs = Math.Max(MaxFrameValueMs, valueMs);
        if (ms > 1.0 && valueMs <= 1.0) StalledFrames++;
        if (GC.CollectionCount(0) != collections) DrainsWithCollection++;
        if (_frameMs.Count < FrameSamples) _frameMs.Add(ms);
        return new DisposalDrain(values, bytes, ms, behind, forced);
    }

    /// <summary>A region change, a layer's dispose, shutdown: every value disposed now.</summary>
    public DisposalDrain DrainAll()
    {
        Owner();
        if (_queue.Count == 0) return default;
        long start = _now();
        int values = 0;
        long bytes = 0;
        while (_queue.Count > 0) { bytes += DisposeNext(out _); values++; }
        double ms = (_now() - start) * _tickMs;
        FullDrains++;
        MaxFullDrainMs = Math.Max(MaxFullDrainMs, ms);
        return new DisposalDrain(values, bytes, ms, false, false);
    }

    private long DisposeNext(out double ms)
    {
        var (value, bytes) = _queue.Dequeue();
        HeldBytes -= bytes;
        long t0 = _now();
        try
        {
            _dispose(value);
            InsideValueForTest?.Invoke();
            Disposed++;
            BytesDisposed += bytes;
        }
        catch (Exception e)
        {
            Errors++;
            LastError = $"{e.GetType().Name}: {e.Message}";
        }
        ms = (_now() - t0) * _tickMs;
        MaxValueMs = Math.Max(MaxValueMs, ms);
        return bytes;
    }

    private void Owner()
    {
        if (Environment.CurrentManagedThreadId != _owner)
            throw new InvalidOperationException("DeferredDisposals is used only on the thread that made it (the main thread)");
    }
}
