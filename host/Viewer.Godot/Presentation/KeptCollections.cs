// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Runtime.CompilerServices;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The Godot collections the viewer gives the engine, one small set per site,
/// made once on the main thread and kept for the process: cleared and reused after each engine call, never disposed and
/// never finalized while the process runs (a region change or a layer's dispose does not destroy them either).
/// GodotSharp 4.7.2 hands the engine an unpinned pointer into a collection's managed wrapper on every mutating call
/// (<c>IsReadOnly</c> and <c>Count</c>, read by every setter, <c>Resize</c> and <c>Clear</c>) and in its <c>Dispose</c> and
/// finalizer, so a compacting collection on another thread during such a call makes the engine read, and the binding
/// write, the wrapper's old address. Two rules close that: (1) a kept collection is never destroyed, so neither
/// <c>Dispose</c> nor the finalizer ever runs on it; (2) every operation on it, and every engine call that takes it, runs
/// while the wrapper is pinned (<see cref="Pin"/> under <c>fixed</c>), so nothing can move it during the call.
/// Main thread only. What the sets hold between calls: the wrappers and their empty native collections (about a dozen),
/// no surface data: <c>Clear</c> drops the engine's references to a surface's packed arrays as soon as the call returns
/// (the engine's mesh keeps its own copy).
/// All six sets are made at startup, before the first frame
/// (<see cref="PrepareAtStartup"/>), not at a site's first use, so no timed upload unit pays for making them.
/// Every surface the viewer draws (mesh LODs, prims, sculpts, terrain,
/// water) is handed over as prepared bytes through <c>PrimLayer.HandOver(mesh, surface)</c>, so the one engine surface
/// call on the shipping paths is <c>RenderingServer.MeshAddSurface</c> with the handover's dictionary; the five array
/// sets of the <c>MeshAddSurfaceFromArrays</c> sites (prims, surfaces, water, server terrain, node terrain) had no
/// shipping site left and were removed. The node terrain view is the harness's now and keeps its own set there.
/// </summary>
internal static class KeptCollections
{
    /// <summary>The surface dictionary of <c>RenderingServer.MeshAddSurface</c> (handover).</summary>
    internal sealed class SurfaceDictionary
    {
        public readonly global::Godot.Collections.Dictionary Surface = new();

        public void Clear()
        {
            if (SkipClearForTest) return;
            Surface.Clear();
        }
    }

    private static SurfaceDictionary? _handover;

    /// <summary>Every surface's handover (<c>PrimLayer.HandOver</c>): mesh LODs, prims, sculpts, terrain, water.</summary>
    public static SurfaceDictionary Handover
    {
        get
        {
            if (!FreshForTest && _handover is null) MadeLate++;
            return FreshForTest || _handover is null ? _handover = new SurfaceDictionary() : _handover;
        }
    }

    /// <summary>The frame at which the startup setup made every site's set (null: not
    /// made at startup), and the sets a site had to make itself later, inside its call (the harness check wants 0).</summary>
    public static long? PreparedAtFrame { get; private set; }
    public static int MadeLate { get; private set; }
    /// <summary>What the startup setup took (ms), so it is seen and never inside a timed unit.</summary>
    public static double SetupMs { get; private set; }

    /// <summary>
    /// At startup, on the main thread, before the first frame
    /// (<c>Main._Ready</c>): the Variant disposal's getter built and its queue made (or the fallback logged once), every
    /// site's kept set made (the handover's alone), and the engine names made, so no timed upload unit pays any of it (the heavy scene once paid
    /// it in its first prim build). Idempotent.
    /// </summary>
    public static void PrepareAtStartup()
    {
        if (PreparedAtFrame is not null) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        VariantDisposal.Prepare();
        RuntimeHelpers.RunClassConstructor(typeof(EngineNames).TypeHandle);
        _handover ??= new SurfaceDictionary();
        PreparedAtFrame = (long)global::Godot.Engine.GetProcessFrames();
        SetupMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>The sets made so far (the harness check, <c>--tr-keptcollections</c>).</summary>
    public static IEnumerable<(string Site, global::Godot.Collections.Array? Arrays, global::Godot.Collections.Dictionary? Dictionary, object Wrapper)> Made()
    {
        if (_handover is not null) yield return ("handover", null, _handover.Surface, _handover);
    }

    /// <summary>Test only (the harness check's negative controls): a new set at every use, or no clearing after the call.
    /// Never set in the viewer.</summary>
    public static bool FreshForTest { get; set; }
    public static bool SkipClearForTest { get; set; }

    private sealed class RawData { public byte Data; }

    /// <summary>
    /// A reference to <paramref name="o"/>'s first instance field, for <c>fixed</c>: <c>fixed (byte* p = &amp;Pin(x))</c>
    /// pins the whole object <c>x</c> for the scope (the GC treats a pinned interior pointer as pinning the object that
    /// contains it; the runtime's own <c>RuntimeHelpers.GetRawData</c> takes the same reference). Only its address is
    /// used, never its contents. <c>GCHandle</c> pinning is refused for these wrappers (they have reference fields).
    /// </summary>
    public static ref byte Pin(object o) => ref Unsafe.As<RawData>(o).Data;
}
