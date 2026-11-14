// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The long run (--tr-soak=&lt;minutes&gt;, see <see cref="SoakOptions"/>): one viewer process plays the same cycle of scenes over and over, each
/// scene being a run of the harness with its own arguments, and the process is sampled at the same point of every cycle, after the
/// last scene is gone, so a leak or a slow decay shows as a trend. Two files go to the output folder: the cycles (one row per cycle, row
/// 0 before the first) and the steps (one row per scene run, with its frame figures and its result). Memory is read twice in a cycle row,
/// as it stands and after a forced full collection, so a managed leak is told from a collector that has not yet run. The forced collection
/// is part of every cycle's sample, after the scenes' frames, and is counted in the collection figures.
/// </summary>
public partial class TestRender
{
    private const double SoakMiB = 1048576.0;

    private async Task<int> Soak(string[] args, SoakOptions o)
    {
        if (o.Minutes <= 0 && o.MaxCycles <= 0) { _failures.Add("--tr-soak needs minutes above 0 or --tr-soak-cycles"); return 2; }
        foreach (var (name, path) in new[] { ("--tr-soak-v9", o.RegionV9), ("--tr-soak-v5", o.RegionV5), ("--tr-soak-zoo", o.Zoo) })
            if (path.Length == 0 || !File.Exists(path)) { _failures.Add($"{name}= is not a file: {path}"); return 2; }
        var cyclesCsv = Path.Combine(_out, _tag + ".cycles.csv");
        var stepsCsv = Path.Combine(_out, _tag + ".steps.csv");
        var carried = new List<string> { "--test-render", "--tr-out=" + _out };
        if (args.Contains("--tr-pacing-guard")) carried.Add("--tr-pacing-guard");
        string soakTag = _tag;
        var clock = Stopwatch.StartNew();
        int cycle = 0, stepRuns = 0, stepsFailed = 0, aborted = 0;
        double lastCycleSeconds = 0;
        bool header = false, stepHeader = false;
        var throwsInARow = new Dictionary<string, int>();
        object? firstBackend = null;

        void Append(string path, ref bool headerDone, List<KeyValuePair<string, string>> row)
        {
            var text = (headerDone ? "" : SoakCsv.Header(row) + "\n") + SoakCsv.Line(row) + "\n";
            File.AppendAllText(path, text);
            headerDone = true;
        }

        _beat?.SetPhase("soak baseline");
        await SoakSettle();
        Append(cyclesCsv, ref header, SoakSample(0, clock.Elapsed.TotalSeconds, 0, 0, 0, double.NaN, double.NaN, double.NaN));
        var nativeCsv = Path.Combine(_out, _tag + ".native.csv");
        SoakNativeSample(nativeCsv, 0);
        bool census = args.Contains("--tr-soak-census");
        var censusCsv = Path.Combine(_out, _tag + ".census.csv");
        if (census) { File.WriteAllText(censusCsv, "cycle,after,type,count\n"); SoakCensus(censusCsv, 0, "baseline"); }

        while (true)
        {
            if (o.MaxCycles > 0 && cycle >= o.MaxCycles) break;
            if (o.Minutes > 0 && cycle > 0 && clock.Elapsed.TotalSeconds + lastCycleSeconds > o.Minutes * 60) break;
            cycle++;
            var cycleClock = Stopwatch.StartNew();
            var frames = new List<(double N, double P50, double P95, double Max)>();
            int failedHere = 0, ranHere = 0;
            foreach (var step in SoakPlan.Cycle(o, cycle))
            {
                var stepClock = Stopwatch.StartNew();
                string tag = step.Args.First(a => a.StartsWith("--tr-tag=", StringComparison.Ordinal))["--tr-tag=".Length..];
                var before = GetChildren().Select(c => c.GetInstanceId()).ToHashSet();
                long errorsBefore = Main.EngineLog?.Errors ?? 0;
                _tag = tag;
                _result.Clear(); _failures.Clear(); _exitOverride = 0;
                _beat?.SetPhase($"soak c{cycle} {step.Name}");
                int exit; string? thrown = null;
                try
                {
                    exit = await Run(carried.Concat(step.Args).ToArray());
                    if (_exitOverride != 0) exit = _exitOverride;
                }
                catch (Exception e) { thrown = e.GetType().Name + ": " + e.Message; exit = 2; }
                firstBackend ??= _result.GetValueOrDefault("backend");
                var figures = FrameFiguresOf(_result);
                string failure = thrown ?? _failures.FirstOrDefault() ?? "";
                int failures = _failures.Count + (thrown is null ? 0 : 1);
                // the scene's own stage (its viewport and what hangs on it) is the harness's, and goes with the scene
                foreach (var child in GetChildren())
                    if (!before.Contains(child.GetInstanceId())) { RemoveChild(child); child.QueueFree(); }
                await Frames(2);
                VariantDisposal.DrainAll("soak step");
                if (census) SoakCensus(censusCsv, cycle, step.Name);
                _result.Clear(); _failures.Clear(); _exitOverride = 0;
                _tag = soakTag;
                ranHere++; stepRuns++;
                if (exit != 0) { failedHere++; stepsFailed++; }
                if (figures.N > 0) frames.Add(figures);
                Append(stepsCsv, ref stepHeader, new List<KeyValuePair<string, string>>
                {
                    new("cycle", cycle.ToString(CultureInfo.InvariantCulture)), new("step", step.Name), new("at_s", SoakCsv.Number(Math.Round(clock.Elapsed.TotalSeconds, 1))),
                    new("seconds", SoakCsv.Number(Math.Round(stepClock.Elapsed.TotalSeconds, 2))), new("exit", exit.ToString(CultureInfo.InvariantCulture)),
                    new("failures", failures.ToString(CultureInfo.InvariantCulture)), new("first_failure", failure.Length > 200 ? failure[..200] : failure),
                    new("frames", SoakCsv.Number(figures.N)), new("frame_p50_ms", Num(figures.P50)), new("frame_p95_ms", Num(figures.P95)), new("frame_max_ms", Num(figures.Max)),
                    // the engine's objects and the viewer's RIDs once the scene is gone, so a scene that leaves something behind shows by name
                    new("engine_objects", ((long)Performance.GetMonitor(Performance.Monitor.ObjectCount)).ToString(CultureInfo.InvariantCulture)),
                    new("engine_resources", ((long)Performance.GetMonitor(Performance.Monitor.ObjectResourceCount)).ToString(CultureInfo.InvariantCulture)),
                    new("rids_live", Rids.Ledger.Count.ToString(CultureInfo.InvariantCulture)),
                    new("engine_errors", ((Main.EngineLog?.Errors ?? 0) - errorsBefore).ToString(CultureInfo.InvariantCulture)),   // made during this scene, so a scene that makes the engine complain shows by name
                });
                if (thrown is not null)
                {
                    throwsInARow[step.Name] = throwsInARow.GetValueOrDefault(step.Name) + 1;
                    if (throwsInARow[step.Name] >= 3) aborted = 1;
                }
                else throwsInARow[step.Name] = 0;
                if (aborted != 0) break;
            }
            await SoakSettle();
            double total = frames.Sum(f => f.N);
            double Weighted(Func<(double N, double P50, double P95, double Max), double> pick) => total > 0 ? frames.Sum(f => f.N * pick(f)) / total : double.NaN;
            lastCycleSeconds = cycleClock.Elapsed.TotalSeconds;
            Append(cyclesCsv, ref header, SoakSample(cycle, clock.Elapsed.TotalSeconds, lastCycleSeconds, ranHere, failedHere,
                Weighted(f => f.P50), Weighted(f => f.P95), frames.Count > 0 ? frames.Max(f => f.Max) : double.NaN));
            SoakNativeSample(nativeCsv, cycle);
            Console.Error.WriteLine($"TEST-RENDER {soakTag}: soak cycle {cycle} done in {lastCycleSeconds:F1} s ({failedHere} of {ranHere} steps failed), {clock.Elapsed.TotalMinutes:F1} min in");
            if (aborted != 0) { _failures.Add("a step threw in three cycles in a row; the soak stopped"); break; }
        }
        _result["backend"] = firstBackend;
        _result["soak"] = new { cycles = cycle, step_runs = stepRuns, steps_failed = stepsFailed, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), cycles_csv = Path.GetFileName(cyclesCsv), steps_csv = Path.GetFileName(stepsCsv),
            rids_live_at_end = Rids.Ledger.Count, rids = Rids.Summary(), aborted = aborted != 0 };
        return aborted != 0 ? 2 : 0;
    }

    /// <summary>
    /// The process's native memory at the same point of the cycle, one row per owner in a long file (cycle, kind, owner, MiB, regions): committed
    /// regions grouped by loaded module, mapped file and private-region size class, the process heaps (all of them, and the three largest), the
    /// managed collector's committed bytes and the engine's own allocator. Windows calls of our own; nothing is installed.
    /// </summary>
    private static void SoakNativeSample(string path, int cycle)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var sample = NativeMemorySampler.Sample();
            var rows = new List<(string Kind, string Owner, long Bytes, int Regions)>();
            foreach (var g in NativeMemoryGroups.Group(sample.Regions, 12)) rows.Add((g.Kind, g.Owner, g.Bytes, g.Regions));
            rows.Add(("heaps", "all committed", sample.Heaps.Sum(h => h.Committed), sample.Heaps.Count));
            rows.Add(("heaps", "all allocated", sample.Heaps.Sum(h => h.Allocated), sample.Heaps.Count));
            int rank = 0;
            foreach (var h in sample.Heaps.OrderByDescending(h => h.Committed).Take(3)) { rank++; rows.Add(("heaps", $"largest {rank} allocated", h.Allocated, 1)); rows.Add(("heaps", $"largest {rank} committed", h.Committed, 1)); }
            rows.Add(("managed", "collector committed", GC.GetGCMemoryInfo().TotalCommittedBytes, 1));
            rows.Add(("engine", "static memory", (long)OS.GetStaticMemoryUsage(), 1));
            var text = new System.Text.StringBuilder();
            if (!File.Exists(path)) text.Append("cycle,kind,owner,mib,regions\n");
            foreach (var r in rows)
                text.Append(cycle.ToString(CultureInfo.InvariantCulture)).Append(',').Append(r.Kind).Append(',').Append(SoakCsv.Field(r.Owner)).Append(',')
                    .Append(SoakCsv.Number(Math.Round(r.Bytes / SoakMiB, 3))).Append(',').Append(r.Regions.ToString(CultureInfo.InvariantCulture)).Append('\n');
            File.AppendAllText(path, text.ToString());
        }
        catch (Exception e) { Console.Error.WriteLine("TEST-RENDER: native memory sample failed: " + e.Message); }
    }

    /// <summary>
    /// --tr-soak-census: after every scene and every cycle, the managed wrappers of live engine objects counted by type (the C# binding keeps a
    /// weak list of them; read through reflection, so a development tool only): rows cycle, label, type, count. Two cycles' rows subtracted show
    /// which wrapper types a scene leaves behind.
    /// </summary>
    private static void SoakCensus(string path, int cycle, string label)
    {
        try
        {
            var counts = WrapperCounts();
            if (counts is null) { File.AppendAllText(path, $"{cycle},{label},(no tracker found),0\n"); return; }
            var text = new System.Text.StringBuilder();
            foreach (var kv in counts.OrderBy(k => k.Key, StringComparer.Ordinal))
                text.Append(cycle.ToString(CultureInfo.InvariantCulture)).Append(',').Append(SoakCsv.Field(label)).Append(',').Append(SoakCsv.Field(kv.Key)).Append(',').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            File.AppendAllText(path, text.ToString());
        }
        catch (Exception e) { Console.Error.WriteLine("TEST-RENDER: object census failed: " + e.Message); }
    }

    /// <summary>
    /// The managed wrappers of live engine objects counted by type (the C# binding keeps a weak list of them, read through reflection; null when the
    /// binding has no such list). Development tools and tests only.
    /// </summary>
    internal static Dictionary<string, int>? WrapperCounts()
    {
        var tracker = typeof(GodotObject).Assembly.GetType("Godot.DisposablesTracker");
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        object? list = tracker?.GetField("GodotObjectInstances", flags)?.GetValue(null) ?? tracker?.GetProperty("GodotObjectInstances", flags)?.GetValue(null);
        if (list is not System.Collections.IEnumerable entries) return null;
        var counts = new Dictionary<string, int>();
        foreach (var entry in entries)
        {
            var key = entry.GetType().GetProperty("Key")?.GetValue(entry);
            if (key is null) continue;
            var args = new object?[] { null };
            if (key.GetType().GetMethod("TryGetTarget")?.Invoke(key, args) is not true || args[0] is not GodotObject o || !GodotObject.IsInstanceValid(o)) continue;
            string managed = o.GetType().FullName ?? "?", native = o.GetClass();
            string name = managed.StartsWith("Godot.", StringComparison.Ordinal) && managed["Godot.".Length..] == native ? native : managed + " / " + native;
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        return counts;
    }

    private static string Num(double v) => double.IsNaN(v) ? "" : SoakCsv.Number(Math.Round(v, 3));

    /// <summary>A few frames with the disposal queue emptied: the scenes' own frees have all been made before a sample is taken.</summary>
    private async Task SoakSettle()
    {
        await Frames(5);
        VariantDisposal.DrainAll("soak settle");
        await Frames(2);
    }

    /// <summary>The frame figures a scene run left in its result (the measuring loops write <c>frame_pacing.frame_figures</c>).</summary>
    private static (double N, double P50, double P95, double Max) FrameFiguresOf(Dictionary<string, object?> result)
    {
        try
        {
            if (!result.TryGetValue("frame_pacing", out var pacing) || pacing is null) return (0, double.NaN, double.NaN, double.NaN);
            var element = JsonSerializer.SerializeToElement(pacing);
            if (!element.TryGetProperty("frame_figures", out var f) || f.ValueKind != JsonValueKind.Object) return (0, double.NaN, double.NaN, double.NaN);
            return (f.GetProperty("n").GetDouble(), f.GetProperty("p50").GetDouble(), f.GetProperty("p95").GetDouble(), f.GetProperty("max").GetDouble());
        }
        catch (Exception) { return (0, double.NaN, double.NaN, double.NaN); }
    }

    /// <summary>One cycle row: the process as it stands, then after a forced full collection. The figures are in MiB unless named otherwise.</summary>
    private List<KeyValuePair<string, string>> SoakSample(int cycle, double elapsedSeconds, double cycleSeconds, int steps, int stepsFailed, double p50, double p95, double max)
    {
        string I(long v) => v.ToString(CultureInfo.InvariantCulture);
        string M(long bytes) => SoakCsv.Number(Math.Round(bytes / SoakMiB, 2));
        using var p = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        long WithoutCollect(out long ws, out long priv)
        {
            p.Refresh();
            ws = p.WorkingSet64; priv = p.PrivateMemorySize64;
            return GC.GetTotalMemory(false);
        }
        long inUse = WithoutCollect(out long workingSet, out long privateBytes);
        long peakWs = p.PeakWorkingSet64, peakPrivate = p.PeakPagedMemorySize64;
        int handles = p.HandleCount, threads = p.Threads.Count;
        long video = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed);
        long texture = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed);
        long buffer = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed);
        long heap = info.HeapSizeBytes, committed = info.TotalCommittedBytes;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        double pauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        long allocated = GC.GetTotalAllocatedBytes();
        long objects = (long)Performance.GetMonitor(Performance.Monitor.ObjectCount), resources = (long)Performance.GetMonitor(Performance.Monitor.ObjectResourceCount);
        long nodes = (long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount), orphans = (long)Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount);
        var store = SharedAssets.Stats;
        long engineErrors = Main.EngineLog?.Errors ?? 0, engineWarnings = Main.EngineLog?.Warnings ?? 0;
        int rids = Rids.Ledger.Count;
        // after a forced full collection: what the managed side still holds, and what the process holds with the collector's slack gone
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        long afterGc = GC.GetTotalMemory(false);
        p.Refresh();
        long privateAfter = p.PrivateMemorySize64, workingSetAfter = p.WorkingSet64;
        return new List<KeyValuePair<string, string>>
        {
            new("cycle", I(cycle)), new("elapsed_s", SoakCsv.Number(Math.Round(elapsedSeconds, 1))), new("cycle_s", SoakCsv.Number(Math.Round(cycleSeconds, 1))),
            new("steps", I(steps)), new("steps_failed", I(stepsFailed)),
            new("frame_p50_ms", Num(p50)), new("frame_p95_ms", Num(p95)), new("frame_max_ms", Num(max)),
            new("working_set_mib", M(workingSet)), new("peak_working_set_mib", M(peakWs)), new("private_mib", M(privateBytes)), new("peak_private_mib", M(peakPrivate)),
            new("video_mib", M(video)), new("texture_mib", M(texture)), new("buffer_mib", M(buffer)),
            new("managed_heap_mib", M(heap)), new("managed_committed_mib", M(committed)), new("managed_in_use_mib", M(inUse)), new("managed_after_gc_mib", M(afterGc)),
            new("private_after_gc_mib", M(privateAfter)), new("working_set_after_gc_mib", M(workingSetAfter)),
            new("godot_static_mib", M((long)OS.GetStaticMemoryUsage())), new("godot_static_peak_mib", M((long)OS.GetStaticMemoryPeakUsage())),
            new("gc_gen0", I(g0)), new("gc_gen1", I(g1)), new("gc_gen2", I(g2)), new("gc_pause_total_ms", SoakCsv.Number(Math.Round(pauseMs, 1))), new("allocated_mib", M(allocated)),
            new("handles", I(handles)), new("threads", I(threads)),
            new("engine_objects", I(objects)), new("engine_resources", I(resources)), new("engine_nodes", I(nodes)), new("engine_orphan_nodes", I(orphans)), new("rids_live", I(rids)),
            new("asset_store_mib", M(store.Bytes)), new("asset_store_entries", I(store.Entries)),
            new("engine_errors", I(engineErrors)), new("engine_warnings", I(engineWarnings)),
        };
    }
}
