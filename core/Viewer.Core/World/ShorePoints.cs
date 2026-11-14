// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;

namespace Viewer.Core.World;

/// <summary>Places a live look at the water can stand: dry ground at a shore, and a point out at sea.</summary>
public static class ShorePoints
{
    private const float Step = 4f, Margin = 8f, SpreadApart = 30f, DeepNear = 24f;

    /// <summary>
    /// Up to <paramref name="count"/> places on dry ground 0.3 to 2.5 m above the water with water at least a metre deep within 24 m, at least
    /// 30 m apart, those nearest a metre above the water first. X, Y in metres in the region, Z the ground height.
    /// </summary>
    public static IReadOnlyList<Vector3> Shores(Func<float, float, float> groundAt, float waterHeight, float regionSize, int count)
    {
        var candidates = new List<Vector3>();
        for (float y = Margin; y <= regionSize - Margin; y += Step)
            for (float x = Margin; x <= regionSize - Margin; x += Step)
            {
                float g = groundAt(x, y);
                if (g < waterHeight + 0.3f || g > waterHeight + 2.5f) continue;
                if (DeepWaterNear(groundAt, waterHeight, regionSize, x, y)) candidates.Add(new Vector3(x, y, g));
            }
        var chosen = new List<Vector3>();
        foreach (var c in candidates.OrderBy(c => MathF.Abs(c.Z - (waterHeight + 1f))).ThenBy(c => c.Y).ThenBy(c => c.X))
        {
            if (chosen.Count >= count) break;
            if (chosen.All(p => Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(c.X, c.Y)) >= SpreadApart)) chosen.Add(c);
        }
        return chosen;
    }

    private static bool DeepWaterNear(Func<float, float, float> groundAt, float waterHeight, float regionSize, float x, float y)
    {
        for (int a = 0; a < 8; a++)
        {
            float dx = MathF.Cos(a * MathF.PI / 4), dy = MathF.Sin(a * MathF.PI / 4);
            for (float d = 8f; d <= DeepNear; d += 8f)
            {
                float px = x + dx * d, py = y + dy * d;
                if (px < 0 || py < 0 || px > regionSize || py > regionSize) continue;
                if (groundAt(px, py) <= waterHeight - 1f) return true;
            }
        }
        return false;
    }

    /// <summary>A point over water at least 3 m deep, the nearest to the middle of the region (X, Y), or none.</summary>
    public static Vector2? Sea(Func<float, float, float> groundAt, float waterHeight, float regionSize)
    {
        Vector2? best = null;
        float bestDistance = float.MaxValue;
        var mid = new Vector2(regionSize / 2f, regionSize / 2f);
        for (float y = Margin; y <= regionSize - Margin; y += 8f)
            for (float x = Margin; x <= regionSize - Margin; x += 8f)
            {
                if (groundAt(x, y) > waterHeight - 3f) continue;
                float d = Vector2.Distance(new Vector2(x, y), mid);
                if (d < bestDistance) { bestDistance = d; best = new Vector2(x, y); }
            }
        return best;
    }
}
