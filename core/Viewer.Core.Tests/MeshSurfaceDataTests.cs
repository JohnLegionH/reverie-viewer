// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using Viewer.Core.Meshing;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The surface encoder's options, in plain .NET. Byte-for-byte agreement with the engine is proved
/// by the render harness (--tr-meshprep reads every case back from the engine); these tests pin the options' own rules.
/// </summary>
public class MeshSurfaceDataTests
{
    private static readonly float[] Positions = [1, 2, 3, 4, 5, 6, 7, 8, 9];
    private static readonly float[] Normals = [0, 0, 1, 0, 1, 0, 1, 0, 0];
    private static readonly int[] Indices = [0, 1, 2];

    [Fact]
    public void A_mesh_face_is_the_general_encoding_in_SL_axes_with_the_winding_reversed()
    {
        var face = new MeshFace(Positions, Normals, [0, 0, 1, 0, 0, 1], Indices);
        var a = MeshSurfaceData.Encode(face);
        var b = MeshSurfaceData.Encode(Positions, Normals, Indices, face.Uvs, colors: null, slAxes: true, reverseWinding: true);
        Assert.Equal(a.VertexData, b.VertexData);
        Assert.Equal(a.AttributeData, b.AttributeData);
        Assert.Equal(a.IndexData, b.IndexData);
        Assert.Equal(MeshSurfaceData.Format, a.Format);
        Assert.Equal(new ushort[] { 0, 2, 1 }, U16(a.IndexData));
        // SL (x, y, z) -> engine (x, z, -y)
        Assert.Equal(new float[] { 1, 3, -2 }, F32(a.VertexData, 0, 3));
    }

    [Fact]
    public void Engine_axes_and_winding_are_kept_as_given()
    {
        var s = MeshSurfaceData.Encode(Positions, Normals, Indices, uvs: null, colors: null, slAxes: false, reverseWinding: false);
        Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, F32(s.VertexData, 0, 9));
        Assert.Equal(new ushort[] { 0, 1, 2 }, U16(s.IndexData));
        Assert.Empty(s.AttributeData);
        Assert.Equal(MeshSurfaceData.FormatVertex | MeshSurfaceData.FormatNormal | MeshSurfaceData.FormatTangent | MeshSurfaceData.FormatIndex | MeshSurfaceData.FormatVersion, s.Format);
        Assert.Equal(new float[] { 1, 2, 3, 6, 6, 6 }, s.Aabb);
    }

    [Fact]
    public void A_colour_is_four_truncated_clamped_bytes_before_the_UV_in_each_vertex()
    {
        float[] colors = [1f, 0.5f, 0f, 2f, -1f, 0.999f, 0.0039f, 0.25f, 0.2f, 0.4f, 0.6f, 0.8f];
        float[] uvs = [0.25f, 0f, 1f, 0f, 3f, 0f];
        var s = MeshSurfaceData.Encode(Positions, Normals, Indices, uvs, colors, slAxes: false, reverseWinding: false);
        Assert.Equal(3 * 12, s.AttributeData.Length);
        Assert.Equal(new byte[] { 255, 127, 0, 255 }, s.AttributeData[0..4]);
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(s.AttributeData.AsSpan(4)));
        Assert.Equal(new byte[] { 0, 254, 0, 63 }, s.AttributeData[12..16]);
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(s.AttributeData.AsSpan(16)));
        Assert.Equal(new byte[] { 51, 102, 153, 204 }, s.AttributeData[24..28]);
        Assert.Equal(MeshSurfaceData.FormatColor | MeshSurfaceData.FormatTexUV, s.Format & (MeshSurfaceData.FormatColor | MeshSurfaceData.FormatTexUV));
    }

    [Fact]
    public void Colours_without_UVs_are_four_bytes_a_vertex()
    {
        var s = MeshSurfaceData.Encode(Positions, Normals, Indices, uvs: null, colors: new float[12], slAxes: false, reverseWinding: false);
        Assert.Equal(12, s.AttributeData.Length);
        Assert.Equal(0UL, s.Format & MeshSurfaceData.FormatTexUV);
    }

    private static ushort[] U16(byte[] b) => Enumerable.Range(0, b.Length / 2).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i * 2))).ToArray();
    private static float[] F32(byte[] b, int from, int count) => Enumerable.Range(from, count).Select(i => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(i * 4))).ToArray();
}
