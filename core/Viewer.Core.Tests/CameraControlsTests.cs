// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The camera's mouse tool and keys against the official viewer (lltoolfocus.cpp handleHover; llviewerinput.cpp and key_bindings.xml):
/// which mask does what, the 4-pixel slop, the radians and metres per pixel, the 0.99 zoom factor, a sky click doing nothing, the key
/// names, the nudge rate 0.05 rising to 1 over a quarter second and the per-frame rates. Each expected value is produced by the same
/// camera operation the official code names, on a second camera, so the test fixes which operation and which constant. No shared state.
/// </summary>
public class CameraControlsTests
{
    private const double Frame = 1.0 / 60;
    private static readonly Vector3 Centre = new(100, 100, 30);

    private static AgentCamera Unlocked()
    {
        var cam = new AgentCamera { PositionHalfLife = 0 };
        cam.Pose(Centre, 0, Frame);
        cam.UnlockView();
        cam.Pose(Centre, 0, Frame);
        return cam;
    }

    private static void Same(AgentCamera expected, AgentCamera actual)
    {
        var (e1, t1) = expected.Pose(Centre, 0, Frame);
        var (e2, t2) = actual.Pose(Centre, 0, Frame);
        Assert.True((e1 - e2).Length() < 1e-3f && (t1 - t2).Length() < 1e-3f, $"expected {e1} -> {t1}, got {e2} -> {t2}");
        Assert.True((e1 - Unlocked().Pose(Centre, 0, Frame).Eye).Length() > 1e-3f, "the camera did not move at all");
    }

    // ---- the mouse ----

    [Fact]
    public void Control_drag_orbits_around_by_minus_dx_and_over_by_minus_dy_in_a_turn_of_the_view_width()
    {
        var cam = Unlocked();
        var c = new CameraControls(cam);
        Assert.True(c.MouseDown(CameraMask.Control, validPoint: true));
        c.MouseMove(30, -20, CameraMask.Control, 1000);
        var expected = Unlocked();
        float perPixel = 2 * MathF.PI / 1000;
        expected.OrbitAround(-30 * perPixel);
        expected.OrbitOver(20 * perPixel);
        Same(expected, cam);
    }

    [Fact]
    public void Control_alt_drag_orbits_like_control_drag()
    {
        var a = Unlocked();
        var b = Unlocked();
        var ca = new CameraControls(a);
        var cb = new CameraControls(b);
        ca.MouseDown(CameraMask.Control, true);
        cb.MouseDown(CameraMask.Control | CameraMask.Alt, true);
        ca.MouseMove(25, 10, CameraMask.Control, 800);
        cb.MouseMove(25, 10, CameraMask.Control | CameraMask.Alt, 800);
        Same(a, b);
    }

    [Fact]
    public void Control_shift_drag_pans_by_three_times_the_distance_over_the_view_width_per_pixel()
    {
        var cam = Unlocked();
        float distance = cam.FocusDistance;
        var c = new CameraControls(cam);
        c.MouseDown(CameraMask.Control | CameraMask.Shift, true);
        c.MouseMove(40, 10, CameraMask.Control | CameraMask.Shift, 1000);
        var expected = Unlocked();
        float metresPerPixel = 3f * distance / 1000;
        expected.PanLeft(40 * metresPerPixel);
        expected.PanUp(-10 * metresPerPixel);
        Same(expected, cam);
    }

    [Fact]
    public void Alt_drag_turns_around_with_dx_and_zooms_by_point_ninety_nine_to_the_dy()
    {
        var cam = Unlocked();
        var c = new CameraControls(cam);
        c.MouseDown(CameraMask.Alt, true);
        c.MouseMove(0, 12, CameraMask.Alt, 1000);   // vertical slop passed in one move (12 >= 4)
        c.MouseMove(20, 0, CameraMask.Alt, 1000);
        var expected = Unlocked();
        expected.ZoomIn(MathF.Pow(0.99f, 12));
        expected.OrbitAround(-20 * 2 * MathF.PI / 1000);
        Same(expected, cam);
    }

