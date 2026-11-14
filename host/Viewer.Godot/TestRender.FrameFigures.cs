// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Diagnostics;

namespace Viewer.Godot;

/// <summary>
/// A measuring loop's frame figures: which series they come from on this backend, which frames were set aside because the window was
/// not drawing, and the pacing verdict. See <see cref="FrameFigures"/> for the rules.
/// </summary>
public partial class TestRender
{
    /// <summary>What a measuring loop's frames come to after the frames the window was not drawing in are set aside.</summary>
    private sealed record FrameSeries(List<double> FrameMs, List<double> CpuMs, List<double> GpuMs, List<double> MainMs, FrameJudgement Judgement, MarkedFrames Marks);

    /// <summary>
    /// Sets aside the marked frames, judges the rest by this backend's basis, and writes <c>frame_pacing</c> into the result: the basis
    /// of each figure, the guard's reading of the real frame times, the frame cost with its CPU and GPU parts, the main thread's own
    /// work, how well the cost agrees with the real frame times, and the frames set aside. A measuring run fails on a judgement that
    /// says so (<c>--tr-pacing-guard</c>), and any run that has a test window fails as NOT_DRAWING when more than a tenth of its frames
    /// were set aside.
    /// </summary>
    private FrameSeries JudgeFrames(List<double> frameMs, List<double> cpuMs, List<double> gpuMs, List<double> mainMs, List<bool> marked)
    {
        var marks = FrameFigures.Marks(marked);
        var frames = FrameFigures.Unmarked(frameMs, marked);
        var cpu = FrameFigures.Unmarked(cpuMs, marked);
        var gpu = FrameFigures.Unmarked(gpuMs, marked);
        var main = FrameFigures.Unmarked(mainMs, marked);
        string driver = RenderingServer.GetCurrentRenderingDriverName();
        var judgement = FrameFigures.Judge(driver, frames, cpu, gpu, DisplayRefreshHz(), CurrentPresentSettings());
        var pacing = judgement.Pacing;
        var cost = cpu.Count == gpu.Count ? FrameFigures.Cost(cpu, gpu) : [];
        var agreement = FrameFigures.Compare(frames, cost);
        // the same comparison with the main thread's own work counted in the cost, for the record
        var withMain = cost.Length == main.Count ? FrameFigures.Compare(frames, cost.Zip(main, (c, m) => System.Math.Max(c, m)).ToList()) : null;
        string name = judgement.Basis == FrameBasis.FrameCost ? "frame_cost" : "frame_time";
        var figures = judgement.Figures.Count > 0 ? judgement.Figures.ToList() : frames;
        _result["frame_pacing"] = new
        {
            capped = pacing.Capped, kind = pacing.Kind.ToString(), message = pacing.Message,
            near_refresh_fraction = System.Math.Round(pacing.NearRefreshFraction, 4), mode_ms = System.Math.Round(pacing.ModeMs, 3), mode_fraction = System.Math.Round(pacing.ModeFraction, 4),
            stalls_per_second = System.Math.Round(pacing.StallsPerSecond, 2), stall_regularity = System.Math.Round(pacing.StallRegularity, 4), enforced = _pacingGuard,
            rendering_driver = driver,
            basis = new { p50 = name, p95 = name, p99 = name, max = name },
            flagged = judgement.Fails, judgement = judgement.Message,
            frame_figures = figures.Count == 0 ? null : Dist(figures),
            frame_time = frames.Count == 0 ? null : Dist(frames),
            frame_cost = cost.Length == 0 ? null : Dist(cost.ToList()),
            render_cpu_ms = cpu.Count == 0 ? null : Dist(cpu), render_gpu_ms = gpu.Count == 0 ? null : Dist(gpu), main_thread_ms = main.Count == 0 ? null : Dist(main),
            cost_agreement = new { agrees = agreement.Agrees, message = agreement.Message, p50_difference_ms = System.Math.Round(agreement.P50Difference, 3), p95_difference_ms = System.Math.Round(agreement.P95Difference, 3),
                tolerance = FrameFigures.AgreementTolerance, floor_ms = FrameFigures.AgreementFloorMs,
                with_main_thread = withMain is null ? null : new { agrees = withMain.Agrees, message = withMain.Message } },
            marked = new { frames = marks.Marked, of = marks.Total, share = System.Math.Round(marks.Share, 4), not_drawing = marks.NotDrawing, window_minimized_seen = _keeper?.Minimized ?? 0 },
        };
        if (judgement.Fails && _pacingGuard) _failures.Add("FRAME-PACING: " + judgement.Message);
        if (_keeper is not null && marks.NotDrawing)
        {
            _failures.Add($"NOT_DRAWING: {marks.Marked} of {marks.Total} frames ({marks.Share:P1}) were set aside because the window was not drawing (more than {FrameFigures.MaxMarkedShare:P0})");
            _exitOverride = NotDrawingExit;
        }
        return new FrameSeries(frames.Count > 0 ? frames : frameMs, cpu, gpu, main, judgement, marks);
    }
}
