// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Imaging;

namespace Viewer.Core.Avatars;

/// <summary>
/// Whether a body bake hides its body region, decided once from a fixed resolution and not from the level of the bake that happens to be on the GPU.
/// A bake's alpha says where the system body is cut away (<see cref="BakeAlpha"/>); a sparse alpha bake, mostly clear with a small drawn patch, loses that patch
/// when it is read at a coarse level, so judging it from the resident level would hide or show the body as the avatar's distance changes.
/// <para>
/// The mask is <see cref="Size"/> pixels along its longer side (the bake's own size when that is smaller): a cell of the bake at that resolution is drawn when its
/// average alpha is at least <see cref="BakeAlpha.Minimum"/>, and the bake hides its region when no cell is drawn. 256 is a quarter of a 1024 bake's side: a
/// drawn patch a few pixels across still fills a cell, and the level that gives it (a 1024 bake's level 2) is a sixteenth of the full decode's pixels.
/// A bake that arrives at that level or a finer one is judged from what arrived (a finer level is averaged down to the mask's cells); one that arrives coarser and is not
/// opaque asks for the mask's level once (<see cref="LevelNeeded"/>). A bake with no clear pixel at all is opaque and needs no mask.
/// </para>
/// </summary>
public sealed class BakeAlphaMask
{
    /// <summary>The mask's resolution, in pixels along the longer side (the bake's own size when it is smaller).</summary>
    public const int Size = 256;

    /// <summary>The mask has been decided; it does not change afterwards.</summary>
    public bool Known { get; private set; }

    /// <summary>No cell of the bake is drawn: its region is hidden. Meaningful once <see cref="Known"/>.</summary>
    public bool Hides { get; private set; }

    /// <summary>The coarsest level of a bake of this size that still has the mask's resolution (a codestream with fewer levels: the coarsest it has).</summary>
    public static int LevelFor(int fullWidth, int fullHeight, int levelCount)
    {
        int longer = Math.Max(fullWidth, fullHeight);
        int level = 0;
        while (level + 1 < levelCount && (longer >> (level + 1)) >= Size) level++;
        return level;
    }

    /// <summary>Whether no cell of the mask is drawn, read from a decoded level (RGBA, 4 bytes a pixel); null when the level is coarser than the mask (it cannot say) or has no pixels.</summary>
    public static bool? HidesAt(ReadOnlySpan<byte> rgba, int width, int height, int fullWidth, int fullHeight)
    {
        int target = Math.Min(Size, Math.Max(fullWidth, fullHeight));
        int longer = Math.Max(width, height);
        if (width <= 0 || height <= 0 || longer < target || rgba.Length < (long)width * height * 4) return null;
        int cell = Math.Max(1, longer / target);
        for (int y0 = 0; y0 < height; y0 += cell)
            for (int x0 = 0; x0 < width; x0 += cell)
            {
                int y1 = Math.Min(height, y0 + cell), x1 = Math.Min(width, x0 + cell);
                long sum = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++) sum += rgba[(y * width + x) * 4 + 3];
                if (BakeAlpha.IsDrawn((byte)(sum / ((long)(y1 - y0) * (x1 - x0))))) return false;
            }
        return true;
    }

    /// <summary>A decoded level of the bake. <paramref name="hidesAtMask"/> is <see cref="HidesAt"/> of it; once the mask is known nothing changes it.</summary>
    public void Offer(int width, int height, AlphaClass alpha, bool? hidesAtMask)
    {
        if (Known) return;
        if (alpha == AlphaClass.Opaque) { Known = true; Hides = false; return; }
        if (hidesAtMask is { } hides) { Known = true; Hides = hides; }
    }

    /// <summary>The level to decode to decide the mask, or null when it is decided.</summary>
    public int? LevelNeeded(int fullWidth, int fullHeight, int levelCount) => Known ? null : LevelFor(fullWidth, fullHeight, levelCount);

    /// <summary>Whether a bake slot needs a texture on the GPU: not when a worn mesh hides its part, nor when its own alpha does.</summary>
    public static bool NeedsGpuTexture(bool hiddenByWornMesh, BakeAlphaMask mask) => !hiddenByWornMesh && !(mask.Known && mask.Hides);
}
