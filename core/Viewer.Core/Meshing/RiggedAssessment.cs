// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Numerics;

namespace Viewer.Core.Meshing;

/// <summary>Why a rigged mesh worn on an avatar is not drawn (yet).</summary>
public enum RiggedWhy
{
    /// <summary>Nothing prevents drawing it.</summary>
    Drawable,
    /// <summary>The avatars' skeleton or joint textures do not exist yet (the body is not built). Temporary: the piece is presented again when they do.</summary>
    WaitingForBody,
    /// <summary>The asset has a skin block the reader refused or could not read; it cannot be skinned.</summary>
    SkinNotRead,
    /// <summary>The asset lists a different number of inverse bind matrices than joint names; the joint bindings are dropped.</summary>
    BindingsDropped,
    /// <summary>The skin block binds no joint.</summary>
    NoUsableJoints,
    /// <summary>The level of detail to draw has no weights (or was refused by the reader).</summary>
    LevelWithoutWeights,
    /// <summary>The level's weights do not describe the level's faces vertex for vertex.</summary>
    WeightsDoNotMatchFaces,
    /// <summary>All the joint-matrix tables the avatars share are in use. Temporary: presented again when a table is freed.</summary>
    TableAtlasFull,
}

/// <summary>
/// What decides whether a rigged mesh can be skinned, and which level of detail it is drawn at.
///
/// The rules follow the official viewer (read for behaviour only): a mesh is rigged when its skin info is known
/// (llvovolume.cpp LLVOVolume::isRiggedMesh); its LOD is computed from the wearer, not from the attachment
/// (llvovolume.cpp LLVOVolume::calcLOD, the "mDrawable->isState(RIGGED)" branch): the distance is the avatar's distance to the
/// camera and the radius is the diagonal of the avatar's animated bounding box.
/// </summary>
public static class RiggedAssessment
{
    /// <summary>The wearer's size used when the avatar's bounding box is not known: about the diagonal of a standard avatar's box.</summary>
    public const float DefaultWearerRadius = 2f;

    /// <summary>True for reasons that pass by themselves (the piece is presented again), false for those that stay until the asset changes.</summary>
    public static bool IsTemporary(RiggedWhy why) => why is RiggedWhy.WaitingForBody or RiggedWhy.TableAtlasFull;

    /// <summary>The LOD radius of a rigged mesh: the length of the wearer's bounding-box diagonal (box size in metres);
    /// <see cref="DefaultWearerRadius"/> when the box is empty or not a number.</summary>
    public static float WearerRadius(Vector3 boxSize)
    {
        float diagonal = boxSize.Length();
        return float.IsFinite(diagonal) && diagonal > 0f ? diagonal : DefaultWearerRadius;
    }

    /// <summary>Whether the asset is drawn through the skin (the reader read a full skin from it); an asset whose skin could not be read is drawn as a plain mesh at its attachment point.</summary>
    public static bool IsRigged(MeshGeometry g) => g.Skin?.Data?.Block is not null;

    /// <summary>The distance a rigged mesh takes its level of detail from: the camera to the wearer box (centre and size), 0.01 m at least.</summary>
    public static float BoxDistance(Vector3 camera, Vector3 centre, Vector3 size)
    {
        // the offset from the point to the nearest point of the box: nothing along an axis the point lies within
        var half = size * 0.5f;
        var offset = new Vector3(Outside(camera.X - centre.X, half.X), Outside(camera.Y - centre.Y, half.Y), Outside(camera.Z - centre.Z, half.Z));
        return Math.Max(0.01f, MathF.Floor(offset.Length() / 0.01f + 0.5f) * 0.01f);
    }

    private static float Outside(float from, float half) => from > half ? from - half : from < -half ? from + half : 0f;

    /// <summary>Whether level <paramref name="lod"/> of <paramref name="g"/> can be skinned from what the decoder read.</summary>
    public static RiggedWhy Assess(MeshGeometry g, int lod)
    {
        if (g.Skin?.Data is not { } data) return RiggedWhy.SkinNotRead;
        if (data.Block is not { } block) return RiggedWhy.SkinNotRead;
        if (block.BindingsDropped) return RiggedWhy.BindingsDropped;
        if (block.UsableJoints <= 0) return RiggedWhy.NoUsableJoints;
        if (lod < 0 || lod >= MeshGeometry.LodCount || g.Lods[lod] is not { } geometry || data.Lods[lod] is not { } influences) return RiggedWhy.LevelWithoutWeights;
        if (influences.Count != geometry.Faces.Count) return RiggedWhy.WeightsDoNotMatchFaces;
        for (int f = 0; f < influences.Count; f++)
            if (influences[f].Vertices != geometry.Faces[f].Vertices) return RiggedWhy.WeightsDoNotMatchFaces;
        return RiggedWhy.Drawable;
    }
}
