// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Concurrent;
using System.Threading;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The script backtraces the engine hands the logger with every engine error. GodotSharp 4.7.2 passes them to
/// <c>Logger._LogError</c> as a new managed <c>Godot.Collections.Array</c> that holds its own reference to the engine's
/// array, and through it to one <see cref="ScriptBacktrace"/> per script language (GDScript and C#: two engine objects per
/// error). Nothing disposed that array, so the two backtraces stayed alive until a collection found the array and the
/// finalizer thread destroyed it, through the unpinned destroy that <see cref="KeptCollections"/> describes (the crash
/// class the kept collections and the pinned Variant disposal close).
/// <para>Here the logger hands each array over as soon as it is called (<see cref="Hand"/>), from whichever thread logged
/// the error: the engine logs from the main thread, the render thread and worker threads alike. Handing over only queues
/// the array, still referenced, so it is never finalized. The main thread disposes it while it is pinned: the frame's
/// bounded drain (<see cref="DrainFrame"/>, from <c>Main._Process</c>) and the full drains of a region change, a layer's
/// dispose and shutdown (<see cref="DrainAll"/>, from <see cref="VariantDisposal.DrainAll"/>). Nothing is disposed on the
/// logging thread and nothing is destroyed through an unpinned path.</para>
/// </summary>
internal static class LoggedErrorReleases
{
    private static readonly ConcurrentQueue<global::Godot.Collections.Array> Inbox = new();
    private static DeferredDisposals<global::Godot.Collections.Array>? _queue;
    private static int _mainThread = -1;
    private static long _handed, _handedOffMain;
    private static bool Draining;

    /// <summary>Arrays handed over, those handed over from a thread other than the main thread, and arrays disposed
    /// outside a main-thread drain (the harness check wants 0).</summary>
    public static long Handed => Interlocked.Read(ref _handed);
    public static long HandedOffMain => Interlocked.Read(ref _handedOffMain);
    public static long DisposalsOutsideDrain { get; private set; }
    /// <summary>Arrays disposed by the main thread's drains.</summary>
    public static long Disposed => _queue?.Disposed ?? 0;
    /// <summary>Arrays handed over and not yet disposed.</summary>
    public static int Waiting => Inbox.Count + (_queue?.Count ?? 0);

    /// <summary>Test only (the harness check's negative control): the logger hands nothing over, as before this
    /// release existed, so every backtrace waits for a finalizer. Never set in the viewer.</summary>
    public static bool KeepForTest { get; set; }

    /// <summary>At startup, on the main thread, before the logger is registered: the main thread recorded and the queue
    /// made. Idempotent.</summary>
    public static void Prepare()
    {
        if (_queue is not null) return;
        _mainThread = System.Environment.CurrentManagedThreadId;
        _queue = new DeferredDisposals<global::Godot.Collections.Array>(DisposePinned);
    }

    /// <summary>From the logger, on any thread: the error's backtraces queued for the main thread's drain.</summary>
    public static void Hand(global::Godot.Collections.Array<ScriptBacktrace>? backtraces)
    {
        if (backtraces is null || KeepForTest) return;
        Inbox.Enqueue((global::Godot.Collections.Array)backtraces);
        Interlocked.Increment(ref _handed);
        if (System.Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _handedOffMain);
    }

    /// <summary>Once per frame, main thread: a bounded drain (the <see cref="DeferredDisposals{T}"/> slice).</summary>
    public static DisposalDrain DrainFrame()
    {
        if (Inbox.IsEmpty && (_queue is null || _queue.Count == 0)) return default;
        var q = Take();
        Draining = true;
        try { return q.DrainFrame(); }
        finally { Draining = false; }
    }

    /// <summary>Main thread: everything handed over so far disposed now.</summary>
    public static DisposalDrain DrainAll()
    {
        if (Inbox.IsEmpty && (_queue is null || _queue.Count == 0)) return default;
        var q = Take();
        Draining = true;
        try { return q.DrainAll(); }
        finally { Draining = false; }
    }

    /// <summary>What the releases did (test renders' sidecars).</summary>
    public static object Report() => new
    {
        handed = Handed,
        handed_off_main = HandedOffMain,
        disposed = _queue?.Disposed ?? 0,
        waiting = Waiting,
        errors = _queue?.Errors ?? 0,
        last_error = _queue?.LastError,
        disposals_outside_drain = DisposalsOutsideDrain,
        drain_frames = _queue?.DrainFrames ?? 0,
        frame_ms_max = Math.Round(_queue?.MaxFrameMs ?? 0, 4),
        value_ms_max = Math.Round(_queue?.MaxValueMs ?? 0, 4),
        full_drain_ms_max = Math.Round(_queue?.MaxFullDrainMs ?? 0, 4),
    };

    // main thread only (the queue checks its owner): what the logger handed over moves into the bounded queue
    private static DeferredDisposals<global::Godot.Collections.Array> Take()
    {
        if (_queue is null) Prepare();
        while (Inbox.TryDequeue(out var a)) _queue!.Add(a, 0);
        return _queue!;
    }

    /// <summary>Disposes the array while its managed wrapper is pinned, so the engine's destroy reads a fixed
    /// address for the whole call; <c>Dispose</c> also suppresses its finalizer.</summary>
    private static unsafe void DisposePinned(global::Godot.Collections.Array array)
    {
        if (!Draining) DisposalsOutsideDrain++;
        fixed (byte* p = &KeptCollections.Pin(array))
            array.Dispose();
    }
}
