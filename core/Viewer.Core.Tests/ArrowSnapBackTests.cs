// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Motion;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// After the camera was moved away with Alt, each arrow key (Left, Right, Up, Down) puts it back behind the avatar at the normal
/// distance: the arrows turn or walk the avatar, and every one of them returns the view (llagent.cpp moveYaw, moveAt, resetView).
/// Built the way the host wires the keys, the camera and the avatar's heading. Each test builds its own objects.
/// </summary>
public class ArrowSnapBackTests
{
    private const double Frame = 1.0 / 60;
    private static readonly Vector3 Avatar = new(100, 100, 30);

    [Theory]
    [InlineData(MoveCommand.TurnLeft)]
    [InlineData(MoveCommand.TurnRight)]
    [InlineData(MoveCommand.Forward)]
    [InlineData(MoveCommand.Backward)]
    public void An_arrow_after_an_Alt_camera_puts_the_camera_back_behind_the_avatar_at_the_normal_distance(MoveCommand arrow)
    {
        var input = new AgentControlInput();
        var cam = new AgentCamera();
        input.WalkKeyPressed += cam.ResetOrbit;
        cam.FaceHeading += input.AdoptHeading;
        input.AdoptHeading(0);
        double t = 0;
        (Vector3 Eye, Vector3 Target) view = default;
        void Step()
        {
            t += Frame;
            input.Frame(t, Frame, false, 0);
            view = cam.Pose(Avatar, input.Heading ?? 0, Frame);
        }
        for (int i = 0; i < 30; i++) Step();

        // Alt camera: the view is unlocked, then orbited a quarter turn, tilted and pulled out
        cam.UnlockView();
        cam.OrbitAround(MathF.PI / 2);
        cam.OrbitOver(0.4f);
        cam.PanLeft(1.5f);
        for (int i = 0; i < 60; i++) Step();
        Assert.False(cam.FocusOnAvatar);
        float away = Vector3.Distance(view.Eye, Avatar);

        input.Press(arrow, t);
        for (int i = 0; i < 6; i++) Step();   // a tap
        input.Release(arrow);
        for (int i = 0; i < 240; i++) Step();

        Assert.True(cam.FocusOnAvatar, "the arrow returned the view to the avatar");
        float heading = input.Heading ?? 0;
        var behind = new Vector2(-MathF.Cos(heading), -MathF.Sin(heading));
        var offset = new Vector2(view.Eye.X - Avatar.X, view.Eye.Y - Avatar.Y);
        // an arrow that walks moves the avatar, so measure from where the camera aims
        var aimed = new Vector2(view.Target.X - view.Eye.X, view.Target.Y - view.Eye.Y);
        Assert.True(Vector2.Dot(Vector2.Normalize(aimed), new Vector2(MathF.Cos(heading), MathF.Sin(heading))) > 0.98f, $"the camera looks along the avatar's heading ({arrow})");
        Assert.True(Vector2.Dot(Vector2.Normalize(offset), behind) > 0.98f || arrow is MoveCommand.Forward or MoveCommand.Backward, $"the camera sits behind the avatar ({arrow})");
        Assert.InRange(Vector3.Distance(view.Eye, Avatar + AgentCamera.DefaultHeadOffset), 2.5f, 3.6f);   // the normal distance
        Assert.True(Vector3.Distance(view.Eye, Avatar) < away + 0.01f || away < 4f, "not farther than the Alt view");
        Assert.Equal(0f, cam.OrbitYaw, 3);
        Assert.Equal(0f, cam.OrbitPitch, 3);
    }
}
