// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Avatars;

/// <summary>Where one avatar of a crowd stands (metres from the crowd's centre), whether it walks, and which of the crowd's outfits it wears.</summary>
public readonly record struct CrowdSlot(float X, float Y, bool Walking, int Outfit);

/// <summary>
/// Lays a crowd out on a square grid, the same way every time: row by row, <paramref name="spacing"/> metres apart, centred on the origin.
/// Every fourth avatar stands, the others walk; outfits are dealt out in turn, so each is worn by as many avatars as any other, give or take one.
/// </summary>
public static class CrowdLayout
{
    public static CrowdSlot[] Place(int count, int outfits, float spacing)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        if (outfits < 1) throw new ArgumentOutOfRangeException(nameof(outfits));
        int side = (int)Math.Ceiling(Math.Sqrt(count));
        float half = (side - 1) * spacing / 2f;
        var slots = new CrowdSlot[count];
        for (int i = 0; i < count; i++)
            slots[i] = new CrowdSlot((i % side) * spacing - half, (i / side) * spacing - half, i % 4 != 3, i % outfits);
        return slots;
    }
}
