// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Xunit;
using F = Viewer.Motion.AgentControlFlags;

namespace Viewer.Core.Tests;

/// <summary>
/// The control bits the keys make and when an AgentUpdate goes out, against the official viewer's rules (llviewerinput.cpp agent_push_forward,
/// agent_jump, agent_toggle_fly, agent_slide_*, agent_turn_*; llagent.cpp moveAt, moveAtNudge, moveUp, moveYaw, setFlying;
/// llviewermessage.cpp send_agent_update). Each test builds its own input and rule: no process-wide state, so the class runs in parallel.
/// </summary>
public class OfficialControlRulesTests
{
    private static uint Hold(MoveCommand c, double seconds, double frame = 1.0 / 60)
    {
        var input = new AgentControlInput();
        input.AdoptHeading(0);
        input.Press(c, 0);
        uint last = 0;
        for (double t = frame; t <= seconds + 1e-9; t += frame) last = input.Frame(t, frame, false, 0);
        return last;
    }

    [Fact]
    public void A_forward_key_held_a_tenth_of_a_second_sends_only_the_nudge_bit()
        => Assert.Equal(F.NudgeAtPos, Hold(MoveCommand.Forward, 0.1));

    [Fact]
    public void A_forward_key_held_past_a_quarter_of_a_second_sends_the_move_bit_and_the_fast_bit()
        => Assert.Equal(F.AtPos | F.FastAt, Hold(MoveCommand.Forward, 0.4));

    [Fact]
    public void Backward_and_the_slides_make_the_bits_of_their_direction()
    {
        Assert.Equal(F.AtNeg | F.FastAt, Hold(MoveCommand.Backward, 0.4));
        Assert.Equal(F.LeftPos | F.FastLeft, Hold(MoveCommand.SlideLeft, 0.4));
        Assert.Equal(F.LeftNeg | F.FastLeft, Hold(MoveCommand.SlideRight, 0.4));
        Assert.Equal(F.NudgeLeftPos, Hold(MoveCommand.SlideLeft, 0.1));
    }

    [Fact]
    public void The_turn_keys_send_the_yaw_bit_of_their_direction_and_both_together_send_both()
    {
        Assert.Equal(F.YawPos, Hold(MoveCommand.TurnLeft, 0.4));
        Assert.Equal(F.YawNeg, Hold(MoveCommand.TurnRight, 0.4));
        var input = new AgentControlInput();
        input.AdoptHeading(0);
        input.Press(MoveCommand.TurnLeft, 0); input.Press(MoveCommand.TurnRight, 0);
        uint c = input.Frame(0.1, 0.1, false, 0);
        Assert.Equal(F.YawPos | F.YawNeg, c);
        Assert.Equal(0f, input.Heading!.Value);   // both ways cancel
    }

    [Fact]
    public void The_turn_rate_ramps_from_five_percent_to_full_over_the_first_quarter_second()
    {
        Assert.Equal(0.05f, AgentControlInput.YawRate(0), 3);
        Assert.Equal(0.525f, AgentControlInput.YawRate(0.125), 3);
        Assert.Equal(1f, AgentControlInput.YawRate(0.25), 3);
        Assert.Equal(1f, AgentControlInput.YawRate(3), 3);
    }

    [Fact]
    public void Jump_sends_the_up_bit_and_holding_it_for_half_a_second_starts_flying()
    {
        Assert.Equal(F.UpPos | F.FastUp, Hold(MoveCommand.Jump, 0.3));
        Assert.Equal(F.UpPos | F.FastUp | F.Fly, Hold(MoveCommand.Jump, 0.6));
    }

    [Fact]
    public void Toggling_fly_keeps_the_fly_bit_set_between_key_presses_and_toggling_again_clears_it()
    {
        var input = new AgentControlInput();
        input.AdoptHeading(0);
        input.Press(MoveCommand.ToggleFly, 0); input.Release(MoveCommand.ToggleFly);
        Assert.Equal(F.Fly, input.Frame(0.1, 0.1, true, 0));
        Assert.Equal(F.Fly, input.Frame(0.2, 0.1, true, 0));
        input.Press(MoveCommand.ToggleFly, 0.3);
        Assert.Equal(0u, input.Frame(0.4, 0.1, true, 0));
    }

