// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Viewer.Core.Meshing;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The merge of still faces into one surface (<see cref="CellMerge"/>): placement, colour, indices, the limit and the map back to faces, in plain .NET.</summary>
public class CellMergeTests
{
    private static MeshFace Quad(float z = 0f) =>
        new([-1, 0, -1 + z, 1, 0, -1 + z, 1, 0, 1 + z, -1, 0, 1 + z], [0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1, 0], [0, 0, 1, 0, 1, 1, 0, 1], [0, 1, 2, 0, 2, 3]);

    private static MergePart Part(MeshFace f, Matrix4x4 m, float r = 1, float g = 1, float b = 1, float a = 1, uint local = 1, int face = 0) => new(f, m, r, g, b, a, local, face);

    private static float[] F32(byte[] bytes, int offset, int count) => MemoryMarshal.Cast<byte, float>(bytes.AsSpan(offset * 4, count * 4)).ToArray();
    private static ushort[] U16(byte[] bytes) => MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan()).ToArray();

    [Fact]
    public void One_face_with_no_placement_is_the_unmerged_encoding_with_a_colour_added()
    {
        var face = Quad();
        var plain = MeshSurfaceData.Encode(face);
        var merged = CellMerge.Build([Part(face, Matrix4x4.Identity)]);
        var s = Assert.Single(merged).Surface;
        Assert.Equal(F32(plain.VertexData, 0, 12), F32(s.VertexData, 0, 12));   // positions (a zero may differ in sign)
        Assert.Equal(plain.VertexData.AsSpan(12 * 4).ToArray(), s.VertexData.AsSpan(12 * 4).ToArray());   // normals and tangents byte for byte
        Assert.Equal(plain.IndexData, s.IndexData);     // the same winding
        Assert.Equal(plain.Aabb, s.Aabb);
        Assert.Equal(MeshSurfaceData.Format | MeshSurfaceData.FormatColor, s.Format);
        Assert.Equal(plain.VertexCount, s.VertexCount);
        Assert.Equal(plain.IndexCount, s.IndexCount);
    }

    [Fact]
    public void A_face_is_placed_by_its_transform_with_scale_and_offset()
    {
        // engine axes: the face at SL y = 0 lies at engine z = 0; scale 2 in x, 3 in y (engine), then move by (10, 20, 30)
        var m = Matrix4x4.CreateScale(2, 3, 1) * Matrix4x4.CreateTranslation(10, 20, 30);
        var s = Assert.Single(CellMerge.Build([Part(Quad(), m)])).Surface;
        var p = F32(s.VertexData, 0, 12);
        // vertex 0: SL (-1, 0, -1) -> engine (-1, -1, 0) -> scaled (-2, -3, 0) -> (8, 17, 30)
        Assert.Equal([8f, 17f, 30f], p[..3]);
        Assert.Equal([12f, 17f, 30f], p[3..6]);   // SL (1, 0, -1)
    }

    [Fact]
    public void Normals_follow_the_inverse_transpose_so_a_non_uniform_scale_keeps_them_square_to_the_surface()
    {
        // a slanted face: normal (0, 0.6, 0.8) in engine axes; scale y by 2 -> the normal must tilt toward z: (0, 0.3, 0.8) normalised
        var m = Matrix4x4.CreateScale(1, 2, 1);
        var face = new MeshFace([0, 0, 0, 1, 0, 0, 0, 0, 1], [0, -0.8f, 0.6f, 0, -0.8f, 0.6f, 0, -0.8f, 0.6f], [0, 0, 1, 0, 0, 1], [0, 1, 2]);   // SL normal (0,-0.8,0.6) = engine (0, 0.6, 0.8)
        var s = Assert.Single(CellMerge.Build([Part(face, m)])).Surface;
        var n = Decode(s.VertexData, s.VertexCount, 0);
        float len = MathF.Sqrt(0.3f * 0.3f + 0.8f * 0.8f);
        Assert.Equal(0f, n.X, 2);
        Assert.Equal(0.3f / len, n.Y, 2);
        Assert.Equal(0.8f / len, n.Z, 2);
    }

    private static Vector3 Decode(byte[] vertexData, int n, int vertex)
    {
        var nt = MemoryMarshal.Cast<byte, ushort>(vertexData.AsSpan(n * 12));
        float ox = nt[vertex * 4] / 65535f * 2f - 1f, oy = nt[vertex * 4 + 1] / 65535f * 2f - 1f;
        float z = 1f - MathF.Abs(ox) - MathF.Abs(oy);
        float t = MathF.Max(-z, 0f);
        float x = ox + (ox >= 0 ? -t : t), y = oy + (oy >= 0 ? -t : t);
        return Vector3.Normalize(new Vector3(x, y, z));
    }

    [Fact]
    public void Each_face_keeps_its_own_colour_on_every_vertex_as_the_nearest_byte()
    {
        var merged = CellMerge.Build([Part(Quad(), Matrix4x4.Identity, 0.2f, 0.5f, 1f, 1f, 1), Part(Quad(), Matrix4x4.Identity, 0f, 0.999f, 0.4f, 1f, 2)]);
        var s = Assert.Single(merged).Surface;
        // attribute data: 4 colour bytes then 2 floats a vertex
        Assert.Equal(8 * 12, s.AttributeData.Length);
        byte[] Colour(int vertex) => s.AttributeData.AsSpan(vertex * 12, 4).ToArray();
        Assert.Equal(new byte[] { 51, 128, 255, 255 }, Colour(0));   // 0.2 * 255 = 51; 0.5 * 255 = 127.5 -> 128 (nearest, round half to even gives 128)
        Assert.Equal(new byte[] { 0, 255, 102, 255 }, Colour(4));    // 0.999 -> 254.7 -> 255; 0.4 -> 102
        Assert.Equal(Colour(0), Colour(3));
    }

    [Fact]
    public void Indices_of_later_faces_continue_after_the_vertices_of_earlier_ones()
    {
        var s = Assert.Single(CellMerge.Build([Part(Quad(), Matrix4x4.Identity), Part(Quad(5f), Matrix4x4.Identity)])).Surface;
        // reversed winding per triangle: (0,1,2) -> (0,2,1); the second quad's vertices are 4..7
        Assert.Equal(new ushort[] { 0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6 }, U16(s.IndexData));
        Assert.Equal(8, s.VertexCount);
    }

    [Fact]
    public void The_map_names_each_faces_object_face_and_triangles()
    {
        var m = Assert.Single(CellMerge.Build([Part(Quad(), Matrix4x4.Identity, local: 7, face: 2), Part(Quad(), Matrix4x4.Identity, local: 9, face: 0)]));
        Assert.Equal([new PartRange(7, 2, 0, 2), new PartRange(9, 0, 2, 2)], m.Parts);
    }

    [Fact]
    public void A_surface_is_closed_before_it_passes_the_vertex_limit_and_the_next_one_starts_at_zero()
    {
        // a face of 40,000 vertices (a fan of the same quad's corners): two of them cannot share a surface
        int n = 40000;
        var pos = new float[n * 3]; var nrm = new float[n * 3]; var uv = new float[n * 2];
        for (int i = 0; i < n; i++) { pos[i * 3] = i; nrm[i * 3 + 1] = -1; }
        var big = new MeshFace(pos, nrm, uv, [0, 1, 2]);
        var merged = CellMerge.Build([Part(big, Matrix4x4.Identity, local: 1), Part(big, Matrix4x4.Identity, local: 2), Part(Quad(), Matrix4x4.Identity, local: 3)]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(n, merged[0].Surface.VertexCount);
        Assert.Equal(n + 4, merged[1].Surface.VertexCount);
        Assert.Equal([new PartRange(2, 0, 0, 1), new PartRange(3, 0, 1, 2)], merged[1].Parts);
        Assert.Equal(new ushort[] { 0, 2, 1 }, U16(merged[0].Surface.IndexData));
    }

    [Fact]
    public void A_mirrored_or_collapsed_transform_is_refused()
    {
        Assert.Throws<ArgumentException>(() => CellMerge.Build([Part(Quad(), Matrix4x4.CreateScale(-1, 1, 1))]));
        Assert.Throws<ArgumentException>(() => CellMerge.Build([Part(Quad(), Matrix4x4.CreateScale(0, 1, 1))]));
    }

    [Fact]
    public void The_cell_of_a_position_floors_each_axis_and_negative_positions_do_not_share_cell_zero()
    {
        Assert.Equal((0, 0, 0), CellMerge.CellOf(new Vector3(31.9f, 0f, 0.1f), 32f));
        Assert.Equal((1, 2, 0), CellMerge.CellOf(new Vector3(32f, 64f, 31f), 32f));
        Assert.Equal((-1, 0, 0), CellMerge.CellOf(new Vector3(-0.1f, 5f, 5f), 32f));
    }

    [Fact]
    public void Quantise_is_a_value_the_engine_stores_as_the_nearest_byte()
    {
        for (int k = 0; k <= 255; k++)
        {
            float q = CellMerge.Quantise(k / 255f);
            Assert.Equal(k, (int)Math.Clamp(q * 255f, 0f, 255f));
        }
        Assert.Equal(0, (int)(CellMerge.Quantise(-1f) * 255f));
        Assert.Equal(255, (int)Math.Min(255f, CellMerge.Quantise(2f) * 255f));
    }
}
