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
}
