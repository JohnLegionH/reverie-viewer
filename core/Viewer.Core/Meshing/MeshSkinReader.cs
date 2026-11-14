// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Viewer.Core.Meshing;

/// <summary>
/// Reads the skinning of a mesh asset in full: the skin block and, per level of detail, every vertex's joint influences.
/// Pure and bounded: the asset first passes <see cref="MeshAssetGuard"/>, every count is checked against the bytes that
/// remain and against <see cref="MeshLimits"/>, and nothing here throws on hostile input (a refusal is a reason string).
/// No LibreMetaverse type is involved.
///
/// The layout is the Second Life mesh format (SL wiki "Mesh"): the "skin" part holds joint_names, inverse_bind_matrix,
/// bind_shape_matrix, alt_inverse_bind_matrix, pelvis_offset and lock_scale_if_joint_position; each LOD part is a list of
/// faces, and a face's "Weights" is, per vertex, up to four (joint byte, little-endian 16-bit weight) pairs followed by a
/// 0xFF joint byte when fewer than four are present. How the official viewer treats these values (read for behaviour only,
/// llmodel.cpp LLMeshSkinInfo::fromLLSD, llvolume.cpp unpackVolumeFaces, llskinningutil.cpp):
///   - a different number of inverse bind matrices than joint names drops all joint bindings;
///   - each weight is clamped to 0.001..0.999; a vertex with no influence is bound fully to joint 0;
///   - a joint index beyond the list is clamped to the last joint;
///   - at skinning time the four weights are divided by their sum, so they act as if they added up to 1.
/// The reader applies the same rules once, at decode time, so what it returns can be used as it stands.
/// </summary>
public static class MeshSkinReader
{
    /// <summary>The names of the LOD parts by LOD index (0 lowest .. 3 high).</summary>
    public static readonly System.Collections.Immutable.ImmutableArray<string> LodKeys = ["lowest_lod", "low_lod", "medium_lod", "high_lod"];

    /// <summary>Most joint names a skin block may list; past it the block is refused (the viewer uses only the first 110).</summary>
    public const int MaxListedJoints = 256;
    private const int MaxNameLength = 128;
    private const float MinWeight = 0.001f, MaxWeight = 0.999f;

    public static MeshSkinData Read(byte[] asset, MeshLimits? limits = null)
    {
        limits ??= MeshLimits.Default;
        var lods = new IReadOnlyList<FaceInfluences>?[MeshGeometry.LodCount];
        var lodProblems = new string?[MeshGeometry.LodCount];
        try
        {
            var refusal = MeshAssetGuard.Validate(asset, limits, out var parts);
            if (refusal is not null)
                return new(null, refusal.Reason, lods, lodProblems);

            int start = HeaderEnd(asset, limits);
            MeshSkinBlock? block = null;
            string? blockProblem = null;
            var skinPart = parts.FirstOrDefault(p => p.Name == "skin" && p.Size > 0 && p.Offset >= 0);
            if (skinPart is not null)
            {
                var node = Inflate(asset, start, skinPart, limits, out var why);
                block = node is null ? null : ReadBlock(node, out why);
                if (block is null) blockProblem = why ?? "skin block unreadable";
            }

            int joints = block?.UsableJoints ?? MeshSkinBlock.MaxJoints;
            for (int d = 0; d < MeshGeometry.LodCount; d++)
            {
                var part = parts.FirstOrDefault(p => p.Name == LodKeys[d] && p.Size > 0 && p.Offset >= 0);
                if (part is null) continue;
                var node = Inflate(asset, start, part, limits, out var why);
                if (node is null) { lodProblems[d] = why; continue; }
                lods[d] = ReadFaces(node, joints, limits, block is not null, out lodProblems[d]);
                if (lods[d] is { } read && read.Sum(f => f.Unmatched) is > 0 and int off)
                    lodProblems[d] = $"warning: {off} weight records missing or left over for the faces' vertices (drawn anyway, as the official viewer does)";
            }
            return new(block, blockProblem, lods, lodProblems);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new(null, "skin reader failed: " + e.GetType().Name, lods, lodProblems);
        }
    }

