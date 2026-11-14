// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file class1\environment\waterV.glsl
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

// Adapted 2026 by Legion Builds. WaterWaves.Coordinates follows the wave coordinate lines of the water vertex shader (waterV.glsl: the
// sideways swell added to x, the big wave and the two small waves, each moving along a wave direction with time), written in C#
// for one point at a time.
// Copyright (c) 2026 Legion Builds

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>
/// Where the water's normal map is read for a point of the water at a moment: three sets of texture coordinates (one big wave, two small
/// ones) that drift along the region's two wave directions. Behaviour reference: the official viewer's water vertex shader. The water
/// ripples come from reading the normal map at all three places and blending the normals.
/// </summary>
public static class WaterWaves
{
    /// <summary>The water's clock: the official viewer runs it at half the elapsed seconds.</summary>
    public static double Phase(double elapsedSeconds) => elapsedSeconds * 0.5;

    /// <summary>
    /// The texture coordinates of the big wave and the two small waves for the point (<paramref name="x"/>, <paramref name="y"/>) metres
    /// (region frame, y north) at <paramref name="time"/> (from <see cref="Phase"/>). The big wave and the second small wave move along
    /// <paramref name="direction1"/>, the first small wave along <paramref name="direction2"/>.
    /// </summary>
    public static (Vector2 Big, Vector2 Little1, Vector2 Little2) Coordinates(float x, float y, float time, Vector2 direction1, Vector2 direction2)
    {
        float vx = x + (MathF.Cos(x * 0.08f) + MathF.Sin(y * 0.02f)) * 6f;   // the sweeping swell
        var big = new Vector2(vx * 0.04f + direction1.X * time * 0.055f, y * 0.04f + direction1.Y * time * 0.055f);
        var little1 = new Vector2(vx * 0.45f + direction2.X * time * 0.13f, y * 0.9f + direction2.Y * time * 0.13f);
        var little2 = new Vector2(vx * 0.1f + direction1.X * time * 0.1f, y * 0.2f + direction1.Y * time * 0.1f);
        return (big, little1, little2);
    }
}

/// <summary>
/// A tileable water normal map made here (no texture is fetched): a sum of straight waves of whole numbers of cycles per tile, so every edge
/// continues into the opposite one. Each texel holds the unit normal as (n * 0.5 + 0.5) in red, green and blue; alpha is 255. The tilt is
/// small (a root-mean-square slope of about 0.18) with waves from about 3 to 24 cycles per tile. The official viewer reads a texture from
/// the grid's assets by the id in the water settings; this map is what the viewer ripples the water with when it has not fetched that.
/// </summary>
public static class WaterNormalMap
{
    private const double TargetSlope = 0.18;

    public static byte[] Generate(int size)
    {
        var waves = Waves(size);
        var data = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double dx = 0, dy = 0;
            foreach (var (kx, ky, amp, phase) in waves)
            {
                double c = amp * Math.Cos(2 * Math.PI * (kx * x + ky * y) / size + phase) * 2 * Math.PI / size;
                dx += c * kx; dy += c * ky;
            }
            double nx = -dx, ny = -dy, nz = 1.0, len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            int i = (y * size + x) * 4;
            data[i] = (byte)Math.Round((nx / len * 0.5 + 0.5) * 255);
            data[i + 1] = (byte)Math.Round((ny / len * 0.5 + 0.5) * 255);
            data[i + 2] = (byte)Math.Round((nz / len * 0.5 + 0.5) * 255);
            data[i + 3] = 255;
        }
        return data;
    }

    private static List<(int Kx, int Ky, double Amp, double Phase)> Waves(int size)
    {
        // a fixed pseudo-random sequence, so the map is the same on every machine
        uint s = 0x2545F491u;
        double Next() { s = s * 1664525u + 1013904223u; return (s >> 8) / (double)(1 << 24); }
        var list = new List<(int, int, double, double)>();
        for (int i = 0; i < 14; i++)
        {
            double cycles = 3 + Next() * 21, angle = Next() * Math.PI * 2;
            int kx = (int)Math.Round(cycles * Math.Cos(angle)), ky = (int)Math.Round(cycles * Math.Sin(angle));
            if (kx == 0 && ky == 0) kx = 3;
            double k = Math.Sqrt(kx * kx + ky * ky);
            list.Add((kx, ky, Math.Pow(k, -0.9), Next() * Math.PI * 2));
        }
        // scale the amplitudes so the root-mean-square slope is the target (slope of a wave: amp * 2 pi k / size, mean square half of its square)
        double sum = 0;
        foreach (var (kx, ky, amp, _) in list) sum += amp * amp * (kx * kx + ky * ky) * (2 * Math.PI / size) * (2 * Math.PI / size) / 2;
        double scale = TargetSlope / Math.Sqrt(sum / 1.0) / 1.0;
        return list.ConvertAll(w => (w.Item1, w.Item2, w.Item3 * scale, w.Item4));
    }
}
