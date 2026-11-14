// SPDX-License-Identifier: LGPL-2.1-only
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

// Adapted 2026 by Legion Builds. ProjectorPicture.Render follows getTexture2DLodDiffuse and getProjectedLightAmbiance in deferredUtil.glsl
// (the blur level from the focus, the edge fade, the ambiance), translated from GLSL to C# for a picture made on the CPU. The window
// and canvas arithmetic and the picture handling are our own code.
// Copyright (c) 2026 Legion Builds

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>
/// The picture an engine spot light is given for a projector (Docs/PROJECTOR-SPEC.md). The engine maps a light's texture over the
/// square that holds its round cone, so the projector's rectangular window sits in the middle of a larger black square;
/// the picture is blurred as the official viewer blurs it beyond the focus, faded at its edge as it fades there, multiplied by
/// its alpha, and given the ambiance (the official viewer's light also lights the window with the picture's average colour).
/// </summary>
public static class ProjectorPicture
{
    public const int Size = 256;

    /// <summary>The window's rectangle inside the square canvas: the cone holds the window's corners, and the canvas holds the cone.</summary>
    public static (int X, int Y, int Width, int Height) Window(Beam b, int size = Size)
    {
        float half = MathF.Tan(b.Fov * 0.5f);
        float canvas = half * MathF.Sqrt(1f + b.Aspect * b.Aspect);   // the canvas' half-width at unit distance: the cone's tangent
        int w = Math.Clamp((int)MathF.Round(size * half * b.Aspect / canvas), 1, size);
        int h = Math.Clamp((int)MathF.Round(size * half / canvas), 1, size);
        return ((size - w) / 2, (size - h) / 2, w, h);
    }

    /// <summary>The blur, in mip levels, the whole beam is given. The viewer blurs by distance: the level grows from 0 at the
    /// focus to the picture's mip count at the far end. One level stands for the beam: the one at the middle of its length.</summary>
    public static int BlurLevel(Beam b, int sourceWidth)
    {
        float range = b.Far - b.Near;
        if (range <= 0f) return 0;
        float lodRange = MathF.Log2(Math.Max(2, sourceWidth));
        float mid = range * 0.5f;
        return (int)MathF.Round(Math.Clamp((mid - b.Focus) / range, 0f, 1f) * lodRange);
    }

    /// <summary>The small copy of a texture kept for a projector's picture: level 0 of <paramref name="imageData"/> (an engine image's bytes, whose
    /// mip chain, if it has one, follows level 0), averaged down by whole factors of two until neither side exceeds <paramref name="maxSide"/>.
    /// Null when the data is shorter than one level (nothing to copy).</summary>
    public static (byte[] Rgba, int Width, int Height)? SourceFrom(byte[] imageData, int w, int h, int maxSide)
    {
        if (w <= 0 || h <= 0 || imageData is null || imageData.Length < (long)w * h * 4) return null;
        var px = new byte[w * h * 4];
        Array.Copy(imageData, px, px.Length);
        while (Math.Max(w, h) > maxSide && (w > 1 || h > 1))
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var next = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int s = 0, n = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int sx = Math.Min(w - 1, x * 2 + dx), sy = Math.Min(h - 1, y * 2 + dy);
                                s += px[(sy * w + sx) * 4 + c];
                                n++;
                            }
                        next[(y * nw + x) * 4 + c] = (byte)((s + n / 2) / n);
                    }
            px = next; w = nw; h = nh;
        }
        return (px, w, h);
    }

    /// <summary>The canvas as 8-bit sRGB RGBA, <paramref name="size"/> squared, from the texture's 8-bit sRGB RGBA
    /// <paramref name="rgba"/> (<paramref name="w"/> by <paramref name="h"/>, row 0 at the top).</summary>
    public static byte[] Render(byte[] rgba, int w, int h, Beam b, int sourceWidth, int size = Size)
    {
        // linear, alpha multiplied in (the viewer's light colour is the picture's colour times its alpha)
        var lin = new float[w * h * 3];
        var avg = Vector3.Zero;
        for (int i = 0; i < w * h; i++)
        {
            float a = rgba[i * 4 + 3] / 255f;
            for (int c = 0; c < 3; c++) lin[i * 3 + c] = SrgbToLinear(rgba[i * 4 + c] / 255f) * a;
            avg += new Vector3(lin[i * 3], lin[i * 3 + 1], lin[i * 3 + 2]);
        }
        avg /= Math.Max(1, w * h);
        int level = BlurLevel(b, sourceWidth);
        // the mip the viewer would sample: a box average over 2^level texels each way
        int bw = w, bh = h;
        for (int l = 0; l < level && (bw > 1 || bh > 1); l++)
        {
            int nw = Math.Max(1, bw / 2), nh = Math.Max(1, bh / 2);
            var next = new float[nw * nh * 3];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        float s = 0f;
                        int n = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int sx = Math.Min(bw - 1, x * 2 + dx), sy = Math.Min(bh - 1, y * 2 + dy);
                                s += lin[(sy * bw + sx) * 3 + c];
                                n++;
                            }
                        next[(y * nw + x) * 3 + c] = s / n;
                    }
            lin = next; bw = nw; bh = nh;
        }
        float lodRange = MathF.Log2(Math.Max(2, sourceWidth));
        float det = MathF.Min(level / (lodRange * 0.5f), 1f);
        float edge = 0.25f * det;
        var (wx, wy, ww, wh) = Window(b, size);
        var outBytes = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++) outBytes[i * 4 + 3] = 255;   // black outside the window
        for (int y = 0; y < wh; y++)
            for (int x = 0; x < ww; x++)
            {
                float u = (x + 0.5f) / ww, v = (y + 0.5f) / wh;
                var px = Sample(lin, bw, bh, u, v);
                if (edge > 0f)
                {
                    float d = MathF.Min(0.5f - MathF.Abs(u - 0.5f), 0.5f - MathF.Abs(v - 0.5f));
                    px *= Math.Clamp(d / edge, 0f, 1f);
                }
                px += avg * (2f * b.Ambiance);   // the ambiance: about twice its value of the picture's average colour at a face square on
                int o = ((wy + y) * size + wx + x) * 4;
                outBytes[o] = Encode(px.X); outBytes[o + 1] = Encode(px.Y); outBytes[o + 2] = Encode(px.Z);
            }
        return outBytes;
    }

    private static Vector3 Sample(float[] lin, int w, int h, float u, float v)
    {
        float fx = Math.Clamp(u * w - 0.5f, 0f, w - 1f), fy = Math.Clamp(v * h - 0.5f, 0f, h - 1f);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
        float tx = fx - x0, ty = fy - y0;
        Vector3 P(int x, int y) => new(lin[(y * w + x) * 3], lin[(y * w + x) * 3 + 1], lin[(y * w + x) * 3 + 2]);
        return Vector3.Lerp(Vector3.Lerp(P(x0, y0), P(x1, y0), tx), Vector3.Lerp(P(x0, y1), P(x1, y1), tx), ty);
    }

    private static byte Encode(float linear) => (byte)MathF.Round(LightRules.LinearToSrgb(linear) * 255f);

    private static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
}
