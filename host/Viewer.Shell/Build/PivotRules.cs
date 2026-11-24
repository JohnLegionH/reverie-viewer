// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Shell.Build;

/// <summary>Which point a handle drag turns and stretches the selection about: the middle of the selection's box (the official viewer's
/// choice), the root prim's place, or a point the person typed.</summary>
public enum PivotMode { Centre, Root, Custom }

/// <summary>How a Custom pivot's numbers are read: percent of the box's size, or metres, both counted from the box's minimum corner.</summary>
public enum PivotUnit { Percent, Metres }

/// <summary>One selected object's box: where its middle is in the region, how it is turned, and its size.</summary>
public readonly record struct PivotBox(Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>
/// The pivot rules as pure arithmetic. The middle of a selection is the middle of the box that holds every selected object's
/// <em>extents</em> (the official viewer's bounding box of the selection, LLManip::getPivotPoint), not of their places. A Custom pivot is
/// an offset from the box's minimum corner in the box's own frame (a door's hinge is 0 / 0 / 0 %); the percent and metre offsets work as
/// Firestorm's pivot settings do, read as behaviour and written here from scratch.
/// </summary>
public static class PivotRules
{
    /// <summary>The middle of the selection: the object's own place for one object, else the middle of the region-aligned box that holds
    /// every object's turned box.</summary>
    public static Vector3 Centre(IReadOnlyList<PivotBox> boxes)
    {
        if (boxes.Count == 0)
            return Vector3.Zero;
        if (boxes.Count == 1)
            return boxes[0].Position;
        var (min, max) = Extents(boxes);
        return (min + max) / 2f;
    }

    /// <summary>The custom pivot: for one object its own box (turned with it), for several the region-aligned box that holds them all.
    /// <paramref name="offset"/> is counted from the box's minimum corner, as percent of its size or in metres.</summary>
    public static Vector3 Custom(IReadOnlyList<PivotBox> boxes, Vector3 offset, PivotUnit unit)
    {
        if (boxes.Count == 0)
            return Vector3.Zero;
        if (boxes.Count == 1)
        {
            var b = boxes[0];
            var corner = b.Position + Vector3.Transform(-b.Scale / 2f, b.Rotation);
            return corner + Vector3.Transform(Along(offset, b.Scale, unit), b.Rotation);
        }
        var (min, max) = Extents(boxes);
        return min + Along(offset, max - min, unit);
    }

    private static Vector3 Along(Vector3 offset, Vector3 size, PivotUnit unit) => unit == PivotUnit.Percent ? offset * size / 100f : offset;

    /// <summary>The region-aligned box that holds every turned box.</summary>
    private static (Vector3 Min, Vector3 Max) Extents(IReadOnlyList<PivotBox> boxes)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var b in boxes)
        {
            var half = HalfExtent(b);
            min = Vector3.Min(min, b.Position - half);
            max = Vector3.Max(max, b.Position + half);
        }
        return (min, max);
    }

    /// <summary>How far a turned box reaches from its middle along each region axis: the sum of its three half-sizes' projections.</summary>
    private static Vector3 HalfExtent(PivotBox b)
    {
        var m = Matrix4x4.CreateFromQuaternion(b.Rotation);   // its rows are the box's own axes in the region's frame
        var h = b.Scale / 2f;
        return new Vector3(
            MathF.Abs(m.M11) * h.X + MathF.Abs(m.M21) * h.Y + MathF.Abs(m.M31) * h.Z,
            MathF.Abs(m.M12) * h.X + MathF.Abs(m.M22) * h.Y + MathF.Abs(m.M32) * h.Z,
            MathF.Abs(m.M13) * h.X + MathF.Abs(m.M23) * h.Y + MathF.Abs(m.M33) * h.Z);
    }
}
