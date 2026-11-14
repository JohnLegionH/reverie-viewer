// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>
/// Whether a bounding sphere can show in a perspective view, for deciding cheaply whether something drawn only for what is
/// in view (the glow picture) is needed this frame. The test is generous: the side planes are widened by a share, so a sphere
/// just outside the view, whose glow would still reach into it, counts as visible.
/// </summary>
public static class ViewFrustum
{
    /// <summary>
    /// True when the sphere at <paramref name="centre"/> with <paramref name="radius"/> is inside the view or within
    /// <paramref name="margin"/> (a share of the view's half-width and half-height) outside its sides. The view is at
    /// <paramref name="eye"/>, with the unit axes <paramref name="forward"/>, <paramref name="right"/> and <paramref name="up"/>,
    /// the tangents of half its horizontal and vertical field, and its near and far distances.
    /// </summary>
    public static bool SphereVisible(Vector3 eye, Vector3 forward, Vector3 right, Vector3 up, float tanHalfX, float tanHalfY, float near, float far,
        Vector3 centre, float radius, float margin = 0.1f)
    {
        var p = centre - eye;
        float z = Vector3.Dot(p, forward);
        if (z + radius < near || z - radius > far) return false;
        float ex = tanHalfX * (1f + margin), ey = tanHalfY * (1f + margin);
        float x = MathF.Abs(Vector3.Dot(p, right)), y = MathF.Abs(Vector3.Dot(p, up));
        if (x > z * ex + radius * MathF.Sqrt(1f + ex * ex)) return false;
        if (y > z * ey + radius * MathF.Sqrt(1f + ey * ey)) return false;
        return true;
    }
}
