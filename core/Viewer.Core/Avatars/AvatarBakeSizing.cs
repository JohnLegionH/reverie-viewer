// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llviewerobject.cpp
 * @brief Base class for viewer objects
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

/* Adapted 2026 by Legion Builds: the avatar's pixel area is translated to C# from LLViewerObject::setPixelAreaAndAngle (llviewerobject.cpp),
 * with the camera's pixel-meter ratio and screen pixel area from LLViewerCamera::setPerspective (llviewercamera.cpp:158-160); the bake size claim
 * follows LLVOAvatar::updateTextures and addBakedTextureStats (llvoavatar.cpp:5667-5863). */

using System.Numerics;
using Viewer.Core.Residency;

namespace Viewer.Core.Avatars;

/// <summary>
/// How big an avatar's bakes need to be, as the official viewer decides it. Every baked texture of an avatar claims the avatar's own pixel area
/// as its size (LLVOAvatar::addBakedTextureStats gives the texture <c>pixel_area / texel_area_ratio</c>, the ratio 1 for a bake), and the texture
/// list chooses a discard level from a claim as <see cref="TextureResidency.LevelForClaim"/> does for any texture. An avatar out of view
/// claims nothing (the viewer only makes the claim for an avatar it draws), so its bakes want their coarsest level. Pure.
/// </summary>
public static class AvatarBakeSizing
{
    /// <summary>The avatar's pixel area: the camera's pixels a metre at the avatar's range, times the avatar's largest and middle sizes, not above the screen's.
    /// The range is the distance to the avatar's centre less half its smallest size; within a millimetre of it the avatar fills the screen.</summary>
    /// <param name="size">The avatar's bounding size in metres as the region states it.</param>
    public static double PixelArea(Vector3 camera, Vector3 avatarCentre, Vector3 size, float viewHeightPx, float fovY, float aspect)
    {
        double screen = (double)viewHeightPx * viewHeightPx * aspect;
        float[] s = [size.X, size.Y, size.Z];
        Array.Sort(s);   // smallest, middle, largest
        double range = Vector3.Distance(camera, avatarCentre) - s[0] / 2;
        if (range < 0.001) return screen;
        double pixelsPerMetre = viewHeightPx / (2.0 * Math.Tan(fovY * 0.5)) / range;
        return Math.Min(pixelsPerMetre * s[2] * (pixelsPerMetre * s[1]), screen);
    }

    /// <summary>The level an avatar's bake of <paramref name="fullWidth"/> by <paramref name="fullHeight"/> wants when the avatar's pixel area is
    /// <paramref name="pixelArea"/> (0 for an avatar not in view).</summary>
    public static int WantedLevel(int fullWidth, int fullHeight, int levelCount, double pixelArea) =>
        TextureResidency.LevelForClaim(fullWidth, fullHeight, levelCount, pixelArea);

    /// <summary>The camera's view of an avatar: its pixel area, or 0 when the avatar's bounding sphere is outside the view frustum.</summary>
    public static double ClaimFor(ResidencyCamera camera, Vector3 avatarCentre, Vector3 size)
    {
        float radius = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / 2;
        if (!camera.Sees(new Bounds(avatarCentre, radius))) return 0;
        return PixelArea(camera.Position, avatarCentre, size, camera.ViewportHeight, camera.FovY, camera.Aspect);
    }
}
