// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Motion;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The DisableCameraConstraints setting (default off) against the official camera (llagentcamera.cpp): with it on, the camera is held to
/// no draw distance, no zoom limit and no 50 m cap in the rear view, may go a thousand metres under the land, and the unlocked view roams
/// across regions; with it off, nothing changes. Each test builds its own camera: no shared state, so the class runs in parallel.
/// </summary>
public class CameraConstraintsTests
{
    private const double Frame = 1.0 / 60;
    private static readonly Vector3 Avatar = new(100, 100, 30);

    private static AgentCamera Cam(bool off, Func<float, float, float>? land = null, float draw = 128f) =>
        new() { PositionHalfLife = 0, AvatarHeight = 1.9f, DrawDistance = draw, LandHeight = land, ConstraintsOff = off };

    [Fact]
    public void Constraints_are_on_by_default()
    {
        Assert.False(new AgentCamera().ConstraintsOff);
    }

    [Fact]
    public void With_constraints_off_the_rear_view_may_be_zoomed_out_past_the_zoom_limit_and_the_fifty_metre_cap()
    {
        // llagentcamera.cpp 1827-1828: the camera is the zoom fraction times the rear offset; cameraOrbitIn 999-1002 clamps the fraction only
        // when constraints are on
        var cam = Cam(off: true);
        cam.OrbitIn(-300f);
        var (eye, _) = cam.Pose(Avatar, 0, Frame);
        float away = (eye - (Avatar + cam.ThirdPersonHeadOffset)).Length();
        Assert.True(away > 250f, $"the camera should be about 300 m behind, was {away}");
    }

    [Fact]
    public void With_constraints_on_the_rear_view_zoom_stays_within_its_limits()
    {
        var cam = Cam(off: false);
        cam.OrbitIn(-300f);
        Assert.Equal(AgentCamera.MaxZoom, cam.Zoom, 3);
        var (eye, _) = cam.Pose(Avatar, 0, Frame);
        Assert.True((eye - (Avatar + cam.ThirdPersonHeadOffset)).Length() <= AgentCamera.MaxDistance + 0.01f);
    }

    [Fact]
    public void With_constraints_off_the_camera_may_go_beyond_the_draw_distance_and_far_under_the_land_but_not_a_thousand_metres_under()
    {
        // llagentcamera.cpp 1968 (draw distance only when constraints are on) and 1991-2000, 2122-2135 (the floor is 1000 m under the land)
        var cam = Cam(off: true, land: (_, _) => 5000f);
        var (eye, _) = cam.Pose(Avatar, 0, Frame);
        Assert.Equal(4000f, eye.Z, 2);
    }

    [Fact]
    public void With_constraints_on_the_ground_keeps_the_camera_half_a_metre_above_the_land()
    {
        var cam = Cam(off: false, land: (_, _) => 40f);
        var (eye, _) = cam.Pose(Avatar, 0, Frame);
        Assert.Equal(40.5f, eye.Z, 3);
    }

    [Fact]
    public void With_constraints_off_the_unlocked_view_roams_far_from_the_avatar()
    {
        var cam = Cam(off: true);
        cam.Pose(Avatar, 0, Frame);
        cam.UnlockView();
        for (int i = 0; i < 6; i++) { cam.PanLeft(100f); cam.Pose(Avatar, 0, Frame); }
        var (eye, _) = cam.Pose(Avatar, 0, Frame);
        Assert.True((eye - Avatar).Length() > 500f, $"the camera should have roamed 600 m, was {(eye - Avatar).Length()}");
    }

    [Fact]
    public void With_constraints_on_the_unlocked_view_stops_at_the_draw_distance()
    {
        var cam = Cam(off: false);
        cam.Pose(Avatar, 0, Frame);
        cam.UnlockView();
        for (int i = 0; i < 6; i++) { cam.PanLeft(100f); cam.Pose(Avatar, 0, Frame); }
        var (eye, _) = cam.Pose(Avatar, 0, Frame);
        Assert.Equal(128f, (eye - Avatar).Length(), 2);
    }

    [Fact]
    public void With_constraints_off_an_unlocked_zoom_out_is_not_limited_to_four_times_the_distance_or_the_region()
    {
        var cam = Cam(off: true);
        cam.Pose(Avatar, 0, Frame);
        cam.UnlockView();
        float start = cam.FocusDistance;
        cam.ZoomIn(100f);
        cam.Pose(Avatar, 0, Frame);
        Assert.True(cam.FocusDistance > start * 50f, $"{start} -> {cam.FocusDistance}");
    }

    [Fact]
    public void With_constraints_on_an_unlocked_zoom_out_is_limited_to_four_times_the_distance()
    {
        var cam = Cam(off: false);
        cam.Pose(Avatar, 0, Frame);
        cam.UnlockView();
        float start = cam.FocusDistance;
        cam.ZoomIn(100f);
        cam.Pose(Avatar, 0, Frame);
        Assert.True(cam.FocusDistance <= start * 4f + 0.01f, $"{start} -> {cam.FocusDistance}");
    }
}
