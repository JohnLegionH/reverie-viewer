// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;
using F = Viewer.Motion.AgentControlFlags;
using Key = Viewer.Core.Tests.MovementRig.Key;

namespace Viewer.Core.Tests;

/// <summary>
/// Running, as the official viewer does it (its llviewerinput.cpp agent_handle_doubletap_run, run_forward and
/// toggle_run, llagent.cpp sendWalkRun, llviewermessage.cpp after a region arrival): a second press of a walk key within
/// a quarter second of the last press of any walk key runs while that key stays down; the Always Run toggle runs until it
/// is toggled off; every change is told to the region with the reliable SetAlwaysRun message, and the state is told
/// again after each region arrival. Every test builds its own objects: no process-wide state, so the class runs in
/// parallel with the others.
/// </summary>
public class RunTests
{
    private static (AgentControlInput Input, List<bool> Told) NewInput()
    {
        var input = new AgentControlInput();
        var told = new List<bool>();
        input.WalkRunChanged += told.Add;
        return (input, told);
    }

    // ---------------- double-tap ----------------

    [Fact]
    public void A_second_press_of_the_same_walk_key_within_a_quarter_second_runs_while_it_is_held()
    {
        foreach (var key in new[] { MoveCommand.Forward, MoveCommand.Backward, MoveCommand.SlideLeft, MoveCommand.SlideRight })
        {
            var (input, told) = NewInput();
            input.Press(key, 0);
            input.Release(key);
            Assert.False(input.Running);
            input.Press(key, 0.2);
            Assert.True(input.Running);
            Assert.False(input.AlwaysRun);
            Assert.Equal([true], told);
            input.Release(key);
            Assert.False(input.Running);
            Assert.Equal([true, false], told);
        }
    }

    [Fact]
    public void The_double_tap_window_is_a_quarter_second_from_the_last_press_of_any_walk_key()
    {
        var (input, told) = NewInput();
        input.Press(MoveCommand.Forward, 0);
        input.Release(MoveCommand.Forward);
        input.Press(MoveCommand.Forward, 0.25);   // exactly the window: the press is not within it
        Assert.False(input.Running);
        input.Release(MoveCommand.Forward);
        input.Press(MoveCommand.Forward, 0.45);   // 0.2 s after the press at 0.25: the timer was reset by that press
        Assert.True(input.Running);
        Assert.Equal([true], told);
    }

    [Fact]
    public void A_press_of_another_walk_key_starts_the_window_for_that_key()
    {
        var (input, told) = NewInput();
        input.Press(MoveCommand.Forward, 0);
        input.Release(MoveCommand.Forward);
        input.Press(MoveCommand.Backward, 0.1);
        Assert.False(input.Running);
        input.Release(MoveCommand.Backward);
        input.Press(MoveCommand.Forward, 0.2);    // the last walk key pressed was Backward
        Assert.False(input.Running);
        input.Release(MoveCommand.Forward);
        input.Press(MoveCommand.Forward, 0.3);    // and now Forward twice in a row
        Assert.True(input.Running);
        Assert.Equal([true], told);
    }

    [Fact]
    public void Turn_keys_and_other_keys_take_no_part_in_the_double_tap()
    {
        var (input, told) = NewInput();
        input.Press(MoveCommand.TurnLeft, 0);
        input.Release(MoveCommand.TurnLeft);
        input.Press(MoveCommand.TurnLeft, 0.1);
        input.Release(MoveCommand.TurnLeft);
        input.Press(MoveCommand.Jump, 0.15);
        input.Release(MoveCommand.Jump);
        input.Press(MoveCommand.Jump, 0.2);
        Assert.False(input.Running);
        Assert.Empty(told);
    }

    [Fact]
    public void Releasing_a_different_key_does_not_end_the_temporary_run_and_pressing_one_does_not_restart_it()
    {
        var (input, told) = NewInput();
        input.Press(MoveCommand.Forward, 0);
        input.Release(MoveCommand.Forward);
        input.Press(MoveCommand.Forward, 0.1);
        input.Press(MoveCommand.SlideLeft, 0.15);   // running already: nothing is told, the window is not touched
        input.Release(MoveCommand.SlideLeft);
        Assert.True(input.Running);
        Assert.Equal([true], told);
        input.Release(MoveCommand.Forward);
        Assert.False(input.Running);
        Assert.Equal([true, false], told);
    }

    [Fact]
    public void Double_tap_running_can_be_turned_off()
    {
        var (input, told) = NewInput();
        input.AllowDoubleTapRun = false;
        input.Press(MoveCommand.Forward, 0);
        input.Release(MoveCommand.Forward);
        input.Press(MoveCommand.Forward, 0.1);
        Assert.False(input.Running);
        Assert.Empty(told);
    }

    [Fact]
    public void Double_tap_running_is_on_by_default_as_in_the_official_viewer()
    {
        Assert.True(new AgentControlInput().AllowDoubleTapRun);
    }

