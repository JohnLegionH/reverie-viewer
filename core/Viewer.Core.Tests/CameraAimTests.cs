// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Camera;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Aiming a test camera and judging the picture: where the eye goes for a distance and two angles, where a target lands on the screen,
/// and the check that a target fills enough of the frame and sits in the middle. Every member builds its own values: no shared state.
/// </summary>
public class CameraAimTests
{
    private const float W = 1920, H = 1080;
    private static readonly Vector3 Soles = new(100, 120, 25);

    private static void Near(Vector3 want, Vector3 got, float tol = 0.01f)
    {
        Assert.True(Vector3.Distance(want, got) <= tol, $"wanted {want}, got {got}");
    }

    [Fact]
    public void The_eye_stands_in_front_of_a_target_that_faces_east()
    {
        var pose = CameraAim.Pose(new Vector3(10, 20, 30), 3f, 0f, 0f, 0f, 40f);
        Near(new Vector3(13, 20, 30), pose.Eye);
        Near(new Vector3(10, 20, 30), pose.Target);
        Assert.Equal(40f, pose.FovDeg);
    }

    [Fact]
    public void The_angle_around_is_measured_from_the_way_the_target_faces_and_ninety_is_its_left()
    {
        // facing north (yaw 90 degrees): in front is +y, its left is -x, behind is -y
        float north = MathF.PI / 2;
        Near(new Vector3(10, 22, 30), CameraAim.Pose(new Vector3(10, 20, 30), 2f, 0f, 0f, north, 40f).Eye);
        Near(new Vector3(8, 20, 30), CameraAim.Pose(new Vector3(10, 20, 30), 2f, 90f, 0f, north, 40f).Eye);
        Near(new Vector3(10, 18, 30), CameraAim.Pose(new Vector3(10, 20, 30), 2f, 180f, 0f, north, 40f).Eye);
    }

    [Fact]
    public void The_angle_above_raises_the_eye_and_keeps_the_distance()
    {
        var pose = CameraAim.Pose(new Vector3(0, 0, 0), 4f, 0f, 30f, 0f, 40f);
        Near(new Vector3(4 * MathF.Cos(MathF.PI / 6), 0, 2f), pose.Eye);
        Assert.Equal(4f, Vector3.Distance(pose.Eye, pose.Target), 3);
    }

