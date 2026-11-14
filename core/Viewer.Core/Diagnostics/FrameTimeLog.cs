// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;

namespace Viewer.Core.Diagnostics;

/// <summary>What the frame-time log writes.</summary>
public enum FrameTimeLogMode
{
    /// <summary>Nothing.</summary>
    Off,
    /// <summary>Hitches (frames over <see cref="FrameTimeLog.HitchMs"/>) and a summary every few seconds.</summary>
    Hitches,
    /// <summary>The same, and one line for every frame.</summary>
    Full,
}

/// <summary>
/// A frame-time log for finding why movement looks uneven. Off by default. While on, it writes a line for each frame that took longer than
/// <see cref="HitchMs"/> (with what the frame's work was spent on, largest first, and the garbage collector's share), and a summary of the
/// frame times every <see cref="SummarySeconds"/> seconds (median, 95th and 99th percentile, the number of frames over 33 and over 100 ms).
/// In <see cref="FrameTimeLogMode.Full"/> every frame also gets a line. Main thread only.
/// </summary>
public sealed class FrameTimeLog
{
    public const double HitchMs = 33.0, LongHitchMs = 100.0, SummarySeconds = 5.0;

    private readonly Action<string, LogField[]> _write;
    private readonly Dictionary<string, double> _costs = new();
    private readonly List<double> _window = new();
    private double _windowMs;
    private int _over33, _over100;

    public FrameTimeLog(Action<string, LogField[]> write, FrameTimeLogMode mode)
    {
        _write = write;
        Mode = mode;
    }

    public FrameTimeLogMode Mode { get; }

    /// <summary>Adds <paramref name="ms"/> to what the current frame spent on <paramref name="part"/>.</summary>
    public void Cost(string part, double ms)
    {
        if (Mode == FrameTimeLogMode.Off) return;
        _costs[part] = _costs.TryGetValue(part, out double have) ? have + ms : ms;
    }

    /// <summary>Ends the current frame, which took <paramref name="frameMs"/> from the previous frame's start, with <paramref name="gcPauseMs"/>
    /// of collector pause and <paramref name="gcs"/> collections during it. <paramref name="extra"/> is written on the frame's line in Full mode and on a hitch line.</summary>
    public void EndFrame(double frameMs, double gcPauseMs, int gcs, params LogField[] extra)
    {
        if (Mode == FrameTimeLogMode.Off) return;
        double own = _costs.Values.Sum();
        string top = string.Join(' ', _costs.OrderByDescending(c => c.Value).Take(4).Select(c => c.Key + "=" + N(c.Value)));
        _costs.Clear();

        if (Mode == FrameTimeLogMode.Full)
            _write("frame.sample", [new("ms", R(frameMs)), new("own_ms", R(own)), new("gc_pause_ms", R(gcPauseMs)), new("gcs", gcs), .. extra]);
        if (frameMs > HitchMs)
            _write("frame.hitch", [new("ms", R(frameMs)), new("top", top), new("engine_ms", R(Math.Max(0, frameMs - own))),
                new("gc_pause_ms", R(gcPauseMs)), new("gcs", gcs), new("long", frameMs > LongHitchMs), .. extra]);

        _window.Add(frameMs);
        _windowMs += frameMs;
        if (frameMs > HitchMs) _over33++;
        if (frameMs > LongHitchMs) _over100++;
        if (_windowMs < SummarySeconds * 1000) return;

        var sorted = _window.OrderBy(x => x).ToArray();
        double P(double p) => sorted[Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1)];
        _write("frame.pacing", [new("frames", sorted.Length), new("p50_ms", R(P(50))), new("p95_ms", R(P(95))), new("p99_ms", R(P(99))),
            new("max_ms", R(sorted[^1])), new("over_33_ms", _over33), new("over_100_ms", _over100)]);
        _window.Clear();
        _windowMs = 0;
        _over33 = _over100 = 0;
    }

    private static double R(double v) => Math.Round(v, 2);
    private static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
