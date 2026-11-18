// SPDX-License-Identifier: LGPL-2.1-only AND MIT
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

/*
 * Baking Lab, BakingLab/ACES.hlsl (https://github.com/TheRealMJP/BakingLab), whose fitted curve and two matrices the Hill fit below uses:
 *
 *  Baking Lab
 *  by MJP and David Neubelt
 *  http://mynameismjp.wordpress.com/
 *
 *  All code licensed under the MIT license
 *
 * The code in this file was originally written by Stephen Hill (@self_shadow), who deserves all
 * credit for coming up with this fit and implementing it. Buy him a beer next time you see him. :)
 *
 * Copyright (c) 2016 MJP
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

// Adapted 2026 by Legion Builds. ToneMap.Apply follows the official post pass (toneMap's exposure, mix and clamp, called from postDeferredTonemap.glsl), HdrScale follows the sky's high dynamic range scale set in
// llsettingsvo.cpp (the square root of the gamma times two) and ColourPath follows which sky takes the tone mapper (llsettingssky.cpp: getTonemapMix, canAutoAdjust, and pipeline.cpp:
// tonemap and the choice between the legacy and the tone-mapped gamma pass), written in C#. AutoExposure follows the dynamic exposure (luminanceF.glsl, exposureF.glsl, and the range LLPipeline::generateExposure sets in pipeline.cpp). AcesHill uses Stephen Hill's fit as Baking Lab publishes it (MIT, notice above), written in C#.
// PbrNeutral is written from the equations of the Khronos PBR Neutral tone mapper's specification text (github.com/KhronosGroup/ToneMapping, PBR_Neutral), not from
// any implementation of it. The rest of this file is our own code.

// Copyright (c) 2026 Legion Builds

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>The tone mapper the official viewer's post pass applies: the Khronos PBR Neutral mapper or the ACES fit by Hill.</summary>
public enum ToneMapType { PbrNeutral = 0, AcesHill = 1 }

/// <summary>
/// The tone mapping of a high dynamic range sky (the official post pass, tonemapUtilF.glsl's toneMap; the two curves are our own, see the file header): the colour is scaled by the exposure, mapped by the chosen
/// tone mapper, and mixed with the exposed colour as it was, by the mix factor; the result is clamped to 0..1. Linear colour in, linear colour out.
/// </summary>
public static class ToneMap
{
    /// <summary>The official defaults of the settings that steer it: exposure 1, the ACES fit, a mix of 0.7.</summary>
    public const float DefaultExposure = 1f, DefaultMix = 0.7f;
    public const ToneMapType DefaultType = ToneMapType.AcesHill;

    // Hill's fit (Baking Lab, notice above): linear sRGB into the space the fit works in (the ACES input transform with the RRT's saturation folded in), and back out
    // (the ODT's saturation and the transform back to linear sRGB). Rows of each matrix.
    private static readonly Vector3 HillInX = new(0.59719f, 0.35458f, 0.04823f), HillInY = new(0.07600f, 0.90834f, 0.01566f), HillInZ = new(0.02840f, 0.13383f, 0.83777f);
    private static readonly Vector3 HillOutX = new(1.60475f, -0.53108f, -0.07367f), HillOutY = new(-0.10208f, 1.10813f, -0.00605f), HillOutZ = new(-0.00327f, -0.07276f, 1.07602f);

    private static float Row(Vector3 row, Vector3 v) => row.X * v.X + row.Y * v.Y + row.Z * v.Z;

    // The fitted RRT and ODT curve: a ratio of two quadratics in the value.
    private static float HillCurve(float v)
    {
        float numerator = v * (v + 0.0245786f) - 0.000090537f;
        float denominator = v * (0.983729f * v + 0.4329510f) + 0.238081f;
        return numerator / denominator;
    }

    /// <summary>The ACES fit by Hill: sRGB to the working space, the fitted curve, back, clamped.</summary>
    public static Vector3 AcesHill(Vector3 c)
    {
        var fitted = new Vector3(HillCurve(Row(HillInX, c)), HillCurve(Row(HillInY, c)), HillCurve(Row(HillInZ, c)));
        return Clamp01(new Vector3(Row(HillOutX, fitted), Row(HillOutY, fitted), Row(HillOutZ, fitted)));
    }

    private static Vector3 Clamp01(Vector3 v) => Vector3.Clamp(v, Vector3.Zero, Vector3.One);

    // The PBR Neutral parameters as the specification names them: F90 (the reflectance of a common dielectric at normal incidence), Ks (where highlight compression
    // starts) and Kd (how fast compressed highlights lose their saturation).
    private const float F90 = 0.04f, Ks = 0.8f - F90, Kd = 0.15f;

    /// <summary>
    /// The Khronos PBR Neutral tone mapper, from its specification's equations: an offset f taken off every channel (x - x^2 / (4 F90) for a smallest channel x up to
    /// 2 F90, else F90); when the largest channel p after the offset is above Ks, the colour is scaled so that p becomes p_n = 1 - (1 - Ks)^2 / (p + 1 - 2 Ks), and blended
    /// toward the grey p_n with weight 1 - g, g = 1 / (Kd (p - p_n) + 1).
    /// </summary>
    public static Vector3 PbrNeutral(Vector3 c)
    {
        float smallest = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float f = smallest <= 2f * F90 ? smallest - smallest * smallest / (4f * F90) : F90;
        var offset = c - new Vector3(f);
        float p = MathF.Max(offset.X, MathF.Max(offset.Y, offset.Z));
        if (p <= Ks) return offset;
        float pn = 1f - (1f - Ks) * (1f - Ks) / (p + 1f - 2f * Ks);
        float g = 1f / (Kd * (p - pn) + 1f);
        return offset * (pn / p * g) + new Vector3(pn * (1f - g));
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
