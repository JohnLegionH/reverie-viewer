// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The motion trace's record of what the viewer told the region (the live movement test reads it): the control
/// bits of each update sent and each run state, kept only while recording and taken once.</summary>
public sealed class MotionTraceSentTests
{
    [Fact]
    public void Nothing_is_kept_before_the_trace_starts()
    {
        var t = new MotionTrace();
        t.Sent(1.0, 0x1);
        t.RunTold(1.1, true);
        Assert.Empty(t.TakeSent());
    }

    [Fact]
    public void Updates_and_run_states_come_back_in_order_and_once()
    {
        var t = new MotionTrace();
        t.Start();
        t.Sent(1.0, 0x80000);
        t.RunTold(1.1, true);
        t.Sent(1.2, 0x401);
        t.RunTold(2.0, false);
        var got = t.TakeSent();
        Assert.Equal([new MotionEvent(1.0, 0x80000, null), new MotionEvent(1.1, 0, 1), new MotionEvent(1.2, 0x401, null), new MotionEvent(2.0, 0, 0)], got);
        Assert.Empty(t.TakeSent());
    }

    [Fact]
    public void Starting_again_drops_what_was_kept_and_stopping_stops_keeping()
    {
        var t = new MotionTrace();
        t.Start();
        t.Sent(1.0, 0x1);
        t.Start();
        Assert.Empty(t.TakeSent());
        t.Sent(2.0, 0x2);
        t.Stop();
        t.Sent(3.0, 0x4);
        Assert.Equal([new MotionEvent(2.0, 0x2, null)], t.TakeSent());
    }
}
