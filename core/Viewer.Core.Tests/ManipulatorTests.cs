// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Interaction;
using Viewer.Shell.Build;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The in-world handles without an engine: their fixed size on screen, which handle the pointer is on, and what a drag along an arrow, across a
/// plane handle, round a ring or out from a corner does, with the rotation snap outside the snap ring. A simple camera looking straight down stands
/// in for the engine's projection. Pure functions only, so the class runs in parallel.
/// </summary>
public class ManipulatorTests
{
    // a camera 40 m above (10, 10, 30) looking straight down, 1000 pixels high with a 60 degree field of view; x goes right, y goes up the screen
    private static readonly HandleView View = new(new Vector3(10, 10, 70), MathF.PI / 3, 1000);
    private static readonly float PixelsPerMetreAt30 = 500f / (40f * MathF.Tan(MathF.PI / 6));

    private static Vector2? Project(Vector3 p)
    {
        float depth = View.Camera.Z - p.Z;
        if (depth <= 0) return null;
        float f = 500f / MathF.Tan(MathF.PI / 6);
        return new Vector2(500 + (p.X - 10) * f / depth, 500 - (p.Y - 10) * f / depth);
    }

    private static PickRay Down(float x, float y) => new(new Vector3(x, y, 70), new Vector3(0, 0, -1));
    private static void Near(float want, float got, float tol = 1e-3f) => Assert.True(MathF.Abs(want - got) <= tol, $"want {want}, got {got}");

    [Fact]
    public void Handles_keep_their_size_on_screen_their_length_in_metres_grows_with_the_distance()
    {
        var c = new Vector3(10, 10, 30);
        float near = Manipulator.Metres(Manipulator.ArrowPixels, c, View with { Camera = c + new Vector3(0, 0, 10) });
        float far = Manipulator.Metres(Manipulator.ArrowPixels, c, View with { Camera = c + new Vector3(0, 0, 20) });
        Near(10 * MathF.Tan(50f / 1000f * MathF.PI / 3), near, 1e-4f);
        Near(2 * near, far, 1e-4f);
        Near(1.5f * near, Manipulator.Metres(Manipulator.ArrowPixels, c, View with { Camera = c + new Vector3(0, 0, 10), UiScale = 1.5f }), 1e-4f);
    }

    [Fact]
    public void Dragging_an_arrow_moves_along_its_axis_only_and_a_plane_handle_moves_in_its_plane()
    {
        var c = new Vector3(10, 10, 30);
        var moved = Manipulator.AxisDrag(c, Vector3.UnitX, new PickRay(new Vector3(10, 0, 30), Vector3.UnitY), new PickRay(new Vector3(13, 0, 32), Vector3.UnitY));
        Assert.True(Vector3.Distance(new Vector3(3, 0, 0), moved) < 1e-4f, moved.ToString());
        var plane = Manipulator.PlaneDrag(c, Vector3.UnitZ, Down(10, 10), Down(13, 14));
        Assert.True(Vector3.Distance(new Vector3(3, 4, 0), plane) < 1e-4f, plane.ToString());
        Assert.Equal(Vector3.Zero, Manipulator.PlaneDrag(c, Vector3.UnitX, Down(10, 10), Down(13, 14)));   // edge-on: nothing
    }

    [Fact]
    public void A_ring_drag_turns_by_the_angle_swept_and_snaps_to_5_625_degrees_only_outside_the_snap_ring()
    {
        var c = new Vector3(10, 10, 30);
        Near(90, Manipulator.RingDegrees(c, Vector3.UnitZ, Down(12, 10), Down(10, 12)));
        Near(-45, Manipulator.RingDegrees(c, Vector3.UnitZ, Down(12, 10), Down(12, 8)));
        Near(10, Manipulator.RotationDegrees(10, 150, snapping: true));                 // inside the snap ring: as dragged
        Near(11.25f, Manipulator.RotationDegrees(10, 250, snapping: true));             // outside it: the nearest 5.625 step
        Near(10, Manipulator.RotationDegrees(10, 250, snapping: false));
    }

