// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using Viewer.Core.Diagnostics;

namespace Viewer.Core.Imaging;

/// <summary>A decode ran past its deadline; its thread was written off (<see cref="DecodeScheduler"/>).</summary>
public sealed class DecodeDeadlineException(TimeSpan deadline) : Exception($"decode ran past its {deadline.TotalSeconds:F0} s deadline");

/// <summary>Every decode thread has been written off and the cap allows no more: decoding has stopped for this process.</summary>
public sealed class DecodeStoppedException() : Exception("texture decoding stopped: every decode thread overran and the cap was reached");

/// <summary>
/// Containment for a decode the guard (<see cref="J2kGuard"/>) does not catch. Texture decodes run on
/// a small set of dedicated threads (not the shared thread pool, so a stuck decode cannot starve the rest of the viewer),
/// each with a deadline. A decode past its deadline fails at once (its texture shows its placeholder) and is logged once.
/// A running decode cannot be stopped in .NET, so its thread is written off (left to finish or spin on its own, its result
/// thrown away) and a new thread takes its place, up to <see cref="MaxWrittenOff"/> write-offs. Past the cap no thread is
/// replaced: the remaining threads carry on, and once none is left every queued and later decode fails at once
/// (<see cref="DecodeStoppedException"/>, logged once as an error) while the rest of the viewer keeps running.
/// </summary>
public sealed class DecodeScheduler : IDisposable
{
    private sealed class Job(Func<object?> work, string what)
    {
        public readonly Func<object?> Work = work;
        public readonly string What = what;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The work's result, handed over once (<see cref="TakeResult"/>) and not kept, so a
        /// finished job that is still referenced somewhere (a decode thread waiting for its next job kept its last one
        /// reachable, measured) holds no decoded level.</summary>
        private object? _result;

        public void Complete(object? result)
        {
            Volatile.Write(ref _result, result);
            Done.TrySetResult();
        }

        public object? TakeResult() => Interlocked.Exchange(ref _result, null);
    }

    private sealed class Worker
    {
        public Job? Current;
        public long Started;
        public bool WrittenOff;
    }

    private readonly BlockingCollection<Job> _queue = new();
    private readonly List<Worker> _workers = new();
    private readonly object _lock = new();
    private readonly Timer _watch;
    private int _created, _writtenOff;
    private bool _stoppedLogged;

    /// <summary>Threads: the logical cores less two (the main and render threads), 1 to 16: close to the concurrency the
    /// shared pool gave before (measured: 8 threads delayed first images on the region A v5 path).</summary>
    public static int DefaultThreads(int cores) => Math.Clamp(cores - 2, 1, 16);

    /// <summary>30 s: this machine's worst legitimate 2048x2048 full-resolution decode is under 1 s, so a
    /// hardware-floor machine several times slower, with every decode thread busy, stays far below it.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);

    /// <summary>The process-wide texture decoder (the host sets <see cref="Log"/> at startup).</summary>
    public static DecodeScheduler Textures { get; } = new(DefaultThreads(Environment.ProcessorCount), DefaultDeadline, DefaultThreads(Environment.ProcessorCount));

    public DecodeScheduler(int threads, TimeSpan deadline, int maxWrittenOff, CoreLog? log = null)
    {
        if (threads < 1) throw new ArgumentOutOfRangeException(nameof(threads));
        Threads = threads;
        Deadline = deadline;
        MaxWrittenOff = maxWrittenOff;
        Log = log;
        for (int i = 0; i < threads; i++) StartWorker();
        var period = TimeSpan.FromMilliseconds(Math.Clamp(deadline.TotalMilliseconds / 4, 10, 1000));
        _watch = new Timer(_ => Watch(), null, period, period);
    }

    public int Threads { get; }
    public TimeSpan Deadline { get; }
    public int MaxWrittenOff { get; }
    public CoreLog? Log { get; set; }

    /// <summary>Threads written off so far (each one overran its deadline).</summary>
    public int WrittenOff { get { lock (_lock) return _writtenOff; } }
    /// <summary>Threads still taking work.</summary>
    public int Live { get { lock (_lock) return _workers.Count(w => !w.WrittenOff); } }
    /// <summary>Threads started in all (the first set plus replacements).</summary>
    public int Created { get { lock (_lock) return _created; } }
    public bool Stopped { get { lock (_lock) return _workers.All(w => w.WrittenOff); } }

