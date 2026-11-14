// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The test switch that leaves one of an avatar's built-in motions out, so a render can show which motion moves what: with a motion left out
/// it does not run (and does not step); every other motion runs as before; with nothing left out the avatar is as it was.
/// </summary>
public class DefaultMotionSetTests
{
    [Fact]
    public void With_nothing_left_out_every_always_on_motion_runs()
    {
        var c = new MotionController();
        new AvatarDefaultMotions(1).Start(c, 5.0);
        foreach (var name in new[] { "head_rot", "eye_rot", "body_noise", "breathe_rot", "pelvis_fix" })
            Assert.True(c.IsRunning(name, 5.0), name);
    }

    [Theory]
    [InlineData(DefaultMotionSet.Head, "head_rot")]
    [InlineData(DefaultMotionSet.Eye, "eye_rot")]
    [InlineData(DefaultMotionSet.BodySway, "body_noise")]
    [InlineData(DefaultMotionSet.Breathe, "breathe_rot")]
    [InlineData(DefaultMotionSet.PelvisFix, "pelvis_fix")]
    public void A_motion_left_out_does_not_run_and_the_others_do(DefaultMotionSet leftOut, string name)
    {
        var c = new MotionController();
        new AvatarDefaultMotions(1, DefaultMotionSet.All & ~leftOut).Start(c, 5.0);
        string[] all = ["head_rot", "eye_rot", "body_noise", "breathe_rot", "pelvis_fix"];
        foreach (var other in all.Where(n => n != name)) Assert.True(c.IsRunning(other, 5.0), other + " should run");   // the positive state first
        Assert.False(c.IsRunning(name, 5.0), name + " should not run");
    }

    [Fact]
    public void Hands_left_out_never_change_pose_and_with_hands_the_pose_follows_the_request()
    {
        var with = new AvatarDefaultMotions(1);
        var without = new AvatarDefaultMotions(1, DefaultMotionSet.All & ~DefaultMotionSet.Hands);
        var c1 = new MotionController(); var c2 = new MotionController();
        with.Start(c1, 0); without.Start(c2, 0);
        for (int i = 1; i <= 10; i++)
        {
            with.Step(c1, i * 0.05, 0f, 0f, false, false, false, HandMotion.PoseCount - 1);
            without.Step(c2, i * 0.05, 0f, 0f, false, false, false, HandMotion.PoseCount - 1);
        }
        Assert.Equal(HandMotion.PoseCount - 1, with.Hand.Wanted);
        Assert.Equal(HandMotion.Relaxed, without.Hand.Wanted);
    }

    [Fact]
    public void The_flying_roll_left_out_does_not_start_in_the_air()
    {
        var c = new MotionController();
        var d = new AvatarDefaultMotions(1, DefaultMotionSet.All & ~DefaultMotionSet.Fly);
        d.Start(c, 0);
        d.Step(c, 0.1, 0f, 0f, false, true, false, null);
        d.Step(c, 0.2, 0.5f, 5f, false, true, false, null);
        Assert.False(c.IsRunning("fly_adjust", 0.2));
        var e = new AvatarDefaultMotions(1);
        var c2 = new MotionController();
        e.Start(c2, 0);
        e.Step(c2, 0.1, 0f, 0f, false, true, false, null);
        Assert.True(c2.IsRunning("fly_adjust", 0.1));   // the control: with it in, it starts
    }

    [Theory]
    [InlineData(null, DefaultMotionSet.None)]
    [InlineData("", DefaultMotionSet.None)]
    [InlineData("breathe", DefaultMotionSet.Breathe)]
    [InlineData("sway", DefaultMotionSet.BodySway)]
    [InlineData("eye,head", DefaultMotionSet.Eye | DefaultMotionSet.Head)]
    [InlineData(" Hands , Pelvis ,fly", DefaultMotionSet.Hands | DefaultMotionSet.PelvisFix | DefaultMotionSet.Fly)]
    [InlineData("all", DefaultMotionSet.All)]
    public void The_switch_names_the_motions_to_leave_out(string? list, DefaultMotionSet expected) =>
        Assert.Equal(expected, DefaultMotionNames.Parse(list));

    [Fact]
    public void An_unknown_name_is_refused()
    {
        Assert.Equal(DefaultMotionSet.Breathe, DefaultMotionNames.Parse("breathe"));   // the positive state first
        Assert.Null(DefaultMotionNames.Parse("breathe,dance"));
    }
}