    [Fact]
    public void A_drag_that_stays_inside_four_pixels_does_nothing()
    {
        var cam = Unlocked();
        var before = cam.Pose(Centre, 0, Frame);
        var c = new CameraControls(cam);
        c.MouseDown(CameraMask.Control, true);
        c.MouseMove(1, 1, CameraMask.Control, 1000);
        c.MouseMove(1, -1, CameraMask.Control, 1000);   // 2 pixels in each direction in all
        var after = cam.Pose(Centre, 0, Frame);
        Assert.True((before.Eye - after.Eye).Length() < 1e-4f);
    }

    [Fact]
    public void A_click_on_the_sky_is_not_a_valid_point_and_the_drag_moves_nothing()
    {
        var cam = Unlocked();
        var before = cam.Pose(Centre, 0, Frame);
        var c = new CameraControls(cam);
        Assert.False(c.MouseDown(CameraMask.Alt, validPoint: false));
        c.MouseMove(300, 300, CameraMask.Alt, 1000);
        var after = cam.Pose(Centre, 0, Frame);
        Assert.True((before.Eye - after.Eye).Length() < 1e-4f);
        Assert.False(c.Dragging);
    }

    [Fact]
    public void Moving_without_a_button_down_or_after_it_is_released_does_nothing()
    {
        var cam = Unlocked();
        var before = cam.Pose(Centre, 0, Frame);
        var c = new CameraControls(cam);
        c.MouseMove(300, 300, CameraMask.Control, 1000);
        c.MouseDown(CameraMask.Control, true);
        c.MouseUp();
        c.MouseMove(300, 300, CameraMask.Control, 1000);
        Assert.True((before.Eye - cam.Pose(Centre, 0, Frame).Eye).Length() < 1e-4f);
    }

    // ---- the keys ----

    [Theory]
    [InlineData("LEFT", CameraMask.Alt, CameraKey.SpinAroundCw)]
    [InlineData("A", CameraMask.Alt, CameraKey.SpinAroundCw)]
    [InlineData("RIGHT", CameraMask.Alt, CameraKey.SpinAroundCcw)]
    [InlineData("D", CameraMask.Alt, CameraKey.SpinAroundCcw)]
    [InlineData("UP", CameraMask.Alt, CameraKey.MoveIn)]
    [InlineData("W", CameraMask.Alt, CameraKey.MoveIn)]
    [InlineData("DOWN", CameraMask.Alt, CameraKey.MoveOut)]
    [InlineData("S", CameraMask.Alt, CameraKey.MoveOut)]
    [InlineData("PGUP", CameraMask.Alt, CameraKey.SpinOver)]
    [InlineData("E", CameraMask.Alt, CameraKey.SpinOver)]
    [InlineData("PGDN", CameraMask.Alt, CameraKey.SpinUnder)]
    [InlineData("C", CameraMask.Alt, CameraKey.SpinUnder)]
    [InlineData("W", CameraMask.Alt | CameraMask.Control, CameraKey.SpinOver)]
    [InlineData("UP", CameraMask.Alt | CameraMask.Control, CameraKey.SpinOver)]
    [InlineData("S", CameraMask.Alt | CameraMask.Control, CameraKey.SpinUnder)]
    [InlineData("DOWN", CameraMask.Alt | CameraMask.Control, CameraKey.SpinUnder)]
    [InlineData("A", CameraMask.Alt | CameraMask.Control | CameraMask.Shift, CameraKey.PanLeft)]
    [InlineData("RIGHT", CameraMask.Alt | CameraMask.Control | CameraMask.Shift, CameraKey.PanRight)]
    [InlineData("W", CameraMask.Alt | CameraMask.Control | CameraMask.Shift, CameraKey.PanUp)]
    [InlineData("DOWN", CameraMask.Alt | CameraMask.Control | CameraMask.Shift, CameraKey.PanDown)]
    public void The_bound_keys_are_the_official_ones(string name, CameraMask mask, CameraKey expected)
    {
        Assert.True(CameraControls.TryKey(name, mask, out var key));
        Assert.Equal(expected, key);
    }

    [Theory]
    [InlineData("LEFT", CameraMask.None)]
    [InlineData("W", CameraMask.Shift)]
    [InlineData("A", CameraMask.Control)]
    [InlineData("A", CameraMask.Alt | CameraMask.Shift)]
    [InlineData("F", CameraMask.Alt)]
    public void Other_keys_and_masks_are_not_camera_keys(string name, CameraMask mask) =>
        Assert.False(CameraControls.TryKey(name, mask, out _));

