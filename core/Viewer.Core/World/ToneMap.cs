// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file postDeferredTonemap.glsl
 *
 * $LicenseInfo:firstyear=2024&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2024, Linden Research, Inc.
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
* @file llsettingssky.cpp
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
* @file llsettingsvo.cpp
* @author Rider Linden
* @brief Subclasses for viewer specific settings behaviors.
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
 * @file pipeline.cpp
 * @brief Rendering pipeline.
 *
 * $LicenseInfo:firstyear=2005&license=viewerlgpl$
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
 * @file exposureF.glsl
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
 * @file luminanceF.glsl
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

// Adapted 2026 by Legion Builds. ToneMap follows the functions of the official tone mapping shader (tonemapUtilF.glsl: toneMapACES_Hill with its two matrices and the fitted curve,
// PBRNeutralToneMapping, and toneMap's exposure, mix and clamp, called from postDeferredTonemap.glsl), HdrScale follows the sky's high dynamic range scale set in
// llsettingsvo.cpp (the square root of the gamma times two) and ColourPath follows which sky takes the tone mapper (llsettingssky.cpp: getTonemapMix, canAutoAdjust, and pipeline.cpp:
// tonemap and the choice between the legacy and the tone-mapped gamma pass), written in C#. AutoExposure follows the dynamic exposure (luminanceF.glsl, exposureF.glsl, and the range LLPipeline::generateExposure sets in pipeline.cpp). The rest of this file is our own code.

// Copyright (c) 2026 Legion Builds

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>The tone mapper the official viewer's post pass applies: Khronos neutral or the ACES fit by Hill.</summary>
public enum ToneMapType { PbrNeutral = 0, AcesHill = 1 }

/// <summary>
/// The tone mapping of a high dynamic range sky (the official post pass, tonemapUtilF.glsl): the colour is scaled by the exposure, mapped by the chosen
/// tone mapper, and mixed with the exposed colour as it was, by the mix factor; the result is clamped to 0..1. Linear colour in, linear colour out.
/// </summary>
public static class ToneMap
{
    /// <summary>The official defaults of the settings that steer it: exposure 1, the ACES fit, a mix of 0.7.</summary>
    public const float DefaultExposure = 1f, DefaultMix = 0.7f;
    public const ToneMapType DefaultType = ToneMapType.AcesHill;

    /// <summary>The ACES fit by Hill: sRGB to the working space, the fitted curve, back, clamped.</summary>
    public static Vector3 AcesHill(Vector3 c)
    {
        // sRGB to the working space (the official input matrix), the fitted curve per channel, and back (the output matrix)
        float x = 0.59719f * c.X + 0.35458f * c.Y + 0.04823f * c.Z;
        float y = 0.07600f * c.X + 0.90834f * c.Y + 0.01566f * c.Z;
        float z = 0.02840f * c.X + 0.13383f * c.Y + 0.83777f * c.Z;
        static float Fit(float v) => (v * (v + 0.0245786f) - 0.000090537f) / (v * (0.983729f * v + 0.4329510f) + 0.238081f);
        x = Fit(x); y = Fit(y); z = Fit(z);
        return Clamp01(new Vector3(
            1.60475f * x - 0.53108f * y - 0.07367f * z,
            -0.10208f * x + 1.10813f * y - 0.00605f * z,
            -0.00327f * x - 0.07276f * y + 1.07602f * z));
    }

    private static Vector3 Clamp01(Vector3 v) => Vector3.Clamp(v, Vector3.Zero, Vector3.One);

    /// <summary>The Khronos neutral tone mapper.</summary>
    public static Vector3 PbrNeutral(Vector3 c)
    {
        const float startCompression = 0.8f - 0.04f, desaturation = 0.15f;
        float x = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float offset = x < 0.08f ? x - 6.25f * x * x : 0.04f;
        c -= new Vector3(offset);
        float peak = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        if (peak < startCompression) return c;
        const float d = 1f - startCompression;
        float newPeak = 1f - d * d / (peak + d - startCompression);
        c *= newPeak / peak;
        float g = 1f - 1f / (desaturation * (peak - newPeak) + 1f);
        return Vector3.Lerp(c, new Vector3(newPeak), g);
    }

