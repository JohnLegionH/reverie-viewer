// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The frame-time log's hooks in the frame loop (see <see cref="FrameTimeLog"/>). Off unless the setting or NV_FRAME_TIME_LOG turns it on;
/// then each part of <c>_Process</c> is timed with the high-resolution clock, and the frame's total, the collector's pause and the
/// own avatar's drawn place and the camera's place (in the full mode) go to the log.
/// </summary>
public partial class Main
{
    private FrameTimeLog? _frameLog;
    private bool _frameLogMeasured;
    private FrameSentinel? _sentinelFirst, _sentinelLast;
    private long _frameMainEnd, _frameProcessStamp, _frameWallStamp;
    private int _frameGcs;
    private TimeSpan _frameGcPause;

    private void StartFrameTimeLog(SettingsService settings)
    {
        var mode = FrameTimeLogSettings.Resolve(settings, System.Environment.GetEnvironmentVariable);
        if (mode == FrameTimeLogMode.Off) return;
        Viewer.Godot.Presentation.FrameProbe.Enabled = true;     // the presentation steps are timed (wall clock) while the log is on
        Viewer.Godot.Presentation.FrameProbe.ThreadCpu = false;
        _frameLog = new FrameTimeLog((evt, fields) => Log.Info(evt, fields), mode);
        _frameGcs = System.GC.CollectionCount(0);
        _frameGcPause = System.GC.GetTotalPauseDuration();
        _sentinelFirst = new FrameSentinel { ProcessPriority = int.MinValue };   // processed before every other node
        _sentinelLast = new FrameSentinel { ProcessPriority = int.MaxValue };    // and after
        AddChild(_sentinelFirst);
        AddChild(_sentinelLast);
        GetTree().ProcessFrame += () => _frameProcessStamp = Stopwatch.GetTimestamp();
        Log.Info("frame.log", CoreLog.F("mode", mode.ToString().ToLowerInvariant()), CoreLog.F("hitch_ms", FrameTimeLog.HitchMs));
    }

    /// <summary>The clock now, or 0 while the log is off (pass it to <see cref="FrameCost"/>).</summary>
    private long FrameStamp() => _frameLog is null ? 0 : Stopwatch.GetTimestamp();

    /// <summary>Adds the time since <paramref name="from"/> to this frame's cost of <paramref name="part"/>.</summary>
    private void FrameCost(string part, long from)
    {
        if (_frameLog is { } log && from != 0) log.Cost(part, Stopwatch.GetElapsedTime(from).TotalMilliseconds);
    }

    private string WorstNeighbourDrain()
    {
        string worst = "none";
        double most = -1;
        foreach (var stack in _stacks.Values)
        {
            var last = stack.Presenter.Coordinator.Last;
            if (last.SpentMs <= most) continue;
            most = last.SpentMs;
            worst = $"{stack.World.Region?.Name ?? "?"}:{last.Applied}/{last.Steps}/{last.Backlog}/{last.SpentMs:0.#}";
        }
        return worst;
    }

    private static string TopSteps()
    {
        var f = Viewer.Godot.Presentation.FrameProbe.TakeFrame();
        var top = new List<(string Name, double Ms)>();
        for (int i = 0; i < Viewer.Godot.Presentation.FrameProbe.Count; i++)
            if (f.Ms[i] >= 0.5) top.Add((Viewer.Godot.Presentation.FrameProbe.NameOf(i), f.Ms[i]));
        return string.Join(' ', top.OrderByDescending(t => t.Ms).Take(4).Select(t => t.Name.Replace(' ', '_') + "=" + System.Math.Round(t.Ms, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>Ends the frame for the log: <paramref name="delta"/> is the engine's time since the previous frame (used for the first frame only).</summary>
    private void FrameLogEnd(double delta)
    {
        if (_frameLog is not { } log) return;
        var pause = System.GC.GetTotalPauseDuration();
        int gcs = System.GC.CollectionCount(0);
        if (!_frameLogMeasured)
        {
            RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);   // the engine's own render times, to take the engine share apart
            _frameLogMeasured = true;
        }
        var vp = GetViewport().GetViewportRid();
        double Ms(long from, long to) => from == 0 || to == 0 ? -1 : System.Math.Round(Stopwatch.GetElapsedTime(from, to).TotalMilliseconds, 2);
        long mainEnd = Stopwatch.GetTimestamp();
        // the engine's share of the frame, taken apart: its own count of the node processing (all nodes, not only this one), the physics step,
        // the render thread's frame setup, and the main viewport's measured CPU and GPU render times
        LogField[] extra =
        [
            CoreLog.F("t", System.Math.Round(_cameraClock.Elapsed.TotalSeconds, 4)),
            CoreLog.F("before_nodes_ms", Ms(_frameProcessStamp, _sentinelFirst?.Stamp ?? 0)),   // this frame: the engine's start (deferred calls) up to the first node
            CoreLog.F("main_ms", Ms(_sentinelFirst?.Stamp ?? 0, mainEnd)),   // this frame: from the first node to the end of this node's processing
            CoreLog.F("later_nodes_prev_ms", Ms(_frameMainEnd, _sentinelLast?.Stamp ?? 0)),   // the frame before: the nodes after this one
            CoreLog.F("gap_prev_ms", Ms(_sentinelLast?.Stamp ?? 0, _frameProcessStamp)),   // from the last node of the frame before to this frame's start: drawing and presenting
            CoreLog.F("steps", TopSteps()),   // the presentation steps of the frame (all regions together), largest first
            CoreLog.F("neighbour", WorstNeighbourDrain()),   // the neighbour whose queue drain took longest this frame: region applied/steps/backlog/spent ms
            CoreLog.F("process_ms", System.Math.Round(Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0, 2)),
            CoreLog.F("physics_ms", System.Math.Round(Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0, 2)),
            CoreLog.F("setup_cpu_ms", System.Math.Round(RenderingServer.GetFrameSetupTimeCpu(), 2)),
            CoreLog.F("render_cpu_ms", System.Math.Round(RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp), 2)),
            CoreLog.F("render_gpu_ms", System.Math.Round(RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp), 2)),
        ];
        if (log.Mode == FrameTimeLogMode.Full)
        {
            var cam = _camera?.GlobalPosition ?? default;
            var own = OwnAvatars?.SelfDrawnSoles ?? default;   // where the body was moved to this frame, not where it would be placed now
            extra = [.. extra,
                CoreLog.F("own", $"{own.X:0.####},{own.Y:0.####},{own.Z:0.####}"), CoreLog.F("cam", $"{cam.X:0.####},{cam.Y:0.####},{cam.Z:0.####}")];
        }
        _frameMainEnd = mainEnd;
        // the frame is timed on the wall clock between this frame's processing and the last one's, not by the engine's delta: the delta of a frame
        // leaves out a stall that came after the engine took it and before the nodes ran, and shows it one frame late
        long wallNow = mainEnd;
        double wallMs = _frameWallStamp == 0 ? delta * 1000.0 : Stopwatch.GetElapsedTime(_frameWallStamp, wallNow).TotalMilliseconds;
        _frameWallStamp = wallNow;
        log.EndFrame(wallMs, (pause - _frameGcPause).TotalMilliseconds, gcs - _frameGcs, extra);
        _frameGcs = gcs;
        _frameGcPause = pause;
    }
}
