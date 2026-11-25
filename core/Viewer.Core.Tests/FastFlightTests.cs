// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Session;
using Viewer.Motion;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Fast flight: Shift held while flying tells the region the avatar runs (the SetAlwaysRun message), and releasing it tells
/// the region the state without Shift again. A region that applies no run speed to a flight flies at its usual speed. Every
/// test builds its own objects: no process-wide state, so the class runs in parallel with the others.
/// </summary>
public class FastFlightTests
{
    private static (AgentControlInput Input, List<bool> Told) NewInput(bool flying = true, bool allowed = true)
    {
        var input = new AgentControlInput { FastFlightAllowed = allowed };
        input.AdoptHeading(0);
        input.AdoptFlying(flying);
        var told = new List<bool>();
        input.WalkRunChanged += told.Add;
        return (input, told);
    }

    private static uint Frame(AgentControlInput input, double now) => input.Frame(now, 0.02, inAir: input.Flying, groundSpeed: 10);

    [Fact]
    public void Shift_held_while_flying_tells_the_region_to_run_and_releasing_it_tells_it_to_stop()
    {
        var (input, told) = NewInput();
        input.SetFastKey(true);
        Assert.True(input.FastFlying);
        Assert.Equal([true], told);
        input.SetFastKey(true);   // a repeat changes nothing
        Frame(input, 0.1);
        Assert.Equal([true], told);
        input.SetFastKey(false);
        Assert.False(input.FastFlying);
        Assert.Equal([true, false], told);
    }

    [Fact]
    public void Shift_on_the_ground_tells_the_region_nothing()
    {
        var (input, told) = NewInput(flying: false);
        input.SetFastKey(true);
        input.Press(MoveCommand.SlideLeft, 0);
        Frame(input, 0.5);
        input.Release(MoveCommand.SlideLeft);
        input.SetFastKey(false);
        Assert.False(input.FastFlying);
        Assert.Empty(told);
    }

    [Fact]
    public void Taking_off_with_Shift_held_starts_the_fast_flight_and_landing_ends_it()
    {
        var (input, told) = NewInput(flying: false);
        input.SetFastKey(true);
        input.Press(MoveCommand.ToggleFly, 0);
        Assert.Equal([true], told);
        input.Press(MoveCommand.ToggleFly, 1);
        Assert.Equal([true, false], told);
    }

    [Fact]
    public void A_flight_that_a_held_jump_starts_or_a_held_down_key_ends_follows_Shift_in_that_frame()
    {
        var (input, told) = NewInput(flying: false);
        input.SetFastKey(true);
        input.Press(MoveCommand.Jump, 0);
        for (int i = 1; i <= 6; i++) Frame(input, i * 0.2);
        Assert.True(input.Flying);
        Assert.Equal([true], told);
        input.Release(MoveCommand.Jump);
        input.Press(MoveCommand.Down, 2);
        input.Frame(2.1, 0.02, inAir: false, groundSpeed: 0);
        Assert.False(input.Flying);
        Assert.Equal([true, false], told);
    }

    [Fact]
    public void Sitting_down_or_a_saved_flight_state_follows_Shift_too()
    {
        var (input, told) = NewInput();
        input.SetFastKey(true);
        input.AdoptFlying(false);
        input.AdoptFlying(true);
        Assert.Equal([true, false, true], told);
    }

    [Fact]
    public void With_Always_Run_on_Shift_adds_no_message_and_releasing_it_does_not_stop_the_run()
    {
        var (input, told) = NewInput();
        input.SetAlwaysRun(true);
        input.SetFastKey(true);
        input.SetFastKey(false);
        Assert.Equal([true], told);
    }

    [Fact]
    public void Turning_Always_Run_off_while_Shift_is_held_keeps_the_fast_flight_until_Shift_comes_up()
    {
        var (input, told) = NewInput();
        input.SetAlwaysRun(true);
        input.SetFastKey(true);
        input.SetAlwaysRun(false);
        Assert.Equal([true], told);
        input.SetFastKey(false);
        Assert.Equal([true, false], told);
    }

    [Fact]
    public void The_end_of_a_double_tap_run_does_not_stop_a_fast_flight()
    {
        var (input, told) = NewInput();
        input.Press(MoveCommand.Forward, 0);
        input.Release(MoveCommand.Forward);
        input.Press(MoveCommand.Forward, 0.1);   // the double-tap
        input.SetFastKey(true);
        input.Release(MoveCommand.Forward);
        Assert.Equal([true], told);
        input.SetFastKey(false);
        Assert.Equal([true, false], told);
    }