    [Fact]
    public void Losing_focus_ends_a_temporary_run_but_not_always_run()
    {
        var (input, told) = NewInput();
        input.Press(MoveCommand.Forward, 0);
        input.Release(MoveCommand.Forward);
        input.Press(MoveCommand.Forward, 0.1);
        input.SetFocused(false);
        Assert.False(input.Running);
        Assert.Equal([true, false], told);

        var (always, toldAlways) = NewInput();
        always.Press(MoveCommand.ToggleRun, 0);
        always.Press(MoveCommand.Forward, 0.1);
        always.SetFocused(false);
        Assert.True(always.Running);
        Assert.True(always.AlwaysRun);
        Assert.Equal([true], toldAlways);
    }

    // ---------------- always run ----------------

    [Fact]
    public void The_always_run_toggle_flips_the_default_and_tells_the_region_each_time()
    {
        var (input, told) = NewInput();
        input.Press(MoveCommand.ToggleRun, 0);
        Assert.True(input.AlwaysRun);
        Assert.True(input.Running);
        input.Press(MoveCommand.ToggleRun, 1);
        Assert.False(input.AlwaysRun);
        Assert.False(input.Running);
        Assert.Equal([true, false], told);
        Assert.Equal(0u, input.Frame(2, 1, false, 0));   // a toggle holds no key and sends no control bit
    }

    [Fact]
    public void While_always_run_is_on_releasing_a_walk_key_keeps_running_and_a_double_tap_tells_nothing()
    {
        var (input, told) = NewInput();
        input.Press(MoveCommand.ToggleRun, 0);
        input.Press(MoveCommand.Forward, 0.1);
        input.Release(MoveCommand.Forward);
        input.Press(MoveCommand.Forward, 0.2);
        input.Release(MoveCommand.Forward);
        Assert.True(input.Running);
        Assert.Equal([true], told);
    }

    [Fact]
    public void The_always_run_default_can_be_adopted_at_start_and_is_told_once()
    {
        var (input, told) = NewInput();
        input.SetAlwaysRun(true);
        input.SetAlwaysRun(true);
        Assert.True(input.AlwaysRun);
        Assert.True(input.Running);
        Assert.Equal([true], told);
    }

    [Fact]
    public void The_run_state_is_told_again_on_request_as_running_or_always_running()
    {
        var (input, told) = NewInput();
        input.ResendRun();
        input.Press(MoveCommand.Forward, 0);
        input.Release(MoveCommand.Forward);
        input.Press(MoveCommand.Forward, 0.1);
        input.ResendRun();
        input.Release(MoveCommand.Forward);
        input.Press(MoveCommand.ToggleRun, 1);
        input.Release(MoveCommand.Forward);
        input.ResendRun();
        Assert.Equal([false, true, true, false, true, true], told);
    }

    // ---------------- run keys ----------------

    [Fact]
    public void A_run_key_runs_while_held_and_moves_like_its_walk_key()
    {
        foreach (var (run, walk, nudge, move) in new[]
        {
            (MoveCommand.RunForward, MoveCommand.Forward, F.NudgeAtPos, F.AtPos | F.FastAt),
            (MoveCommand.RunBackward, MoveCommand.Backward, F.NudgeAtNeg, F.AtNeg | F.FastAt),
            (MoveCommand.RunLeft, MoveCommand.SlideLeft, F.NudgeLeftPos, F.LeftPos | F.FastLeft),
            (MoveCommand.RunRight, MoveCommand.SlideRight, F.NudgeLeftNeg, F.LeftNeg | F.FastLeft),
        })
        {
            var (input, told) = NewInput();
            input.Press(run, 0);
            Assert.True(input.Running);
            Assert.True(input.IsHeld(walk));
            Assert.Equal(nudge, input.Frame(0.1, 0.1, false, 0));
            Assert.Equal(nudge, input.Frame(0.2, 0.1, false, 0));
            Assert.Equal(move, input.Frame(0.4, 0.2, false, 0));
            input.Release(run);
            Assert.False(input.Running);
            Assert.False(input.IsHeld(walk));
            Assert.Equal([true, false], told);
        }
    }

    // ---------------- the Always Run key and the run keys' names ----------------

    [Fact]
    public void Control_R_toggles_always_run_by_default_and_the_run_keys_are_unbound_as_in_the_official_viewer()
    {
        Assert.True(KeyBindings.Default.TryCommand("R", false, out var cmd, control: true));
        Assert.Equal(MoveCommand.ToggleRun, cmd);
        Assert.False(KeyBindings.Default.TryCommand("R", false, out _));
        for (char k = 'A'; k <= 'Z'; k++)
            foreach (bool shift in new[] { false, true })
                if (KeyBindings.Default.TryCommand(k.ToString(), shift, out var bound))
                    Assert.DoesNotContain(bound, new[] { MoveCommand.RunForward, MoveCommand.RunBackward, MoveCommand.RunLeft, MoveCommand.RunRight });
        // a Control press of a key bound without Control still moves the avatar, as before
        Assert.True(KeyBindings.Default.TryCommand("W", false, out cmd, control: true));
        Assert.Equal(MoveCommand.Forward, cmd);
    }

