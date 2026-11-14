// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Runtime.InteropServices;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;

namespace Viewer.Shell;

/// <summary>A skinned surface: the engine's usual surface data plus its skin data (bones and weights per vertex).</summary>
public sealed record PreparedSkinnedSurface(PreparedSurface Surface, byte[] SkinData, int Bones);

/// <summary>
/// A body part's surface in the engine's layout, made off the main thread. The vertex, attribute and index data are the
/// viewer's usual encoding (<see cref="MeshSurfaceData.Encode(MeshFace)"/>: SL axes to the engine's, winding reversed, UVs);
/// the format adds bones and weights, and the skin data holds, for every vertex, four 16-bit bone indices into the
/// avatar's palette then four 16-bit weights (each times 65535, clamped, truncated): the body binds two bones a vertex,
/// the other two are bone 0 at weight 0. The layout is checked byte for byte against the engine's own packing by the
/// render harness (--tr-avatarprep).
/// </summary>
public static class AvatarSurfaceData
{
    public const ulong FormatBones = 0x400UL, FormatWeights = 0x800UL;
    public const int SkinStride = 16;

    public static PreparedSkinnedSurface Encode(BodyPart part, int paletteSize) => Encode(part, null, paletteSize);

    /// <summary>A body part with its shape morphs applied (<see cref="BodyMorph.Compute"/>); null: the part as the body has it.</summary>
    public static PreparedSkinnedSurface Encode(BodyPart part, MorphedPart? morphed, int paletteSize)
    {
        int n = part.Mesh.VertexCount;
        var positions = new float[n * 3];
        var normals = new float[n * 3];
        var uvs = new float[n * 2];
        var pos = morphed?.Positions ?? part.Positions;
        var nrm = morphed?.Normals ?? part.Normals;
        var uv = morphed?.Uvs ?? part.Mesh.Uvs;
        for (int i = 0; i < n; i++)
        {
            positions[i * 3] = pos[i].X; positions[i * 3 + 1] = pos[i].Y; positions[i * 3 + 2] = pos[i].Z;
            normals[i * 3] = nrm[i].X; normals[i * 3 + 1] = nrm[i].Y; normals[i * 3 + 2] = nrm[i].Z;
            uvs[i * 2] = uv[i].X; uvs[i * 2 + 1] = uv[i].Y;
        }
        var indices = part.Mesh.Indices.Select(x => (int)x).ToArray();
        return Encode(new MeshFace(positions, normals, uvs, indices), part.BoneA, part.BoneB, part.WeightB, paletteSize, part.Type);
    }

    /// <summary>Any skinned face (SL axes and winding): per vertex two palette entries and the second one's weight.</summary>
    public static PreparedSkinnedSurface Encode(MeshFace face, int[] boneA, int[] boneB, float[] weightB, int paletteSize, string name = "face")
    {
        int n = face.Positions.Length / 3;
        var s = MeshSurfaceData.Encode(face);
        var skin = new byte[n * SkinStride];
        var w = MemoryMarshal.Cast<byte, ushort>(skin.AsSpan());
        for (int i = 0; i < n; i++)
        {
            int a = boneA[i], b = boneB[i];
            if (a < 0 || a >= paletteSize || b < 0 || b >= paletteSize) throw new InvalidDataException($"{name} vertex {i}: bone outside the palette of {paletteSize}");
            float wb = weightB[i];
            w[i * 8] = (ushort)a; w[i * 8 + 1] = (ushort)b;
            w[i * 8 + 4] = Unorm16(1f - wb); w[i * 8 + 5] = Unorm16(wb);
        }
        return new PreparedSkinnedSurface(s with { Format = s.Format | FormatBones | FormatWeights }, skin, paletteSize);
    }

    private static ushort Unorm16(float v)
    {
        float s = v * 65535f;
        return (ushort)(s < 0f ? 0f : s > 65535f ? 65535f : s);
    }
}