    [Fact]
    public void A_corner_drag_gives_the_ratio_of_the_pointers_distance_from_the_centre()
    {
        var c = new Vector3(10, 10, 30);
        Near(2f, Manipulator.CornerRatio(c, new Vector3(1, 1, 0), Down(11, 11), Down(12, 12)));
        Near(0.5f, Manipulator.CornerRatio(c, new Vector3(1, 1, 0), Down(12, 12), Down(11, 11)));
    }

    [Fact]
    public void The_pointer_on_an_arrow_a_plane_handle_a_ring_a_corner_or_a_face_picks_that_handle_and_elsewhere_none()
    {
        var c = new Vector3(10, 10, 30);
        float arrow = Manipulator.Metres(Manipulator.ArrowPixels, c, View);
        var onX = Project(c + new Vector3(arrow * 0.7f, 0, 0))!.Value;
        Assert.Equal(new Handle(HandleKind.Axis, 0, 1), Manipulator.Hit(ManipMode.Move, c, Quaternion.Identity, Vector3.One, View, Project, onX));
        var onMinusY = Project(c + new Vector3(0, -arrow * 0.7f, 0))!.Value;
        Assert.Equal(new Handle(HandleKind.Axis, 1, -1), Manipulator.Hit(ManipMode.Move, c, Quaternion.Identity, Vector3.One, View, Project, onMinusY));
        var onXY = Project(c + new Vector3(arrow * 1.8f, arrow * 1.8f, 0))!.Value;
        Assert.Equal(new Handle(HandleKind.Plane, 2), Manipulator.Hit(ManipMode.Move, c, Quaternion.Identity, Vector3.One, View, Project, onXY));
        Assert.Equal(Handle.None, Manipulator.Hit(ManipMode.Move, c, Quaternion.Identity, Vector3.One, View, Project, new Vector2(900, 100)));

        float ring = Manipulator.Metres(Manipulator.RingPixels, c, View);
        var onZRing = Project(c + new Vector3(ring * 0.7071f, ring * 0.7071f, 0))!.Value;
        Assert.Equal(new Handle(HandleKind.Ring, 2), Manipulator.Hit(ManipMode.Rotate, c, Quaternion.Identity, Vector3.One, View, Project, onZRing));

        var size = new Vector3(4, 2, 1);
        var corner = Project(c + new Vector3(2, 1, 0.5f))!.Value;
        Assert.Equal(new Handle(HandleKind.Corner, Corner: 7), Manipulator.Hit(ManipMode.Scale, c, Quaternion.Identity, size, View, Project, corner));
        var face = Project(c + new Vector3(-2, 0, 0))!.Value;
        Assert.Equal(new Handle(HandleKind.Face, 0, -1), Manipulator.Hit(ManipMode.Scale, c, Quaternion.Identity, size, View, Project, face));
        Assert.True(PixelsPerMetreAt30 > 0);
    }

    [Fact]
    public void The_handles_are_drawn_at_the_same_size_on_screen_and_the_one_under_the_pointer_is_yellow()
    {
        var c = new Vector3(10, 10, 30);
        var lines = Manipulator.Lines(ManipMode.Move, c, Quaternion.Identity, Vector3.One, View, new Handle(HandleKind.Axis, 0, 1));
        Assert.NotEmpty(lines);
        float tipPixels(HandleView v, Func<Vector3, Vector2?> p) =>
            Vector2.Distance(p(c)!.Value, p(c + new Vector3(Manipulator.Metres(Manipulator.ArrowPixels, c, v) * Manipulator.HotEnd, 0, 0))!.Value);
        // the arrow's angle on screen is its pixels' share of the field of view (the official rule), so its tip is where that angle projects
        Near(Manipulator.HotEnd * 500f / MathF.Tan(MathF.PI / 6) * MathF.Tan(Manipulator.ArrowPixels / 1000f * MathF.PI / 3), tipPixels(View, Project), 0.5f);
        Assert.Contains(lines, l => l.Colour == Manipulator.Yellow);
        Assert.Contains(lines, l => l.Colour == Manipulator.Green);
        Assert.Equal(3 * 64, Manipulator.Lines(ManipMode.Rotate, c, Quaternion.Identity, Vector3.One, View, Handle.None).Count);
        Assert.Equal(12 + 14 * 12, Manipulator.Lines(ManipMode.Scale, c, Quaternion.Identity, Vector3.One, View, Handle.None).Count);
    }

