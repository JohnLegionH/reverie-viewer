// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Godot.Presentation;

namespace Viewer.Godot;

/// <summary>
///Render harness only: what happened in each frame of a camera path run, so a long frame can
/// be explained rather than guessed at. Per frame: the engine's pipeline compilations by source (Godot 4.7
/// RenderingServer.RenderingInfo.PipelineCompilations{Canvas,Mesh,Surface,Draw,Specialization}; the class reference
/// describes Surface compilations as "a stutter when loading a scene the first time" and Draw compilations as "stutters
/// during gameplay the first time", Specialization as background work that should not stutter), .NET collections, the
/// frame's upload units, and first uses in our own code (a new material, GPU texture, mesh or sculpt object, the first
/// water or sky). Frames over <see cref="LongMs"/> are kept whole; every frame counts toward the summary.
/// </summary>
internal sealed class FrameLog(PrimLayer prims, EnvironmentLayer environment)
{
    public const double LongMs = 33.0;

    private static readonly RenderingServer.RenderingInfo[] Sources =
    {
        RenderingServer.RenderingInfo.PipelineCompilationsCanvas, RenderingServer.RenderingInfo.PipelineCompilationsMesh,
        RenderingServer.RenderingInfo.PipelineCompilationsSurface, RenderingServer.RenderingInfo.PipelineCompilationsDraw,
        RenderingServer.RenderingInfo.PipelineCompilationsSpecialization,
    };
    private static readonly string[] SourceNames = { "canvas", "mesh", "surface", "draw", "specialization" };

    public readonly record struct Snapshot(int Gc0, int Gc2, TimeSpan GcPause, long[] Pipelines, int TexturedMaterials, int Materials, int GpuTextures,
        int MeshObjects, int SculptObjects, bool Water, bool Sky, int Uploads);

    private readonly List<object> _long = new();
    private readonly Dictionary<(string, int), long[]> _pipelinesByFrame = new();
    private readonly long[] _total = new long[Sources.Length];
    private readonly long[] _first = Pipelines();
    private int _frames, _framesWithCompilations, _longWithCompilations, _longWithUploadOver4, _longWithGc, _longWithFirstUse, _longUnexplained;
    private double _msInCompilingFrames, _msAll, _gcPauseAll, _gcPauseInLong;
    private int _longMostlyGc, _longWithCompilationsNoGc;

    private static long[] Pipelines() => Sources.Select(s => (long)RenderingServer.GetRenderingInfo(s)).ToArray();

    public Snapshot Snap(int uploadsSoFar) => new(GC.CollectionCount(0), GC.CollectionCount(2), GC.GetTotalPauseDuration(), Pipelines(), prims.TexturedMaterials, prims.Materials,
        prims.GpuTextures, prims.MeshObjects, prims.SculptObjects, environment.WaterHeight is not null, environment.Frame is not null, uploadsSoFar);

    /// <summary>The pipeline compilations counted during a frame, by source (null when the frame was not logged).</summary>
    public Dictionary<string, long>? PipelinesIn(string phase, int frame) =>
        _pipelinesByFrame.TryGetValue((phase, frame), out var d) ? Named(d) : null;

    private static Dictionary<string, long> Named(long[] d) => SourceNames.Select((n, i) => (n, d[i])).Where(x => x.Item2 != 0).ToDictionary(x => x.n, x => x.Item2);

    public void Record(string phase, int frame, double ms, double atSeconds, Snapshot before, List<(PrimLayer.UploadSample U, string Phase, int Frame)> uploads)
    {
        var now = Snap(uploads.Count);
        var d = now.Pipelines.Zip(before.Pipelines, (a, b) => a - b).ToArray();
        for (int i = 0; i < d.Length; i++) _total[i] += d[i];
        _pipelinesByFrame[(phase, frame)] = d;
        bool compiled = d.Any(x => x != 0);
        _frames++;
        _msAll += ms;
        double pauseMs = (now.GcPause - before.GcPause).TotalMilliseconds;
        _gcPauseAll += pauseMs;
        if (compiled) { _framesWithCompilations++; _msInCompilingFrames += ms; }
        if (ms <= LongMs) return;
        var units = uploads.Skip(before.Uploads).Take(now.Uploads - before.Uploads).Select(u => u.U).ToList();
        int gcs = now.Gc0 - before.Gc0;
        _gcPauseInLong += pauseMs;
        if (pauseMs >= (ms - 16.7) / 2) _longMostlyGc++;
        if (compiled && gcs == 0) _longWithCompilationsNoGc++;
        var firstUse = new List<string>();
        if (now.TexturedMaterials > before.TexturedMaterials) firstUse.Add($"{now.TexturedMaterials - before.TexturedMaterials} textured materials");
        if (now.Materials > before.Materials) firstUse.Add($"{now.Materials - before.Materials} colour materials");
        if (now.GpuTextures > before.GpuTextures) firstUse.Add($"{now.GpuTextures - before.GpuTextures} GPU textures");
        if (now.MeshObjects > before.MeshObjects) firstUse.Add($"{now.MeshObjects - before.MeshObjects} mesh objects");
        if (now.SculptObjects > before.SculptObjects) firstUse.Add($"{now.SculptObjects - before.SculptObjects} sculpt objects");
        if (now.Water && !before.Water) firstUse.Add("first water");
        if (now.Sky && !before.Sky) firstUse.Add("first sky");
        if (compiled) _longWithCompilations++;
        if (units.Any(u => u.Ms > 4)) _longWithUploadOver4++;
        if (gcs > 0) _longWithGc++;
        if (firstUse.Count > 0) _longWithFirstUse++;
        if (!compiled && gcs == 0 && firstUse.Count == 0 && !units.Any(u => u.Ms > 4)) _longUnexplained++;
        _long.Add(new
        {
            phase, frame, at_s = Math.Round(atSeconds, 2), ms = Math.Round(ms, 1), pipelines = Named(d),
            gcs, gen2 = now.Gc2 - before.Gc2, gc_pause_ms = Math.Round(pauseMs, 1), first_use = firstUse,
            uploads = units.Count, first_uploads = units.Count(u => u.First), upload_ms = Math.Round(units.Sum(u => u.Ms), 2),
            drawn_objects = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame),
        });
    }

    public object Summary() => new
    {
        frames = _frames, long_threshold_ms = LongMs, long_frames = _long.Count,
        pipeline_compilations = Named(_total), pipeline_counters_at_start = Named(_first),
        frames_with_compilations = _framesWithCompilations,
        ms_in_frames_with_compilations = Math.Round(_msInCompilingFrames, 1), ms_all_frames = Math.Round(_msAll, 1),
        long_with_compilations = _longWithCompilations, long_with_upload_over_4ms = _longWithUploadOver4, long_with_gc = _longWithGc,
        long_with_first_use = _longWithFirstUse, long_unexplained = _longUnexplained,
        gc_pause_ms_all_frames = Math.Round(_gcPauseAll, 1), gc_pause_ms_in_long_frames = Math.Round(_gcPauseInLong, 1),
        long_mostly_gc_pause = _longMostlyGc, long_with_compilations_and_no_gc = _longWithCompilationsNoGc,
        long_frames_detail = _long,
    };
}
