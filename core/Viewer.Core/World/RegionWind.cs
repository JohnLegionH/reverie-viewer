// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>
/// A region's wind as its LayerData wind layer gives it: a 16 x 16 grid of horizontal velocities (metres a second) over the region,
/// the first array along x, the second along y, row by row from the south-west corner.
/// </summary>
public sealed class RegionWind
{
    public const int Cells = 16;

    public RegionWind(float[] x, float[] y)
    {
        X = x;
        Y = y;
    }

    public float[] X { get; }
    public float[] Y { get; }

    /// <summary>The wind at (<paramref name="x"/>, <paramref name="y"/>) in a region <paramref name="sizeX"/> by <paramref name="sizeY"/>
    /// metres: the velocity of the cell the point falls in (no vertical part).</summary>
    public Vector3 At(float x, float y, float sizeX, float sizeY)
    {
        if (X.Length < Cells * Cells || Y.Length < Cells * Cells || !(sizeX > 0) || !(sizeY > 0) || !float.IsFinite(x) || !float.IsFinite(y))
            return Vector3.Zero;
        int cx = Math.Clamp((int)MathF.Floor(x / sizeX * Cells), 0, Cells - 1);
        int cy = Math.Clamp((int)MathF.Floor(y / sizeY * Cells), 0, Cells - 1);
        int i = cy * Cells + cx;
        return new Vector3(X[i], Y[i], 0f);
    }
}
