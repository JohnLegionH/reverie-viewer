// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;

namespace Viewer.Core.World;

/// <summary>Finds bare, level land for a live look at the ground: well above the water, with no object near it.</summary>
public static class PlainGround
{
    private const float Step = 2f, Margin = 8f, SlopeReach = 1.5f, MaxSlope = 0.25f;

    /// <summary>
    /// The place nearest (<paramref name="nearX"/>, <paramref name="nearY"/>) where the land is at least <paramref name="minAboveWater"/> metres above the
    /// water, level to within a quarter of a metre over 1.5 m each way, with none of <paramref name="objects"/> within <paramref name="clearRadius"/> metres
    /// (measured in the horizontal plane). X, Y in region metres, Z the land height; null when there is no such place.
    /// </summary>
    public static Vector3? Find(Func<float, float, float?> groundAt, float waterHeight, float regionSize, IReadOnlyList<Vector3> objects,
        float minAboveWater, float clearRadius, float nearX, float nearY)
    {
        Vector3? best = null;
        float bestDistance = float.MaxValue;
        for (float y = Margin; y <= regionSize - Margin; y += Step)
            for (float x = Margin; x <= regionSize - Margin; x += Step)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(nearX, nearY));
                if (d >= bestDistance) continue;
                if (groundAt(x, y) is not float g || g < waterHeight + minAboveWater) continue;
                if (!Level(groundAt, x, y, g)) continue;
                if (objects.Any(o => Vector2.Distance(new Vector2(o.X, o.Y), new Vector2(x, y)) < clearRadius)) continue;
                best = new Vector3(x, y, g);
                bestDistance = d;
            }
        return best;
    }

    /// <summary>What a search over one region found and how many of its places each rule turned down.</summary>
    public readonly record struct Result(Vector3? Found, int Cells, int NoTerrain, int TooLow, int NotLevel, int ObjectNear, int Plain);

    /// <summary>Like <see cref="Find"/>, and says why places were turned down.</summary>
    public static Result Search(Func<float, float, float?> groundAt, float waterHeight, float regionSize, IReadOnlyList<Vector3> objects,
        float minAboveWater, float clearRadius, float nearX, float nearY)
    {
        Vector3? best = null;
        float bestDistance = float.MaxValue;
        int cells = 0, noTerrain = 0, tooLow = 0, notLevel = 0, near = 0, plain = 0;
        for (float y = Margin; y <= regionSize - Margin; y += Step)
            for (float x = Margin; x <= regionSize - Margin; x += Step)
            {
                cells++;
                if (groundAt(x, y) is not float g) { noTerrain++; continue; }
                if (g < waterHeight + minAboveWater) { tooLow++; continue; }
                if (!Level(groundAt, x, y, g)) { notLevel++; continue; }
                if (objects.Any(o => Vector2.Distance(new Vector2(o.X, o.Y), new Vector2(x, y)) < clearRadius)) { near++; continue; }
                plain++;
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(nearX, nearY));
                if (d >= bestDistance) continue;
                best = new Vector3(x, y, g);
                bestDistance = d;
            }
        return new Result(best, cells, noTerrain, tooLow, notLevel, near, plain);
    }

    private static bool Level(Func<float, float, float?> groundAt, float x, float y, float g)
    {
        foreach (var (dx, dy) in new[] { (SlopeReach, 0f), (-SlopeReach, 0f), (0f, SlopeReach), (0f, -SlopeReach) })
            if (groundAt(x + dx, y + dy) is not float h || MathF.Abs(h - g) > MaxSlope) return false;
        return true;
    }
}
