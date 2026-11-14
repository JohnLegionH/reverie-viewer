// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Meshing;

/// <summary>
/// The skin block of a rigged mesh asset (the "skin" part of the asset), read in full. Matrices use the row-vector
/// convention of System.Numerics (a point p maps to Vector3.Transform(p, m); the translation is the fourth row), the
/// same as the wire's row-major 16-number lists.
/// </summary>
/// <param name="JointNames">The skeleton joints the mesh binds to, in the order the vertex joint indices refer to.
/// Empty when the asset's joint and inverse-bind counts disagree (see <see cref="BindingsDropped"/>).</param>
/// <param name="InverseBind">One inverse bind matrix per joint name.</param>
/// <param name="BindShape">Applied to a vertex before any joint matrix; identity when the asset has none.</param>
/// <param name="AlternateInverseBind">The optional alternate inverse bind matrices. Their translations are the joint
/// positions the mesh asks for (joint position overrides); they are never used to skin.</param>
/// <param name="PelvisOffset">The mesh's pelvis height offset in metres; 0 when absent.</param>
/// <param name="LockScaleIfJointPosition">When a mesh overrides a joint's position, also pin that joint's scale.</param>
/// <param name="BindingsDropped">True when the asset lists a different number of inverse bind matrices than joint names.
/// The official viewer then discards the joint bindings, and so does the reader.</param>
public sealed record MeshSkinBlock(
    IReadOnlyList<string> JointNames,
    IReadOnlyList<Matrix4x4> InverseBind,
    Matrix4x4 BindShape,
    IReadOnlyList<Matrix4x4> AlternateInverseBind,
    float PelvisOffset,
    bool LockScaleIfJointPosition,
    bool BindingsDropped)
{
    /// <summary>The most joints one mesh object may use (the official viewer's per-mesh limit); entries past it are not used.</summary>
    public const int MaxJoints = 110;

    public int UsableJoints => Math.Min(JointNames.Count, MaxJoints);

    /// <summary>True when the mesh carries joint position overrides it can apply: one alternate matrix per joint name.</summary>
    public bool HasJointOverrides => AlternateInverseBind.Count > 0 && AlternateInverseBind.Count == JointNames.Count;
}

/// <summary>
/// The skinning influences of one face: four slots per vertex (unused slots have weight 0 and joint 0).
/// Weights are normalised to add up to 1 per vertex, as the official viewer's skinning does when it uses them; joint
/// indices are clamped into the mesh's joint list.
/// </summary>
/// <param name="Repaired">How many vertices had to be repaired: no influence at all (bound fully to joint 0), or a joint
/// index beyond the joint list (clamped to its last entry).</param>
/// <param name="Unmatched">Weight records missing for the face's vertices, plus whole records left over after them (the official unpack only warns).</param>
public sealed record FaceInfluences(int Vertices, byte[] Joints, float[] Weights, int Repaired, int Unmatched = 0);

/// <summary>Everything a rigged mesh asset says about its skinning, per level of detail.</summary>
/// <param name="Block">The skin block; null when the asset has none or it was refused (<see cref="BlockProblem"/>).</param>
/// <param name="Lods">By LOD index (0 lowest .. 3 high, as in <see cref="MeshGeometry"/>): the faces' influences, in
/// face order; null for a LOD the asset lacks, has no weights for, or that was refused (<see cref="LodProblems"/>).
/// A face with no geometry has zero vertices.</param>
public sealed record MeshSkinData(MeshSkinBlock? Block, string? BlockProblem, IReadOnlyList<IReadOnlyList<FaceInfluences>?> Lods, IReadOnlyList<string?> LodProblems)
{
    /// <summary>True when the asset is rigged: it has a skin block.</summary>
    public bool Rigged => Block is not null;
}
