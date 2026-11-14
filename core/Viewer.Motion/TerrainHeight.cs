// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
namespace Viewer.Motion;

/// <summary>The land's height between its samples.</summary>
public static class TerrainHeight
{
    /// <summary>The height at (x, y) in metres from the samples at whole metres, interpolated between the four around it (the land is drawn
    /// as a surface between its samples, so a camera kept above the nearest sample can still be under the drawn land).</summary>
    public static float Bilinear(Func<int, int, float> sample, float x, float y)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        float a = sample(x0, y0), b = sample(x0 + 1, y0), c = sample(x0, y0 + 1), d = sample(x0 + 1, y0 + 1);
        return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
    }
}
