// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Viewer.Core.Avatars;

/// <summary>
/// A body mesh in the "Linden Binary Mesh 1.0" layout (little-endian): a 24-byte header string; a weights flag and a
/// detail-UV flag (one byte each); the mesh's position, rotation (three floats each), rotation order (one byte) and scale;
/// a vertex count (16-bit); then for every vertex its position, normal, binormal (three floats each), UV (two floats),
/// detail UV (when flagged) and weight (one float, when flagged); a triangle count (16-bit) and three 16-bit indices per
/// triangle; when weighted, a 16-bit count of skin joint names, each a 64-byte zero-padded string. The shape morphs follow
/// (<see cref="MorphTarget"/>); the vertex remaps after them only serve clothing-texture masks and are not read. Only full meshes are read (the
/// lower-detail files share the full mesh's vertices and carry only triangles).
/// </summary>
public sealed class LlmMesh
{
    public const string Header = "Linden Binary Mesh 1.0";
    public const int HeaderBytes = 24, JointNameBytes = 64;

    public required int VertexCount { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] Uvs { get; init; }
    /// <summary>One per vertex when the mesh is weighted (see <see cref="SkinBinding"/>); empty otherwise.</summary>
    public required float[] Weights { get; init; }
    /// <summary>Triangle corner indices, three per triangle, wound as the file stores them.</summary>
    public required ushort[] Indices { get; init; }
    public required string[] SkinJoints { get; init; }
    public required Vector3 MeshPosition { get; init; }
    public required Vector3 MeshScale { get; init; }
    /// <summary>The shape morphs the file carries, by name (empty when it has none, as the lower-detail files and the synthetic meshes of tests).</summary>
    public IReadOnlyDictionary<string, MorphTarget> Morphs { get; init; } = NoMorphs;
    public bool Weighted => Weights.Length > 0;

    public static LlmMesh Parse(ReadOnlySpan<byte> b)
    {
        var r = new Reader(b);
        if (Encoding.ASCII.GetString(r.Take(HeaderBytes)).TrimEnd('\0') != Header) throw new InvalidDataException("not a binary body mesh (header)");
        bool weighted = r.Byte() != 0, detail = r.Byte() != 0;
        var position = r.Vec3();
        r.Vec3();   // rotation (zero in every body mesh)
        r.Byte();   // rotation order
        var scale = r.Vec3();
        int n = r.U16();
        if (n == 0) throw new InvalidDataException("a body mesh with no vertices (a lower-detail file?)");
        var pos = new Vector3[n];
        var nrm = new Vector3[n];
        var uv = new Vector2[n];
        for (int i = 0; i < n; i++) pos[i] = r.Vec3();
        for (int i = 0; i < n; i++) nrm[i] = r.Vec3();
        r.Take(12 * n);   // binormals
        for (int i = 0; i < n; i++) uv[i] = new(r.F32(), r.F32());
        if (detail) r.Take(8 * n);
        var w = weighted ? new float[n] : [];
        for (int i = 0; i < w.Length; i++) w[i] = r.F32();
        int t = r.U16();
        var idx = new ushort[3 * t];
        for (int i = 0; i < idx.Length; i++)
        {
            idx[i] = (ushort)r.U16();
            if (idx[i] >= n) throw new InvalidDataException($"triangle index {idx[i]} past {n} vertices");
        }
        var joints = new string[weighted ? r.U16() : 0];
        for (int i = 0; i < joints.Length; i++)
        {
            var name = r.Take(JointNameBytes);
            int z = name.IndexOf((byte)0);
            joints[i] = Encoding.ASCII.GetString(z < 0 ? name : name[..z]);
        }
        var morphs = r.Remaining > 0 ? MorphTarget.ReadAll(r.Rest()) : NoMorphs;
        return new LlmMesh { VertexCount = n, Positions = pos, Normals = nrm, Uvs = uv, Weights = w, Indices = idx, SkinJoints = joints, MeshPosition = position, MeshScale = scale, Morphs = morphs };
    }

    private static readonly IReadOnlyDictionary<string, MorphTarget> NoMorphs = new Dictionary<string, MorphTarget>();

    private ref struct Reader(ReadOnlySpan<byte> b)
    {
        private readonly ReadOnlySpan<byte> _b = b;
        private int _o;

        public ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || _o + n > _b.Length) throw new InvalidDataException($"body mesh truncated at byte {_o} (wanted {n} more of {_b.Length})");
            var s = _b.Slice(_o, n);
            _o += n;
            return s;
        }

        public int Remaining => _b.Length - _o;
        public ReadOnlySpan<byte> Rest() => _b[_o..];

        public byte Byte() => Take(1)[0];
        public int U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public Vector3 Vec3() => new(F32(), F32(), F32());
    }
}
