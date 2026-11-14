// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.ImportExport;
using LibreMetaverse.Rendering;
using LibreMetaverse.StructuredData;

namespace Viewer.Core.Tests;

/// <summary>
/// Builds the synthetic "mesh zoo" assets (our own content) with LibreMetaverse's own mesh encoder:
/// ModelPrim.CreateAsset encodes one LOD (high_lod) per call, so each LOD is encoded separately and the parts are
/// combined under one binary-LLSD header with LibreMetaverse's own serializer and zlib helper (SerializeLLSDBinary,
/// ZCompressOSD), in the SL layout (header map of {offset, size} per part, offsets after the header).
/// </summary>
internal static class MeshZooBuilder
{
    public static readonly string[] LodKeys = ["lowest_lod", "low_lod", "medium_lod", "high_lod"];

    public sealed record Asset(string Name, Guid Id, byte[] Bytes, int[] FacesPerLod, int[] TrianglesPerLod);

    /// <summary>The three zoo meshes. Deterministic geometry; the bytes carry a date, so tests compare decoded geometry.</summary>
    public static IReadOnlyList<Asset> Make() =>
    [
        Build("mesh-cube-6face", new Guid("4e560006-0000-4000-8000-000000000001"), d => Cube(new[] { 1, 1, 2, 4 }[d]), [0, 1, 2, 3]),
        Build("mesh-sphere", new Guid("4e560006-0000-4000-8000-000000000002"), d => Sphere(new[] { 4, 8, 16, 32 }[d]), [0, 1, 2, 3]),
        // No lowest LOD: the viewer must fall back to the next higher one.
        Build("mesh-two-face-wave", new Guid("4e560006-0000-4000-8000-000000000003"), d => Wave(new[] { 0, 4, 8, 16 }[d]), [1, 2, 3]),
    ];

    private static Asset Build(string name, Guid id, Func<int, List<ModelFace>> lod, int[] present)
    {
        var parts = new List<(string Key, byte[] Z)>();
        var faces = new int[4];
        var tris = new int[4];
        foreach (int d in present)
        {
            var f = lod(d);
            faces[d] = f.Count;
            tris[d] = f.Sum(x => x.Indices.Count / 3);
            var prim = new ModelPrim { Faces = f, Scale = Vector3.One, Rotation = Quaternion.Identity };
            prim.Positions = f.SelectMany(x => x.Vertices.Select(v => v.Position)).ToList();
            prim.BoundMin = new Vector3(-0.5f, -0.5f, -0.5f);
            prim.BoundMax = new Vector3(0.5f, 0.5f, 0.5f);
            prim.CreateAsset(UUID.Zero);
            var decoded = new AssetMesh(UUID.Zero, prim.Asset);
            if (!decoded.Decode()) throw new InvalidOperationException("encoder output did not decode");
            parts.Add((LodKeys[d], Helpers.ZCompressOSD(decoded.MeshData["high_lod"])));
        }
        var header = new OSDMap { ["version"] = 1, ["creator"] = UUID.Zero };
        int offset = 0;
        foreach (var (key, z) in parts)
        {
            header[key] = new OSDMap { ["offset"] = offset, ["size"] = z.Length };
            offset += z.Length;
        }
        var bytes = OSDParser.SerializeLLSDBinary(header, false).Concat(parts.SelectMany(p => p.Z)).ToArray();
        return new Asset(name, id, bytes, faces, tris);
    }

    private static ModelFace Face(IEnumerable<(Vector3 P, Vector3 N, Vector2 T)> verts, IEnumerable<uint> idx)
    {
        // ModelFace.AddVertex takes one triangle corner at a time and builds the index list itself (deduplicating vertices).
        var f = new ModelFace();
        var v = verts.ToList();
        foreach (var i in idx)
            f.AddVertex(new Vertex { Position = v[(int)i].P, Normal = v[(int)i].N, TexCoord = v[(int)i].T });
        return f;
    }

    /// <summary>A grid of n x n quads on a unit square, placed by (origin, u axis, v axis).</summary>
    private static ModelFace Grid(int n, Vector3 origin, Vector3 u, Vector3 v, Vector3 normal)
    {
        var verts = new List<(Vector3, Vector3, Vector2)>();
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
                verts.Add((origin + u * (i / (float)n) + v * (j / (float)n), normal, new Vector2(i / (float)n, j / (float)n)));
        var idx = new List<uint>();
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                uint a = (uint)(j * (n + 1) + i), b = a + 1, c = a + (uint)(n + 1), e = c + 1;
                idx.AddRange([a, b, e, a, e, c]);
            }
        return Face(verts, idx);
    }

    private static List<ModelFace> Cube(int n) =>
    [
        Grid(n, new(-0.5f, -0.5f, 0.5f), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)),     // top
        Grid(n, new(-0.5f, -0.5f, -0.5f), new(0, 1, 0), new(1, 0, 0), new(0, 0, -1)),   // bottom
        Grid(n, new(0.5f, -0.5f, -0.5f), new(0, 1, 0), new(0, 0, 1), new(1, 0, 0)),     // +x
        Grid(n, new(-0.5f, -0.5f, -0.5f), new(0, 0, 1), new(0, 1, 0), new(-1, 0, 0)),   // -x
        Grid(n, new(-0.5f, 0.5f, -0.5f), new(0, 0, 1), new(1, 0, 0), new(0, 1, 0)),     // +y
        Grid(n, new(-0.5f, -0.5f, -0.5f), new(1, 0, 0), new(0, 0, 1), new(0, -1, 0)),   // -y
    ];

    private static List<ModelFace> Sphere(int seg)
    {
        int rings = seg / 2;
        var verts = new List<(Vector3, Vector3, Vector2)>();
        for (int r = 0; r <= rings; r++)
            for (int s = 0; s <= seg; s++)
            {
                double th = Math.PI * r / rings, ph = 2 * Math.PI * s / seg;
                var n = new Vector3((float)(Math.Sin(th) * Math.Cos(ph)), (float)(Math.Sin(th) * Math.Sin(ph)), (float)Math.Cos(th));
                verts.Add((n * 0.5f, n, new Vector2(s / (float)seg, r / (float)rings)));
            }
        var idx = new List<uint>();
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < seg; s++)
            {
                uint a = (uint)(r * (seg + 1) + s), b = a + 1, c = a + (uint)(seg + 1), d = c + 1;
                idx.AddRange([a, c, b, b, c, d]);
            }
        return [Face(verts, idx)];
    }

    private static List<ModelFace> Wave(int n)
    {
        ModelFace Half(float x0)
        {
            var verts = new List<(Vector3, Vector3, Vector2)>();
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    float x = x0 + 0.5f * i / n, y = -0.5f + (float)j / n;
                    float z = 0.25f * MathF.Sin(x * 6f) * MathF.Cos(y * 6f);
                    verts.Add((new Vector3(x, y, z), Vector3.UnitZ, new Vector2(i / (float)n, j / (float)n)));
                }
            var idx = new List<uint>();
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    uint a = (uint)(j * (n + 1) + i), b = a + 1, c = a + (uint)(n + 1), d = c + 1;
                    idx.AddRange([a, b, d, a, d, c]);
                }
            return Face(verts, idx);
        }
        return [Half(-0.5f), Half(0f)];
    }
}
