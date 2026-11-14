// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// End-to-end texture orientation on screen. Every object wears one lossless 256x256 test texture
/// whose RED channel is u (0 at the left edge, 255 at the right) and GREEN channel is v (0 at the BOTTOM row, 255 at the
/// top, SL's convention), with a blue up-arrow drawn on top (readable by eye in any orientation). All faces fullbright, so
/// the rendered pixels give (u, v) directly and their screen gradients give the orientation. Three orthographic views:
/// from the south (side faces, planar, mesh, sculpt, circular-path shapes), from above (top caps, and a lit sphere for the
/// lighting check) and from below (bottom caps). Expectations: Docs/TEXTURE-ORIENTATION-SPEC.md.
/// </summary>
public static class OrientationZoo
{
    public const string Format = "nv-orientation-1";
    public static readonly Guid Texture = new("4e56007d-0000-4000-8000-000000000010");
    public static readonly Guid White = new("4e56007d-0000-4000-8000-000000000011");
    public const int Size = 256;
    public static readonly Vector3 Origin = new(150, 150, 25);

    public static readonly IReadOnlyDictionary<Guid, string> Images = new Dictionary<Guid, string>
    {
        [Texture] = "orientation-uv.j2c", [White] = "white.j2c",
    };
    public static readonly IReadOnlyDictionary<Guid, string> Meshes = new Dictionary<Guid, string>
    {
        [MeshZoo.Assets.Keys.First()] = "../meshes/" + MeshZoo.Assets.Values.First(),
    };

