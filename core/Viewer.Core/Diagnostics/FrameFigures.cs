// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;

namespace Viewer.Core.Diagnostics;

/// <summary>Which series a run's frame figures (p50, p95, p99, max) are taken from.</summary>
public enum FrameBasis
{
    /// <summary>The wall-clock time between two drawn frames. Right where frames are not held by the display (Vulkan here).</summary>
    FrameTime,
    /// <summary>The larger of the engine's measured CPU and GPU render time of the main viewport for each frame. Used where the
    /// time between frames is the present queue's, not the scene's (a windowed Direct3D 12 swap chain held to the display's refresh).</summary>
    FrameCost,
}

/// <summary>How the frame cost of a run compares with its real frame times.</summary>
/// <param name="Agrees">True when both the median and the 95th percentile of the cost are within the tolerance of the frame times'.</param>
public sealed record CostAgreement(bool Agrees, double FrameP50, double FrameP95, double CostP50, double CostP95, double P50Difference, double P95Difference, string Message);

/// <summary>One run's frame judgement: the figures' basis, the pacing reading, and whether the run fails on it.</summary>
/// <param name="Basis">Where the run's frame figures come from.</param>
/// <param name="Pacing">The guard's reading of the real frame times (always made, whatever the basis).</param>
/// <param name="Fails">True when the run's frame figures cannot be used.</param>
/// <param name="Message">One line for the run's log and failure list.</param>
/// <param name="Figures">The series the frame figures are taken from (the real frame times, or the frame cost).</param>
public sealed record FrameJudgement(FrameBasis Basis, FramePacingVerdict Pacing, bool Fails, string Message, IReadOnlyList<double> Figures);

/// <summary>The frames a run could not draw because its window was not drawing (minimised), and the rule that fails the run.</summary>
/// <param name="Marked">Frames set aside.</param>
/// <param name="Total">All frames of the run's measured part.</param>
/// <param name="NotDrawing">True when more than <see cref="FrameFigures.MaxMarkedShare"/> of the frames are marked.</param>
public sealed record MarkedFrames(int Marked, int Total, bool NotDrawing)
{
    public double Share => Total == 0 ? 0 : (double)Marked / Total;
}

/// <summary>
/// What a measuring run's frame figures are made of. On Vulkan a free-running window gives real frame times, and those stay the
/// figures. On Direct3D 12 a windowed swap chain is held to the display's refresh (bursts of fast frames and one wait per refresh),
/// so the time between frames describes the present queue; the figures there come from the frame cost, the larger of the engine's
/// measured CPU and GPU render time of the main viewport for each frame. The cost is checked against the real frame times on Vulkan,
/// where both exist (<see cref="Compare"/>).
/// </summary>
public static class FrameFigures
{
    /// <summary>Direct3D 12 frame figures are taken from the frame cost. Off: on Vulkan, where both exist, the cost (the larger of the
    /// CPU and GPU render time of the main viewport) did not agree with the real frame times (busy view at 6,000 objects: cost p50 8.7 ms
    /// against frame time 22.5 ms, because the frame is bound by the main thread's own work, which the render times leave out), and
    /// with the window placed at the bottom of the stacking order Direct3D 12's real frame times are not held to the display's refresh
    /// on the machine this was measured on. The rule stays so it can be switched on again once the cost covers the whole frame.</summary>
    public const bool D3D12UsesFrameCost = false;

    /// <summary>The cost agrees with the frame times when each of p50 and p95 differs by at most this share of the frame time's value
    /// or <see cref="AgreementFloorMs"/>, whichever is larger.</summary>
    public const double AgreementTolerance = 0.25;

    /// <summary>The smallest difference that counts: below this the two series are the same to the clock's resolution and the
    /// engine's per-frame timing noise (frames of about a millisecond are timed with a Windows clock good to about 0.1 ms).</summary>
    public const double AgreementFloorMs = 0.25;

    /// <summary>A run with more than this share of its frames marked (the window was not drawing) ends as NOT_DRAWING.</summary>
    public const double MaxMarkedShare = 0.10;

    public static FrameBasis BasisFor(string? renderingDriver) => BasisFor(renderingDriver, D3D12UsesFrameCost);

    public static FrameBasis BasisFor(string? renderingDriver, bool d3d12UsesFrameCost) =>
        d3d12UsesFrameCost && string.Equals(renderingDriver, "d3d12", StringComparison.OrdinalIgnoreCase) ? FrameBasis.FrameCost : FrameBasis.FrameTime;

    /// <summary>The cost of each frame: the larger of its CPU and GPU render time. The series must have the same length.</summary>
    public static double[] Cost(IReadOnlyList<double> cpuMs, IReadOnlyList<double> gpuMs)
    {
        if (cpuMs.Count != gpuMs.Count) throw new ArgumentException($"the CPU series has {cpuMs.Count} frames and the GPU series {gpuMs.Count}");
        var cost = new double[cpuMs.Count];
        for (int i = 0; i < cost.Length; i++) cost[i] = Math.Max(cpuMs[i], gpuMs[i]);
        return cost;
    }

