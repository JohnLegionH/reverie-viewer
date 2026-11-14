// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The user's camera against the official viewer's camera code (llagentcamera.cpp, lltoolfocus.cpp, llviewerinput.cpp, written down
/// first): the rear view and its offsets, unlocking the view, orbit around and over (1 to 179 degrees from up), zoom limits, the wheel
/// rule, pan, the reset, the terrain and draw-distance constraints, the mouse tool's masks and constants and the key rates. Each test
/// builds its own camera and shares no state, so the class runs in parallel.
/// </summary>
public class AgentCameraTests
{
    private const double Frame = 1.0 / 60;
    private static readonly Vector3 Centre = new(100, 100, 30);
    private const float Height = 1.9f;

    /// <summary>A camera with the smoothing off, so a pose is the formula's value at once.</summary>
    private static AgentCamera Cam() => new() { PositionHalfLife = 0, AvatarHeight = Height };

    private static (Vector3 Eye, Vector3 Target) Pose(AgentCamera cam, float heading = 0, Vector3? centre = null, int frames = 1)
    {
        (Vector3, Vector3) pose = default;
        for (int i = 0; i < frames; i++) pose = cam.Pose(centre ?? Centre, heading, Frame);
        return pose;
    }

    /// <summary>Where the unlocked view's focus starts: the avatar's head (the eye height used for mouse-look).</summary>
    private static Vector3 Head => Centre + new Vector3(0, 0, AgentCamera.EyeHeightFraction * Height - Height / 2);

