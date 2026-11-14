// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.IO.Compression;
using System.Numerics;
using LibreMetaverse;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using LibreMetaverse.StructuredData;
using Viewer.Core.Building;

namespace Viewer.Core.Protocol;

/// <summary>
/// A model upload in the grid's format, written from the official viewer's behaviour (llmodel.cpp writeModel, llmeshrepository.cpp
/// wholeModelToLLSD and the two-step upload; read as behaviour, no code copied).
/// <para>The mesh asset: a binary LLSD header (no "LLSD/Binary" line) mapping each block's name to its offset after the header and its size,
/// then the blocks in the order physics_convex, lowest_lod, low_lod, medium_lod, high_lod, physics_mesh, each a binary LLSD value compressed
/// as a zlib stream. A LOD block is an array of faces; a face has its positions as unsigned 16-bit numbers over the LOD's position domain,
/// its normals mapped from -1..1, its UVs over the face's own UV domain, and its triangles as 16-bit indices, all little-endian. A face
/// without triangles is written as NoGeometry. With no physics model, physics_convex holds one hull (Min, Max, BoundingVerts) and the prim's
/// physics shape is a convex hull; with the lowest LOD as physics, that LOD is also written as physics_mesh and the shape is the prim.</para>
/// <para>The request: asset type mesh, inventory type object, the item's name, description, folders and the official upload permissions, and
/// asset_resources with the meshes, textures and one instance per prim (position, rotation, scale, material wood, physics shape, the mesh's
/// index and its faces' colours, and each face's texture index only when textures go with the model). The first step carries empty texture
/// bytes, the second the textures themselves.</para>
/// </summary>
public static class LmvModelUpload
{
    public const int PhysicsShapePrim = 0, PhysicsShapeNone = 1, PhysicsShapeConvexHull = 2;

    /// <summary>The prim material of an uploaded model (wood, as official).</summary>
    public const int MaterialWood = 3;

    public const string Lowest = "lowest_lod", Low = "low_lod", Medium = "medium_lod", High = "high_lod", PhysicsMesh = "physics_mesh", PhysicsConvex = "physics_convex";

    private static readonly System.Collections.Immutable.ImmutableArray<string> LodNames = [Lowest, Low, Medium, High];

    // ---------------- the mesh asset ----------------

    /// <summary>The mesh asset of one prim: four LODs (index 0 lowest .. 3 high; positions already within -0.5..0.5) and the physics choice.</summary>
    public static byte[] WriteMeshAsset(IReadOnlyList<ModelFace>[] lods, PhysicsSource physics)
    {
        var blocks = new List<(string Name, byte[] Data)>();
        var lowest = lods[0];
        var (min, max) = Bounds(lowest.Any(f => f.Triangles > 0) ? lowest : lods[^1]);
        blocks.Add((PhysicsConvex, Zip(HullBlock(min, max))));
        for (int i = 0; i < LodNames.Length; i++)
            blocks.Add((LodNames[i], Zip(LodBlock(lods[i]))));
        if (physics == PhysicsSource.LowestLod)
            blocks.Add((PhysicsMesh, Zip(LodBlock(lowest))));
        var header = new OSDMap();
        int offset = 0;
        foreach (var (name, data) in blocks)
        {
            header[name] = new OSDMap { ["offset"] = OSD.FromInteger(offset), ["size"] = OSD.FromInteger(data.Length) };
            offset += data.Length;
        }
        using var ms = new MemoryStream();
        ms.Write(OSDParser.SerializeLLSDBinary(header, false));
        foreach (var (_, data) in blocks) ms.Write(data);
        return ms.ToArray();
    }

