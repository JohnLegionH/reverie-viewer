// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using Viewer.Core.Meshing;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A rigged mesh face in the engine's surface layout: the ordinary face (<see cref="MeshSurfaceData.Encode(MeshFace)"/>) with its
/// four joint indices and four weights a vertex added to the attribute data as two more custom channels of four half floats
/// each, after the UVs (the engine's attribute order is colour, UV, second UV, then the custom channels). The expected
/// bytes are worked by hand from that layout. Pure, runs in parallel.
/// </summary>
public class SkinnedSurfaceDataTests
{
    private static MeshFace Triangle() => new([0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 0, 1, 0, 0, 1, 0, 0, 1], [0, 0, 1, 0, 0, 1], [0, 1, 2]);

    private static FaceInfluences Influences() => new(3, [1, 2, 0, 0, 5, 0, 0, 0, 7, 0, 0, 0], [0.25f, 0.75f, 0, 0, 1, 0, 0, 0, 0.5f, 0.5f, 0, 0], 0);

    [Fact]
    public void The_format_names_the_two_custom_channels_as_four_half_floats_each()
    {
        // the engine's flags: custom 0 is bit 6 and custom 1 bit 7; each channel's format sits in three bits, custom 0 from bit 13 and
        // custom 1 from bit 16; "RGBA half" is format 3
        var plain = MeshSurfaceData.Encode(Triangle());
        var skinned = SkinnedSurfaceData.Encode(Triangle(), Influences());
        ulong added = (1UL << 6) | (1UL << 7) | (3UL << 13) | (3UL << 16);
        Assert.Equal(plain.Format | added, skinned.Format);
    }

    [Fact]
    public void Each_vertex_keeps_its_uvs_then_gets_its_four_joints_and_four_weights_as_half_floats()
    {
        var s = SkinnedSurfaceData.Encode(Triangle(), Influences());
        Assert.Equal(3 * 24, s.AttributeData.Length);   // 8 bytes of UV, 8 of joints, 8 of weights a vertex
        // vertex 1: UV (1, 0) as two floats, joints (5, 0, 0, 0), weights (1, 0, 0, 0)
        var v = s.AttributeData.AsSpan(24);
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(v));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(v[4..]));
        Assert.Equal((Half)5, BinaryPrimitives.ReadHalfLittleEndian(v[8..]));
        Assert.Equal((Half)0, BinaryPrimitives.ReadHalfLittleEndian(v[10..]));
        Assert.Equal((Half)1, BinaryPrimitives.ReadHalfLittleEndian(v[16..]));
        // vertex 0: joints (1, 2, 0, 0), weights (.25, .75, 0, 0); both are exact in half precision
        var v0 = s.AttributeData.AsSpan(0);
        Assert.Equal((Half)1, BinaryPrimitives.ReadHalfLittleEndian(v0[8..]));
        Assert.Equal((Half)2, BinaryPrimitives.ReadHalfLittleEndian(v0[10..]));
        Assert.Equal((Half)0.25f, BinaryPrimitives.ReadHalfLittleEndian(v0[16..]));
        Assert.Equal((Half)0.75f, BinaryPrimitives.ReadHalfLittleEndian(v0[18..]));
    }

    [Fact]
    public void The_positions_normals_and_indices_are_the_ordinary_faces()
    {
        var plain = MeshSurfaceData.Encode(Triangle());
        var skinned = SkinnedSurfaceData.Encode(Triangle(), Influences());
        Assert.Equal(plain.VertexData, skinned.VertexData);
        Assert.Equal(plain.IndexData, skinned.IndexData);
        Assert.Equal(plain.VertexCount, skinned.VertexCount);
        Assert.Equal(plain.Aabb, skinned.Aabb);
    }

    [Fact]
    public void Influences_for_another_number_of_vertices_are_refused()
    {
        Assert.Throws<ArgumentException>(() => SkinnedSurfaceData.Encode(Triangle(), new FaceInfluences(2, new byte[8], new float[8], 0)));
    }
}
