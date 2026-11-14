// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What the Move &amp; View settings change: flying by holding the jump key, the follow camera's smoothing, and the arrow keys while
/// the chat bar has the keyboard. Plain .NET; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class MoveViewPreferenceTests
{
    private const double Frame60 = 1.0 / 60;
    private const uint Fly = 0x00002000u;

    private static uint HoldJump(AgentControlInput input, double seconds)
    {
        input.Press(MoveCommand.Jump, 0);
        uint bits = 0;
        for (double t = Frame60; t <= seconds + 1e-9; t += Frame60) bits = input.Frame(t, Frame60, false, 0);
        return bits;
    }

    [Fact]
    public void Fly_by_holding_jump_is_on_by_default_and_a_long_hold_flies()
    {
        var input = new AgentControlInput();
        Assert.True(input.AutomaticFly);
        Assert.NotEqual(0u, HoldJump(input, 0.6) & Fly);
        Assert.True(input.Flying);
    }

    [Fact]
    public void With_fly_by_holding_off_a_long_hold_of_jump_keeps_jumping_and_does_not_fly()
    {
        var input = new AgentControlInput { AutomaticFly = false };
        uint bits = HoldJump(input, 0.8);
        Assert.NotEqual(0u, bits & 0x10u);   // still going up: the jump bit
        Assert.Equal(0u, bits & Fly);
        Assert.False(input.Flying);
    }

    [Fact]
    public void With_fly_by_holding_off_the_fly_key_still_flies()
    {
        Assert.Equal(0u, HoldJump(new AgentControlInput { AutomaticFly = false }, 0.8) & Fly);   // holding jump does nothing to the flight state
        var input = new AgentControlInput { AutomaticFly = false };
        input.Press(MoveCommand.ToggleFly, 0);
        Assert.NotEqual(0u, input.Frame(0.1, 0.1, true, 0) & Fly);
        Assert.True(input.Flying);
    }

    [Fact]
    public void The_camera_smoothing_setting_scales_the_official_half_life_and_zero_turns_it_off()
    {
        var cam = new FollowCamera();
        Assert.Equal(1f, cam.Smoothing);
        Assert.Equal(FollowCamera.PositionSmoothingHalfLife, cam.PositionHalfLife, 6);
        cam.Smoothing = 2f;
        Assert.Equal(2f * FollowCamera.PositionSmoothingHalfLife, cam.PositionHalfLife, 6);
        cam.Smoothing = 0f;
        Assert.Equal(0f, cam.PositionHalfLife);
        cam.Smoothing = 9f;
        Assert.Equal(4f, cam.Smoothing);
        Assert.Equal(4f * FollowCamera.PositionSmoothingHalfLife, cam.PositionHalfLife, 6);
        cam.Smoothing = float.NaN;
        Assert.Equal(1f, cam.Smoothing);
    }

    private static InputFocus Focus() => new(new FloaterManager());

    [Fact]
    public void Arrow_keys_always_move_is_off_by_default_and_while_typing_no_key_moves_the_avatar()
    {
        var f = Focus();
        f.SetWindowFocused(true);
        Assert.False(f.ArrowKeysAlwaysMove);
        Assert.True(f.MovementAllowedFor(arrowKey: true));    // not typing: any key
        Assert.True(f.MovementAllowedFor(arrowKey: false));
        f.SetTextEntryActive(true);
        Assert.False(f.MovementAllowedFor(arrowKey: true));
        Assert.False(f.MovementAllowedFor(arrowKey: false));
    }

    [Fact]
    public void With_arrow_keys_always_move_the_arrows_move_the_avatar_while_typing_and_other_keys_do_not()
    {
        var f = Focus();
        f.SetWindowFocused(true);
        f.ArrowKeysAlwaysMove = true;
        f.SetTextEntryActive(true);
        Assert.True(f.MovementAllowedFor(arrowKey: true));
        Assert.False(f.MovementAllowedFor(arrowKey: false));
        Assert.False(f.MovementAllowed);   // the general rule is unchanged: typing still keeps the letters
    }

    [Fact]
    public void The_avatar_controls_accept_keys_while_typing_only_when_arrow_keys_always_move()
    {
        var f = Focus();
        var seen = new List<bool>();
        f.DriverFocusedChanged += seen.Add;
        Assert.False(f.DriverFocused);                 // no window focus yet
        f.SetWindowFocused(true);
        Assert.True(f.DriverFocused);
        f.SetTextEntryActive(true);
        Assert.False(f.DriverFocused);                 // typing, option off: the controls ignore keys
        f.ArrowKeysAlwaysMove = true;
        Assert.True(f.DriverFocused);                  // the option turned on while typing takes effect at once
        f.ArrowKeysAlwaysMove = false;
        Assert.False(f.DriverFocused);
        f.ArrowKeysAlwaysMove = true;
        f.SetTextEntryActive(false);
        Assert.True(f.DriverFocused);
        f.SetWindowFocused(false);
        Assert.False(f.DriverFocused);                 // never without the window's focus
        Assert.Equal([true, false, true, false, true, false], seen);   // each change once, and nothing for a change that changed nothing
    }

    [Fact]
    public void No_key_moves_the_avatar_when_the_window_is_not_focused_whatever_the_setting()
    {
        var f = Focus();
        f.SetWindowFocused(false);
        f.ArrowKeysAlwaysMove = true;
        f.SetTextEntryActive(true);
        Assert.False(f.MovementAllowedFor(arrowKey: true));
        f.SetTextEntryActive(false);
        Assert.False(f.MovementAllowedFor(arrowKey: true));
        f.SetWindowFocused(true);
        Assert.True(f.MovementAllowedFor(arrowKey: true));   // the same keys move the avatar once the window has focus
    }
}
