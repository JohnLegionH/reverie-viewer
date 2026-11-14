// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Viewer.Core.Building;

/// <summary>
/// Reads glTF 2.0 models (.glb, and .gltf with its buffers and images embedded or beside it) for upload, following the glTF 2.0
/// specification (Khronos): the default scene's node tree with each node's transform, triangle primitives (one face per material),
/// base-colour and PBR material values, and the images. A skin, or joints and weights on a primitive, is a rigged mesh and is refused.
/// Positions go from glTF's axes (y up) to the viewer's (z up); v is flipped, because glTF's UV origin is the image's top left.
/// </summary>
public static class GltfReader
{
    private const uint Magic = 0x46546C67, ChunkJson = 0x4E4F534A, ChunkBin = 0x004E4942;

    public static ImportedModel Read(byte[] file, Func<string, byte[]?> sibling)
    {
        byte[]? glbBin = null;
        ReadOnlySpan<byte> json;
        if (file.Length >= 12 && BitConverter.ToUInt32(file, 0) == Magic)
        {
            if (BitConverter.ToUInt32(file, 4) != 2)
                return ImportedModel.Refused(ModelRefusals.Unreadable, "glTF version");
            int at = 12;
            json = default;
            while (at + 8 <= file.Length)
            {
                int len = BitConverter.ToInt32(file, at);
                uint type = BitConverter.ToUInt32(file, at + 4);
                if (len < 0 || at + 8 + len > file.Length)
                    return ImportedModel.Refused(ModelRefusals.Unreadable, "chunk");
                if (type == ChunkJson) json = file.AsSpan(at + 8, len);
                else if (type == ChunkBin && glbBin is null) glbBin = file[(at + 8)..(at + 8 + len)];
                at += 8 + len;
            }
            if (json.IsEmpty)
                return ImportedModel.Refused(ModelRefusals.Unreadable, "no JSON");
        }
        else
            json = file;
        using var doc = JsonDocument.Parse(json.ToArray());
        return new Reader(doc.RootElement, glbBin, sibling).Run();
    }

    /// <summary>A glTF position or normal in the viewer's axes: glTF's y up becomes z up, its z (towards the viewer) becomes -y.</summary>
    public static Vector3 ToViewer(Vector3 g) => new(g.X, -g.Z, g.Y);

    private sealed class Reader(JsonElement root, byte[]? glbBin, Func<string, byte[]?> sibling)
    {
        private readonly Dictionary<int, byte[]> _buffers = [];
        private readonly List<ModelMesh> _meshes = [];
        private string? _refusal, _detail;

        public ImportedModel Run()
        {
            if (root.TryGetProperty("skins", out var skins) && skins.GetArrayLength() > 0)
                return ImportedModel.Refused(ModelRefusals.Rigged);
            var images = ReadImages();
            if (_refusal is not null) return ImportedModel.Refused(_refusal, _detail);
            var materials = ReadMaterials();
            IEnumerable<int> top;
            if (Arr("scenes") is { } scenes && scenes.GetArrayLength() > 0)
            {
                int s = root.TryGetProperty("scene", out var si) ? si.GetInt32() : 0;
                top = scenes[Math.Clamp(s, 0, scenes.GetArrayLength() - 1)].TryGetProperty("nodes", out var n) ? n.EnumerateArray().Select(e => e.GetInt32()).ToArray() : [];
            }
            else
            {
                // no scene: every node that is nobody's child
                var nodes = Arr("nodes");
                int count = nodes?.GetArrayLength() ?? 0;
                var children = new HashSet<int>();
                for (int i = 0; i < count; i++)
                    if (nodes!.Value[i].TryGetProperty("children", out var c)) foreach (var k in c.EnumerateArray()) children.Add(k.GetInt32());
                top = Enumerable.Range(0, count).Where(i => !children.Contains(i)).ToArray();
            }
            foreach (int n in top)
            {
                Visit(n, Matrix4x4.Identity, 0);
                if (_refusal is not null) return ImportedModel.Refused(_refusal, _detail);
            }
            return new ImportedModel(_meshes, materials, images);
        }

