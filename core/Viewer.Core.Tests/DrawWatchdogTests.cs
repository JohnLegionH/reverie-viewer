// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The draw watchdog: a run is called not drawing only when frames stop being drawn while the per-frame callback keeps
/// running; a blocked main thread, a slow frame and a run that has not drawn yet are never called that. Time is passed in
/// as ticks (1000 a second here), so no test waits. Parallel with other classes: no shared state.
/// </summary>
public class DrawWatchdogTests
{
    private const long PerSecond = 1000;

    [Fact]
    public void A_loop_that_draws_every_frame_is_never_a_stall()
    {
        var w = new DrawWatchdog(PerSecond);
        for (int i = 0; i < 5000; i++)
        {
            long t = i * 16;
            w.FrameDrawn(t);
            Assert.False(w.FrameProcessed(t));
        }
        Assert.Equal(5000, w.Drawn);
    }

    [Fact]
    public void Frames_that_stop_being_drawn_while_the_loop_runs_are_a_stall_after_ten_seconds()
    {
        var w = new DrawWatchdog(PerSecond);
        w.FrameDrawn(0);
        bool stalled = false;
        long t = 0;
        // the loop goes on at about 147 callbacks a second (an engine that sleeps between iterations), nothing is drawn
        while (t < 9_900) { t += 7; Assert.False(w.FrameProcessed(t)); }
        while (!stalled && t < 12_000) { t += 7; stalled = w.FrameProcessed(t); }
        Assert.True(stalled);
        Assert.InRange(t, 10_000, 10_010);
    }

    [Fact]
    public void A_blocked_main_thread_is_not_a_stall()
    {
        var w = new DrawWatchdog(PerSecond);
        w.FrameDrawn(0);
        // one callback after a 30 s block: the loop was not running during it, so one callback is no stall
        Assert.False(w.FrameProcessed(30_000));
        w.FrameDrawn(30_000);
        Assert.False(w.FrameProcessed(30_016));
    }

    [Fact]
    public void A_run_that_has_not_drawn_yet_is_not_a_stall()
    {
        var w = new DrawWatchdog(PerSecond);
        for (int i = 0; i < 5000; i++) Assert.False(w.FrameProcessed(i * 10L));
        Assert.Equal(0, w.Drawn);
        Assert.Equal(0, w.SecondsSinceDrawn(60_000, PerSecond));
    }

    [Fact]
    public void Drawing_again_clears_the_stall()
    {
        var w = new DrawWatchdog(PerSecond);
        w.FrameDrawn(0);
        long t = 0;
        while (!w.FrameProcessed(t += 7)) { }
        w.FrameDrawn(t);
        Assert.False(w.FrameProcessed(t + 7));
        Assert.Equal(0.007, w.SecondsSinceDrawn(t + 7, PerSecond), 6);
    }

    [Fact]
    public void A_zero_tick_rate_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DrawWatchdog(0));
    }
}
