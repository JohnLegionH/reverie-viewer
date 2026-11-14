// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file noise.h
 * @brief Perlin noise routines for procedural textures, etc
 *
 * $LicenseInfo:firstyear=2000&license=viewerlgpl$
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
// Adapted 2026 by Legion Builds. TreeNoise follows init(), fast_setup(), noise3() and turbulence3() of noise.h, written in C#. The official init()
// seeds the C runtime's generator with 42 (so the noise is repeatable), fills the tables from rand() and shuffles the permutation; MsvcRand is
// the Windows runtime's generator (state * 214013 + 2531011, the state's bits 16 to 30), which is what the Windows viewer's rand() is.
// Copyright (c) 2026 Legion Builds
using System.Collections.Immutable;
using System.Numerics;

namespace Viewer.Core.World;

/// <summary>The Windows C runtime's rand(): the state moves by state * 214013 + 2531011 and the result is the state's bits 16 to 30.</summary>
internal sealed class MsvcRand
{
    private uint _state = 1;

    public void Seed(uint seed) => _state = seed;

    public int Next()
    {
        _state = unchecked(_state * 214013u + 2531011u);
        return (int)((_state >> 16) & 0x7FFF);
    }
}

/// <summary>The official viewer's noise (noise.h) over its fixed tables, and the turbulence the tree trunks use.</summary>
internal static class TreeNoise
{
    private const int B = 0x100, BM = 0xff, N = 0x1000;
    private const float NF32 = 4096f;

    private static readonly ImmutableArray<int> P_ = [];
    private static readonly ImmutableArray<float> G1_ = [], G2_ = [], G3_ = [];

    static TreeNoise()
    {
        var p = new int[B + B + 2];
        var g1 = new float[B + B + 2];
        var g2 = new float[(B + B + 2) * 2];
        var g3 = new float[(B + B + 2) * 3];
        var rng = new MsvcRand();
        rng.Seed(42);   // the official viewer wants repeatable noise, so it seeds with a known value
        int i, j, k;
        for (i = 0; i < B; i++)
        {
            p[i] = i;
            g1[i] = (float)((rng.Next() % (B + B)) - B) / B;
            for (j = 0; j < 2; j++) g2[i * 2 + j] = (float)((rng.Next() % (B + B)) - B) / B;
            Normalize(g2, i * 2, 2);
            for (j = 0; j < 3; j++) g3[i * 3 + j] = (float)((rng.Next() % (B + B)) - B) / B;
            Normalize(g3, i * 3, 3);
        }
        while (--i > 0)   // the official loop is "while (--i)", i being B after the fill
        {
            k = p[i];
            p[i] = p[j = rng.Next() % B];
            p[j] = k;
        }
        for (i = 0; i < B + 2; i++)
        {
            p[B + i] = p[i];
            g1[B + i] = g1[i];
            for (j = 0; j < 2; j++) g2[(B + i) * 2 + j] = g2[i * 2 + j];
            for (j = 0; j < 3; j++) g3[(B + i) * 3 + j] = g3[i * 3 + j];
        }
        P_ = [.. p]; G1_ = [.. g1]; G2_ = [.. g2]; G3_ = [.. g3];
    }

    private static void Normalize(float[] v, int at, int n)
    {
        float sum = 0f;
        for (int c = 0; c < n; c++) sum += v[at + c] * v[at + c];
        float s = 1f / (float)Math.Sqrt(sum);
        for (int c = 0; c < n; c++) v[at + c] *= s;
    }

    internal static float G1(int i) => G1_[i];
    internal static float G2(int i, int k) => G2_[i * 2 + k];
    internal static float G3(int i, int k) => G3_[i * 3 + k];
    internal static int P(int i) => P_[i];

    private static float SCurve(float t) => t * t * (3f - 2f * t);
    private static float Lerp(float t, float a, float b) => a + t * (b - a);

    private static void FastSetup(float vec, out byte b0, out byte b1, out float r0, out float r1)
    {
        r1 = vec + NF32;
        int t = (int)r1;   // truncation toward zero, as lltrunc
        b0 = (byte)t;
        b1 = (byte)(b0 + 1);
        r0 = r1 - t;
        r1 = r0 - 1f;
    }

    private static float At3(float rx, float ry, float rz, int q) => rx * G3_[q * 3] + ry * G3_[q * 3 + 1] + rz * G3_[q * 3 + 2];

    public static float Noise3(float x, float y, float z)
    {
        FastSetup(x, out byte bx0, out byte bx1, out float rx0, out float rx1);
        FastSetup(y, out byte by0, out byte by1, out float ry0, out float ry1);
        FastSetup(z, out byte bz0, out byte bz1, out float rz0, out float rz1);
        int i = P_[bx0], j = P_[bx1];
        int b00 = P_[i + by0], b10 = P_[j + by0], b01 = P_[i + by1], b11 = P_[j + by1];
        float t = SCurve(rx0), sy = SCurve(ry0), sz = SCurve(rz0);
        float u = At3(rx0, ry0, rz0, b00 + bz0), v = At3(rx1, ry0, rz0, b10 + bz0);
        float a = Lerp(t, u, v);
        u = At3(rx0, ry1, rz0, b01 + bz0); v = At3(rx1, ry1, rz0, b11 + bz0);
        float b = Lerp(t, u, v);
        float c = Lerp(sy, a, b);
        u = At3(rx0, ry0, rz1, b00 + bz1); v = At3(rx1, ry0, rz1, b10 + bz1);
        a = Lerp(t, u, v);
        u = At3(rx0, ry1, rz1, b01 + bz1); v = At3(rx1, ry1, rz1, b11 + bz1);
        b = Lerp(t, u, v);
        float d = Lerp(sy, a, b);
        return Lerp(sz, c, d);
    }

    public static float Turbulence3(Vector3 v, float freq)
    {
        float t = 0f;
        for (; freq >= 1f; freq *= 0.5f)
            t += Noise3(freq * v.X, freq * v.Y, freq * v.Z) / freq;
        return t;
    }
}
