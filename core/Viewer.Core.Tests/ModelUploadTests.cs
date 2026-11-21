// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using LibreMetaverse.StructuredData;
using OpenSimNGC.Appearance.Baking;
using Viewer.Core.Building;
using Viewer.Core.Protocol;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Model uploads: reading .glb, .gltf and .dae files into the viewer's mesh data, the mesh asset in the official format (read back by the
/// viewer's own mesh decoder, and its byte layout), automatic LODs, the two-step request and its answer, bulk uploads, and the price rules of
/// image uploads. Every model is generated here; no grid, no engine and no shared state, so the class runs in parallel.
/// </summary>
public class ModelUploadTests
{
    // ---------------- generated models ----------------

    /// <summary>A one-metre box (glTF axes, y up) whose top and bottom use material 1 and its sides material 0; UVs (0,0) at each face's
    /// first corner. Each material has its own 4×4 base-colour texture: red for material 0, blue for material 1.</summary>
    internal static class TestModels
    {
        public static byte[] Png(byte r, byte g, byte b, int size = 4)
        {
            var planes = new RgbaPlanes(size, size, true);
            for (int i = 0; i < size * size; i++) { planes.R[i] = r; planes.G[i] = g; planes.B[i] = b; planes.A[i] = 255; }
            return BakeDryRun.Png(planes);
        }

