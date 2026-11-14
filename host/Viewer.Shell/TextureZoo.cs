// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// L4 "texture zoo": our own generated 64x64 textures on prims and a mesh: plain, repeats, offset, rotation, planar
/// texgen, tint, fullbright, a blended-alpha texture, and colour alpha on an opaque texture. The fixture
/// (fixtures/textures/texture-zoo.texzoo.json) is this serialisation plus the image and mesh maps; the images are
/// encoded with LibreMetaverse's encoder (fixtures/textures/*.j2c).
/// </summary>
public static class TextureZoo
{
    public const string Format = "nv-texzoo-1";
    public static readonly Guid Grid = new("4e560007-0000-4000-8000-00000000000a");
    public static readonly Guid Gradient = new("4e560007-0000-4000-8000-00000000000b");
    public static readonly Guid AlphaBlend = new("4e560007-0000-4000-8000-00000000000c");

    public static readonly IReadOnlyDictionary<Guid, string> Images = new Dictionary<Guid, string>
    {
        [Grid] = "tex-grid-arrow.j2c", [Gradient] = "tex-gradient.j2c", [AlphaBlend] = "tex-alpha-blend.j2c",
    };

    /// <summary>The source pixels (RGBA8, top row first) and whether the image carries alpha.</summary>
    public static (byte[] Rgba, bool Alpha) Pixels(Guid id)
    {
        const int n = 64;
        var b = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = (y * n + x) * 4;
                byte r, g, bl, a = 255;
                if (id == Grid)
                {
                    bool check = (x / 8 + y / 8) % 2 == 0;
                    (r, g, bl) = check ? ((byte)235, (byte)235, (byte)235) : ((byte)40, (byte)60, (byte)150);
                    if (x < 32 && y < 32 && x >= y) (r, g, bl) = ((byte)230, (byte)40, (byte)30);   // a triangle in the top-left: shows rotation and flips
                    if (x >= 56 && y >= 56) (r, g, bl) = ((byte)40, (byte)200, (byte)60);          // a green block bottom-right
                }
                else if (id == Gradient) (r, g, bl) = ((byte)(x * 4), (byte)(255 - y * 4), (byte)90);
                else
                {
                    (r, g, bl) = ((byte)250, (byte)200, (byte)40);
                    a = (byte)Math.Clamp(255 - (int)(Math.Sqrt((x - 32) * (x - 32) + (y - 32) * (y - 32)) * 8), 0, 255);
                }
                b[i] = r; b[i + 1] = g; b[i + 2] = bl; b[i + 3] = a;
            }
        return (b, id == AlphaBlend);
    }

    public static IReadOnlyList<ZooPrim> Make()
    {
        var o = PrimZoo.Origin;
        var plain = FaceTexture.Plain(Grid);
        var s = new Vector3(1.6f, 1.6f, 1.6f);
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.35f);
        var white = new FaceColors(Rgba.White);
        var cyl = new PrimShape { ProfileCurve = 0, PathCurve = 16 };
        var sphere = new PrimShape { ProfileCurve = 5, PathCurve = 32 };
        ZooPrim P(uint id, string name, int col, int row, PrimShape shape, FaceColors c, FaceTextures t) =>
            new(id, 0, name, o + new Vector3(col * 3f, row * 3f, 0), q, s, shape, c, t);
        return
        [
            P(5001, "plain", 0, 0, PrimShape.Box, white, new(plain)),
            P(5002, "repeats-2x2", 1, 0, PrimShape.Box, white, new(plain with { RepeatU = 2, RepeatV = 2 })),
            P(5003, "offset-quarter", 2, 0, PrimShape.Box, white, new(plain with { OffsetU = 0.25f, OffsetV = 0.1f })),
            P(5004, "rotation-45", 3, 0, PrimShape.Box, white, new(plain with { Rotation = MathF.PI / 4 })),
            P(5005, "planar-cylinder", 4, 0, cyl, white, new(plain with { TexGen = TexGen.Planar })),
            P(5006, "tinted-sphere", 0, 1, sphere, new FaceColors(new Rgba(1f, 0.45f, 0.35f, 1)), new(FaceTexture.Plain(Gradient))),
            P(5007, "fullbright", 1, 1, PrimShape.Box, white, new(plain with { Fullbright = true })),
            P(5008, "alpha-texture", 2, 1, PrimShape.Box, white, new(FaceTexture.Plain(AlphaBlend))),
            P(5009, "colour-alpha-half", 3, 1, PrimShape.Box, new FaceColors(new Rgba(1, 1, 1, 0.5f)), new(plain)),
            new(5010, 0, "mesh-cube", o + new Vector3(4 * 3f, 3f, 0), q, s, PrimShape.Box with { SculptType = PrimShape.SculptTypeMesh, SculptId = MeshZoo.Assets.Keys.First() },
                white, new FaceTextures(plain, [null, FaceTexture.Plain(Gradient), plain with { Rotation = MathF.PI / 2 }, null, plain with { RepeatU = 3, RepeatV = 3 }, null])),
        ];
    }

    public static string Serialise(IReadOnlyList<ZooPrim> prims)
    {
        var root = JsonNode.Parse(PrimZoo.Serialise(prims))!.AsObject();
        root["format"] = Format;
        var images = new JsonObject();
        foreach (var (id, f) in Images) images[id.ToString()] = f;
        root["images"] = images;
        var meshes = new JsonObject();
        foreach (var (id, f) in MeshZoo.Assets) meshes[id.ToString()] = "../meshes/" + f;
        root["meshes"] = meshes;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }

    public static (IReadOnlyList<ZooPrim> Prims, IReadOnlyDictionary<Guid, string> Images, IReadOnlyDictionary<Guid, string> Meshes) Parse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if ((string?)root["format"] != Format) throw new InvalidDataException("texture zoo: unknown format");
        var images = root["images"]!.AsObject().ToDictionary(kv => Guid.Parse(kv.Key), kv => (string)kv.Value!);
        var meshes = root["meshes"]!.AsObject().ToDictionary(kv => Guid.Parse(kv.Key), kv => (string)kv.Value!);
        root["format"] = PrimZoo.Format;
        root.Remove("images");
        root.Remove("meshes");
        return (PrimZoo.Parse(root.ToJsonString()), images, meshes);
    }
}