    /// <summary>The values of <paramref name="series"/> whose frame is not marked.</summary>
    public static List<double> Unmarked(IReadOnlyList<double> series, IReadOnlyList<bool> marked)
    {
        if (series.Count != marked.Count) throw new ArgumentException($"{series.Count} values and {marked.Count} marks");
        var kept = new List<double>(series.Count);
        for (int i = 0; i < series.Count; i++) if (!marked[i]) kept.Add(series[i]);
        return kept;
    }

    public static MarkedFrames Marks(IReadOnlyList<bool> marked)
    {
        int n = 0;
        foreach (bool m in marked) if (m) n++;
        return new MarkedFrames(n, marked.Count, marked.Count > 0 && (double)n / marked.Count > MaxMarkedShare);
    }

    /// <summary>
    /// The frame judgement of a run on <paramref name="renderingDriver"/>. The real frame times always go to the pacing guard. On the
    /// frame-time basis the run fails when the guard calls it paced. On the frame-cost basis only the engine's own vsync or frame-rate
    /// limit fails the run (a control run is meant to be flagged); the refresh rhythms the guard reads in the real frame times describe
    /// the present queue, which the cost does not depend on, and are reported, not failed. A run whose cost was not measured (no positive
    /// value) cannot use the cost and falls back to the frame times.
    /// </summary>
    public static FrameJudgement Judge(string? renderingDriver, IReadOnlyList<double> frameMs, IReadOnlyList<double> cpuMs, IReadOnlyList<double> gpuMs,
        double refreshHz, PresentSettings settings) =>
        Judge(renderingDriver, frameMs, cpuMs, gpuMs, refreshHz, settings, D3D12UsesFrameCost);

    public static FrameJudgement Judge(string? renderingDriver, IReadOnlyList<double> frameMs, IReadOnlyList<double> cpuMs, IReadOnlyList<double> gpuMs,
        double refreshHz, PresentSettings settings, bool d3d12UsesFrameCost)
    {
        var pacing = FramePacingGuard.Evaluate(frameMs, refreshHz, settings);
        var basis = BasisFor(renderingDriver, d3d12UsesFrameCost);
        double[] cost = cpuMs.Count == gpuMs.Count && cpuMs.Count == frameMs.Count ? Cost(cpuMs, gpuMs) : [];
        if (basis == FrameBasis.FrameCost && !cost.Any(c => c > 0))
            return new(FrameBasis.FrameTime, pacing, pacing.Capped, "frame cost not measured (no CPU or GPU render time): the frame figures are the real frame times; " + pacing.Message, frameMs);
        if (basis == FrameBasis.FrameTime)
            return new(FrameBasis.FrameTime, pacing, pacing.Capped, pacing.Message, frameMs);
        bool engineLimit = pacing.Kind == PacingKind.EngineLimit;
        string message = engineLimit
            ? "frame cost basis, but " + pacing.Message
            : pacing.Capped
                ? "frame figures are the frame cost (the larger of the CPU and GPU render time); the real frame times are held by the present queue and are not used: " + pacing.Message
                : "frame figures are the frame cost (the larger of the CPU and GPU render time); " + pacing.Message;
        return new(FrameBasis.FrameCost, pacing, engineLimit, message, cost);
    }

    /// <summary>Whether the frame cost's median and 95th percentile agree with the real frame times' within the tolerance.</summary>
    public static CostAgreement Compare(IReadOnlyList<double> frameMs, IReadOnlyList<double> cost)
    {
        if (frameMs.Count == 0 || cost.Count == 0) return new(false, 0, 0, 0, 0, 0, 0, "no frames to compare");
        double fp50 = Percentile(frameMs, 50), fp95 = Percentile(frameMs, 95), cp50 = Percentile(cost, 50), cp95 = Percentile(cost, 95);
        double d50 = cp50 - fp50, d95 = cp95 - fp95;
        bool ok50 = Math.Abs(d50) <= Math.Max(AgreementFloorMs, AgreementTolerance * fp50);
        bool ok95 = Math.Abs(d95) <= Math.Max(AgreementFloorMs, AgreementTolerance * fp95);
        string f(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        return new(ok50 && ok95, fp50, fp95, cp50, cp95, d50, d95,
            $"frame time p50 {f(fp50)} ms, p95 {f(fp95)} ms; frame cost p50 {f(cp50)} ms ({(d50 >= 0 ? "+" : "")}{f(d50)}), p95 {f(cp95)} ms ({(d95 >= 0 ? "+" : "")}{f(d95)}): {(ok50 && ok95 ? "agree" : "do not agree")} within {AgreementTolerance:P0} or {f(AgreementFloorMs)} ms");
    }

    /// <summary>The value at percentile <paramref name="p"/> (0 to 100) by the nearest-rank rule the harness's figures use.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var s = values.OrderBy(x => x).ToArray();
        return s[Math.Clamp((int)Math.Ceiling(p / 100.0 * s.Length) - 1, 0, s.Length - 1)];
    }
}
