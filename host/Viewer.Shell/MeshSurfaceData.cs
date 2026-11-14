// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using Viewer.Core.Diagnostics;
using System.Runtime.InteropServices;
using Viewer.Core.Meshing;

namespace Viewer.Shell;

/// <summary>One surface in the engine's own surface layout (the dictionary a mesh surface is added
/// with), made from a mesh face off the main thread. Plain bytes and numbers: no engine type.</summary>
public sealed record PreparedSurface(ulong Format, byte[] VertexData, byte[] AttributeData, byte[] IndexData, int VertexCount, int IndexCount, float[] Aabb)
{
    public long Bytes => VertexData.LongLength + AttributeData.LongLength + IndexData.LongLength;
}

/// <summary>A mesh LOD's surfaces, one per face, ready to hand to the engine.</summary>
public record PreparedMeshLod(IReadOnlyList<PreparedSurface> Surfaces)
{
    public long Bytes => Surfaces.Sum(s => s.Bytes);
}

/// <summary>
/// The surface data of a mesh LOD, prepared off the main thread, so the main thread only hands
/// ready buffers to the engine (RenderingServer.MeshAddSurface). Before, the main thread made engine-axis arrays and the
/// engine packed them (MeshAddSurfaceFromArrays): 35-39 ms for region A's largest LODs. This is the layout the engine made
/// from our arrays, read back from it with RenderingServer.MeshGetSurface (Godot 4.7.2, the format our surfaces get) and
/// checked byte for byte by the render harness (--tr-meshprep) on every mesh-zoo LOD and synthetic edge cases:
/// - format: vertex, normal, tangent, UV and index arrays, and the engine's current format version flag;
/// - vertex data: every position (3 floats, engine axes: SL x, z, -y), then for every vertex its normal and tangent as
///   two unsigned 16-bit octahedral pairs (the engine adds a tangent when none is given);
/// - attribute data: every UV (2 floats);
/// - index data: every triangle reversed (SL winds anticlockwise, the engine clockwise), 16-bit up to 65,536 vertices,
///   32-bit above;
/// - the bounding box: the least and greatest position on each axis, a size under 0.00001 raised to 0.00001 (the only rule
///   that gave the engine's box on every case the harness checks, flat and nearly flat faces included).
/// Pure computation on the face's arrays, which nothing changes after decoding; safe on any thread.
/// </summary>
public static class MeshSurfaceData
{
    /// <summary>Vertex | Normal | Tangent | TexUV | Index, and the format version flag (bit 35), as the engine reports.</summary>
    public const ulong Format = FormatVertex | FormatNormal | FormatTangent | FormatTexUV | FormatIndex | FormatVersion;

    public const ulong FormatVertex = 0x1UL, FormatNormal = 0x2UL, FormatTangent = 0x4UL, FormatColor = 0x8UL, FormatTexUV = 0x10UL,
        FormatIndex = 0x1000UL, FormatVersion = 1UL << 35;

    /// <summary>The engine's least box size on each axis (its CMP_EPSILON).</summary>
    private const float BoxEpsilon = 0.00001f;

    public static PreparedMeshLod Prepare(IReadOnlyList<MeshFace> faces) => new(faces.Select(Encode).ToArray());

    /// <summary>A mesh, prim or sculpt face: SL axes, SL winding, UVs.</summary>
    public static PreparedSurface Encode(MeshFace f) => f.Positions.Length == 0 ? EmptyFace() : Encode(f.Positions, f.Normals, f.Indices, f.Uvs, colors: null, slAxes: true, reverseWinding: true);

    /// <summary>A face with no geometry, as the official viewer keeps it: one vertex at the origin and three indices that all name it (llvolume.cpp 2338-2347), so the
    /// face keeps its surface number and its texture entry and draws nothing.</summary>
    public static PreparedSurface EmptyFace() => Encode([0f, 0f, 0f], [0f, 0f, 1f], [0, 0, 0], [0f, 0f], colors: null, slAxes: true, reverseWinding: true);

