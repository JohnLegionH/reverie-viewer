// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The per-frame crossing trace: a development aid, off unless switched on, that writes every frame from one second before a region crossing to one
/// second after it, and only into the log folder it is given. With no folder it writes nothing at all.
/// </summary>
public class CrossingFrameTraceTests
{
    private const double Frame = 1.0 / 50;

    private static string NewFolder() => TestTemp.NewFolderPath("crossing-frames");   // not made: the trace makes it only when it writes

    /// <summary>Frames from 0 to <paramref name="until"/> seconds, a crossing marked at each of <paramref name="marks"/>; each frame's line is its time.</summary>
    private static void Run(CrossingFrameTrace trace, double until, params double[] marks)
    {
        var pending = new Queue<double>(marks);
        for (double t = 0; t <= until + 1e-9; t += Frame)
        {
            if (pending.Count > 0 && t >= pending.Peek() - 1e-9) { pending.Dequeue(); trace.Mark(t, "crossing"); }
            double at = t;
            trace.Frame(at, () => at.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
        }
        trace.Flush();
    }

    private static string[] Files(string folder) => Directory.Exists(Path.Combine(folder, CrossingFrameTrace.SubFolder)) ? Directory.GetFiles(Path.Combine(folder, CrossingFrameTrace.SubFolder)).OrderBy(f => f).ToArray() : [];

    private static double[] Times(string file) => File.ReadAllLines(file).Select(l => double.Parse(l, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    [Fact]
    public void Every_frame_from_one_second_before_a_crossing_to_one_second_after_is_written_and_no_other()
    {
        string folder = NewFolder();
        var trace = new CrossingFrameTrace(folder, on: true);
        Run(trace, 6.0, 3.0);
        var files = Files(folder);
        Assert.Single(files);
        var times = Times(files[0]);
        Assert.Equal(101, times.Length);   // 2.00 .. 4.00 at 50 frames a second
        Assert.Equal(2.0, times.First(), 3);
        Assert.Equal(4.0, times.Last(), 3);
        Assert.True(times.Zip(times.Skip(1)).All(p => p.Second > p.First), "every frame once, in order");
    }

    [Fact]
    public void Each_crossing_gets_its_own_file_and_a_crossing_inside_a_window_extends_it()
    {
        string folder = NewFolder();
        var trace = new CrossingFrameTrace(folder, on: true);
        Run(trace, 12.0, 2.0, 2.5, 8.0);
        var files = Files(folder);
        Assert.Equal(2, files.Length);
        var first = Times(files[0]);
        Assert.Equal(1.0, first.First(), 3);
        Assert.Equal(3.5, first.Last(), 3);   // the second crossing's second after
        var second = Times(files[1]);
        Assert.Equal(7.0, second.First(), 3);
        Assert.Equal(9.0, second.Last(), 3);
    }

    [Fact]
    public void With_no_folder_nothing_is_written_and_no_line_is_built()
    {
        var trace = new CrossingFrameTrace(null, on: true);
        int built = 0;
        for (double t = 0; t < 3; t += Frame)
        {
            if (Math.Abs(t - 1.5) < Frame / 2) trace.Mark(t, "crossing");
            trace.Frame(t, () => { built++; return "x"; });
        }
        trace.Flush();
        Assert.False(trace.Active);
        Assert.Equal(0, built);
        Assert.Equal(0, trace.FilesWritten);
    }

    [Fact]
    public void Switched_off_nothing_is_written_even_with_a_folder()
    {
        string folder = NewFolder();
        var trace = new CrossingFrameTrace(folder, on: false);
        int built = 0;
        for (double t = 0; t < 3; t += Frame)
        {
            if (Math.Abs(t - 1.5) < Frame / 2) trace.Mark(t, "crossing");
            trace.Frame(t, () => { built++; return "x"; });
        }
        trace.Flush();
        Assert.False(trace.Active);
        Assert.Equal(0, built);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Events_noted_between_frames_go_into_the_next_frame_line()
    {
        string folder = NewFolder();
        var trace = new CrossingFrameTrace(folder, on: true);
        for (double t = 0; t <= 3.0 + 1e-9; t += Frame)
        {
            if (Math.Abs(t - 1.5) < Frame / 2) { trace.Note(t, "update region=b"); trace.Mark(t, "crossing"); }
            double at = t;
            trace.Frame(at, () => "f");
        }
        trace.Flush();
        var lines = File.ReadAllLines(Assert.Single(Files(folder)));
        var withEvent = lines.Where(l => l.Contains("update region=b")).ToArray();
        Assert.Single(withEvent);
        Assert.StartsWith("f", withEvent[0]);
    }

    [Fact]
    public void The_switch_is_off_unless_its_variable_is_one() =>
        Assert.Equal(Environment.GetEnvironmentVariable(CrossingFrameTrace.Variable) == "1", CrossingFrameTrace.SwitchedOn);
}
