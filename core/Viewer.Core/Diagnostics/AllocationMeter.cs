// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Diagnostics;

/// <summary>The kinds of work whose managed allocations are counted. The first five
/// are main-thread upload units (PrimLayer's unit records); the next five run on workers; <see cref="Other"/> is a
/// large buffer made outside any counted work.</summary>
public enum AllocKind { Texture, Prim, Sculpt, Mesh, Terrain, TextureDecode, TexturePrepare, MeshDecode, MeshPrepare, TerrainPrepare, MorphPrepare, RiggedPrepare, Other }

/// <summary>
/// Managed bytes allocated inside each kind of work, from
/// the running thread's own counter (<see cref="GC.GetAllocatedBytesForCurrentThread"/>) read at the work's start and end.
/// Counted apart for the main thread (<see cref="MarkMainThread"/>) and every other thread, as running totals the harness
/// reads once a frame and differences ("finished in this frame"). Bytes are exclusive: work counted inside other counted
/// work on the same thread (a mesh decoded inline inside a prim unit) is taken out of the outer one, so the kinds add up.
/// <see cref="Large"/> adds the size of a buffer the code makes itself that lands on the large-object heap (85,000 bytes
/// or more) to the kind running on that thread. Cost: two reads of the thread's counter, three interlocked adds and two
/// thread-static writes per counted piece of work; nothing here allocates (the totals are fixed arrays made once), except
/// <see cref="NoteThread"/>, once per dedicated thread when it starts.
/// </summary>
public sealed class AllocationMeter
{
    /// <summary>Arrays of this many bytes or more go on the large-object heap (the runtime's documented threshold).</summary>
    public const int LargeObjectBytes = 85_000;
    public static readonly int Kinds = Enum.GetValues<AllocKind>().Length;

    /// <summary>The meter the viewer's code counts into.</summary>
    public static AllocationMeter Shared { get; } = new();

    // [0..Kinds) the main thread, [Kinds..2*Kinds) every other thread
    private readonly long[] _bytes = new long[2 * Kinds];
    private readonly long[] _items = new long[2 * Kinds];
    private readonly long[] _large = new long[2 * Kinds];
    private int _mainThread = -1;

    // the counted work running on this thread: its meter and kind, and the bytes of counted work nested inside it so far
    [ThreadStatic] private static AllocationMeter? t_meter;
    [ThreadStatic] private static int t_kind;
    [ThreadStatic] private static long t_nested;

    /// <summary>The calling thread is the main thread from now on (its counted work is counted apart).</summary>
    public void MarkMainThread()
    {
        Volatile.Write(ref _mainThread, Environment.CurrentManagedThreadId);
        NoteThread("main");
    }

    // the operating system's id of each noted thread and its role (the runtime's allocation samples name the OS thread)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _roles = new();

    /// <summary>Notes the calling thread's role (once, when a dedicated thread starts), so a runtime allocation sample can be
    /// placed by its thread (<see cref="RoleOf"/>). Windows only (elsewhere nothing is noted).</summary>
    public void NoteThread(string role)
    {
        if (OperatingSystem.IsWindows()) _roles[GetCurrentThreadId()] = role;
    }

    /// <summary>The noted role of an operating-system thread id, or null.</summary>
    public string? RoleOf(long osThreadId) => _roles.TryGetValue(osThreadId, out var r) ? r : null;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public bool OnMainThread => Environment.CurrentManagedThreadId == Volatile.Read(ref _mainThread);

    /// <summary>Starts counting <paramref name="kind"/> on this thread; end it with <see cref="Scope.End"/> on the same thread
    /// (never across an await).</summary>
    public Scope Begin(AllocKind kind)
    {
        var s = new Scope(this, kind, GC.GetAllocatedBytesForCurrentThread(), t_meter, t_kind, t_nested);
        t_meter = this;
        t_kind = (int)kind;
        t_nested = 0;
        return s;
    }

    /// <summary>A buffer of <paramref name="bytes"/> the code made itself: counted when it is a large object, under the
    /// counted work running on this thread (<see cref="AllocKind.Other"/> outside any).</summary>
    public static void Large(long bytes)
    {
        if (bytes < LargeObjectBytes) return;
        var m = t_meter ?? Shared;
        Interlocked.Add(ref m._large[m.Row() + (t_meter is null ? (int)AllocKind.Other : t_kind)], bytes);
    }

    /// <summary>The same, under a named kind (a buffer made just before its work starts on this thread).</summary>
    public void Large(long bytes, AllocKind kind)
    {
        if (bytes >= LargeObjectBytes) Interlocked.Add(ref _large[Row() + (int)kind], bytes);
    }

    private int Row() => OnMainThread ? 0 : Kinds;

    /// <summary>Copies the running totals (main thread first, then every other thread; <see cref="Kinds"/> each) into the
    /// caller's arrays of 2 x <see cref="Kinds"/>. Allocates nothing.</summary>
    public void Read(long[] bytes, long[] items, long[] large)
    {
        for (int i = 0; i < _bytes.Length; i++)
        {
            bytes[i] = Interlocked.Read(ref _bytes[i]);
            items[i] = Interlocked.Read(ref _items[i]);
            large[i] = Interlocked.Read(ref _large[i]);
        }
    }

    public long BytesOf(AllocKind kind, bool mainThread) => Interlocked.Read(ref _bytes[(mainThread ? 0 : Kinds) + (int)kind]);
    public long ItemsOf(AllocKind kind, bool mainThread) => Interlocked.Read(ref _items[(mainThread ? 0 : Kinds) + (int)kind]);
    public long LargeOf(AllocKind kind, bool mainThread) => Interlocked.Read(ref _large[(mainThread ? 0 : Kinds) + (int)kind]);

    /// <summary>One piece of counted work in progress (a value, nothing allocated).</summary>
    public readonly struct Scope
    {
        private readonly AllocationMeter? _meter;
        private readonly AllocKind _kind;
        private readonly long _start;
        private readonly AllocationMeter? _outerMeter;
        private readonly int _outerKind;
        private readonly long _outerNested;

        internal Scope(AllocationMeter meter, AllocKind kind, long start, AllocationMeter? outerMeter, int outerKind, long outerNested)
        {
            _meter = meter; _kind = kind; _start = start; _outerMeter = outerMeter; _outerKind = outerKind; _outerNested = outerNested;
        }

        /// <summary>Ends the work: adds its own bytes (less the counted work nested in it) to its kind and returns them.</summary>
        public long End()
        {
            if (_meter is null) return 0;
            long all = GC.GetAllocatedBytesForCurrentThread() - _start;
            long own = all - t_nested;
            int i = _meter.Row() + (int)_kind;
            Interlocked.Add(ref _meter._bytes[i], own);
            Interlocked.Increment(ref _meter._items[i]);
            t_meter = _outerMeter;
            t_kind = _outerKind;
            t_nested = _outerMeter is null ? 0 : _outerNested + all;   // the outer work's nested bytes include all of this one
            return own;
        }
    }
}