    /// <summary>
    /// Any surface the viewer draws, in the engine's layout. <paramref name="slAxes"/>: positions and
    /// normals are SL axes (x east, y north, z up) and become the engine's (x, z, -y); otherwise they are the engine's
    /// already (terrain, water). <paramref name="reverseWinding"/>: every whole triangle reversed (SL winds anticlockwise,
    /// the engine clockwise; terrain and water are made in the engine's winding). <paramref name="uvs"/> (2 floats a
    /// vertex) and <paramref name="colors"/> (RGBA, 4 floats a vertex) are optional: the engine stores a colour as 4
    /// unsigned bytes (each channel times 255, clamped, truncated), before the UVs in each vertex's attribute data.
    /// </summary>
    public static PreparedSurface Encode(float[] positions, float[] normals, int[] indices, float[]? uvs, float[]? colors, bool slAxes, bool reverseWinding)
    {
        int n = positions.Length / 3;
        int attributeStride = (colors is null ? 0 : 4) + (uvs is null ? 0 : 8);
        var vertex = new byte[n * 12 + n * 8];
        var attribute = new byte[n * attributeStride];
        AllocationMeter.Large(vertex.Length); AllocationMeter.Large(attribute.Length);   // (measurement only)
        var pos = MemoryMarshal.Cast<byte, float>(vertex.AsSpan(0, n * 12));
        var nt = MemoryMarshal.Cast<byte, ushort>(vertex.AsSpan(n * 12));
        float ax = 0, ay = 0, az = 0, bx = 0, by = 0, bz = 0;
        for (int i = 0; i < n; i++)
        {
            float x = positions[i * 3], y, z;
            if (slAxes) { y = positions[i * 3 + 2]; z = -positions[i * 3 + 1]; }
            else { y = positions[i * 3 + 1]; z = positions[i * 3 + 2]; }
            pos[i * 3] = x; pos[i * 3 + 1] = y; pos[i * 3 + 2] = z;
            if (i == 0) { ax = bx = x; ay = by = y; az = bz = z; }
            else
            {
                if (x < ax) ax = x;
                if (y < ay) ay = y;
                if (z < az) az = z;
                if (x > bx) bx = x;
                if (y > by) by = y;
                if (z > bz) bz = z;
            }
            float nx = normals[i * 3], ny, nz;
            if (slAxes) { ny = normals[i * 3 + 2]; nz = -normals[i * 3 + 1]; }
            else { ny = normals[i * 3 + 1]; nz = normals[i * 3 + 2]; }
            var (nu, nv) = Octahedral(nx, ny, nz);
            nt[i * 4] = nu; nt[i * 4 + 1] = nv;
            var (tu, tv) = DummyTangent(nx, ny, nz);
            nt[i * 4 + 2] = tu; nt[i * 4 + 3] = tv;
            int a = i * attributeStride;
            if (colors is not null)
            {
                for (int c = 0; c < 4; c++) attribute[a + c] = Unorm8(colors[i * 4 + c]);
                a += 4;
            }
            if (uvs is not null)
            {
                BinaryPrimitives.WriteSingleLittleEndian(attribute.AsSpan(a), uvs[i * 2]);
                BinaryPrimitives.WriteSingleLittleEndian(attribute.AsSpan(a + 4), uvs[i * 2 + 1]);
            }
        }
        // as the main-thread path did (EngineWinding): every whole triangle reversed, any stray last index left at 0
        var src = indices;
        int count = src.Length;
        byte[] index;
        if (n <= 65536)
        {
            index = new byte[count * 2];
            AllocationMeter.Large(index.Length);
            var ix = MemoryMarshal.Cast<byte, ushort>(index.AsSpan());
            if (reverseWinding) for (int t = 0; t + 2 < count; t += 3) { ix[t] = (ushort)src[t]; ix[t + 1] = (ushort)src[t + 2]; ix[t + 2] = (ushort)src[t + 1]; }
            else for (int t = 0; t < count; t++) ix[t] = (ushort)src[t];
        }
        else
        {
            index = new byte[count * 4];
            AllocationMeter.Large(index.Length);
            var ix = MemoryMarshal.Cast<byte, int>(index.AsSpan());
            if (reverseWinding) for (int t = 0; t + 2 < count; t += 3) { ix[t] = src[t]; ix[t + 1] = src[t + 2]; ix[t + 2] = src[t + 1]; }
            else src.AsSpan().CopyTo(ix);
        }
        ulong format = FormatVertex | FormatNormal | FormatTangent | FormatIndex | FormatVersion | (colors is null ? 0 : FormatColor) | (uvs is null ? 0 : FormatTexUV);
        float sx = MathF.Max(bx - ax, BoxEpsilon), sy = MathF.Max(by - ay, BoxEpsilon), sz = MathF.Max(bz - az, BoxEpsilon);
        return new PreparedSurface(format, vertex, attribute, index, n, count, [ax, ay, az, sx, sy, sz]);
    }

