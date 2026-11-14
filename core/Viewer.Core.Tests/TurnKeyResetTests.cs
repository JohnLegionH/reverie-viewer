// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Motion;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Which keys put the camera back on the avatar. In the official viewer every call that moves the avatar except the look keys ends in
/// resetView(): moveAt, moveAtNudge, moveLeft, moveLeftNudge and moveUp (llagent.cpp 666-791), moveYaw, whose reset_view argument
/// defaults to true (llagent.cpp 796-822, llagent.h 539), which the turn-left and turn-right keys call (llviewerinput.cpp 268-307), and
/// toggleFlying (llagent.cpp 925-939). movePitch does not reset. A key held down resets again every frame. Each test builds its own objects.
/// </summary>
public class TurnKeyResetTests
{
    private const double Frame = 1.0 / 60;
    private static readonly Vector3 Avatar = new(100, 100, 30);

    private static (AgentControlInput Input, AgentCamera Cam) Rig()
    {
        var input = new AgentControlInput();
        var cam = new AgentCamera { PositionHalfLife = 0 };
        input.WalkKeyPressed += cam.ResetOrbit;   // the same hook the host makes
        cam.Pose(Avatar, 0, Frame);
        return (input, cam);
    }

    public static IEnumerable<object[]> ResettingKeys() =>
    [
        [MoveCommand.Forward], [MoveCommand.Backward], [MoveCommand.SlideLeft], [MoveCommand.SlideRight],
        [MoveCommand.RunForward], [MoveCommand.RunBackward], [MoveCommand.RunLeft], [MoveCommand.RunRight],
        [MoveCommand.TurnLeft], [MoveCommand.TurnRight], [MoveCommand.Jump], [MoveCommand.Down], [MoveCommand.ToggleFly],
    ];

    public static IEnumerable<object[]> KeepingKeys() => [[MoveCommand.Stop], [MoveCommand.ToggleRun]];

    [Theory, MemberData(nameof(ResettingKeys))]
    public void A_key_that_moves_the_avatar_puts_the_camera_back_on_it(MoveCommand key)
    {
        var (input, cam) = Rig();
        cam.UnlockView();
        Assert.False(cam.FocusOnAvatar);
        input.Press(key, 0);
        Assert.True(cam.FocusOnAvatar, $"{key} should reset the view");
    }

    [Theory, MemberData(nameof(KeepingKeys))]
    public void A_key_that_does_not_move_the_avatar_leaves_the_camera_where_it_is(MoveCommand key)
    {
        var (input, cam) = Rig();
        cam.UnlockView();
        input.Press(key, 0);
        Assert.False(cam.FocusOnAvatar, $"{key} should not reset the view");
    }

    [Theory]
    [InlineData(MoveCommand.TurnLeft)]
    [InlineData(MoveCommand.TurnRight)]
    [InlineData(MoveCommand.Forward)]
    [InlineData(MoveCommand.Jump)]
    public void A_key_held_down_resets_the_view_again_every_frame_and_a_released_one_does_not(MoveCommand key)
    {
        var (input, cam) = Rig();
        input.Press(key, 0);
        cam.UnlockView();                                   // an Alt-drag while the key is held
        input.Frame(Frame, Frame, false, 0);
        Assert.True(cam.FocusOnAvatar, "a held key resets the view in every frame (resetView in the key handler)");
        input.Release(key);
        cam.UnlockView();
        input.Frame(2 * Frame, Frame, false, 0);
        Assert.False(cam.FocusOnAvatar, "a released key leaves the view alone");
    }

    [Fact]
    public void The_turn_keys_in_mouse_look_do_nothing()
    {
        var (input, cam) = Rig();
        cam.UnlockView();
        input.EnterMouseLook();
        input.Press(MoveCommand.TurnLeft, 0);
        Assert.False(cam.FocusOnAvatar);
    }
}
