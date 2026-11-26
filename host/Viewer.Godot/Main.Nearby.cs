// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Viewer.Core.Session;
using Viewer.Core.Social;

namespace Viewer.Godot;

/// <summary>Gives the People window's Nearby tab the avatars in range. It is asked only while the tab is on screen.</summary>
public partial class Main
{
    private IReadOnlyList<(NearbyAvatar Avatar, string Name)>? NearbyNow(ViewerSession session)
    {
        if (session.IsEnded || session.World.LastSelfMotion is not { } self)
            return null;
        var origin = session.World.OriginOffset;   // positions in the world are in the anchor region's frame; the list's are the region's own
        // a seated avatar's position is measured from its seat, so it is taken from the coarse list instead
        var exact = session.World.AvatarList().Where(a => a.ParentLocalId == 0).Select(a => (a.AgentId, a.Position - origin));
        return NearbyAvatars.List(session.AgentId, self.Motion.Position - origin, exact, session.CoarseLocations)
            .Select(a => (a, session.Names.Get(a.Id) ?? NameCache.Placeholder(a.Id)))
            .ToList();
    }
}
