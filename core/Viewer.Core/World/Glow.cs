// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file glowF.glsl
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

// Adapted 2026 by Legion Builds. GlowBlur.Kernel and GlowBlur.Run follow glowF.glsl (the eight tap weights, the weighted sum times the
// strength), translated to C# for a picture made on the CPU. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

namespace Viewer.Core.World;

/// <summary>What kind of face a glow value sits on (the rules differ for PBR faces).</summary>
public enum GlowFace
{
    /// <summary>A face with no material, or a legacy material, in any alpha mode: its glow value is used as set.</summary>
    Legacy,
    /// <summary>A PBR face, opaque or blended: the glow value scales the brightest channel of its emissive colour.</summary>
    Pbr,
    /// <summary>A PBR face in mask alpha mode: draws no glow at all.</summary>
    PbrMask,
}

/// <summary>
/// How much a face glows. A face's glow value (0 to 1, the texture entry's glow) is kept as one byte, so it is
/// truncated to a multiple of 1/255. The glow is that value times the texture's alpha at each pixel (the colour's tint
/// alpha does not scale it); a PBR face's glow is further scaled by its brightest emissive channel, and a PBR face in mask
/// alpha mode draws none. See Docs/GLOW-SPEC.md.
/// </summary>
public static class FaceGlow
{
    /// <summary>The glow value as it is kept: truncated to a byte (0.5 becomes 127/255), clamped to 0..1.</summary>
    public static float Quantise(float glow) => Math.Clamp((int)(glow * 255f), 0, 255) / 255f;

    /// <summary>The face's glow weight before the texture's alpha: 0 when the face draws no glow.</summary>
    public static float Weight(float glow, GlowFace kind, float emissiveMax = 1f) => kind switch
    {
        GlowFace.PbrMask => 0f,
        GlowFace.Pbr => Quantise(glow) * Math.Clamp(emissiveMax, 0f, 1f),
        _ => Quantise(glow),
    };

    /// <summary>The glow at a pixel: the weight times the texture's alpha there.</summary>
    public static float AtPixel(float weight, float textureAlpha) => weight * Math.Clamp(textureAlpha, 0f, 1f);

    /// <summary>The brightest channel of a PBR face's emissive light at a pixel: the emissive factor times the emissive
    /// texture's texel, both linear.</summary>
    public static float EmissiveMax(float factorR, float factorG, float factorB, float texelR, float texelG, float texelB) =>
        MathF.Max(factorR * texelR, MathF.Max(factorG * texelG, factorB * texelB));

    /// <summary>
    /// The factor the face shader applies to one channel of a face's colour (linear) when it draws the glow picture. The glow
    /// is the face's colour as displayed (gamma-encoded) times the glow weight, so the factor takes the colour to the one whose
    /// encoding is the weight times the colour's own encoding.
    /// </summary>
    public static float Scale(float colour, float weight)
    {
        float c = MathF.Max(colour, 0.0001f);
        return Decode(Math.Clamp(weight, 0f, 1f) * Encode(c)) / c;
    }

    /// <summary>The display curve (sRGB): linear light to the value shown, and back.</summary>
    public static float Encode(float c) => c < 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
    public static float Decode(float c) => c < 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
}

/// <summary>
/// The glow blur and its numbers: the glow picture is a square of <see cref="Resolution"/> pixels over the whole view;
/// <see cref="Passes"/> passes alternate a horizontal and a vertical blur with a fixed eight-tap kernel, each tap
/// <see cref="Delta"/> of the view apart, each pass multiplying by <see cref="Strength"/>, and the result is added to the
/// displayed picture. The numbers are the official viewer's defaults (its documented settings).
/// </summary>
public static class GlowBlur
{
    public const int Resolution = 512;
    public const int Passes = 4;
    public const float Width = 1.3f;
    public const float Strength = 0.325f;
    /// <summary>The eight taps' weights (constant data, not a static array).</summary>
    public static ReadOnlySpan<float> Kernel => [0.25f, 0.5f, 0.8f, 1f, 1f, 0.8f, 0.5f, 0.25f];
    /// <summary>The distance between taps, as a share of the view.</summary>
    public static float Delta => Width / Resolution;

