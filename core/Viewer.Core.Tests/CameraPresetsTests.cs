// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The front, side and rear views and the views a person saves.</summary>
public class CameraPresetsTests
{
    private static (Vector3 Eye, Vector3 Target) PoseOf(FollowCamera camera, float heading = 0.7f) =>
        camera.Pose(new Vector3(100, 100, 30), heading, 0.0);

    private static void Same((Vector3 Eye, Vector3 Target) a, (Vector3 Eye, Vector3 Target) b)
    {
        Assert.True((a.Eye - b.Eye).Length() < 0.005f, $"eye {a.Eye} against {b.Eye}");
        Assert.True((a.Target - b.Target).Length() < 0.005f, $"target {a.Target} against {b.Target}");
    }

    [Fact]
    public void The_three_official_views_have_the_official_offsets()
    {
        Assert.Equal((new Vector3(-3f, 0f, 0.75f), new Vector3(1f, 0f, 1f)), (CameraPresets.Rear.Offset, CameraPresets.Rear.Focus));
        Assert.Equal((new Vector3(2.2f, 0f, 0f), new Vector3(0f, 0f, 0f)), (CameraPresets.Front.Offset, CameraPresets.Front.Focus));
        Assert.Equal((new Vector3(-1f, 0.7f, 0.5f), new Vector3(1.5f, 0.7f, 1f)), (CameraPresets.Side.Offset, CameraPresets.Side.Focus));
    }

    [Fact]
    public void The_front_view_puts_the_camera_ahead_of_the_avatar_looking_back()
    {
        var camera = new FollowCamera();
        camera.ApplyView(CameraPresets.Front.Offset, CameraPresets.Front.Focus);
        var (eye, target) = PoseOf(camera, 0f);
        Assert.True(eye.X > target.X + 2f, $"eye {eye}, target {target}");
    }

    [Fact]
    public void The_side_view_puts_the_camera_where_its_offset_says()
    {
        var camera = new FollowCamera();
        camera.ApplyView(CameraPresets.Side.Offset, CameraPresets.Side.Focus);
        var (eye, target) = PoseOf(camera, 0f);
        var head = new Vector3(100, 100, 30) + camera.ThirdPersonHeadOffset;
        Assert.True((eye - (head + CameraPresets.Side.Offset)).Length() < 0.005f, $"eye {eye}");
        Assert.True((target - (new Vector3(100, 100, 30) + CameraPresets.Side.Focus)).Length() < 0.005f, $"target {target}");
    }

    [Fact]
    public void A_view_taken_after_turning_and_zooming_puts_the_camera_back_where_it_was()
    {
        var seen = new FollowCamera();
        seen.SetOrbit(1.1f, 0.3f);
        seen.SetZoom(2.2f);
        var before = PoseOf(seen);
        var (offset, focus) = seen.CurrentView();

        var picked = new FollowCamera();
        picked.ApplyView(CameraPresets.Front.Offset, CameraPresets.Front.Focus);
        PoseOf(picked);
        picked.ApplyView(offset, focus);
        Same(before, PoseOf(picked));
    }

    [Fact]
    public void Picking_a_view_clears_the_turn_and_the_zoom_and_reset_returns_to_the_picked_view()
    {
        var camera = new FollowCamera();
        camera.SetOrbit(1f, 0.2f);
        camera.SetZoom(3f);
        camera.ApplyView(CameraPresets.Side.Offset, CameraPresets.Side.Focus);
        Assert.Equal((0f, 0f, 1f), (camera.OrbitYaw, camera.OrbitPitch, camera.Zoom));
        var picked = PoseOf(camera);
        camera.SetOrbit(2f, 0f);
        camera.Reset();
        Same(picked, PoseOf(camera));
    }

    [Fact]
    public void A_saved_view_is_found_replaced_by_name_removed_and_survives_being_written_and_read()
    {
        var list = new CameraPresets();
        Assert.True(list.Save("  Close up ", new Vector3(-1, 0, 0.2f), new Vector3(0, 0, 1)));
        Assert.True(list.Save("close up", new Vector3(-2, 0, 0.2f), new Vector3(0, 0, 1)));
        Assert.True(list.Save("Above", new Vector3(-1, 0, 4), new Vector3(1, 0, 0)));
        Assert.Equal(2, list.Saved.Count);
        var read = CameraPresets.FromText(list.ToText());
        Assert.Equal(new[] { "Above", "close up" }, new[] { read.Saved[0].Name, read.Saved[1].Name });
        Assert.Equal(new Vector3(-2, 0, 0.2f), read.Find("CLOSE UP")!.Offset);
        Assert.True(read.Remove("close up"));
        Assert.Null(read.Find("close up"));
        Assert.Single(read.Saved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Rear")]
    [InlineData("front")]
    [InlineData("SIDE")]
    public void A_view_without_a_name_or_with_a_built_in_name_is_not_saved(string name) =>
        Assert.False(new CameraPresets().Save(name, new Vector3(-1, 0, 0), default));

    [Fact]
    public void A_view_with_no_distance_or_a_number_that_is_not_one_is_not_saved()
    {
        var list = new CameraPresets();
        Assert.False(list.Save("zero", default, default));
        Assert.False(list.Save("bad", new Vector3(float.NaN, 0, 0), default));
        Assert.True(list.Save("good", new Vector3(-1, 0, 0), default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[{\"name\":\"x\"}]")]
    [InlineData("{\"a\":1}")]
    public void Unreadable_saved_text_gives_no_saved_views(string? text) => Assert.Empty(CameraPresets.FromText(text).Saved);
}
