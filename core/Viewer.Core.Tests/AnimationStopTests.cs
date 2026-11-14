// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Motion;
using Xunit;
using F = Viewer.Motion.AgentControlFlags;

namespace Viewer.Core.Tests;

/// <summary>
/// The end of the viewer's own animations, reported to the region as the finish-animation control bit. The official viewer's
/// LLAgent::onAnimStop sends it when stand up, pre-jump or a landing ends; a landing's end is not reported while a jump was asked for a
/// moment ago (so a quick second jump is not cut short) or while the jump key is down. The time of the last jump input is recorded in
/// moveUp, only while not flying. A motion that runs out on its own is what ends an animation (llmotioncontroller.cpp, the send-stop
/// time). Plain .NET; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class AnimationStopTests
{
    private const double Frame60 = 1.0 / 60;

    private static uint NextBits(AgentControlInput input, double now) => input.Frame(now, Frame60, false, 0);

    // ---- the time of the last jump input

    [Fact]
    public void A_jump_key_held_on_the_ground_records_the_time_of_each_frame_it_is_held()
    {
        var input = new AgentControlInput();
        Assert.Equal(0.0, input.LastJumpInputTime);
        input.Press(MoveCommand.Jump, 2.0);
        NextBits(input, 2.0);
        Assert.Equal(2.0, input.LastJumpInputTime, 6);
        NextBits(input, 2.1);
        Assert.Equal(2.1, input.LastJumpInputTime, 6);
    }

    [Fact]
    public void Jump_input_while_flying_is_not_recorded()
    {
        var input = new AgentControlInput();
        input.AdoptFlying(true);
        input.Press(MoveCommand.Jump, 5.0);
        NextBits(input, 5.0);
        Assert.Equal(0.0, input.LastJumpInputTime);
    }

    [Fact]
    public void A_held_jump_stops_being_recorded_when_it_turns_into_flight()
    {
        var input = new AgentControlInput();
        input.Press(MoveCommand.Jump, 0.0);
        double t = 0;
        while (!input.Flying && t < 2.0) { t += Frame60; NextBits(input, t); }
        Assert.True(input.Flying, "a held jump should start flying");
        double recorded = input.LastJumpInputTime;
        Assert.True(recorded > 0.0, "the jump input before the flight is recorded");
        NextBits(input, t + 0.5);
        Assert.Equal(recorded, input.LastJumpInputTime);
    }

    // ---- what the end of an animation sends

    [Theory]
    [InlineData("pre_jump")]
    [InlineData("land")]
    [InlineData("medium_land")]
    [InlineData("stand_up")]
    public void The_end_of_stand_up_pre_jump_or_a_landing_sends_finish_animation_for_one_update(string which)
    {
        var input = new AgentControlInput();
        input.AnimationStopped(Id(which), 10.0);
        Assert.Equal(F.FinishAnim, NextBits(input, 10.01));
        Assert.Equal(0u, NextBits(input, 10.02));
    }

    [Fact]
    public void The_end_of_any_other_animation_sends_nothing()
    {
        var input = new AgentControlInput();
        input.AnimationStopped(Guid.Parse("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0"), 10.0);   // walk
        input.AnimationStopped(AnimationIds.PreJump, 10.0);                                // proves the input reports what it should
        Assert.Equal(F.FinishAnim, NextBits(input, 10.01));
        var other = new AgentControlInput();
        other.AnimationStopped(Guid.Parse("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0"), 10.0);
        Assert.Equal(0u, NextBits(other, 10.01));
    }

    [Fact]
    public void A_landing_that_ends_soon_after_a_jump_input_is_not_reported_but_one_that_ends_later_is()
    {
        var input = new AgentControlInput();
        input.Press(MoveCommand.Jump, 20.0);
        NextBits(input, 20.0);
        input.Release(MoveCommand.Jump);
        NextBits(input, 20.05);
        Assert.Equal(20.0, input.LastJumpInputTime, 6);

        input.AnimationStopped(AnimationIds.Land, 20.5);                 // half a second after the jump: suppressed
        input.AnimationStopped(AnimationIds.MediumLand, 20.9);           // still inside the second
        Assert.Equal(0u, NextBits(input, 20.95));

        input.AnimationStopped(AnimationIds.Land, 21.5);                 // 1.5 s after it: reported
        Assert.Equal(F.FinishAnim, NextBits(input, 21.51));
    }

    [Fact]
    public void The_end_of_pre_jump_is_reported_even_right_after_a_jump_input()
    {
        var input = new AgentControlInput();
        input.Press(MoveCommand.Jump, 30.0);
        NextBits(input, 30.0);
        input.Release(MoveCommand.Jump);
        NextBits(input, 30.05);
        input.AnimationStopped(AnimationIds.PreJump, 30.1);
        Assert.Equal(F.FinishAnim, NextBits(input, 30.11));
    }

    [Fact]
    public void A_landing_ending_while_the_jump_key_is_down_is_not_reported_and_the_end_of_stand_up_is()
    {
        var input = new AgentControlInput();
        input.AdoptFlying(true);                                         // flying: the jump input is not recorded, so only the held key can hold the report back
        input.Press(MoveCommand.Jump, 40.0);
        NextBits(input, 40.0);                                           // the up bit is now set
        Assert.Equal(0.0, input.LastJumpInputTime);
        input.AnimationStopped(AnimationIds.StandUp, 40.01);             // a different animation: reported whatever the keys do
        Assert.Equal(F.FinishAnim, NextBits(input, 40.02) & F.FinishAnim);
        input.AnimationStopped(AnimationIds.Land, 40.03);
        Assert.Equal(0u, NextBits(input, 40.04) & F.FinishAnim);
        input.Release(MoveCommand.Jump);
        NextBits(input, 40.05);                                          // the key is up: the same animation's end is reported
        input.AnimationStopped(AnimationIds.Land, 40.06);
        Assert.Equal(F.FinishAnim, NextBits(input, 40.07) & F.FinishAnim);
    }

    [Fact]
    public void A_landing_with_no_jump_input_at_all_is_reported()
    {
        var input = new AgentControlInput();
        input.AnimationStopped(AnimationIds.Land, 0.5);                  // the clock's start counts as no jump input
        Assert.Equal(F.FinishAnim, NextBits(input, 0.51));
    }

    [Fact]
    public void The_finish_animation_bit_is_a_known_control_bit_with_the_protocol_value()
    {
        Assert.Equal(0x00008000u, F.FinishAnim);
        Assert.Contains(F.FinishAnim, F.All);
    }

    // ---- an animation that runs out of time

    private static AnimationClip Clip(bool loop, float duration, float easeOut = 0.2f) => new()
    {
        Duration = duration, Loop = loop, LoopIn = 0, LoopOut = duration, Priority = 2, EaseIn = 0.1f, EaseOut = easeOut, Emote = "",
        Tracks = [new JointTrack("mChest", -1, [0f, 1f], [Quaternion.Identity, Quaternion.Identity], [], [])],
    };

    private static readonly Guid IdA = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    [Fact]
    public void A_motion_that_does_not_loop_reports_once_when_it_runs_out()
    {
        var c = new MotionController();
        var ran = new List<Guid>();
        c.MotionRanOut += ran.Add;
        var signalled = new[] { new SignalledMotion(IdA, Clip(loop: false, duration: 1.0f), 1) };
        c.Update(100.0, signalled);
        c.Update(100.5, signalled);
        Assert.Empty(ran);                                               // still playing
        c.Update(101.0, signalled);                                      // its own end: the duration less its ease out is 100.8
        c.Update(101.5, signalled);
        c.Update(102.0, signalled);
        Assert.Equal([IdA], ran);
    }

    [Fact]
    public void A_motion_that_loops_never_runs_out_and_one_the_region_stops_is_not_a_run_out()
    {
        var looping = new MotionController();
        var ranLooping = new List<Guid>();
        looping.MotionRanOut += ranLooping.Add;
        var loop = new[] { new SignalledMotion(IdA, Clip(loop: true, duration: 1.0f), 1) };
        looping.Update(100.0, loop);
        looping.Update(105.0, loop);
        Assert.Empty(ranLooping);

        var stopped = new MotionController();
        var ranStopped = new List<Guid>();
        stopped.MotionRanOut += ranStopped.Add;
        stopped.Update(100.0, [new SignalledMotion(IdA, Clip(loop: false, duration: 5.0f), 1)]);
        stopped.Update(100.5, []);                                       // the region stops it long before its end
        stopped.Update(106.0, []);
        Assert.Empty(ranStopped);

        // and a run-out is reported by a controller that is built the same way, so the checks above are not vacuous
        var control = new MotionController();
        var ranControl = new List<Guid>();
        control.MotionRanOut += ranControl.Add;
        var once = new[] { new SignalledMotion(IdA, Clip(loop: false, duration: 1.0f), 1) };
        control.Update(100.0, once);
        control.Update(102.0, once);
        Assert.Equal([IdA], ranControl);
    }

    private static Guid Id(string which) => which switch
    {
        "pre_jump" => AnimationIds.PreJump,
        "land" => AnimationIds.Land,
        "medium_land" => AnimationIds.MediumLand,
        _ => AnimationIds.StandUp,
    };
}
