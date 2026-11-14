// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Viewer.Core.Diagnostics;
using Viewer.Godot.Presentation;

namespace Viewer.Godot;

/// <summary>
/// What each recorded frame of a path run allocated.
/// Per frame: managed bytes in the whole process (<see cref="GC.GetTotalAllocatedBytes"/>, not precise: each thread's open
/// allocation context, a few KB, is counted when it is used up; the precise read suspended the runtime, 37 us a frame) and on the main thread
/// (<see cref="GC.GetAllocatedBytesForCurrentThread"/>); collections by generation (each counted once, at its own
/// generation) and their pause; the main thread's bytes per upload unit kind (the unit records' AllocBytes) and in
/// counted worker kinds run inline on the main thread, the rest of the main thread's bytes; the workers' bytes per kind of
/// work finished in the frame (<see cref="AllocationMeter"/>); large-object bytes per kind where the code makes the
/// buffer; the large-object heap's size before and after the frame's last collection (<see cref="GC.GetGCMemoryInfo"/>).
/// <see cref="Start"/> and <see cref="Stop"/> bracket the frame's work and allocate nothing; <see cref="Record"/> (after
/// <see cref="Stop"/>, outside the window) keeps the frame.
/// </summary>
internal sealed class AllocFrames(AllocationMeter meter)
{
    /// <summary>The main thread's attributed bytes may exceed its measured total by at most this much (the check).</summary>
    public const long ToleranceBytes = 64;

    private static readonly int K = AllocationMeter.Kinds;
    private static readonly AllocKind[] UnitKinds = { AllocKind.Texture, AllocKind.Prim, AllocKind.Sculpt, AllocKind.Mesh, AllocKind.Terrain };
    private static readonly AllocKind[] WorkerKinds = { AllocKind.TextureDecode, AllocKind.TexturePrepare, AllocKind.MeshDecode, AllocKind.MeshPrepare, AllocKind.TerrainPrepare };

    public sealed record Row(string Phase, int Frame, double Ms, double AtS, long Total, long Main, int Gc0, int Gc1, int Gc2, double PauseMs,
        int GcBefore, long StartUtcTicks, long EndUtcTicks, long[] Units, long[] Meter, long[] Large, long LohBefore, long LohAfter, long Rest);

    private readonly long[] _b0 = new long[2 * K], _i0 = new long[2 * K], _l0 = new long[2 * K];
    private readonly long[] _b1 = new long[2 * K], _i1 = new long[2 * K], _l1 = new long[2 * K];
    private long _total0, _total1, _main0, _main1, _utc0, _utc1;
    private int _gc00, _gc10, _gc20, _gc01, _gc11, _gc21;
    private TimeSpan _pause0, _pause1;
    private int _mainThread = -1;
    private long _overheadTicks;   // the time Start and Stop take themselves (their own cost, reported)
    public List<Row> Rows { get; } = new();
    public List<string> Failures { get; } = new();

    /// <summary>The frame's work starts (main thread). Allocates nothing.</summary>
    public void Start()
    {
        long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _mainThread = Environment.CurrentManagedThreadId;
        _utc0 = DateTime.UtcNow.Ticks;
        _gc00 = GC.CollectionCount(0); _gc10 = GC.CollectionCount(1); _gc20 = GC.CollectionCount(2);
        _pause0 = GC.GetTotalPauseDuration();
        meter.Read(_b0, _i0, _l0);
        _total0 = GC.GetTotalAllocatedBytes(false);
        _main0 = GC.GetAllocatedBytesForCurrentThread();
        _overheadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - c0;
    }

    /// <summary>The frame's work ends (main thread, the same as <see cref="Start"/>'s). Allocates nothing.</summary>
    public void Stop()
    {
        _main1 = GC.GetAllocatedBytesForCurrentThread();
        long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _total1 = GC.GetTotalAllocatedBytes(false);
        meter.Read(_b1, _i1, _l1);
        _pause1 = GC.GetTotalPauseDuration();
        _gc01 = GC.CollectionCount(0); _gc11 = GC.CollectionCount(1); _gc21 = GC.CollectionCount(2);
        _utc1 = DateTime.UtcNow.Ticks;
        if (Environment.CurrentManagedThreadId != _mainThread) _mainThread = -2;
        _overheadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - c0;
    }

