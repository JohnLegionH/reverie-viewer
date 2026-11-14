// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Interaction;
using Viewer.Core.Meshing;

namespace Viewer.Core.Building;

/// <summary>The limits an edit is held to: the official viewer's own (prim sizes 0.01 to 64 m, objects up to 4096 m, 256 prims in a
/// linkset), narrowed or widened by the grid where it states its own in the simulator features' OpenSim extras.</summary>
public sealed record EditLimits(float MinScale, float MaxScale, float MinPhysScale, float MaxPhysScale, float MinHeight, float MaxHeight, int MaxLinkPrims)
{
    public static EditLimits Official { get; } = new(0.01f, 64f, 0.01f, 64f, float.NegativeInfinity, 4096f, 256);

    /// <summary>The official limits with the grid's own where its OpenSim extras carry them (MinPrimScale, MaxPrimScale, MinPhysPrimScale,
    /// MaxPhysPrimScale, MinSimHeight, MaxSimHeight).</summary>
    public static EditLimits FromExtras(IReadOnlyDictionary<string, double>? extras)
    {
        if (extras is null)
            return Official;
        float Get(string key, float fallback) => extras.TryGetValue(key, out var v) && double.IsFinite(v) ? (float)v : fallback;
        var o = Official;
        float min = Get("MinPrimScale", o.MinScale), max = Get("MaxPrimScale", o.MaxScale);
        if (min <= 0 || max <= 0 || min > max)
            (min, max) = (o.MinScale, o.MaxScale);
        float minPhys = Get("MinPhysPrimScale", o.MinPhysScale), maxPhys = Get("MaxPhysPrimScale", o.MaxPhysScale);
        if (minPhys <= 0 || maxPhys <= 0 || minPhys > maxPhys)
            (minPhys, maxPhys) = (o.MinPhysScale, o.MaxPhysScale);
        float low = Get("MinSimHeight", o.MinHeight), high = Get("MaxSimHeight", o.MaxHeight);
        if (high <= 0 || low >= high)
            (low, high) = (o.MinHeight, o.MaxHeight);
        return new(min, max, minPhys, maxPhys, low, high, o.MaxLinkPrims);
    }
}

/// <summary>What the agent may do with a selected object, from the flags the region sends with it and, once its properties have
/// arrived, its owner permissions.</summary>
public sealed record EditAccess(bool CanModify, bool CanMove, bool Owned, bool Locked)
{
    /// <summary>Read-only: selected, shown, nothing can be changed.</summary>
    public bool ReadOnly => !CanModify && !CanMove;
    /// <summary>Position and rotation can be changed.</summary>
    public bool CanPlace => CanMove && !Locked;
    /// <summary>Size, shape and the object's own settings can be changed.</summary>
    public bool CanReshape => CanModify;

    /// <summary>The region's move and modify flags decide (llviewerobject.cpp permMove, permModify); an owned object whose owner mask has
    /// no move is locked (llpanelobject.cpp).</summary>
    public static EditAccess Of(uint flags, uint? ownerMask)
    {
        bool owned = (flags & ObjectFlagBits.YouOwner) != 0;
        bool locked = owned && ownerMask is { } m && (m & PermMove) == 0;
        return new((flags & ObjectFlagBits.Modify) != 0, (flags & ObjectFlagBits.Move) != 0, owned, locked);
    }

    private const uint PermMove = 1u << 19;
}

/// <summary>The edit tools' grid and snapping (the official viewer's SnapEnabled grid, its rotation snap and its scale snap).</summary>
public static class Snap
{
    /// <summary>Rotation snaps to 360/64 degree steps.</summary>
    public const float AngleIncrementDegrees = 5.625f;
    /// <summary>The grid's default unit, in metres (GridResolution).</summary>
    public const float DefaultGridUnit = 0.5f;
    /// <summary>Grid ticks are never drawn or snapped to closer than this many pixels apart.</summary>
    public const float MinTickPixels = 3f;