    [Fact]
    public void Releasing_Shift_during_a_double_tap_run_leaves_the_run_on()
    {
        var (input, told) = NewInput();
        input.SetFastKey(true);
        input.Press(MoveCommand.Forward, 0);
        input.Release(MoveCommand.Forward);
        input.Press(MoveCommand.Forward, 0.1);
        input.SetFastKey(false);
        Assert.Equal([true], told);
        input.Release(MoveCommand.Forward);
        Assert.Equal([true, false], told);
    }

    [Fact]
    public void Where_fast_flight_is_not_allowed_Shift_sends_nothing()
    {
        var (input, told) = NewInput(allowed: false);
        input.SetFastKey(true);
        Frame(input, 0.5);
        input.SetFastKey(false);
        Assert.False(input.FastFlying);
        Assert.Empty(told);
        Assert.False(new AgentControlInput().FastFlightAllowed);   // off until the session's grid allows it
    }

    [Fact]
    public void Losing_focus_lets_go_of_Shift()
    {
        var (input, told) = NewInput();
        input.SetFastKey(true);
        input.SetFocused(false);
        Assert.False(input.FastFlying);
        Assert.Equal([true, false], told);
        input.SetFocused(true);
        Frame(input, 1);
        Assert.Equal([true, false], told);
    }

    [Fact]
    public void An_unfocused_window_takes_no_Shift_from_the_keyboard_but_takes_it_from_automation()
    {
        var (input, told) = NewInput();
        input.SetFocused(false);
        input.SetFastKey(true);
        Assert.Empty(told);
        input.SetFastKey(true, synthetic: true);
        Assert.Equal([true], told);
        input.SetFastKey(false);   // a release always counts
        Assert.Equal([true, false], told);
    }

    [Fact]
    public void An_arrival_in_a_region_during_a_fast_flight_tells_it_the_avatar_runs()
    {
        var (input, told) = NewInput();
        input.SetFastKey(true);
        input.ResendRun();
        Assert.Equal([true, true], told);
    }

    [Fact]
    public void Shift_changes_no_control_bit_of_a_held_move_key()
    {
        var (plain, _) = NewInput();
        var (fast, _) = NewInput();
        fast.SetFastKey(true);
        plain.Press(MoveCommand.Forward, 0);
        fast.Press(MoveCommand.Forward, 0);
        uint a = 0, b = 0;
        for (int i = 1; i <= 5; i++) { a = Frame(plain, i * 0.1); b = Frame(fast, i * 0.1); }
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("W", MoveCommand.Forward)]
    [InlineData("S", MoveCommand.Backward)]
    [InlineData("UP", MoveCommand.Forward)]
    [InlineData("DOWN", MoveCommand.Backward)]
    [InlineData("E", MoveCommand.Jump)]
    [InlineData("C", MoveCommand.Down)]
    [InlineData("PGUP", MoveCommand.Jump)]
    [InlineData("PGDN", MoveCommand.Down)]
    [InlineData("F", MoveCommand.ToggleFly)]
    [InlineData("HOME", MoveCommand.ToggleFly)]
    [InlineData("SPACE", MoveCommand.Stop)]
    public void A_key_with_no_Shift_binding_keeps_its_command_while_Shift_is_held(string key, MoveCommand expected)
    {
        // the official viewer applies a binding while extra modifier keys are held (llviewerinput.cpp, scanKey)
        Assert.True(KeyBindings.Default.TryCommand(key, shift: true, out var command));
        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData("A", MoveCommand.SlideLeft)]
    [InlineData("D", MoveCommand.SlideRight)]
    [InlineData("LEFT", MoveCommand.SlideLeft)]
    [InlineData("RIGHT", MoveCommand.SlideRight)]
    public void A_Shift_binding_still_wins_over_the_plain_one(string key, MoveCommand expected)
    {
        Assert.True(KeyBindings.Default.TryCommand(key, shift: true, out var command));
        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData("W")]
    [InlineData("E")]
    [InlineData("F")]
    public void A_Control_and_Shift_chord_moves_nothing(string key) =>
        Assert.False(KeyBindings.Default.TryCommand(key, shift: true, out _, control: true));

    [Fact]
    public void Fast_flight_is_declared_for_grids_other_than_Second_Life()
    {
        Assert.Equal(FeatureReach.OpenSimOnly, GridPolicy.ReachOf(ViewerFeature.FastFlight));
        Assert.False(GridPolicy.Allows(ViewerFeature.FastFlight, GridKind.SecondLife));
        Assert.True(GridPolicy.Allows(ViewerFeature.FastFlight, GridKind.OpenSim));
    }
}
