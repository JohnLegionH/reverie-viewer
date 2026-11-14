// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

#if NV_AUTOMATION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The figures a long live run reads once per cycle through the automation API's sample command (development builds only): the process's
/// private bytes, handles and threads, the managed side as it stands and after two forced full collections, the engine's object counts and
/// video memory, the native heaps, the engine log's error count and the pipelines the renderer has compiled so far (the difference between
/// two samples is the pipelines made in between). Nothing here sends anything to the grid.
/// </summary>
internal static class LiveSample
{
    private const double MiB = 1048576.0;

    public static Dictionary<string, object> Take()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        var info = GC.GetGCMemoryInfo();
        var row = new Dictionary<string, object>
        {
            ["private_mib"] = Round(p.PrivateMemorySize64),
            ["working_set_mib"] = Round(p.WorkingSet64),
            ["handles"] = p.HandleCount,
            ["threads"] = p.Threads.Count,
            ["managed_committed_mib"] = Round(info.TotalCommittedBytes),
            ["managed_in_use_mib"] = Round(GC.GetTotalMemory(false)),
            ["gc_gen0"] = GC.CollectionCount(0), ["gc_gen1"] = GC.CollectionCount(1), ["gc_gen2"] = GC.CollectionCount(2),
            ["gc_pause_total_ms"] = Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds, 1),
            ["video_mib"] = Round((long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed)),
            ["texture_mib"] = Round((long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed)),
            ["buffer_mib"] = Round((long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed)),
            ["engine_objects"] = (long)Performance.GetMonitor(Performance.Monitor.ObjectCount),
            ["engine_resources"] = (long)Performance.GetMonitor(Performance.Monitor.ObjectResourceCount),
            ["engine_nodes"] = (long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
            ["engine_orphan_nodes"] = (long)Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount),
            ["godot_static_mib"] = Round((long)OS.GetStaticMemoryUsage()),
            ["engine_errors"] = Main.EngineLog?.Errors ?? 0,
            ["engine_warnings"] = Main.EngineLog?.Warnings ?? 0,
            ["pipelines_canvas"] = Pipelines(RenderingServer.RenderingInfo.PipelineCompilationsCanvas),
            ["pipelines_mesh"] = Pipelines(RenderingServer.RenderingInfo.PipelineCompilationsMesh),
            ["pipelines_surface"] = Pipelines(RenderingServer.RenderingInfo.PipelineCompilationsSurface),
            ["pipelines_draw"] = Pipelines(RenderingServer.RenderingInfo.PipelineCompilationsDraw),
            ["pipelines_specialization"] = Pipelines(RenderingServer.RenderingInfo.PipelineCompilationsSpecialization),
        };
        var native = NativeMemorySampler.Sample();
        row["heaps_committed_mib"] = Round(native.Heaps.Sum(h => h.Committed));
        row["heaps_allocated_mib"] = Round(native.Heaps.Sum(h => h.Allocated));
        int rank = 0;
        foreach (var h in native.Heaps.OrderByDescending(h => h.Committed).Take(3)) { rank++; row[$"heap{rank}_committed_mib"] = Round(h.Committed); row[$"heap{rank}_allocated_mib"] = Round(h.Allocated); }
        // after two full collections (finalizers run in between): what the managed side still holds, and what the process holds with the collector's slack gone
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        p.Refresh();
        row["managed_live_after_gc_mib"] = Round(GC.GetTotalMemory(false));
        row["private_after_gc_mib"] = Round(p.PrivateMemorySize64);
        return row;
    }

    /// <summary>A light reading for comparing two runs: the main thread's and the process's processor time so far, the engine's measured GPU and CPU render time summed over the frames since it was switched on, the frame count, the collector's counts and pause, and what the background inventory fetch is doing. Nothing is forced and nothing is sent to the grid.</summary>
    public static Dictionary<string, object> Light(Main main)
    {
        var cpu = CpuMeter.Take();
        FrameMeter.Enable(main);
        var row = new Dictionary<string, object>
        {
            ["main_thread_cpu_ms"] = Math.Round(cpu.ThreadMs, 1), ["process_cpu_ms"] = Math.Round(cpu.ProcessMs, 1),
            ["frames"] = FrameMeter.Frames, ["gpu_ms_sum"] = Math.Round(FrameMeter.GpuMs, 1), ["render_cpu_ms_sum"] = Math.Round(FrameMeter.RenderCpuMs, 1),
            ["gc_gen0"] = GC.CollectionCount(0), ["gc_gen1"] = GC.CollectionCount(1), ["gc_gen2"] = GC.CollectionCount(2),
            ["gc_pause_total_ms"] = Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds, 1),
            ["allocated_mib"] = Math.Round(GC.GetTotalAllocatedBytes() / MiB, 1),
            // the main thread's slices in processor time since the meter was switched on (counts), and the longest since the last reset
            ["script_slices"] = FrameMeter.Process.Count, ["script_slices_over_4ms"] = FrameMeter.Process.Over, ["script_slice_max_ms"] = Math.Round(FrameMeter.Process.MaxMs, 2), ["script_cpu_ms_sum"] = Math.Round(FrameMeter.Process.TotalMs, 1),
            ["presenter_slices"] = FrameMeter.Presenter.Count, ["presenter_slices_over_4ms"] = FrameMeter.Presenter.Over, ["presenter_slice_max_ms"] = Math.Round(FrameMeter.Presenter.MaxMs, 2), ["presenter_cpu_ms_sum"] = Math.Round(FrameMeter.Presenter.TotalMs, 1),
        };
        foreach (var kv in main.InventoryFetchState()) row[kv.Key] = kv.Value;
        return row;
    }

    private static long Pipelines(RenderingServer.RenderingInfo kind) => (long)RenderingServer.GetRenderingInfo(kind);
    private static double Round(long bytes) => Math.Round(bytes / MiB, 2, MidpointRounding.AwayFromZero);
}
#endif