    /// <summary>How many parts each grid unit is cut into for snapping: the largest power of two that keeps ticks at least
    /// <see cref="MinTickPixels"/> apart, at most <paramref name="subdivision"/> when sub-units are on and 1 when they are off, at least 1/32.</summary>
    public static float Subdivisions(float gridUnit, float metresPerPixel, bool subUnits, int subdivision = 32)
    {
        float max = subUnits ? subdivision : 1f, min = 1f / 32f;
        if (!(gridUnit > 0) || !(metresPerPixel > 0))
            return max;
        // the largest power of two whose tick (unit / level) is still MinTickPixels wide
        float level = MathF.Pow(2f, MathF.Floor(MathF.Log2(gridUnit / (metresPerPixel * MinTickPixels))));
        return Math.Clamp(level, min, max);
    }

    /// <summary>A value along an axis rounded to the grid (unit / subdivisions), measured from <paramref name="origin"/>.</summary>
    public static float ToGrid(float value, float origin, float unit, float subdivisions)
    {
        float step = unit / subdivisions;
        return step > 0 ? origin + MathF.Round((value - origin) / step, MidpointRounding.AwayFromZero) * step : value;
    }

    /// <summary>A rotation angle in degrees: snapped to <see cref="AngleIncrementDegrees"/> steps only while the pointer is outside the snap ring.</summary>
    public static float Angle(float degrees, bool outsideRing) =>
        outsideRing ? MathF.Round(degrees / AngleIncrementDegrees, MidpointRounding.AwayFromZero) * AngleIncrementDegrees : degrees;

    /// <summary>A size dragged with a scale handle: rounded to the grid unit / subdivisions while snapping, then held to the limits.</summary>
    public static float Size(float size, float unit, float subdivisions, bool snapping, float min, float max)
    {
        float step = unit / subdivisions;
        if (snapping && step > 0)
            size = MathF.Round(size / step, MidpointRounding.AwayFromZero) * step;
        return Math.Clamp(size, min, max);
    }
}

/// <summary>The scale handles: a corner keeps the proportions, a face changes one axis, and with "Stretch Both Sides" off the opposite face stays put.</summary>
public static class ScaleHandles
{
    /// <summary>A corner drag by <paramref name="ratio"/>: every axis by the same ratio, the ratio held so no axis leaves the limits.</summary>
    public static Vector3 Corner(Vector3 start, float ratio, float min, float max)
    {
        float smallest = MathF.Min(start.X, MathF.Min(start.Y, start.Z)), largest = MathF.Max(start.X, MathF.Max(start.Y, start.Z));
        if (!(smallest > 0))
            return start;
        ratio = Math.Clamp(ratio, min / smallest, max / largest);
        return start * ratio;
    }

    /// <summary>A face drag to <paramref name="newSize"/> along local axis <paramref name="axis"/> (0, 1, 2) on the face facing
    /// <paramref name="sign"/> (+1 or -1): the new scale and the shift of the centre in the object's own axes (none when both sides stretch).</summary>
    public static (Vector3 Scale, Vector3 CentreShift) Face(Vector3 start, int axis, int sign, float newSize, bool bothSides, float min, float max)
    {
        newSize = Math.Clamp(newSize, min, max);
        var scale = start;
        float old = axis switch { 0 => start.X, 1 => start.Y, _ => start.Z };
        switch (axis) { case 0: scale.X = newSize; break; case 1: scale.Y = newSize; break; default: scale.Z = newSize; break; }
        if (bothSides)
            return (scale, Vector3.Zero);
        float d = (newSize - old) / 2f * Math.Sign(sign);
        var shift = axis switch { 0 => new Vector3(d, 0, 0), 1 => new Vector3(0, d, 0), _ => new Vector3(0, 0, d) };
        return (scale, shift);
    }
}

/// <summary>A region's ground area in metres, its south-west corner relative to the agent's own region's.</summary>
public readonly record struct RegionArea(float X, float Y, float Width, float Depth);

/// <summary>Number entry in the edit window.</summary>
public static class EditNumbers
{
    /// <summary>Rotation entry is held to 0.05 degree steps.</summary>
    public const float RotationStepDegrees = 0.05f;