    [Fact]
    public void A_bindings_file_can_name_the_run_commands_and_the_control_mask()
    {
        var read = KeyBindings.Parse("""
            { "bindings": [ { "key": "W", "mask": "SHIFT", "command": "run_forward" }, { "key": "S", "mask": "SHIFT", "command": "run_backward" },
              { "key": "A", "mask": "SHIFT", "command": "run_left" }, { "key": "D", "mask": "SHIFT", "command": "run_right" },
              { "key": "R", "mask": "CTL", "command": "toggle_run" } ] }
            """, out _);
        Assert.NotNull(read);
        Assert.True(read!.TryCommand("W", true, out var c) && c == MoveCommand.RunForward);
        Assert.True(read.TryCommand("S", true, out c) && c == MoveCommand.RunBackward);
        Assert.True(read.TryCommand("A", true, out c) && c == MoveCommand.RunLeft);
        Assert.True(read.TryCommand("D", true, out c) && c == MoveCommand.RunRight);
        Assert.True(read.TryCommand("R", false, out c, control: true) && c == MoveCommand.ToggleRun);
        Assert.False(read.TryCommand("R", false, out _));
    }

    // ---------------- the message ----------------

    [Fact]
    public void The_run_message_is_the_reliable_SetAlwaysRun_with_the_agent_and_session_and_the_flag()
    {
        var agent = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var session = Guid.Parse("66666666-7777-8888-9999-000000000000");
        foreach (bool run in new[] { true, false })
        {
            var packet = LmvTransport.BuildSetAlwaysRun(new LibreMetaverse.UUID(agent), new LibreMetaverse.UUID(session), run);
            Assert.True(packet.Header.Reliable);
            Assert.Equal(agent, packet.AgentData.AgentID.Guid);
            Assert.Equal(session, packet.AgentData.SessionID.Guid);
            Assert.Equal(run, packet.AgentData.AlwaysRun);
        }
    }

    [Fact]
    public void The_transport_keeps_the_run_state_it_was_last_given_for_the_next_region_arrival()
    {
        using var transport = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "run"));
        Assert.False(transport.WalkRun);
        transport.SendAlwaysRun(true);
        Assert.True(transport.WalkRun);
        transport.SendAlwaysRun(false);
        Assert.False(transport.WalkRun);
    }

    // ---------------- the driver and the drawn avatar ----------------

    [Fact]
    public void A_double_tap_tells_the_region_to_run_once_at_the_second_press_and_to_walk_at_the_release_with_no_stall()
    {
        var run = MovementRig.Run(
            [new Key(0.5, true, MoveCommand.Forward), new Key(0.58, false, MoveCommand.Forward), new Key(0.68, true, MoveCommand.Forward), new Key(3.0, false, MoveCommand.Forward)], 4.5);
        Assert.Equal(2, run.RunMessages.Count);
        Assert.True(run.RunMessages[0].Run);
        Assert.InRange(run.RunMessages[0].At, 0.68, 0.70);
        Assert.False(run.RunMessages[1].Run);
        Assert.InRange(run.RunMessages[1].At, 3.0, 3.02);
        // no stop bit and no hold of the drawn avatar, between the presses or at the final release; no stall
        Assert.Empty(run.Sent.Where(s => (s.Controls & Viewer.Motion.AgentControlFlags.Stop) != 0));
        Assert.Equal(0, run.Halts);
        double began = run.Frames.First(f => f.At > 0.5 && JerkMeter.SpeedAt(run, f.At) > 1.0f).At;
        Assert.True(JerkMeter.LowestSpeed(run, began, 3.0) > 1.0f);
        // the jerk figures while the key is held (to the release at 3.0 s; the avatar's measures 1415 over that time); after it
        // the avatar coasts to the region's stop and settles on the region's position, which is not what this limit is about
        var (avatar, camera) = JerkMeter.Of(run, 0.4, 3.0);
        Assert.True(avatar.RmsJerk <= 1420, $"the avatar's jerk figure rose to {avatar.RmsJerk:F0}");
        Assert.True(camera.RmsJerk <= 1290, $"the camera's jerk figure rose to {camera.RmsJerk:F0}");
    }

    [Fact]
    public void A_single_tap_does_not_run()
    {
        var run = MovementRig.Run([new Key(0.5, true, MoveCommand.Forward), new Key(0.58, false, MoveCommand.Forward)], 2.0);
        Assert.Empty(run.RunMessages);
    }
}