    /// <summary>A colour channel as the engine stores it: times 255, clamped to 0..255, truncated.</summary>
    private static byte Unorm8(float v)
    {
        float s = v * 255f;
        return (byte)(s < 0f ? 0f : s > 255f ? 255f : s);
    }

    /// <summary>The engine's octahedral normal encoding (float arithmetic in the engine's order, each component scaled to
    /// 0..65535 and truncated).</summary>
    private static (ushort, ushort) Octahedral(float x, float y, float z)
    {
        float d = MathF.Abs(x) + MathF.Abs(y) + MathF.Abs(z);
        x /= d; y /= d; z /= d;
        float ox, oy;
        if (z >= 0f) { ox = x; oy = y; }
        else
        {
            ox = (1f - MathF.Abs(y)) * (x >= 0f ? 1f : -1f);
            oy = (1f - MathF.Abs(x)) * (y >= 0f ? 1f : -1f);
        }
        ox = ox * 0.5f + 0.5f;
        oy = oy * 0.5f + 0.5f;
        return (Unorm16(ox), Unorm16(oy));
    }

    private static ushort Unorm16(float v)
    {
        float s = v * 65535f;
        return (ushort)(s < 0f ? 0f : s > 65535f ? 65535f : s);
    }

    /// <summary>The tangent the engine writes when a surface has normals but no tangents (read back from it): the
    /// cross product of (n.z, -n.x, n.y) with the normalised normal, normalised, in the engine's tangent encoding (the
    /// octahedral pair with its second component kept above 1/32767 and folded into the upper half for a positive
    /// binormal sign), with (0, 65535) written as (65535, 65535).</summary>
    private static (ushort, ushort) DummyTangent(float nx, float ny, float nz)
    {
        var (ux, uy, uz) = Normalised(nx, ny, nz);
        float ax = nz, ay = -nx, az = ny;
        var (tx, ty, tz) = Normalised(ay * uz - az * uy, az * ux - ax * uz, ax * uy - ay * ux);
        float d = MathF.Abs(tx) + MathF.Abs(ty) + MathF.Abs(tz);
        tx /= d; ty /= d; tz /= d;
        float ox, oy;
        if (tz >= 0f) { ox = tx; oy = ty; }
        else
        {
            ox = (1f - MathF.Abs(ty)) * (tx >= 0f ? 1f : -1f);
            oy = (1f - MathF.Abs(tx)) * (ty >= 0f ? 1f : -1f);
        }
        ox = ox * 0.5f + 0.5f;
        oy = oy * 0.5f + 0.5f;
        const float bias = 1.0f / 32767.0f;
        oy = oy > bias ? oy : bias;   // the engine's MAX: a zero tangent (NaN here) becomes the bias, as it does there
        oy = oy * 0.5f + 0.5f;
        ushort u = Unorm16(ox), v = Unorm16(oy);
        if (u == 0 && v == 65535) u = 65535;
        return (u, v);
    }

    /// <summary>The engine's normalisation: divided by the length (a zero vector stays zero).</summary>
    private static (float, float, float) Normalised(float x, float y, float z)
    {
        float l = x * x + y * y + z * z;
        if (l == 0f) return (0f, 0f, 0f);
        l = MathF.Sqrt(l);
        return (x / l, y / l, z / l);
    }
}
