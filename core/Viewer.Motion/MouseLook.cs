// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file lltoolgun.cpp
 * @brief LLToolGun class implementation
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
 * @file llviewerwindow.cpp
 * @brief Implementation of the LLViewerWindow class.
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
 * @file llagent.cpp
 * @brief LLAgent class implementation
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
/*
 * Ported to C# from the Second Life Viewer 26.4 (release tag Second_Life_Release#e960123-26.4, commit e960123230d521e8557a9582100bebcac36a7ca2) source files whose notices are above.
 * Adapted 2026 by Legion Builds. Copyright (c) 2026 Legion Builds for the changes.
 */
namespace Viewer.Motion;

/// <summary>
/// Mouse-look numbers, from the official viewer (behaviour only, in our own words): the sensitivity setting (default 3.0,
/// range 0 to 15) is rescaled to 0.5 to 2.75 and multiplied by a nominal 0.0025 radians per pixel (lltoolgun.cpp,
/// handleHover); optional smoothing eases the mouse movement towards each new movement at 16 a second (llviewerwindow.cpp,
/// the MouseSmooth branch of the mouse-move handler, off by default); the view may not point closer than 5 degrees to
/// straight up or 1 degree to straight down (llagent.cpp, pitch).
/// </summary>
public static class MouseLookRules
{
    public const float DefaultSensitivity = 3f, NominalRadiansPerPixel = 0.0025f, SmoothRate = 16f;
    /// <summary>The highest and lowest the view may point, in radians above the horizon.</summary>
    public const float MaxPitch = 85f * MathF.PI / 180, MinPitch = -89f * MathF.PI / 180;

    /// <summary>Radians of turn per pixel of mouse movement for a sensitivity setting (0 to 15 maps to 0.5 to 2.75 times the nominal).</summary>
    public static float RadiansPerPixel(float sensitivity)
    {
        float t = Math.Clamp(float.IsFinite(sensitivity) ? sensitivity : DefaultSensitivity, 0f, 15f) / 15f;
        return (0.5f + t * (2.75f - 0.5f)) * NominalRadiansPerPixel;
    }
}

/// <summary>The mouse smoothing: each movement is eased into a running value that is rounded to whole pixels.</summary>
public sealed class MouseSmoother
{
    private double _x, _y;

    /// <summary>One frame of <paramref name="dt"/> seconds with the raw movement; the smoothed movement in whole pixels.</summary>
    public (int X, int Y) Step(float dx, float dy, double dt)
    {
        double k = Math.Min(Math.Max(0, dt) * MouseLookRules.SmoothRate, 1);
        _x += (dx - _x) * k;
        _y += (dy - _y) * k;
        return ((int)Math.Floor(_x + 0.5), (int)Math.Floor(_y + 0.5));
    }

    /// <summary>Forgets the running value (the view changed).</summary>
    public void Reset() => _x = _y = 0;
}