        private JsonElement? Arr(string name) => root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a : null;

        private void Visit(int index, Matrix4x4 parent, int depth)
        {
            var nodes = Arr("nodes");
            if (nodes is null || index < 0 || index >= nodes.Value.GetArrayLength() || depth > 64)
                return;
            var node = nodes.Value[index];
            var world = LocalMatrix(node) * parent;
            if (node.TryGetProperty("skin", out _)) { _refusal = ModelRefusals.Rigged; return; }
            if (node.TryGetProperty("mesh", out var mi))
            {
                string name = node.TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "";
                var mesh = ReadMesh(mi.GetInt32(), world, name);
                if (_refusal is not null) return;
                if (mesh is not null) _meshes.Add(mesh);
            }
            if (node.TryGetProperty("children", out var ch))
                foreach (var c in ch.EnumerateArray())
                {
                    Visit(c.GetInt32(), world, depth + 1);
                    if (_refusal is not null) return;
                }
        }

        private static Matrix4x4 LocalMatrix(JsonElement node)
        {
            if (node.TryGetProperty("matrix", out var m) && m.GetArrayLength() == 16)
            {
                var v = m.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                // glTF stores column-major for column vectors; System.Numerics uses row vectors, so the same order reads as its rows
                return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
            }
            var t = node.TryGetProperty("translation", out var te) ? Vec3(te) : Vector3.Zero;
            var r = node.TryGetProperty("rotation", out var re) && re.GetArrayLength() == 4
                ? new Quaternion(re[0].GetSingle(), re[1].GetSingle(), re[2].GetSingle(), re[3].GetSingle()) : Quaternion.Identity;
            var s = node.TryGetProperty("scale", out var se) ? Vec3(se) : Vector3.One;
            return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(r)) * Matrix4x4.CreateTranslation(t);
        }

        private static Vector3 Vec3(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());

