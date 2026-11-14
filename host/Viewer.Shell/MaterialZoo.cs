// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// L4 "material zoo": PBR materials (glTF 2.0 material assets) on prims and a mesh, each showing one part of the
/// material: base colour texture, factors only, metal, normal map, an ORM texture (occlusion, roughness, metallic), an
/// emissive texture, a texture transform, a glTF override (texture, base colour and rotation replaced), a mesh with PBR
/// and legacy faces side by side, and a legacy-textured box for comparison. Every PBR face also carries a legacy
/// texture entry, so a face whose material is not drawn shows the legacy look. Our own generated 64x64 textures,
/// encoded with LibreMetaverse's encoder, and material assets written as the grid serves them (LLSD XML holding the
/// glTF JSON); the fixture (fixtures/materials/material-zoo.matzoo.json) is this serialisation plus the file maps.
/// </summary>
public static class MaterialZoo
{
    public const string Format = "nv-matzoo-1";

    private static Guid Id(int n) => new($"4e560009-0000-4000-8000-0000000000{n:x2}");

    public static readonly Guid BaseTex = Id(0x01), NormalTex = Id(0x02), OrmTex = Id(0x03), EmissiveTex = Id(0x04), AltBaseTex = Id(0x05);
    public static readonly Guid MBase = Id(0x11), MFactor = Id(0x12), MMetal = Id(0x13), MNormal = Id(0x14), MOrm = Id(0x15),
        MEmissive = Id(0x16), MTransform = Id(0x17), MFull = Id(0x18);

    // the alpha zoo (MakeAlpha): a texture with alpha bands, a specular map, PBR materials in each alpha mode, and the
    // region's legacy materials (served through RenderMaterials, not assets)
    public static readonly Guid AlphaTex = Id(0x06), SpecTex = Id(0x07);
    public static readonly Guid MBlend = Id(0x19), MMask = Id(0x1a), MMaskDouble = Id(0x1b);
    public static readonly Guid LNone = Id(0x21), LBlend = Id(0x22), LMask = Id(0x23), LMaskHigh = Id(0x24), LEmissive = Id(0x25),
        LNormSpec = Id(0x26), LNormOnly = Id(0x27);

    public static readonly IReadOnlyDictionary<Guid, string> Images = new Dictionary<Guid, string>
    {
        [BaseTex] = "mat-base.j2c", [NormalTex] = "mat-normal.j2c", [OrmTex] = "mat-orm.j2c", [EmissiveTex] = "mat-emissive.j2c", [AltBaseTex] = "mat-alt-base.j2c",
    };

    public static readonly IReadOnlyDictionary<Guid, string> Materials = new Dictionary<Guid, string>
    {
        [MBase] = "mat-base.material", [MFactor] = "mat-factor.material", [MMetal] = "mat-metal.material", [MNormal] = "mat-normal.material",
        [MOrm] = "mat-orm.material", [MEmissive] = "mat-emissive.material", [MTransform] = "mat-transform.material", [MFull] = "mat-full.material",
    };

    /// <summary>The alpha zoo's images (the alpha texture and the specular map are its own; the others are shared).</summary>
    public static readonly IReadOnlyDictionary<Guid, string> AlphaImages = new Dictionary<Guid, string>
    {
        [BaseTex] = "mat-base.j2c", [NormalTex] = "mat-normal.j2c", [AlphaTex] = "mat-alpha.j2c", [SpecTex] = "mat-spec.j2c",
    };

    public static readonly IReadOnlyDictionary<Guid, string> AlphaMaterials = new Dictionary<Guid, string>
    {
        [MBlend] = "mat-blend.material", [MMask] = "mat-mask.material", [MMaskDouble] = "mat-mask-double.material",
    };

    /// <summary>True for the images that carry alpha (encoded with their alpha channel).</summary>
    public static bool HasAlpha(Guid id) => id == AlphaTex;

