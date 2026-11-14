// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Diagnostics;

namespace Viewer.Godot.Presentation;

/// <summary>
/// Measurement only: how a frame's main-thread time and managed allocation divide between the named steps of the presentation (the
/// avatar presenter and the prim layer). Off in the viewer (<see cref="Enabled"/> false: a section is a branch and nothing else);
/// the render harness turns it on and takes each frame's figures with <see cref="TakeFrame"/>. Sections nest: a section's figure is its
/// own time, without the sections opened inside it, so the figures of one frame add up to the time inside the outermost section.
/// Main thread only.
/// </summary>
public static class FrameProbe
{
    /// <summary>Test renders only; false in the viewer.</summary>
    public static bool Enabled { get; set; }

    /// <summary>True (the default): sections are timed in the main thread's processor time (its cycle counter), so time the thread spends waiting for a
    /// processor, or paused by a collection another thread caused, is not counted in whatever section it fell in. False: wall-clock time.</summary>
    public static bool ThreadCpu { get; set; } = true;

    private static long Now() => ThreadCpu ? CpuMeter.ThreadCycles() : Stopwatch.GetTimestamp();
    private static double TicksPerMs => ThreadCpu ? CpuMeter.CyclesPerMs : Stopwatch.Frequency / 1000.0;

    public const int MaxSections = 64;
    private static readonly string[] _names = new string[MaxSections];
    private static int _count;
    private static readonly long[] _ticks = new long[MaxSections];
    private static readonly long[] _bytes = new long[MaxSections];
    private static readonly int[] _calls = new int[MaxSections];
    // the time and allocation of the sections opened inside the current one, by nesting depth
    private static readonly long[] _childTicks = new long[64];
    private static readonly long[] _childBytes = new long[64];
    private static int _depth;

    /// <summary>A section's id (made once, at class load, by the code that uses it).</summary>
    public static int Register(string name)
    {
        for (int i = 0; i < _count; i++)
            if (_names[i] == name) return i;
        if (_count >= MaxSections) throw new InvalidOperationException("too many frame sections");
        _names[_count] = name;
        return _count++;
    }

    public static int Count => _count;
    public static string NameOf(int id) => _names[id];

