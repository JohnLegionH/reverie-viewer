// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.World;
using Viewer.Motion;

namespace Viewer.Shell;

/// <summary>
/// Lookups across the regions the viewer holds, for places given in the anchor region's frame (the frame the camera and the drawn avatars are
/// in; each world's <see cref="WorldState.OriginOffset"/> says where its region lies in it). The official viewer takes the ground under a
/// point from whichever region holds the point (llworld.cpp, LLWorld::resolveLandHeightGlobal, which answers 0 where no region holds it).
/// </summary>
public static class RegionGround
{
    /// <summary>The world whose region holds (<paramref name="x"/>, <paramref name="y"/>), or null.</summary>
    public static WorldState? WorldAt(IEnumerable<WorldState> worlds, float x, float y)
    {
        foreach (var w in worlds)
        {
            if (w.Region is not { } region) continue;
            var origin = w.OriginOffset;
            float lx = x - origin.X, ly = y - origin.Y;
            if (lx >= 0 && ly >= 0 && lx < region.SizeX && ly < region.SizeY) return w;
        }
        return null;
    }

    /// <summary>The ground height at the point, from the region that holds it (llworld.cpp, LLWorld::resolveLandHeightGlobal); 0 where
    /// no region holds the point. Between the region's samples the height is interpolated, as the land is drawn.</summary>
    public static float Height(IEnumerable<WorldState> worlds, float x, float y)
    {
        if (WorldAt(worlds, x, y) is not { } world) return 0f;
        var origin = world.OriginOffset;
        return TerrainHeight.Bilinear((ix, iy) => Sample(world, ix, iy), x - origin.X, y - origin.Y);
    }

    /// <summary>The height sample at whole metres of the region's own frame, the region's far edge taking its last sample; 0 where a patch
    /// has not arrived. Patches are 16 m square.</summary>
    private static float Sample(WorldState world, int x, int y)
    {
        int sx = world.Region?.SizeX ?? 256, sy = world.Region?.SizeY ?? 256;
        int ix = Math.Clamp(x, 0, sx - 1), iy = Math.Clamp(y, 0, sy - 1);
        return world.TryGetPatch(ix / 16, iy / 16, out var h) && h.Length >= 256 ? h[(iy % 16) * 16 + ix % 16] : 0f;
    }

    /// <summary>A region's constraint plane (n . p = w, given in that region's own frame) in the anchor frame: for a region lying
    /// <paramref name="originOffset"/> from the anchor, n . p_anchor = w + n . offset. No plane (all zero) stays none.</summary>
    public static Vector4 PlaneInAnchorFrame(Vector4 plane, Vector3 originOffset) =>
        plane == Vector4.Zero ? plane : new Vector4(plane.X, plane.Y, plane.Z, plane.W + plane.X * originOffset.X + plane.Y * originOffset.Y + plane.Z * originOffset.Z);
}
