// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file class3\environment\underWaterF.glsl
 *
 * $LicenseInfo:firstyear=2007&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2007, Linden Research, Inc.
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
 * @file class1\environment\waterFogF.glsl
 *
 * $LicenseInfo:firstyear=2007&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2007, Linden Research, Inc.
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
 * @file class3/deferred/waterHazeF.glsl
 *
 * $LicenseInfo:firstyear=2023&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2023, Linden Research, Inc.
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
* @file llsettingswater.h
* @author optional
* @brief A base class for asset based settings groups.
*
* $LicenseInfo:2011&license=viewerlgpl$
* Second Life Viewer Source Code
* Copyright (C) 2017, Linden Research, Inc.
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
 * @file llviewercamera.cpp
 * @brief LLViewerCamera class implementation
 *
 * $LicenseInfo:firstyear=2002&license=viewerlgpl$
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

// Adapted 2026 by Legion Builds. Ported from the Second Life Viewer (github.com/secondlife/viewer, commit
// e34b5c188e758b6d24b768cc44fbee459be2855c), written in C#:
// UnderwaterView.EyeBelow follows LLViewerCamera::cameraUnderWater (indra/newview/llviewercamera.cpp).
// UnderwaterView.FogDensity follows LLSettingsWater::getModifiedWaterFogDensity (indra/llinventory/llsettingswater.cpp).
// UnderwaterView.Fog follows getWaterFogViewNoClip for an eye under the water (indra/newview/app_settings/shaders/class1/environment/waterFogF.glsl).
// UnderwaterView.Haze follows the main() of indra/newview/app_settings/shaders/class3/deferred/waterHazeF.glsl (the transmittance to the
// power 1.7) and its blend. UnderwaterView.SurfaceFromBelow and UnderwaterView.Distort follow the main() of
// indra/newview/app_settings/shaders/class3/environment/underWaterF.glsl.
// Copyright (c) 2026 Legion Builds

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>
/// The view from below the water, as the official viewer draws it: while the camera is under the surface the water's fog density is raised
/// to the underwater modifier, everything below the surface is hazed by the water fog along the ray from the camera, and the surface seen
/// from below is what lies above it, moved a little by the waves and fogged along the way to the surface, with no mirror reflection.
/// Heights are the region's (z up). All colours are linear unless said otherwise. The drawing is in the host (EnvironmentLayer), whose
/// shader writes the same formulas.
/// </summary>
public static class UnderwaterView
{
    // where the density and the slant times the ray's rise cancel, the fog expression is 0/0: the divisor is held this far from zero
    private const double SmallestDivisor = 1e-4;

    /// <summary>Whether the camera counts as under the water: strictly below the surface.</summary>
    public static bool EyeBelow(float eyeZ, float? waterHeight) => waterHeight is { } h && eyeZ < h;

    /// <summary>The water height of the region the camera is over: a neighbour's where the point lies in one, else the agent's own region's.</summary>
    public static float HeightAt(float x, float y, float homeHeight, IReadOnlyList<WaterRect> others)
    {
        for (int i = 0; i < others.Count; i++)
        {
            var r = others[i];
            if (x >= r.X0 && x < r.X1 && y >= r.Y0 && y < r.Y1) return r.Height;
        }
        return homeHeight;
    }

    /// <summary>
    /// The water fog density in force: under water, and with a modifier above 0, the density to the power of the modifier (held to 10).
    /// A negative density with a modifier that is not whole is taken as 1, since that power has no real value.
    /// </summary>
    public static float FogDensity(float density, float fogMod, bool underwater)
    {
        if (!underwater || !(fogMod > 0f)) return density;
        float mod = Math.Min(fogMod, 10f);
        if (density < 0f && mod != MathF.Round(mod)) density = 1f;
        return MathF.Pow(density, mod);
    }

    /// <summary>
    /// The water fog along a ray from an eye under the surface to a point <paramref name="distance"/> metres away: the fog's light (to add)
    /// and the share of what is there that survives (to multiply). <paramref name="fogColour"/> is the frame's fog colour as stored
    /// (display-encoded), <paramref name="density"/> from <see cref="FogDensity"/>, <paramref name="slant"/> from
    /// <see cref="WaterShading.FogSlant"/>, <paramref name="eyeDepth"/> how far under the surface the eye is, <paramref name="rayUp"/> the
    /// ray's height component (1 straight up, -1 straight down). The fog's light dims with the eye's depth.
    /// </summary>
    public static (Vector3 Light, float Survives) Fog(Vector3 fogColour, float density, float slant, float eyeDepth, float rayUp, float distance)
    {
        double length = Math.Max(distance, 0.1), kd = density;
        double t1 = -kd * Math.Pow(0.98, slant * Math.Max(eyeDepth, 0f));
        double t2 = kd - (double)slant * rayUp;
        if (Math.Abs(t2) < SmallestDivisor) t2 = t2 < 0 ? -SmallestDivisor : SmallestDivisor;
        double t3 = Math.Pow(0.98, t2 * length) - 1.0;
        double inner = Math.Min(t1 / t2 * t3, 1.0);
        double lit = inner > 0.0 ? Math.Pow(inner, 1.0 / 1.7) : 0.0;
        double survives = Math.Pow(0.98, length * kd);
        return (new Vector3(Decode(fogColour.X), Decode(fogColour.Y), Decode(fogColour.Z)) * (float)lit, (float)survives);
    }

    /// <summary>What the haze leaves of a point below the surface: what survives, to the power 1.7, with the fog's light added.</summary>
    public static Vector3 Haze(Vector3 behind, Vector3 light, float survives) => behind * MathF.Pow(MathF.Max(survives, 0f), 1.7f) + light;

    /// <summary>The surface seen from below: what is above it, dimmed by the water on the way there, with the fog's light added.</summary>
    public static Vector3 SurfaceFromBelow(Vector3 above, Vector3 light, float survives) => above * survives + light;

    /// <summary>
    /// Where the view through the surface reads what is above: the screen position moved by the sideways part of the three wave layers
    /// (their sum made a unit vector) times the water's scale below, the same at any distance.
    /// </summary>
    public static Vector2 Distort(Vector2 screen, Vector3 waveSum, float scaleBelow)
    {
        var w = Vector3.Normalize(waveSum);
        return screen + new Vector2(w.X, w.Y) * scaleBelow;
    }

    private static float Decode(float v) => v < 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
}