        private ModelMesh? ReadMesh(int index, Matrix4x4 world, string nodeName)
        {
            var meshes = Arr("meshes");
            if (meshes is null || index < 0 || index >= meshes.Value.GetArrayLength())
                return null;
            var mesh = meshes.Value[index];
            // the node's name first: several nodes may show one mesh, and each becomes its own prim
            string name = nodeName.Length > 0 ? nodeName : mesh.TryGetProperty("name", out var mn) && mn.GetString() is { Length: > 0 } s ? s : $"mesh{index}";
            var byMaterial = new Dictionary<int, (List<float> P, List<float> N, List<float> U, List<int> I)>();
            var order = new List<int>();
            Matrix4x4.Invert(world, out var inv);
            var normalMatrix = Matrix4x4.Transpose(inv);
            bool flip = world.GetDeterminant() < 0;
            foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                int mode = prim.TryGetProperty("mode", out var mo) ? mo.GetInt32() : 4;
                if (mode != 4)
                    continue;   // points, lines and strips carry no triangles a prim can use
                var attrs = prim.GetProperty("attributes");
                if (attrs.TryGetProperty("JOINTS_0", out _) || attrs.TryGetProperty("WEIGHTS_0", out _)) { _refusal = ModelRefusals.Rigged; return null; }
                if (!attrs.TryGetProperty("POSITION", out var pa))
                    continue;
                var pos = Floats(pa.GetInt32(), 3);
                if (pos is null) return null;
                int nv = pos.Length / 3;
                var nrm = attrs.TryGetProperty("NORMAL", out var na) ? Floats(na.GetInt32(), 3) : null;
                var uv = attrs.TryGetProperty("TEXCOORD_0", out var ua) ? Floats(ua.GetInt32(), 2) : null;
                if (_refusal is not null) return null;
                int[] idx = prim.TryGetProperty("indices", out var ia) ? Ints(ia.GetInt32()) ?? [] : Enumerable.Range(0, nv).ToArray();
                if (_refusal is not null) return null;
                int material = prim.TryGetProperty("material", out var me) ? me.GetInt32() : -1;
                if (!byMaterial.TryGetValue(material, out var acc))
                {
                    acc = ([], [], [], []);
                    byMaterial[material] = acc;
                    order.Add(material);
                }
                int baseVertex = acc.P.Count / 3;
                for (int v = 0; v < nv; v++)
                {
                    var p = ToViewer(Vector3.Transform(new Vector3(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]), world));
                    acc.P.Add(p.X); acc.P.Add(p.Y); acc.P.Add(p.Z);
                    var n = nrm is { } nn && nn.Length >= (v + 1) * 3 ? Vector3.TransformNormal(new Vector3(nn[v * 3], nn[v * 3 + 1], nn[v * 3 + 2]), normalMatrix) : Vector3.Zero;
                    n = n.LengthSquared() > 0 ? Vector3.Normalize(ToViewer(n)) : Vector3.Zero;
                    acc.N.Add(n.X); acc.N.Add(n.Y); acc.N.Add(n.Z);
                    if (uv is { } uu && uu.Length >= (v + 1) * 2) { acc.U.Add(uu[v * 2]); acc.U.Add(1f - uu[v * 2 + 1]); }
                    else { acc.U.Add(0); acc.U.Add(0); }
                }
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    if (idx[t] >= nv || idx[t + 1] >= nv || idx[t + 2] >= nv || idx[t] < 0 || idx[t + 1] < 0 || idx[t + 2] < 0)
                    { _refusal = ModelRefusals.Unreadable; _detail = "index"; return null; }
                    // a mirrored transform turns the winding round; it is put back so the faces still face out
                    acc.I.Add(baseVertex + idx[t]);
                    acc.I.Add(baseVertex + idx[flip ? t + 2 : t + 1]);
                    acc.I.Add(baseVertex + idx[flip ? t + 1 : t + 2]);
                }
            }
            var faces = order.Select(m =>
            {
                var a = byMaterial[m];
                var n = a.N.ToArray();
                if (n.All(x => x == 0)) n = FlatNormals(a.P, a.I);
                return new ModelFace(a.P.ToArray(), n, a.U.ToArray(), a.I.ToArray(), m);
            }).Where(f => f.Triangles > 0).ToArray();
            return faces.Length == 0 ? null : new ModelMesh(name, faces);
        }

        /// <summary>Normals from the triangles when the file gives none: each vertex the sum of its triangles' faces, normalised.</summary>
        private static float[] FlatNormals(List<float> p, List<int> idx)
        {
            var n = new Vector3[p.Count / 3];
            for (int t = 0; t + 2 < idx.Count; t += 3)
            {
                Vector3 V(int i) => new(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
                var f = Vector3.Cross(V(idx[t + 1]) - V(idx[t]), V(idx[t + 2]) - V(idx[t]));
                n[idx[t]] += f; n[idx[t + 1]] += f; n[idx[t + 2]] += f;
            }
            return n.SelectMany(v => v.LengthSquared() > 0 ? Normalize(Vector3.Normalize(v)) : [0f, 0f, 1f]).ToArray();
            static float[] Normalize(Vector3 v) => [v.X, v.Y, v.Z];
        }

        private byte[]? Buffer(int index)
        {
            if (_buffers.TryGetValue(index, out var b)) return b;
            var buffers = Arr("buffers");
            if (buffers is null || index < 0 || index >= buffers.Value.GetArrayLength()) { _refusal = ModelRefusals.Unreadable; _detail = "buffer"; return null; }
            var buf = buffers.Value[index];
            byte[]? data = buf.TryGetProperty("uri", out var u) ? Uri(u.GetString() ?? "") : glbBin;
            if (data is null) { _refusal ??= ModelRefusals.MissingFile; _detail ??= u.ValueKind == JsonValueKind.String ? u.GetString() : "buffer"; return null; }
            _buffers[index] = data;
            return data;
        }

        private byte[]? Uri(string uri)
        {
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = uri.IndexOf(',');
                return comma > 0 && uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase) ? Convert.FromBase64String(uri[(comma + 1)..]) : null;
            }
            string name = System.Uri.UnescapeDataString(uri);
            if (name.Contains("..") || Path.IsPathRooted(name))
                return null;   // only files beside the model are read
            return sibling(name);
        }

        private (byte[] Data, int Offset, int Length, int Stride)? View(int index)
        {
            var views = Arr("bufferViews");
            if (views is null || index < 0 || index >= views.Value.GetArrayLength()) { _refusal = ModelRefusals.Unreadable; _detail = "bufferView"; return null; }
            var v = views.Value[index];
            var data = Buffer(v.GetProperty("buffer").GetInt32());
            if (data is null) return null;
            int off = v.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0, len = v.GetProperty("byteLength").GetInt32();
            if (off < 0 || len < 0 || (long)off + len > data.Length) { _refusal = ModelRefusals.Unreadable; _detail = "bufferView range"; return null; }
            return (data, off, len, v.TryGetProperty("byteStride", out var s) ? s.GetInt32() : 0);
        }

        private static int ComponentSize(int type) => type switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => 0 };

        private static int Width(string type) => type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => 0 };

        /// <summary>An accessor's values as floats (normalised integers mapped to 0..1 or -1..1), <paramref name="want"/> per element.</summary>
        private float[]? Floats(int index, int want)
        {
            var acc = Accessor(index, out int count, out int ctype, out int width, out bool normalized);
            if (acc is not { } a) return null;
            if (width < want) { _refusal = ModelRefusals.Unreadable; _detail = "accessor type"; return null; }
            var output = new float[count * want];
            int cs = ComponentSize(ctype), stride = a.Stride > 0 ? a.Stride : cs * width;
            for (int i = 0; i < count; i++)
                for (int c = 0; c < want; c++)
                {
                    int at = a.Offset + i * stride + c * cs;
                    if (at + cs > a.Data.Length) { _refusal = ModelRefusals.Unreadable; _detail = "accessor range"; return null; }
                    output[i * want + c] = ctype switch
                    {
                        5126 => BitConverter.ToSingle(a.Data, at),
                        5121 => normalized ? a.Data[at] / 255f : a.Data[at],
                        5120 => normalized ? Math.Max((sbyte)a.Data[at] / 127f, -1f) : (sbyte)a.Data[at],
                        5123 => normalized ? BitConverter.ToUInt16(a.Data, at) / 65535f : BitConverter.ToUInt16(a.Data, at),
                        5122 => normalized ? Math.Max(BitConverter.ToInt16(a.Data, at) / 32767f, -1f) : BitConverter.ToInt16(a.Data, at),
                        _ => BitConverter.ToUInt32(a.Data, at),
                    };
                }
            return output;
        }

        private int[]? Ints(int index)
        {
            var acc = Accessor(index, out int count, out int ctype, out _, out _);
            if (acc is not { } a) return null;
            int cs = ComponentSize(ctype), stride = a.Stride > 0 ? a.Stride : cs;
            var output = new int[count];
            for (int i = 0; i < count; i++)
            {
                int at = a.Offset + i * stride;
                if (at + cs > a.Data.Length) { _refusal = ModelRefusals.Unreadable; _detail = "index range"; return null; }
                long v = ctype switch { 5121 => a.Data[at], 5123 => BitConverter.ToUInt16(a.Data, at), 5125 => BitConverter.ToUInt32(a.Data, at), _ => -1 };
                if (v < 0 || v > int.MaxValue) { _refusal = ModelRefusals.Unreadable; _detail = "index type"; return null; }
                output[i] = (int)v;
            }
            return output;
        }

        private (byte[] Data, int Offset, int Length, int Stride)? Accessor(int index, out int count, out int ctype, out int width, out bool normalized)
        {
            count = 0; ctype = 0; width = 0; normalized = false;
            var accs = Arr("accessors");
            if (accs is null || index < 0 || index >= accs.Value.GetArrayLength()) { _refusal = ModelRefusals.Unreadable; _detail = "accessor"; return null; }
            var a = accs.Value[index];
            count = a.GetProperty("count").GetInt32();
            ctype = a.GetProperty("componentType").GetInt32();
            width = Width(a.GetProperty("type").GetString() ?? "");
            normalized = a.TryGetProperty("normalized", out var nz) && nz.GetBoolean();
            if (count < 0 || width == 0 || ComponentSize(ctype) == 0 || a.TryGetProperty("sparse", out _)) { _refusal = ModelRefusals.Unreadable; _detail = "accessor kind"; return null; }
            if (!a.TryGetProperty("bufferView", out var bv)) { _refusal = ModelRefusals.Unreadable; _detail = "accessor without data"; return null; }
            var view = View(bv.GetInt32());
            if (view is not { } v) return null;
            int off = a.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
            return (v.Data, v.Offset + off, v.Length - off, v.Stride);
        }

        private List<ModelImage> ReadImages()
        {
            var list = new List<ModelImage>();
            if (Arr("images") is not { } images) return list;
            int i = 0;
            foreach (var img in images.EnumerateArray())
            {
                string name = img.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } s ? s : $"image{i}";
                byte[]? bytes = null;
                if (img.TryGetProperty("bufferView", out var bv) && View(bv.GetInt32()) is { } v)
                    bytes = v.Data[v.Offset..(v.Offset + v.Length)];
                else if (img.TryGetProperty("uri", out var u))
                {
                    string uri = u.GetString() ?? "";
                    bytes = Uri(uri);
                    if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && name == $"image{i}")
                        name = Path.GetFileNameWithoutExtension(uri);
                }
                _refusal = null; _detail = null;   // a missing image leaves its faces untextured; it does not stop the model
                list.Add(new ModelImage(name, bytes ?? []));
                i++;
            }
            return list;
        }

        private List<ModelMaterial> ReadMaterials()
        {
            var list = new List<ModelMaterial>();
            if (Arr("materials") is not { } mats) return list;
            int i = 0;
            foreach (var m in mats.EnumerateArray())
            {
                string name = m.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } s ? s : $"material{i}";
                var color = Vector4.One; int baseImage = -1, mrImage = -1, normalImage = -1, emissiveImage = -1;
                float metallic = 1, roughness = 1;
                if (m.TryGetProperty("pbrMetallicRoughness", out var pbr))
                {
                    if (pbr.TryGetProperty("baseColorFactor", out var f) && f.GetArrayLength() == 4)
                        color = new Vector4(f[0].GetSingle(), f[1].GetSingle(), f[2].GetSingle(), f[3].GetSingle());
                    baseImage = TextureImage(pbr, "baseColorTexture");
                    mrImage = TextureImage(pbr, "metallicRoughnessTexture");
                    if (pbr.TryGetProperty("metallicFactor", out var mf)) metallic = mf.GetSingle();
                    if (pbr.TryGetProperty("roughnessFactor", out var rf)) roughness = rf.GetSingle();
                }
                normalImage = TextureImage(m, "normalTexture");
                emissiveImage = TextureImage(m, "emissiveTexture");
                var emissive = m.TryGetProperty("emissiveFactor", out var ef) ? Vec3(ef) : Vector3.Zero;
                string alphaMode = m.TryGetProperty("alphaMode", out var am) ? am.GetString() ?? "OPAQUE" : "OPAQUE";
                float cutoff = m.TryGetProperty("alphaCutoff", out var ac) ? ac.GetSingle() : 0.5f;
                bool doubleSided = m.TryGetProperty("doubleSided", out var ds) && ds.GetBoolean();
                list.Add(new ModelMaterial(name, color, baseImage, doubleSided, metallic, roughness, mrImage, normalImage, emissiveImage, emissive, alphaMode, cutoff));
                i++;
            }
            return list;
        }

        private int TextureImage(JsonElement owner, string property)
        {
            if (!owner.TryGetProperty(property, out var t) || !t.TryGetProperty("index", out var ti)) return -1;
            var textures = Arr("textures");
            int index = ti.GetInt32();
            if (textures is null || index < 0 || index >= textures.Value.GetArrayLength()) return -1;
            return textures.Value[index].TryGetProperty("source", out var src) ? src.GetInt32() : -1;
        }
    }
}
