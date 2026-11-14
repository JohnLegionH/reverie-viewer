// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The Alt camera as a person uses it, against the official camera tool (lltoolfocus.cpp, llagentcamera.cpp): the pointer's vertical movement
/// is the window's (y down) while the official tool reads it with y up, so pulling the pointer up zooms in; an Alt-click on the ground
/// focuses that point and Alt-drags carry the camera to it, again and again across the region; the camera never goes under the land; the
/// ground height between two ground samples is interpolated. Each test builds its own camera: no shared state, so the class runs in parallel.
/// </summary>
public class CameraCrossingTests
{
    private const double Frame = 1.0 / 60;
    private const float Height = 1.9f;
    private const float View = 1920f;

    private static float Hills(float x, float y) => 30f + 8f * MathF.Sin(x / 17f) + 5f * MathF.Cos(y / 11f);

    private static AgentCamera Cam(Func<float, float, float>? land, float draw = 512f) =>
        new() { PositionHalfLife = 0, AvatarHeight = Height, LandHeight = land, DrawDistance = draw };

    // ---- the pointer's direction: the window's y runs down, the official tool's runs up ----

    [Fact]
    public void Pulling_the_pointer_up_with_Alt_held_zooms_in_and_pushing_it_down_zooms_out()
    {
        var cam = Cam(null);
        var avatar = new Vector3(100, 100, 30);
        cam.Pose(avatar, 0, Frame);
        cam.UnlockView();
        cam.SetFocus(new Vector3(130, 100, 30));
        cam.Pose(avatar, 0, Frame);
        var controls = new CameraControls(cam);
        float start = cam.FocusDistance;
        controls.MouseDown(CameraMask.Alt, true);
        controls.MouseMoveScreen(0, -40, CameraMask.Alt, View);   // 40 pixels up
        cam.Pose(avatar, 0, Frame);
        float closer = cam.FocusDistance;
        Assert.Equal(start * MathF.Pow(0.99f, 40f), closer, 0.01f);
        controls.MouseMoveScreen(0, 40, CameraMask.Alt, View);    // and back down
        cam.Pose(avatar, 0, Frame);
        Assert.Equal(start, cam.FocusDistance, 0.01f);
    }

    [Fact]
    public void Pulling_the_pointer_up_with_Ctrl_and_Alt_held_lowers_the_camera_about_the_focus_and_pushing_it_down_raises_it()
    {
        var cam = Cam(null);
        var avatar = new Vector3(100, 100, 30);
        cam.Pose(avatar, 0, Frame);
        cam.UnlockView();
        cam.SetFocus(new Vector3(130, 100, 30));
        cam.Pose(avatar, 0, Frame);
        var controls = new CameraControls(cam);
        controls.MouseDown(CameraMask.Control | CameraMask.Alt, true);
        float before = cam.Pose(avatar, 0, Frame).Eye.Z;
        controls.MouseMoveScreen(0, 60, CameraMask.Control | CameraMask.Alt, View);   // down: the camera goes up
        float up = cam.Pose(avatar, 0, Frame).Eye.Z;
        controls.MouseMoveScreen(0, -120, CameraMask.Control | CameraMask.Alt, View); // up: the camera goes down
        float down = cam.Pose(avatar, 0, Frame).Eye.Z;
        Assert.True(up > before, $"pushing the pointer down should raise the camera: {before} -> {up}");
        Assert.True(down < up, $"pulling the pointer up should lower the camera: {up} -> {down}");
    }

    [Fact]
    public void Pulling_the_pointer_up_with_Ctrl_Shift_and_Alt_held_pans_the_view_down()
    {
        var cam = Cam(null);
        var avatar = new Vector3(100, 100, 30);
        cam.Pose(avatar, 0, Frame);
        cam.UnlockView();
        cam.SetFocus(new Vector3(130, 100, 30));
        cam.Pose(avatar, 0, Frame);
        var controls = new CameraControls(cam);
        controls.MouseDown(CameraMask.Control | CameraMask.Shift | CameraMask.Alt, true);
        float before = cam.FocusPoint.Z;
        controls.MouseMoveScreen(0, -50, CameraMask.Control | CameraMask.Shift | CameraMask.Alt, View);
        cam.Pose(avatar, 0, Frame);
        Assert.True(cam.FocusPoint.Z < before - 0.1f, $"focus z {before} -> {cam.FocusPoint.Z}");
    }