    /// <summary>The rotation for Euler angles in degrees, as the official viewer reads them: X, then Y, then Z, each about the world axes.</summary>
    public static Quaternion FromEulerDegrees(Vector3 degrees)
    {
        static float Step(float d) => MathF.Round(d / RotationStepDegrees, MidpointRounding.AwayFromZero) * RotationStepDegrees * MathF.PI / 180f;
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, Step(degrees.X));
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Step(degrees.Y));
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Step(degrees.Z));
        // x first, then y, then z, each about the world axes
        return Quaternion.Normalize(Quaternion.Concatenate(Quaternion.Concatenate(qx, qy), qz));
    }

    /// <summary>The Euler angles in degrees (each -180 to 180) that <see cref="FromEulerDegrees"/> turns back into <paramref name="q"/>.</summary>
    public static Vector3 ToEulerDegrees(Quaternion q)
    {
        // R = Rz * Ry * Rx acting on column vectors; System.Numerics stores the transpose (row vectors), so R[i][j] is m.M(j+1)(i+1)
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q));
        float r20 = m.M13, r21 = m.M23, r22 = m.M33, r10 = m.M12, r00 = m.M11;
        float y = MathF.Asin(Math.Clamp(-r20, -1f, 1f));
        float x, z;
        if (MathF.Abs(r20) < 0.99999f)
        {
            x = MathF.Atan2(r21, r22);
            z = MathF.Atan2(r10, r00);
        }
        else
        {
            x = MathF.Atan2(-m.M32, m.M22);
            z = 0;
        }
        const float deg = 180f / MathF.PI;
        return new Vector3(x * deg, y * deg, z * deg);
    }

    /// <summary>A position typed in: x and y inside the region, z from the ground less half the object's diagonal up to the limit.</summary>
    public static Vector3 ClampPosition(Vector3 p, float regionWidth, float regionHeight, float ground, float halfDiagonal, EditLimits limits)
    {
        float low = MathF.Min(MathF.Max(ground - halfDiagonal, limits.MinHeight), limits.MaxHeight);
        return new Vector3(Math.Clamp(p.X, 0, regionWidth), Math.Clamp(p.Y, 0, regionHeight), Math.Clamp(p.Z, low, limits.MaxHeight));
    }

    public static Vector3? TypedPosition(Vector3 p, IReadOnlyList<RegionArea> regions, float ground, float halfDiagonal, EditLimits limits) => null;

    /// <summary>A size typed in: each axis held to the limits (the physical limits for a physical object).</summary>
    public static Vector3 ClampScale(Vector3 s, bool physical, EditLimits limits)
    {
        float min = physical ? limits.MinPhysScale : limits.MinScale, max = physical ? limits.MaxPhysScale : limits.MaxScale;
        return Vector3.Clamp(s, new Vector3(min), new Vector3(max));
    }
}

/// <summary>Link and Unlink.</summary>
public static class LinkRules
{
    /// <summary>The order the roots go in ObjectLink: the last selected first (it becomes the root), then the others, latest first.</summary>
    public static IReadOnlyList<uint> Order(IReadOnlyList<uint> selectedInOrder) => selectedInOrder.Reverse().Distinct().ToArray();

    /// <summary>Why a link is refused, or null: at least two roots, every one modifiable and owned, at most the limit's prims in all.</summary>
    public static string? Refusal(IReadOnlyList<(uint Flags, int Prims)> roots, EditLimits limits)
    {
        if (roots.Count < 2)
            return "link.too_few";
        if (roots.Any(r => (r.Flags & ObjectFlagBits.Modify) == 0))
            return "link.not_modifiable";
        if (roots.Any(r => (r.Flags & ObjectFlagBits.YouOwner) == 0))
            return "link.not_owned";
        if (roots.Sum(r => Math.Max(1, r.Prims)) > limits.MaxLinkPrims)
            return "link.too_many";
        return null;
    }
}

/// <summary>The prim types of the edit window's Object tab.</summary>
public enum PrimKind { Box, Cylinder, Prism, Sphere, Torus, Tube, Ring, Sculpt }

/// <summary>The ObjectShape message's quantised shape block.</summary>
public readonly record struct ShapeBytes(byte PathCurve, byte ProfileCurve, ushort PathBegin, ushort PathEnd, byte PathScaleX, byte PathScaleY,
    byte PathShearX, byte PathShearY, sbyte PathTwist, sbyte PathTwistBegin, sbyte PathRadiusOffset, sbyte PathTaperX, sbyte PathTaperY,
    byte PathRevolutions, sbyte PathSkew, ushort ProfileBegin, ushort ProfileEnd, ushort ProfileHollow);

