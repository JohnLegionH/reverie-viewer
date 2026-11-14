// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llvosky.cpp
 * @brief LLVOSky class implementation
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

/**
 * @file sunDiscF.glsl
 *
 * $LicenseInfo:firstyear=2005&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2005, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

/**
 * @file indra_constants.cpp
 * @brief some useful short term constants for Indra
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

// Adapted 2026 by Legion Builds. SunDisc follows the square the official viewer draws the sun's picture on (llvosky.cpp: LLVOSky::updateHeavenlyBodyGeometry, with the body factor and
// disk radius of llvosky.h and llvosky.cpp, and the picture's corners in its texture coordinates), the picture shown as it is with no light-strength term (sunDiscF.glsl), and the
// default sun texture (indra_constants.cpp: IMG_SUN), written in C#. The rest of this file is our own code.

// Copyright (c) 2026 Legion Builds

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>
/// The sun's disc as a picture on a square in the sky. The official viewer draws the sky's sun texture on a square facing the camera, set across the sun's direction, its half size
/// the body factor (0.1) times the disk radius (0.5) times the sky's sun scale, grown towards the horizon (a third wider and a fifth taller at most); the picture is drawn as it is,
/// with no term for the light's strength. Directions in the viewer's frame (z up).
/// </summary>
public static class SunDisc
{
    /// <summary>The official default sun texture (IMG_SUN), used when a sky names none.</summary>
    public static readonly Guid DefaultTextureId = new("cce0f112-878f-4586-a2e2-a8f104bba271");

    /// <summary>The body factor times the disk radius: the square's half size, as a part of its distance, at scale 1 with the sun overhead.</summary>
    public const float HalfSize = 0.1f * 0.5f;

    /// <summary>How much the square grows across and up towards the horizon (times one minus the sun's height).</summary>
    public const float HorizontalGrowth = 0.3f, VerticalGrowth = 0.2f;

    /// <summary>The texture a sky draws its sun with: its own, else the default.</summary>
    public static Guid TextureOf(SkyFrame f) => f.SunTextureId is { } id && id != Guid.Empty ? id : DefaultTextureId;

    /// <summary>
    /// The square's axes and half sizes (as parts of its distance): across is the sun's direction crossed with the vertical, up is across crossed with the sun's direction
    /// (at the zenith, the official viewer turns to fixed axes).
    /// </summary>
    public static (Vector3 Right, Vector3 Up, float HalfAcross, float HalfUp) Axes(Vector3 sunDirection, float scale)
    {
        var sun = Vector3.Normalize(sunDirection);
        var right = Vector3.Cross(sun, Vector3.UnitZ);
        if (right.LengthSquared() < 1e-8f) right = -Vector3.UnitY;
        right = Vector3.Normalize(right);
        var up = Vector3.Normalize(Vector3.Cross(right, sun));
        float growth = 1f - sun.Z;
        return (right, up, scale * HalfSize * (1f + growth * HorizontalGrowth), scale * HalfSize * (1f + growth * VerticalGrowth));
    }

    /// <summary>
    /// Where a view direction falls on the sun's picture: 0 to 1 across (from the left, looking at the sun) and 0 to 1 up (from the bottom), or null outside the square and for a
    /// direction away from the sun.
    /// </summary>
    public static Vector2? Coordinates(Vector3 direction, Vector3 sunDirection, float scale)
    {
        var sun = Vector3.Normalize(sunDirection);
        float along = Vector3.Dot(direction, sun);
        if (!(along > 1e-4f) || !(scale > 0f)) return null;
        var (right, up, halfAcross, halfUp) = Axes(sun, scale);
        // the point the ray meets on the plane of the square (the sun's direction at distance 1), taken from the square's centre
        var offset = direction / along - sun;
        float u = Vector3.Dot(offset, right) / halfAcross, v = Vector3.Dot(offset, up) / halfUp;
        if (MathF.Abs(u) > 1.0005f || MathF.Abs(v) > 1.0005f) return null;   // a hair of slack for rounding at the edge
        u = Math.Clamp(u, -1f, 1f); v = Math.Clamp(v, -1f, 1f);
        return new Vector2(0.5f + 0.5f * u, 0.5f + 0.5f * v);
    }
}
