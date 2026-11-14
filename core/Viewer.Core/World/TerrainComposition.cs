// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file class1\deferred\terrainF.glsl
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
 * @file llsurfacepatch.cpp
 * @brief LLSurfacePatch class implementation
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

// Adapted 2026 by Legion Builds. LayerWeights follows the mix chain of the official terrain fragment shader (terrainF.glsl) and Jitter follows the second texture
// coordinate of a land vertex in llsurfacepatch.cpp (the noise scale 4.9215 * 7 times 0.2222, the factor 0.75 and the offset 0.5, clamped); our own gradient noise stands in for the
// official noise function. The rest of this file is our own code.

// Copyright (c) 2026 Legion Builds

using System.Collections.Immutable;

namespace Viewer.Core.World;

/// <summary>
/// The region's legacy terrain texturing settings from the region handshake. Corner values are named by
/// their (x, y) corner: index 0 = "00" (south-west), 1 = "01" (north-west, x 0 y 1), 2 = "10" (south-east), 3 = "11"
/// (north-east), in the handshake's field order TerrainStartHeight00, 01, 10, 11 (Docs/TERRAIN-COMPOSITION-SPEC.md 1).
/// </summary>
public sealed record RegionTerrain(ImmutableArray<Guid> Details, ImmutableArray<float> StartHeights, ImmutableArray<float> HeightRanges)
{
    public const int Layers = 4;

    public bool Equals(RegionTerrain? o) => o is not null && Details.SequenceEqual(o.Details) && StartHeights.SequenceEqual(o.StartHeights) && HeightRanges.SequenceEqual(o.HeightRanges);
    public override int GetHashCode() => HashCode.Combine(Details.Length > 0 ? Details[0] : Guid.Empty, StartHeights.Length > 0 ? StartHeights[0] : 0f);
}

/// <summary>
/// SL's legacy terrain composition, written from the behaviour spec Docs/TERRAIN-COMPOSITION-SPEC.md
/// (no viewer code used). A composition value c in [0, 3] per ground point picks and blends the
/// four detail textures: c = clamp((height + noise - startHeight) * 4 / heightRange, 0, 3), start height and range
/// interpolated from the region's four corners, the noise our own gradient noise with the official scales and strengths (zero mean, 2D).
/// </summary>
public static class TerrainComposition
{
    /// <summary>Metres per repeat of a detail texture (the official default RenderTerrainScale).</summary>
    public const float DetailRepeatMetres = 12f;
    /// <summary>The detail pattern's offset, as a fraction of one repeat, for a scene whose origin is the south-west corner of grid cell
    /// (<paramref name="gridX"/>, <paramref name="gridY"/>) (cells are 256 m): the corner's global metres modulo the repeat, over the repeat.
    /// The shader adds it to (scene metres / repeat), so a point has the same pattern coordinate whichever region draws it.</summary>
    public static (float X, float Y) DetailOffset(int gridX, int gridY) =>
        ((float)(gridX * 256.0 % DetailRepeatMetres / DetailRepeatMetres), (float)(gridY * 256.0 % DetailRepeatMetres / DetailRepeatMetres));

    /// <summary>Horizontal and vertical scales of the noise lattice (spec 3).</summary>
    public const float NoiseXyScale = 4.9215f, NoiseZScale = 4f;

    /// <summary>Corner value at fraction (fx, fy) of the region (0..1 each), from the four "xy"-named corners.</summary>
    public static float Corners(ImmutableArray<float> v, float fx, float fy) =>
        (1 - fx) * (1 - fy) * v[0] + (1 - fx) * fy * v[1] + fx * (1 - fy) * v[2] + fx * fy * v[3];

    /// <summary>
    /// The composition value at a ground point: region-local (x, y) metres, its height, the region size and its global
    /// origin (for the noise, so it is continuous across regions). <paramref name="noise"/> false leaves the noise out (tests).
    /// </summary>
    public static float Value(RegionTerrain t, float x, float y, float height, int sizeX, int sizeY, double originX = 0, double originY = 0, bool noise = true)
    {
        float fx = Math.Clamp(x / sizeX, 0, 1), fy = Math.Clamp(y / sizeY, 0, 1);
        float start = Corners(t.StartHeights, fx, fy), range = Corners(t.HeightRanges, fx, fy);
        float twiddle = noise ? Noise(originX + x, originY + y, height) : 0f;
        if (range <= 0.0001f) range = 0.0001f;
        return Math.Clamp((height + twiddle - start) * RegionTerrain.Layers / range, 0f, 3f);
    }