    private static (Vector3, Vector3) Bounds(IEnumerable<ModelFace> faces)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var f in faces)
            for (int i = 0; i + 2 < f.Positions.Length; i += 3)
            {
                var p = new Vector3(f.Positions[i], f.Positions[i + 1], f.Positions[i + 2]);
                min = Vector3.Min(min, p); max = Vector3.Max(max, p); any = true;
            }
        return any ? (min, max) : (new Vector3(-0.5f), new Vector3(0.5f));
    }

    private static OSDArray V(Vector3 v) => [OSD.FromReal(v.X), OSD.FromReal(v.Y), OSD.FromReal(v.Z)];

    private static ushort Q(float v, float min, float range) => (ushort)Math.Clamp(Math.Round((v - min) / range * 65535.0), 0, 65535);

    /// <summary>A LOD's faces: positions over the LOD's whole domain, UVs over each face's own.</summary>
    public static OSDArray LodBlock(IReadOnlyList<ModelFace> faces)
    {
        var (min, max) = Bounds(faces);
        var range = Vector3.Max(max - min, new Vector3(1e-6f));
        var array = new OSDArray();
        foreach (var f in faces)
        {
            if (f.Triangles == 0 || f.Vertices < 3)
            {
                array.Add(new OSDMap { ["NoGeometry"] = OSD.FromBoolean(true) });
                continue;
            }
            int n = f.Vertices;
            var pos = new byte[n * 6]; var nrm = new byte[n * 6]; var uv = new byte[n * 4];
            var uvMin = new Vector2(float.MaxValue); var uvMax = new Vector2(float.MinValue);
            for (int i = 0; i < n; i++)
            {
                var t = new Vector2(f.Uvs[i * 2], f.Uvs[i * 2 + 1]);
                uvMin = Vector2.Min(uvMin, t); uvMax = Vector2.Max(uvMax, t);
            }
            var uvRange = Vector2.Max(uvMax - uvMin, new Vector2(1e-6f));
            for (int i = 0; i < n; i++)
            {
                for (int c = 0; c < 3; c++)
                {
                    BitConverter.TryWriteBytes(pos.AsSpan(i * 6 + c * 2), Q(f.Positions[i * 3 + c], min[c], range[c]));
                    float nv = i * 3 + c < f.Normals.Length ? Math.Clamp(f.Normals[i * 3 + c], -1f, 1f) : 0f;
                    BitConverter.TryWriteBytes(nrm.AsSpan(i * 6 + c * 2), Q(nv, -1f, 2f));
                }
                BitConverter.TryWriteBytes(uv.AsSpan(i * 4), Q(f.Uvs[i * 2], uvMin.X, uvRange.X));
                BitConverter.TryWriteBytes(uv.AsSpan(i * 4 + 2), Q(f.Uvs[i * 2 + 1], uvMin.Y, uvRange.Y));
            }
            var tris = new byte[f.Indices.Length * 2];
            for (int i = 0; i < f.Indices.Length; i++)
                BitConverter.TryWriteBytes(tris.AsSpan(i * 2), (ushort)f.Indices[i]);
            array.Add(new OSDMap
            {
                ["PositionDomain"] = new OSDMap { ["Min"] = V(min), ["Max"] = V(max) },
                ["Position"] = OSD.FromBinary(pos),
                ["Normal"] = OSD.FromBinary(nrm),
                ["TexCoord0Domain"] = new OSDMap { ["Min"] = new OSDArray { OSD.FromReal(uvMin.X), OSD.FromReal(uvMin.Y) }, ["Max"] = new OSDArray { OSD.FromReal(uvMax.X), OSD.FromReal(uvMax.Y) } },
                ["TexCoord0"] = OSD.FromBinary(uv),
                ["TriangleList"] = OSD.FromBinary(tris),
            });
        }
        return array;
    }

    /// <summary>The single hull uploaded when no physics model is given: the corners of the lowest LOD's bounds, over those bounds.</summary>
    private static OSDMap HullBlock(Vector3 min, Vector3 max)
    {
        var range = Vector3.Max(max - min, new Vector3(1e-6f));
        var verts = new byte[8 * 6];
        for (int k = 0; k < 8; k++)
        {
            var p = new Vector3((k & 1) != 0 ? max.X : min.X, (k & 2) != 0 ? max.Y : min.Y, (k & 4) != 0 ? max.Z : min.Z);
            for (int c = 0; c < 3; c++)
                BitConverter.TryWriteBytes(verts.AsSpan(k * 6 + c * 2), Q(p[c], min[c], range[c]));
        }
        return new OSDMap { ["Min"] = V(min), ["Max"] = V(max), ["BoundingVerts"] = OSD.FromBinary(verts) };
    }

    private static byte[] Zip(OSD value)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(OSDParser.SerializeLLSDBinary(value, false));
        return ms.ToArray();
    }

    // ---------------- the plan ----------------

    /// <summary>
    /// The upload of a model read from a file: one prim per mesh, its positions scaled by the setting and centred and normalised into the
    /// unit cube (the prim's scale is its size in metres, its position its centre), its LODs, and its faces' colours and textures.
    /// <paramref name="encodeTexture"/> makes an image's J2C bytes (null when it cannot); textures are used only when the settings include them.
    /// </summary>
    public static ModelUploadPlan Plan(ImportedModel model, ModelSettings settings, string name, string description, Guid folderId, Guid textureFolderId,
        Func<ModelImage, byte[]?> encodeTexture)
    {
        float scale = Math.Clamp(settings.Scale, ModelSettings.MinScale, ModelSettings.MaxScale);
        var meshes = new List<byte[]>();
        var instances = new List<PlannedInstance>();
        var textures = new List<byte[]>();
        var textureOf = new Dictionary<int, int>();
        int triangles = 0;
        foreach (var mesh in model.Meshes)
        {
            var (bmin, bmax) = mesh.Bounds();
            bmin *= scale; bmax *= scale;
            var centre = (bmin + bmax) / 2;
            var size = Vector3.Max(bmax - bmin, new Vector3(0.01f));
            var faces = mesh.Faces.Select(f =>
            {
                var p = new float[f.Positions.Length];
                for (int i = 0; i + 2 < p.Length; i += 3)
                    for (int c = 0; c < 3; c++)
                        p[i + c] = (f.Positions[i + c] * scale - centre[c]) / size[c];
                return f with { Positions = p };
            }).ToArray();
            var lods = ModelLods.Build(new ModelMesh(mesh.Name, faces), settings.AutoLods);
            triangles += faces.Sum(f => f.Triangles);
            meshes.Add(WriteMeshAsset(lods, settings.Physics));
            var planned = new List<PlannedFace>();
            foreach (var f in faces)
            {
                var mat = f.Material >= 0 && f.Material < model.Materials.Count ? model.Materials[f.Material] : ModelMaterial.Default;
                int image = -1;
                if (settings.IncludeTextures && mat.BaseColorImage >= 0 && mat.BaseColorImage < model.Images.Count)
                {
                    if (!textureOf.TryGetValue(mat.BaseColorImage, out image))
                    {
                        var j2c = model.Images[mat.BaseColorImage].Encoded.Length > 0 ? encodeTexture(model.Images[mat.BaseColorImage]) : null;
                        image = j2c is null ? -1 : textures.Count;
                        if (j2c is not null) textures.Add(j2c);
                        textureOf[mat.BaseColorImage] = image;
                    }
                }
                planned.Add(new PlannedFace(mat.BaseColor, image));
            }
            instances.Add(new PlannedInstance(meshes.Count - 1, mesh.Name, centre, Quaternion.Identity, size,
                settings.Physics == PhysicsSource.LowestLod ? PhysicsShapePrim : PhysicsShapeConvexHull, planned));
        }
        return new ModelUploadPlan(name, description, folderId, textureFolderId, meshes, textures, instances) { Triangles = triangles };
    }

    /// <summary>An image's J2C bytes at the size an upload arrives at: PNG read here, anything else by <paramref name="decode"/> (the engine's
    /// image reader in the viewer); null when neither can read it.</summary>
    public static byte[]? EncodeTexture(ModelImage image, Func<byte[], (int Width, int Height, byte[] Rgba)?>? decode)
    {
        var px = PngDecoder.Decode(image.Encoded) ?? decode?.Invoke(image.Encoded);
        if (px is not { } p || p.Width <= 0 || p.Height <= 0 || p.Width > UploadImageRules.MaxSourceDimension || p.Height > UploadImageRules.MaxSourceDimension)
            return null;
        var (w, h) = UploadImageRules.TargetSize(p.Width, p.Height);
        var scaled = UploadChecks.Scale(p.Rgba, p.Width, p.Height, w, h);
        return LmvTexture.EncodeUpload(w, h, scaled, UploadChecks.HasAlpha(scaled));
    }

    // ---------------- the request ----------------

    /// <summary>The asset resources: the meshes, the textures (their bytes only when <paramref name="withTextureBytes"/>, else empty, as the fee
    /// step sends them), and the prims.</summary>
    public static OSDMap AssetResources(ModelUploadPlan plan, bool withTextureBytes)
    {
        var instances = new OSDArray();
        foreach (var inst in plan.Instances)
        {
            var faces = new OSDArray();
            foreach (var f in inst.Faces)
            {
                var face = new OSDMap
                {
                    ["diffuse_color"] = new OSDArray { OSD.FromReal(f.Color.X), OSD.FromReal(f.Color.Y), OSD.FromReal(f.Color.Z), OSD.FromReal(f.Color.W) },
                    ["fullbright"] = OSD.FromBoolean(f.Fullbright),
                };
                if (f.Image >= 0)
                {
                    face["image"] = OSD.FromInteger(f.Image);
                    face["scales"] = OSD.FromReal(1); face["scalet"] = OSD.FromReal(1);
                    face["offsets"] = OSD.FromReal(0); face["offsett"] = OSD.FromReal(0);
                    face["imagerot"] = OSD.FromReal(0);
                }
                faces.Add(face);
            }
            instances.Add(new OSDMap
            {
                ["position"] = V(inst.Position),
                ["rotation"] = new OSDArray { OSD.FromReal(inst.Rotation.X), OSD.FromReal(inst.Rotation.Y), OSD.FromReal(inst.Rotation.Z), OSD.FromReal(inst.Rotation.W) },
                ["scale"] = V(inst.Scale),
                ["material"] = OSD.FromInteger(MaterialWood),
                ["physics_shape_type"] = OSD.FromInteger(inst.PhysicsShapeType),
                ["mesh"] = OSD.FromInteger(inst.Mesh),
                ["mesh_name"] = OSD.FromString(inst.MeshName),
                ["face_list"] = faces,
            });
        }
        var meshList = new OSDArray();
        foreach (var m in plan.Meshes) meshList.Add(OSD.FromBinary(m));
        var textureList = new OSDArray();
        foreach (var t in plan.Textures) textureList.Add(OSD.FromBinary(withTextureBytes ? t : []));
        return new OSDMap
        {
            ["instance_list"] = instances,
            ["mesh_list"] = meshList,
            ["texture_list"] = textureList,
            ["metric"] = OSD.FromString("MUT_Unspecified"),
        };
    }

    /// <summary>The first step's body: the whole request with empty texture bytes.</summary>
    public static OSDMap RequestBody(ModelUploadPlan plan) => new()
    {
        ["folder_id"] = OSD.FromUUID(new UUID(plan.FolderId)),
        ["texture_folder_id"] = OSD.FromUUID(new UUID(plan.TextureFolderId)),
        ["asset_type"] = "mesh",
        ["inventory_type"] = "object",
        ["name"] = plan.Name,
        ["description"] = plan.Description.Length > 0 ? plan.Description : "(No Description)",
        ["next_owner_mask"] = OSD.FromInteger((int)LmvBuild.UploadNextOwnerMask),
        ["group_mask"] = OSD.FromInteger(0),
        ["everyone_mask"] = OSD.FromInteger(0),
        ["expected_upload_cost"] = OSD.FromInteger(plan.ExpectedCost),
        ["asset_resources"] = AssetResources(plan, false),
    };

    /// <summary>The second step's body: the asset resources with the textures' bytes.</summary>
    public static OSDMap SendBody(ModelUploadPlan plan) => AssetResources(plan, true);

    /// <summary>The first step's answer: the uploader, the fee and the weights (land impact = resource_cost), or an error.</summary>
    public static ModelTicket ParseTicket(OSD? reply)
    {
        var t = LmvBuild.ParseTicket(reply);
        if (!t.Ok)
            return new ModelTicket(null, 0, 0, 0, 0, 0, t.Error);
        var data = reply is OSDMap m && m.TryGetValue("data", out var d) && d is OSDMap dm ? dm : new OSDMap();
        double R(string key) => data.TryGetValue(key, out var v) ? v.AsReal() : 0;
        return new ModelTicket(t.Uploader, t.Price, R("resource_cost"), R("model_streaming_cost"), R("physics_cost"), R("simulation_cost"), null);
    }
}