/// <summary>The shape parameters as the official Object tab offers them, per prim type, and their encoding for the ObjectShape message.</summary>
public static class ShapeRules
{
    public const double MinCutGap = 0.02;

    public const int Line = 0x10, Circle = 0x20;
    public const int ProfileCircle = 0, ProfileSquare = 1, ProfileEqualTriangle = 3, ProfileHalfCircle = 5;
    public const int HoleSame = 0x00, HoleCircle = 0x10, HoleSquare = 0x20, HoleTriangle = 0x30;

    public static PrimKind KindOf(PrimShape s)
    {
        if (s.SculptType != 0)
            return PrimKind.Sculpt;
        int profile = s.ProfileCurve & 0x07;
        bool line = s.PathCurve == Line || s.PathCurve == 0x80;
        return (profile, line) switch
        {
            (ProfileSquare, true) => PrimKind.Box,
            (ProfileCircle, true) => PrimKind.Cylinder,
            (ProfileEqualTriangle, true) => PrimKind.Prism,
            (ProfileHalfCircle, false) => PrimKind.Sphere,
            (ProfileCircle, false) => PrimKind.Torus,
            (ProfileSquare, false) => PrimKind.Tube,
            (ProfileEqualTriangle, false) => PrimKind.Ring,
            _ => line ? PrimKind.Box : PrimKind.Torus,
        };
    }

    private static bool CircularPath(PrimKind k) => k is PrimKind.Sphere or PrimKind.Torus or PrimKind.Tube or PrimKind.Ring;

    /// <summary>The default shape of a prim type (the shape a new prim of that type gets when the type is chosen).</summary>
    public static PrimShape OfKind(PrimKind kind) => kind switch
    {
        PrimKind.Cylinder => new PrimShape { ProfileCurve = ProfileCircle, PathCurve = Line },
        PrimKind.Prism => new PrimShape { ProfileCurve = ProfileEqualTriangle, PathCurve = Line },
        PrimKind.Sphere => new PrimShape { ProfileCurve = ProfileHalfCircle, PathCurve = Circle },
        PrimKind.Torus => new PrimShape { ProfileCurve = ProfileCircle, PathCurve = Circle, PathScaleY = 0.25 },
        PrimKind.Tube => new PrimShape { ProfileCurve = ProfileSquare, PathCurve = Circle, PathScaleY = 0.25 },
        PrimKind.Ring => new PrimShape { ProfileCurve = ProfileEqualTriangle, PathCurve = Circle, PathScaleY = 0.25 },
        _ => PrimShape.Box,
    };

