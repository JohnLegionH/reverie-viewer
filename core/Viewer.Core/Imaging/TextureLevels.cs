// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Imaging;

/// <summary>
/// Discard levels. Level 0 is full resolution; each level halves width and
/// height (JPEG 2000 reduced resolution: ceil(size / 2^level) for an image at the origin), down to about
/// <see cref="CoarsestSide"/> px on the longest side, or fewer levels when the codestream has fewer resolution levels.
/// </summary>
public static class TextureLevels
{
    /// <summary>The coarsest level is the last whose longest side is still at least this many pixels.</summary>
    public const int CoarsestSide = 32;

    /// <summary>A side's length at <paramref name="level"/>.</summary>
    public static int Size(int full, int level) => level <= 0 ? full : (int)(((long)full + (1L << level) - 1) >> level);

    /// <summary>How many levels an image of this size with <paramref name="decompositionLevels"/> wavelet levels has (at least 1).</summary>
    public static int Count(int width, int height, int decompositionLevels)
    {
        int longest = Math.Max(width, height), n = 0;
        while (n < decompositionLevels && n < 30 && Size(longest, n + 1) >= CoarsestSide) n++;
        return n + 1;
    }
}

/// <summary>
/// A full mipmap chain for an RGBA8 image, built off the main thread with the decode, laid out as the
/// engine expects (level 0, then each half-size level down to 1x1, each MAX(1, size / 2) of the one before). Each texel
/// is the rounded mean of the 2x2 block above it, (a + b + c + d + 2) / 4, with the right or lower texel repeated on a
/// side of length 1: the same box filter as Godot's own RGBA8 mipmap generation, so a chain handed over ready uploads the
/// same pixels the engine would have made (checked on the GPU path by the render harness: --tr-mipcheck).
/// </summary>
public static class MipChain
{
    public static int Levels(int width, int height)
    {
        int n = 1;
        while (width > 1 || height > 1) { width = Math.Max(1, width >> 1); height = Math.Max(1, height >> 1); n++; }
        return n;
    }

    public static long Bytes(int width, int height)
    {
        long total = 0;
        while (true)
        {
            total += (long)width * height * 4;
            if (width == 1 && height == 1) return total;
            width = Math.Max(1, width >> 1); height = Math.Max(1, height >> 1);
        }
    }

    /// <param name="pool">: where the chain's buffer comes from (null: a new array). Every byte is written.</param>
    public static byte[] Build(int width, int height, ReadOnlySpan<byte> rgba, PixelPool? pool = null)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("RGBA8 length does not match the size");
        int length = checked((int)Bytes(width, height));
        var chain = pool?.Rent(length) ?? new byte[length];
        rgba.CopyTo(chain);
        int src = 0, w = width, h = height;
        while (w > 1 || h > 1)
        {
            int dw = Math.Max(1, w >> 1), dh = Math.Max(1, h >> 1);
            int dst = src + w * h * 4;
            int right = w == 1 ? 0 : 4, down = h == 1 ? 0 : w * 4;
            for (int y = 0; y < dh; y++)
            {
                int up = src + y * 2 * down, o = dst + y * dw * 4;
                for (int x = 0; x < dw; x++, up += right * 2, o += 4)
                    for (int c = 0; c < 4; c++)
                        chain[o + c] = (byte)((chain[up + c] + chain[up + right + c] + chain[up + down + c] + chain[up + down + right + c] + 2) >> 2);
            }
            src = dst; w = dw; h = dh;
        }
        return chain;
    }
}

/// <summary>
/// A discard level made from the full-resolution image: the reference that tests and the decode probe compare a reduced
/// decode with. The decode path itself does not use it: every level comes from the decoder's reduced-resolution decode.
/// Each step halves the image to
/// ceil(size / 2), the size a JPEG 2000 reduced level has (<see cref="TextureLevels.Size"/>). The filter is centred on the
/// even texel (weights 1, 2, 1, the neighbour repeated at an edge), because a JPEG 2000 low-pass sample x sits at source
/// position 2x: a plain 2x2 box (<see cref="MipChain"/>) would move the image by half a texel per level.
/// </summary>
public static class LevelDownscale
{
    /// <param name="pool">: where the working and result buffers come from (null: new arrays); each
    /// working buffer goes back as soon as the next pass has read it (never the caller's <paramref name="rgba"/>).</param>
    public static byte[] ToLevel(int width, int height, byte[] rgba, int level, out int w, out int h, PixelPool? pool = null)
    {
        w = width; h = height;
        var cur = rgba;
        for (int n = 0; n < level; n++)
        {
            int dw = (w + 1) >> 1, dh = (h + 1) >> 1;
            // horizontal pass
            var tmp = pool?.Rent(dw * h * 4) ?? new byte[dw * h * 4];   // every byte is written below
            for (int y = 0; y < h; y++)
                for (int x = 0; x < dw; x++)
                {
                    int c0 = 2 * x, l = Math.Max(0, c0 - 1), r = Math.Min(w - 1, c0 + 1);
                    int s = (y * w) * 4, o = (y * dw + x) * 4;
                    for (int c = 0; c < 4; c++)
                        tmp[o + c] = (byte)((cur[s + l * 4 + c] + 2 * cur[s + c0 * 4 + c] + cur[s + r * 4 + c] + 2) >> 2);
                }
            var next = pool?.Rent(dw * dh * 4) ?? new byte[dw * dh * 4];   // every byte is written below
            for (int y = 0; y < dh; y++)
            {
                int c0 = 2 * y, u = Math.Max(0, c0 - 1), d = Math.Min(h - 1, c0 + 1);
                for (int x = 0; x < dw; x++)
                {
                    int o = (y * dw + x) * 4;
                    for (int c = 0; c < 4; c++)
                        next[o + c] = (byte)((tmp[(u * dw + x) * 4 + c] + 2 * tmp[(c0 * dw + x) * 4 + c] + tmp[(d * dw + x) * 4 + c] + 2) >> 2);
                }
            }
            pool?.Return(tmp);
            if (!ReferenceEquals(cur, rgba)) pool?.Return(cur);
            cur = next; w = dw; h = dh;
        }
        return cur;
    }
}
