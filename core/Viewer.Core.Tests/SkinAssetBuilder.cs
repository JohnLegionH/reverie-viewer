// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Viewer.Core.Tests;

/// <summary>
/// Builds synthetic rigged-mesh assets for the skin reader's tests, with its own small binary-LLSD writer (so the reader
/// is tested against an independent encoder, not against its own parser). Layout: the SL mesh format, a header map of
/// {offset, size} per part followed by the zlib-compressed parts.
/// </summary>
internal static class SkinAssetBuilder
{
    public sealed record Skin(string[] Names, Matrix4x4[] Inverse, Matrix4x4? BindShape = null, Matrix4x4[]? Alt = null, float? Pelvis = null, bool? Lock = null);

    /// <summary>One face: its vertex count, and its weight bytes (null: no Weights entry), or no geometry at all.</summary>
    public sealed record Face(int Vertices, byte[]? Weights, bool NoGeometry = false);

    public static byte[] Asset(Skin? skin, params (int Lod, Face[] Faces)[] lods)
    {
        var parts = new List<(string Key, byte[] Z)>();
        if (skin is not null) parts.Add(("skin", Z(SkinNode(skin))));
        foreach (var (lod, faces) in lods) parts.Add((Viewer.Core.Meshing.MeshSkinReader.LodKeys[lod], Z(FacesNode(faces))));
        using var header = new MemoryStream();
        header.WriteByte((byte)'{');
        Int(header, parts.Count + 1);
        Key(header, "version"); header.WriteByte((byte)'i'); Int(header, 1);
        int offset = 0;
        foreach (var (key, z) in parts)
        {
            Key(header, key);
            header.WriteByte((byte)'{'); Int(header, 2);
            Key(header, "offset"); header.WriteByte((byte)'i'); Int(header, offset);
            Key(header, "size"); header.WriteByte((byte)'i'); Int(header, z.Length);
            header.WriteByte((byte)'}');
            offset += z.Length;
        }
        header.WriteByte((byte)'}');
        foreach (var (_, z) in parts) header.Write(z);
        return header.ToArray();
    }

    /// <summary>Weight bytes for one vertex: (joint, 16-bit weight) pairs, then 0xFF when fewer than four.</summary>
    public static byte[] Vertex(params (byte Joint, ushort Weight)[] influences)
    {
        var b = new List<byte>();
        foreach (var (j, w) in influences) { b.Add(j); b.Add((byte)(w & 0xFF)); b.Add((byte)(w >> 8)); }
        if (influences.Length < 4) b.Add(0xFF);
        return b.ToArray();
    }

    public static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static Matrix4x4 Mat(float seed) => new(seed, seed + 1, seed + 2, seed + 3, seed + 4, seed + 5, seed + 6, seed + 7, seed + 8, seed + 9, seed + 10, seed + 11, seed + 12, seed + 13, seed + 14, seed + 15);

    // ---- nodes ----

    private static byte[] SkinNode(Skin s)
    {
        var entries = new List<(string, Action<MemoryStream>)>
        {
            ("joint_names", m => List(m, s.Names.Select<string, Action<MemoryStream>>(n => x => Str(x, n)))),
            ("inverse_bind_matrix", m => List(m, s.Inverse.Select<Matrix4x4, Action<MemoryStream>>(a => x => Matrix(x, a)))),
        };
        if (s.BindShape is { } bs) entries.Add(("bind_shape_matrix", m => Matrix(m, bs)));
        if (s.Alt is { } alt) entries.Add(("alt_inverse_bind_matrix", m => List(m, alt.Select<Matrix4x4, Action<MemoryStream>>(a => x => Matrix(x, a)))));
        if (s.Pelvis is { } p) entries.Add(("pelvis_offset", m => Real(m, p)));
        if (s.Lock is { } l) entries.Add(("lock_scale_if_joint_position", m => m.WriteByte((byte)(l ? '1' : '0'))));
        using var ms = new MemoryStream();
        Map(ms, entries);
        return ms.ToArray();
    }

    private static byte[] FacesNode(Face[] faces)
    {
        using var ms = new MemoryStream();
        List(ms, faces.Select<Face, Action<MemoryStream>>(f => m =>
        {
            var e = new List<(string, Action<MemoryStream>)>();
            if (f.NoGeometry) e.Add(("NoGeometry", x => x.WriteByte((byte)'1')));
            else e.Add(("Position", x => Binary(x, new byte[f.Vertices * 6])));
            if (f.Weights is { } w) e.Add(("Weights", x => Binary(x, w)));
            Map(m, e);
        }));
        return ms.ToArray();
    }

    // ---- LLSD binary writing ----

    private static void Int(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
    private static void Real(Stream s, double v) { s.WriteByte((byte)'r'); Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, v); s.Write(b); }
    private static void Key(Stream s, string k) { s.WriteByte((byte)'k'); var b = Encoding.UTF8.GetBytes(k); Int(s, b.Length); s.Write(b); }
    private static void Str(Stream s, string v) { s.WriteByte((byte)'s'); var b = Encoding.UTF8.GetBytes(v); Int(s, b.Length); s.Write(b); }
    private static void Binary(Stream s, byte[] v) { s.WriteByte((byte)'b'); Int(s, v.Length); s.Write(v); }

    private static void List(Stream s, IEnumerable<Action<MemoryStream>> items)
    {
        var l = items.ToList();
        s.WriteByte((byte)'['); Int(s, l.Count);
        foreach (var i in l) i((MemoryStream)s);
        s.WriteByte((byte)']');
    }

    private static void Map(Stream s, List<(string Key, Action<MemoryStream> Write)> entries)
    {
        s.WriteByte((byte)'{'); Int(s, entries.Count);
        foreach (var (k, w) in entries) { Key(s, k); w((MemoryStream)s); }
        s.WriteByte((byte)'}');
    }

    /// <summary>A matrix as 16 reals, row-major.</summary>
    private static void Matrix(Stream s, Matrix4x4 m)
    {
        float[] v = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
        List(s, v.Select<float, Action<MemoryStream>>(f => x => Real(x, f)));
    }

    private static byte[] Z(byte[] raw)
    {
        using var o = new MemoryStream();
        using (var z = new ZLibStream(o, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        return o.ToArray();
    }
}
