// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The test run's heartbeat file: a step is written before its work starts (also when the calling thread never
/// returns from it), frames every <see cref="RunHeartbeat.FramesPerWrite"/> frames, the file always holds a whole
/// heartbeat, and a write that fails never stops the run. Runs in parallel with other classes: each test writes only its
/// own temp folder and its own heartbeat's writer thread.
/// </summary>
public class RunHeartbeatTests : IDisposable
{
    private readonly string _root = TestTemp.NewFolder("heartbeat");

    public void Dispose() => TestTemp.Delete(_root);   // only the unique folder this instance was handed

    private static Dictionary<string, string> Read(RunHeartbeat h)
    {
        Assert.True(h.Flush());
        return RunHeartbeat.Parse(File.ReadAllText(h.FilePath));
    }

    [Fact]
    public void A_step_is_written_with_the_cycle_and_frame()
    {
        using var h = new RunHeartbeat(Path.Combine(_root, "run.heartbeat"));
        h.SetPhase("lifecycle");
        for (int i = 0; i < 7; i++) h.CountFrame();
        h.SetCycle(3, 256);
        h.StartStep("reset");
        var d = Read(h);
        Assert.Equal("lifecycle", d["phase"]);
        Assert.Equal("reset", d["step"]);
        Assert.Equal("3", d["cycle"]);
        Assert.Equal("256", d["size"]);
        Assert.Equal("7", d["frame"]);
        Assert.Equal("7", d["step_frame"]);
        Assert.Equal("1", d["steps"]);
        Assert.Equal(Environment.ProcessId.ToString(), d["pid"]);
        Assert.True(DateTime.TryParse(d["step_started_utc"], null, System.Globalization.DateTimeStyles.RoundtripKind, out _));
    }

    [Fact]
    public void Frames_drawn_are_counted_apart_from_frames_processed_and_the_window_state_is_written()
    {
        using var h = new RunHeartbeat(Path.Combine(_root, "drawn.heartbeat"));
        for (int i = 0; i < 90; i++) h.CountFrame();
        for (int i = 0; i < 4; i++) h.CountDrawn();
        h.SetWindow("mode=Minimized can_draw=False\nfocused=False");
        h.StartStep("path");
        var d = Read(h);
        Assert.Equal("90", d["frame"]);
        Assert.Equal("4", d["drawn"]);
        Assert.Equal("mode=Minimized can_draw=False focused=False", d["window"]);
        Assert.Equal(4, h.Drawn);
    }

    [Fact]
    public void A_step_is_written_even_when_the_calling_thread_never_comes_back()
    {
        using var h = new RunHeartbeat(Path.Combine(_root, "frozen.heartbeat"));
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var caller = new Thread(() => { h.SetCycle(2, 1024); h.StartStep("frozen"); started.Set(); release.Wait(); }) { IsBackground = true };
        caller.Start();
        Assert.True(started.Wait(60_000));
        // no Flush from here: the writer alone must bring the file up to the frozen step (poll, generous cap)
        string? step = null;
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            try { if (File.Exists(h.FilePath)) step = RunHeartbeat.Parse(File.ReadAllText(h.FilePath)).GetValueOrDefault("step"); }
            catch (IOException) { }   // the writer replacing the file at that moment
            if (step == "frozen") break;
            Thread.Sleep(20);
        }
        release.Set();
        caller.Join();
        Assert.Equal("frozen", step);
    }

    [Fact]
    public void Frames_are_written_every_FramesPerWrite_frames_and_not_between()
    {
        using var h = new RunHeartbeat(Path.Combine(_root, "frames.heartbeat"));
        h.SetPhase("render");
        Assert.Equal(1, h.WritesAsked);
        for (int i = 0; i < RunHeartbeat.FramesPerWrite - 1; i++) h.CountFrame();
        Assert.Equal(1, h.WritesAsked);
        h.CountFrame();
        Assert.Equal(2, h.WritesAsked);
        Assert.Equal(RunHeartbeat.FramesPerWrite.ToString(), Read(h)["frame"]);
        for (int i = 0; i < 3 * RunHeartbeat.FramesPerWrite + 5; i++) h.CountFrame();
        Assert.Equal(5, h.WritesAsked);
        Assert.Equal((4 * RunHeartbeat.FramesPerWrite + 5).ToString(), Read(h)["frame"]);   // the latest state when written
    }

    [Fact]
    public void The_file_holds_one_whole_heartbeat_and_no_side_file_is_left()
    {
        using var h = new RunHeartbeat(Path.Combine(_root, "whole.heartbeat"));
        for (int i = 0; i < 50; i++) h.StartStep("step " + i);
        var d = Read(h);
        Assert.Equal("step 49", d["step"]);
        Assert.Equal("50", d["steps"]);
        Assert.Equal(12, d.Count);
        Assert.False(File.Exists(h.FilePath + ".writing"));
        Assert.InRange(h.Writes, 1, 50);   // requests made while a write runs are covered by the next one
        Assert.Equal(0, h.WriteFailures);
        Assert.True(h.WriteMsMax >= 0 && h.WriteMsTotal >= h.WriteMsMax);
    }

    [Fact]
    public void A_failed_write_is_counted_and_the_run_goes_on()
    {
        // a folder that does not exist: every write fails
        using var h = new RunHeartbeat(Path.Combine(_root, "missing", "run.heartbeat"));
        h.SetPhase("lifecycle");
        h.StartStep("build");
        Assert.True(h.Flush());
        Assert.InRange(h.WriteFailures, 1, 2);
        Assert.Equal(h.Writes, h.WriteFailures);
        Assert.Equal("build", h.Step);
    }

    [Fact]
    public void A_relative_path_is_refused() => Assert.Throws<ArgumentException>(() => new RunHeartbeat("run.heartbeat"));
}
