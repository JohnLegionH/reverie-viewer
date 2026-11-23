// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Motion;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Left-dragging the own avatar, against the official viewer: a left press on the own avatar (or an attachment of it with no touch
/// handler) hands the drag to the camera tool with mouse steering on (lltoolpie.cpp handleLeftClickPick, the own-avatar branch;
/// lltoolfocus.cpp pickCallback and handleHover). The horizontal movement turns the avatar, one turn per view width, and the vertical
/// movement pitches the agent's frame instead of zooming; the rear view's camera and focus offsets turn with that pitch
/// (llagentcamera.cpp calcThirdPersonFocusOffset and the rear offset turned by the agent's frame), within LLAgent::pitch's limits, and a
/// reset of the view while the button is up levels it again with a 0.3 s time constant. Each test builds its own camera: no shared
/// state, so the class runs in parallel.
/// </summary>
public sealed class CameraSteerTests
{
    private const double Frame = 1.0 / 60;
    private static readonly Vector3 Centre = new(100, 100, 30);
    private const float Height = 1.9f;
    private const float ViewWidth = 2000f;
    private const float RadiansPerPixel = 2 * MathF.PI / ViewWidth;

    private static AgentCamera Cam() => new() { PositionHalfLife = 0, AvatarHeight = Height };

    private static (Vector3 Eye, Vector3 Target) Pose(AgentCamera cam, float heading = 0) => cam.Pose(Centre, heading, Frame);

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-3f) =>
        Assert.True((expected - actual).Length() <= tolerance, $"expected {expected}, got {actual}");

    private static float DegreesFromUp(Vector3 v) => MathF.Acos(Math.Clamp(Vector3.Normalize(v).Z, -1f, 1f)) * 180f / MathF.PI;

    [Fact]
    public void A_drag_on_the_own_avatar_turns_it_right_with_the_pointer_and_tilts_with_the_vertical_movement_instead_of_zooming()
    {
        var cam = Cam();
        float turned = 0;
        cam.YawAvatar = r => turned += r;
        Pose(cam);
        var tool = new CameraControls(cam);
        Assert.True(tool.MouseDown(CameraMask.None, validPoint: true, mouseSteering: true));
        Assert.True(tool.MouseSteering);

        tool.MouseMoveScreen(100, 0, CameraMask.None, ViewWidth);           // pointer right: cameraOrbitAround(-dx * rpp) = gAgent.yaw, to the right
        Assert.Equal(-100 * RadiansPerPixel, turned, 4);

        tool.MouseMoveScreen(0, 100, CameraMask.None, ViewWidth);           // pointer pulled down (y up: dy = -100): cameraOrbitOver(+100 * rpp)
        Assert.Equal(100 * RadiansPerPixel, cam.AvatarPitch, 4);           // the at axis turns down: the camera rises behind the avatar
        Assert.Equal(1f, cam.Zoom, 5);                                       // no zoom while steering

        tool.MouseMoveScreen(0, -40, CameraMask.None, ViewWidth);           // pointer pushed up: the pitch comes back by the same rate
        Assert.Equal(60 * RadiansPerPixel, cam.AvatarPitch, 4);

        tool.MouseUp();
        Assert.False(tool.MouseSteering);                                    // releaseMouse clears mMouseSteering
    }

    [Fact]
    public void The_same_drag_without_mouse_steering_still_zooms()
    {
        var cam = Cam();
        Pose(cam);
        var tool = new CameraControls(cam);
        tool.MouseDown(CameraMask.Alt, validPoint: true);
        Assert.False(tool.MouseSteering);
        tool.MouseMoveScreen(0, 100, CameraMask.Alt, ViewWidth);
        Assert.Equal(0f, cam.AvatarPitch);
        Assert.NotEqual(1f, cam.Zoom);
    }

    [Fact]
    public void A_left_press_on_the_own_avatar_steers_only_without_Alt_in_third_person_and_with_time_running()
    {
        Assert.True(CameraControls.SteersOnOwnAvatar(CameraMask.None, thirdPerson: true, freezeTime: false, hitOwnAvatar: true));
        Assert.True(CameraControls.SteersOnOwnAvatar(CameraMask.Control, thirdPerson: true, freezeTime: false, hitOwnAvatar: true));
        Assert.False(CameraControls.SteersOnOwnAvatar(CameraMask.Alt, thirdPerson: true, freezeTime: false, hitOwnAvatar: true));
        Assert.False(CameraControls.SteersOnOwnAvatar(CameraMask.None, thirdPerson: false, freezeTime: false, hitOwnAvatar: true));
        Assert.False(CameraControls.SteersOnOwnAvatar(CameraMask.None, thirdPerson: true, freezeTime: true, hitOwnAvatar: true));
        Assert.False(CameraControls.SteersOnOwnAvatar(CameraMask.None, thirdPerson: true, freezeTime: false, hitOwnAvatar: false));
    }

    [Fact]
    public void The_agents_pitch_turns_the_rear_views_camera_and_focus_offsets_about_the_avatars_left_axis()
    {
        var cam = Cam();
        Pose(cam);
        cam.OrbitOver(0.3f);                                                 // on the avatar in third person: gAgent.pitch
        Assert.Equal(0.3f, cam.AvatarPitch, 5);
        var (eye, target) = Pose(cam);
        float c = MathF.Cos(0.3f), s = MathF.Sin(0.3f);
        // camera: root + head offset (0,0,1) + (-3,0,0.75) turned down about left; focus: root + (1,0,1) turned the same way
        Near(new Vector3(100 + (-3 * c + 0.75f * s), 100, 31 + (3 * s + 0.75f * c)), eye);
        Near(new Vector3(100 + (c + s), 100, 30 + (-s + c)), target);
    }

    [Fact]
    public void The_pitch_turns_with_the_heading()
    {
        var cam = Cam();
        Pose(cam, MathF.PI / 2);
        cam.OrbitOver(0.3f);
        var (eye, _) = Pose(cam, MathF.PI / 2);
        float c = MathF.Cos(0.3f), s = MathF.Sin(0.3f);
        Near(new Vector3(100, 100 + (-3 * c + 0.75f * s), 31 + (3 * s + 0.75f * c)), eye);   // facing north: behind is south
    }

    [Fact]
    public void Pitching_down_stops_with_the_at_axis_179_degrees_from_up()
    {
        var cam = Cam();
        Pose(cam);
        cam.OrbitOver(2.0f);
        Assert.Equal(89f * MathF.PI / 180f, cam.AvatarPitch, 4);
    }

    [Fact]
    public void Pitching_up_stops_with_the_camera_looking_5_degrees_from_straight_up()
    {
        var cam = Cam();
        var (eye, target) = Pose(cam);
        for (int i = 0; i < 100; i++)
        {
            cam.OrbitOver(-0.02f);
            (eye, target) = Pose(cam);
        }
        Assert.True(cam.AvatarPitch < -0.5f, $"pitch {cam.AvatarPitch}");
        float fromUp = DegreesFromUp(target - eye);
        Assert.InRange(fromUp, 4.9f, 6.5f);
    }

    [Fact]
    public void A_reset_with_the_button_up_levels_the_pitch_with_a_three_tenths_second_time_constant()
    {
        var cam = Cam();
        Pose(cam);
        cam.OrbitOver(0.4f);
        Pose(cam);
        cam.ResetView();
        // resetView: at = lerp(at, flattened at, 1 - 2^(-dt / 0.3)), then the frame is rebuilt from it
        float k = 1f - MathF.Pow(2f, -(float)Frame / 0.3f);
        float horizontal = MathF.Cos(0.4f) + k * (1f - MathF.Cos(0.4f)), down = MathF.Sin(0.4f) * (1f - k);
        Assert.Equal(MathF.Atan2(down, horizontal), cam.AvatarPitch, 4);
        Assert.True(cam.AvatarPitch < 0.4f - 1e-3f);
    }

    [Fact]
    public void A_reset_while_the_left_button_is_held_keeps_the_pitch()
    {
        var cam = Cam();
        Pose(cam);
        cam.OrbitOver(0.4f);
        Pose(cam);
        cam.LeftMouseDown = true;
        cam.ResetView();
        Assert.Equal(0.4f, cam.AvatarPitch, 5);
    }
}
