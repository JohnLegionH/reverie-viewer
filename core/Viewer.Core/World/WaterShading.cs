// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file waterF.glsl
 *
 * $LicenseInfo:firstyear=2022&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2022, Linden Research, Inc.
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
 * @file class1/deferred/deferredUtil.glsl
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

// Adapted 2026 by Legion Builds. WaterShading.Fresnel and WaterShading.Surface follow the main() of the water fragment shader (waterF.glsl: the
// fresnel weights from the scale and offset, the mix of what is seen through the water with the reflected light) and WaterShading.Fog follows
// the water fog function of waterFogF.glsl (the thickness, the fog light and what survives), written in C# for one ray at a time over a flat
// surface. The reflected light is the sky's colour times the fresnel factor and nothing else, as in the shader. FresnelFactors, Refract and Glint
// follow calculateFresnelFactors and main of waterF.glsl (three wave layers, the ripple offset of the refraction, the sun's punctual light) and
// pbrPunctual with its specular, occlusion and distribution terms in deferredUtil.glsl.
// Copyright (c) 2026 Legion Builds

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>
/// The colour of a point on the water surface seen from above it, worked out the way the official viewer's water does for a sky
/// without a reflection-probe ambiance: what is seen through the water (the bottom, dimmed and tinted by the water's fog along the ray)
/// blended with the sky's reflection by a fresnel weight made from the frame's fresnel scale and offset. Behaviour reference: the
/// official viewer's water fragment shader and its water fog function (no code copied); the formulas are written out in our own words in
/// the comments below. All colours are linear. A flat surface is assumed (the wave tilt is small and fades with distance).
/// </summary>
public static class WaterShading
{
    /// <summary>The limit the official water fog puts under the light direction's height (the fog slant factor is one over the larger of the two).</summary>
    public const float FogLightClamp = 0.3f;

    /// <summary>
    /// The fog's slant factor for a light of height <paramref name="lightZ"/> (the sun's, or the moon's when the sun is down).
    /// </summary>
    public static float FogSlant(float lightZ) => 1f / MathF.Max(lightZ, FogLightClamp);

    /// <summary>
    /// The fresnel weights for a surface seen at <paramref name="cosIncidence"/> (1 looking straight down, 0 along the surface):
    /// the weight of the reflection against what is seen through the water, and the factor on the reflected light. Each is
    /// built from <c>max(0, offset - scale * cos)</c>; the weight is three times its square, up to 1, the factor the value itself.
    /// </summary>
    public static (float Weight, float RadianceScale) Fresnel(float cosIncidence, float fresnelScale, float fresnelOffset)
    {
        float d = MathF.Max(0f, fresnelOffset - fresnelScale * cosIncidence);
        return (MathF.Min(1f, 3f * d * d), d);
    }

    /// <summary>
    /// The water's fog along a ray of <paramref name="thickness"/> metres through the water: the fog's own light (to add) and the share
    /// of what is behind that survives (to multiply). <paramref name="fogColour"/> is the frame's fog colour as stored (display-encoded),
    /// <paramref name="density"/> the frame's fog density, <paramref name="slant"/> from <see cref="FogSlant"/>, <paramref name="cosIncidence"/>
    /// the ray's cosine to the vertical. Eye above the water.
    /// </summary>
    public static (Vector3 Light, float Survives) Fog(Vector3 fogColour, float density, float slant, float cosIncidence, float thickness)
    {
        double l = Math.Max(thickness, 0.1), kd = density;
        double t1 = -kd, t2 = kd + slant * cosIncidence;
        double t3 = Math.Pow(0.98, t2 * l) - 1.0;
        double lit = Math.Pow(Math.Min(t1 / t2 * t3, 1.0), 1.0 / 1.7);
        double survives = Math.Pow(0.98, l * kd);
        return (new Vector3(Decode(fogColour.X), Decode(fogColour.Y), Decode(fogColour.Z)) * (float)lit, (float)survives);
    }

