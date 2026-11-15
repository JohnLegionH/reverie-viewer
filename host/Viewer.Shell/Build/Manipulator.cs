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
    /// <summary>The pointer is on a ring or an arrow within this many pixels of it.</summary>
    public const float PickPixels = 8f;
    /// <summary>The rotation snaps while the pointer is farther from the centre than this many ring radii.</summary>
    public const float SnapRingRadii = 2f;

    public static readonly Vector4 Red = new(1, 0.2f, 0.2f, 1), Green = new(0.2f, 1, 0.2f, 1), Blue = new(0.3f, 0.4f, 1, 1), Yellow = new(1, 1, 0.2f, 1), White = new(1, 1, 1, 0.9f);

    /// <summary>The length in metres that shows as <paramref name="pixels"/> on screen at <paramref name="centre"/> (the official apparent-angle rule).</summary>
    public static float Metres(float pixels, Vector3 centre, HandleView v)
    {
        float range = Vector3.Distance(v.Camera, centre);
        if (!(range > 0.001f) || !(v.ViewHeightPixels > 0))
            return 1f;
        return range * MathF.Tan(pixels / v.ViewHeightPixels * v.FovY) * v.UiScale;
    }

    public static Vector3 AxisVector(int axis) => axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
    private static Vector4 Colour(int axis) => axis switch { 0 => Red, 1 => Green, _ => Blue };

    // ---------------- dragging ----------------

    /// <summary>The point on the line through <paramref name="origin"/> along <paramref name="axis"/> nearest the ray, as a distance along the axis.</summary>
    public static float AlongAxis(Vector3 origin, Vector3 axis, PickRay ray)
    {
        var d = Vector3.Normalize(ray.Direction);
        var w = origin - ray.Origin;
        float a = Vector3.Dot(axis, axis), b = Vector3.Dot(axis, d), c = 1f, dd = Vector3.Dot(axis, w), e = Vector3.Dot(d, w);
        float denom = a * c - b * b;
        if (MathF.Abs(denom) < 1e-6f)
            return 0f;   // the ray runs along the axis: no movement
        return (b * e - c * dd) / denom;
    }

    /// <summary>A drag along an arrow: how far the selection moves, from where the drag started to where the pointer is now.</summary>
    public static Vector3 AxisDrag(Vector3 centre, Vector3 axis, PickRay start, PickRay now)
    {
        axis = Vector3.Normalize(axis);
        return axis * (AlongAxis(centre, axis, now) - AlongAxis(centre, axis, start));
    }

    /// <summary>Where a ray meets the plane through <paramref name="point"/> square to <paramref name="normal"/>, or null when it does not.</summary>
    public static Vector3? OnPlane(Vector3 point, Vector3 normal, PickRay ray)
    {
        float den = Vector3.Dot(normal, ray.Direction);
        if (MathF.Abs(den) < 1e-6f)
            return null;
        float t = Vector3.Dot(point - ray.Origin, normal) / den;
        return t > 0 ? ray.Origin + ray.Direction * t : null;
    }

    /// <summary>A drag of a plane handle: the movement in the plane through the centre, or zero when the plane is edge-on.</summary>
    public static Vector3 PlaneDrag(Vector3 centre, Vector3 normal, PickRay start, PickRay now) =>
        OnPlane(centre, normal, start) is { } a && OnPlane(centre, normal, now) is { } b ? b - a : Vector3.Zero;

    /// <summary>A drag on a ring: the angle in degrees about <paramref name="axis"/> from where the drag started to where the pointer is now,
    /// positive counter-clockwise looking down the axis.</summary>
    public static float RingDegrees(Vector3 centre, Vector3 axis, PickRay start, PickRay now)
    {
        axis = Vector3.Normalize(axis);
        if (OnPlane(centre, axis, start) is not { } a || OnPlane(centre, axis, now) is not { } b)
            return 0f;
        var u = a - centre;
        var v = b - centre;
        if (u.LengthSquared() < 1e-10f || v.LengthSquared() < 1e-10f)
            return 0f;
        float angle = MathF.Atan2(Vector3.Dot(axis, Vector3.Cross(u, v)), Vector3.Dot(u, v));
        return angle * 180f / MathF.PI;
    }

    /// <summary>The angle a ring drag turns by: snapped to <see cref="Viewer.Core.Building.Snap.AngleIncrementDegrees"/> steps while snapping is on and
    /// the pointer is outside the snap ring.</summary>
    /// <summary>Whether a ring drag is in the snap regime: the pointer, carried onto the ring's plane, lies farther from the centre than
    /// <see cref="SnapRingRadii"/> ring radii.</summary>
    /// <remarks>The official rotate tool reads it there (llmaniprotate.cpp: the pointer projected onto the snap plane, beyond SNAP_GUIDE_INNER_RADIUS
    /// ring radii), so a flat ring seen from low down still snaps once the pointer is well out from it; when the plane is seen edge on and
    /// cannot be hit, the distance on screen decides.</remarks>
    public static bool InSnapRegime(Vector3 centre, Vector3 axis, float ringMetres, PickRay pointer, float pointerPixelsFromCentre)
    {
        var n = Vector3.Normalize(axis);
        float facing = MathF.Abs(Vector3.Dot(Vector3.Normalize(pointer.Direction), n));
        if (facing > 0.01f && OnPlane(centre, n, pointer) is { } hit)
            return Vector3.Distance(hit, centre) > ringMetres * SnapRingRadii;
        return pointerPixelsFromCentre > RingPixels * SnapRingRadii;
    }

    public static float RotationDegrees(float raw, float pointerPixelsFromCentre, bool snapping) =>
        Viewer.Core.Building.Snap.Angle(raw, snapping && pointerPixelsFromCentre > RingPixels * SnapRingRadii);

    /// <summary>A ring is read edge on while the view meets its axis at more than 80 degrees (llmaniprotate.cpp AXIS_ONTO_CAM_TOLERANCE).</summary>
    public static readonly float EdgeOnFacing = MathF.Cos(80f * MathF.PI / 180f);

    /// <summary>
    /// A ring drag's turn in degrees, from where the drag started (<paramref name="start"/>) to where the pointer is now, about
    /// <paramref name="axis"/> (counter-clockwise looking down the axis is positive); snapped to 5.625-degree steps while snapping is on
    /// and the pointer is in the snap regime.
    /// </summary>
    /// <remarks>
    /// Read as the official rotate tool reads the pointer (llmaniprotate.cpp dragConstrained, as behaviour). Seen well off edge on, the
    /// pointer is carried onto the ring's plane, and it snaps beyond two ring radii. Seen nearly edge on, or when the pointer is above the
    /// horizon so it never meets the plane, it is read on one of two snap planes half a ring radius above and below the ring: a plane
    /// the camera looks at from its far side snaps for a pointer outside two radii on the near half; the other snaps for a pointer inside
    /// two radii, on the far half, or off the plane. There the pointer is first carried onto a plane facing the camera, then onto the
    /// ring's plane at its own sideways distance (at most two radii) and pulled toward the camera so it lies on the two-radius circle; so
    /// a pointer far out to one side reads a quarter turn and the turn keeps growing, never stalling, as it moves out.
    /// </remarks>
    public static float RingTurn(Vector3 centre, Vector3 axis, float ringMetres, Vector3 camera, PickRay start, PickRay now, bool snapping)
    {
        var n = Vector3.Normalize(axis);
        if (!(ringMetres > 0) || !float.IsFinite(n.X))
            return 0f;
        var s = ReadRing(centre, n, ringMetres, camera, start);
        var e = ReadRing(centre, n, ringMetres, camera, now);
        if (s.InPlane is not { } u || e.InPlane is not { } v || u.LengthSquared() < 1e-10f || v.LengthSquared() < 1e-10f)
            return 0f;
        float degrees = MathF.Atan2(Vector3.Dot(n, Vector3.Cross(u, v)), Vector3.Dot(u, v)) * 180f / MathF.PI;
        return RotationDegrees(degrees, e.Snap, snapping);
    }

    /// <summary>Where the pointer lies in the ring's plane, from the centre, and whether it is in the snap regime.</summary>
    private static (Vector3? InPlane, bool Snap) ReadRing(Vector3 centre, Vector3 n, float radius, Vector3 camera, PickRay ray)
    {
        var toCentre = centre - camera;
        float facing = toCentre.LengthSquared() > 1e-12f ? MathF.Abs(Vector3.Dot(Vector3.Normalize(toCentre), n)) : 1f;
        if (facing >= EdgeOnFacing && OnPlane(centre, n, ray) is { } hit)
        {
            var p = hit - centre;
            return (p, p.Length() > radius * SnapRingRadii);
        }
        if (facing >= EdgeOnFacing)
            return (OnCircleTowardCamera(centre, n, radius, camera, ray), true);   // above the horizon: well out from the ring
        int side = SnapPlaneSide(centre, n, radius, camera, ray);
        var planeCentre = side == 0 ? centre : centre + n * radius * 0.5f * side;
        return (OnCircleTowardCamera(planeCentre, n, radius, camera, ray), side != 0);
    }

    /// <summary>Which snap plane takes the pointer: +1 the one above the ring (along its axis), -1 the one below, 0 neither.</summary>
    private static int SnapPlaneSide(Vector3 centre, Vector3 n, float radius, Vector3 camera, PickRay ray)
    {
        foreach (int side in new[] { 1, -1 })
        {
            var pc = centre + n * radius * 0.5f * side;
            var look = pc - camera;
            if (look.LengthSquared() < 1e-12f)
                continue;
            look = Vector3.Normalize(look);
            float dot = Vector3.Dot(look, n);
            if (MathF.Abs(dot) < 0.01f)
            {
                // the snap plane itself seen edge on: the pointer on the view's plane, on this plane's side of it
                if (OnLine(pc, look, ray) is { } q && Vector3.Dot(q - pc, n) * side > 0)
                    return side;
                continue;
            }
            var hit = OnPlane(pc, n, ray);
            var p = hit is { } h ? h - pc : Vector3.Zero;
            if (dot * side > 0)
            {
                // seen from beyond it: outside two radii, on the camera's half
                if (hit is not null && p.Length() > radius * SnapRingRadii && Vector3.Dot(p, look) < 0)
                    return side;
            }
            else if (hit is null || p.Length() < radius * SnapRingRadii || Vector3.Dot(p, look) > 0)
                return side;   // seen from this side: inside two radii, on the far half, or off the plane
        }
        return 0;
    }

    /// <summary>The pointer carried onto a plane facing the camera through <paramref name="planeCentre"/>, then onto the ring's plane at
    /// its sideways distance (at most two ring radii), and pulled toward the camera onto the two-radius circle.</summary>
    private static Vector3? OnCircleTowardCamera(Vector3 planeCentre, Vector3 n, float radius, Vector3 camera, PickRay ray)
    {
        var look = planeCentre - camera;
        if (look.LengthSquared() < 1e-12f)
            return null;
        look = Vector3.Normalize(look);
        if (OnLine(planeCentre, look, ray) is not { } q)
            return null;
        var p = q - planeCentre;
        p -= n * Vector3.Dot(p, n);
        float snapRadius = radius * SnapRingRadii;
        float lateral = MathF.Min(snapRadius, p.Length());
        float depth = lateral > 0.01f ? MathF.Sqrt(MathF.Max(0, snapRadius * snapRadius - lateral * lateral)) : snapRadius;
        var lookInPlane = look - n * Vector3.Dot(look, n);
        return p - lookInPlane * depth;
    }

    /// <summary>Where the ray's line meets the plane through <paramref name="point"/> square to <paramref name="normal"/> (behind its start too).</summary>
    private static Vector3? OnLine(Vector3 point, Vector3 normal, PickRay ray)
    {
        float den = Vector3.Dot(normal, ray.Direction);
        if (MathF.Abs(den) < 1e-6f)
            return null;
        return ray.Origin + ray.Direction * (Vector3.Dot(point - ray.Origin, normal) / den);
    }

    /// <summary>A ring drag's turn: snapped to 5.625-degree steps while snapping is on and the pointer is in the snap regime.</summary>
    public static float RotationDegrees(float raw, bool inSnapRegime, bool snapping) => Viewer.Core.Building.Snap.Angle(raw, snapping && inSnapRegime);

    /// <summary>A corner drag: the ratio of the pointer's distance from the centre now to when the drag started, along the corner's direction.</summary>
    public static float CornerRatio(Vector3 centre, Vector3 cornerDirection, PickRay start, PickRay now)
    {
        var dir = Vector3.Normalize(cornerDirection);
        float s = AlongAxis(centre, dir, start), n = AlongAxis(centre, dir, now);
        return MathF.Abs(s) < 1e-5f ? 1f : MathF.Max(n / s, 0.001f);
    }

    // ---------------- where the handles are, and which one the pointer is on ----------------

    /// <summary>The corner of a box of <paramref name="size"/> numbered <paramref name="corner"/>, in the box's own axes.</summary>
    public static Vector3 CornerOffset(Vector3 size, int corner) =>
        new((corner & 1) != 0 ? size.X / 2 : -size.X / 2, (corner & 2) != 0 ? size.Y / 2 : -size.Y / 2, (corner & 4) != 0 ? size.Z / 2 : -size.Z / 2);

    /// <summary>The handle under the pointer, or <see cref="Handle.None"/>. <paramref name="project"/> turns a region point into window pixels (null
    /// when behind the camera); <paramref name="frame"/> is the handles' axes (the world's, or the object's for Stretch); <paramref name="size"/> is the
    /// selection's box for Stretch.</summary>
    public static Handle Hit(ManipMode mode, Vector3 centre, Quaternion frame, Vector3 size, HandleView view, Func<Vector3, Vector2?> project, Vector2 pointer)
    {
        if (project(centre) is not { } c)
            return Handle.None;
        var best = Handle.None;
        float bestDistance = float.MaxValue;
        void Consider(Handle h, float pixels)
        {
            if (pixels < bestDistance)
            {
                bestDistance = pixels;
                best = h;
            }
        }
        switch (mode)
        {
            case ManipMode.Move:
            {
                float arrow = Metres(ArrowPixels, centre, view);
                for (int axis = 0; axis < 3; axis++)
                {
                    var dir = Vector3.Transform(AxisVector(axis), frame);
                    // the plane handle square to this axis, at 1.8 arrow lengths along the other two
                    var (u, w) = Others(axis);
                    var corner = PlaneCentre(centre, frame, axis, arrow, view);
                    if (project(corner) is { } pc)
                    {
                        float half = MathF.Max(PickPixels, (project(corner + Vector3.Transform(AxisVector(u), frame) * arrow * PlaneTick) is { } edge ? Vector2.Distance(edge, pc) : 0));
                        if (Vector2.Distance(pc, pointer) <= half)
                            Consider(new Handle(HandleKind.Plane, axis), Vector2.Distance(pc, pointer) * 0.5f);
                    }
                    foreach (int sign in new[] { 1, -1 })
                    {
                        if (project(centre + dir * sign * arrow * HotStart) is { } a && project(centre + dir * sign * arrow * HotEnd) is { } b)
                        {
                            float d = SegmentDistance(pointer, a, b);
                            if (d <= PickPixels)
                                Consider(new Handle(HandleKind.Axis, axis, sign), d);
                        }
                    }
                }
                break;
            }
            case ManipMode.Rotate:
            {
                float radius = Metres(RingPixels, centre, view);
                for (int axis = 0; axis < 3; axis++)
                {
                    var (u, w) = Others(axis);
                    var du = Vector3.Transform(AxisVector(u), frame);
                    var dw = Vector3.Transform(AxisVector(w), frame);
                    Vector2? prev = null;
                    for (int i = 0; i <= 64; i++)
                    {
                        float t = i / 64f * MathF.Tau;
                        var p = project(centre + (du * MathF.Cos(t) + dw * MathF.Sin(t)) * radius);
                        if (p is { } pp && prev is { } pv)
                        {
                            float d = SegmentDistance(pointer, pv, pp);
                            if (d <= PickPixels)
                                Consider(new Handle(HandleKind.Ring, axis), d);
                        }
                        prev = p;
                    }
                }
                break;
            }
            case ManipMode.Scale:
            {
                for (int corner = 0; corner < 8; corner++)
                    if (project(centre + Vector3.Transform(CornerOffset(size, corner), frame)) is { } pc && Vector2.Distance(pc, pointer) <= BoxPixels)
                        Consider(new Handle(HandleKind.Corner, Corner: corner), Vector2.Distance(pc, pointer));
                for (int axis = 0; axis < 3; axis++)
                    foreach (int sign in new[] { 1, -1 })
                    {
                        var offset = AxisVector(axis) * sign * (axis switch { 0 => size.X, 1 => size.Y, _ => size.Z }) / 2;
                        if (project(centre + Vector3.Transform(offset, frame)) is { } pf && Vector2.Distance(pf, pointer) <= BoxPixels)
                            Consider(new Handle(HandleKind.Face, axis, sign), Vector2.Distance(pf, pointer));
                    }
                break;
            }
        }
        _ = c;
        return best;
    }

    private static (int, int) Others(int axis) => axis switch { 0 => (1, 2), 1 => (0, 2), _ => (0, 1) };

    /// <summary>The middle of the plane handle square to <paramref name="axis"/>: 1.8 arrow lengths out along the other two axes, on the camera's side
    /// of each, so it is never hidden behind the selection (the official plane handles turn to face the camera the same way).</summary>
    public static Vector3 PlaneCentre(Vector3 centre, Quaternion frame, int axis, float arrow, HandleView view)
    {
        var (u, w) = Others(axis);
        var du = Vector3.Transform(AxisVector(u), frame);
        var dw = Vector3.Transform(AxisVector(w), frame);
        var toCamera = view.Camera - centre;
        float su = Vector3.Dot(toCamera, du) < 0 ? -1 : 1, sw = Vector3.Dot(toCamera, dw) < 0 ? -1 : 1;
        return centre + (du * su + dw * sw) * arrow * PlaneOffset;
    }

    private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len = ab.LengthSquared();
        float t = len < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / len, 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    // ---------------- the drawing ----------------

    /// <summary>The lines that draw the handles of <paramref name="mode"/> at <paramref name="centre"/>, the one under the pointer or being dragged in yellow.</summary>
    public static IReadOnlyList<HandleLine> Lines(ManipMode mode, Vector3 centre, Quaternion frame, Vector3 size, HandleView view, Handle highlighted)
    {
        var lines = new List<HandleLine>();
        Vector4 Pick(Handle h, Vector4 normal) => h == highlighted ? Yellow : normal;
        switch (mode)
        {
            case ManipMode.Move:
            {
                float arrow = Metres(ArrowPixels, centre, view), cone = arrow / 4f;
                for (int axis = 0; axis < 3; axis++)
                {
                    var dir = Vector3.Transform(AxisVector(axis), frame);
                    var (u, w) = Others(axis);
                    var du = Vector3.Transform(AxisVector(u), frame);
                    var dw = Vector3.Transform(AxisVector(w), frame);
                    foreach (int sign in new[] { 1, -1 })
                    {
                        var col = Pick(new Handle(HandleKind.Axis, axis, sign), Colour(axis));
                        var tip = centre + dir * sign * arrow * HotEnd;
                        lines.Add(new(centre + dir * sign * arrow * HotStart, tip, col));
                        // the arrow head: four lines back from the tip
                        var back = tip - dir * sign * cone;
                        foreach (var side in new[] { du, -du, dw, -dw })
                            lines.Add(new(tip, back + side * cone * 0.4f, col));
                    }
                    // the plane handle: a small square at 1.8 arrow lengths along the other two axes
                    var pcol = Pick(new Handle(HandleKind.Plane, axis), Colour(axis));
                    var pc = PlaneCentre(centre, frame, axis, arrow, view);
                    float t = arrow * PlaneTick;
                    var q = new[] { pc + (du + dw) * t, pc + (du - dw) * t, pc - (du + dw) * t, pc - (du - dw) * t };
                    for (int i = 0; i < 4; i++)
                        lines.Add(new(q[i], q[(i + 1) % 4], pcol));
                }
                break;
            }
            case ManipMode.Rotate:
            {
                float radius = Metres(RingPixels, centre, view);
                for (int axis = 0; axis < 3; axis++)
                {
                    var (u, w) = Others(axis);
                    var du = Vector3.Transform(AxisVector(u), frame);
                    var dw = Vector3.Transform(AxisVector(w), frame);
                    var col = Pick(new Handle(HandleKind.Ring, axis), Colour(axis));
                    for (int i = 0; i < 64; i++)
                    {
                        float a = i / 64f * MathF.Tau, b = (i + 1) / 64f * MathF.Tau;
                        lines.Add(new(centre + (du * MathF.Cos(a) + dw * MathF.Sin(a)) * radius, centre + (du * MathF.Cos(b) + dw * MathF.Sin(b)) * radius, col));
                    }
                }
                break;
            }
            case ManipMode.Scale:
            {
                // the box's edges, then a small box at every corner and face centre
                for (int corner = 0; corner < 8; corner++)
                    for (int bit = 1; bit <= 4; bit <<= 1)
                        if ((corner & bit) == 0)
                            lines.Add(new(centre + Vector3.Transform(CornerOffset(size, corner), frame), centre + Vector3.Transform(CornerOffset(size, corner | bit), frame), White));
                for (int corner = 0; corner < 8; corner++)
                {
                    var p = centre + Vector3.Transform(CornerOffset(size, corner), frame);
                    AddBox(lines, p, frame, Metres(BoxPixels, p, view) / 2, Pick(new Handle(HandleKind.Corner, Corner: corner), White));
                }
                for (int axis = 0; axis < 3; axis++)
                    foreach (int sign in new[] { 1, -1 })
                    {
                        var p = centre + Vector3.Transform(AxisVector(axis) * sign * (axis switch { 0 => size.X, 1 => size.Y, _ => size.Z }) / 2, frame);
                        AddBox(lines, p, frame, Metres(BoxPixels, p, view) / 2, Pick(new Handle(HandleKind.Face, axis, sign), Colour(axis)));
                    }
                break;
            }
        }
        return lines;
    }

    private static void AddBox(List<HandleLine> lines, Vector3 p, Quaternion frame, float half, Vector4 colour)
    {
        var s = new Vector3(half * 2);
        for (int corner = 0; corner < 8; corner++)
            for (int bit = 1; bit <= 4; bit <<= 1)
                if ((corner & bit) == 0)
                    lines.Add(new(p + Vector3.Transform(CornerOffset(s, corner), frame), p + Vector3.Transform(CornerOffset(s, corner | bit), frame), colour));
    }

    /// <summary>The drawn grid: lines one grid unit apart in the grid's horizontal plane through <paramref name="centre"/>, out to
    /// <paramref name="extent"/> metres each way, in the grid's frame (its origin and rotation); every line faded by <paramref name="opacity"/>.</summary>
    public static IReadOnlyList<HandleLine> GridLines(Vector3 centre, Vector3 origin, Quaternion rotation, float unit, float extent, float opacity)
    {
        var lines = new List<HandleLine>();
        if (!(unit > 0) || !(extent > 0))
            return lines;
        var inv = Quaternion.Inverse(rotation);
        var local = Vector3.Transform(centre - origin, inv);
        // lines on the grid nearest the centre
        float cx = MathF.Round(local.X / unit) * unit, cy = MathF.Round(local.Y / unit) * unit;
        int n = Math.Min(200, (int)MathF.Floor(extent / unit));
        var col = new Vector4(1, 1, 1, Math.Clamp(opacity, 0, 1));
        Vector3 W(float x, float y) => origin + Vector3.Transform(new Vector3(x, y, local.Z), rotation);
        for (int i = -n; i <= n; i++)
        {
            lines.Add(new(W(cx + i * unit, cy - n * unit), W(cx + i * unit, cy + n * unit), col));
            lines.Add(new(W(cx - n * unit, cy + i * unit), W(cx + n * unit, cy + i * unit), col));
        }
        return lines;
    }
}
