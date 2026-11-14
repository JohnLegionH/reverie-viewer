// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>
/// Interest management (map section 6 item 5-6, web gateway bb91ba5): the simulator culls what it sends by the draw
/// distance the viewer reports (AgentUpdate camera far), clamped to 64-1024 m, default 1024 as the gateway's; HUD
/// attachments (points 31-38) never enter the 3D world; only our own region's events are applied (the first-burst rule
/// and epoch check). The viewer-side query below is what the presentation draws: objects whose linkset root (or, for an
/// attachment, whose wearer) lies within the draw distance of the camera.
/// </summary>
public static class Interest
{
    public const float MinDrawDistance = 64f;
    public const float MaxDrawDistance = 1024f;
    public const float DefaultDrawDistance = 128f;

    public static float ClampDrawDistance(float metres) =>
        float.IsNaN(metres) ? DefaultDrawDistance : Math.Clamp(metres, MinDrawDistance, MaxDrawDistance);

    /// <summary>HUD attachment points (31-38) are screen-space, never world objects.</summary>
    public static bool IsHudPoint(int attachmentPoint) => attachmentPoint is >= 31 and <= 38;

    /// <summary>The objects within <paramref name="drawDistance"/> of <paramref name="camera"/>, judged by linkset root or wearer.</summary>
    public static IReadOnlyList<ObjectState> InRange(WorldState world, Vector3 camera, float drawDistance)
    {
        var snapshot = world.Snapshot();
        float d2 = drawDistance * drawDistance;
        var result = new List<ObjectState>();
        foreach (var o in snapshot.Objects.Values)
        {
            var anchor = AnchorPosition(snapshot, o);
            if (anchor is { } p && Vector3.DistanceSquared(p, camera) <= d2)
                result.Add(o);
        }
        return result;
    }

    /// <summary>Region position of the thing that places <paramref name="o"/>: its root prim, or its wearer. Null if unresolved.</summary>
    private static Vector3? AnchorPosition(WorldSnapshot s, ObjectState o)
    {
        var cur = o;
        for (int hops = 0; hops < 256; hops++)
        {
            if (cur.ParentLocalId == 0)
                return cur.Position;
            if (s.Objects.TryGetValue(cur.ParentLocalId, out var parent))
            {
                cur = parent;
                continue;
            }
            if (s.Avatars.TryGetValue(cur.ParentLocalId, out var wearer))
                return wearer.Position;
            return null;   // parent not known yet (reordered delivery): not drawn until it is
        }
        return null;
    }
}
