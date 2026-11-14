// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llquaternion.cpp
 * @brief LLQuaternion class implementation.
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

/**
 * @file llcriticaldamp.cpp
 * @brief Implementation of the critical damping functionality.
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

/**
 * @file llperlin.cpp
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
 * @file llmath.h
 * @brief Useful math constants and macros.
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

// Adapted 2026 by Legion Builds. The Euler constructor, the Euler angles, constrain, the quaternion product and the interpolation (llquaternion.cpp), the smoothing
// interpolant (llcriticaldamp.cpp), the two-dimensional gradient noise (llperlin.cpp) and clamp_rescale (llmath.h) follow the files named above, rewritten in C# for
// our own types; the noise table is filled from a fixed seed. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>
/// The quaternion and smoothing helpers the official viewer's built-in avatar motions use (behaviour taken from llmath/llquaternion.cpp and
/// llcommon/llcriticaldamp.cpp of the official viewer's source, written down in our own words). The official quaternion product <c>a * b</c>
/// applies a first and then b; <see cref="Then"/> is that product.
/// </summary>
public static class OfficialMath
{
    /// <summary>The rotation made of <paramref name="roll"/> about x, <paramref name="pitch"/> about y and <paramref name="yaw"/> about z (llquaternion.cpp setQuat(roll, pitch, yaw)).</summary>
    public static Quaternion FromEuler(float roll, float pitch, float yaw)
    {
        roll *= 0.5f; pitch *= 0.5f; yaw *= 0.5f;
        float sinX = MathF.Sin(roll), cosX = MathF.Cos(roll), sinY = MathF.Sin(pitch), cosY = MathF.Cos(pitch), sinZ = MathF.Sin(yaw), cosZ = MathF.Cos(yaw);
        return new Quaternion(
            sinX * cosY * cosZ + cosX * sinY * sinZ,
            cosX * sinY * cosZ - sinX * cosY * sinZ,
            cosX * cosY * sinZ + sinX * sinY * cosZ,
            cosX * cosY * cosZ - sinX * sinY * sinZ);
    }

    /// <summary>The angles (roll, pitch, yaw) of a rotation, as llquaternion.cpp getEulerAngles works them out.</summary>
    public static (float Roll, float Pitch, float Yaw) ToEuler(Quaternion q)
    {
        float sx = 2 * (q.X * q.W - q.Y * q.Z);
        float sy = 2 * (q.Y * q.W + q.X * q.Z);
        float ys = q.W * q.W - q.Y * q.Y;
        float xz = q.X * q.X - q.Z * q.Z;
        float cx = ys - xz;
        float cy = MathF.Sqrt(sx * sx + cx * cx);
        if (cy > 1e-6f) return (MathF.Atan2(sx, cx), MathF.Atan2(sy, cy), MathF.Atan2(2 * (q.Z * q.W - q.X * q.Y), ys + xz));
        return sy > 0
            ? (0f, MathF.PI / 2, 2 * MathF.Atan2(q.Z + q.X, q.W + q.Y))
            : (0f, -MathF.PI / 2, 2 * MathF.Atan2(q.Z - q.X, q.W - q.Y));
    }

    /// <summary>The rotation limited to <paramref name="radians"/> of turn: a negative w is flipped, and a turn larger than the limit keeps its axis at the limit's angle (llquaternion.cpp constrain).</summary>
    public static Quaternion Constrain(Quaternion q, float radians)
    {
        float cosLimit = MathF.Cos(radians / 2), sinLimit = MathF.Sin(radians / 2);
        if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        if (q.W < cosLimit)
        {
            float axisLength = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
            float k = axisLength > 1e-12f ? sinLimit / axisLength : 0f;
            q = new Quaternion(q.X * k, q.Y * k, q.Z * k, cosLimit);
        }
        return q;
    }

    /// <summary>Normalised linear interpolation from <paramref name="a"/> (at 0) to <paramref name="b"/> (at 1), by the shorter way round.</summary>
    public static Quaternion Nlerp(float t, Quaternion a, Quaternion b)
    {
        if (Quaternion.Dot(a, b) < 0) b = Quaternion.Negate(b);
        var q = new Quaternion(a.X * (1 - t) + b.X * t, a.Y * (1 - t) + b.Y * t, a.Z * (1 - t) + b.Z * t, a.W * (1 - t) + b.W * t);
        return Quaternion.Normalize(q);
    }

    /// <summary>The official <c>first * second</c>: the rotation that applies <paramref name="first"/> and then <paramref name="second"/>.</summary>
    public static Quaternion Then(Quaternion first, Quaternion second) => Quaternion.Multiply(second, first);

    /// <summary>How far a value with half-life <paramref name="halfLife"/> moves in <paramref name="dt"/> seconds (llcriticaldamp.cpp calcInterpolant).</summary>
    public static float Interpolant(double dt, double halfLife) => halfLife <= 0 ? 1f : Math.Clamp(1f - (float)Math.Pow(2.0, -dt / halfLife), 0f, 1f);

    /// <summary>llmath.h clamp_rescale: <paramref name="value"/> from [a, b] onto [c, d], held at the ends.</summary>
    public static float ClampRescale(float value, float a, float b, float c, float d)
    {
        float u = b == a ? (value >= b ? 1f : 0f) : Math.Clamp((value - a) / (b - a), 0f, 1f);
        return c + (d - c) * u;
    }

    /// <summary>The rotation whose x, y and z axes point along the three given directions (the quaternion of the matrix with those rows).</summary>
    public static Quaternion FromAxes(Vector3 x, Vector3 y, Vector3 z)
    {
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }
}

/// <summary>
/// Two-dimensional gradient noise, as llmath/llperlin.cpp noise2 makes it: a table of 256 unit gradients and a permutation, lattice values of
/// zero, smoothed with the s-curve. The official table is filled from the C library's random numbers each run, so it has no fixed values to
/// match; this one is filled from a fixed seed, which gives every avatar the same sway pattern for the same time.
/// </summary>
public static class OfficialNoise
{
    private const int B = 0x100, BM = 0xff;
    private static readonly System.Collections.Immutable.ImmutableArray<int> P;
    private static readonly System.Collections.Immutable.ImmutableArray<float> Gx, Gy;

    static OfficialNoise()
    {
        var p = new int[B + B + 2];
        var gx = new float[B + B + 2];
        var gy = new float[B + B + 2];
        var rng = new Random(20260409);
        for (int i = 0; i < B; i++)
        {
            p[i] = i;
            float x = (rng.Next(B + B) - B) / (float)B, y = (rng.Next(B + B) - B) / (float)B;
            float s = MathF.Sqrt(x * x + y * y);
            if (s < 1e-6f) { x = 1f; y = 0f; s = 1f; }
            gx[i] = x / s;
            gy[i] = y / s;
        }
        for (int i = B - 1; i > 0; i--)
        {
            int j = rng.Next(B);
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < B + 2; i++)
        {
            p[B + i] = p[i];
            gx[B + i] = gx[i];
            gy[B + i] = gy[i];
        }
        P = [.. p];
        Gx = [.. gx];
        Gy = [.. gy];
    }

    private static void Setup(float v, out int b0, out int b1, out float r0, out float r1)
    {
        float r = v + 4096f;
        int t = (int)r;
        b0 = t & BM;
        b1 = (b0 + 1) & BM;
        r0 = r - t;
        r1 = r0 - 1f;
    }

    private static float S(float t) => t * t * (3f - 2f * t);

    public static float Noise2(float x, float y)
    {
        Setup(x, out int bx0, out int bx1, out float rx0, out float rx1);
        Setup(y, out int by0, out int by1, out float ry0, out float ry1);
        int i = P[bx0], j = P[bx1];
        int b00 = P[i + by0], b10 = P[j + by0], b01 = P[i + by1], b11 = P[j + by1];
        float sx = S(rx0), sy = S(ry0);
        float u = rx0 * Gx[b00] + ry0 * Gy[b00];
        float v = rx1 * Gx[b10] + ry0 * Gy[b10];
        float a = u + sx * (v - u);
        u = rx0 * Gx[b01] + ry1 * Gy[b01];
        v = rx1 * Gx[b11] + ry1 * Gy[b11];
        float b = u + sx * (v - u);
        return a + sy * (b - a);
    }
}
