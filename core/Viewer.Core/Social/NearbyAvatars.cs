// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Session;

namespace Viewer.Core.Social;

/// <summary>One avatar in range: how far it is in metres, and whether that comes from the region's coarse list only.</summary>
public readonly record struct NearbyAvatar(Guid Id, float Distance, bool Approximate);

/// <summary>
/// Who is near the agent, nearest first. An avatar the region sends as an object is measured from its own position; one only in the
/// region's coarse-location list is measured from that, whose height is coarse and may be unknown. The range is the official
/// viewer's default (NearMeRange, 130 metres).
/// </summary>
public static class NearbyAvatars
{
    public const float DefaultRange = 130f;

    /// <summary>A coarse height of this or more means the height is not known.</summary>
    public const float UnknownHeight = 1020f;

    public static IReadOnlyList<NearbyAvatar> List(Guid self, Vector3 own, IEnumerable<(Guid Id, Vector3 Position)> exact,
        IReadOnlyList<CoarseAvatar> coarse, float range = DefaultRange)
    {
        var found = new Dictionary<Guid, NearbyAvatar>();
        foreach (var (id, at) in exact)
            found[id] = new NearbyAvatar(id, Vector3.Distance(own, at), false);
        foreach (var other in coarse)
        {
            if (found.ContainsKey(other.Id))
                continue;
            float distance = other.Position.Z >= UnknownHeight
                ? Vector2.Distance(new Vector2(own.X, own.Y), new Vector2(other.Position.X, other.Position.Y))
                : Vector3.Distance(own, other.Position);
            found[other.Id] = new NearbyAvatar(other.Id, distance, true);
        }
        found.Remove(self);
        found.Remove(Guid.Empty);
        return found.Values.Where(a => a.Distance <= range).OrderBy(a => a.Distance).ThenBy(a => a.Id).ToList();
    }
}