    // ---- crossing the region from one standing spot, as the person does ----

    /// <summary>Where the camera's forward ray meets the ground, found by marching along it (what the pick finds under the pointer).</summary>
    private static Vector3 GroundAhead(Vector3 eye, Vector3 forward, Func<float, float, float> land, float horizontal)
    {
        var flat = new Vector3(forward.X, forward.Y, 0);
        flat = Vector3.Normalize(flat);
        float x = eye.X + flat.X * horizontal, y = eye.Y + flat.Y * horizontal;
        return new Vector3(x, y, land(x, y));
    }

    private static (Vector3 Eye, Vector3 Target) Settle(AgentCamera cam, Vector3 avatar, Func<float, float, float> land, ref float lowest)
    {
        (Vector3, Vector3) pose = default;
        for (int i = 0; i < 4; i++)
        {
            pose = cam.Pose(avatar, 0, Frame);
            lowest = MathF.Min(lowest, pose.Item1.Z - land(pose.Item1.X, pose.Item1.Y));
        }
        return pose;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Alt_clicking_the_ground_ahead_and_pulling_the_pointer_up_carries_the_camera_across_the_region_and_never_under_the_land(bool hills)
    {
        Func<float, float, float> land = hills ? Hills : (_, _) => 30f;
        var avatar = new Vector3(10, 128, land(10, 128) + Height / 2);
        var cam = Cam(land);
        var controls = new CameraControls(cam);
        float lowest = float.MaxValue;
        var pose = Settle(cam, avatar, land, ref lowest);
        var forward = Vector3.Normalize(pose.Target - pose.Eye);
        for (int hop = 0; hop < 12 && pose.Eye.X < 235f; hop++)
        {
            var point = GroundAhead(pose.Eye, forward, land, 100f);
            cam.UnlockView();
            cam.SetFocus(point);
            Assert.True(controls.MouseDown(CameraMask.Alt, true));
            for (int step = 0; step < 14; step++)
            {
                controls.MouseMoveScreen(0, -50, CameraMask.Alt, View);   // the pointer pulled up, 50 pixels at a time
                pose = Settle(cam, avatar, land, ref lowest);
            }
            controls.MouseUp();
            forward = Vector3.Normalize(new Vector3(1, 0, -0.2f));
        }
        Assert.True(pose.Eye.X > 230f, $"the camera crossed to x = {pose.Eye.X:N1} only");
        Assert.True(lowest >= AgentCamera.MinOffGround - 0.01f, $"the camera went to {lowest:N2} m above the land");
    }

    [Fact]
    public void Tilting_under_a_focused_ground_point_and_zooming_out_keeps_the_camera_above_the_land_and_lets_it_come_back_out()
    {
        Func<float, float, float> land = Hills;
        var avatar = new Vector3(100, 100, land(100, 100) + Height / 2);
        var cam = Cam(land);
        var controls = new CameraControls(cam);
        float lowest = float.MaxValue;
        Settle(cam, avatar, land, ref lowest);
        cam.UnlockView();
        cam.SetFocus(new Vector3(120, 100, land(120, 100)));
        controls.MouseDown(CameraMask.Control | CameraMask.Alt, true);
        for (int i = 0; i < 60; i++)
        {
            controls.MouseMoveScreen(0, -30, CameraMask.Control | CameraMask.Alt, View);   // pulled up: the camera goes down the sphere
            Settle(cam, avatar, land, ref lowest);
        }
        controls.MouseUp();
        var low = cam.Pose(avatar, 0, Frame).Eye;
        Assert.True(low.Z - land(low.X, low.Y) < 1.5f, $"the camera should have gone down to the land: {low.Z - land(low.X, low.Y):N2} m above it");
        Assert.True(lowest >= AgentCamera.MinOffGround - 0.01f, $"lowest {lowest:N2}");
        // tilting back the other way brings it up again, as far as the person pushes (nothing keeps it down)
        controls.MouseDown(CameraMask.Control | CameraMask.Alt, true);
        for (int i = 0; i < 40; i++) controls.MouseMoveScreen(0, 20, CameraMask.Control | CameraMask.Alt, View);
        var pose = Settle(cam, avatar, land, ref lowest);
        Assert.True(pose.Eye.Z - land(pose.Eye.X, pose.Eye.Y) > AgentCamera.MinOffGround + 0.05f, $"still lying on the ground at {pose.Eye.Z - land(pose.Eye.X, pose.Eye.Y):N2} m");
    }

    // ---- a full turn and the pitch limits around a focused point ----

    [Fact]
    public void Orbiting_a_full_turn_around_a_focused_point_comes_back_to_where_it_started_at_the_same_distance()
    {
        var cam = Cam(null);
        var avatar = new Vector3(100, 100, 30);
        cam.Pose(avatar, 0, Frame);
        cam.UnlockView();
        cam.SetFocus(new Vector3(130, 100, 30));
        var start = cam.Pose(avatar, 0, Frame).Eye;
        var controls = new CameraControls(cam);
        controls.MouseDown(CameraMask.Control | CameraMask.Alt, true);
        for (int i = 0; i < 8; i++) controls.MouseMoveScreen(View / 16f, 0, CameraMask.Control | CameraMask.Alt, View);   // one view width is one full turn
        var half = cam.Pose(avatar, 0, Frame).Eye;
        Assert.True((half - start).Length() > 1.8f * (start - cam.FocusPoint).Length() * 0.9f, $"half a turn should put the camera on the far side: {start} -> {half}");
        for (int i = 0; i < 8; i++) controls.MouseMoveScreen(View / 16f, 0, CameraMask.Control | CameraMask.Alt, View);
        var end = cam.Pose(avatar, 0, Frame).Eye;
        Assert.True((end - start).Length() < 0.05f, $"{start} -> {end}");
        Assert.Equal((start - cam.FocusPoint).Length(), cam.FocusDistance, 0.01f);
    }

    [Fact]
    public void Tilting_around_a_focused_point_stops_one_degree_from_straight_up_and_from_straight_down()
    {
        var cam = Cam(null);
        var avatar = new Vector3(100, 100, 30);
        cam.Pose(avatar, 0, Frame);
        cam.UnlockView();
        cam.SetFocus(new Vector3(130, 100, 30));
        cam.Pose(avatar, 0, Frame);
        var controls = new CameraControls(cam);
        controls.MouseDown(CameraMask.Control | CameraMask.Alt, true);
        for (int i = 0; i < 40; i++) controls.MouseMoveScreen(0, 100, CameraMask.Control | CameraMask.Alt, View);   // pushed down: up over the top
        var top = cam.Pose(avatar, 0, Frame).Eye - cam.FocusPoint;
        Assert.Equal(1f, MathF.Acos(top.Z / top.Length()) * 180f / MathF.PI, 0.05f);
        for (int i = 0; i < 80; i++) controls.MouseMoveScreen(0, -100, CameraMask.Control | CameraMask.Alt, View);
        var bottom = cam.Pose(avatar, 0, Frame).Eye - cam.FocusPoint;
        Assert.Equal(179f, MathF.Acos(bottom.Z / bottom.Length()) * 180f / MathF.PI, 0.05f);
    }

    // ---- the ground between two samples ----

    [Fact]
    public void The_ground_height_at_a_sample_is_the_sample_and_between_samples_it_is_interpolated()
    {
        float Sample(int x, int y) => x * 2f + y * 10f;   // a plane: bilinear reproduces it exactly
        Assert.Equal(Sample(5, 7), TerrainHeight.Bilinear(Sample, 5f, 7f), 1e-4f);
        Assert.Equal(5.5f * 2f + 7f * 10f, TerrainHeight.Bilinear(Sample, 5.5f, 7f), 1e-4f);
        Assert.Equal(5.25f * 2f + 7.75f * 10f, TerrainHeight.Bilinear(Sample, 5.25f, 7.75f), 1e-4f);
    }

    [Fact]
    public void The_ground_height_on_a_ridge_between_samples_is_the_mean_of_the_four_around_it()
    {
        float Sample(int x, int y) => (x + y) % 2 == 0 ? 10f : 20f;
        Assert.Equal(15f, TerrainHeight.Bilinear(Sample, 3.5f, 3.5f), 1e-4f);
    }
}