    /// <summary>The glow picture (<paramref name="rgb"/>, width x height x 3, values 0..1) after all passes.</summary>
    public static float[] Run(float[] rgb, int width, int height)
    {
        var src = (float[])rgb.Clone();
        var dst = new float[src.Length];
        var kernel = Kernel;
        for (int pass = 0; pass < Passes; pass++)
        {
            bool horizontal = pass % 2 == 0;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            for (int c = 0; c < 3; c++)
            {
                float sum = 0f;
                for (int t = 0; t < kernel.Length; t++)
                {
                    // tap positions in texels, from the pixel's centre: the taps straddle it (-3.5 .. +3.5 steps)
                    float step = (t - 3.5f) * Delta;
                    float fx = x + (horizontal ? step * width : 0f), fy = y + (horizontal ? 0f : step * height);
                    sum += kernel[t] * Sample(src, width, height, fx, fy, c);
                }
                dst[(y * width + x) * 3 + c] = Math.Clamp(sum * Strength, 0f, 1f);
            }
            (src, dst) = (dst, src);
        }
        return src;
    }

    // bilinear, clamped to the edge; (fx, fy) in texels with pixel x's centre at x
    private static float Sample(float[] img, int w, int h, float fx, float fy, int c)
    {
        fx = Math.Clamp(fx, 0f, w - 1); fy = Math.Clamp(fy, 0f, h - 1);
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = fx - x0, ty = fy - y0;
        float a = img[(y0 * w + x0) * 3 + c] * (1 - tx) + img[(y0 * w + x1) * 3 + c] * tx;
        float b = img[(y1 * w + x0) * 3 + c] * (1 - tx) + img[(y1 * w + x1) * 3 + c] * tx;
        return a * (1 - ty) + b * ty;
    }

    /// <summary>
    /// Fills a rectangle of the glow picture (<see cref="Resolution"/> square, 3 channels), the rectangle given as shares of the
    /// view (u right, v down) with the colour (0..1, gamma-encoded) already scaled by the glow value. Each picture pixel
    /// takes the colour in proportion to how much of it the rectangle covers; where the rectangle only part-covers a pixel
    /// the larger value is kept, so faces side by side do not dim each other.
    /// </summary>
    public static void Fill(float[] picture, float u0, float v0, float u1, float v1, float r, float g, float b)
    {
        int n = Resolution;
        for (int y = 0; y < n; y++)
        {
            float cy = MathF.Max(0f, MathF.Min(v1 * n, y + 1) - MathF.Max(v0 * n, y));
            if (cy <= 0f) continue;
            for (int x = 0; x < n; x++)
            {
                float cx = MathF.Max(0f, MathF.Min(u1 * n, x + 1) - MathF.Max(u0 * n, x));
                if (cx <= 0f) continue;
                float cover = cx * cy;
                int i = (y * n + x) * 3;
                picture[i] = MathF.Max(picture[i], r * cover);
                picture[i + 1] = MathF.Max(picture[i + 1], g * cover);
                picture[i + 2] = MathF.Max(picture[i + 2], b * cover);
            }
        }
    }

    /// <summary>The picture at a place in the view (u right, v down, both 0..1), bilinear.</summary>
    public static (float R, float G, float B) At(float[] picture, float u, float v)
    {
        float fx = u * Resolution - 0.5f, fy = v * Resolution - 0.5f;
        return (Sample(picture, Resolution, Resolution, fx, fy, 0), Sample(picture, Resolution, Resolution, fx, fy, 1), Sample(picture, Resolution, Resolution, fx, fy, 2));
    }

    /// <summary>The displayed colour with the glow added, clamped to 0..1.</summary>
    public static float Add(float displayed, float glow) => Math.Clamp(displayed + glow, 0f, 1f);
}