    public static byte[] Pixels(Guid id)
    {
        var b = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int i = (y * Size + x) * 4;
                if (id == White) { b[i] = b[i + 1] = b[i + 2] = 255; b[i + 3] = 255; continue; }
                int v = Size - 1 - y;   // image row 0 is the top: v is 255 there
                b[i] = (byte)x; b[i + 1] = (byte)v;
                // a blue up-arrow in the middle: a shaft and a head pointing to the image top
                int cx = x - 128, cy = 128 - y;
                bool shaft = Math.Abs(cx) < 10 && cy > -70 && cy < 30;
                bool head = cy >= 30 && cy < 80 && Math.Abs(cx) < (80 - cy);
                b[i + 2] = (byte)(shaft || head ? 255 : 0);
                b[i + 3] = 255;
            }
        return b;
    }

    /// <summary>What a check expects at an object: "u right" / "v up" on screen (or none: direction not asserted), never mirrored.</summary>
    public sealed record Check(uint LocalId, string Name, string View, bool URight, bool VUp, Vector3[] Offsets);

    private static readonly Vector3 Here = Vector3.Zero;

    public static IReadOnlyList<ZooPrim> Make()
    {
        var o = Origin;
        var tex = new FaceTextures(FaceTexture.Plain(Texture) with { Fullbright = true });
        var planar = new FaceTextures(FaceTexture.Plain(Texture) with { Fullbright = true, TexGen = TexGen.Planar });
        var white = new FaceColors(Rgba.White);
        var s1 = new Vector3(1, 1, 1);
        Quaternion Z(float deg) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deg * MathF.PI / 180);
        PrimShape Cyl = new() { ProfileCurve = 0, PathCurve = 16 }, Prism = new() { ProfileCurve = 3, PathCurve = 16 };
        PrimShape Sphere = new() { ProfileCurve = 5, PathCurve = 32 }, Torus = new() { ProfileCurve = 0, PathCurve = 32, PathScaleY = 0.25 };
        PrimShape Tube = new() { ProfileCurve = 1, PathCurve = 32, PathScaleY = 0.25 }, Ring = new() { ProfileCurve = 3, PathCurve = 32, PathScaleY = 0.25 };
        ZooPrim P(uint id, string name, float x, float y, Quaternion q, PrimShape shape, FaceTextures? t, FaceColors? c = null, Vector3? scale = null) =>
            new(id, 0, name, o + new Vector3(x, y, 0), q, scale ?? s1, shape, c ?? white, t);
        return
        [
            // row 1 (y = 0), seen from the south: side faces
            P(7201, "box-0", -9, 0, Z(0), PrimShape.Box, tex),
            P(7202, "box-90", -7.5f, 0, Z(90), PrimShape.Box, tex),
            P(7203, "box-180", -6, 0, Z(180), PrimShape.Box, tex),
            P(7204, "box-270", -4.5f, 0, Z(270), PrimShape.Box, tex),
            P(7205, "cylinder", -3, 0, Z(0), Cyl, tex),
            P(7206, "prism", -1.5f, 0, Z(0), Prism, tex),
            P(7207, "sphere", 0, 0, Z(0), Sphere, tex),
            P(7208, "torus", 1.5f, 0, Z(0), Torus, tex),
            P(7209, "tube", 3, 0, Z(0), Tube, tex),
            P(7210, "ring", 4.5f, 0, Z(0), Ring, tex),
            P(7211, "box-planar", 6, 0, Z(0), PrimShape.Box, planar),
            new(7212, 0, "mesh-cube", o + new Vector3(7.5f, 0, 0), Quaternion.Identity, s1, PrimShape.Box with { SculptType = PrimShape.SculptTypeMesh, SculptId = MeshZoo.Assets.Keys.First() }, white, tex),
            P(7213, "sculpt-sphere", 9, 0, Z(0), new PrimShape { ProfileCurve = 0, PathCurve = 32, SculptType = SculptMesher.Sphere, SculptId = SculptZoo.SphereMap }, tex),
            // the lit sphere (not fullbright, white texture) for the lighting check, seen from above
            P(7214, "lit-sphere", 0, 4, Z(0), Sphere, new FaceTextures(FaceTexture.Plain(White)), scale: new Vector3(1.5f, 1.5f, 1.5f)),
        ];
    }

    /// <summary>The checks, by view. Offsets are SL metres from the object's centre to where the view samples it.</summary>
    public static IReadOnlyList<Check> Checks()
    {
        Vector3[] centre = [Here];
        Vector3[] ringed = [new(0, 0, 0.35f), new(0, 0, -0.35f), new(0.35f, 0, 0), new(-0.35f, 0, 0), new(0.2f, 0, 0.2f), new(-0.2f, 0, -0.2f)];
        var list = new List<Check>();
        // from the south: every line-path side shows u right, v up (spec 3); circular paths and planar: not mirrored only
        foreach (var (id, name) in new[] { (7201u, "box-0 side"), (7202u, "box-90 side"), (7203u, "box-180 side"), (7204u, "box-270 side"), (7205u, "cylinder side"), (7206u, "prism side") })
            list.Add(new(id, name, "south", true, true, centre));
        foreach (var (id, name) in new[] { (7207u, "sphere"), (7208u, "torus"), (7209u, "tube"), (7210u, "ring"), (7211u, "box planar side"), (7213u, "sculpt sphere") })
            list.Add(new(id, name, "south", false, false, ringed));
        list.Add(new(7212, "mesh cube south face (UVs: u along +X, v along +Z, v = 0 at the image bottom)", "south", true, true, centre));
        // from above, north up: top caps u right, v up (spec 2)
        foreach (var (id, name) in new[] { (7201u, "box top cap"), (7205u, "cylinder top cap") })
            list.Add(new(id, name, "above", true, true, centre));
        // from below, east right (screen up = south): bottom caps u right, v up (spec 2)
        foreach (var (id, name) in new[] { (7201u, "box bottom cap"), (7205u, "cylinder bottom cap") })
            list.Add(new(id, name, "below", true, true, centre));
        return list;
    }

    public static string Serialise(IReadOnlyList<ZooPrim> prims)
    {
        var root = JsonNode.Parse(PrimZoo.Serialise(prims))!.AsObject();
        root["format"] = Format;
        var images = new JsonObject();
        foreach (var (id, f) in Images) images[id.ToString()] = f;
        root["images"] = images;
        var meshes = new JsonObject();
        foreach (var (id, f) in Meshes) meshes[id.ToString()] = f;
        root["meshes"] = meshes;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }

    public static (IReadOnlyList<ZooPrim> Prims, IReadOnlyDictionary<Guid, string> Images, IReadOnlyDictionary<Guid, string> Meshes) Parse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if ((string?)root["format"] != Format) throw new InvalidDataException("orientation zoo: unknown format");
        var images = root["images"]!.AsObject().ToDictionary(kv => Guid.Parse(kv.Key), kv => (string)kv.Value!);
        var meshes = root["meshes"]!.AsObject().ToDictionary(kv => Guid.Parse(kv.Key), kv => (string)kv.Value!);
        root["format"] = PrimZoo.Format;
        root.Remove("images");
        root.Remove("meshes");
        return (PrimZoo.Parse(root.ToJsonString()), images, meshes);
    }

    public sealed record FieldResult(int Samples, int Unmirrored, int URight, int VUp)
    {
        public double UnmirroredShare => Samples == 0 ? 0 : (double)Unmirrored / Samples;
        public double URightShare => Samples == 0 ? 0 : (double)URight / Samples;
        public double VUpShare => Samples == 0 ? 0 : (double)VUp / Samples;
    }

    /// <summary>
    /// The orientation over every pixel of a square screen footprint (half size <paramref name="half"/> px): each pixel whose
    /// neighbourhood is on the surface (no seam, edge or background) votes. Point samples near tile seams and profile edges
    /// gave false "mirrored" readings, so the checks decide by majority.
    /// </summary>
    public static FieldResult Field(byte[] rgba, int width, int height, int cx, int cy, int half)
    {
        int samples = 0, unmirrored = 0, uRight = 0, vUp = 0;
        for (int y = cy - half; y <= cy + half; y++)
            for (int x = cx - half; x <= cx + half; x++)
                if (Gradient(rgba, width, height, x, y, 2) is { } g)
                {
                    samples++;
                    if (g.DuRight * g.DvUp - g.DuUp * g.DvRight > 0) unmirrored++;
                    if (g.DuRight > MathF.Abs(g.DuUp)) uRight++;
                    if (g.DvUp > MathF.Abs(g.DvRight)) vUp++;
                }
        return new(samples, unmirrored, uRight, vUp);
    }

    /// <summary>
    /// The orientation at one screen point from the rendered (u, v): gradients over +/- step pixels. Screen up is -y.
    /// Null when the neighbourhood is not all on the object (background, an edge or a texture seam).
    /// </summary>
    public static (float DuRight, float DuUp, float DvRight, float DvUp)? Gradient(byte[] rgba, int width, int height, int px, int py, int step = 3)
    {
        if (px - step < 0 || py - step < 0 || px + step >= width || py + step >= height) return null;
        (int U, int V) At(int x, int y) { int i = (y * width + x) * 4; return (rgba[i], rgba[i + 1]); }
        var l = At(px - step, py); var r = At(px + step, py); var u = At(px, py - step); var d = At(px, py + step);
        float duR = (r.U - l.U) / (2f * step), dvR = (r.V - l.V) / (2f * step), duU = (u.U - d.U) / (2f * step), dvU = (u.V - d.V) / (2f * step);
        // a seam or an edge jumps; a real surface changes smoothly at this texture's scale
        if (MathF.Abs(duR) > 30 || MathF.Abs(dvR) > 30 || MathF.Abs(duU) > 30 || MathF.Abs(dvU) > 30) return null;
        if (MathF.Abs(duR) + MathF.Abs(duU) < 1f || MathF.Abs(dvR) + MathF.Abs(dvU) < 1f) return null;
        return (duR, duU, dvR, dvU);
    }
}
