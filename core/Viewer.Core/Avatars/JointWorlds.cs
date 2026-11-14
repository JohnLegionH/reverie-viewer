// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>
/// World matrices for every node of an <see cref="AvatarRig"/> (joints, collision volumes, attachment points) from a
/// local position and rotation per node. The frame is the avatar root's: mRoot at the origin, mPelvis at its own
/// position above it (the official skeleton's hierarchy). Row-vector convention (System.Numerics): a point p in a node's own
/// frame is at Vector3.Transform(p, world[node]) in the root frame.
///
/// <see cref="AvatarPose"/>, which only feeds the stock body, measures from the pelvis instead (it takes the default pelvis
/// height off the root entry); a caller that mixes the two translates by that height. Joint scale on bones is not applied
/// (the skeleton file gives every bone a scale of one); how a scaled joint scales its children's offsets is shape work.
/// Allocation-free.
/// </summary>
public static class JointWorlds
{
    /// <summary>The node's local transform: scale, then rotation, then its offset from the parent.</summary>
    public static Matrix4x4 Local(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        var m = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation);
        m.Translation = position;
        return m;
    }

    /// <summary>Fills <paramref name="world"/> (one entry per node) from per-node local positions and rotations.</summary>
    public static void Compute(AvatarRig rig, ReadOnlySpan<Vector3> position, ReadOnlySpan<Quaternion> rotation, Span<Matrix4x4> world)
    {
        int n = rig.NodeCount;
        if (position.Length < n || rotation.Length < n || world.Length < n) throw new ArgumentException("spans shorter than the rig's node count");
        for (int i = 0; i < n; i++)
        {
            var local = Local(position[i], rotation[i], rig.DefaultScale[i]);
            int p = rig.Parent(i);
            world[i] = p < 0 ? local : local * world[p];   // parents come first
        }
    }

    /// <summary>The rest pose: every node at its default position and rotation.</summary>
    public static void Rest(AvatarRig rig, Span<Matrix4x4> world) => Compute(rig, rig.DefaultPosition, rig.DefaultRotation, world);

    /// <summary>The world matrix of a node as <see cref="AvatarRig.Find"/> names it: the root is the identity.</summary>
    public static Matrix4x4 Of(int node, ReadOnlySpan<Matrix4x4> world) => node == AvatarRig.Root ? Matrix4x4.Identity : world[node];
}
