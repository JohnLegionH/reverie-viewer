// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llimagegl.cpp
 * @brief Generic GL image handler
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

// Adapted 2026 by Legion Builds. The test that decides whether an alpha channel can be drawn as a cut-out (LLImageGL::analyzeAlphaData,
// llrender/llimagegl.cpp) is rewritten in C# for our own pixel arrays: the same sixteen-bucket histogram of the alpha, the same added
// histogram of the 2x2 box-averaged alpha, and the same three rejection rules. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

namespace Viewer.Core.Imaging;

/// <summary>
/// Whether a texture's alpha is "all or nothing" enough to be drawn as a cut-out instead of blended (the official viewer's
/// <c>getIsAlphaMask</c>). The alpha values are sorted into sixteen buckets of sixteen. The 2x2 box average of the alpha is added
/// to the same histogram, so a high-frequency alpha (a checkerboard of 0 and 255, which averages to the middle) counts as mid-range
/// and is not a mask. The texture is NOT a mask when more than 1/48 of the (doubled) samples are in buckets 2 to 12, or when every
/// sample is in the lower half of the range and the alpha is not zero everywhere, or every sample is in the upper half and the alpha
/// is not 255 everywhere (an intentional translucency, such as a window pane).
/// </summary>
public static class AlphaMask
{
    /// <summary>Analyses the alpha (the fourth byte of each pixel) of an RGBA8 picture, top row first. An odd width or height leaves
    /// its last column or row out of the 2x2 averages (the official textures are always even).</summary>
    public static bool Analyze(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length < (long)width * height * 4) return false;
        Span<long> sample = stackalloc long[16];
        long alphaTotal = 0, length;
        if (width >= 2 && height >= 2)
        {
            // every 2x2 block adds its four alphas to the histogram, and its average (counted four times)
            int w2 = width & ~1, h2 = height & ~1;
            for (int y = 0; y < h2; y += 2)
                for (int x = 0; x < w2; x += 2)
                {
                    int s1 = rgba[(y * width + x) * 4 + 3], s3 = rgba[(y * width + x + 1) * 4 + 3];
                    int s2 = rgba[((y + 1) * width + x) * 4 + 3], s4 = rgba[((y + 1) * width + x + 1) * 4 + 3];
                    alphaTotal += s1 + s2 + s3 + s4;
                    sample[s1 / 16]++; sample[s2 / 16]++; sample[s3 / 16]++; sample[s4 / 16]++;
                    int sum = s1 + s2 + s3 + s4;
                    alphaTotal += sum;
                    sample[sum / (16 * 4)] += 4;
                }
            length = (long)w2 * h2 * 2;   // every sample was taken twice, in effect
        }
        else
        {
            length = (long)width * height;
            for (int i = 0; i < length; i++)
            {
                int s = rgba[i * 4 + 3];
                alphaTotal += s;
                sample[s / 16]++;
            }
        }
        long mid = 0, lower = 0, upper = 0;
        for (int i = 2; i < 13; i++) mid += sample[i];
        for (int i = 0; i < 8; i++) lower += sample[i];
        for (int i = 8; i < 16; i++) upper += sample[i];
        // not a mask: more than a forty-eighth of the samples mid-range; or all in the lower half with some alpha (not clear everywhere);
        // or all in the upper half with some less than solid (a deliberate translucency)
        if (mid > length / 48 || (lower == length && alphaTotal != 0) || (upper == length && alphaTotal != 255 * length)) return false;
        return true;
    }
}