    [Fact]
    public void The_down_key_lands_a_flying_avatar_that_is_on_the_ground_and_slow()
    {
        var input = new AgentControlInput();
        input.AdoptHeading(0);
        input.Press(MoveCommand.ToggleFly, 0);
        input.Press(MoveCommand.Down, 0.1);
        uint c = input.Frame(0.2, 0.1, inAir: false, groundSpeed: 1f);
        Assert.False(input.Flying);
        Assert.Equal(F.UpNeg | F.FastUp, c);
    }

    // ---- send_agent_update ----

    private static AgentUpdate Update(uint controls, float x = 0, Quaternion? body = null, Quaternion? head = null) =>
        new(controls, body ?? Quaternion.Identity, new Vector3(x, 0, 2), new Vector3(x + 1, 0, 2), head);

    [Fact]
    public void A_change_of_control_bits_goes_out_at_once_and_once_more_after_it()
    {
        var rule = new AgentUpdateRule();
        Assert.True(rule.ShouldSend(Update(0), 0));
        Assert.True(rule.ShouldSend(Update(F.AtPos), 0.001));      // changed: at once, inside the throttle
        Assert.True(rule.ShouldSend(Update(F.AtPos), 0.010));      // the follow-up of the change
        Assert.False(rule.ShouldSend(Update(F.AtPos), 0.020));     // nothing moved: not sent
    }

    [Fact]
    public void Updates_without_a_change_go_out_no_more_than_125_a_second()
    {
        var rule = new AgentUpdateRule();
        Assert.True(rule.ShouldSend(Update(0, 0), 0));
        Assert.False(rule.ShouldSend(Update(0, 5), 0.004));        // the camera moved a lot, but 4 ms is inside 1/125 s
        Assert.True(rule.ShouldSend(Update(0, 5), 0.009));
    }

    [Fact]
    public void A_camera_that_moved_a_centimetre_or_turned_is_sent_and_one_that_did_not_is_not_until_the_keep_alive_second()
    {
        var rule = new AgentUpdateRule();
        Assert.True(rule.ShouldSend(Update(0, 0), 0));
        Assert.False(rule.ShouldSend(Update(0, 0.005f), 0.05));
        Assert.True(rule.ShouldSend(Update(0, 0.02f), 0.10));
        Assert.False(rule.ShouldSend(Update(0, 0.02f), 0.90));
        Assert.True(rule.ShouldSend(Update(0, 0.02f), 1.20));      // more than a second since the last: the keep-alive
    }

    [Fact]
    public void A_body_rotation_of_more_than_about_two_and_a_half_degrees_is_sent_and_a_smaller_one_is_not()
    {
        var rule = new AgentUpdateRule();
        Assert.True(rule.ShouldSend(Update(0), 0));
        Assert.False(rule.ShouldSend(Update(0, body: Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f * MathF.PI / 180)), 0.1));
        Assert.True(rule.ShouldSend(Update(0, body: Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 4f * MathF.PI / 180)), 0.2));
    }

    [Fact]
    public void A_head_rotation_between_half_a_degree_and_two_and_a_half_is_sent_once_the_rising_threshold_reaches_it()
    {
        var rule = new AgentUpdateRule();
        Assert.True(rule.ShouldSend(Update(0), 0));
        var head = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.5f * MathF.PI / 180);   // dot about 0.99991: between the two thresholds
        Assert.False(rule.ShouldSend(Update(0, head: head), 0.02));   // threshold 0.9997 + 0.02 * 0.00058 = 0.99971: not yet
        Assert.True(rule.ShouldSend(Update(0, head: head), 0.40));    // 0.9997 + 0.4 * 0.00058 = 0.99993: reached
    }

    [Fact]
    public void A_head_rotation_under_half_a_degree_is_close_enough_and_never_sent()
    {
        var rule = new AgentUpdateRule();
        Assert.True(rule.ShouldSend(Update(0), 0));
        var head = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f * MathF.PI / 180);
        Assert.False(rule.ShouldSend(Update(0, head: head), 0.8));
    }
}
