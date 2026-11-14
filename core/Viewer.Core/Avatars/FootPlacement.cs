// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Avatars;

/// <summary>
/// Where the official viewer puts a joint of a standing avatar, for footsteps (llvoavatar.cpp updateFootstepSounds reads the foot
/// joints' world positions). The official skeleton hangs from the pelvis, whose own position is zeroed (llavatarappearance.cpp
/// buildSkeleton), and the pelvis is placed its pelvis-to-foot height (to the foot joint) above the soles (llvoavatar.cpp
/// updateCharacter), plus the hover. So the foot joints of any shape rest exactly on the soles, and the walk moves them from there.
/// </summary>
public static class FootPlacement
{
    /// <summary>The height of <paramref name="node"/> in the region's frame: <paramref name="soles"/> plus the node's height over the
    /// pelvis at rest in the pose <paramref name="joints"/> holds (begun, with the avatar's shape set; the clip's pelvis movement counts),
    /// plus <paramref name="pelvisAboveSoles"/>: the shape's pelvis-to-foot height (<see cref="BodyMeasure.PelvisToFoot"/>) with the
    /// hover and any worn mesh's pelvis offset.</summary>
    public static float NodeHeight(float soles, AvatarJoints joints, int node, float pelvisAboveSoles)
    {
        int pelvis = joints.Rig.Pelvis;
        var at = joints.Overrides is { } o && o.TryGet(pelvis, out var moved) ? moved : joints.Shape?.Position[pelvis] ?? joints.Rig.DefaultPosition[pelvis];
        float rest = joints.RootWorld().Translation.Z + at.Z;
        return soles + joints.WorldPosition(node).Z - rest + pelvisAboveSoles;
    }
}