    [Theory]
    [InlineData(0.0, 0.05f)]
    [InlineData(0.125, 0.525f)]
    [InlineData(0.25, 1f)]
    [InlineData(3.0, 1f)]
    public void The_key_rate_rises_from_five_percent_to_full_over_a_quarter_second(double held, float rate) =>
        Assert.Equal(rate, CameraControls.OrbitRate(held), 3);

    [Fact]
    public void A_held_orbit_key_unlocks_the_view_and_turns_it_ninety_degrees_a_second_at_full_rate()
    {
        var cam = new AgentCamera { PositionHalfLife = 0 };
        cam.Pose(Centre, 0, Frame);
        var c = new CameraControls(cam);
        c.KeyDown(CameraKey.SpinAroundCcw, 0);
        Assert.False(cam.FocusOnAvatar);
        var expected = Unlocked();
        for (int i = 0; i < 60; i++)
        {
            double now = 1.0 + i * Frame;   // held for longer than a quarter second: full rate
            c.Frame(now, Frame);
            cam.Pose(Centre, 0, Frame);
        }
        // spin_around_ccw = setOrbitLeftKey: orbit around by +rate * 90 degrees / second
        float perFrame = MathF.PI / 2 * (float)Frame;
        for (int i = 0; i < 60; i++) { expected.OrbitAround(perFrame); expected.Pose(Centre, 0, Frame); }
        Same(expected, cam);
    }

    [Fact]
    public void A_key_let_go_stops_turning_and_a_new_press_starts_again_at_the_nudge_rate()
    {
        var cam = new AgentCamera { PositionHalfLife = 0 };
        cam.Pose(Centre, 0, Frame);
        var c = new CameraControls(cam);
        c.KeyDown(CameraKey.SpinOver, 0);
        c.Frame(0.0, Frame);
        var first = cam.Pose(Centre, 0, Frame).Eye;
        c.KeyUp(CameraKey.SpinOver);
        c.Frame(1.0, Frame);
        var held = cam.Pose(Centre, 0, Frame).Eye;
        Assert.True((first - held).Length() < 1e-4f, "a released key kept turning the view");
        var expected = Unlocked();
        expected.OrbitOver(0.05f * MathF.PI / 2 * (float)Frame);
        Assert.True((expected.Pose(Centre, 0, Frame).Eye - first).Length() < 1e-3f, "the first frame is not at the nudge rate");
    }

    [Fact]
    public void A_held_pan_key_moves_the_focus_five_metres_a_second()
    {
        var cam = new AgentCamera { PositionHalfLife = 0 };
        cam.Pose(Centre, 0, Frame);
        var c = new CameraControls(cam);
        c.KeyDown(CameraKey.PanLeft, 0);
        var start = cam.Pose(Centre, 0, Frame).Target;
        for (int i = 0; i < 60; i++) { c.Frame(1.0 + i * Frame, Frame); cam.Pose(Centre, 0, Frame); }
        var moved = cam.Pose(Centre, 0, Frame).Target - start;
        Assert.Equal(5f, moved.Length(), 1);
        Assert.True(moved.Y > 0, "the camera looks east, so its left is north");
    }

    [Fact]
    public void The_move_in_key_asks_for_mouse_look_when_the_zoom_passes_the_closest()
    {
        var cam = new AgentCamera { PositionHalfLife = 0 };
        cam.Pose(Centre, 0, Frame);
        var c = new CameraControls(cam);
        // an unlocked view moves in to the focus and stops short of it: mouse-look is only for the camera behind the avatar
        c.KeyDown(CameraKey.MoveIn, 0);
        for (int i = 0; i < 600; i++) { c.Frame(1.0 + i * Frame, Frame); cam.Pose(Centre, 0, Frame); }
        Assert.Equal(CameraViewMode.ThirdPerson, cam.Mode);
        Assert.True(cam.FocusDistance >= 0.14f, $"the camera went to {cam.FocusDistance} m from the focus");
    }
}
