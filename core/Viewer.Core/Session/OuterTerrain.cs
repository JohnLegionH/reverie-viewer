// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

/// <summary>
/// The ground just outside one region, read from the regions that lie there: what lets two regions' terrain meet in one surface. A
/// region's terrain grid asks for the height at a vertex of its own frame that falls outside it (one step past an edge); this answers
/// from the neighbour whose rectangle holds the same point. This follows the official viewer's design of joining the surfaces of
/// neighbouring regions so their edge patches use each other's edge heights (llsurface.cpp, connectNeighbor; llsurfacepatch.cpp,
/// updateNorthEdge and updateEastEdge); nothing is copied from it.
/// </summary>
public static class OuterTerrain
{
    private const int PatchSize = 16;

    /// <summary>Height in metres at grid vertex (<paramref name="gx"/>, <paramref name="gy"/>) of <paramref name="self"/>'s own frame, from
    /// another of <paramref name="regions"/> that covers the same point; null when none does or its patch has not arrived.</summary>
    public static float? HeightAt(IReadOnlyList<RegionWorld> regions, RegionWorld self, int gx, int gy)
    {
        // the point in the anchor's frame, then in each other region's own frame
        double x = self.OriginOffset.X + gx, y = self.OriginOffset.Y + gy;
        foreach (var other in regions)
        {
            if (ReferenceEquals(other.Sim, self.Sim))
                continue;
            int lx = (int)Math.Floor(x - other.OriginOffset.X), ly = (int)Math.Floor(y - other.OriginOffset.Y);
            if (lx < 0 || ly < 0 || lx >= other.Region.SizeX || ly >= other.Region.SizeY)
                continue;
            if (other.World.TryGetPatch(lx / PatchSize, ly / PatchSize, out var h) && h.Length >= PatchSize * PatchSize)
                return h[(ly % PatchSize) * PatchSize + lx % PatchSize];
        }
        return null;
    }

    /// <summary>Whether two regions share part of an edge (or a corner), so a change in one's edge heights moves the other's edge normals.</summary>
    public static bool Touches(RegionWorld a, RegionWorld b)
    {
        if (ReferenceEquals(a.Sim, b.Sim))
            return false;
        // rectangles in the anchor's frame; touching when they overlap or meet within one step
        double ax0 = a.OriginOffset.X, ay0 = a.OriginOffset.Y, ax1 = ax0 + a.Region.SizeX, ay1 = ay0 + a.Region.SizeY;
        double bx0 = b.OriginOffset.X, by0 = b.OriginOffset.Y, bx1 = bx0 + b.Region.SizeX, by1 = by0 + b.Region.SizeY;
        return ax0 <= bx1 + 1 && bx0 <= ax1 + 1 && ay0 <= by1 + 1 && by0 <= ay1 + 1;
    }
}
