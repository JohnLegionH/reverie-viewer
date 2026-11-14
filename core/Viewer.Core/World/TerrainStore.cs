// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.World;

/// <summary>
/// One region's terrain patches (the var-region workaround's store: LibreMetaverse's own fixed 16x16 array is off,
/// ADR-007). Keyed by patch coordinates; a later patch for the same cell replaces the earlier one (terraform edits).
/// Not thread-safe: <see cref="WorldState"/> owns it under its lock.
/// </summary>
public sealed class TerrainStore
{
    private readonly Dictionary<(int X, int Y), float[]> _patches = new();
    private int _patchSize;

    public int PatchCount => _patches.Count;

    /// <summary>Edge length of the patches seen so far (16 on OpenSim); 0 before the first.</summary>
    public int PatchSize => _patchSize;

    internal bool Put(int x, int y, int size, float[] heights)
    {
        if (size <= 0 || x < 0 || y < 0 || heights.Length < size * size)
            return false;
        _patchSize = size;
        _patches[(x, y)] = heights;
        return true;
    }

    public bool TryGet(int x, int y, out float[] heights) => _patches.TryGetValue((x, y), out heights!);

    /// <summary>How many patches cover <paramref name="region"/> (a 512 m var region of 16 m patches: 1024).</summary>
    public static int ExpectedPatches(RegionInfo region, int patchSize)
    {
        if (patchSize <= 0)
            return 0;
        return (region.SizeX + patchSize - 1) / patchSize * ((region.SizeY + patchSize - 1) / patchSize);
    }

    /// <summary>Patches received that lie inside <paramref name="region"/>.</summary>
    public int PatchesInside(RegionInfo region)
    {
        if (_patchSize <= 0)
            return 0;
        int nx = (region.SizeX + _patchSize - 1) / _patchSize, ny = (region.SizeY + _patchSize - 1) / _patchSize;
        return _patches.Keys.Count(k => k.X < nx && k.Y < ny);
    }

    /// <summary>Every patch of the region has arrived (the "region B terrain completeness" check).</summary>
    public bool IsComplete(RegionInfo region) =>
        _patchSize > 0 && PatchesInside(region) == ExpectedPatches(region, _patchSize);

    internal void Clear()
    {
        _patches.Clear();
        _patchSize = 0;
    }
}
