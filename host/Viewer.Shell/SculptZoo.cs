// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// "sculpt zoo": our own generated sculpt maps on sculpted prims: sphere, torus, plane (colour only, no texture),
/// cylinder, a mirrored and an inverted sphere, a long thin (256x16) map, and a map the asset service does not have (the
/// official stand-in sphere). Textured faces use the texture zoo's grid image, so UV orientation shows. The fixture
/// (fixtures/sculpts/sculpt-zoo.sculptzoo.json) is this serialisation plus the image map; the maps are encoded
/// LOSSLESSLY (<c>LmvTexture.EncodeLossless</c>; fixtures/sculpts/zoo/*.j2c) and reach the mesher through the
/// guarded decoder.
/// </summary>
public static class SculptZoo
{
    public const string Format = "nv-sculptzoo-1";
    public static readonly Guid SphereMap = new("4e56007b-0000-4000-8000-000000000001");
    public static readonly Guid TorusMap = new("4e56007b-0000-4000-8000-000000000002");
    public static readonly Guid PlaneMap = new("4e56007b-0000-4000-8000-000000000003");
    public static readonly Guid CylinderMap = new("4e56007b-0000-4000-8000-000000000004");
    public static readonly Guid ThinMap = new("4e56007b-0000-4000-8000-000000000005");
    /// <summary>Referenced but never provided: the asset service answers NotFound.</summary>
    public static readonly Guid MissingMap = new("4e56007b-0000-4000-8000-0000000000ff");

    public static readonly IReadOnlyDictionary<Guid, string> Images = new Dictionary<Guid, string>
    {
        [SphereMap] = "zoo/sculpt-sphere.j2c", [TorusMap] = "zoo/sculpt-torus.j2c", [PlaneMap] = "zoo/sculpt-plane.j2c",
        [CylinderMap] = "zoo/sculpt-cylinder.j2c", [ThinMap] = "zoo/sculpt-thin.j2c", [TextureZoo.Grid] = "../textures/tex-grid-arrow.j2c",
    };

    public static (int W, int H) Size(Guid id) => id == ThinMap ? (256, 16) : (64, 64);

    /// <summary>The map's pixels (RGBA8, top row first): a point per pixel, x/y/z in R/G/B (0..255 = -0.5..0.5).</summary>
    public static byte[] Pixels(Guid id)
    {
        var (w, h) = Size(id);
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double u = x / (double)(w - 1), v = y / (double)(h - 1);
                double px, py, pz;
                if (id == SphereMap) { double a = 2 * Math.PI * u, c = Math.PI * v; px = Math.Sin(c) * Math.Cos(a) * 0.5; py = Math.Sin(c) * Math.Sin(a) * 0.5; pz = Math.Cos(c) * 0.5; }
                else if (id == TorusMap) { double a = 2 * Math.PI * u, c = 2 * Math.PI * v; px = (0.32 + 0.16 * Math.Cos(c)) * Math.Cos(a); py = (0.32 + 0.16 * Math.Cos(c)) * Math.Sin(a); pz = 0.16 * Math.Sin(c); }
                else if (id == PlaneMap) { px = u - 0.5; py = v - 0.5; pz = 0.15 * Math.Sin(3 * Math.PI * u) * Math.Cos(2 * Math.PI * v); }
                else if (id == CylinderMap) { double a = 2 * Math.PI * u, r = 0.3 + 0.15 * Math.Sin(Math.PI * v); px = r * Math.Cos(a); py = r * Math.Sin(a); pz = v - 0.5; }
                else { double a = 2 * Math.PI * u, r = 0.25 + 0.12 * Math.Sin(6 * Math.PI * u); px = r * Math.Cos(a); py = r * Math.Sin(a); pz = v - 0.5; }   // thin: a fluted column
                int i = (y * w + x) * 4;
                b[i] = Byte(px); b[i + 1] = Byte(py); b[i + 2] = Byte(pz); b[i + 3] = 255;
            }
        return b;
    }

    private static byte Byte(double p) => (byte)Math.Clamp(Math.Round((p + 0.5) * 255), 0, 255);

    public static readonly Vector3 Origin = PrimZoo.Origin;

    public static IReadOnlyList<ZooPrim> Make()
    {
        var o = Origin;
        var s = new Vector3(2f, 2f, 2f);
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.35f);
        var white = new FaceColors(Rgba.White);
        var grid = new FaceTextures(FaceTexture.Plain(TextureZoo.Grid));
        // sculpts: the official viewer sets a circle path and a circle profile
        PrimShape Sculpt(Guid map, int type, int flags = 0) => new() { ProfileCurve = 0, PathCurve = 32, SculptType = type, SculptId = map, SculptFlags = flags };
        ZooPrim P(uint id, string name, int col, int row, PrimShape shape, FaceColors c, FaceTextures? t) =>
            new(id, 0, name, o + new Vector3(col * 3f, row * 3f, 0), q, s, shape, c, t);
        return
        [
            P(7001, "sphere", 0, 0, Sculpt(SphereMap, SculptMesher.Sphere), white, grid),
            P(7002, "torus", 1, 0, Sculpt(TorusMap, SculptMesher.Torus), white, grid),
            P(7003, "plane-colour-only", 2, 0, Sculpt(PlaneMap, SculptMesher.Plane), new FaceColors(new Rgba(1f, 0.55f, 0.2f, 1)), null),
            P(7004, "cylinder", 3, 0, Sculpt(CylinderMap, SculptMesher.Cylinder), white, grid),
            P(7005, "sphere-mirrored", 0, 1, Sculpt(SphereMap, SculptMesher.Sphere, SculptMesher.FlagMirror), white, grid),
            P(7006, "sphere-inverted", 1, 1, Sculpt(SphereMap, SculptMesher.Sphere, SculptMesher.FlagInvert), white, grid),
            P(7007, "long-thin-256x16", 2, 1, Sculpt(ThinMap, SculptMesher.Cylinder), new FaceColors(new Rgba(0.6f, 0.9f, 1f, 1)), grid),
            P(7008, "missing-map", 3, 1, Sculpt(MissingMap, SculptMesher.Torus), new FaceColors(new Rgba(0.9f, 0.3f, 0.8f, 1)), null),
        ];
    }

    public static string Serialise(IReadOnlyList<ZooPrim> prims)
    {
        var root = JsonNode.Parse(PrimZoo.Serialise(prims))!.AsObject();
        root["format"] = Format;
        var images = new JsonObject();
        foreach (var (id, f) in Images) images[id.ToString()] = f;
        root["images"] = images;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }

    public static (IReadOnlyList<ZooPrim> Prims, IReadOnlyDictionary<Guid, string> Images) Parse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if ((string?)root["format"] != Format) throw new InvalidDataException("sculpt zoo: unknown format");
        var images = root["images"]!.AsObject().ToDictionary(kv => Guid.Parse(kv.Key), kv => (string)kv.Value!);
        root["format"] = PrimZoo.Format;
        root.Remove("images");
        return (PrimZoo.Parse(root.ToJsonString()), images);
    }
}