    // the steps of the presentation frame
    public static readonly int WpFrame = Register("wp.frame (rest)");
    public static readonly int WpDetails = Register("wp.details");
    public static readonly int WpEnvironment = Register("wp.environment");
    public static readonly int WpDisposal = Register("wp.disposal");
    public static readonly int WpApply = Register("wp.apply (queue, bookkeeping)");
    public static readonly int WpTerrain = Register("wp.terrain");
    public static readonly int PrimResidency = Register("prim.residency");
    public static readonly int PrimStep = Register("prim.step (rest)");
    public static readonly int PrimArrivals = Register("prim.arrivals");
    public static readonly int PrimRigged = Register("prim.rigged mesh build (rest)");
    public static readonly int PrimRiggedHand = Register("prim.rigged hand-over");
    public static readonly int PrimRiggedPrep = Register("prim.rigged prepared here");
    public static readonly int PrimRiggedTable = Register("prim.rigged bind table");
    public static readonly int PrimMeshAsset = Register("prim.mesh asset");
    public static readonly int PrimInstance = Register("prim.instance");
    public static readonly int PrimLight = Register("prim.light");
    public static readonly int PrimMaterialSets = Register("prim.pbr/legacy lookup");
    public static readonly int PrimUsers = Register("prim.texture users");
    public static readonly int PrimFaces = Register("prim.face materials");
    public static readonly int PrimBlend = Register("prim.blend set");
    public static readonly int PrimAttached = Register("prim.attached");
    public static readonly int AvFrame = Register("av.frame (rest)");
    public static readonly int AvApply = Register("av.apply");
    public static readonly int AvFree = Register("av.free departed");
    public static readonly int AvAnimations = Register("av.animations pump");
    public static readonly int AvLayer = Register("av.layer made");
    public static readonly int AvAdd = Register("av.body added (skeleton, instance)");
    public static readonly int AvLabel = Register("av.label");
    public static readonly int AvWorn = Register("av.worn check");
    public static readonly int AvBakes = Register("av.bakes (rest)");
    public static readonly int AvBakeUpload = Register("av.bake upload");
    public static readonly int AvApplyBakes = Register("av.bake materials");
    public static readonly int AvShape = Register("av.shape");
    public static readonly int AvMorph = Register("av.morph hand-over");
    public static readonly int AvClip = Register("av.clip");
    public static readonly int AvPlace = Register("av.place");
    public static readonly int SkinFlushBones = Register("skin.flush bone rows");
    public static readonly int SkinFlushAtlas = Register("skin.flush table atlas");
    public static readonly int SkinResize = Register("skin.atlas resize");
    public static readonly int AvBakeFetch = Register("av.bake fetch (TryGetLevel)");
    public static readonly int AvLayerFrame = Register("av.pose and skeleton (rest)");
    public static readonly int AvPoseEval = Register("av.pose evaluate");
    public static readonly int AvJoints = Register("av.joints and skin rows");
    public static readonly int AvBoneCalls = Register("av.bone engine calls");
    public static readonly int AvInstanceCall = Register("av.instance transform call");
    public static readonly int AvPoseSubmit = Register("av.pose jobs given to workers");
    public static readonly int PrimResBudget = Register("prim.residency video-memory query");
    public static readonly int PrimResUpdate = Register("prim.residency scoring (core)");
    public static readonly int PrimResActions = Register("prim.residency actions (evict, load, level)");
    public static readonly int PrimGeometry = Register("prim.prim/sculpt geometry build");
    public static readonly int AvClipSet = Register("av.clip set (tracks)");
    public static readonly int AvClipFeet = Register("av.clip foot table");
    public static readonly int PrimWorld = Register("prim.object and transform lookup");
    public static readonly int PrimAfter = Register("prim.census and merge bookkeeping");
    public static readonly int PrimRemove = Register("prim.remove (instance, users)");

    /// <summary>An open section; dispose it (or call <see cref="End"/>) to close it.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly int _id;
        private readonly long _t0, _a0;

        internal Scope(int id)
        {
            _id = id;
            _t0 = Now();
            _a0 = GC.GetAllocatedBytesForCurrentThread();
            if (_depth < _childTicks.Length - 1) _depth++;
            _childTicks[_depth] = 0;
            _childBytes[_depth] = 0;
        }

        public void End() => Dispose();

        public void Dispose()
        {
            if (_t0 == 0) return;
            long elapsed = Now() - _t0;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - _a0;
            _ticks[_id] += elapsed - _childTicks[_depth];
            _bytes[_id] += allocated - _childBytes[_depth];
            _calls[_id]++;
            if (_depth > 0) _depth--;
            _childTicks[_depth] += elapsed;
            _childBytes[_depth] += allocated;
        }
    }

    public static Scope Begin(int id) => Enabled ? new Scope(id) : default;

    /// <summary>One frame's figures: each section's own milliseconds, bytes allocated and times entered.</summary>
    public sealed class Frame
    {
        public readonly double[] Ms = new double[MaxSections];
        public readonly long[] Bytes = new long[MaxSections];
        public readonly int[] Calls = new int[MaxSections];
        public double TotalMs;
        public long TotalBytes;
    }

    /// <summary>Takes the figures since the last call and starts the next frame's.</summary>
    public static Frame TakeFrame()
    {
        var f = new Frame();
        for (int i = 0; i < _count; i++)
        {
            f.Ms[i] = _ticks[i] / TicksPerMs;
            f.Bytes[i] = _bytes[i];
            f.Calls[i] = _calls[i];
            f.TotalMs += f.Ms[i];
            f.TotalBytes += f.Bytes[i];
            _ticks[i] = 0; _bytes[i] = 0; _calls[i] = 0;
        }
        _depth = 0;
        return f;
    }
}
