// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using Godot;
using Viewer.Core.Residency;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The draw distance decides what is built. The official viewer builds and draws geometry only for what lies inside its view volume, whose
/// far plane is the draw distance, with no margin (see <see cref="DrawDistanceGate"/>). Objects beyond the distance are not presented; when
/// the camera moves, ones that fall beyond are released, farthest first, and ones that come within are presented again, both inside a small
/// time budget a frame, so a move across a region of many objects never costs a frame.
/// </summary>
public sealed partial class PrimLayer
{
    private readonly DrawDistanceGate _gate = new();

    /// <summary>The scan and the releases of a frame each stop at this many milliseconds.</summary>
    public const double GateScanMs = 0.5, GateReleaseMs = 1.0;

    /// <summary>Objects the draw distance holds back, and objects it released since the layer began (measurement).</summary>
    public float DrawDistanceMetres => _gate.Distance;
    public int BeyondDrawDistance => _gate.Beyond;
    public int ReleasedByDrawDistance { get; private set; }
    public double WorstGateMs { get; private set; }
    public double WorstReleaseMs { get; private set; }

    private static System.Numerics.Vector3 Num(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Main thread, once a frame after the camera is set.</summary>
    private void UpdateDrawDistance()
    {
        float wanted = Hub.DrawDistance;
        if (_gate.Distance != wanted)
        {
            bool wasOff = float.IsPositiveInfinity(_gate.Distance);
            _gate.Distance = wanted;
            if (wasOff && !float.IsPositiveInfinity(wanted) && _instances.Count > 0)
                MarkAll();   // objects presented before the distance was known are judged by it
        }
        if (float.IsPositiveInfinity(wanted)) return;
        long t0 = Stopwatch.GetTimestamp();
        long scanEnd = t0 + (long)(GateScanMs * Stopwatch.Frequency / 1000.0);
        var cam = Num(Camera);
        _gate.Scan(cam, Stopwatch.GetTimestamp, scanEnd, local => _dirty.Add(local));
        long releaseEnd = Stopwatch.GetTimestamp() + (long)(GateReleaseMs * Stopwatch.Frequency / 1000.0);
        while (Stopwatch.GetTimestamp() < releaseEnd && _gate.TryNextRelease(cam, out var id, out var origin, out float radius))
        {
            long r0 = Stopwatch.GetTimestamp();
            Remove(id);
            double rm = (Stopwatch.GetTimestamp() - r0) * 1000.0 / Stopwatch.Frequency;
            if (rm > WorstReleaseMs) WorstReleaseMs = rm;
            _gate.HoldBeyond(id, origin, radius);
            ReleasedByDrawDistance++;
        }
        long end = Stopwatch.GetTimestamp();
        double ms = (end - t0) * 1000.0 / Stopwatch.Frequency;
        if (ms > WorstGateMs) WorstGateMs = ms;
        if (end - _lastStatsAt >= 5 * Stopwatch.Frequency && (Dirty > 0 || _gate.Tracked > 0))
        {
            _lastStatsAt = end;
            Main.Log.Info("presentation.draw-distance",
                Viewer.Core.Diagnostics.CoreLog.F("origin", _world.OriginOffset.ToString()), Viewer.Core.Diagnostics.CoreLog.F("metres", wanted),
                Viewer.Core.Diagnostics.CoreLog.F("tracked", _gate.Tracked), Viewer.Core.Diagnostics.CoreLog.F("instances", Instances),
                Viewer.Core.Diagnostics.CoreLog.F("beyond", _gate.Beyond), Viewer.Core.Diagnostics.CoreLog.F("released", ReleasedByDrawDistance),
                Viewer.Core.Diagnostics.CoreLog.F("dirty", Dirty), Viewer.Core.Diagnostics.CoreLog.F("merged_cells", MergedCells),
                Viewer.Core.Diagnostics.CoreLog.F("merged_objects", MergedObjects), Viewer.Core.Diagnostics.CoreLog.F("mergeable", MergeableObjects), Viewer.Core.Diagnostics.CoreLog.F("merge_reasons", MergeReasons()), Viewer.Core.Diagnostics.CoreLog.F("draw_calls", (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame)), Viewer.Core.Diagnostics.CoreLog.F("objects_drawn", (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame)), Viewer.Core.Diagnostics.CoreLog.F("worst_gate_ms", System.Math.Round(WorstGateMs, 2)), Viewer.Core.Diagnostics.CoreLog.F("worst_release_ms", System.Math.Round(WorstReleaseMs, 2)));
        }
    }

    private long _lastStatsAt;
}