    // ---- the skin block ----

    private static MeshSkinBlock? ReadBlock(Node root, out string? why)
    {
        why = null;
        if (root.Map is not { } m) { why = "skin block is not a map"; return null; }

        var names = new List<string>();
        if (m.TryGetValue("joint_names", out var jn))
        {
            if (jn.List is not { } l) { why = "joint_names is not a list"; return null; }
            if (l.Count > MaxListedJoints) { why = $"{l.Count} joint names (limit {MaxListedJoints})"; return null; }
            foreach (var n in l)
            {
                if (n.Text is not { } s || s.Length > MaxNameLength) { why = "a joint name is not a short string"; return null; }
                names.Add(s);
            }
        }

        var inverse = new List<Matrix4x4>();
        if (m.TryGetValue("inverse_bind_matrix", out var ib) && !ReadMatrices(ib, MaxListedJoints, inverse, "inverse_bind_matrix", out why)) return null;

        var bindShape = Matrix4x4.Identity;
        if (m.TryGetValue("bind_shape_matrix", out var bs) && !ReadMatrix(bs, out bindShape, out why)) { why = "bind_shape_matrix: " + why; return null; }

        var alt = new List<Matrix4x4>();
        if (m.TryGetValue("alt_inverse_bind_matrix", out var ab) && !ReadMatrices(ab, MaxListedJoints, alt, "alt_inverse_bind_matrix", out why)) return null;

        float pelvis = 0f;
        if (m.TryGetValue("pelvis_offset", out var po))
        {
            if (po.Number is not { } d || !double.IsFinite(d) || !float.IsFinite((float)d)) { why = "pelvis_offset is not a finite number"; return null; }
            pelvis = (float)d;
        }
        bool lockScale = m.TryGetValue("lock_scale_if_joint_position", out var ls) && ls.Truth;

        bool dropped = inverse.Count != names.Count;
        if (dropped)
        {
            names.Clear();
            inverse.Clear();
        }
        return new MeshSkinBlock(names, inverse, bindShape, alt, pelvis, lockScale, dropped);
    }

    private static bool ReadMatrices(Node n, int max, List<Matrix4x4> into, string what, out string? why)
    {
        why = null;
        if (n.List is not { } l) { why = what + " is not a list"; return false; }
        if (l.Count > max) { why = $"{what}: {l.Count} matrices (limit {max})"; return false; }
        foreach (var e in l)
        {
            if (!ReadMatrix(e, out var mat, out var w)) { why = $"{what}: {w}"; return false; }
            into.Add(mat);
        }
        return true;
    }

    private static bool ReadMatrix(Node n, out Matrix4x4 m, out string? why)
    {
        m = Matrix4x4.Identity;
        why = null;
        if (n.List is not { Count: 16 } l) { why = "a matrix needs 16 numbers"; return false; }
        Span<float> v = stackalloc float[16];
        for (int i = 0; i < 16; i++)
        {
            if (l[i].Number is not { } d || !double.IsFinite(d) || !float.IsFinite((float)d)) { why = "a matrix entry is not a finite number"; return false; }
            v[i] = (float)d;
        }
        m = new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
        return true;
    }

    // ---- the per-vertex influences ----