    /// <summary>Every parameter held to the official range for the prim's type.</summary>
    public static PrimShape Clamp(PrimShape s)
    {
        var kind = KindOf(s);
        if (kind == PrimKind.Sculpt)
            return s;
        static (double, double) Cut(double b, double e)
        {
            b = Math.Clamp(b, 0, 1 - MinCutGap);
            e = Math.Clamp(e, MinCutGap, 1);
            return (Math.Min(b, e - MinCutGap), e);
        }
        var (pb, pe) = Cut(s.PathBegin, s.PathEnd);
        var (fb, fe) = Cut(s.ProfileBegin, s.ProfileEnd);
        double sx = s.PathScaleX, sy = s.PathScaleY, radius = s.PathRadiusOffset, skew = s.PathSkew, revs = Math.Clamp(s.PathRevolutions, 1, 4);
        double taperX = Math.Clamp(s.PathTaperX, -1, 1), taperY = Math.Clamp(s.PathTaperY, -1, 1);
        if (kind is PrimKind.Torus or PrimKind.Tube or PrimKind.Ring)
        {
            // hole size (llprimitive.cpp OBJECT_MIN_HOLE_SIZE, OBJECT_MAX_HOLE_SIZE_X/Y)
            sx = Math.Clamp(sx, 0.05, 1);
            sy = Math.Clamp(sy, 0.05, 0.5);
            // the radius offset may not push the hole through the outside (llpanelobject.cpp)
            double holeY = sy, room = 1 - holeY * (1 - (Math.Sign(taperY) == Math.Sign(radius) || radius == 0 ? Math.Abs(taperY) : 0)) / (1 - holeY);
            radius = Math.Clamp(radius, -Math.Max(0, room), Math.Max(0, room));
            skew = Math.Clamp(skew, -0.95, 0.95);
            if (revs > 1 && skew != 0)
            {
                double minSkew = 1 - 1 / (revs * sx + 1);
                if (Math.Abs(skew) < minSkew)
                    skew = Math.Sign(skew) * minSkew;
            }
        }
        else if (kind == PrimKind.Sphere)
        {
            (sx, sy, radius, skew, revs, taperX, taperY) = (1, 1, 0, 0, 1, 0, 0);
        }
        else
        {
            sx = Math.Clamp(sx, 0, 2);
            sy = Math.Clamp(sy, 0, 2);
            (radius, skew, revs) = (0, 0, 1);
        }
        return s with
        {
            PathBegin = pb, PathEnd = pe, ProfileBegin = fb, ProfileEnd = fe,
            ProfileHollow = Math.Clamp(s.ProfileHollow, 0, MaxHollow(s)),
            PathTwist = Math.Clamp(s.PathTwist, -1, 1), PathTwistBegin = Math.Clamp(s.PathTwistBegin, -1, 1),
            PathScaleX = sx, PathScaleY = sy,
            PathShearX = Math.Clamp(s.PathShearX, -0.5, 0.5), PathShearY = Math.Clamp(s.PathShearY, -0.5, 0.5),
            PathTaperX = taperX, PathTaperY = taperY, PathRadiusOffset = radius, PathSkew = skew, PathRevolutions = revs,
        };
    }

    /// <summary>The largest hollow for a shape (0.95, or 0.70 with a square hole on a round or triangular type).</summary>
    public static double MaxHollow(PrimShape s) =>
        s.ProfileHole == HoleSquare && KindOf(s) is PrimKind.Cylinder or PrimKind.Torus or PrimKind.Prism or PrimKind.Ring or PrimKind.Sphere ? 0.70 : 0.95;

    /// <summary>The quantised block the ObjectShape message carries.</summary>
    public static ShapeBytes Encode(PrimShape s)
    {
        // llvolumemessage.cpp packing with the quanta of llvolume.h: cuts and hollow in 1/50000, scale, shear, twist and taper in 1/100,
        // revolutions in 0.015 steps above one
        const double cut = 0.00002, unit = 0.01, rev = 0.015;
        static int R(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
        static sbyte S8(double v) => (sbyte)Math.Clamp(R(v), sbyte.MinValue, sbyte.MaxValue);
        static byte U8(double v) => (byte)Math.Clamp(R(v), 0, 255);
        static ushort U16(double v) => (ushort)Math.Clamp(R(v), 0, 50000);
        return new ShapeBytes(
            (byte)s.PathCurve, (byte)((s.ProfileCurve & 0x0F) | (s.ProfileHole & 0xF0)),
            U16(s.PathBegin / cut), U16(50000 - R(s.PathEnd / cut)),
            U8(200 - R(s.PathScaleX / unit)), U8(200 - R(s.PathScaleY / unit)),
            unchecked((byte)S8(s.PathShearX / unit)), unchecked((byte)S8(s.PathShearY / unit)),
            S8(s.PathTwist / unit), S8(s.PathTwistBegin / unit), S8(s.PathRadiusOffset / unit),
            S8(s.PathTaperX / unit), S8(s.PathTaperY / unit),
            U8((s.PathRevolutions - 1) / rev), S8(s.PathSkew / unit),
            U16(s.ProfileBegin / cut), U16(50000 - R(s.ProfileEnd / cut)), U16(s.ProfileHollow / cut));
    }

    /// <summary>Twist in the window is in degrees: -180 to 180 on a straight path, -360 to 360 on a circular one.</summary>
    public static double TwistDegreesRange(PrimShape s) => CircularPath(KindOf(s)) ? 360 : 180;
}
