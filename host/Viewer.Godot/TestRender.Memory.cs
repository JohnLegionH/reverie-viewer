// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;

namespace Viewer.Godot;

/// <summary>
/// Where a render run's memory is, at named points of the run: the process's working set and private bytes, the managed heap as the
/// .NET runtime reports it (heap size and committed bytes as of its last collection, the large and pinned object heaps, and the live size after a
/// full collection when asked), Godot's own static memory, the video, texture and buffer memory the rendering server counts, and the remainder
/// (private bytes that are neither the managed heap nor Godot's static memory: the runtime's own structures, native libraries, driver
/// allocations, thread stacks). The figures are for reading a run's memory by stage; they are not a measurement.
/// </summary>
public sealed class MemoryStages
{
    private const double MiB = 1048576.0;
    private readonly List<object> _stages = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly bool _collectEveryStage;

    /// <param name="collectEveryStage">Every snapshot runs a full collection first (--tr-mem-collect), so each stage's managed figure is what is still
    /// reachable; the run's timing and peaks change, so it is for reading the managed heap by stage, not for the peak or frame figures.</param>
    public MemoryStages(bool collectEveryStage = false) => _collectEveryStage = collectEveryStage;

    /// <summary>The stages taken so far, each a record of figures in MiB.</summary>
    public IReadOnlyList<object> Stages => _stages;

    /// <summary>Takes a snapshot named <paramref name="stage"/>. With <paramref name="collect"/> a full blocking collection runs first, so the managed
    /// figure is what is still reachable (this changes the run's timing; the recording phase never uses it).</summary>
    public object Snap(string stage, bool collect = false)
    {
        collect |= _collectEveryStage;
        if (collect)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        }
        using var p = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        long committed = info.TotalCommittedBytes, heap = info.HeapSizeBytes;
        long loh = info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0, poh = info.GenerationInfo.Length > 4 ? info.GenerationInfo[4].SizeAfterBytes : 0;
        long managedLive = GC.GetTotalMemory(false);
        long godotStatic = (long)OS.GetStaticMemoryUsage();
        long priv = p.PrivateMemorySize64;
        long texture = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed);
        long buffer = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed);
        long video = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed);
        var s = new
        {
            stage, at_s = Math.Round(_clock.Elapsed.TotalSeconds, 1), collected = collect,
            working_set_mib = Math.Round(p.WorkingSet64 / MiB, 1), peak_working_set_mib = Math.Round(p.PeakWorkingSet64 / MiB, 1),
            private_mib = Math.Round(priv / MiB, 1), peak_private_mib = Math.Round(p.PeakPagedMemorySize64 / MiB, 1),
            managed_heap_mib = Math.Round(heap / MiB, 1), managed_committed_mib = Math.Round(committed / MiB, 1), managed_in_use_mib = Math.Round(managedLive / MiB, 1),
            managed_loh_mib = Math.Round(loh / MiB, 1), managed_poh_mib = Math.Round(poh / MiB, 1),
            godot_static_mib = Math.Round(godotStatic / MiB, 1), godot_static_peak_mib = Math.Round((long)OS.GetStaticMemoryPeakUsage() / MiB, 1),
            video_mib = Math.Round(video / MiB, 1), texture_mib = Math.Round(texture / MiB, 1), buffer_mib = Math.Round(buffer / MiB, 1),
            other_native_mib = Math.Round((priv - committed - godotStatic) / MiB, 1),
        };
        _stages.Add(s);
        return s;
    }
}
