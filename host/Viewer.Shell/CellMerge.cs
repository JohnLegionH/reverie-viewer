// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Meshing;

namespace Viewer.Shell;

/// <summary>One face to put into a merged surface: its geometry (SL axes, as a prim, sculpt or mesh face holds it), where its object puts it, and the
/// colour its object gives the face. <see cref="ToCell"/> takes a vertex in the mesh's engine axes (x, z, -y: the SL position turned as
/// <see cref="MeshSurfaceData.Encode(MeshFace)"/> turns it) to the merged mesh's own space, row-vector convention (v * M), scale included. The colour is
/// the face's tint, raw sRGB, 0..1; it is carried per vertex so faces that differ only in colour can share one surface.</summary>
public readonly record struct MergePart(MeshFace Face, Matrix4x4 ToCell, float R, float G, float B, float A, uint Local, int FaceIndex);

/// <summary>Where one face's triangles sit in a merged surface (the map from merged geometry back to its object and face, for picking).</summary>
public readonly record struct PartRange(uint Local, int FaceIndex, int FirstTriangle, int Triangles);

/// <summary>A merged surface in the engine's layout and the map of the faces in it.</summary>
public sealed record MergedSurface(PreparedSurface Surface, PartRange[] Parts);

/// <summary>
/// Faces of still objects merged into surfaces, so that a cell of the world is drawn with a few draw calls instead of one a face. A pure computation on
/// plain arrays (no engine type), safe on any thread: the caller groups the faces that may share a draw (the same material but for the tint), and
/// <see cref="Build"/> puts each group's faces into as few surfaces as the vertex limit allows. Positions and normals are placed as the engine would
/// have placed them from the object's transform (normals by the inverse transpose, so a non-uniform scale is right); winding, tangents, octahedral
/// encoding and the box come from <see cref="MeshSurfaceData.Encode(float[], float[], int[], float[]?, float[]?, bool, bool)"/>, the layer every other surface takes.
/// Each colour is stored as the engine stores it (a byte a channel), set to the nearest of the 256 values.
/// </summary>
public static class CellMerge
{
    /// <summary>A surface is closed before it would hold more vertices than this (the 16-bit index range, with room to spare).</summary>
    public const int MaxVerticesPerSurface = 60000;

    /// <summary>The faces as one or more surfaces, in the order given. A part whose transform mirrors or collapses space is refused.</summary>
    public static List<MergedSurface> Build(IReadOnlyList<MergePart> parts)
    {
        var result = new List<MergedSurface>();
        int start = 0;
        while (start < parts.Count)
        {
            int end = start, vertices = 0;
            while (end < parts.Count && (end == start || vertices + parts[end].Face.Vertices <= MaxVerticesPerSurface)) vertices += parts[end++].Face.Vertices;
            result.Add(BuildOne(parts, start, end, vertices));
            start = end;
        }
        return result;
    }

    private static MergedSurface BuildOne(IReadOnlyList<MergePart> parts, int from, int to, int vertices)
    {
        int triangles = 0;
        for (int p = from; p < to; p++) triangles += parts[p].Face.Triangles;
        var pos = new float[vertices * 3];
        var nrm = new float[vertices * 3];
        var uv = new float[vertices * 2];
        var col = new float[vertices * 4];
        var idx = new int[triangles * 3];
        var ranges = new PartRange[to - from];
        int v = 0, t = 0;
        for (int p = from; p < to; p++)
        {
            var part = parts[p];
            var face = part.Face;
            var m = part.ToCell;
            var linear = new Matrix4x4(m.M11, m.M12, m.M13, 0, m.M21, m.M22, m.M23, 0, m.M31, m.M32, m.M33, 0, 0, 0, 0, 1);
            if (Det3(m) <= 0f || !Matrix4x4.Invert(linear, out var inverse))
                throw new ArgumentException("a merged part's transform mirrors or collapses space (object " + part.Local + ", face " + part.FaceIndex + ")");
            var normalMatrix = Matrix4x4.Transpose(inverse);
            float r = Quantise(part.R), g = Quantise(part.G), b = Quantise(part.B), a = Quantise(part.A);
            int n = face.Vertices;
            for (int i = 0; i < n; i++)
            {
                // the SL position and normal turned to the engine's axes, as the encoder does for an unmerged face
                var local = new Vector3(face.Positions[i * 3], face.Positions[i * 3 + 2], -face.Positions[i * 3 + 1]);
                var at = Vector3.Transform(local, m);
                var normal = new Vector3(face.Normals[i * 3], face.Normals[i * 3 + 2], -face.Normals[i * 3 + 1]);
                normal = Vector3.TransformNormal(normal, normalMatrix);
                float length = normal.Length();
                if (length > 0f) normal /= length;
                int o = (v + i) * 3;
                pos[o] = at.X; pos[o + 1] = at.Y; pos[o + 2] = at.Z;
                nrm[o] = normal.X; nrm[o + 1] = normal.Y; nrm[o + 2] = normal.Z;
                uv[(v + i) * 2] = face.Uvs[i * 2]; uv[(v + i) * 2 + 1] = face.Uvs[i * 2 + 1];
                int c = (v + i) * 4;
                col[c] = r; col[c + 1] = g; col[c + 2] = b; col[c + 3] = a;
            }
            // the faces' indices keep their SL winding; the encoder reverses every triangle for the engine, as it does for an unmerged face
            for (int k = 0; k < face.Indices.Length; k++) idx[t * 3 + k] = face.Indices[k] + v;
            ranges[p - from] = new PartRange(part.Local, part.FaceIndex, t, face.Triangles);
            v += n;
            t += face.Triangles;
        }
        // the positions are the engine's already; the SL-axes turn was done above
        return new MergedSurface(MeshSurfaceData.Encode(pos, nrm, idx, uv, col, slAxes: false, reverseWinding: true), ranges);
    }

    /// <summary>The cell of an SL position (x east, y north, z up) for cells of <paramref name="size"/> metres.</summary>
    public static (int X, int Y, int Z) CellOf(Vector3 slPosition, float size) =>
        ((int)MathF.Floor(slPosition.X / size), (int)MathF.Floor(slPosition.Y / size), (int)MathF.Floor(slPosition.Z / size));

    /// <summary>A colour channel as the byte the engine would store it from this value, given back as a value that stores to that same byte.</summary>
    public static float Quantise(float v)
    {
        float k = MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
        return (k + 0.5f) / 255f;
    }

    private static float Det3(Matrix4x4 m) =>
        m.M11 * (m.M22 * m.M33 - m.M23 * m.M32) - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31) + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);
}