    private static List<FaceInfluences>? ReadFaces(Node root, int jointCount, MeshLimits limits, bool rigged, out string? why)
    {
        why = null;
        if (root.List is not { } faces) { why = "LOD part is not a list of faces"; return null; }
        if (faces.Count > limits.MaxFacesPerLod) { why = $"{faces.Count} faces (limit {limits.MaxFacesPerLod})"; return null; }
        var result = new List<FaceInfluences>(faces.Count);
        bool any = false;
        foreach (var f in faces)
        {
            if (f.Map is not { } fm) { why = "a face is not a map"; return null; }
            if (fm.TryGetValue("NoGeometry", out var ng) && ng.Truth) { result.Add(new(0, [], [], 0)); continue; }
            int vertices = fm.TryGetValue("Position", out var pos) && pos.Bytes is { } pb ? pb.Length / 6 : 0;
            if (vertices > limits.MaxVerticesPerFace) { why = $"{vertices} vertices in a face"; return null; }
            // A face without weights in a LOD that has some is bound fully to joint 0, every vertex repaired.
            byte[] bytes = fm.TryGetValue("Weights", out var wn) && wn.Bytes is { } w ? w : [];
            any |= bytes.Length > 0;
            result.Add(ReadWeights(bytes, vertices, jointCount));
        }
        if (!any) return null;   // no weights at all: an unrigged LOD
        if (!rigged) { why = "weights without a skin block"; return null; }
        return result;
    }

    /// <summary>One face's weight bytes into four slots per vertex (see the class remarks for the rules).</summary>
    internal static FaceInfluences ReadWeights(ReadOnlySpan<byte> w, int vertices, int jointCount)
    {
        var joints = new byte[vertices * 4];
        var weights = new float[vertices * 4];
        int repaired = 0, idx = 0, last = Math.Max(0, jointCount - 1), missing = 0;
        Span<float> raw = stackalloc float[4];
        Span<byte> jt = stackalloc byte[4];
        for (int v = 0; v < vertices; v++)
        {
            if (w.Length > 0 && idx >= w.Length) missing++;   // a weight list with no record left for this vertex
            raw.Clear();
            jt.Clear();
            int n = 0;
            bool fix = false;
            // Up to four (joint, weight) pairs; fewer than four end with a 0xFF joint byte, four need no terminator.
            while (n < 4 && idx < w.Length)
            {
                byte joint = w[idx++];
                if (joint == 0xFF) break;
                if (idx + 2 > w.Length) { idx = w.Length; break; }   // a pair cut short by the end of the data
                int influence = w[idx] | (w[idx + 1] << 8);
                idx += 2;
                raw[n] = Math.Clamp(influence / 65535f, MinWeight, MaxWeight);
                if (joint > last) { joint = (byte)last; fix = true; }
                jt[n] = joint;
                n++;
            }
            float sum = raw[0] + raw[1] + raw[2] + raw[3];
            if (n == 0 || sum <= 0f) { raw[0] = 1f; sum = 1f; jt[0] = 0; fix = true; }
            for (int k = 0; k < 4; k++)
            {
                joints[v * 4 + k] = jt[k];
                weights[v * 4 + k] = raw[k] / sum;
            }
            if (fix) repaired++;
        }
        return new FaceInfluences(vertices, joints, weights, repaired, missing + (idx < w.Length ? 1 : 0));
    }

    // ---- faces with no geometry ----

