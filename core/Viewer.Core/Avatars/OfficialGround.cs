// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>
/// The ground an avatar's constraints and feet use: the official viewer's LLWorld::resolveStepHeightGlobal. Straight down from a point, the
/// first thing hit is the land, or the avatar's foot plane (the collision plane the region sends with the avatar) when that is hit first, so an
/// avatar standing on a deck or a prim floor has that surface as its ground, and one on land has the land. The behaviour follows the official
/// viewer's source (llworld.cpp); nothing is copied.
/// </summary>
public static class OfficialGround
{
    /// <summary>The official viewer adds this to the distance to the foot plane, to cover the error in the plane a physics engine reports (metres).</summary>
    public const float PlaneAllowance = 0.05f;

    /// <summary>The height of the ground straight below <paramref name="point"/> (region metres): <paramref name="land"/>, or the foot plane
    /// (<paramref name="plane"/>: normal in x, y, z and the plane's offset in w, in the same frame as the point) when the plane is hit before the land.
    /// All zero means the region sent no plane. Null when neither the land nor a plane is known.</summary>
    public static float? HeightUnder(Vector3 point, float? land, Vector4 plane)
    {
        if (plane == Vector4.Zero) return land;   // the official viewer's isExactlyClear: no plane was sent
        // the distance down to the plane the official way: the point's dot product with the plane's normal less the plane's offset, plus the allowance
        float toPlane = MathF.Max(0f, Vector3.Dot(point, new Vector3(plane.X, plane.Y, plane.Z)) - plane.W + PlaneAllowance);
        if (land is not float l) return point.Z - toPlane;
        return toPlane < point.Z - l ? point.Z - toPlane : l;   // the plane when it is hit before the land
    }
}
