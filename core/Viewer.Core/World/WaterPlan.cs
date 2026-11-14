// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.World;

/// <summary>A rectangle of water: corners in metres in the anchor region's frame (x east, y north), and the height of its surface.</summary>
public readonly record struct WaterRect(float X0, float Y0, float X1, float Y1, float Height)
{
    public float Area => (X1 - X0) * (Y1 - Y0);
}

/// <summary>
/// Which water surface covers which ground. Each region has its own water height; the official viewer draws a region's water over that
/// region's extent at that height (LLWorld::updateWaterObjects makes a water object per region and one for the void around them), and
/// the void takes the height of the region the agent is in. The plan cuts a square around the anchor region into rectangles along every region's edges: a rectangle
/// inside a region is at that region's height, every other rectangle (the void and any gap between regions) at the anchor region's height.
/// </summary>
public static class WaterPlan
{
    /// <summary>The rectangles covering the square of half-size <paramref name="reach"/> around the point (<paramref name="centreX"/>, <paramref name="centreY"/>),
    /// without overlap. <paramref name="regions"/> are the regions' extents with their water heights (the anchor region among them); regions
    /// are not expected to overlap each other.</summary>
    public static IReadOnlyList<WaterRect> Build(float anchorHeight, float centreX, float centreY, float reach, IReadOnlyList<WaterRect> regions)
    {
        float left = centreX - reach, right = centreX + reach, bottom = centreY - reach, top = centreY + reach;
        var xs = new SortedSet<float> { left, right };
        var ys = new SortedSet<float> { bottom, top };
        foreach (var r in regions)
        {
            foreach (var x in new[] { r.X0, r.X1 }) if (x > left && x < right) xs.Add(x);
            foreach (var y in new[] { r.Y0, r.Y1 }) if (y > bottom && y < top) ys.Add(y);
        }
        var xl = xs.ToArray();
        var yl = ys.ToArray();
        var plan = new List<WaterRect>((xl.Length - 1) * (yl.Length - 1));
        for (int i = 0; i + 1 < xl.Length; i++)
        {
            for (int j = 0; j + 1 < yl.Length; j++)
            {
                float mx = (xl[i] + xl[i + 1]) / 2, my = (yl[j] + yl[j + 1]) / 2;
                float height = anchorHeight;
                foreach (var r in regions)
                    if (mx >= r.X0 && mx < r.X1 && my >= r.Y0 && my < r.Y1) { height = r.Height; break; }
                plan.Add(new WaterRect(xl[i], yl[j], xl[i + 1], yl[j + 1], height));
            }
        }
        return plan;
    }
}