    /// <summary>
    /// For each level of detail, whether each of its submeshes is marked NoGeometry (null for a level the asset lacks or that cannot be read).
    /// The official viewer's unpack gives such a submesh an empty face in its place (llvolume.cpp 2338-2347), so the faces after it keep their
    /// numbers; LibreMetaverse's decode leaves it out, and <see cref="Restore"/> puts it back.
    /// </summary>
    public static bool[]?[] NoGeometryFlags(byte[] asset, MeshLimits? limits = null)
    {
        limits ??= MeshLimits.Default;
        var result = new bool[]?[MeshGeometry.LodCount];
        try
        {
            if (MeshAssetGuard.Validate(asset, limits, out var parts) is not null) return result;
            int start = HeaderEnd(asset, limits);
            for (int d = 0; d < MeshGeometry.LodCount; d++)
            {
                var part = parts.FirstOrDefault(p => p.Name == LodKeys[d] && p.Size > 0 && p.Offset >= 0);
                if (part is null) continue;
                var node = Inflate(asset, start, part, limits, out _);
                if (node?.List is not { } faces || faces.Count > limits.MaxFacesPerLod) continue;
                result[d] = faces.Select(f => f.Map is { } fm && fm.TryGetValue("NoGeometry", out var ng) && ng.Truth).ToArray();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        return result;
    }

    /// <summary>The decoded faces of one level with an empty face put back at each position <paramref name="empty"/> marks. Unchanged when the counts do not
    /// fit (the decoder dropped something else too).</summary>
    public static IReadOnlyList<MeshFace> Restore(IReadOnlyList<MeshFace> decoded, bool[]? empty)
    {
        if (empty is null || empty.Length <= decoded.Count || empty.Count(e => !e) != decoded.Count) return decoded;
        var faces = new List<MeshFace>(empty.Length);
        int next = 0;
        foreach (var isEmpty in empty) faces.Add(isEmpty ? new MeshFace([], [], [], []) : decoded[next++]);
        return faces;
    }

    /// <summary>
    /// Makes each face's influences as long as the decoded face's vertices, as the official viewer's face has weights for every vertex it allocated
    /// (llvolume.cpp 2533): a vertex without a record is bound fully to joint 0, records beyond the vertices are dropped. A level whose faces were
    /// changed gets a warning in <see cref="MeshSkinData.LodProblems"/>; it is drawn all the same (llvolume.cpp 2594-2597 only warns).
    /// </summary>
    public static MeshSkinData Reconcile(MeshSkinData data, IReadOnlyList<MeshLod?> geometry)
    {
        var lods = data.Lods.ToArray();
        var problems = data.LodProblems.ToArray();
        for (int d = 0; d < lods.Length && d < geometry.Count; d++)
        {
            if (lods[d] is not { } inf || geometry[d] is not { } geo) continue;
            bool same = inf.Count == geo.Faces.Count && Enumerable.Range(0, inf.Count).All(f => inf[f].Vertices == geo.Faces[f].Vertices);
            if (same) continue;
            var fitted = new List<FaceInfluences>(geo.Faces.Count);
            for (int f = 0; f < geo.Faces.Count; f++)
            {
                int vertices = geo.Faces[f].Vertices;
                var have = f < inf.Count ? inf[f] : null;
                if (have is not null && have.Vertices == vertices) { fitted.Add(have); continue; }
                var joints = new byte[vertices * 4];
                var weights = new float[vertices * 4];
                int copy = Math.Min(vertices, have?.Vertices ?? 0);
                if (have is not null) { Array.Copy(have.Joints, joints, copy * 4); Array.Copy(have.Weights, weights, copy * 4); }
                for (int v = copy; v < vertices; v++) weights[v * 4] = 1f;
                fitted.Add(new FaceInfluences(vertices, joints, weights, (have?.Repaired ?? 0) + vertices - copy, Math.Abs(vertices - (have?.Vertices ?? 0))));
            }
            lods[d] = fitted;
            problems[d] ??= $"warning: the weights did not match the faces ({inf.Count} lists for {geo.Faces.Count} faces); fitted, and drawn anyway, as the official viewer does";
        }
        return data with { Lods = lods, LodProblems = problems };
    }

    // ---- container plumbing ----

    private static int HeaderEnd(byte[] asset, MeshLimits limits) => MeshAssetGuard.HeaderLength(asset, limits);

    private static Node? Inflate(byte[] asset, int start, MeshAssetGuard.Part part, MeshLimits limits, out string? why)
    {
        why = null;
        if ((long)start + part.Offset + part.Size > asset.Length) { why = "part beyond the asset"; return null; }
        var bytes = MeshAssetGuard.Inflate(asset.AsSpan(start + part.Offset, part.Size), limits.MaxInflatedPartBytes, out why);
        if (bytes is null) return null;
        var p = new LlsdParser(bytes, limits);
        var node = p.Parse(0);
        if (node is null) why = p.Problem ?? "unreadable LLSD";
        return node;
    }

    // ---- a small bounded binary-LLSD reader ----

    private sealed class Node
    {
        public Dictionary<string, Node>? Map;
        public List<Node>? List;
        public string? Text;
        public byte[]? Bytes;
        public double? Number;
        public bool Truth;
        public bool TryGetValue(string k, out Node n) { n = null!; return Map is not null && Map.TryGetValue(k, out n!); }
    }

    private sealed class LlsdParser(byte[] data, MeshLimits limits)
    {
        private int _pos, _nodes;
        public string? Problem;

        private Node? Fail(string why) { Problem ??= why; return null; }

        public Node? Parse(int depth)
        {
            if (depth > limits.MaxLlsdDepth) return Fail("nesting too deep");
            if (++_nodes > limits.MaxLlsdNodes) return Fail("too many nodes");
            if (_pos >= data.Length) return Fail("truncated");
            char t = (char)data[_pos++];
            switch (t)
            {
                case '!': return new Node();
                case '1': return new Node { Truth = true };
                case '0': return new Node();
                case 'i':
                    if (!Has(4)) return Fail("truncated");
                    var i = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(_pos, 4)); _pos += 4;
                    return new Node { Number = i, Truth = i != 0 };
                case 'r':
                    if (!Has(8)) return Fail("truncated");
                    var r = BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(_pos, 8)); _pos += 8;
                    return new Node { Number = r, Truth = r != 0 };
                case 'd':
                    if (!Has(8)) return Fail("truncated");
                    _pos += 8;
                    return new Node();
                case 'u':
                    if (!Has(16)) return Fail("truncated");
                    _pos += 16;
                    return new Node();
                case 's': case 'l':
                {
                    if (!Len(out int n)) return Fail("string length beyond the data");
                    var s = Encoding.UTF8.GetString(data, _pos, n); _pos += n;
                    return new Node { Text = s, Number = double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null };
                }
                case 'b':
                {
                    if (!Len(out int n)) return Fail("binary length beyond the data");
                    var b = data.AsSpan(_pos, n).ToArray(); _pos += n;
                    return new Node { Bytes = b };
                }
                case '[':
                {
                    if (!Len(out int count)) return Fail("list count beyond the data");
                    var list = new List<Node>(Math.Min(count, 4096));
                    for (int k = 0; k < count; k++)
                    {
                        var e = Parse(depth + 1);
                        if (e is null) return null;
                        list.Add(e);
                    }
                    if (_pos >= data.Length || data[_pos] != (byte)']') return Fail("missing ']'");
                    _pos++;
                    return new Node { List = list };
                }
                case '{':
                {
                    if (!Len(out int count)) return Fail("map count beyond the data");
                    var map = new Dictionary<string, Node>(Math.Min(count, 64), StringComparer.Ordinal);
                    for (int k = 0; k < count; k++)
                    {
                        if (_pos >= data.Length || data[_pos] != (byte)'k') return Fail("map key expected");
                        _pos++;
                        if (!Len(out int kn) || kn > 256) return Fail("map key length bad");
                        var key = Encoding.UTF8.GetString(data, _pos, kn); _pos += kn;
                        var v = Parse(depth + 1);
                        if (v is null) return null;
                        map[key] = v;
                    }
                    if (_pos >= data.Length || data[_pos] != (byte)'}') return Fail("missing '}'");
                    _pos++;
                    return new Node { Map = map };
                }
                default: return Fail($"unknown LLSD type 0x{(byte)t:X2}");
            }
        }

        private bool Has(int n) => data.Length - _pos >= n;

        private bool Len(out int len)
        {
            len = 0;
            if (!Has(4)) return false;
            len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(_pos, 4));
            _pos += 4;
            return len >= 0 && len <= data.Length - _pos;
        }
    }
}