    /// <summary>Keeps the frame bracketed by the last <see cref="Start"/> and <see cref="Stop"/>: its upload units are
    /// <paramref name="uploads"/> from <paramref name="unitsFrom"/> on.</summary>
    public void Record(string phase, int frame, double ms, double atS, List<(PrimLayer.UploadSample U, string Phase, int Frame)> uploads, int unitsFrom)
    {
        if (_mainThread == -2) Failures.Add($"{phase} frame {frame}: started and stopped on different threads");
        var units = new long[UnitKinds.Length];
        for (int i = unitsFrom; i < uploads.Count; i++)
        {
            var u = uploads[i].U;
            int k = Array.IndexOf(UnitKinds, KindOf(u));
            if (k >= 0) units[k] += u.AllocBytes;
        }
        var meterDelta = new long[2 * K];
        var largeDelta = new long[2 * K];
        for (int i = 0; i < 2 * K; i++) { meterDelta[i] = _b1[i] - _b0[i]; largeDelta[i] = _l1[i] - _l0[i]; }
        long main = _main1 - _main0;
        // the main thread's attributed bytes: its units, and counted worker kinds run inline on it
        long attributed = units.Sum() + WorkerKinds.Sum(k => meterDelta[(int)k]);
        long rest = main - attributed;
        if (rest < -ToleranceBytes) Failures.Add($"{phase} frame {frame}: main thread attributed {attributed} bytes > measured {main}");
        long lohBefore = -1, lohAfter = -1;
        int gcs = _gc01 - _gc00;
        if (gcs > 0)
        {
            var info = GC.GetGCMemoryInfo(GCKind.Any);
            if (info.Index > 0 && info.GenerationInfo.Length > 3) { lohBefore = info.GenerationInfo[3].SizeBeforeBytes; lohAfter = info.GenerationInfo[3].SizeAfterBytes; }
        }
        int g2 = _gc21 - _gc20, g1 = _gc11 - _gc10 - g2, g0 = gcs - g1 - g2;
        Rows.Add(new Row(phase, frame, ms, atS, _total1 - _total0, main, g0, g1, g2, (_pause1 - _pause0).TotalMilliseconds, _gc00, _utc0, _utc1,
            units, meterDelta, largeDelta, lohBefore, lohAfter, rest));
    }

    private static AllocKind KindOf(PrimLayer.UploadSample u) => u.Kind switch
    {
        "prim" => AllocKind.Prim, "sculpt" => AllocKind.Sculpt, "mesh" => AllocKind.Mesh, "terrain" => AllocKind.Terrain, _ => AllocKind.Texture,
    };

    private static string Name(AllocKind k) => k switch
    {
        AllocKind.TextureDecode => "texture_decode", AllocKind.TexturePrepare => "texture_prepare", AllocKind.MeshDecode => "mesh_decode",
        AllocKind.MeshPrepare => "mesh_prepare", AllocKind.TerrainPrepare => "terrain_prepare", _ => k.ToString().ToLowerInvariant(),
    };

    /// <summary>The frames over <paramref name="hitchMs"/> with at least <paramref name="minGcs"/> collections (the burst).</summary>
    public IEnumerable<int> BurstRows(double hitchMs, int minGcs) =>
        Enumerable.Range(0, Rows.Count).Where(i => Rows[i].Phase == "path" && Rows[i].Ms > hitchMs && Rows[i].Gc0 + Rows[i].Gc1 + Rows[i].Gc2 >= minGcs);