    /// <summary>The post pass for one colour (the scene's own exposure scale is 1).</summary>
    public static Vector3 Apply(Vector3 color, float exposure, ToneMapType type, float mix)
    {
        var exposed = color * exposure;
        var mapped = type == ToneMapType.AcesHill ? AcesHill(exposed) : PbrNeutral(exposed);
        return Clamp01(Vector3.Lerp(exposed, mapped, mix));
    }

    /// <summary>What the sky's colours are scaled by in a high dynamic range sky: the square root of the sky's gamma, times two.</summary>
    public static float HdrScale(float gamma) => MathF.Sqrt(gamma) * 2f;
}

/// <summary>
/// The scene's automatic exposure (the official viewer's dynamic exposure: luminanceF.glsl takes the lit scene's brightness over the middle of the view, exposureF.glsl turns it
/// into a scale, LLPipeline::generateExposure sets its range from the sky): a dark scene is brightened, a bright one darkened, by at most the sky's high dynamic range scale either way.
/// </summary>
public static class AutoExposure
{
    /// <summary>The official defaults of the settings beside it: the luminance that counts as fully bright, how far the scale has to travel in one speed-target time, and that time.</summary>
    public const float DefaultCoefficient = 0.175f, DefaultSpeedError = 0.1f, DefaultSpeedTarget = 2f;

    /// <summary>The luminance weights of the official shaders.</summary>
    public static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    /// <summary>The scale's range for a sky: 1 to 1 on the classic path, else from one over the sky's high dynamic range scale to that scale (when it is over 1).</summary>
    public static (float Min, float Max) Range(float probeAmbiance, float gamma = 1f)
    {
        if (probeAmbiance > 0f)
        {
            float scale = ToneMap.HdrScale(gamma);
            if (scale > 1f) return (1f / scale, scale);
        }
        return (1f, 1f);
    }

    /// <summary>The scale a steady scene settles at: the mean luminance, held to the coefficient and squared, picks a point from the top of the range (dark) to the bottom (bright).</summary>
    public static float Target(float meanLuminance, float min, float max, float coefficient = DefaultCoefficient)
    {
        float l = Math.Clamp(meanLuminance, 0f, coefficient) / coefficient;
        l *= l;
        return max + (min - max) * l;
    }

    /// <summary>The scale after a frame of <paramref name="dt"/> seconds: it moves towards the target at the official speed.</summary>
    public static float Step(float previous, float target, float dt, float speedError = DefaultSpeedError, float speedTarget = DefaultSpeedTarget)
    {
        float speed = -MathF.Log(speedError) / speedTarget;
        return previous + (target - previous) * (1f - MathF.Exp(-speed * dt));
    }

    /// <summary>The scale for a sky and a scene's mean luminance, settled.</summary>
    public static float Settled(float meanLuminance, float probeAmbiance, float gamma = 1f)
    {
        var (min, max) = Range(probeAmbiance, gamma);
        return Target(meanLuminance, min, max);
    }
}

/// <summary>Which colour path a sky takes: the classic one (a legacy sky, clamped to 0..1) or the tone-mapped one (a sky that sets a reflection probe ambiance).</summary>
public static class ColourPath
{
    /// <summary>True when the frame is drawn through the tone mapper: its reflection probe ambiance is not 0 (or the auto-adjust setting for legacy skies is on).</summary>
    public static bool IsHdr(float probeAmbiance, bool autoAdjustLegacy = false) => probeAmbiance != 0f || autoAdjustLegacy;

    /// <summary>The tone mapper's mix for a sky: 0 on the classic path, else the setting.</summary>
    public static float TonemapMix(float probeAmbiance, float setting = ToneMap.DefaultMix, bool autoAdjustLegacy = false) => IsHdr(probeAmbiance, autoAdjustLegacy) ? setting : 0f;
}
