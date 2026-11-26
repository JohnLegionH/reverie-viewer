// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Viewer.Core.Session;
using Viewer.Core.Diagnostics;
using Viewer.Core.Social;
using Viewer.Core.World;

namespace Viewer.Godot;

/// <summary>Gives the People window's Nearby tab the avatars in range. It is asked only while the tab is on screen.</summary>
public partial class Main
{
    private IReadOnlyList<(NearbyAvatar Avatar, string Name)>? NearbyNow(ViewerSession session)
    {
        if (session.IsEnded || session.World.LastSelfMotion is not { } motion)
            return null;
        // every region the session sees, the current one first; places are made relative to the current region's corner, as its coarse list is
        var worlds = session.Worlds;
        var exact = worlds.SelectMany(w => w.World.AvatarList()
            .Select(a => (a.AgentId, At: PlaceInRegion(w.World, a)))
            .Where(a => a.At is not null)
            .Select(a => (a.AgentId, a.At!.Value - w.OriginOffset + w.Offset)))
            .ToList();
        // the own place: seated, it is worked out from the seat like anyone's; standing, it is the agent's latest motion
        var seated = session.World.AvatarOf(session.AgentId) is { ParentLocalId: not 0 } me ? PlaceInRegion(session.World, me) : null;
        var own = (seated ?? motion.Motion.Position) - session.World.OriginOffset;
        var list = NearbyAvatars.List(session.AgentId, own, exact, session.CoarseLocations)
            .Select(a => (Avatar: a, Name: session.Names.Get(a.Id) ?? NameCache.Placeholder(a.Id)))
            .ToList();
#if NV_AUTOMATION
        // for a driven run, what the tab is given: counts, ids and distances, never a name
        if (Viewer.Automation.AutomationArgs.Enabled(global::Godot.OS.GetCmdlineUserArgs()))
            Log.Info("nearby.list", CoreLog.F("count", list.Count), CoreLog.F("rows", string.Join(";", list.Select(r =>
                r.Avatar.Id + "=" + r.Avatar.Distance.ToString("0.00", CultureInfo.InvariantCulture) + (r.Avatar.Approximate ? "~" : "")))));
#endif
        return list;
    }

    private static System.Numerics.Vector3? PlaceInRegion(WorldState world, AvatarState avatar) =>
        NearbyAvatars.WorldPosition(avatar.Position, avatar.ParentLocalId,
            id => world.Object(new ObjectId(avatar.Id.Epoch, id)) is { } o ? (o.Position, o.Rotation, o.ParentLocalId) : null);
}
