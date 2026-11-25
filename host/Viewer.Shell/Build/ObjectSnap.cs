// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Shell.Build;

/// <summary>A box along the grid frame's axes.</summary>
public readonly record struct SnapBox(Vector3 Min, Vector3 Max)
{
    public Vector3 Middle => (Min + Max) / 2f;
}

/// <summary>What caught: two faces touching, two like sides lining up, or two middles lining up. In this order, which also breaks a tie.</summary>
public enum SnapKind { Face, Edge, Centre }

/// <summary>One axis that caught: which axis (0, 1, 2), where along it, what kind, and the box it caught on.</summary>
public readonly record struct SnapGuide(int Axis, float At, SnapKind Kind, SnapBox Target);

/// <summary>A move after snapping, and one guide for each axis that caught.</summary>
public readonly record struct SnapResult(Vector3 Move, IReadOnlyList<SnapGuide> Guides);

/// <summary>
/// Snap to objects as pure arithmetic: a box being moved catches on other boxes. Both are boxes along the grid frame's axes, so a box
/// prim that stands square to that frame is caught exactly; a turned, round or mesh shape is caught at its box, not its surface. A box
/// is a candidate only while the moved box is within the snap distance of it on all three axes. On each axis being moved, the smallest
/// step that makes two faces touch, two like sides line up or the two middles line up wins; on a tie a face comes before a side and a
/// side before a middle. Our own design.
/// </summary>
public static class ObjectSnap
{
    /// <summary>The snap distance: this many metres, or <see cref="MaxPixels"/> pixels on screen at the drag point, whichever is smaller.</summary>
    public const float MaxDistance = 0.25f;
    public const float MaxPixels = 8f;
    /// <summary>Objects this near the selection when a drag begins, in metres between the boxes, can be caught on during it.</summary>
    public const float Reach = 10f;

    /// <summary>Steps this close in size count as equal.</summary>
    private const float Tie = 1e-4f;
    /// <summary>A move along an axis smaller than this is no move along it (a turned frame leaves a little arithmetic noise).</summary>
    private const float Still = 1e-4f;

    private static float At(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    /// <summary>How far apart two boxes are along each axis (zero where they overlap), the largest of the three.</summary>
    public static float Apart(SnapBox a, SnapBox b)
    {
        var gap = Vector3.Max(Vector3.Zero, Vector3.Max(b.Min - a.Max, a.Min - b.Max));
        return MathF.Max(gap.X, MathF.Max(gap.Y, gap.Z));
    }

    /// <summary><paramref name="moving"/> is the box where the drag began and <paramref name="move"/> how far the pointer has taken it, both
    /// in the grid frame. With <paramref name="letGo"/> nothing catches.</summary>
    public static SnapResult Snap(SnapBox moving, Vector3 move, IReadOnlyList<SnapBox> others, float distance, bool letGo)
    {
        if (letGo || !(distance > 0))
            return new(move, []);
        var proposed = new SnapBox(moving.Min + move, moving.Max + move);
        var step = new float[3];
        var caught = new SnapGuide?[3];
        foreach (var other in others)
        {
            if (Apart(proposed, other) > distance + Tie)
                continue;
            for (int axis = 0; axis < 3; axis++)
            {
                if (MathF.Abs(At(move, axis)) < Still)
                    continue;
                float lo = At(proposed.Min, axis), hi = At(proposed.Max, axis), mid = At(proposed.Middle, axis);
                float otherLo = At(other.Min, axis), otherHi = At(other.Max, axis), otherMid = At(other.Middle, axis);
                var pairs = new (float From, float To, SnapKind Kind)[]
                {
                    (hi, otherLo, SnapKind.Face), (lo, otherHi, SnapKind.Face),
                    (lo, otherLo, SnapKind.Edge), (hi, otherHi, SnapKind.Edge),
                    (mid, otherMid, SnapKind.Centre),
                };
                foreach (var (from, to, kind) in pairs)
                {
                    float d = to - from;
                    if (MathF.Abs(d) > distance + Tie)
                        continue;
                    if (caught[axis] is { } best)
                    {
                        float smaller = MathF.Abs(step[axis]) - MathF.Abs(d);
                        if (smaller < -Tie || (smaller <= Tie && kind >= best.Kind))
                            continue;
                    }
                    step[axis] = d;
                    caught[axis] = new SnapGuide(axis, to, kind, other);
                }
            }
        }
        var guides = caught.Where(g => g is not null).Select(g => g!.Value).ToArray();
        return new(move + new Vector3(step[0], step[1], step[2]), guides);
    }

    /// <summary>The guide's four lines in the region's frame: a rectangle on the plane that caught, <paramref name="margin"/> metres wider
    /// on every side than the target box and the moved box together. Where two faces meet, their own outlines lie along both boxes'
    /// edges; this one stands clear of both, and seen edge-on its ends still stick out past them.</summary>
    public static IReadOnlyList<(Vector3 From, Vector3 To)> Outline(SnapGuide guide, Quaternion frame, SnapBox moved, float margin)
    {
        int u = guide.Axis == 0 ? 1 : 0, v = guide.Axis == 2 ? 1 : 2;
        Vector3 Corner(float a, float b)
        {
            var p = new float[3];
            p[guide.Axis] = guide.At;
            p[u] = a;
            p[v] = b;
            return Vector3.Transform(new Vector3(p[0], p[1], p[2]), frame);
        }
        var min = Vector3.Min(guide.Target.Min, moved.Min);
        var max = Vector3.Max(guide.Target.Max, moved.Max);
        float u0 = At(min, u) - margin, u1 = At(max, u) + margin, v0 = At(min, v) - margin, v1 = At(max, v) + margin;
        var c = new[] { Corner(u0, v0), Corner(u1, v0), Corner(u1, v1), Corner(u0, v1) };
        return [(c[0], c[1]), (c[1], c[2]), (c[2], c[3]), (c[3], c[0])];
    }
}
