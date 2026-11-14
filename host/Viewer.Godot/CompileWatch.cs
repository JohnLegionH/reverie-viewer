// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Viewer.Godot.Presentation;

namespace Viewer.Godot;

/// <summary>
/// Render harness only: every pipeline the engine compiles after the start-up warm-up, with the frame it fell in. The engine
/// counts compilations by source (<see cref="RenderingServer.RenderingInfo"/> PipelineCompilationsCanvas, Mesh, Surface, Draw,
/// Specialization). Specialization compilations run on a background thread behind the engine's fallback shader; the other four
/// sources compile where the pipeline is first needed, on the path that draws the frame, so a frame that meets one waits for it.
/// The watch reads the counters once per drawn frame, from <see cref="Arm"/> (the end of the warm-up) on, and keeps a row for each
/// frame in which a counter moved: which phase of the run, the frame's length, the compilations by source, whether a source that
/// waits was among them, and whether the frame was long against the run's own median.
/// </summary>
internal sealed class CompileWatch
{
    /// <summary>The watch of this run, or null when the run does not keep one.</summary>
    public static CompileWatch? Active { get; set; }

    private static readonly RenderingServer.RenderingInfo[] Sources =
    {
        RenderingServer.RenderingInfo.PipelineCompilationsCanvas, RenderingServer.RenderingInfo.PipelineCompilationsMesh,
        RenderingServer.RenderingInfo.PipelineCompilationsSurface, RenderingServer.RenderingInfo.PipelineCompilationsDraw,
        RenderingServer.RenderingInfo.PipelineCompilationsSpecialization,
    };
    private static readonly string[] SourceNames = { "canvas", "mesh", "surface", "draw", "specialization" };
    private const int Specialization = 4;

    /// <summary>A frame is long when it is over this many milliseconds and over this many times the run's median frame.</summary>
    public const double LongFloorMs = 16.7;
    public const double LongTimesMedian = 2.5;

    public sealed record Row(string Phase, int Frame, double AtSeconds, double Ms, IReadOnlyDictionary<string, long> Compiled, bool Waits, bool Long);

    private long[] _last = Array.Empty<long>();
    private long _lastTick;
    private long _armedAt;
    private bool _armed;
    private int _frame;
    private readonly List<double> _allMs = new();
    private readonly List<(string Phase, int Frame, double At, double Ms, long[] D)> _rows = new();
    private readonly long[] _total = new long[Sources.Length];

    /// <summary>The phase the next frames belong to (a run names its stages: settling, merging, the measured frames).</summary>
    public string Phase { get; set; } = "run";

    private static long[] Read() => Sources.Select(s => (long)RenderingServer.GetRenderingInfo(s)).ToArray();

    /// <summary>Starts counting: what is compiled up to now is the warm-up's.</summary>
    public void Arm()
    {
        _last = Read();
        _lastTick = _armedAt = Stopwatch.GetTimestamp();
        _armed = true;
        _frame = 0;
        _allMs.Clear();
        _rows.Clear();
        Array.Clear(_total);
    }

    /// <summary>One drawn frame.</summary>
    public void Frame()
    {
        if (!_armed) return;
        long now = Stopwatch.GetTimestamp();
        double ms = (now - _lastTick) * 1000.0 / Stopwatch.Frequency;
        _lastTick = now;
        var cur = Read();
        var d = cur.Zip(_last, (a, b) => a - b).ToArray();
        _last = cur;
        _frame++;
        _allMs.Add(ms);
        if (d.All(x => x == 0)) return;
        for (int i = 0; i < d.Length; i++) _total[i] += d[i];
        _rows.Add((Phase, _frame, (now - _armedAt) / (double)Stopwatch.Frequency, ms, d));
    }

    private double Median()
    {
        if (_allMs.Count == 0) return 0;
        var s = _allMs.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }

    /// <summary>The rows, each marked with whether a source that waits moved and whether the frame was long.</summary>
    public IReadOnlyList<Row> Rows()
    {
        double median = Median();
        double longAbove = Math.Max(LongFloorMs, LongTimesMedian * median);
        return _rows.Select(r => new Row(r.Phase, r.Frame, Math.Round(r.At, 3), Math.Round(r.Ms, 2),
            SourceNames.Zip(r.D).Where(p => p.Second != 0).ToDictionary(p => p.First, p => p.Second),
            r.D.Where((x, i) => i != Specialization).Any(x => x != 0), r.Ms > longAbove)).ToList();
    }

    /// <summary>Compilations of sources that wait for the compile, after the warm-up.</summary>
    public long WaitingCompilations => _total.Where((x, i) => i != Specialization).Sum();

    /// <summary>Specialization compilations (background), after the warm-up.</summary>
    public long BackgroundCompilations => _total[Specialization];

    /// <summary>Frames that held a specialization compilation and were long: the background compile did not stay in the background.</summary>
    public int LongBackgroundFrames => Rows().Count(r => !r.Waits && r.Long);

    public object Report()
    {
        double median = Median();
        var rows = Rows();
        return new
        {
            frames = _frame, median_frame_ms = Math.Round(median, 2), long_above_ms = Math.Round(Math.Max(LongFloorMs, LongTimesMedian * median), 2),
            compiled = SourceNames.Zip(_total).Where(p => p.Second != 0).ToDictionary(p => p.First, p => p.Second),
            waiting_compilations = WaitingCompilations, background_compilations = BackgroundCompilations,
            frames_with_compilations = rows.Count, frames_that_waited = rows.Count(r => r.Waits), long_frames_with_compilations = rows.Count(r => r.Long),
            rows,
        };
    }
}
