// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Motion;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The camera's farthest distance from its focus when the camera constraints are off. The official viewer's cameraZoomIn and cameraOrbitIn
/// (llagentcamera.cpp) clamp the new distance to getCameraMaxZoomDistance() in that case too, and with the constraints off that distance is
/// INT_MAX metres, so a zoom out can no longer grow without limit. Plain .NET; every member builds its own camera, so the class runs in parallel.
/// </summary>
public class CameraZoomLimitTests
{
    private const double Frame = 1.0 / 60;
    private static readonly Vector3 Centre = new(100, 100, 30);
    private static readonly Vector3 Point = new(110, 100, 31);

    /// <summary>(F32)INT_MAX: what getCameraMaxZoomDistance returns when the camera constraints are off.</summary>
    private const float OfficialUnlimited = 2147483648f;

    private static AgentCamera FocusedCamera(bool constraintsOff)
    {
        var cam = new AgentCamera { PositionHalfLife = 0, AvatarHeight = 1.9f, ConstraintsOff = constraintsOff };
        cam.Pose(Centre, 0, Frame);
        cam.SetFocus(Point, () => Point, extents: new Vector3(1, 1, 1), isAvatar: false);
        cam.Pose(Centre, 0, Frame);
        return cam;
    }

    [Fact]
    public void With_the_constraints_off_a_zoom_out_from_a_focus_stops_at_the_largest_distance_the_official_viewer_allows()
    {
        var cam = FocusedCamera(constraintsOff: true);
        cam.ZoomIn(1e12f);
        cam.Pose(Centre, 0, Frame);
        Assert.True(cam.FocusDistance > 1e6f, $"the camera should have gone a long way out, was {cam.FocusDistance}");
        Assert.InRange(cam.FocusDistance, OfficialUnlimited * 0.999f, OfficialUnlimited * 1.001f);
    }

    [Fact]
    public void With_the_constraints_off_orbiting_out_from_a_focus_stops_at_the_same_largest_distance()
    {
        var cam = FocusedCamera(constraintsOff: true);
        cam.OrbitIn(-3e9f);
        cam.Pose(Centre, 0, Frame);
        Assert.True(cam.FocusDistance > 1e6f, $"the camera should have gone a long way out, was {cam.FocusDistance}");
        Assert.InRange(cam.FocusDistance, OfficialUnlimited * 0.999f, OfficialUnlimited * 1.001f);
    }

    [Fact]
    public void With_the_constraints_off_a_zoom_that_stays_under_the_largest_distance_is_not_limited()
    {
        var cam = FocusedCamera(constraintsOff: true);
        float before = cam.FocusDistance;
        cam.ZoomIn(1000f);
        cam.Pose(Centre, 0, Frame);
        // a thousand times the distance to the focus is not held to the draw distance or the region any more
        Assert.InRange(cam.FocusDistance, before * 990f, before * 1010f);
    }

    [Fact]
    public void With_the_constraints_on_a_zoom_out_is_still_held_to_four_times_the_distance_and_the_region()
    {
        var cam = FocusedCamera(constraintsOff: false);
        float before = cam.FocusDistance;
        cam.ZoomIn(1e12f);
        cam.Pose(Centre, 0, Frame);
        Assert.InRange(cam.FocusDistance, before * 3.9f, before * 4.1f);
    }
}
