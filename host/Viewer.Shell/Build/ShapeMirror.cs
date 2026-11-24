// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Building;
using Viewer.Core.Meshing;

namespace Viewer.Shell.Build;

/// <summary>
/// Mirror's shape rules: the shape a prim must have so that it is the mirror image of itself about one of its own middle planes. A
/// reflection is not a rotation, so a prim with a handed shape (twisted, sheared, cut) cannot be put onto its image by placement alone;
/// the shape parameters are reflected too. <c>axis</c> is the local axis the plane stands square to: 0 for X, 1 for Y, 2 for Z (the
/// prim's path runs along Z, its profile lies in X and Y). Each table below was checked against the real mesh (the reflected vertices of
/// the old shape are the vertices of the new one; MirrorShapeTests). Where no parameters can express the image, <see cref="Flip"/> gives
/// null and the part is only placed. Our own code, no source adapted.
/// </summary>
public static class ShapeMirror
{
    private const double Eps = 1e-9;

    /// <summary>The shape that is the mirror image of <paramref name="shape"/> about the plane through its middle square to local
    /// <paramref name="axis"/>; the shape itself when it is its own image; null when no shape parameters give the image (a mesh, a flexible
    /// path, a curved path with handed settings, a cut that would wrap past the start, a sheared path about its own length).</summary>
    public static PrimShape? Flip(PrimShape shape, int axis)
    {
        if (axis is < 0 or > 2)
            return null;
        if (shape.SculptType == PrimShape.SculptTypeMesh)
            return null;
        if (shape.SculptType != 0)
            return axis == 0 ? shape with { SculptFlags = shape.SculptFlags ^ SculptMesher.FlagMirror } : null;   // the Mirror bit flips a sculpt along its X axis
        return (shape.PathCurve & 0xF0) switch
        {
            ShapeRules.Line => FlipStraight(shape, axis),
            ShapeRules.Circle or 0x30 => FlipCurved(shape, axis),
            _ => null,
        };
    }

    /// <summary>Whether the two shapes are the same as the region keeps them (the quantised block is equal), so nothing needs sending.</summary>
    public static bool SameOnTheWire(PrimShape a, PrimShape b) => ShapeRules.Encode(a) == ShapeRules.Encode(b) && a.SculptFlags == b.SculptFlags;

    private static PrimShape? FlipStraight(PrimShape s, int axis)
    {
        if (!ProfileKnown(s.ProfileCurve & 0x07))
            return null;
        switch (axis)
        {
            case 2:
                // the path runs the other way: the cut, the twist ends and the taper (1 minus the path scale) trade ends
                if (Math.Abs(s.PathShearX) > Eps || Math.Abs(s.PathShearY) > Eps)
                    return null;   // a shear is measured from the bottom, so the image would also have to slide sideways
                return s with
                {
                    PathBegin = 1 - s.PathEnd, PathEnd = 1 - s.PathBegin,
                    PathTwistBegin = s.PathTwist, PathTwist = s.PathTwistBegin,
                    PathScaleX = 2 - s.PathScaleX, PathScaleY = 2 - s.PathScaleY,
                };
            case 0:
                return ProfileAbout(s, 0) is { } x
                    ? x with { PathTwistBegin = -s.PathTwistBegin, PathTwist = -s.PathTwist, PathShearX = -s.PathShearX }
                    : null;
            default:
                return ProfileAbout(s, 1) is { } y
                    ? y with { PathTwistBegin = -s.PathTwistBegin, PathTwist = -s.PathTwist, PathShearY = -s.PathShearY }
                    : null;
        }
    }

    /// <summary>A curved path (a sphere, torus, tube or ring) is its own image about every middle plane when nothing about it is handed;
    /// otherwise it is not flipped.</summary>
    private static PrimShape? FlipCurved(PrimShape s, int axis) =>
        s.PathBegin <= Eps && s.PathEnd >= 1 - Eps && s.ProfileBegin <= Eps && s.ProfileEnd >= 1 - Eps
        && Math.Abs(s.PathTwist) <= Eps && Math.Abs(s.PathTwistBegin) <= Eps && Math.Abs(s.PathShearX) <= Eps && Math.Abs(s.PathShearY) <= Eps
        && Math.Abs(s.PathTaperX) <= Eps && Math.Abs(s.PathTaperY) <= Eps && Math.Abs(s.PathRadiusOffset) <= Eps && Math.Abs(s.PathSkew) <= Eps
        && Math.Abs(s.PathRevolutions - 1) <= Eps
            ? s : null;

    private static bool ProfileKnown(int curve) => curve is 0 or 1 or 3;

    /// <summary>The profile cut reflected about the profile's X plane (plane 0) or Y plane (plane 1). A cut [b, e] becomes [c - e, c - b]
    /// (taken once round the profile if it would start below zero), where the constant c depends on where the profile type starts round its
    /// outline, found against the mesh: a circle 0.5 about X and 0 about Y, a square 0.25 and 0.75, a triangle 0 about Y (a triangle, or a
    /// triangular hole, has no image about X). Null when the image would run past the end of the profile.</summary>
    private static PrimShape? ProfileAbout(PrimShape s, int plane)
    {
        int curve = s.ProfileCurve & 0x07;
        bool triangleHole = s.ProfileHollow > 0 && (s.ProfileHole == ShapeRules.HoleTriangle || (s.ProfileHole == ShapeRules.HoleSame && curve == 3));
        if (plane == 0 && (curve == 3 || triangleHole))
            return null;
        double c = curve switch { 0 => plane == 0 ? 0.5 : 0.0, 1 => plane == 0 ? 0.25 : 0.75, _ => 0.0 };
        double b = s.ProfileBegin, e = s.ProfileEnd;
        if (b <= Eps && e >= 1 - Eps)
            return s;
        double nb = c - e, ne = c - b;
        if (nb < -Eps)
        {
            nb += 1;
            ne += 1;
        }
        return nb >= -Eps && ne <= 1 + Eps && ne > nb ? s with { ProfileBegin = Math.Max(0, nb), ProfileEnd = Math.Min(1, ne) } : null;
    }
}
