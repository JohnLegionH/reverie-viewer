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
    public static EditLimits FromExtras(IReadOnlyDictionary<string, double>? extras) => Official;
}

/// <summary>What the agent may do with a selected object, from the flags the region sends with it and, once its properties have
/// arrived, its owner permissions.</summary>
public sealed record EditAccess(bool CanModify, bool CanMove, bool Owned, bool Locked)
{
    /// <summary>Read-only: selected, shown, nothing can be changed.</summary>
    public bool ReadOnly => !CanModify && !CanMove;
    /// <summary>Position and rotation can be changed.</summary>
    public bool CanPlace => false;
    /// <summary>Size, shape and the object's own settings can be changed.</summary>
    public bool CanReshape => false;

    public static EditAccess Of(uint flags, uint? ownerMask) => new(false, false, false, false);
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
    public static float Subdivisions(float gridUnit, float metresPerPixel, bool subUnits, int subdivision = 32) => 0;

    /// <summary>A value along an axis rounded to the grid (unit / subdivisions), measured from <paramref name="origin"/>.</summary>
    public static float ToGrid(float value, float origin, float unit, float subdivisions) => 0;

    /// <summary>A rotation angle in degrees: snapped to <see cref="AngleIncrementDegrees"/> steps only while the pointer is outside the snap ring.</summary>
    public static float Angle(float degrees, bool outsideRing) => 0;

    /// <summary>A size dragged with a scale handle: rounded to the grid unit / subdivisions while snapping, then held to the limits.</summary>
    public static float Size(float size, float unit, float subdivisions, bool snapping, float min, float max) => 0;
}

/// <summary>The scale handles: a corner keeps the proportions, a face changes one axis, and with "Stretch Both Sides" off the opposite face stays put.</summary>
public static class ScaleHandles
{
    /// <summary>A corner drag by <paramref name="ratio"/>: every axis by the same ratio, the ratio held so no axis leaves the limits.</summary>
    public static Vector3 Corner(Vector3 start, float ratio, float min, float max) => default;

    /// <summary>A face drag to <paramref name="newSize"/> along local axis <paramref name="axis"/> (0, 1, 2) on the face facing
    /// <paramref name="sign"/> (+1 or -1): the new scale and the shift of the centre in the object's own axes (none when both sides stretch).</summary>
    public static (Vector3 Scale, Vector3 CentreShift) Face(Vector3 start, int axis, int sign, float newSize, bool bothSides, float min, float max) => default;
}

/// <summary>Number entry in the edit window.</summary>
public static class EditNumbers
{
    /// <summary>Rotation entry is held to 0.05 degree steps.</summary>
    public const float RotationStepDegrees = 0.05f;

    /// <summary>The rotation for Euler angles in degrees, as the official viewer reads them: X, then Y, then Z, each about the world axes.</summary>
    public static Quaternion FromEulerDegrees(Vector3 degrees) => Quaternion.Identity;

    /// <summary>The Euler angles in degrees (each -180 to 180) that <see cref="FromEulerDegrees"/> turns back into <paramref name="q"/>.</summary>
    public static Vector3 ToEulerDegrees(Quaternion q) => default;

    /// <summary>A position typed in: x and y inside the region, z from the ground less half the object's diagonal up to the limit.</summary>
    public static Vector3 ClampPosition(Vector3 p, float regionWidth, float regionHeight, float ground, float halfDiagonal, EditLimits limits) => default;

    /// <summary>A size typed in: each axis held to the limits (the physical limits for a physical object).</summary>
    public static Vector3 ClampScale(Vector3 s, bool physical, EditLimits limits) => default;
}

/// <summary>Link and Unlink.</summary>
public static class LinkRules
{
    /// <summary>The order the roots go in ObjectLink: the last selected first (it becomes the root), then the others, latest first.</summary>
    public static IReadOnlyList<uint> Order(IReadOnlyList<uint> selectedInOrder) => [];

    /// <summary>Why a link is refused, or null: at least two roots, every one modifiable and owned, at most the limit's prims in all.</summary>
    public static string? Refusal(IReadOnlyList<(uint Flags, int Prims)> roots, EditLimits limits) => null;
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

    public static PrimKind KindOf(PrimShape s) => PrimKind.Box;

    /// <summary>The default shape of a prim type (the shape a new prim of that type gets when the type is chosen).</summary>
    public static PrimShape OfKind(PrimKind kind) => PrimShape.Box;

    /// <summary>Every parameter held to the official range for the prim's type.</summary>
    public static PrimShape Clamp(PrimShape s) => s;

    /// <summary>The largest hollow for a shape (0.95, or 0.70 with a square hole on a round or triangular type).</summary>
    public static double MaxHollow(PrimShape s) => 0;

    /// <summary>The quantised block the ObjectShape message carries.</summary>
    public static ShapeBytes Encode(PrimShape s) => default;

    /// <summary>Twist in the window is in degrees: -180 to 180 on a straight path, -360 to 360 on a circular one.</summary>
    public static double TwistDegreesRange(PrimShape s) => 0;
}
