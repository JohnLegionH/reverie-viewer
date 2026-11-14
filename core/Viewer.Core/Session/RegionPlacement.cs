// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Session;

/// <summary>One connected region of a session: its simulator, what is known of it, and the world its events go into.</summary>
/// <param name="Offset">Where the region's south-west corner lies relative to the current region's, in metres (x east, y north, z 0);
/// zero for the current region.</param>
/// <param name="OriginOffset">The same relative to the region the presentation is anchored on (the one the agent logged in to or
/// last teleported to). It does not change when the agent crosses, so what is drawn never moves.</param>
public sealed record RegionWorld(SimRef Sim, RegionInfo Region, Viewer.Core.World.WorldState World, bool IsCurrent, Vector3 Offset, Vector3 OriginOffset);

/// <summary>What changed among a session's regions.</summary>
public enum RegionWorldChangeKind { Added, Removed, Crossed }

/// <summary>A region came, went, or became the current one (<see cref="RegionWorldChangeKind.Crossed"/>: the agent walked into it).</summary>
public sealed record RegionWorldChange(RegionWorldChangeKind Kind, SimRef Sim);

/// <summary>
/// Where regions lie relative to each other, from their handles. A handle is the region's south-west corner in metres (x in the
/// high 32 bits, y in the low), so the arithmetic is exact for variable-size regions too; the size only matters for reach.
/// Behaviour reference: the world places a region by its handle (Firestorm and the official viewer, <c>LLWorld::addRegion</c>
/// in <c>llworld.cpp</c>); nothing is copied.
/// </summary>
public static class RegionPlacement
{
    /// <summary>The corner of <paramref name="to"/> relative to the corner of <paramref name="from"/>, in metres.</summary>
    public static Vector3 Offset(SimRef from, SimRef to) =>
        new((long)(to.Handle >> 32) - (long)(from.Handle >> 32), (long)(to.Handle & 0xFFFFFFFF) - (long)(from.Handle & 0xFFFFFFFF), 0);

    /// <summary>The distance in metres from <paramref name="point"/> (in the current region's frame) to the nearest point of a region
    /// whose corner is at <paramref name="offset"/> and that is <paramref name="sizeX"/> by <paramref name="sizeY"/> metres; 0 inside it.
    /// Height is ignored.</summary>
    public static float DistanceTo(Vector3 point, Vector3 offset, int sizeX, int sizeY)
    {
        float dx = Math.Max(Math.Max(offset.X - point.X, point.X - (offset.X + sizeX)), 0f);
        float dy = Math.Max(Math.Max(offset.Y - point.Y, point.Y - (offset.Y + sizeY)), 0f);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Whether such a region is no farther than <paramref name="drawDistance"/> from <paramref name="point"/>.</summary>
    public static bool InReach(Vector3 point, Vector3 offset, int sizeX, int sizeY, float drawDistance) =>
        DistanceTo(point, offset, sizeX, sizeY) <= drawDistance;
}
