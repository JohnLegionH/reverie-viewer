// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Runtime.CompilerServices;
using Viewer.Core.World;

namespace Viewer.Core.Protocol;

/// <summary>
/// The land patches of each region the transport has heard from, answering the ground height at a whole metre. The protocol library's
/// own terrain array is switched off (it assumes a 256 m region), so anything that needs the ground height before the world model is
/// involved, such as the home position, reads it here. A region is any object the caller keys it by (the library's simulator);
/// patches are square tiles (16 m on this grid), row-major (<c>y * size + x</c>), in any region size. Thread-safe: patches arrive on a
/// network thread and the commands read on another.
/// </summary>
internal sealed class GroundHeights
{
    private readonly ConditionalWeakTable<object, Store> _regions = new();

    private sealed class Store
    {
        public readonly TerrainStore Patches = new();
    }

    /// <summary>Keeps the patch at (<paramref name="patchX"/>, <paramref name="patchY"/>); a later one for the same cell replaces it, and a
    /// patch too short for its size is ignored.</summary>
    public void Put(object region, int patchX, int patchY, int size, float[] heights)
    {
        var store = _regions.GetValue(region, _ => new Store());
        lock (store)
            store.Patches.Put(patchX, patchY, size, heights);
    }

    /// <summary>The height at the whole metre (<paramref name="x"/>, <paramref name="y"/>) of the region, or null when its patch has not
    /// arrived (or the point lies outside every patch).</summary>
    public float? HeightAt(object region, int x, int y)
    {
        if (x < 0 || y < 0 || !_regions.TryGetValue(region, out var store))
            return null;
        lock (store)
        {
            int size = store.Patches.PatchSize;
            if (size <= 0 || !store.Patches.TryGet(x / size, y / size, out var heights))
                return null;
            return heights[(y % size) * size + (x % size)];
        }
    }
}