    /// <summary>The colour of the water surface: what is seen through it blended with the reflected sky.</summary>
    /// <param name="behind">The linear colour of what lies under the water along the ray, before the water's fog.</param>
    /// <param name="reflected">The linear colour of the sky in the mirrored direction, as the sky is shown (the official shader reflects the rendered sky itself).</param>
    public static Vector3 Surface(WaterLook water, float slant, float cosIncidence, float thickness, Vector3 behind, Vector3 reflected)
    {
        var (light, survives) = Fog(water.FogColor, water.FogDensity, slant, cosIncidence, thickness);
        var through = behind * survives + light;
        var (weight, scale) = Fresnel(cosIncidence, water.FresnelScale, water.FresnelOffset);
        return Vector3.Lerp(through, reflected * scale, weight);   // waterF.glsl: radiance *= df2.y, then mix(fb, radiance, min(1, df2.x)); no other factor
    }

    /// <summary>
    /// The fresnel factors of three layers of waves (the big wave, a mix of the two small ones, the second small one): each layer's
    /// factor is <c>max(0, dot(view, wave) * scale + offset)</c> squared. <paramref name="view"/> points from the eye to the water, the waves
    /// are unit normals. <c>Weight</c> is the sum of the three (how much of the reflection shows), <c>RadianceScale</c> the same measure
    /// taken on the blended normal <paramref name="waveFinal"/> (the factor on the reflected light).
    /// </summary>
    public static (Vector3 Layers, float Weight, float RadianceScale) FresnelFactors(Vector3 view, Vector3 wave1, Vector3 wave2, Vector3 wave3, Vector3 waveFinal, float scale, float offset)
    {
        static float Layer(float dot, float scale, float offset) { float d = MathF.Max(0f, dot * scale + offset); return d * d; }
        var layers = new Vector3(Layer(Vector3.Dot(view, wave1), scale, offset), Layer(Vector3.Dot(view, (wave2 + wave3) * 0.5f), scale, offset), Layer(Vector3.Dot(view, wave3), scale, offset));
        return (layers, MathF.Max(0f, layers.X + layers.Y + layers.Z), MathF.Max(0f, Vector3.Dot(view, waveFinal) * scale + offset));
    }

    /// <summary>
    /// Where the refracted view reads the scene behind the water: the screen position (0..1) nudged by the waves' sideways tilt times the
    /// refraction scale, less and less with distance (over the square root of it, at least 1), and kept inside the screen.
    /// </summary>
    public static Vector2 Refract(Vector2 screen, Vector2 waveXY, float refractionScale, float distance)
    {
        float k = refractionScale / MathF.Max(MathF.Sqrt(distance), 1f) * 2f;
        return new Vector2(Math.Clamp(screen.X + waveXY.X * k, 0f, 0.999f), Math.Clamp(screen.Y + waveXY.Y * k, 0f, 0.999f));
    }

    /// <summary>
    /// The sun's glint on the water for a white light: the light's cosine to the normal times the specular term (fresnel, occlusion and
    /// microfacet distribution of the official punctual light, metal with full reflectance) at the given roughness (the water's blur
    /// multiplier times two, at least 8/255), not above 10. <paramref name="toCamera"/> and <paramref name="toLight"/> point away from the surface.
    /// </summary>
    public static float Glint(Vector3 normal, Vector3 toCamera, Vector3 toLight, float roughness)
    {
        double rough = Math.Max(roughness, 8.0 / 255.0), a = rough * rough;
        var n = Vector3.Normalize(normal); var v = Vector3.Normalize(toCamera); var l = Vector3.Normalize(toLight);
        var h = Vector3.Normalize(l + v);
        double nl = Math.Clamp(Vector3.Dot(n, l), 0.001, 1.0), nv = Math.Clamp(Math.Abs(Vector3.Dot(n, v)), 0.001, 1.0), nh = Math.Clamp(Vector3.Dot(n, h), 0.0, 1.0);
        double g = 2 * nl / (nl + Math.Sqrt(a * a + (1 - a * a) * nl * nl)) * (2 * nv / (nv + Math.Sqrt(a * a + (1 - a * a) * nv * nv)));
        double rs = a * a, f = (nh * rs - nh) * nh + 1.0, d = rs / (Math.PI * f * f);
        double spec = 1.0 * g * d / (4.0 * nl * nv);   // fresnel is 1 at full reflectance
        return (float)Math.Clamp(nl * spec, 0.0, 10.0);
    }

    private static float Decode(float v) => v < 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
}