    /// <summary>The source pixels (RGBA8, top row first) of a zoo texture; only the alpha texture carries alpha.</summary>
    public static byte[] Pixels(Guid id)
    {
        const int n = 64;
        var b = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = (y * n + x) * 4;
                byte r, g, bl;
                if (id == BaseTex)
                {
                    bool check = (x / 8 + y / 8) % 2 == 0;
                    (r, g, bl) = check ? ((byte)230, (byte)210, (byte)180) : ((byte)170, (byte)70, (byte)45);
                    if (x < 24 && y < 24 && x >= y) (r, g, bl) = ((byte)30, (byte)90, (byte)200);   // a triangle in the top-left: shows rotation and flips
                }
                else if (id == AltBaseTex)
                {
                    bool stripe = ((x + y) / 8) % 2 == 0;
                    (r, g, bl) = stripe ? ((byte)240, (byte)240, (byte)240) : ((byte)40, (byte)150, (byte)90);
                    if (x < 24 && y < 24 && x >= y) (r, g, bl) = ((byte)200, (byte)40, (byte)160);
                }
                else if (id == NormalTex)
                {
                    // a dome in each 16 px cell, in the OpenGL tangent-space convention glTF uses (+X right, +Y up the image)
                    float dx = (x % 16 - 7.5f) / 7f, dy = (y % 16 - 7.5f) / 7f;
                    float d2 = dx * dx + dy * dy;
                    var nrm = d2 < 1 ? Vector3.Normalize(new Vector3(dx, -dy, MathF.Sqrt(1 - d2))) : Vector3.UnitZ;
                    (r, g, bl) = (Unit(nrm.X), Unit(nrm.Y), Unit(nrm.Z));
                }
                else if (id == AlphaTex)
                {
                    // four horizontal bands of alpha, top to bottom: 0 (a hole), 100 (below every mask cutoff), 170 (between
                    // the cutoffs 128 and 200) and 255; a warm checker so the bands' colour shows
                    bool check = (x / 8 + y / 8) % 2 == 0;
                    (r, g, bl) = check ? ((byte)250, (byte)200, (byte)60) : ((byte)200, (byte)60, (byte)40);
                    b[i + 3] = y < 16 ? (byte)0 : y < 32 ? (byte)100 : y < 48 ? (byte)170 : (byte)255;
                    b[i] = r; b[i + 1] = g; b[i + 2] = bl;
                    continue;
                }
                else if (id == SpecTex)
                {
                    // the specular colour by quadrant: red, green, blue, white
                    (r, g, bl) = (x < n / 2, y < n / 2) switch
                    {
                        (true, true) => ((byte)255, (byte)40, (byte)40),
                        (false, true) => ((byte)40, (byte)255, (byte)40),
                        (true, false) => ((byte)40, (byte)40, (byte)255),
                        _ => ((byte)255, (byte)255, (byte)255),
                    };
                }
                else if (id == OrmTex)
                {
                    // R occlusion (a dark 4 px border), G roughness (four columns, smooth to rough), B metallic (top half 0, bottom half 1)
                    bool border = x < 4 || y < 4 || x >= n - 4 || y >= n - 4;
                    (r, g, bl) = (border ? (byte)70 : (byte)255, (byte)(40 + (x / 16) * 70), y < n / 2 ? (byte)0 : (byte)255);
                }
                else
                {
                    // emissive: a bright ring
                    float d = MathF.Sqrt((x - 31.5f) * (x - 31.5f) + (y - 31.5f) * (y - 31.5f));
                    bool ring = d > 14 && d < 22;
                    (r, g, bl) = ring ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0);
                }
                b[i] = r; b[i + 1] = g; b[i + 2] = bl; b[i + 3] = 255;
            }
        return b;
    }

    private static byte Unit(float v) => (byte)Math.Clamp((int)MathF.Round((v * 0.5f + 0.5f) * 255f), 0, 255);

    /// <summary>A material's glTF document: one material whose slots name the zoo's textures by asset id.</summary>
    public static string Gltf(Guid material)
    {
        var images = new List<Guid>();
        JsonObject Tex(Guid t, JsonObject? transform = null)
        {
            if (!images.Contains(t)) images.Add(t);
            var o = new JsonObject { ["index"] = images.IndexOf(t) };
            if (transform is not null) o["extensions"] = new JsonObject { ["KHR_texture_transform"] = transform };
            return o;
        }
        var pmr = new JsonObject();
        var m = new JsonObject();
        void Factors(float[] bc, float metal, float rough)
        {
            pmr["baseColorFactor"] = new JsonArray(bc.Select(f => (JsonNode)f).ToArray());
            pmr["metallicFactor"] = metal;
            pmr["roughnessFactor"] = rough;
        }
        if (material == MBase) { Factors([1, 1, 1, 1], 0, 0.6f); pmr["baseColorTexture"] = Tex(BaseTex); }
        else if (material == MFactor) Factors([0.8f, 0.25f, 0.15f, 1], 0, 0.35f);
        else if (material == MMetal) Factors([0.95f, 0.8f, 0.45f, 1], 1, 0.3f);
        else if (material == MNormal) { Factors([1, 1, 1, 1], 0, 0.5f); pmr["baseColorTexture"] = Tex(BaseTex); m["normalTexture"] = Tex(NormalTex); }
        else if (material == MOrm) { Factors([0.8f, 0.8f, 0.8f, 1], 1, 1); pmr["metallicRoughnessTexture"] = Tex(OrmTex); m["occlusionTexture"] = Tex(OrmTex); }
        else if (material == MEmissive)
        {
            Factors([0.1f, 0.1f, 0.1f, 1], 0, 0.8f);
            m["emissiveTexture"] = Tex(EmissiveTex);
            m["emissiveFactor"] = new JsonArray(1f, 0.6f, 0.2f);
        }
        else if (material == MTransform)
        {
            Factors([1, 1, 1, 1], 0, 0.6f);
            pmr["baseColorTexture"] = Tex(BaseTex, new JsonObject { ["offset"] = new JsonArray(0.25f, 0f), ["scale"] = new JsonArray(2f, 2f), ["rotation"] = 0.5f });
        }
        else if (material == MBlend || material == MMask || material == MMaskDouble)
        {
            Factors([1, 1, 1, 1], 0, 0.7f);
            pmr["baseColorTexture"] = Tex(AlphaTex);
            m["alphaMode"] = material == MBlend ? "BLEND" : "MASK";
            if (material != MBlend) m["alphaCutoff"] = 0.55f;   // between the texture's bands 100/255 and 170/255
            if (material == MMaskDouble) m["doubleSided"] = true;
        }
        else if (material == MFull)
        {
            Factors([1, 1, 1, 1], 1, 1);
            pmr["baseColorTexture"] = Tex(BaseTex);
            pmr["metallicRoughnessTexture"] = Tex(OrmTex);
            m["normalTexture"] = Tex(NormalTex);
            m["occlusionTexture"] = Tex(OrmTex);
            m["emissiveTexture"] = Tex(EmissiveTex);
            m["emissiveFactor"] = new JsonArray(0.25f, 0.25f, 0.25f);
        }
        else throw new ArgumentException("not a zoo material", nameof(material));
        m["pbrMetallicRoughness"] = pmr;
        var doc = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["images"] = new JsonArray(images.Select(i => (JsonNode)new JsonObject { ["uri"] = i.ToString() }).ToArray()),
            ["textures"] = new JsonArray(images.Select((_, k) => (JsonNode)new JsonObject { ["source"] = k }).ToArray()),
            ["materials"] = new JsonArray(m),
        };
        return doc.ToJsonString();
    }

    /// <summary>A material asset as the grid serves it: an LLSD XML map with the version, the type and the glTF as a string.</summary>
    public static string Asset(Guid material) =>
        "<?xml version=\"1.0\" ?>\n<llsd><map><key>version</key><string>1.1</string><key>type</key><string>GLTF 2.0</string><key>data</key><string>"
        + System.Security.SecurityElement.Escape(Gltf(material)) + "</string></map></llsd>\n";

    /// <summary>The override on the override box's every face: another base texture, a warm base colour and a 90 degree rotation.</summary>
    public static readonly string OverrideJson =
        "{\"tex\":[\"" + AltBaseTex + "\"],\"bc\":[1.0,0.85,0.6,1.0],\"ti\":[{\"r\":1.5707964}]}";

    /// <summary>The alpha zoo's legacy materials, as the region serves them (specular colour white, exponent 51, no
    /// environment, as the materials in the field).</summary>
    public static IReadOnlyList<LegacyMaterial> LegacyMaterials()
    {
        var none = new MaterialMap(Guid.Empty, 0, 0, 1, 1, 0);
        var white = new Rgba(1, 1, 1, 1);
        LegacyMaterial L(Guid id, LegacyAlphaMode mode, byte cutoff = 0, MaterialMap? n = null, MaterialMap? s = null, Rgba? sc = null, byte exp = 51) =>
            new(id, n ?? none, s ?? none, sc ?? white, exp, 0, mode, cutoff);
        return
        [
            L(LNone, LegacyAlphaMode.None), L(LBlend, LegacyAlphaMode.Blend), L(LMask, LegacyAlphaMode.Mask, 128),
            L(LMaskHigh, LegacyAlphaMode.Mask, 200), L(LEmissive, LegacyAlphaMode.Emissive),
            L(LNormSpec, LegacyAlphaMode.None, n: new MaterialMap(NormalTex, 0, 0, 2, 2, 0), s: new MaterialMap(SpecTex, 0, 0, 1, 1, 0), sc: new Rgba(1f, 0.9f, 0.7f, 1f), exp: 200),
            L(LNormOnly, LegacyAlphaMode.None, n: new MaterialMap(NormalTex, 0, 0, 1, 1, 0.5f)),
        ];
    }

    public const uint AlphaGround = 7000;

    /// <summary>
    /// The alpha zoo: legacy materials in each diffuse alpha mode (none, blend, mask at 128 and at 200, emissive) on the
    /// alpha-banded texture, a legacy normal and specular map on a sphere, a normal map alone (the default specular), a
    /// fullbright masked face, and PBR blend, mask and double-sided mask; over a textured ground, so what shows through
    /// each face is seen.
    /// </summary>
    public static IReadOnlyList<ZooPrim> MakeAlpha()
    {
        var o = PrimZoo.Origin;
        var s = new Vector3(1.6f, 1.6f, 1.6f);
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.35f);
        var white = new FaceColors(Rgba.White);
        var sphere = new PrimShape { ProfileCurve = 5, PathCurve = 32 };
        var alpha = new FaceTextures(FaceTexture.Plain(AlphaTex));
        var based = new FaceTextures(FaceTexture.Plain(BaseTex));
        ZooPrim P(uint id, string name, float col, float row, PrimShape shape, FaceTextures t) => new(id, 0, name, o + new Vector3(col * 3f, row * 3f, 0), q, s, shape, white, t);
        return
        [
            new(AlphaGround, 0, "ground", o + new Vector3(6f, 3f, -1.2f), Quaternion.Identity, new Vector3(18f, 10f, 0.1f), PrimShape.Box, white,
                new FaceTextures(FaceTexture.Plain(BaseTex) with { RepeatU = 6, RepeatV = 3 })),
            P(7001, "legacy-none", 0, 0, PrimShape.Box, alpha),
            P(7002, "legacy-blend", 1, 0, PrimShape.Box, alpha),
            P(7003, "legacy-mask-128", 2, 0, PrimShape.Box, alpha),
            P(7004, "legacy-mask-200", 3, 0, PrimShape.Box, alpha),
            P(7005, "legacy-emissive", 4, 0, PrimShape.Box, alpha),
            P(7006, "legacy-normal-specular", 0, 1, sphere, based),
            P(7007, "legacy-normal-only", 1, 1, PrimShape.Box, based),
            P(7008, "legacy-fullbright-mask", 2, 1, PrimShape.Box, new FaceTextures(FaceTexture.Plain(AlphaTex) with { Fullbright = true })),
            P(7009, "pbr-blend", 3, 1, PrimShape.Box, alpha),
            P(7010, "pbr-mask-double-sided", 4, 1, PrimShape.Box, alpha),
            P(7011, "pbr-mask", 2, 2, PrimShape.Box, alpha),
        ];
    }

    /// <summary>The alpha zoo's legacy material per object (on every face, through the texture entry).</summary>
    public static IReadOnlyDictionary<uint, Guid> AlphaFaceMaterials() => new Dictionary<uint, Guid>
    {
        [7001] = LNone, [7002] = LBlend, [7003] = LMask, [7004] = LMaskHigh, [7005] = LEmissive, [7006] = LNormSpec, [7007] = LNormOnly, [7008] = LMask,
    };

    /// <summary>The alpha zoo's PBR materials (every face of the object).</summary>
    public static IReadOnlyDictionary<uint, IReadOnlyList<RenderMaterialEntry>> AlphaRenderMaterials()
    {
        static IReadOnlyList<RenderMaterialEntry> All(Guid m) => Enumerable.Range(0, 6).Select(f => new RenderMaterialEntry(f, m)).ToList();
        return new Dictionary<uint, IReadOnlyList<RenderMaterialEntry>> { [7009] = All(MBlend), [7010] = All(MMaskDouble), [7011] = All(MMask) };
    }

    /// <summary>The alpha zoo's fixture (fixtures/materials/alpha-zoo.matzoo.json): the material zoo's format plus the
    /// region's legacy materials and each object's legacy material id.</summary>
    public static string SerialiseAlpha() =>
        Serialise(MakeAlpha(), AlphaImages, AlphaMaterials, AlphaRenderMaterials(), new Dictionary<uint, IReadOnlyList<FaceMaterialOverride>>(), LegacyMaterials(), AlphaFaceMaterials());

    public const uint OverrideBox = 6008;
    public const uint MeshCube = 6009;

    public static IReadOnlyList<ZooPrim> Make()
    {
        var o = PrimZoo.Origin;
        var s = new Vector3(1.6f, 1.6f, 1.6f);
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.35f);
        var white = new FaceColors(Rgba.White);
        var sphere = new PrimShape { ProfileCurve = 5, PathCurve = 32 };
        var legacy = new FaceTextures(FaceTexture.Plain(BaseTex));
        ZooPrim P(uint id, string name, int col, int row, PrimShape shape) => new(id, 0, name, o + new Vector3(col * 3f, row * 3f, 0), q, s, shape, white, legacy);
        return
        [
            P(6001, "pbr-base-texture", 0, 0, PrimShape.Box),
            P(6002, "pbr-factors-sphere", 1, 0, sphere),
            P(6003, "pbr-metal-sphere", 2, 0, sphere),
            P(6004, "pbr-normal-map", 3, 0, PrimShape.Box),
            P(6005, "pbr-orm", 4, 0, PrimShape.Box),
            P(6006, "pbr-emissive", 0, 1, PrimShape.Box),
            P(6007, "pbr-texture-transform", 1, 1, PrimShape.Box),
            P(OverrideBox, "pbr-override", 2, 1, PrimShape.Box),
            P(MeshCube, "mesh-pbr-and-legacy", 3, 1, PrimShape.Box with { SculptType = PrimShape.SculptTypeMesh, SculptId = MeshZoo.Assets.Keys.First() }),
            P(6010, "legacy-reference", 4, 1, PrimShape.Box),
        ];
    }

    /// <summary>Each object's PBR material per face (the render material parameter); every face of a prim not listed has none.</summary>
    public static IReadOnlyDictionary<uint, IReadOnlyList<RenderMaterialEntry>> RenderMaterials()
    {
        static IReadOnlyList<RenderMaterialEntry> All(Guid m, int faces) => Enumerable.Range(0, faces).Select(f => new RenderMaterialEntry(f, m)).ToList();
        return new Dictionary<uint, IReadOnlyList<RenderMaterialEntry>>
        {
            [6001] = All(MBase, 6), [6002] = All(MFactor, 1), [6003] = All(MMetal, 1), [6004] = All(MNormal, 6), [6005] = All(MOrm, 6),
            [6006] = All(MEmissive, 6), [6007] = All(MTransform, 6), [OverrideBox] = All(MBase, 6),
            [MeshCube] = [new(0, MFull), new(2, MFull), new(4, MFull)],
        };
    }

    /// <summary>Each object's glTF overrides per face.</summary>
    public static IReadOnlyDictionary<uint, IReadOnlyList<FaceMaterialOverride>> Overrides() => new Dictionary<uint, IReadOnlyList<FaceMaterialOverride>>
    {
        [OverrideBox] = Enumerable.Range(0, 6).Select(f => new FaceMaterialOverride(f, OverrideJson)).ToList(),
    };

    /// <summary>The PBR faces the zoo draws (prims with all faces PBR, plus the mesh's three).</summary>
    public static int PbrFaces => RenderMaterials().Values.Sum(v => v.Count);

    public static string Serialise(IReadOnlyList<ZooPrim> prims) =>
        Serialise(prims, Images, Materials, RenderMaterials(), Overrides(), [], new Dictionary<uint, Guid>());

    /// <summary>A zoo of the caller's own (the glow zoo): its prims with the images, material assets and PBR material ids it names.</summary>
    public static string SerialiseCustom(IReadOnlyList<ZooPrim> prims, IReadOnlyDictionary<Guid, string> images, IReadOnlyDictionary<Guid, string> materials,
        IReadOnlyDictionary<uint, IReadOnlyList<RenderMaterialEntry>> renderMaterials) =>
        Serialise(prims, images, materials, renderMaterials, new Dictionary<uint, IReadOnlyList<FaceMaterialOverride>>(), [], new Dictionary<uint, Guid>());

    private static string Serialise(IReadOnlyList<ZooPrim> prims, IReadOnlyDictionary<Guid, string> images, IReadOnlyDictionary<Guid, string> materials,
        IReadOnlyDictionary<uint, IReadOnlyList<RenderMaterialEntry>> renderMaterials, IReadOnlyDictionary<uint, IReadOnlyList<FaceMaterialOverride>> overrides,
        IReadOnlyList<LegacyMaterial> legacy, IReadOnlyDictionary<uint, Guid> faceMaterials)
    {
        var root = JsonNode.Parse(PrimZoo.Serialise(prims))!.AsObject();
        root["format"] = Format;
        root["images"] = Map(images);
        root["materials"] = Map(materials);
        root["meshes"] = Map(MeshZoo.Assets.ToDictionary(kv => kv.Key, kv => "../meshes/" + kv.Value));
        var rm = new JsonObject();
        foreach (var (local, faces) in renderMaterials)
            rm[local.ToString()] = new JsonArray(faces.Select(f => (JsonNode)new JsonArray(f.Face, f.MaterialId.ToString())).ToArray());
        root["render_materials"] = rm;
        var ov = new JsonObject();
        foreach (var (local, faces) in overrides)
            ov[local.ToString()] = new JsonArray(faces.Select(f => (JsonNode)new JsonArray(f.Face, f.Json)).ToArray());
        root["overrides"] = ov;
        // optional (the material zoo has none): the region's legacy materials and each object's legacy material id
        if (legacy.Count > 0)
            root["legacy_materials"] = new JsonArray(legacy.Select(m => (JsonNode)new JsonArray(m.Id.ToString(), MapJson(m.Normal), MapJson(m.Specular),
                new JsonArray(m.SpecularColor.R, m.SpecularColor.G, m.SpecularColor.B, m.SpecularColor.A), (int)m.SpecularExponent, (int)m.EnvironmentIntensity,
                (int)m.DiffuseAlphaMode, (int)m.AlphaMaskCutoff)).ToArray());
        if (faceMaterials.Count > 0)
        {
            var fm = new JsonObject();
            foreach (var (local, id) in faceMaterials) fm[local.ToString()] = id.ToString();
            root["face_materials"] = fm;
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }

    private static JsonArray MapJson(MaterialMap m) => new(m.TextureId.ToString(), m.OffsetU, m.OffsetV, m.RepeatU, m.RepeatV, m.Rotation);

    private static JsonObject Map(IReadOnlyDictionary<Guid, string> m)
    {
        var o = new JsonObject();
        foreach (var (id, f) in m) o[id.ToString()] = f;
        return o;
    }

    public sealed record Parsed(IReadOnlyList<ZooPrim> Prims, IReadOnlyDictionary<Guid, string> Images, IReadOnlyDictionary<Guid, string> Materials,
        IReadOnlyDictionary<Guid, string> Meshes, IReadOnlyDictionary<uint, IReadOnlyList<RenderMaterialEntry>> RenderMaterials,
        IReadOnlyDictionary<uint, IReadOnlyList<FaceMaterialOverride>> Overrides)
    {
        /// <summary>The region's legacy materials and each object's legacy material id (every face); empty in the material zoo.</summary>
        public IReadOnlyList<LegacyMaterial> LegacyMaterials { get; init; } = [];
        public IReadOnlyDictionary<uint, Guid> FaceMaterials { get; init; } = new Dictionary<uint, Guid>();
    }

    public static Parsed Parse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if ((string?)root["format"] != Format) throw new InvalidDataException("material zoo: unknown format");
        static Dictionary<Guid, string> Files(JsonNode? n) => n!.AsObject().ToDictionary(kv => Guid.Parse(kv.Key), kv => (string)kv.Value!);
        var images = Files(root["images"]);
        var materials = Files(root["materials"]);
        var meshes = Files(root["meshes"]);
        var rm = root["render_materials"]!.AsObject().ToDictionary(kv => uint.Parse(kv.Key),
            kv => (IReadOnlyList<RenderMaterialEntry>)kv.Value!.AsArray().Select(f => new RenderMaterialEntry((int)f![0]!, Guid.Parse((string)f[1]!))).ToList());
        var ov = root["overrides"]!.AsObject().ToDictionary(kv => uint.Parse(kv.Key),
            kv => (IReadOnlyList<FaceMaterialOverride>)kv.Value!.AsArray().Select(f => new FaceMaterialOverride((int)f![0]!, (string)f[1]!)).ToList());
        static MaterialMap Mm(JsonNode n) => new(Guid.Parse((string)n[0]!), (float)n[1]!, (float)n[2]!, (float)n[3]!, (float)n[4]!, (float)n[5]!);
        var legacy = root["legacy_materials"]?.AsArray().Select(x =>
        {
            var c = x![3]!.AsArray();
            return new LegacyMaterial(Guid.Parse((string)x[0]!), Mm(x[1]!), Mm(x[2]!), new Rgba((float)c[0]!, (float)c[1]!, (float)c[2]!, (float)c[3]!),
                (byte)(int)x[4]!, (byte)(int)x[5]!, (LegacyAlphaMode)(int)x[6]!, (byte)(int)x[7]!);
        }).ToList() ?? [];
        var fm = root["face_materials"]?.AsObject().ToDictionary(kv => uint.Parse(kv.Key), kv => Guid.Parse((string)kv.Value!)) ?? new Dictionary<uint, Guid>();
        foreach (var k in new[] { "images", "materials", "meshes", "render_materials", "overrides", "legacy_materials", "face_materials" }) root.Remove(k);
        root["format"] = PrimZoo.Format;
        return new Parsed(PrimZoo.Parse(root.ToJsonString()), images, materials, meshes, rm, ov) { LegacyMaterials = legacy, FaceMaterials = fm };
    }
}
