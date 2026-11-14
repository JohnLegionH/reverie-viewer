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

// Adapted 2026 by Legion Builds. The ramp picture is read as the official terrain shader reads its alpha ramp (clamped, linearly filtered); the bundled picture is a decoded copy of the
// viewer's alpha_gradient_2d (THIRD-PARTY-NOTICES.md). The rest of this file is our own code.

// Copyright (c) 2026 Legion Builds

namespace Viewer.Core.World;

/// <summary>
/// The terrain's blend ramp: a 256 x 256 eight-bit picture read at (composition value, jitter), the way the official terrain shader reads
/// its two-dimensional alpha ramp. Alpha is 1 where the first coordinate is 0 and falls to 0 where it is 1; the second coordinate moves the
/// place of the fall, so the edge between two ground layers is broken up. Linear filtering, clamped at the edges. Rows are kept bottom first
/// (v = 0 is the picture's bottom row). The bundled picture is a decoded copy of the official viewer's alpha_gradient_2d (see THIRD-PARTY-NOTICES.md).
/// </summary>
public static class TerrainRamp
{
    public const int Size = 256;

    // the picture is read once and never changed, so one copy serves every session (held in an object, as the cloud noise is)
    private sealed class Picture { public readonly byte[] Bytes = Load(); }
    private static readonly Picture s_picture = new();

    /// <summary>The picture's bytes, row after row, the bottom row first (what the renderer uploads).</summary>
    public static byte[] BottomFirst => s_picture.Bytes;

    private static byte[] Load()
    {
        using var s = typeof(TerrainRamp).Assembly.GetManifestResourceStream("terrain-ramp.raw") ?? throw new InvalidOperationException("the bundled terrain ramp picture is missing");
        var b = new byte[Size * Size];
        int got = 0;
        while (got < b.Length) { int n = s.Read(b, got, b.Length - got); if (n <= 0) break; got += n; }
        if (got != b.Length) throw new InvalidDataException("the bundled terrain ramp picture is the wrong size");
        return b;
    }

    /// <summary>The ramp at (<paramref name="u"/>, <paramref name="v"/>), both clamped to 0..1; the result is 0..1.</summary>
    public static float Sample(float u, float v)
    {
        double x = Math.Clamp(u, 0f, 1f) * Size - 0.5, y = Math.Clamp(v, 0f, 1f) * Size - 0.5;
        double fx = Math.Floor(x), fy = Math.Floor(y), tx = x - fx, ty = y - fy;
        int x0 = Math.Clamp((int)fx, 0, Size - 1), x1 = Math.Clamp((int)fx + 1, 0, Size - 1);
        int y0 = Math.Clamp((int)fy, 0, Size - 1), y1 = Math.Clamp((int)fy + 1, 0, Size - 1);
        var data = s_picture.Bytes;
        double a = data[y0 * Size + x0], b = data[y0 * Size + x1], c = data[y1 * Size + x0], d = data[y1 * Size + x1];
        return (float)(((a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty) / 255.0);
    }
}
