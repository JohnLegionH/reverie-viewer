// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;

namespace Viewer.Shell.NameTags;

/// <summary>
/// Where an avatar's name tag is worked out from. The region sends a flying avatar's position only every 5 m or when its velocity
/// changes, so the stated position steps; the body is drawn at a smoothed position that moves evenly. A tag that hangs from the
/// stated position jumps against the body it names, so the tag is placed from the pose the body is drawn at (the official viewer
/// hangs the tag from the drawn skeleton's root: llvoavatar.cpp, idleCalcNameTagPosition).
/// </summary>
public static class NameTagPose
{
    /// <summary>The middle of the avatar's body box and its heading as the avatar is drawn now. An avatar the scene does not hold (or
    /// cannot place) keeps the pose the region states.</summary>
    public static (Vector3 Position, Quaternion Rotation) Of(AvatarScene? scene, AvatarState state)
    {
        if (scene is not null)
            foreach (var e in scene.Entries)
                if (e.AgentId == state.AgentId && scene.DrawnPose(e, out var centre, out var heading))
                    return (centre, heading);
        return (state.Position, state.Rotation);
    }
}