    /// <summary>The four layer weights for a composition value and its jitter, as the official terrain shader blends them (see <see cref="TerrainRamp"/>).</summary>
    public static (float W0, float W1, float W2, float W3) LayerWeights(float c, float jitter)
    {
        // the shader's mix( mix(layer 3, layer 2, a2), mix(layer 1, layer 0, a1), aFinal ), a1 = ramp(c), aFinal = ramp(c - 1), a2 = ramp(c - 2)
        float a1 = TerrainRamp.Sample(c, jitter), aF = TerrainRamp.Sample(c - 1f, jitter), a2 = TerrainRamp.Sample(c - 2f, jitter);
        return (aF * a1, aF * (1f - a1), (1f - aF) * a2, (1f - aF) * (1f - a2));
    }

    /// <summary>Metres to lattice units of the jitter noise: 1 / (4.9215 * 7) of a metre, times 0.2222.</summary>
    public const double JitterLatticePerMetre = 1.0 / (NoiseXyScale * 7.0) * 0.2222222222;

    /// <summary>The jitter of a ground vertex at a global position in metres, 0..1: the second coordinate of the ramp. A slow noise
    /// (a lattice about 155 m across) of the global position, so it is continuous across region borders: 0.5 plus 0.75 times a signed noise, clamped.</summary>
    public static float Jitter(double globalX, double globalY) =>
        (float)Math.Clamp(GradientNoise(globalX * JitterLatticePerMetre, globalY * JitterLatticePerMetre) * 0.75 + 0.5, 0.0, 1.0);

    /// <summary>A linear reading of a composition value (weights between neighbouring layers, summing to 1). Not what is drawn, see <see cref="LayerWeights"/>;
    /// the noise statistics tests measure with it.</summary>
    public static (float W0, float W1, float W2, float W3) Weights(float c)
    {
        static float W(float c, int i) => Math.Max(0f, 1f - Math.Abs(c - i));
        return (W(c, 0), W(c, 1), W(c, 2), W(c, 3));
    }

    /// <summary>Spec 3: a low-frequency part (strength 6.5 on a 1/0.2222 coarser lattice) plus two signed octaves of turbulence
    /// (frequencies 2 and 1, each divided by its frequency, strength 1.5^2), all doubled. Metres of height. The noise is 2D (the height
    /// is not part of it) and has zero mean, so the layers are not pushed up or down; our own gradient noise, deterministic by position.</summary>
    public static float Noise(double gx, double gy, float height)
    {
        double vx = gx / NoiseXyScale, vy = gy / NoiseXyScale;
        double low = GradientNoise(vx * 0.2222, vy * 0.2222) * 6.5;
        double turb = GradientNoise(vx * 2, vy * 2) / 2 + GradientNoise(vx, vy);
        return (float)((low + turb * 2.25) * 2.0);
    }

    /// <summary>Classic 2D gradient noise: a unit gradient at each lattice point (chosen by a hash of the point), the dot products
    /// with the offsets to the four corners blended with a smoothstep. Zero at the lattice points, roughly -0.7 to 0.7 between.</summary>
    public static double GradientNoise(double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double rx = x - x0, ry = y - y0, sx = S(rx), sy = S(ry);
        double Corner(int i, int j, double dx, double dy)
        {
            double a = Hash(x0 + i, y0 + j) * Math.PI;   // gradient angle, -pi..pi
            return Math.Cos(a) * dx + Math.Sin(a) * dy;
        }
        double u = Corner(0, 0, rx, ry), v = Corner(1, 0, rx - 1, ry);
        double bottom = u + (v - u) * sx;
        u = Corner(0, 1, rx, ry - 1); v = Corner(1, 1, rx - 1, ry - 1);
        double top = u + (v - u) * sx;
        return bottom + (top - bottom) * sy;
        static double S(double t) => t * t * (3 - 2 * t);
    }

    private static double Hash(int x, int y)
    {
        unchecked
        {
            uint h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u;
            h ^= h >> 13; h *= 0x5BD1E995u; h ^= h >> 15;
            h ^= h << 7; h *= 0x9E3779B1u; h ^= h >> 16;
            return h / (double)uint.MaxValue * 2.0 - 1.0;
        }
    }
}
