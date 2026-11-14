// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>How a face is blended with what is behind it.</summary>
public enum OutfitFaceAlpha : byte { Opaque, Mask, Blend }

/// <summary>
/// One face (one material slot) of a rigged mesh object: its own vertices, as the grid's mesh format keeps them, in the
/// avatar's root frame at the default pose (x forward, y left, z up, metres) with the mesh's bind shape already applied.
/// Every vertex carries four joint-list entries and four weights (unused slots weight 0; the weights add up to 1).
/// </summary>
public sealed class OutfitFace
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] Uvs { get; init; }
    public required int[] Indices { get; init; }

    /// <summary>Four entries of the mesh's joint list per vertex.</summary>
    public required byte[] Joints { get; init; }

    /// <summary>Four weights per vertex.</summary>
    public required float[] Weights { get; init; }

    public required OutfitFaceAlpha Alpha { get; init; }
    public required Vector3 Tint { get; init; }

    public int VertexCount => Positions.Length;
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// A rigged mesh object: its own joint list (skeleton joint indices, at most <see cref="MaxJoints"/>), the inverse bind
/// matrix of every list entry (row-vector convention: a default-pose vertex v skins to the weighted sum over its four
/// influences of v * InverseBind[i] * jointWorld[JointList[i]]) and its faces.
/// </summary>
public sealed class OutfitMesh
{
    /// <summary>The most joints one mesh may name.</summary>
    public const int MaxJoints = 110;

    public required string Name { get; init; }
    public required int[] JointList { get; init; }
    public required Matrix4x4[] InverseBind { get; init; }
    public required OutfitFace[] Faces { get; init; }

    public int VertexCount => Faces.Sum(f => f.VertexCount);
    public int TriangleCount => Faces.Sum(f => f.TriangleCount);
}

/// <summary>The rigged mesh objects one avatar wears.</summary>
public sealed class Outfit
{
    public required int Seed { get; init; }
    public required OutfitMesh[] Meshes { get; init; }

    public int VertexCount => Meshes.Sum(m => m.VertexCount);
    public int TriangleCount => Meshes.Sum(m => m.TriangleCount);
    public int FaceCount => Meshes.Sum(m => m.Faces.Length);

    /// <summary>The sum of the joint lists: the bone transforms one engine skeleton per mesh has to be given each frame.</summary>
    public int ListedJoints => Meshes.Sum(m => m.JointList.Length);
}