        // the six sides of a unit cube: (normal, the four corners counter-clockwise seen from outside)
        private static readonly (Vector3 N, Vector3[] C)[] Sides =
        [
            (Vector3.UnitX, [new(0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, 0.5f)]),
            (-Vector3.UnitX, [new(-0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, -0.5f)]),
            (Vector3.UnitZ, [new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f)]),
            (-Vector3.UnitZ, [new(0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, -0.5f)]),
            (Vector3.UnitY, [new(-0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f)]),
            (-Vector3.UnitY, [new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, 0.5f), new(-0.5f, -0.5f, 0.5f)]),
        ];

        private static readonly Vector2[] Corner = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)];   // glTF UVs, origin at the image's top left

        /// <summary>The box's binary buffer and the JSON describing it; <paramref name="images"/> embeds the two PNGs as buffer views
        /// (null: they are named as files beside the model).</summary>
        public static (byte[] Bin, string Json) Gltf(bool imagesInBuffer, string? bufferUri, string extraNode = "", string extraRoot = "", int nodes = 1)
        {
            using var bin = new MemoryStream();
            var views = new List<string>();
            var accessors = new List<string>();
            int View(byte[] data)
            {
                while (bin.Length % 4 != 0) bin.WriteByte(0);
                views.Add($"{{\"buffer\":0,\"byteOffset\":{bin.Length},\"byteLength\":{data.Length}}}");
                bin.Write(data);
                return views.Count - 1;
            }
            string prims = "";
            for (int m = 0; m < 2; m++)
            {
                // material 0: the four sides (x and z normals); material 1: top and bottom (y normals)
                var sides = m == 0 ? Sides[..4] : Sides[4..];
                var pos = new List<float>(); var nrm = new List<float>(); var uv = new List<float>(); var idx = new List<ushort>();
                foreach (var (n, c) in sides)
                {
                    ushort b0 = (ushort)(pos.Count / 3);
                    for (int k = 0; k < 4; k++) { pos.AddRange([c[k].X, c[k].Y, c[k].Z]); nrm.AddRange([n.X, n.Y, n.Z]); uv.AddRange([Corner[k].X, Corner[k].Y]); }
                    idx.AddRange([b0, (ushort)(b0 + 1), (ushort)(b0 + 2), b0, (ushort)(b0 + 2), (ushort)(b0 + 3)]);
                }
                int vp = View(pos.SelectMany(BitConverter.GetBytes).ToArray()), vn = View(nrm.SelectMany(BitConverter.GetBytes).ToArray());
                int vu = View(uv.SelectMany(BitConverter.GetBytes).ToArray()), vi = View(idx.SelectMany(BitConverter.GetBytes).ToArray());
                int nv = pos.Count / 3;
                accessors.Add($"{{\"bufferView\":{vp},\"componentType\":5126,\"count\":{nv},\"type\":\"VEC3\",\"min\":[-0.5,-0.5,-0.5],\"max\":[0.5,0.5,0.5]}}");
                accessors.Add($"{{\"bufferView\":{vn},\"componentType\":5126,\"count\":{nv},\"type\":\"VEC3\"}}");
                accessors.Add($"{{\"bufferView\":{vu},\"componentType\":5126,\"count\":{nv},\"type\":\"VEC2\"}}");
                accessors.Add($"{{\"bufferView\":{vi},\"componentType\":5123,\"count\":{idx.Count},\"type\":\"SCALAR\"}}");
                int a = m * 4;
                prims += (m > 0 ? "," : "") + $"{{\"attributes\":{{\"POSITION\":{a},\"NORMAL\":{a + 1},\"TEXCOORD_0\":{a + 2}}},\"indices\":{a + 3},\"material\":{m}}}";
            }
            string images;
            if (imagesInBuffer)
            {
                int i0 = View(Png(255, 0, 0)), i1 = View(Png(0, 0, 255));
                images = $"[{{\"bufferView\":{i0},\"mimeType\":\"image/png\"}},{{\"bufferView\":{i1},\"mimeType\":\"image/png\"}}]";
            }
            else
                images = "[{\"uri\":\"red.png\"},{\"uri\":\"blue.png\"}]";
            string buffer = bufferUri is null ? $"{{\"byteLength\":{bin.Length}}}" : $"{{\"byteLength\":{bin.Length},\"uri\":\"{bufferUri}\"}}";
            string nodeList = string.Join(",", Enumerable.Range(0, nodes).Select(n => $"{{\"mesh\":0,\"name\":\"box{n}\",\"translation\":[{n * 3},0,0]{extraNode}}}"));
            string json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[" + string.Join(",", Enumerable.Range(0, nodes)) + "]}]," +
                $"\"nodes\":[{nodeList}],\"meshes\":[{{\"name\":\"box\",\"primitives\":[{prims}]}}]," +
                "\"materials\":[{\"name\":\"sides\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicFactor\":0.0,\"roughnessFactor\":0.8}}," +
                "{\"name\":\"ends\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[1,1,1,1],\"baseColorTexture\":{\"index\":1}}}]," +
                "\"textures\":[{\"source\":0},{\"source\":1}]," +
                $"\"images\":{images},\"accessors\":[{string.Join(",", accessors)}],\"bufferViews\":[{string.Join(",", views)}],\"buffers\":[{buffer}]{extraRoot}}}";
            return (bin.ToArray(), json);
        }

        public static byte[] Glb(string extraNode = "", string extraRoot = "", int nodes = 1)
        {
            var (bin, json) = Gltf(true, null, extraNode, extraRoot, nodes);
            var j = Encoding.UTF8.GetBytes(json);
            int jl = (j.Length + 3) & ~3, bl = (bin.Length + 3) & ~3;
            using var ms = new MemoryStream();
            void U32(uint v) => ms.Write(BitConverter.GetBytes(v));
            U32(0x46546C67); U32(2); U32((uint)(12 + 8 + jl + 8 + bl));
            U32((uint)jl); U32(0x4E4F534A); ms.Write(j); for (int i = j.Length; i < jl; i++) ms.WriteByte(0x20);
            U32((uint)bl); U32(0x004E4942); ms.Write(bin); for (int i = bin.Length; i < bl; i++) ms.WriteByte(0);
            return ms.ToArray();
        }

        /// <summary>A COLLADA file: a unit square (two triangles, as a polylist of one quad) with a textured material and a cube corner
        /// triangle with a coloured one, y up, in centimetres.</summary>
        public static string Dae(string extra = "") => """
            <?xml version="1.0" encoding="utf-8"?>
            <COLLADA xmlns="http://www.collada.org/2005/11/COLLADASchema" version="1.4.1">
              <asset><unit name="centimeter" meter="0.01"/><up_axis>Y_UP</up_axis></asset>
              <library_images><image id="tex-img" name="tex"><init_from>red.png</init_from></image></library_images>
              <library_effects>
                <effect id="fx-tex"><profile_COMMON>
                  <newparam sid="surf"><surface type="2D"><init_from>tex-img</init_from></surface></newparam>
                  <newparam sid="samp"><sampler2D><source>surf</source></sampler2D></newparam>
                  <technique sid="common"><lambert><diffuse><texture texture="samp" texcoord="UVMap"/></diffuse></lambert></technique>
                </profile_COMMON></effect>
                <effect id="fx-col"><profile_COMMON><technique sid="common"><phong><diffuse><color>0 1 0 1</color></diffuse></phong></technique></profile_COMMON></effect>
              </library_effects>
              <library_materials>
                <material id="mat-tex" name="textured"><instance_effect url="#fx-tex"/></material>
                <material id="mat-col" name="green"><instance_effect url="#fx-col"/></material>
              </library_materials>
              <library_geometries><geometry id="geo" name="square"><mesh>
                <source id="geo-pos"><float_array id="geo-pos-a" count="15">0 0 0 100 0 0 100 100 0 0 100 0 0 0 100</float_array>
                  <technique_common><accessor source="#geo-pos-a" count="5" stride="3"/></technique_common></source>
                <source id="geo-uv"><float_array id="geo-uv-a" count="8">0 0 1 0 1 1 0 1</float_array>
                  <technique_common><accessor source="#geo-uv-a" count="4" stride="2"/></technique_common></source>
                <vertices id="geo-v"><input semantic="POSITION" source="#geo-pos"/></vertices>
                <polylist material="TEX" count="1"><input semantic="VERTEX" source="#geo-v" offset="0"/><input semantic="TEXCOORD" source="#geo-uv" offset="1" set="0"/>
                  <vcount>4</vcount><p>0 0 1 1 2 2 3 3</p></polylist>
                <triangles material="COL" count="1"><input semantic="VERTEX" source="#geo-v" offset="0"/><p>0 1 4</p></triangles>
              </mesh></geometry></library_geometries>
              <library_visual_scenes><visual_scene id="scene"><node id="n" name="square-node"><translate>0 0 50</translate>
                <instance_geometry url="#geo"><bind_material><technique_common>
                  <instance_material symbol="TEX" target="#mat-tex"/><instance_material symbol="COL" target="#mat-col"/>
                </technique_common></bind_material></instance_geometry></node></visual_scene></library_visual_scenes>
              <scene><instance_visual_scene url="#scene"/></scene>
            """ + extra + "</COLLADA>";
    }

    private static byte[]? NoSibling(string _) => null;

    private static Vector3 P(ModelFace f, int i) => new(f.Positions[i * 3], f.Positions[i * 3 + 1], f.Positions[i * 3 + 2]);

    // ---------------- reading ----------------

    [Fact]
    public void A_glb_box_reads_as_one_mesh_with_a_face_per_material_and_its_two_textures()
    {
        var m = ModelImport.Read("box.glb", TestModels.Glb(), NoSibling);
        Assert.True(m.Ok, m.Refusal);
        var mesh = Assert.Single(m.Meshes);
        Assert.Equal(2, mesh.Faces.Count);
        Assert.Equal(12, mesh.Triangles);
        Assert.Equal([0, 1], mesh.Faces.Select(f => f.Material));
        Assert.Equal(2, m.Materials.Count);
        Assert.Equal(0, m.Materials[0].BaseColorImage);
        Assert.Equal(1, m.Materials[1].BaseColorImage);
        Assert.Equal(0f, m.Materials[0].Metallic);
        Assert.Equal(0.8f, m.Materials[0].Roughness, 3);
        Assert.Equal(2, m.Images.Count);
        var red = PngDecoder.Decode(m.Images[0].Encoded);
        Assert.NotNull(red);
        Assert.Equal((byte)255, red!.Value.Rgba[0]);
        Assert.Equal((byte)0, red.Value.Rgba[2]);
        var blue = PngDecoder.Decode(m.Images[1].Encoded)!.Value;
        Assert.Equal((byte)255, blue.Rgba[2]);
    }

    [Fact]
    public void Gltf_axes_become_the_viewers_and_v_is_flipped()
    {
        var m = ModelImport.Read("box.glb", TestModels.Glb(), NoSibling);
        Assert.Equal(2, Assert.Single(m.Meshes).Faces.Count);
        var ends = m.Meshes[0].Faces[1];
        // glTF's top (+y) becomes the viewer's top (+z): every vertex of the first end face (glTF y = +0.5) is at z = +0.5
        for (int i = 0; i < 4; i++)
            Assert.Equal(0.5f, P(ends, i).Z, 5);
        // glTF +z (towards the viewer) becomes -y
        var sides = m.Meshes[0].Faces[0];
        // the first side's first corner, glTF (0.5, -0.5, 0.5), is the viewer's (0.5, -0.5, -0.5)
        Assert.Equal(new Vector3(0.5f, -0.5f, -0.5f), P(sides, 0), new Vec3Tolerance(1e-5f));
        // normals turn with the positions: the first side's +x stays +x; the top's +y becomes +z
        Assert.Equal(1f, sides.Normals[0], 5);
        Assert.Equal(1f, ends.Normals[2], 5);
        // UVs: glTF (0,1) at the first corner becomes (0,0), its (1,0) becomes (1,1)
        Assert.Equal(0f, sides.Uvs[1], 5);
        Assert.Equal(1f, sides.Uvs[5], 5);
    }

    [Fact]
    public void A_gltf_with_its_buffer_and_images_beside_it_reads_like_the_glb()
    {
        var (bin, json) = TestModels.Gltf(false, "box.bin");
        var files = new Dictionary<string, byte[]> { ["box.bin"] = bin, ["red.png"] = TestModels.Png(255, 0, 0), ["blue.png"] = TestModels.Png(0, 0, 255) };
        var m = ModelImport.Read("box.gltf", Encoding.UTF8.GetBytes(json), n => files.GetValueOrDefault(n));
        Assert.True(m.Ok, m.Refusal);
        Assert.Equal(12, m.Triangles);
        Assert.Equal("red", m.Images[0].Name);
        Assert.Equal((byte)255, PngDecoder.Decode(m.Images[1].Encoded)!.Value.Rgba[2]);
        // a buffer in a data URI reads the same
        var (bin2, json2) = TestModels.Gltf(true, "data:application/octet-stream;base64," + Convert.ToBase64String(TestModels.Gltf(true, null).Bin));
        Assert.Equal(12, ModelImport.Read("box.gltf", Encoding.UTF8.GetBytes(json2), NoSibling).Triangles);
        Assert.NotEmpty(bin2);
        // a missing buffer refuses the model with the file's name
        var missing = ModelImport.Read("box.gltf", Encoding.UTF8.GetBytes(json), NoSibling);
        Assert.Equal(ModelRefusals.MissingFile, missing.Refusal);
        Assert.Equal("box.bin", missing.Detail);
    }

    [Fact]
    public void Several_nodes_become_several_meshes_each_at_its_nodes_place()
    {
        var m = ModelImport.Read("three.glb", TestModels.Glb(nodes: 3), NoSibling);
        Assert.Equal(3, m.Meshes.Count);
        Assert.Equal(["box0", "box1", "box2"], m.Meshes.Select(x => x.Name));
        var (min, max) = m.Meshes[2].Bounds();
        Assert.Equal(6f, (min.X + max.X) / 2, 4);
        Assert.Equal(1f, max.Z - min.Z, 4);
    }

    [Fact]
    public void Rigged_models_are_refused()
    {
        var skinned = ModelImport.Read("rig.glb", TestModels.Glb(",\"skin\":0", ",\"skins\":[{\"joints\":[0]}]"), NoSibling);
        Assert.Equal(ModelRefusals.Rigged, skinned.Refusal);
        var dae = ModelImport.Read("rig.dae", Encoding.UTF8.GetBytes(TestModels.Dae().Replace("<library_visual_scenes>",
            "<library_controllers><controller id=\"c\"><skin source=\"#geo\"/></controller></library_controllers><library_visual_scenes>")), NoSibling);
        Assert.Equal(ModelRefusals.Rigged, dae.Refusal);
        Assert.Equal("Rigged mesh isn't supported yet.", StringTable.English().Get(ModelRefusals.Rigged));
    }

    [Fact]
    public void Files_that_are_not_models_or_are_broken_are_refused_with_a_reason()
    {
        Assert.Equal(ModelRefusals.Format, ModelImport.Read("notes.txt", [1, 2, 3], NoSibling).Refusal);
        Assert.Equal(ModelRefusals.Unreadable, ModelImport.Read("broken.glb", Encoding.UTF8.GetBytes("{not json"), NoSibling).Refusal);
        Assert.Equal(ModelRefusals.Unreadable, ModelImport.Read("broken.dae", Encoding.UTF8.GetBytes("<COLLADA><unclosed>"), NoSibling).Refusal);
        Assert.Equal(ModelRefusals.NoGeometry, ModelImport.Read("empty.gltf", Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"}}"), NoSibling).Refusal);
        Assert.True(ModelImport.IsSupportedFile("C/models/A.GLB"));
        Assert.True(ModelImport.IsSupportedFile("x.gltf"));
        Assert.True(ModelImport.IsSupportedFile("x.dae"));
        Assert.False(ModelImport.IsSupportedFile("x.obj"));
        foreach (var key in typeof(ModelRefusals).GetFields().Select(f => (string)f.GetValue(null)!))
            Assert.True(StringTable.English().Has(key), key);
    }

    [Fact]
    public void A_dae_reads_its_polylist_and_triangles_per_material_in_metres_with_z_up_and_its_texture()
    {
        var files = new Dictionary<string, byte[]> { ["red.png"] = TestModels.Png(255, 0, 0) };
        var m = ModelImport.Read("square.dae", Encoding.UTF8.GetBytes(TestModels.Dae()), n => files.GetValueOrDefault(n));
        Assert.True(m.Ok, m.Refusal);
        var mesh = Assert.Single(m.Meshes);
        Assert.Equal("square-node", mesh.Name);
        Assert.Equal(2, mesh.Faces.Count);
        Assert.Equal(2, mesh.Faces[0].Triangles);   // the quad, fanned
        Assert.Equal(1, mesh.Faces[1].Triangles);
        var tex = m.Materials[mesh.Faces[0].Material];
        Assert.Equal(0, tex.BaseColorImage);
        Assert.Equal((byte)255, PngDecoder.Decode(m.Images[0].Encoded)!.Value.Rgba[0]);
        var green = m.Materials[mesh.Faces[1].Material];
        Assert.Equal(-1, green.BaseColorImage);
        Assert.Equal(new Vector4(0, 1, 0, 1), green.BaseColor);
        // centimetres to metres, y up to z up: the file's (100, 100, 0) after the node's 50 cm along its z is (1, -0.5, 1)
        var (min, max) = mesh.Bounds();
        Assert.Equal(new Vector3(0, -1.5f, 0), min, new Vec3Tolerance(1e-4f));
        Assert.Equal(new Vector3(1, -0.5f, 1), max, new Vec3Tolerance(1e-4f));
        Assert.Equal(1f, mesh.Faces[0].Uvs.Max(), 4);
        // normals come from the triangles when the file has none
        Assert.All(Enumerable.Range(0, mesh.Faces[0].Vertices), i => Assert.Equal(1f, new Vector3(mesh.Faces[0].Normals[i * 3], mesh.Faces[0].Normals[i * 3 + 1], mesh.Faces[0].Normals[i * 3 + 2]).Length(), 3));
    }

    private sealed class Vec3Tolerance(float eps) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= eps;
        public int GetHashCode(Vector3 v) => 0;
    }

    [Fact]
    public void The_png_reader_reads_rgb_rgba_grey_and_palette_images_and_refuses_others()
    {
        var rgba = PngDecoder.Decode(TestModels.Png(10, 20, 30, 8))!.Value;
        Assert.Equal((8, 8), (rgba.Width, rgba.Height));
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, rgba.Rgba[..4]);
        Assert.Null(PngDecoder.Decode([1, 2, 3, 4]));
        // a palette image of 2×1 with transparency: hand-made chunks
        var pal = PngFrom(3, [0, 0, 1], 2, 1, plte: [1, 2, 3, 4, 5, 6], trns: [128]);
        var p = PngDecoder.Decode(pal)!.Value;
        Assert.Equal(new byte[] { 1, 2, 3, 128, 4, 5, 6, 255 }, p.Rgba);
        var grey = PngDecoder.Decode(PngFrom(0, [0, 77], 1, 1))!.Value;
        Assert.Equal(new byte[] { 77, 77, 77, 255 }, grey.Rgba);
        var sub = PngDecoder.Decode(PngFrom(2, [1, 10, 10, 10, 5, 5, 5], 2, 1))!.Value;   // filter 1 (sub): the second pixel adds the first
        Assert.Equal(new byte[] { 10, 10, 10, 255, 15, 15, 15, 255 }, sub.Rgba);
    }

    private static byte[] PngFrom(byte colour, byte[] raw, int w, int h, byte[]? plte = null, byte[]? trns = null)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            ms.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            ms.Write(Encoding.ASCII.GetBytes(type)); ms.Write(data); ms.Write([0, 0, 0, 0]);
        }
        Chunk("IHDR", [0, 0, 0, (byte)w, 0, 0, 0, (byte)h, 8, colour, 0, 0, 0]);
        if (plte is not null) Chunk("PLTE", plte);
        if (trns is not null) Chunk("tRNS", trns);
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) zs.Write(raw);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", []);
        return ms.ToArray();
    }

    // ---------------- LODs ----------------

    [Fact]
    public void Each_automatic_lod_aims_at_a_third_of_the_one_above()
    {
        Assert.Equal([10, 30, 90, 270], ModelLods.Targets(270));
        Assert.Equal([1, 1, 4, 12], ModelLods.Targets(12));
    }

    private static ModelFace Sphere(int rings, int segments)
    {
        var p = new List<float>(); var n = new List<float>(); var uv = new List<float>(); var idx = new List<int>();
        for (int r = 0; r <= rings; r++)
            for (int s = 0; s <= segments; s++)
            {
                float th = MathF.PI * r / rings, ph = 2 * MathF.PI * s / segments;
                var v = new Vector3(MathF.Sin(th) * MathF.Cos(ph), MathF.Sin(th) * MathF.Sin(ph), MathF.Cos(th)) * 0.5f;
                p.AddRange([v.X, v.Y, v.Z]); var nn = Vector3.Normalize(v); n.AddRange([nn.X, nn.Y, nn.Z]); uv.AddRange([(float)s / segments, (float)r / rings]);
            }
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < segments; s++)
            {
                int a = r * (segments + 1) + s, b = a + segments + 1;
                idx.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
        return new ModelFace(p.ToArray(), n.ToArray(), uv.ToArray(), idx.ToArray(), 0);
    }

    [Fact]
    public void Simplified_lods_stay_within_their_target_and_the_original_bounds_and_keep_geometry()
    {
        var sphere = Sphere(40, 60);   // 4,800 triangles
        var mesh = new ModelMesh("sphere", [sphere]);
        var lods = ModelLods.Build(mesh, auto: true);
        var targets = ModelLods.Targets(mesh.Triangles);
        Assert.Same(mesh.Faces, lods[3]);
        for (int lod = 0; lod < 3; lod++)
        {
            int t = lods[lod].Sum(f => f.Triangles);
            Assert.InRange(t, 1, targets[lod]);
            Assert.True(t >= targets[lod] / 8, $"lod {lod}: {t} triangles for a target of {targets[lod]}");
            foreach (var f in lods[lod])
            {
                Assert.All(f.Indices, i => Assert.InRange(i, 0, f.Vertices - 1));
                for (int i = 0; i < f.Vertices; i++) Assert.InRange(P(f, i).Length(), 0f, 0.5001f);
            }
        }
        // without automatic LODs every level is the high one
        Assert.All(ModelLods.Build(mesh, auto: false), l => Assert.Same(mesh.Faces, l));
    }

    // ---------------- the mesh asset ----------------

    private static ModelUploadPlan PlanOf(ImportedModel m, ModelSettings? s = null) =>
        LmvModelUpload.Plan(m, s ?? new ModelSettings(IncludeTextures: true), "box", "", Guid.NewGuid(), Guid.NewGuid(), img => LmvModelUpload.EncodeTexture(img, null));

    [Fact]
    public void The_mesh_asset_reads_back_through_the_viewers_own_decoder()
    {
        var m = ModelImport.Read("box.glb", TestModels.Glb(), NoSibling);
        var plan = PlanOf(m);
        var asset = Assert.Single(plan.Meshes);
        var decoded = LmvMesh.Decode(Guid.NewGuid(), asset);
        Assert.True(decoded.Ok, decoded.Refusal?.ToString());
        var high = decoded.Geometry!.Lods[3]!;
        Assert.Equal(2, high.Faces.Count);
        Assert.Equal(12, high.Triangles);
        Assert.All(decoded.Geometry.Lods, l => Assert.NotNull(l));
        // positions come back in the unit cube at the box's corners; UVs at 0 or 1
        foreach (var f in high.Faces)
        {
            Assert.All(f.Positions, v => Assert.True(MathF.Abs(MathF.Abs(v) - 0.5f) < 1e-3f, $"{v}"));
            Assert.All(f.Uvs, v => Assert.True(MathF.Abs(v) < 1e-3f || MathF.Abs(v - 1) < 1e-3f, $"{v}"));
        }
        // the first end face is the top: every vertex at z = +0.5
        var top = decoded.Geometry.Lods[3]!.Faces[1];
        Assert.Equal(0.5f, top.Positions[2], 3);
        Assert.Equal(1f, top.Normals[2], 2);
    }

    [Fact]
    public void The_mesh_asset_layout_is_a_bare_binary_header_then_zlib_blocks_in_the_official_order()
    {
        var plan = PlanOf(ModelImport.Read("box.glb", TestModels.Glb(), NoSibling), new ModelSettings(Physics: PhysicsSource.LowestLod));
        var asset = plan.Meshes[0];
        Assert.Equal((byte)'{', asset[0]);   // a binary LLSD map with no "<? LLSD/Binary ?>" line
        var header = (OSDMap)OSDParser.DeserializeLLSDBinary(asset);
        string[] order = ["physics_convex", "lowest_lod", "low_lod", "medium_lod", "high_lod", "physics_mesh"];
        Assert.Equal(order.OrderBy(x => x), header.Keys.OrderBy(x => x));
        int headerLength = OSDParser.SerializeLLSDBinary(header, false).Length;
        int expected = 0;
        foreach (var key in order)
        {
            var e = (OSDMap)header[key];
            Assert.Equal(expected, e["offset"].AsInteger());
            int at = headerLength + expected;
            Assert.Equal(0x78, asset[at]);   // a zlib stream
            expected += e["size"].AsInteger();
        }
        Assert.Equal(headerLength + expected, asset.Length);
        var high = (OSDArray)Inflate(asset, headerLength + ((OSDMap)header["high_lod"])["offset"].AsInteger(), ((OSDMap)header["high_lod"])["size"].AsInteger());
        var face = (OSDMap)high[0];
        byte[] pos = face["Position"].AsBinary();
        Assert.Equal(16 * 6, pos.Length);   // 16 vertices, three 16-bit values each
        Assert.Equal(16 * 6, face["Normal"].AsBinary().Length);
        Assert.Equal(16 * 4, face["TexCoord0"].AsBinary().Length);
        Assert.Equal(24 * 2, face["TriangleList"].AsBinary().Length);
        // little-endian: the first vertex (0.5, -0.5, -0.5) over the domain -0.5..0.5 is FFFF 0000 0000
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00 }, pos[..6]);
        Assert.Equal(-0.5, ((OSDArray)((OSDMap)face["PositionDomain"])["Min"])[0].AsReal(), 3);
        // the normal +x maps to FFFF, 0 to 7FFF or 8000
        var nrm = face["Normal"].AsBinary();
        Assert.Equal(0xFFFF, BitConverter.ToUInt16(nrm, 0));
        Assert.InRange(BitConverter.ToUInt16(nrm, 2), 0x7FFF, 0x8000);
        var hull = (OSDMap)Inflate(asset, headerLength, ((OSDMap)header["physics_convex"])["size"].AsInteger());
        Assert.Equal(8 * 6, hull["BoundingVerts"].AsBinary().Length);
        Assert.Equal(LmvModelUpload.PhysicsShapePrim, plan.Instances[0].PhysicsShapeType);
        // with the official default (no physics model): no physics_mesh, a convex hull
        var none = PlanOf(ModelImport.Read("box.glb", TestModels.Glb(), NoSibling), new ModelSettings());
        Assert.False(((OSDMap)OSDParser.DeserializeLLSDBinary(none.Meshes[0])).ContainsKey("physics_mesh"));
        Assert.Equal(LmvModelUpload.PhysicsShapeConvexHull, none.Instances[0].PhysicsShapeType);
    }

    private static OSD Inflate(byte[] asset, int offset, int size)
    {
        using var z = new ZLibStream(new MemoryStream(asset, offset, size), CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return OSDParser.DeserializeLLSDBinary(o.ToArray());
    }

    [Fact]
    public void An_empty_face_in_a_lod_is_written_as_no_geometry()
    {
        var face = new ModelFace([], [], [], [], 0);
        var block = LmvModelUpload.LodBlock([face, Sphere(4, 4)]);
        Assert.True(((OSDMap)block[0])["NoGeometry"].AsBoolean());
        Assert.True(((OSDMap)block[1]).ContainsKey("TriangleList"));
    }

    // ---------------- the request ----------------

    [Fact]
    public void The_fee_request_carries_the_model_with_empty_texture_bytes_and_the_upload_carries_the_textures()
    {
        var m = ModelImport.Read("box.glb", TestModels.Glb(nodes: 2), NoSibling);
        var folder = Guid.NewGuid();
        var plan = LmvModelUpload.Plan(m, new ModelSettings(IncludeTextures: true, Scale: 2f), "boxes", "", folder, Guid.Empty, img => LmvModelUpload.EncodeTexture(img, null));
        var body = LmvModelUpload.RequestBody(plan with { ExpectedCost = 0 });
        Assert.Equal("mesh", body["asset_type"].AsString());
        Assert.Equal("object", body["inventory_type"].AsString());
        Assert.Equal("boxes", body["name"].AsString());
        Assert.Equal("(No Description)", body["description"].AsString());
        Assert.Equal(folder, body["folder_id"].AsUUID().Guid);
        Assert.Equal((int)LmvBuild.UploadNextOwnerMask, body["next_owner_mask"].AsInteger());
        var res = (OSDMap)body["asset_resources"];
        Assert.Equal(2, ((OSDArray)res["mesh_list"]).Count);
        var textures = (OSDArray)res["texture_list"];
        Assert.Equal(2, textures.Count);
        Assert.All(textures, t => Assert.Empty(t.AsBinary()));
        var sent = LmvModelUpload.SendBody(plan);
        Assert.All((OSDArray)sent["texture_list"], t => Assert.True(t.AsBinary().Length > 0));
        var instances = (OSDArray)res["instance_list"];
        Assert.Equal(2, instances.Count);
        var second = (OSDMap)instances[1];
        Assert.Equal(1, second["mesh"].AsInteger());
        Assert.Equal(LmvModelUpload.MaterialWood, second["material"].AsInteger());
        // scale 2: each box is 2 m, the second centred 6 m along glTF x (the viewer's x)
        Assert.Equal(2.0, ((OSDArray)second["scale"])[0].AsReal(), 3);
        Assert.Equal(6.0, ((OSDArray)second["position"])[0].AsReal(), 3);
        var faces = (OSDArray)second["face_list"];
        Assert.Equal(2, faces.Count);
        Assert.Equal(0, ((OSDMap)faces[0])["image"].AsInteger());
        Assert.Equal(1, ((OSDMap)faces[1])["image"].AsInteger());
        Assert.Equal(4, ((OSDArray)((OSDMap)faces[0])["diffuse_color"]).Count);
        // without textures: no image on any face and no texture list entries
        var plain = LmvModelUpload.Plan(m, new ModelSettings(IncludeTextures: false), "boxes", "", folder, Guid.Empty, img => LmvModelUpload.EncodeTexture(img, null));
        var plainRes = LmvModelUpload.AssetResources(plain, true);
        Assert.Empty((OSDArray)plainRes["texture_list"]);
        Assert.All((OSDArray)plainRes["instance_list"], i => Assert.All((OSDArray)((OSDMap)i)["face_list"], f => Assert.False(((OSDMap)f).ContainsKey("image"))));
    }

    [Fact]
    public void The_fee_answer_gives_the_price_and_the_weights()
    {
        var reply = new OSDMap
        {
            ["state"] = "upload", ["uploader"] = "http://grid.example.org:9000/cap/up", ["upload_price"] = OSD.FromInteger(12),
            ["data"] = new OSDMap { ["resource_cost"] = OSD.FromReal(2.5), ["model_streaming_cost"] = OSD.FromReal(1.5), ["physics_cost"] = OSD.FromReal(0.4), ["simulation_cost"] = OSD.FromReal(0.5) },
        };
        var t = LmvModelUpload.ParseTicket(reply);
        Assert.True(t.Ok);
        Assert.Equal(12, t.Price);
        Assert.Equal(2.5, t.LandImpact);
        Assert.Equal(1.5, t.Streaming);
        Assert.Equal(0.4, t.Physics);
        Assert.Equal(0.5, t.Simulation);
        var refused = LmvModelUpload.ParseTicket(new OSDMap { ["state"] = "error", ["error"] = new OSDMap { ["message"] = "not enough money" } });
        Assert.False(refused.Ok);
        Assert.Equal("not enough money", refused.Error);
    }

    // ---------------- bulk ----------------

    private sealed class FakeModels : IModelUploadService
    {
        public List<(string Name, int Expected)> Requests = [];
        public List<string> Sent = [];
        public Func<string, int> Price = _ => 10;
        public Func<string, bool> SendFails = _ => false;

        public Task<ModelTicket> RequestModelAsync(ModelUploadPlan plan, CancellationToken ct)
        {
            Requests.Add((plan.Name, plan.ExpectedCost));
            return Task.FromResult(new ModelTicket(new Uri("http://grid.example.org/up/" + plan.Name), Price(plan.Name), 1.5, 1, 0.5, 0.5, null));
        }

        public Task<UploadDone> SendModelAsync(Uri uploader, ModelUploadPlan plan, CancellationToken ct)
        {
            Sent.Add(plan.Name);
            return Task.FromResult(SendFails(plan.Name) ? new UploadDone(Guid.Empty, Guid.Empty, "asset_failed") : new UploadDone(Guid.NewGuid(), Guid.NewGuid(), null));
        }
    }

    private static ModelJob Job(string name) =>
        new(name, () => new ModelUploadPlan(name, "", Guid.Empty, Guid.Empty, [[1]], [], []));

    [Fact]
    public async Task A_bulk_upload_asks_each_fee_first_then_sends_one_at_a_time_and_one_failure_does_not_stop_the_rest()
    {
        var svc = new FakeModels { SendFails = n => n == "b" };
        var jobs = new[] { Job("a"), new ModelJob("broken", () => null, ModelRefusals.Unreadable), Job("b"), Job("c") };
        var batch = new ModelUploadBatch(svc, jobs);
        Assert.Equal(UploadItemState.Failed, batch.States[1].State);
        Assert.Equal(ModelRefusals.Unreadable, batch.States[1].Error);
        await batch.CheckFeesAsync(CancellationToken.None);
        Assert.Equal([("a", 0), ("b", 0), ("c", 0)], svc.Requests);
        Assert.Empty(svc.Sent);
        Assert.Equal(30, batch.TotalFee);
        Assert.Equal(1.5, batch.States[0].Fee!.LandImpact);
        svc.Requests.Clear();
        await batch.RunAsync(CancellationToken.None);
        Assert.Equal([("a", 10), ("b", 10), ("c", 10)], svc.Requests);
        Assert.Equal(["a", "b", "c"], svc.Sent);
        Assert.Equal([UploadItemState.Done, UploadItemState.Failed, UploadItemState.Failed, UploadItemState.Done], batch.States.Select(s => s.State));
        Assert.Equal("asset_failed", batch.States[2].Error);
        Assert.Equal(2, batch.Uploaded);
    }

    [Fact]
    public async Task A_model_whose_fee_changed_is_not_sent()
    {
        var svc = new FakeModels();
        var batch = new ModelUploadBatch(svc, [Job("a")]);
        await batch.CheckFeesAsync(CancellationToken.None);
        svc.Price = _ => 25;
        await batch.RunAsync(CancellationToken.None);
        Assert.Empty(svc.Sent);
        Assert.Equal("price_changed", batch.States[0].Error);
        // nothing goes before the fee is known
        var fresh = new FakeModels();
        await new ModelUploadBatch(fresh, [Job("x")]).RunAsync(CancellationToken.None);
        Assert.Empty(fresh.Requests);
    }

    // ---------------- image prices (llagentbenefits.cpp) ----------------

    [Fact]
    public void An_image_price_follows_the_account_benefits_with_the_2k_rule()
    {
        Assert.Null(TexturePrice.For(512, 512, null, null));            // no benefits (OpenSim): the upload's answer prices it
        Assert.Equal(10, TexturePrice.For(1024, 1024, 10, [50, 100]));  // 1024×1024 is not above the 2K area
        Assert.Equal(50, TexturePrice.For(2048, 1024, 10, [100, 50]));  // the lowest of the large prices
        Assert.Equal(10, TexturePrice.For(2048, 2048, 10, []));         // no large prices listed: the plain price
        Assert.Equal(10, TexturePrice.For(2048, 2048, 10, null));
        Assert.Equal(1024 * 1024 + 1, TexturePrice.Min2KArea);
        Assert.True(TexturePrice.Offers2K(10, [50]));
        Assert.False(TexturePrice.Offers2K(10, []));
        Assert.True(TexturePrice.Offers2K(null, null));
    }

    [Fact]
    public void Allow_2048_keeps_large_images_at_up_to_2048_and_the_default_stays_1024()
    {
        Assert.Equal((1024, 1024), UploadImageRules.TargetSize(4000, 4000));
        Assert.Equal((2048, 2048), UploadImageRules.TargetSize(4000, 4000, allow2K: true));
        Assert.Equal((2048, 1024), UploadImageRules.TargetSize(2048, 1024, allow2K: true));
        Assert.Equal((512, 512), UploadImageRules.TargetSize(512, 512, allow2K: true));
    }

    [Fact]
    public void Checking_images_with_allow_2048_keeps_a_large_image_at_2048()
    {
        var big = new UploadSource("big.png", 3000, 3000, new byte[3000 * 3000 * 4]);
        Assert.Equal(1024, UploadChecks.Check([big]).Single().TargetWidth);
        Assert.Equal(2048, UploadChecks.Check([big], allow2K: true).Single().TargetWidth);
    }

    [Fact]
    public void The_image_upload_cost_line_uses_the_benefit_price_and_falls_back_to_the_grids_answer()
    {
        Assert.Equal(50, UploadCost.PerItem(benefitPrice: 50, answerPrice: 0));
        Assert.Equal(7, UploadCost.PerItem(benefitPrice: null, answerPrice: 7));
        Assert.Null(UploadCost.PerItem(benefitPrice: null, answerPrice: null));
    }

}
