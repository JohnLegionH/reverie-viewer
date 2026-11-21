// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Godot.Presentation;

/// <summary>Where the avatars' attachment points are, for the layer that draws what they wear. Main thread only.</summary>
public interface IAttachmentFrames
{
    /// <summary>The attachment point's frame in the region (SL axes, row vectors, metres) as the avatar with simulator local id
    /// <paramref name="wearerLocalId"/> is posed now. False while the avatar is not drawn, or when the skeleton has no such point.</summary>
    bool WearerHeld(uint wearerLocalId);

    /// <summary>Whether the avatar with simulator local id <paramref name="wearerLocalId"/> is drawn here and is not the user's own (its pieces removed in a
    /// burst are held, <see cref="Viewer.Core.Avatars.WornRemovalHold"/>).</summary>
    bool WearerIsOther(uint wearerLocalId);

    bool TryPoint(uint wearerLocalId, int attachmentPointId, out System.Numerics.Matrix4x4 world);

    /// <summary>Which drawn skeleton the wearer's pieces are bound to: an identity for the layer that draws it and the avatar's row in that layer. Two
    /// calls that give equal values name the same skeleton, and so the same pose. False while the avatar is not drawn.</summary>
    bool TrySkeleton(uint wearerLocalId, out object layer, out int row);

    /// <summary>The skeleton the avatars are drawn with; null until it is read.</summary>
    Viewer.Core.Avatars.AvatarRig? Rig { get; }

    /// <summary>The textures the skinning of worn rigged meshes reads; null until the first avatar is drawn.</summary>
    SkinTextures? SkinTextures { get; }

    /// <summary>The object <paramref name="local"/>, worn by the avatar with simulator local id <paramref name="wearerLocalId"/>, is a rigged mesh
    /// <paramref name="asset"/>: its joint overrides and pelvis offset apply to the avatar, which gets a row of joint matrices. False while the
    /// avatar is not drawn (the caller asks again), or when no row is left. Asking again for an object already registered changes nothing.</summary>
    bool WearRigged(uint wearerLocalId, uint local, System.Guid asset, Viewer.Core.Meshing.MeshSkinBlock skin);

    /// <summary>The object no longer wears a rigged mesh (detached, or the mesh changed).</summary>
    void UnwearRigged(uint wearerLocalId, uint local);

    /// <summary>The avatar's row of joint matrices and the place its rigged meshes are skinned into (SL axes, row vectors, metres), as it is posed
    /// now. False while the avatar is not drawn or wears no rigged mesh.</summary>
    bool TryRigged(uint wearerLocalId, out int row, out System.Numerics.Matrix4x4 world);
}