    /// <summary>Runs <paramref name="work"/> on a decode thread. Faults with <see cref="DecodeDeadlineException"/> past the
    /// deadline, <see cref="DecodeStoppedException"/> once decoding has stopped; otherwise the work's result or exception.
    /// <paramref name="what"/> names it in the log (an asset id, never bytes).</summary>
    public async Task<T> Run<T>(Func<T> work, string what)
    {
        var job = new Job(() => work(), what);
        lock (_lock)
        {
            if (_workers.All(w => w.WrittenOff)) throw new DecodeStoppedException();
            _queue.Add(job);
        }
        await job.Done.Task.ConfigureAwait(false);
        return (T)job.TakeResult()!;
    }

    private void StartWorker()
    {
        var w = new Worker();
        int n;
        lock (_lock) { _workers.Add(w); n = ++_created; }
        new Thread(() => Loop(w)) { IsBackground = true, Name = $"texture-decode-{n}" }.Start();
    }

    private void Loop(Worker w)
    {
        Diagnostics.AllocationMeter.Shared.NoteThread("texture_decode");   // (measurement only)
        try
        {
            // each job runs in its own method and hands its result over once (Job.Complete), so
            // nothing on this thread keeps the last job's result (a decoded level) while it waits for the next. Measured:
            // with the loop's own locals, or with the result kept in the job's task, an idle decode thread kept its last
            // level reachable after the texture source had let it go.
            while (RunOne(w, _queue.Take())) { }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Runs one job on worker <paramref name="w"/>; false when the worker was written off (it overran). Not inlined,
    /// so its locals end with it.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool RunOne(Worker w, Job job)
    {
        lock (_lock) { w.Current = job; w.Started = Stopwatch.GetTimestamp(); }
        object? result = null;
        Exception? error = null;
        // the managed bytes each decode allocates on this thread
        var alloc = Diagnostics.AllocationMeter.Shared.Begin(Diagnostics.AllocKind.TextureDecode);
        try { result = job.Work(); }
        catch (Exception e) { error = e; }
        finally { alloc.End(); }
        lock (_lock)
        {
            w.Current = null;
            if (w.WrittenOff) return false;   // it overran: the job already failed and another thread took this one's place
        }
        if (error is not null) job.Done.TrySetException(error);
        else job.Complete(result);
        return true;
    }

    private void Watch()
    {
        var failed = new List<(Job Job, double Ms, int WrittenOff, bool Replaced)>();
        bool stoppedNow = false;
        List<Job>? drained = null;
        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            foreach (var w in _workers.ToList())
            {
                if (w.WrittenOff || w.Current is not { } job) continue;
                double ms = (now - w.Started) * 1000.0 / Stopwatch.Frequency;
                if (ms <= Deadline.TotalMilliseconds) continue;
                w.WrittenOff = true;
                bool replaced = ++_writtenOff <= MaxWrittenOff;
                if (replaced) StartWorker();   // the lock is re-entrant
                failed.Add((job, ms, _writtenOff, replaced));
            }
            if (failed.Count == 0) return;
            if (_workers.All(w => w.WrittenOff) && !_stoppedLogged)
            {
                _stoppedLogged = stoppedNow = true;
                drained = new List<Job>();
                while (_queue.TryTake(out var q)) drained.Add(q);
            }
        }
        foreach (var (job, ms, n, replaced) in failed)
        {
            job.Done.TrySetException(new DecodeDeadlineException(Deadline));
            Log?.Warn("texture.decode.deadline", CoreLog.F("asset", job.What), CoreLog.F("ms", (long)ms), CoreLog.F("written_off", n), CoreLog.F("cap", MaxWrittenOff), CoreLog.F("replaced", replaced));
        }
        if (stoppedNow)
        {
            foreach (var q in drained!) q.Done.TrySetException(new DecodeStoppedException());
            Log?.Error("texture.decode.stopped", null, CoreLog.F("written_off", WrittenOff), CoreLog.F("cap", MaxWrittenOff));
        }
    }

    public void Dispose()
    {
        _watch.Dispose();
        _queue.CompleteAdding();
    }
}