    /// <summary>The report: every frame as columns (bytes), the path's totals per kind, and the check.</summary>
    public object Report(GcEventRecorder? events, double hitchMs, int minGcs)
    {
        var path = Rows.Where(r => r.Phase == "path").ToList();
        object Col<T>(Func<Row, T> f) => Rows.Select(f).ToList();
        var columns = new Dictionary<string, object>
        {
            ["phase"] = Col(r => r.Phase), ["frame"] = Col(r => r.Frame), ["ms"] = Col(r => Math.Round(r.Ms, 2)), ["at_s"] = Col(r => Math.Round(r.AtS, 3)),
            ["process_bytes"] = Col(r => r.Total), ["main_bytes"] = Col(r => r.Main), ["main_rest_bytes"] = Col(r => r.Rest),
            ["gen0"] = Col(r => r.Gc0), ["gen1"] = Col(r => r.Gc1), ["gen2"] = Col(r => r.Gc2), ["gc_pause_ms"] = Col(r => Math.Round(r.PauseMs, 2)),
            ["loh_before_last_gc"] = Col(r => r.LohBefore), ["loh_after_last_gc"] = Col(r => r.LohAfter),
        };
        for (int u = 0; u < UnitKinds.Length; u++) { int uu = u; columns[$"unit_{Name(UnitKinds[u])}_bytes"] = Col(r => r.Units[uu]); }
        foreach (var k in WorkerKinds)
        {
            columns[$"worker_{Name(k)}_bytes"] = Col(r => r.Meter[K + (int)k]);
            columns[$"inline_{Name(k)}_bytes"] = Col(r => r.Meter[(int)k]);
        }
        foreach (var k in Enum.GetValues<AllocKind>())
            columns[$"large_{Name(k)}_bytes"] = Col(r => r.Large[(int)k] + r.Large[K + (int)k]);
        long Sum(Func<Row, long> f) => path.Sum(f);
        var totals = new Dictionary<string, long>
        {
            ["process"] = Sum(r => r.Total), ["main"] = Sum(r => r.Main), ["main_rest"] = Sum(r => r.Rest),
        };
        for (int u = 0; u < UnitKinds.Length; u++) { int uu = u; totals[$"unit_{Name(UnitKinds[u])}"] = Sum(r => r.Units[uu]); }
        foreach (var k in WorkerKinds) { totals[$"worker_{Name(k)}"] = Sum(r => r.Meter[K + (int)k]); totals[$"inline_{Name(k)}"] = Sum(r => r.Meter[(int)k]); }
        foreach (var k in Enum.GetValues<AllocKind>()) totals[$"large_{Name(k)}"] = Sum(r => r.Large[(int)k] + r.Large[K + (int)k]);
        totals["workers_counted"] = WorkerKinds.Sum(k => totals[$"worker_{Name(k)}"]);
        totals["other_threads_uncounted"] = totals["process"] - totals["main"] - totals["workers_counted"];
        var bursts = BurstRows(hitchMs, minGcs).ToList();
        return new
        {
            start_stop_ms_total = Math.Round(_overheadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 3),
            start_stop_us_per_frame = Rows.Count == 0 ? 0 : Math.Round(_overheadTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / Rows.Count, 3),
            gc_events = events is null ? "off" : events.TicksOn ? "ticks" : "on", tolerance_bytes = ToleranceBytes, check_failures = Failures.Take(20).ToList(), check_failure_count = Failures.Count,
            path_totals = totals, burst = new { hitch_ms = hitchMs, min_collections = minGcs, rows = bursts },
            collections = events is null ? null : Collections(events), ticks = events is null ? null : Ticks(events, bursts),
            frames = columns,
        };
    }

    // each collection placed in its frame by its index (the frame whose count range holds it)
    private object Collections(GcEventRecorder events) => events.Collections().Select(c =>
    {
        int row = Rows.FindIndex(r => c.Index > r.GcBefore && c.Index <= r.GcBefore + r.Gc0 + r.Gc1 + r.Gc2);
        return new { index = c.Index, gen = c.Generation, reason = GcEventRecorder.ReasonName(c.Reason), type = GcEventRecorder.TypeName(c.Type), row };
    }).ToList();

    // the allocation samples by type: over the path, and in the burst frames and the 10 before each
    private object? Ticks(GcEventRecorder events, List<int> bursts)
    {
        var ticks = events.Ticks();
        var pathRows = Rows.Where(r => r.Phase == "path").ToList();
        if (ticks.Count == 0 || pathRows.Count == 0) return new { samples_all = ticks.Count, dropped = events.TicksDropped };
        long p0 = pathRows[0].StartUtcTicks, p1 = pathRows[^1].EndUtcTicks;
        var windows = bursts.Select(i => (Rows[Math.Max(0, i - 10)].StartUtcTicks, Rows[i].EndUtcTicks)).ToList();
        // the thread each sample came from: the main thread, a texture decode thread, or any other (the pool: mesh decode and
        // preparation, terrain preparation, texture preparation and fetches)
        string Role(GcEventRecorder.Tick t) => meter.RoleOf(t.OsThread) ?? "other";
        object Top(IEnumerable<GcEventRecorder.Tick> ts) => ts.GroupBy(t => (t.Type, t.Large, Role(t))).Select(g => new { type = g.Key.Type, large = g.Key.Large, thread = g.Key.Item3, samples = g.Count(), bytes = g.Sum(t => t.Bytes) })
            .OrderByDescending(x => x.bytes).Take(40).ToList();
        object ByThread(IEnumerable<GcEventRecorder.Tick> ts) => ts.GroupBy(t => (Role(t), t.Large)).Select(g => new { thread = g.Key.Item1, large = g.Key.Large, samples = g.Count(), bytes = g.Sum(t => t.Bytes) })
            .OrderByDescending(x => x.bytes).ToList();
        var inPath = ticks.Where(t => t.AtTicks >= p0 && t.AtTicks <= p1).ToList();
        var inBurst = ticks.Where(t => windows.Any(w => t.AtTicks >= w.Item1 && t.AtTicks <= w.Item2)).ToList();
        return new { samples_all = ticks.Count, dropped = events.TicksDropped, samples_in_path = inPath.Count, samples_in_burst_windows = inBurst.Count, path_by_thread = ByThread(inPath), burst_by_thread = ByThread(inBurst), path = Top(inPath), burst_windows = Top(inBurst) };
    }
}