    private static Vector3 RotZ(Vector3 v, float radians) => Vector3.Transform(v, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians));

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-3f) =>
        Assert.True((expected - actual).Length() <= tolerance, $"expected {expected}, got {actual}");

    // ---- the rear view (llagentcamera.cpp 1752-1960, settings CameraOffsetRearView and FocusOffsetRearView) ----

    [Fact]
    public void The_rear_view_is_the_avatar_plus_the_head_offset_plus_the_camera_offset_looking_at_the_focus_offset()
    {
        var (eye, target) = Pose(Cam());
        Near(new Vector3(100 - 3, 100, 30 + 1 + 0.75f), eye);   // root + (0,0,1) head offset + (-3,0,0.75)
        Near(new Vector3(100 + 1, 100, 30 + 1), target);         // root + (1,0,1)
    }

    [Fact]
    public void The_rear_view_turns_with_the_avatars_heading()
    {
        var (eye, target) = Pose(Cam(), MathF.PI / 2);
        Near(new Vector3(100, 100 - 3, 31.75f), eye);
        Near(new Vector3(100, 101, 31), target);
    }

    // ---- unlocking the view (unlockView, setFocusOnAvatar(false)) ----

    [Fact]
    public void Unlocking_keeps_the_camera_where_it_is_and_focuses_the_avatars_head()
    {
        var cam = Cam();
        var (eye0, _) = Pose(cam);
        cam.UnlockView();
        Assert.False(cam.FocusOnAvatar);
        var (eye, target) = Pose(cam);
        Near(eye0, eye);
        Near(Head, target);
    }

    [Fact]
    public void An_unlocked_focus_follows_the_avatar_it_was_set_on_and_keeps_its_offset()
    {
        var cam = Cam();
        Pose(cam);
        cam.UnlockView();
        var (e0, t0) = Pose(cam);
        var (e1, t1) = Pose(cam, 0, Centre + new Vector3(5, 2, 0));
        Near(t0 + new Vector3(5, 2, 0), t1);
        Near(e0 + new Vector3(5, 2, 0), e1);
    }

    // ---- orbit around and over (cameraOrbitAround 852, cameraOrbitOver 876) ----

    [Fact]
    public void Orbit_around_turns_the_offset_about_up_around_the_focus()
    {
        var cam = Cam();
        var (eye0, _) = Pose(cam);
        cam.UnlockView();
        Pose(cam);
        cam.OrbitAround(MathF.PI / 2);
        var (eye, target) = Pose(cam);
        Near(Head + RotZ(eye0 - Head, MathF.PI / 2), eye);
        Near(Head, target);
    }

    [Theory]
    [InlineData(0.2f)]
    [InlineData(-0.35f)]
    public void Orbit_over_changes_the_angle_from_up_by_the_angle_and_keeps_the_distance(float angle)
    {
        var cam = Cam();
        var (eye0, _) = Pose(cam);
        cam.UnlockView();
        Pose(cam);
        cam.OrbitOver(angle);
        var (eye, _) = Pose(cam);
        var before = eye0 - Head;
        var after = eye - Head;
        float angleBefore = MathF.Acos(before.Z / before.Length()), angleAfter = MathF.Acos(after.Z / after.Length());
        Assert.Equal(angleBefore - angle, angleAfter, 3);   // a positive angle takes the camera up (towards the pole)
        Assert.Equal(before.Length(), after.Length(), 3);
    }

    [Theory]
    [InlineData(10f, 1f)]
    [InlineData(-10f, 179f)]
    public void Orbit_over_stops_one_degree_from_straight_up_or_down(float angle, float degreesFromUp)
    {
        var cam = Cam();
        Pose(cam);
        cam.UnlockView();
        Pose(cam);
        cam.OrbitOver(angle);
        var (eye, _) = Pose(cam);
        var offset = eye - Head;
        Assert.Equal(degreesFromUp, MathF.Acos(offset.Z / offset.Length()) * 180 / MathF.PI, 2);
    }

    [Fact]
    public void Orbit_around_on_the_avatar_turns_the_avatar_not_the_camera()
    {
        // cameraOrbitAround while the focus is on the avatar in third person: gAgent.yaw(radians) (lines 859-862)
        var cam = Cam();
        float turned = 0, pitched = 0;
        cam.YawAvatar = r => turned += r;
        cam.PitchAvatar = r => pitched += r;
        Pose(cam);
        cam.OrbitAround(0.3f);
        cam.OrbitOver(0.1f);
        Assert.Equal(0.3f, turned, 4);
        Assert.Equal(0.1f, pitched, 4);
        Assert.True(cam.FocusOnAvatar);
    }

    // ---- zoom (cameraZoomIn 928, cameraOrbitIn 985, handleScrollWheel 2072) ----

    [Fact]
    public void Zoom_in_scales_the_distance_to_the_focus()
    {
        var cam = Cam();
        var (eye0, _) = Pose(cam);
        cam.UnlockView();
        Pose(cam);
        cam.ZoomIn(0.5f);
        var (eye, _) = Pose(cam);
        Assert.Equal((eye0 - Head).Length() * 0.5f, (eye - Head).Length(), 3);
    }

    [Fact]
    public void Zoom_in_on_a_clicked_point_stops_at_fifteen_centimetres()
    {
        var cam = Cam();
        Pose(cam);
        var point = new Vector3(110, 100, 31);
        cam.SetFocus(point);
        Pose(cam);
        cam.ZoomIn(0.0001f);
        var (eye, target) = Pose(cam);
        Near(point, target);
        Assert.Equal(0.15f, (eye - point).Length(), 3);
    }

    [Fact]
    public void Zoom_in_on_an_object_stops_at_two_centimetres()
    {
        var cam = Cam();
        Pose(cam);
        var point = new Vector3(110, 100, 31);
        cam.SetFocus(point, () => point, extents: new Vector3(1, 1, 1), isAvatar: false);
        Pose(cam);
        cam.ZoomIn(0.0001f);
        var (eye, _) = Pose(cam);
        Assert.Equal(0.02f, (eye - point).Length(), 3);
    }

    [Fact]
    public void Zoom_out_is_at_most_four_times_the_current_distance_and_never_past_the_draw_distance()
    {
        var cam = Cam();
        cam.DrawDistance = 21f;   // the limit is draw distance - 1 = 20
        Pose(cam);
        cam.UnlockView();
        Pose(cam);
        float distance = (Pose(cam).Eye - Head).Length();
        cam.ZoomIn(100f);
        Assert.Equal(MathF.Min(distance * 4f, 20f), (Pose(cam).Eye - Head).Length(), 2);
        var far = Cam();
        far.DrawDistance = 1000f;
        Pose(far);
        far.UnlockView();
        Pose(far);
        float d = (Pose(far).Eye - Head).Length();
        far.ZoomIn(100f);
        Assert.Equal(d * 4f, (Pose(far).Eye - Head).Length(), 2);
    }

    [Fact]
    public void The_wheel_on_the_avatar_zooms_by_the_fourth_root_of_two_per_click()
    {
        var cam = Cam();
        Pose(cam);
        cam.ScrollWheel(-1);   // a click away from the user zooms in
        Assert.Equal(MathF.Pow(2f, -0.25f), cam.Zoom, 4);
        cam.ScrollWheel(1);
        cam.ScrollWheel(1);
        Assert.Equal(MathF.Pow(2f, 0.25f), cam.Zoom, 4);
    }

    [Fact]
    public void The_wheel_stops_at_eight_times_the_default_distance()
    {
        var cam = Cam();
        Pose(cam);
        for (int i = 0; i < 60; i++) { cam.ScrollWheel(1); Pose(cam); }
        Assert.Equal(8f, cam.Zoom, 4);
    }

    [Fact]
    public void The_wheel_past_a_quarter_of_the_default_distance_enters_mouse_look()
    {
        var cam = Cam();
        Pose(cam);
        bool entered = false;
        for (int i = 0; i < 20 && !entered; i++) { entered = cam.ScrollWheel(-1); Pose(cam); }
        Assert.True(entered);
        Assert.Equal(CameraViewMode.Mouselook, cam.Mode);
    }

    [Fact]
    public void The_wheel_on_an_unlocked_view_scales_the_distance_to_the_focus()
    {
        var cam = Cam();
        var (eye0, _) = Pose(cam);
        cam.UnlockView();
        Pose(cam);
        cam.ScrollWheel(-1);
        Assert.Equal((eye0 - Head).Length() * MathF.Pow(2f, -0.25f), (Pose(cam).Eye - Head).Length(), 3);
    }

    // ---- pan (cameraPanLeft 1066, cameraPanUp 1088) ----

    [Fact]
    public void Pan_left_moves_the_focus_and_the_camera_along_the_cameras_left_axis()
    {
        var cam = Cam();
        var (eye0, target0) = Pose(cam);   // looking east: left is north (+Y)
        cam.UnlockView();
        Pose(cam);
        cam.PanLeft(2f);
        var (eye, target) = Pose(cam, frames: 1);
        Near(Head + new Vector3(0, 2, 0), target);
        Near(eye0 + new Vector3(0, 2, 0), eye, 0.02f);
    }

    [Fact]
    public void Pan_up_moves_the_focus_along_the_cameras_up_axis()
    {
        var cam = Cam();
        var (eye0, _) = Pose(cam);
        cam.UnlockView();
        var (_, t0) = Pose(cam);
        var forward = Vector3.Normalize(t0 - eye0);
        var left = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, forward));
        var up = Vector3.Cross(forward, left);
        cam.PanUp(1.5f);
        var (_, target) = Pose(cam);
        Near(Head + up * 1.5f, target, 0.01f);
    }

    // ---- reset (Esc: setFocusOnAvatar(true), resetView(true, true); walking: resetView()) ----

    [Fact]
    public void Reset_view_puts_the_default_view_back_with_the_default_zoom()
    {
        var cam = Cam();
        var (eye0, target0) = Pose(cam);
        cam.UnlockView();
        Pose(cam);
        cam.OrbitAround(1f);
        cam.OrbitOver(0.3f);
        cam.PanLeft(2f);
        cam.ScrollWheel(1);
        Pose(cam);
        cam.ResetView(changeCamera: true);
        Assert.True(cam.FocusOnAvatar);
        Assert.Equal(1f, cam.Zoom, 4);
        var (eye, target) = Pose(cam);
        Near(eye0, eye);
        Near(target0, target);
    }

    [Fact]
    public void Walking_after_an_orbit_turns_the_avatar_to_face_where_the_camera_looks_and_keeps_the_zoom()
    {
        // setFocusOnAvatar(true) with reset_axes: the avatar takes the camera's flattened at axis (llagentcamera.cpp 2710-2733)
        var cam = Cam();
        float? faced = null;
        cam.FaceHeading += h => faced = h;
        Pose(cam);
        cam.ScrollWheel(1);
        float zoom = cam.Zoom;
        cam.UnlockView();
        Pose(cam);
        cam.OrbitAround(MathF.PI / 2);   // the camera now stands south of the avatar looking north
        Pose(cam);
        cam.ResetView();
        Assert.NotNull(faced);
        Assert.Equal(MathF.PI / 2, faced!.Value, 2);
        Assert.Equal(zoom, cam.Zoom, 4);
        Assert.True(cam.FocusOnAvatar);
    }

    [Fact]
    public void Esc_resets_the_view_without_turning_the_avatar()
    {
        // handle_reset_view (llviewermenu.cpp 4596-4604): setFocusOnAvatar(true, false, false) first, so reset_axes is off; resetView(true, true) then finds the focus already on the avatar
        var cam = Cam();
        bool faced = false;
        cam.FaceHeading += _ => faced = true;
        var (eye0, _) = Pose(cam);
        cam.UnlockView();
        Pose(cam);
        cam.OrbitAround(MathF.PI / 2);
        Pose(cam);
        cam.ResetView(changeCamera: true, turnAvatar: false);
        Assert.False(faced);
        Assert.True(cam.FocusOnAvatar);
        Near(eye0, Pose(cam).Eye);
    }

    [Fact]
    public void Reset_view_with_the_focus_already_on_the_avatar_does_not_turn_it()
    {
        var cam = Cam();
        bool faced = false;
        cam.FaceHeading += _ => faced = true;
        Pose(cam);
        cam.ResetView();
        Assert.False(faced);
    }

    // ---- constraints (calcCameraPositionTargetGlobal 1968-2008, getCameraMinOffGround 2122) ----

    [Fact]
    public void The_camera_stays_half_a_metre_above_the_ground()
    {
        var cam = Cam();
        cam.LandHeight = (_, _) => 32.4f;   // the default eye is at 31.75: under the ground
        var (eye, _) = Pose(cam);
        Assert.Equal(32.9f, eye.Z, 3);
    }

    [Fact]
    public void The_ground_does_not_move_a_camera_that_is_above_it()
    {
        var cam = Cam();
        cam.LandHeight = (_, _) => 20f;
        Near(new Vector3(97, 100, 31.75f), Pose(cam).Eye);
    }

    [Fact]
    public void The_camera_is_never_farther_from_the_avatar_than_the_draw_distance()
    {
        var cam = Cam();
        cam.DrawDistance = 2f;
        var (eye, _) = Pose(cam);
        Assert.Equal(2f, (eye - Centre).Length(), 3);
    }

    [Fact]
    public void A_user_who_may_manage_the_estate_is_not_held_by_the_draw_distance_or_the_ground()
    {
        var cam = Cam();
        cam.DrawDistance = 2f;
        cam.LandHeight = (_, _) => 40f;
        cam.ConstraintsOff = true;
        Near(new Vector3(97, 100, 31.75f), Pose(cam).Eye);
    }

    // ---- modes ----

    [Fact]
    public void Changing_to_third_person_from_mouse_look_resets_the_zoom_and_starts_at_the_avatar()
    {
        var cam = Cam();
        Pose(cam);
        cam.ScrollWheel(1);
        cam.ChangeToMouselook();
        Assert.Equal(CameraViewMode.Mouselook, cam.Mode);
        cam.ChangeToThirdPerson();
        Assert.Equal(CameraViewMode.ThirdPerson, cam.Mode);
        Assert.Equal(1f, cam.Zoom, 4);
        Assert.Equal(AgentCamera.MinDistance, cam.Distance, 3);
    }

    [Fact]
    public void Mouse_look_is_refused_when_it_is_turned_off()
    {
        var cam = Cam();
        cam.MouselookAllowed = false;
        cam.ChangeToMouselook();
        Assert.Equal(CameraViewMode.ThirdPerson, cam.Mode);
    }

    /// <summary>
    /// The camera centres on the avatar where it is drawn, in every frame: the agent's position is the avatar's render position
    /// (llvoavatar.cpp, updateCharacter: gAgent.setPositionAgent(getRenderPosition())), which the drawable has already damped, and the
    /// camera smooths only its offset from the avatar, in the avatar's space, over less than 50 m (llagentcamera.cpp, updateCamera,
    /// MAX_CAMERA_SMOOTH_DISTANCE). So the camera travels with the avatar, for a frame's step of flight and for a larger correction alike.
    /// </summary>
    [Theory]
    [InlineData(0.27f)]   // a frame of flight at 16 m/s
    [InlineData(5f)]      // a correction the drawable damps
    [InlineData(12f)]     // a step past the drawable's 10 m snap
    public void The_camera_travels_with_the_avatar_where_it_is_drawn_with_no_damping_of_its_own(float step)
    {
        var cam = new AgentCamera { AvatarHeight = Height };   // the official smoothing on
        for (int i = 0; i < 120; i++) cam.Pose(Centre, 0, Frame);   // settled
        var (eye0, target0) = cam.Pose(Centre, 0, Frame);
        var moved = Centre + new Vector3(0, step, 0);
        var (eye1, target1) = cam.Pose(moved, 0, Frame);
        Assert.True((eye1 - moved - (eye0 - Centre)).Length() < 1e-3f, $"eye {eye1 - moved} from the avatar, was {eye0 - Centre}");
        Assert.True((target1 - moved - (target0 - Centre)).Length() < 1e-3f, $"target {target1 - moved} from the avatar, was {target0 - Centre}");
    }
}