    [Fact]
    public void The_drawn_grid_has_lines_one_unit_apart_out_to_its_extent_in_the_grids_frame()
    {
        var lines = Manipulator.GridLines(new Vector3(10.2f, 10.1f, 30), Vector3.Zero, Quaternion.Identity, 0.5f, 2f, 0.7f);
        Assert.Equal(2 * 9, lines.Count);                     // 4 units each way: 9 lines across and 9 along
        Assert.All(lines, l => Assert.Equal(30, l.From.Z));
        Assert.Contains(lines, l => MathF.Abs(l.From.X - 10f) < 1e-4f && MathF.Abs(l.To.X - 10f) < 1e-4f);
        Assert.All(lines, l => Assert.Equal(0.7f, l.Colour.W));
        var turned = Manipulator.GridLines(Vector3.Zero, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4), 1f, 1f, 1f);
        Assert.Contains(turned, l => MathF.Abs(l.From.X - l.To.X) > 1e-3f && MathF.Abs(l.From.Y - l.To.Y) > 1e-3f);   // diagonal in the world
    }
    [Fact]
    public void Plane_handles_sit_on_the_cameras_side_of_the_selection()
    {
        var c = new Vector3(10, 10, 30);
        var southWestBelow = View with { Camera = new Vector3(0, 0, 20) };
        var p = Manipulator.PlaneCentre(c, Quaternion.Identity, 2, 1f, southWestBelow);
        Assert.True(p.X < c.X && p.Y < c.Y && p.Z == c.Z, p.ToString());
        var q = Manipulator.PlaneCentre(c, Quaternion.Identity, 0, 1f, southWestBelow);
        Assert.True(q.X == c.X && q.Y < c.Y && q.Z < c.Z, q.ToString());
        var r = Manipulator.PlaneCentre(c, Quaternion.Identity, 2, 1f, View with { Camera = new Vector3(20, 20, 40) });
        Assert.True(r.X > c.X && r.Y > c.Y, r.ToString());
    }

    [Fact]
    public void The_snap_regime_is_read_in_the_rings_plane_so_a_flat_ring_seen_from_low_down_snaps_beyond_two_ring_radii()
    {
        // a flat ring 1 m across the radius at 20 m, the camera 20 m away and 3 m up: on screen the plane is squeezed, so a point
        // 2.6 radii out in the plane lies well under 200 pixels from the centre, yet the official rule (llmaniprotate.cpp: the pointer
        // projected onto the snap plane, beyond SNAP_GUIDE_INNER_RADIUS ring radii) snaps there
        var centre = new Vector3(100, 100, 20);
        var camera = new Vector3(80, 100, 23);
        PickRay Through(Vector3 p) => new(camera, Vector3.Normalize(p - camera));
        Assert.True(Manipulator.InSnapRegime(centre, Vector3.UnitZ, 1f, Through(centre + new Vector3(-2.6f, 0, 0)), pointerPixelsFromCentre: 120));
        Assert.True(Manipulator.InSnapRegime(centre, Vector3.UnitZ, 1f, Through(centre + new Vector3(0, 2.6f, 0)), pointerPixelsFromCentre: 150));
        Assert.False(Manipulator.InSnapRegime(centre, Vector3.UnitZ, 1f, Through(centre + new Vector3(-1.2f, 0, 0)), pointerPixelsFromCentre: 60));
        Assert.False(Manipulator.InSnapRegime(centre, Vector3.UnitZ, 1f, Through(centre + new Vector3(0, 1.9f, 0)), pointerPixelsFromCentre: 110));
        // seen edge on, the plane cannot be hit: the distance on screen decides, as before
        var level = new Vector3(80, 100, 20);
        Assert.True(Manipulator.InSnapRegime(centre, Vector3.UnitZ, 1f, new PickRay(level, Vector3.UnitX), pointerPixelsFromCentre: 250));
        Assert.False(Manipulator.InSnapRegime(centre, Vector3.UnitZ, 1f, new PickRay(level, Vector3.UnitX), pointerPixelsFromCentre: 150));
    }

    private static bool OnStep(float degrees) => MathF.Abs(degrees / 5.625f - MathF.Round(degrees / 5.625f)) < 1e-3f;

    [Fact]
    public void A_flat_ring_seen_from_low_down_keeps_turning_in_snap_steps_while_the_pointer_goes_well_out_above_the_horizon()
    {
        // the camera 20 m off and 3 m up: pointed above its own height the pointer never meets the ring's plane, yet the official rotate
        // tool (llmaniprotate.cpp dragConstrained, the ring seen nearly edge on) reads it on a snap plane half a ring radius above or below
        // the ring, on the side the camera sees, and keeps snapping instead of stalling
        var centre = new Vector3(100, 100, 20);
        var camera = new Vector3(80, 100, 23);
        PickRay Through(Vector3 p) => new(camera, Vector3.Normalize(p - camera));
        var start = Through(centre + new Vector3(-1, 0, 0));          // the ring's near side
        float last = 0;
        var turns = new List<float>();
        for (float k = 0.3f; k <= 1.95f; k += 0.15f)
        {
            float t = Manipulator.RingTurn(centre, Vector3.UnitZ, 1f, camera, start, Through(centre + new Vector3(0, -k, 6)), snapping: true);
            turns.Add(t);
            Assert.True(t != 0 && OnStep(t), $"k {k}: {t}");
            Assert.True(MathF.Abs(t) >= MathF.Abs(last) - 1e-3f, $"k {k}: {t} after {last} (stalled or turned back)");
            last = t;
        }
        Assert.True(MathF.Abs(turns[^1]) > MathF.Abs(turns[0]) + 30, string.Join(", ", turns));
        Assert.True(MathF.Abs(turns[^1]) <= 90 + 1e-3f, string.Join(", ", turns));
    }

    [Fact]
    public void Seen_from_above_a_ring_drag_is_smooth_near_the_ring_and_snapped_beyond_two_radii()
    {
        var centre = new Vector3(100, 100, 20);
        var camera = new Vector3(100, 90, 40);
        PickRay Through(Vector3 p) => new(camera, Vector3.Normalize(p - camera));
        var start = Through(centre + new Vector3(1, 0, 0));
        float a = 20f * MathF.PI / 180f;
        float near = Manipulator.RingTurn(centre, Vector3.UnitZ, 1f, camera, start, Through(centre + new Vector3(MathF.Cos(a), MathF.Sin(a), 0) * 0.95f), snapping: true);
        Near(20, near, 0.01f);
        float far = Manipulator.RingTurn(centre, Vector3.UnitZ, 1f, camera, start, Through(centre + new Vector3(MathF.Cos(a), MathF.Sin(a), 0) * 3f), snapping: true);
        Near(22.5f, far, 0.01f);
        float off = Manipulator.RingTurn(centre, Vector3.UnitZ, 1f, camera, start, Through(centre + new Vector3(MathF.Cos(a), MathF.Sin(a), 0) * 3f), snapping: false);
        Near(20, off, 0.01f);
    }
}
