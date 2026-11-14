// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Interaction;

namespace Viewer.Shell.Build;

/// <summary>Which handles the edit tools show: Move (axis arrows and plane handles), Rotate (rings) or Stretch (corner and face boxes).</summary>
public enum ManipMode { Move, Rotate, Scale }

/// <summary>The part of a handle under the pointer.</summary>
public enum HandleKind { None, Axis, Plane, Ring, Corner, Face }

/// <summary>A handle: <see cref="Axis"/> is 0, 1 or 2 (x, y, z; for a plane handle the axis it is square to); <see cref="Sign"/> is +1 or -1 for an
/// arrow end or a face; <see cref="Corner"/> numbers the eight corners (bit 0 x, bit 1 y, bit 2 z: set means the positive side).</summary>
public readonly record struct Handle(HandleKind Kind, int Axis = 0, int Sign = 1, int Corner = 0)
{
    public static Handle None { get; } = new(HandleKind.None);
}

/// <summary>What the handles need to know about the view: the camera's place, its vertical field of view, the view's height in pixels and the
/// interface size.</summary>
public readonly record struct HandleView(Vector3 Camera, float FovY, float ViewHeightPixels, float UiScale = 1f);

/// <summary>A line of a handle's drawing, in region metres, with its colour (r, g, b, a in 0..1).</summary>
public readonly record struct HandleLine(Vector3 From, Vector3 To, Vector4 Colour);

/// <summary>
/// The in-world handles of the edit tools, without an engine: their size, which part the pointer is on, and what a drag along each does. The
/// behaviour follows the official viewer's manipulators (llmaniptranslate.cpp, llmaniprotate.cpp, llmanipscale.cpp, read as behaviour only):
/// arrows 50 pixels long with their hot part from 0.2 to 1.2 of that, plane handles at 1.8 arrow lengths, rings 100 pixels across the radius,
/// boxes 10 pixels, all kept the same size on screen whatever the distance; rotation snaps to 5.625 degree steps while the pointer is outside the
/// snap ring at twice the ring's radius.
/// </summary>
public static class Manipulator
{
    public const float ArrowPixels = 50f, RingPixels = 100f, BoxPixels = 10f, HotStart = 0.2f, HotEnd = 1.2f, PlaneOffset = 1.8f, PlaneTick = 0.4f;
    public const float PickPixels = 8f;
    public const float SnapRingRadii = 2f;
    public static readonly Vector4 Red = new(1, 0.2f, 0.2f, 1), Green = new(0.2f, 1, 0.2f, 1), Blue = new(0.3f, 0.4f, 1, 1), Yellow = new(1, 1, 0.2f, 1), White = new(1, 1, 1, 0.9f);
    public static float Metres(float pixels, Vector3 centre, HandleView v) => 0f;
    public static Vector3 AxisVector(int axis) => Vector3.Zero;
    public static float AlongAxis(Vector3 origin, Vector3 axis, PickRay ray) => 0f;
    public static Vector3 AxisDrag(Vector3 centre, Vector3 axis, PickRay start, PickRay now) => Vector3.Zero;
    public static Vector3? OnPlane(Vector3 point, Vector3 normal, PickRay ray) => null;
    public static Vector3 PlaneDrag(Vector3 centre, Vector3 normal, PickRay start, PickRay now) => Vector3.Zero;
    public static float RingDegrees(Vector3 centre, Vector3 axis, PickRay start, PickRay now) => 0f;
    public static float RotationDegrees(float raw, float pointerPixelsFromCentre, bool snapping) => 0f;
    public static float CornerRatio(Vector3 centre, Vector3 cornerDirection, PickRay start, PickRay now) => 0f;
    public static Vector3 CornerOffset(Vector3 size, int corner) => Vector3.Zero;
    public static Handle Hit(ManipMode mode, Vector3 centre, Quaternion frame, Vector3 size, HandleView view, Func<Vector3, Vector2?> project, Vector2 pointer) => Handle.None;
    public static IReadOnlyList<HandleLine> Lines(ManipMode mode, Vector3 centre, Quaternion frame, Vector3 size, HandleView view, Handle highlighted) => [];
    public static IReadOnlyList<HandleLine> GridLines(Vector3 centre, Vector3 origin, Quaternion rotation, float unit, float extent, float opacity) => [];
}
