// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Meshing;

namespace Viewer.Core.Avatars;

/// <summary>
/// What the vertex shader that skins a worn rigged mesh reads, packed as floats, and a C# model of that shader. Engine-free.
///
/// Two textures feed the shader. An avatar has a row of its nodes' world matrices (<see cref="PackBones"/>; one more entry
/// after the last node for the avatar root), refreshed every frame the avatar is posed. A mesh has a table (<see cref="BuildTable"/>),
/// made once from its skin block: for each joint it lists, the bind shape matrix times the inverse bind matrix, and the
/// node of the avatar rig the joint is. A vertex carries up to four (joint index, weight) pairs; the shader looks each joint's node up in the
/// table, takes that node's world matrix from the avatar's row, and blends the products: the same sum as
/// <see cref="AttachmentPlacement.SkinPosition"/>, with the bind shape folded into the table (the weights add up to one, so
/// moving the bind shape inside the sum changes nothing).
///
/// A matrix is three texels of four floats: the first three rows of its column-vector form in the engine's axes (SL's
/// (x, y, z) is the engine's (x, z, -y), as the surface encoder turns vertices), the translation in the fourth channel. The
/// matrices keep the row-vector convention of System.Numerics on the way in.
/// </summary>
public static class SkinTables
{
    public const int TexelsPerMatrix = 3;
    public const int FloatsPerMatrix = TexelsPerMatrix * 4;

    // SL point -> engine point, row-vector form: x stays, y goes to -z, z goes to y
    private static readonly Matrix4x4 ToEngine = new(
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);

    private static readonly Matrix4x4 FromEngine = Matrix4x4.Transpose(ToEngine);   // a rotation: its inverse is its transpose

    /// <summary>Writes a row-vector SL-axes matrix as <see cref="FloatsPerMatrix"/> floats in the engine's axes.</summary>
    public static void PackMatrix(in Matrix4x4 sl, Span<float> texels)
    {
        if (texels.Length < FloatsPerMatrix) throw new ArgumentException("span shorter than a packed matrix");
        // The same transform seen in the engine's axes: FromEngine * sl * ToEngine, where the axis change is a signed permutation of the
        // axes (x, y, z to x, z, -y), so each entry is one entry of sl moved and, for the y axis, negated. Written out, it costs twelve
        // moves instead of two 4x4 products (a row of joint matrices is packed for every avatar every frame it is posed); the test
        // "Packing_a_matrix_for_the_engine_matches_the_axis_change_by_multiplication" holds it to the product.
        texels[0] = sl.M11; texels[1] = sl.M31; texels[2] = -sl.M21; texels[3] = sl.M41;
        texels[4] = sl.M13; texels[5] = sl.M33; texels[6] = -sl.M23; texels[7] = sl.M43;
        texels[8] = -sl.M12; texels[9] = -sl.M32; texels[10] = sl.M22; texels[11] = -sl.M42;
    }

    /// <summary>An avatar's row: the world matrix of each node and, last, of the avatar root, as
    /// <see cref="AvatarJoints.Fill"/> gives them.</summary>
    public static void PackBones(ReadOnlySpan<Matrix4x4> nodeWorldAndRoot, Span<float> row)
    {
        if (row.Length < nodeWorldAndRoot.Length * FloatsPerMatrix) throw new ArgumentException("row shorter than the matrices");
        for (int i = 0; i < nodeWorldAndRoot.Length; i++) PackMatrix(nodeWorldAndRoot[i], row.Slice(i * FloatsPerMatrix, FloatsPerMatrix));
    }

    /// <summary>A mesh's table: for each of the skin's usable joints, its matrix (bind shape, then inverse bind) in
    /// <paramref name="matrices"/> (<see cref="FloatsPerMatrix"/> floats each) and its node in <paramref name="nodes"/> (the rig's node,
    /// mPelvis for a name the skeleton does not know as the official viewer repairs it, the entry after the last node for mRoot).
    /// Returns the joints written.</summary>
    public static int BuildTable(AvatarRig rig, MeshSkinBlock skin, Span<float> matrices, Span<float> nodes)
    {
        int n = skin.UsableJoints;
        if (matrices.Length < n * FloatsPerMatrix || nodes.Length < n) throw new ArgumentException("spans shorter than the skin's usable joints");
        for (int i = 0; i < n; i++)
        {
            PackMatrix(skin.BindShape * skin.InverseBind[i], matrices.Slice(i * FloatsPerMatrix, FloatsPerMatrix));
            int node = rig.FindForSkinning(skin.JointNames[i]);
            nodes[i] = node < 0 ? rig.NodeCount : node;   // AvatarRig.Root, and a rig with no pelvis: the root
        }
        return n;
    }

    /// <summary>The vertex shader in C#: one vertex (engine axes) through its four influences. <paramref name="joints"/> are indices into the mesh's
    /// joint list, <paramref name="boneRow"/> the avatar's row, <paramref name="matrices"/> and <paramref name="nodes"/> the mesh's table.
    /// A weight of zero is skipped, as in the shader.</summary>
    public static Vector3 SkinEngine(Vector3 vertex, ReadOnlySpan<float> joints, ReadOnlySpan<float> weights, ReadOnlySpan<float> boneRow, ReadOnlySpan<float> matrices, ReadOnlySpan<float> nodes)
    {
        var sum = Vector3.Zero;
        for (int k = 0; k < 4; k++)
        {
            if (!(weights[k] > 0f)) continue;
            int j = (int)(joints[k] + 0.5f);
            int node = (int)(nodes[j] + 0.5f);
            var world = boneRow.Slice(node * FloatsPerMatrix, FloatsPerMatrix);
            var bind = matrices.Slice(j * FloatsPerMatrix, FloatsPerMatrix);
            // the point through the bind matrix, then the world matrix (column-vector form: world * bind * point)
            var p = Apply(bind, vertex);
            sum += weights[k] * Apply(world, p);
        }
        return sum;
    }

    private static Vector3 Apply(ReadOnlySpan<float> m, Vector3 v) => new(
        m[0] * v.X + m[1] * v.Y + m[2] * v.Z + m[3],
        m[4] * v.X + m[5] * v.Y + m[6] * v.Z + m[7],
        m[8] * v.X + m[9] * v.Y + m[10] * v.Z + m[11]);
}