    [Fact]
    public void The_yaw_of_a_rotation_is_the_way_it_faces()
    {
        Assert.Equal(MathF.PI / 2, CameraAim.YawOf(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)), 3);
        Assert.Equal(0f, CameraAim.YawOf(Quaternion.Identity), 3);
    }

    [Fact]
    public void The_aimed_point_lands_in_the_middle_of_the_frame()
    {
        var pose = CameraAim.Pose(new Vector3(10, 20, 30), 3f, 35f, 10f, 0.4f, 40f);
        Assert.True(CameraAim.Project(pose, W, H, pose.Target, out var px));
        Assert.Equal(W / 2, px.X, 1);
        Assert.Equal(H / 2, px.Y, 1);
    }

    [Fact]
    public void A_point_behind_the_camera_does_not_project()
    {
        var pose = CameraAim.Pose(new Vector3(0, 0, 0), 3f, 0f, 0f, 0f, 40f);   // the eye is at (3,0,0) looking west
        Assert.True(CameraAim.Project(pose, W, H, new Vector3(-5, 0, 0), out _));
        Assert.False(CameraAim.Project(pose, W, H, new Vector3(50, 0, 0), out _));
    }

    [Fact]
    public void A_point_above_the_middle_lands_in_the_upper_half_of_the_frame()
    {
        var pose = CameraAim.Pose(new Vector3(0, 0, 0), 3f, 0f, 0f, 0f, 40f);
        // the frame's top edge is at tan(20 degrees) of the distance above the middle
        Assert.True(CameraAim.Project(pose, W, H, new Vector3(0, 0, 3f * MathF.Tan(MathF.PI / 9)), out var px));
        Assert.Equal(0f, px.Y, 1);
    }

    [Fact]
    public void The_head_and_chest_boxes_stand_at_the_heights_of_a_person()
    {
        var (head, _) = CameraAim.AvatarBox(AimPoint.Head, Soles, 1.8f);
        var (chest, _) = CameraAim.AvatarBox(AimPoint.Chest, Soles, 1.8f);
        var (body, half) = CameraAim.AvatarBox(AimPoint.Body, Soles, 1.8f);
        Assert.Equal(25 + 1.8f * 0.9688f, head.Z, 2);   // the middle of the head: halfway from the head joint to the top of the skull
        Assert.Equal(25 + 1.8f * 0.72f, chest.Z, 2);
        Assert.Equal(25 + 0.9f, body.Z, 2);
        Assert.Equal(0.9f, half.Z, 2);
    }

    [Fact]
    public void The_head_point_is_above_the_head_joint_and_below_the_top_of_the_skull()
    {
        // the default skeleton: the head joint stands 1.6827 m above the soles and the skull's end 1.7947 m (avatar_skeleton.xml: the joints' positions added up the chain, the skull's end)
        const float headJoint = 1.682703f, skullTop = 1.794703f;
        var (head, _) = CameraAim.AvatarBox(AimPoint.Head, Soles, skullTop);
        float above = head.Z - Soles.Z;
        Assert.InRange(above, headJoint + 0.03f, skullTop - 0.03f);
        Assert.Equal((headJoint + skullTop) / 2f, above, 2);
    }

    [Fact]
    public void The_head_point_follows_the_head_and_skull_joints_of_the_character_definition()
    {
        // the real skeleton file (local input): the same sum, read from the file, not from this file's constants
        string? dir = Environment.GetEnvironmentVariable("NV_CHARACTER_DIR");
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "avatar_skeleton.xml"))) return;   // not a failure: the local input is not here
        var doc = System.Xml.Linq.XDocument.Load(Path.Combine(dir, "avatar_skeleton.xml"));
        float Z(string name) => float.Parse(doc.Descendants("bone").First(b => (string?)b.Attribute("name") == name).Attribute("pos")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2], System.Globalization.CultureInfo.InvariantCulture);
        float EndZ(string name) => float.Parse(doc.Descendants("bone").First(b => (string?)b.Attribute("name") == name).Attribute("end")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2], System.Globalization.CultureInfo.InvariantCulture);
        float headJoint = Z("mPelvis") + Z("mTorso") + Z("mChest") + Z("mNeck") + Z("mHead");
        float skullTop = headJoint + Z("mSkull") + EndZ("mSkull");
        var (head, _) = CameraAim.AvatarBox(AimPoint.Head, Soles, skullTop);
        Assert.Equal((headJoint + skullTop) / 2f, head.Z - Soles.Z, 2);
    }

    [Fact]
    public void A_whole_body_from_three_metres_passes_the_check()
    {
        var (c, half) = CameraAim.AvatarBox(AimPoint.Body, Soles, 1.8f);
        var pose = CameraAim.Pose(c, 3f, 0f, 0f, 0f, 40f);
        var box = CameraAim.BoxOf(pose, W, H, CameraAim.Corners(c, half, 0f));
        var report = CameraAim.Check(box, W, H, 0.5f, 0.1f);
        Assert.True(report.Pass, string.Join(",", report.Reasons));
        Assert.InRange(report.Fill, 0.7f, 0.95f);
        Assert.InRange(report.CentreOffset, 0f, 0.02f);
    }

    [Fact]
    public void A_face_from_one_metre_passes_the_check()
    {
        var (c, half) = CameraAim.AvatarBox(AimPoint.Head, Soles, 1.8f);
        var pose = CameraAim.Pose(c, 1f, 0f, 0f, 0f, 40f);
        var box = CameraAim.BoxOf(pose, W, H, CameraAim.Corners(c, half, 0f));
        var report = CameraAim.Check(box, W, H, 0.25f, 0.1f);
        Assert.True(report.Pass, string.Join(",", report.Reasons));
    }

    [Fact]
    public void A_tiny_figure_fails_as_too_small()
    {
        var (c, half) = CameraAim.AvatarBox(AimPoint.Body, Soles, 1.8f);
        var pose = CameraAim.Pose(c, 30f, 0f, 0f, 0f, 40f);
        var box = CameraAim.BoxOf(pose, W, H, CameraAim.Corners(c, half, 0f));
        var report = CameraAim.Check(box, W, H, 0.5f, 0.1f);
        Assert.False(report.Pass);
        Assert.Contains("too_small", report.Reasons);
        Assert.InRange(report.Fill, 0.01f, 0.2f);
    }

    [Fact]
    public void A_figure_at_the_edge_fails_as_off_centre()
    {
        var (c, half) = CameraAim.AvatarBox(AimPoint.Body, Soles, 1.8f);
        var pose = CameraAim.Pose(c, 3f, 0f, 0f, 0f, 40f);
        var elsewhere = new AimPose(pose.Eye, pose.Target + new Vector3(0, 1.2f, 0), pose.FovDeg);   // the camera looks 1.2 m to one side
        var box = CameraAim.BoxOf(elsewhere, W, H, CameraAim.Corners(c, half, 0f));
        var report = CameraAim.Check(box, W, H, 0.5f, 0.1f);
        Assert.False(report.Pass);
        Assert.Contains("off_centre", report.Reasons);
        Assert.True(report.CentreOffset > 0.1f);
    }

    [Fact]
    public void A_figure_cut_off_by_the_frame_fails_as_cut_off()
    {
        var (c, half) = CameraAim.AvatarBox(AimPoint.Body, Soles, 1.8f);
        var pose = CameraAim.Pose(c, 1.5f, 0f, 0f, 0f, 40f);   // so close that the head and feet are outside the frame
        var box = CameraAim.BoxOf(pose, W, H, CameraAim.Corners(c, half, 0f));
        var report = CameraAim.Check(box, W, H, 0.5f, 0.5f);
        Assert.False(report.Pass);
        Assert.Contains("cut_off", report.Reasons);
        Assert.True(report.VisibleShare < 0.9f);
    }

    [Fact]
    public void A_target_behind_the_camera_fails()
    {
        var (c, half) = CameraAim.AvatarBox(AimPoint.Body, Soles, 1.8f);
        var pose = CameraAim.Pose(c, 3f, 0f, 0f, 0f, 40f);
        var turned = new AimPose(pose.Eye, pose.Eye + (pose.Eye - pose.Target), pose.FovDeg);   // looking away
        var box = CameraAim.BoxOf(turned, W, H, CameraAim.Corners(c, half, 0f));
        var report = CameraAim.Check(box, W, H, 0.5f, 0.1f);
        Assert.False(report.Pass);
        Assert.Contains("behind_camera", report.Reasons);
    }

    [Fact]
    public void A_wider_frame_keeps_the_size_of_the_box_because_the_field_of_view_is_the_vertical_one()
    {
        var (c, half) = CameraAim.AvatarBox(AimPoint.Body, Soles, 1.8f);
        var pose = CameraAim.Pose(c, 3f, 0f, 0f, 0f, 40f);
        var corners = CameraAim.Corners(c, half, 0f);
        var square = CameraAim.BoxOf(pose, 1080, 1080, corners);
        var wide = CameraAim.BoxOf(pose, 1920, 1080, corners);
        Assert.Equal(square.Height, wide.Height, 1);
        Assert.Equal(square.Width, wide.Width, 1);
        Assert.Equal(960f, (wide.Left + wide.Right) / 2, 1);   // and it stays in the middle of the wider frame
    }
}
