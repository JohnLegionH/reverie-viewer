// SPDX-License-Identifier: LGPL-2.1-only AND Apache-2.0 AND CC-BY-3.0
/**
 * @file llreflectionmapmanager.cpp
 * @brief LLReflectionMapManager class implementation
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
 * @file irradianceGenF.glsl
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
 * @file class3/deferred/reflectionProbeF.glsl
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

// Adapted 2026 by Legion Builds. The sky probe follows the default reflection probe of the 26.4 viewer, as that viewer's source defines it: the capture and the
// irradiance map in llreflectionmapmanager.cpp (LLReflectionMapManager::updateProbeFace), the irradiance generation in irradianceGenF.glsl (the Hammersley points
// of Holger Dammertz's notes, cosine-weighted hemisphere sampling, and the mip-filtered sampling of GPU Gems 3, chapter 20.4, as the Khronos glTF sample viewer's
// ibl_filtering.frag uses them), the parameters of the irradiance pass in llsettingsvo.cpp (draw_color, applySpecial), the probe's sampling for ambient in
// reflectionProbeF.glsl (tapIrradianceMap, sampleProbeAmbient, sphereIntersect), and the ambiance scale in LLReflectionMapManager::updateUniforms, written in C#.
// The rest of this file is our own code.
// The irradiance generation in irradianceGenF.glsl is itself derived, as that file says, from the Khronos glTF Sample Viewer's ibl_filtering.frag (Apache License 2.0; the licence text
// is in ThirdPartyLicenses) and uses the Hammersley points of Holger Dammertz's notes (CC-BY 3.0).
// The irradiance generation in irradianceGenF.glsl says, in its own comment, word for word:
//     Code below is derived from the Khronos GLTF Sample viewer:
//     https://github.com/KhronosGroup/glTF-Sample-Viewer/blob/master/source/shaders/ibl_filtering.frag
// That Khronos source is under the Apache License 2.0; the licence text is in ThirdPartyLicenses/LICENSE-glTF-Sample-Viewer-Apache-2.0.txt. The licence file in the Khronos repository is the
// unfilled template and names no copyright holder, so no holder line is given here. Adapted 2026 by Legion Builds, and changed (Apache License 2.0, section 4(b)): this file is that filtering
// rewritten from GLSL in C#, with the cosine-weighted sampling, the sample count and the mip level fixed to the official viewer's constants, and with the cube read done in code.
// The same shader credits its Hammersley points as "CC BY 3.0 (Holger Dammertz)", http://holger.dammertz.org/stuff/notes_HammersleyOnHemisphere.html, "with adapted interface";
// that credit stays with RadicalInverse below, which is that code.

// Copyright (c) 2026 Legion Builds

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>
/// The sky (default) reflection probe of the official viewer, for a sky frame: what the probe captures (the sky with no ambient and a grey blue horizon and blue
/// density, as the irradiance pass draws it, evaluated at the 128 x 128 texel centres of each cube face), the box mip chain, the irradiance map (16 x 16 per face)
/// of 32 cosine-weighted samples read at the 2 x 2 mip, and the lookup that gives a surface's ambient from it. The cube is in the viewer's y-up world frame with
/// the usual cube face layout (face order +X, -X, +Y, -Y, +Z, -Z; row 0 of a face is the top). What the official capture also holds and this one does not:
/// terrain, water, clouds, the sun and moon discs and stars.
/// </summary>
public static class SkyProbe
{
    /// <summary>The probe resolution (RenderReflectionProbeResolution, 128).</summary>
    public const int Resolution = 128;
    /// <summary>The irradiance map's size per face (LL_IRRADIANCE_MAP_RESOLUTION).</summary>
    public const int IrradianceSize = 16;
    /// <summary>The number of mip levels in the capture's chain (128 down to 2).</summary>
    public const int MipCount = 7;
    /// <summary>The coarsest mip level (mMaxProbeLOD = log2(resolution) - 1).</summary>
    public const int MaxLod = 6;
    /// <summary>Samples per irradiance texel (u_sampleCount).</summary>
    public const int SampleCount = 32;
    /// <summary>The width the sample's mip level is worked out for (u_width, left at its default by the official viewer) and the bias added to it (u_lodBias).</summary>
    public const int SourceWidth = 64;
    public const float LodBias = 2f;
    /// <summary>The radius of the sphere a probe without a box is treated as, for the direction of the lookup (tapIrradianceMap: 4096 m).</summary>
    public const float ParallaxRadius = 4096f;
    /// <summary>The probe sits this far above the camera (touch_default_probe).</summary>
    public const float OriginHeight = 64f;

    // ---------------------------------------------------------------- cube faces

    /// <summary>The (unnormalised) direction of the point (<paramref name="s"/>, <paramref name="t"/>), both 0..1, on a face.</summary>
    public static Vector3 FaceDirection(int face, float s, float t)
    {
        float sc = 2 * s - 1, tc = 2 * t - 1;
        return face switch
        {
            0 => new(1, -tc, -sc),
            1 => new(-1, -tc, sc),
            2 => new(sc, 1, tc),
            3 => new(sc, -1, -tc),
            4 => new(sc, -tc, 1),
            _ => new(-sc, -tc, -1),
        };
    }

    /// <summary>The face a direction lands on and the point on it (0..1).</summary>
    public static (int Face, float S, float T) ToFace(Vector3 d)
    {
        float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
        int face; float sc, tc, ma;
        if (ax >= ay && ax >= az) { ma = ax; if (d.X > 0) { face = 0; sc = -d.Z; tc = -d.Y; } else { face = 1; sc = d.Z; tc = -d.Y; } }
        else if (ay >= az) { ma = ay; if (d.Y > 0) { face = 2; sc = d.X; tc = d.Z; } else { face = 3; sc = d.X; tc = -d.Z; } }
        else { ma = az; if (d.Z > 0) { face = 4; sc = d.X; tc = -d.Y; } else { face = 5; sc = -d.X; tc = -d.Y; } }
        return (face, (sc / ma + 1) / 2, (tc / ma + 1) / 2);
    }

    /// <summary>The viewer's y-up frame to the sky's z-up frame (the engine's world is x, z, -y of the sky's).</summary>
    public static Vector3 ToSky(Vector3 d) => new(d.X, -d.Z, d.Y);

    // ---------------------------------------------------------------- the capture

    /// <summary>The sky frame as the irradiance pass draws it: no ambient, and the blue horizon and blue density as grey of their lightness (RenderDesaturateIrradiance).</summary>
    public static SkyFrame IrradiancePassFrame(SkyFrame f)
    {
        static float Lightness(Vector3 c) => (MathF.Max(c.X, MathF.Max(c.Y, c.Z)) + MathF.Min(c.X, MathF.Min(c.Y, c.Z))) / 2f;
        return f with { Ambient = Vector3.Zero, BlueHorizon = new Vector3(Lightness(f.BlueHorizon)), BlueDensity = new Vector3(Lightness(f.BlueDensity)) };
    }

    /// <summary>The sky's colour (linear, times the sky's scale) the capture sees in a direction; below the dome's rim it takes the rim's colour (the official dome ends there, and terrain or water is seen below it).</summary>
    public static Vector3 SkyColour(SkyFrame passFrame, Vector3 direction, float gamma = 1f)
    {
        var s = Vector3.Normalize(ToSky(direction));
        float elevation = MathF.Max(MathF.Asin(Math.Clamp(s.Z, -1f, 1f)) * (180f / MathF.PI), SkyLighting.DomeFloorDegrees);
        bool sunUp = passFrame.SunDirection.Z >= 0f, moonUp = passFrame.MoonDirection.Z >= 0f;
        var light = sunUp ? passFrame.SunDirection : moonUp ? passFrame.MoonDirection : -Vector3.UnitZ;
        double d = Math.Atan2(s.Y, s.X) - Math.Atan2(light.Y, light.X);
        d = Math.Abs(Math.IEEERemainder(d, 2 * Math.PI));
        return SkyLighting.DomeColourHdr(passFrame, elevation, (float)(d * 180 / Math.PI), gamma);
    }

    /// <summary>
    /// What the capture sees of the world along a direction (the viewer's y-up frame, from the probe's centre): the colour of terrain or water there (linear, lit by the sun alone: the
    /// irradiance pass has no ambient), or null where there is none and the sky shows.
    /// </summary>
    public delegate Vector3? WorldSample(Vector3 direction);

    /// <summary>The capture: each face at <see cref="Resolution"/>, row-major, evaluated at the texel centres; <paramref name="world"/>, when given, is drawn over the sky.</summary>
    public static Vector3[][] Capture(SkyFrame f, float gamma = 1f, WorldSample? world = null)
    {
        var pass = IrradiancePassFrame(f);
        var faces = new Vector3[6][];
        Parallel.For(0, 6, face =>
        {
            var px = new Vector3[Resolution * Resolution];
            for (int y = 0; y < Resolution; y++)
                for (int x = 0; x < Resolution; x++)
                {
                    var d = FaceDirection(face, (x + 0.5f) / Resolution, (y + 0.5f) / Resolution);
                    px[y * Resolution + x] = world?.Invoke(d) ?? SkyColour(pass, d, gamma);
                }
            faces[face] = px;
        });
        return faces;
    }

    /// <summary>
    /// A flat ground of one radiance <paramref name="height"/> metres below the probe's centre, drawn as far as <paramref name="farClip"/> metres: the far plane is measured along the axis of the
    /// cube face a direction lands on (a camera's depth), so where a direction meets the ground within that depth it shows the ground's colour; beyond it the sky shows (the official capture draws
    /// only what its camera's far plane reaches).
    /// </summary>
    public static WorldSample FlatGround(Vector3 radiance, float height, float farClip) => d =>
    {
        var n = Vector3.Normalize(d);
        if (n.Y >= -1e-6f) return null;
        float depth = height / -n.Y * MathF.Max(MathF.Abs(n.X), MathF.Max(MathF.Abs(n.Y), MathF.Abs(n.Z)));
        return depth <= farClip ? radiance : null;
    };

    /// <summary>The radiance of ground with the given albedo (linear) lit by the sun alone, as the irradiance pass lights it: the sun's colour times the angle of an upward normal to the sun.</summary>
    public static Vector3 SunLitRadiance(SkyFrame f, Vector3 albedo)
    {
        var sun = SkyLighting.Eep(IrradiancePassFrame(f)).Sun;
        float up = MathF.Max(f.SunDirection.Z >= 0f ? f.SunDirection.Z : f.MoonDirection.Z, 0f);
        return albedo * sun * up;
    }

    /// <summary>The mip chain: each level the 2 x 2 box average of the one before (reflectionmipF.glsl is one bilinear fetch at a texel centre of the smaller target).</summary>
    public static Vector3[][][] MipChain(Vector3[][] top)
    {
        var chain = new Vector3[MipCount][][];
        chain[0] = top;
        int size = Resolution;
        for (int level = 1; level < MipCount; level++)
        {
            int h = size / 2;
            var prev = chain[level - 1];
            var next = new Vector3[6][];
            for (int face = 0; face < 6; face++)
            {
                var o = new Vector3[h * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < h; x++)
                        o[y * h + x] = (prev[face][2 * y * size + 2 * x] + prev[face][2 * y * size + 2 * x + 1] + prev[face][(2 * y + 1) * size + 2 * x] + prev[face][(2 * y + 1) * size + 2 * x + 1]) * 0.25f;
                next[face] = o;
            }
            chain[level] = next;
            size = h;
        }
        return chain;
    }

    // ---------------------------------------------------------------- sampling a cube

    /// <summary>Seamless bilinear sampling of one level of a cube (size per face): at an edge the taps outside the face are the neighbouring face's texels.</summary>
    public static Vector3 Sample(Vector3[][] level, int size, Vector3 direction)
    {
        var (face, s, t) = ToFace(direction);
        float u = s * size - 0.5f, v = t * size - 0.5f;
        int u0 = (int)MathF.Floor(u), v0 = (int)MathF.Floor(v);
        float fu = u - u0, fv = v - v0;
        var acc = Vector3.Zero;
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                int tu = u0 + i, tv = v0 + j, fc = face, px = tu, py = tv;
                float w = (i == 0 ? 1 - fu : fu) * (j == 0 ? 1 - fv : fv);
                if (tu < 0 || tu >= size || tv < 0 || tv >= size)
                {
                    var (nf, ns, nt) = ToFace(FaceDirection(face, (tu + 0.5f) / size, (tv + 0.5f) / size));
                    fc = nf; px = Math.Clamp((int)MathF.Floor(ns * size), 0, size - 1); py = Math.Clamp((int)MathF.Floor(nt * size), 0, size - 1);
                }
                acc += level[fc][py * size + px] * w;
            }
        return acc;
    }

    // ---------------------------------------------------------------- the irradiance map

    // Hammersley Points on the Hemisphere
    // CC BY 3.0 (Holger Dammertz)
    // http://holger.dammertz.org/stuff/notes_HammersleyOnHemisphere.html
    // with adapted interface
    private static float RadicalInverse(uint bits)
    {
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        return bits * 2.3283064365386963e-10f;
    }

    /// <summary>The mip level a sample with the given density reads (computeLod, plus the bias, clamped to 0 to <see cref="MaxLod"/>): always <see cref="MaxLod"/> for the official constants.</summary>
    public static float SampleLod(float pdf) => Math.Clamp(0.5f * MathF.Log2(6f * SourceWidth * SourceWidth / (SampleCount * pdf)) + LodBias, 0f, MaxLod);

    /// <summary>The irradiance map of the capture's mip chain: 16 x 16 per face, each texel the mean of 32 cosine-weighted samples (filterColor).</summary>
    public static Vector3[][] Irradiance(Vector3[][][] chain)
    {
        int size = Resolution >> MaxLod;   // 2
        var irr = new Vector3[6][];
        for (int face = 0; face < 6; face++)
        {
            var o = new Vector3[IrradianceSize * IrradianceSize];
            for (int y = 0; y < IrradianceSize; y++)
                for (int x = 0; x < IrradianceSize; x++)
                {
                    var n = Vector3.Normalize(FaceDirection(face, (x + 0.5f) / IrradianceSize, (y + 0.5f) / IrradianceSize));
                    var tangent = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, n));
                    var bitangent = Vector3.Cross(n, tangent);
                    var acc = Vector3.Zero;
                    for (int i = 0; i < SampleCount; i++)
                    {
                        float xi1 = i / (float)SampleCount, xi2 = RadicalInverse((uint)i);
                        float cosT = MathF.Sqrt(1f - xi2), sinT = MathF.Sqrt(xi2), phi = 2f * MathF.PI * xi1;
                        var local = Vector3.Normalize(new Vector3(sinT * MathF.Cos(phi), sinT * MathF.Sin(phi), cosT));
                        var h = tangent * local.X + bitangent * local.Y + n * local.Z;
                        float lod = SampleLod(cosT / MathF.PI);
                        // the official lookup reads level 'lod' trilinearly; with these constants lod is exactly the coarsest level
                        acc += lod >= MaxLod ? Sample(chain[MaxLod], size, h) : SampleLevels(chain, lod, h);
                    }
                    o[y * IrradianceSize + x] = acc / SampleCount;
                }
            irr[face] = o;
        }
        return irr;
    }

    private static Vector3 SampleLevels(Vector3[][][] chain, float lod, Vector3 h)
    {
        int l0 = (int)MathF.Floor(lod), l1 = Math.Min(l0 + 1, MaxLod);
        float f = lod - l0;
        var a = Sample(chain[l0], Resolution >> l0, h);
        return f <= 0f ? a : a + (Sample(chain[l1], Resolution >> l1, h) - a) * f;
    }

    /// <summary>The sky probe's irradiance map for a sky frame.</summary>
    public static Vector3[][] Irradiance(SkyFrame f, float gamma = 1f, WorldSample? world = null) => Irradiance(MipChain(Capture(f, gamma, world)));

    /// <summary>The map sampled in a direction (textureLod at level 0: bilinear, seamless).</summary>
    public static Vector3 SampleIrradiance(Vector3[][] irradiance, Vector3 direction) => Sample(irradiance, IrradianceSize, direction);

    // ---------------------------------------------------------------- the flat picture the shaders read

    /// <summary>The atlas: six faces side by side, each <see cref="IrradianceSize"/> plus a one-texel border.</summary>
    public const int AtlasWidth = 6 * (IrradianceSize + 2);
    public const int AtlasHeight = IrradianceSize + 2;

    /// <summary>
    /// The irradiance map as one picture (RGB floats, row-major, <see cref="AtlasWidth"/> x <see cref="AtlasHeight"/>): each face with a one-texel border that holds the neighbouring face's texel in that
    /// direction (the same neighbour the seamless cube read takes), so a bilinear read inside a face gives the cube read at its edges.
    /// </summary>
    public static float[] Atlas(Vector3[][] irradiance)
    {
        int s = IrradianceSize, cell = s + 2;
        var data = new float[AtlasWidth * AtlasHeight * 3];
        for (int face = 0; face < 6; face++)
            for (int y = -1; y <= s; y++)
                for (int x = -1; x <= s; x++)
                {
                    int fc = face, px = x, py = y;
                    if (x < 0 || x >= s || y < 0 || y >= s)
                    {
                        var (nf, ns, nt) = ToFace(FaceDirection(face, (x + 0.5f) / s, (y + 0.5f) / s));
                        fc = nf; px = Math.Clamp((int)MathF.Floor(ns * s), 0, s - 1); py = Math.Clamp((int)MathF.Floor(nt * s), 0, s - 1);
                    }
                    var c = irradiance[fc][py * s + px];
                    int k = ((y + 1) * AtlasWidth + face * cell + x + 1) * 3;
                    data[k] = c.X; data[k + 1] = c.Y; data[k + 2] = c.Z;
                }
        return data;
    }

    // ---------------------------------------------------------------- the ambient

    /// <summary>
    /// The direction a surface at <paramref name="position"/> with normal <paramref name="normal"/> reads the map in (tapIrradianceMap with a probe that has no box:
    /// the ray from the surface along the normal meets a sphere of <see cref="ParallaxRadius"/> around the probe's centre, and the direction is from the centre to that point).
    /// Positions are in the viewer's world frame; the probe's centre is <paramref name="cameraPosition"/> plus <see cref="OriginHeight"/> up.
    /// </summary>
    public static Vector3 LookupDirection(Vector3 position, Vector3 normal, Vector3 cameraPosition)
    {
        var centre = cameraPosition + new Vector3(0f, OriginHeight, 0f);
        var l = centre - position;
        float tca = Vector3.Dot(l, normal);
        float d2 = Vector3.Dot(l, l) - tca * tca;
        float thc = MathF.Sqrt(MathF.Max(ParallaxRadius * ParallaxRadius - d2, 0f));
        var hit = position + normal * (tca + thc);
        return hit - centre;
    }

    /// <summary>
    /// The ambient a surface takes in the non-classic path with only the sky probe (sampleProbeAmbient with the probe's weight cancelling):
    /// <c>mix(amblit, irradiance * ambiance, min(ambiance, 1))</c>, where <paramref name="ambiance"/> is the sky's reflection probe ambiance.
    /// </summary>
    public static Vector3 Ambient(Vector3 amblit, Vector3 irradiance, float ambiance)
    {
        float k = MathF.Min(ambiance, 1f);
        var probe = irradiance * ambiance;
        return amblit + (probe - amblit) * k;
    }
}
