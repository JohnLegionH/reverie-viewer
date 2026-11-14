// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Meshing;

namespace Viewer.Core.Avatars;

/// <summary>
/// Where attached things go, from the avatar's node world matrices (<see cref="JointWorlds"/>). Engine-free.
///
/// What the later drawing sessions consume:
///   1. Per avatar, once per change of pose or overrides: <see cref="JointOverrides.Apply"/> gives the local positions,
///      <see cref="JointWorlds.Compute"/> the node world matrices (root frame).
///   2. A rigid attachment: <see cref="Rigid"/>, one matrix per attached object, from its attachment point id and the
///      object's own offset and rotation; several objects on one point each get their own and share the point's node.
///   3. A rigged mesh: <see cref="ResolveJoints"/> once per mesh and avatar, then <see cref="Palette"/> per frame, then
///      <see cref="SkinPosition"/> per vertex (or the same maths in a shader). The result is in the root frame; the draw
///      code puts the avatar's own transform on top, raised by <see cref="JointOverrides.PelvisFixup"/>.
///
/// Behaviour as the official viewer's (read for behaviour: LLViewerJointAttachment, LLSkinningUtil.initSkinningMatrixPalette
/// and getPerVertexSkinMatrix; the SL wiki "Mesh" and "Avatar Attachment Points"): an attachment point is a joint of its own
/// under the joint it names; an attached object sits in that joint's frame, placed by the position and rotation the object
/// update states; a skin joint's matrix is its inverse bind matrix times the joint's world matrix; a vertex is moved by the
/// bind shape matrix first, then by the weighted sum of its (up to four) joints' matrices.
/// </summary>
public static class AttachmentPlacement
{
    /// <summary>The object's own transform inside its attachment point's frame (rotation, then offset).</summary>
    public static Matrix4x4 ObjectInPoint(Vector3 position, Quaternion rotation)
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
        m.Translation = position;
        return m;
    }

    /// <summary>The bit the simulator sets on an attachment point id for an object worn in addition to what the point holds.</summary>
    public const int AddFlag = 0x80;

    /// <summary>The attachment point's id an object names, with the add flag taken off (the official viewer clears it:
    /// LLVOAvatar::getTargetAttachmentPoint).</summary>
    public static int PointId(int rawPoint) => rawPoint & ~AddFlag;

    /// <summary>The node an object worn on <paramref name="rawPoint"/> hangs from: the point the id names, or, for an id the
    /// skeleton does not have, the chest (point 1), as the official viewer does; -1 when the skeleton has neither. A HUD point's
    /// node is returned as it is (<see cref="AvatarRig.IsScreen"/> says it is not on the body).</summary>
    public static int TargetNode(AvatarRig rig, int rawPoint)
    {
        int node = rig.PointNode(PointId(rawPoint));
        return node >= 0 ? node : rig.PointNode(1);
    }

    /// <summary>The root-frame matrix of a rigid attached object (its own frame to the avatar root's), or null when the
    /// skeleton has no such attachment point. HUD points hang off the screen and are not placed on the body (null).</summary>
    public static Matrix4x4? Rigid(AvatarRig rig, int attachmentPointId, ReadOnlySpan<Matrix4x4> nodeWorld, Vector3 position, Quaternion rotation)
    {
        int node = rig.PointNode(attachmentPointId);
        if (node < 0 || rig.IsScreen[node]) return null;
        return ObjectInPoint(position, rotation) * nodeWorld[node];
    }

    /// <summary>The node each of a skin's usable joints binds to: the joint's node, the root, or, for a name the skeleton
    /// does not know, mPelvis. <paramref name="nodes"/> needs at least <see cref="MeshSkinBlock.UsableJoints"/> entries.
    /// Returns how many were written.</summary>
    public static int ResolveJoints(AvatarRig rig, MeshSkinBlock skin, Span<int> nodes)
    {
        int n = skin.UsableJoints;
        if (nodes.Length < n) throw new ArgumentException("span shorter than the skin's usable joints");
        for (int i = 0; i < n; i++) nodes[i] = rig.FindForSkinning(skin.JointNames[i]);
        return n;
    }

    /// <summary>Each skin joint's matrix: its inverse bind matrix times the joint's world matrix. The bind shape matrix is
    /// not folded in (it applies to the vertex before the blend; see <see cref="SkinPosition"/>).</summary>
    public static void Palette(MeshSkinBlock skin, ReadOnlySpan<int> nodes, ReadOnlySpan<Matrix4x4> nodeWorld, Span<Matrix4x4> palette)
    {
        int n = Math.Min(skin.UsableJoints, nodes.Length);
        if (palette.Length < n) throw new ArgumentException("palette shorter than the joints");
        for (int i = 0; i < n; i++)
            palette[i] = skin.InverseBind[i] * (nodes[i] < 0 ? Matrix4x4.Identity : nodeWorld[nodes[i]]);
    }

    /// <summary>One vertex through the skin: the bind shape first, then the four influences' palette matrices, blended by
    /// their weights (which <see cref="MeshSkinReader"/> already normalised to add up to one).</summary>
    public static Vector3 SkinPosition(Vector3 vertex, in Matrix4x4 bindShape, ReadOnlySpan<Matrix4x4> palette, ReadOnlySpan<byte> joints, ReadOnlySpan<float> weights)
    {
        var v = Vector3.Transform(vertex, bindShape);
        var sum = Vector3.Zero;
        for (int k = 0; k < 4; k++)
        {
            if (weights[k] == 0f) continue;
            sum += Vector3.Transform(v, palette[Math.Min(joints[k], palette.Length - 1)]) * weights[k];
        }
        return sum;
    }
}
