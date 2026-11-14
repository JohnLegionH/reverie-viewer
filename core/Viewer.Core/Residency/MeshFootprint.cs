// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Meshing;

namespace Viewer.Core.Residency;

/// <summary>
/// A mesh LOD's GPU bytes as uploaded, our estimate of the engine's vertex and index buffers (the
/// counterpart of <see cref="TextureFootprint"/>): per vertex a position (3 floats, 12 bytes), a normal packed into 4 bytes
/// and a UV (2 floats, 8 bytes); per index 2 bytes when its surface has at most 65,535 vertices, else 4.
/// </summary>
public static class MeshFootprint
{
    public const int BytesPerVertex = 12 + 4 + 8;

    public static long Bytes(IReadOnlyList<MeshFace> faces)
    {
        long b = 0;
        foreach (var f in faces)
            b += (long)f.Vertices * BytesPerVertex + (long)f.Indices.Length * (f.Vertices <= ushort.MaxValue ? 2 : 4);
        return b;
    }

    /// <summary>The bytes of a decoded mesh's arrays as the mesh source keeps them in memory (every
    /// LOD: positions, normals and UVs as floats, indices as ints; array and object headers left out).</summary>
    public static long DecodedBytes(MeshGeometry geometry)
    {
        long b = 0;
        foreach (var lod in geometry.Lods)
            if (lod is not null)
                foreach (var f in lod.Faces)
                    b += 4L * (f.Positions.Length + f.Normals.Length + f.Uvs.Length + f.Indices.Length);
        // a rigged mesh's influences (a joint byte and a weight float for each of four slots a vertex) and its matrices
        if (geometry.Skin?.Data is { } skin)
        {
            foreach (var lod in skin.Lods)
                if (lod is not null)
                    foreach (var f in lod)
                        b += f.Joints.LongLength + 4L * f.Weights.Length;
            if (skin.Block is { } block)
                b += 64L * (block.InverseBind.Count + block.AlternateInverseBind.Count + 1);
        }
        return b;
    }
}
