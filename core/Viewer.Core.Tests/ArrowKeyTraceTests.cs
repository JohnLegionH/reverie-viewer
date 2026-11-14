// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Motion;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The arrow-key trace: one record per press (not per repeat), carrying the control that held the keyboard, whether the avatar's
/// controls received the key, and the avatar's state. Each test builds its own trace: no shared state, so the class runs in parallel.</summary>
public class ArrowKeyTraceTests
{
    [Fact]
    public void A_press_is_reported_with_the_control_that_held_the_keyboard()
    {
        var t = new ArrowKeyTrace();
        Assert.True(t.Press("UP", echo: false, "LineEdit", textEntry: true, ArrowPose.Standing, mouselook: false));
        var r = t.Take();
        Assert.NotNull(r);
        Assert.Equal("UP", r!.Value.Key);
        Assert.Equal("LineEdit", r.Value.FocusOwner);
        Assert.True(r.Value.TextEntry);
    }

    [Fact]
    public void A_held_key_is_reported_once_the_repeats_are_not_new_presses()
    {
        var t = new ArrowKeyTrace();
        Assert.True(t.Press("LEFT", false, "none", false, ArrowPose.Standing, false));
        Assert.False(t.Press("LEFT", true, "none", false, ArrowPose.Standing, false));
        Assert.False(t.Press("LEFT", true, "none", false, ArrowPose.Standing, false));
        Assert.NotNull(t.Take());
        Assert.Null(t.Take());
    }

    [Fact]
    public void The_report_says_whether_the_avatars_controls_received_the_key()
    {
        var taken = new ArrowKeyTrace();
        taken.Press("UP", false, "none", false, ArrowPose.Standing, false);
        taken.WorldReceived();
        var tr = taken.Take(); Assert.NotNull(tr); Assert.True(tr.Value.WorldReceived);

        var refused = new ArrowKeyTrace();
        refused.Press("UP", false, "Button", false, ArrowPose.Standing, false);
        var rr = refused.Take(); Assert.NotNull(rr); Assert.False(rr.Value.WorldReceived);
    }

    [Fact]
    public void Each_press_gets_its_own_record_in_order_and_taking_empties_the_trace()
    {
        var t = new ArrowKeyTrace();
        t.Press("UP", false, "none", false, ArrowPose.Standing, false);
        t.WorldReceived();
        t.Press("DOWN", false, "LineEdit", true, ArrowPose.Standing, false);
        var first = t.Take();
        var second = t.Take();
        Assert.NotNull(first); Assert.NotNull(second);
        Assert.Equal("UP", first!.Value.Key);
        Assert.True(first.Value.WorldReceived);
        Assert.Equal("DOWN", second!.Value.Key);
        Assert.False(second.Value.WorldReceived);   // the mark belongs to the press it followed
        Assert.Null(t.Take());
    }

    [Fact]
    public void The_report_carries_the_avatars_state()
    {
        var t = new ArrowKeyTrace();
        t.Press("UP", false, "none", false, ArrowPose.Flying, mouselook: true);
        var rep = t.Take(); Assert.NotNull(rep); var r = rep.Value;
        Assert.Equal(ArrowPose.Flying, r.Pose);
        Assert.True(r.Mouselook);
    }

    [Theory]
    [InlineData(false, false, ArrowPose.Standing)]
    [InlineData(true, false, ArrowPose.Sitting)]
    [InlineData(false, true, ArrowPose.Flying)]
    [InlineData(true, true, ArrowPose.Sitting)]   // a seated avatar is not flying
    public void The_pose_follows_sitting_then_flying(bool sitting, bool flying, ArrowPose expected)
        => Assert.Equal(expected, ArrowKeyTrace.PoseOf(sitting, flying));
}
