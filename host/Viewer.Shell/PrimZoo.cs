// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>One prim of a zoo fixture: what an ObjectUpdate carries for it (SL coordinates; a child's are local to its parent).</summary>
public sealed record ZooPrim(uint LocalId, uint Parent, string Name, Vector3 Position, Quaternion Rotation, Vector3 Scale, PrimShape Shape, FaceColors Colors, FaceTextures? Textures = null, PrimLight? Light = null, PrimProjector? Projector = null);

/// <summary>
/// L4 "prim zoo": one of each SL prim type plus cut, hollow, twist, taper, shear, dimple, skew, revolution and radius
/// variants, a three-prim linkset with rotated children, and semi-transparent faces. Deterministic; the committed fixture
/// (fixtures/prims/prim-zoo.json) is its serialisation, checked against it by a test.
/// </summary>
public static class PrimZoo
{
    public const string Format = "nv-primzoo-1";
    /// <summary>The zoo sits in a 256 m region, on a 5 x 4 grid of 3 m cells from (100, 100, 25).</summary>
    public static readonly Vector3 Origin = new(100, 100, 25);

    private static PrimShape Cyl => new() { ProfileCurve = 0, PathCurve = 16 };
    private static PrimShape Prism => new() { ProfileCurve = 3, PathCurve = 16 };
    private static PrimShape Sphere => new() { ProfileCurve = 5, PathCurve = 32 };
    private static PrimShape Torus => new() { ProfileCurve = 0, PathCurve = 32, PathScaleY = 0.25 };
    private static PrimShape Tube => new() { ProfileCurve = 1, PathCurve = 32, PathScaleY = 0.25 };
    private static PrimShape Ring => new() { ProfileCurve = 3, PathCurve = 32, PathScaleY = 0.25 };

    /// <summary>The named shapes (also used by the every-LOD mesher test).</summary>
    public static IReadOnlyDictionary<string, PrimShape> Shapes { get; } = new Dictionary<string, PrimShape>
    {
        ["box"] = PrimShape.Box,
        ["cylinder"] = Cyl,
        ["prism"] = Prism,
        ["sphere"] = Sphere,
        ["torus"] = Torus,
        ["tube"] = Tube,
        ["ring"] = Ring,
        ["box-cut-hollow"] = PrimShape.Box with { ProfileBegin = 0.125, ProfileEnd = 0.875, ProfileHollow = 0.5 },
        ["box-hollow-circle"] = PrimShape.Box with { ProfileHollow = 0.6, ProfileHole = 16 },
        ["cylinder-hollow-square"] = Cyl with { ProfileHollow = 0.5, ProfileHole = 32 },
        ["box-twist"] = PrimShape.Box with { PathTwist = 0.5 },
        ["cylinder-taper"] = Cyl with { PathScaleX = 0.3, PathScaleY = 0.6 },
        ["prism-shear"] = Prism with { PathShearX = 0.3, PathScaleX = 0.7 },
        ["box-pathcut"] = PrimShape.Box with { PathBegin = 0.2, PathEnd = 0.8 },
        ["sphere-dimple"] = Sphere with { ProfileBegin = 0.2, ProfileEnd = 0.8 },
        ["sphere-cut"] = Sphere with { ProfileBegin = 0.25, ProfileEnd = 1.0, PathBegin = 0.0, PathEnd = 0.75 },
        ["torus-revolutions"] = Torus with { PathRevolutions = 2, PathScaleY = 0.15, PathRadiusOffset = 0.2 },
        ["tube-skew-taper"] = Tube with { PathSkew = 0.2, PathTaperX = 0.4 },
        ["ring-twist"] = Ring with { PathTwist = 0.5, PathTwistBegin = -0.25 },
    };

    private static readonly Rgba[] Palette =
    [
        new(0.90f, 0.30f, 0.25f, 1), new(0.95f, 0.65f, 0.20f, 1), new(0.90f, 0.85f, 0.25f, 1), new(0.40f, 0.80f, 0.35f, 1),
        new(0.25f, 0.70f, 0.75f, 1), new(0.30f, 0.45f, 0.90f, 1), new(0.60f, 0.40f, 0.85f, 1), new(0.85f, 0.45f, 0.70f, 1),
    ];

    public static IReadOnlyList<ZooPrim> Make()
    {
        var prims = new List<ZooPrim>();
        uint id = 1000;
        int cell = 0;
        foreach (var (name, shape) in Shapes)
        {
            int col = cell % 5, row = cell / 5;
            var colors = new FaceColors(Palette[cell % Palette.Length]);
            if (name == "box")   // per-face colours: each face its own
                colors = new FaceColors(Rgba.White, Enumerable.Range(0, 6).Select(i => (Rgba?)Palette[i]));
            if (name == "sphere")   // semi-transparent
                colors = new FaceColors(Palette[5] with { A = 0.5f });
            if (name == "cylinder")   // one semi-transparent face (the side), opaque caps
                colors = new FaceColors(Palette[1], [null, Palette[4] with { A = 0.35f }]);
            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f * (cell % 3));
            prims.Add(new ZooPrim(id++, 0, name, Origin + new Vector3(col * 3f, row * 3f, 0), rot, new Vector3(1.6f, 1.6f, 1.6f), shape, colors));
            cell++;
        }
        // A linkset: a flat root box, a cylinder standing on it and a tilted torus, both local to the root (rotated 45 degrees).
        uint root = id++;
        var rootRot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4);
        prims.Add(new ZooPrim(root, 0, "linkset-root", Origin + new Vector3(4 * 3f, 3 * 3f + 1f, -0.4f), rootRot, new Vector3(2.4f, 1.2f, 0.3f), PrimShape.Box, new FaceColors(new Rgba(0.55f, 0.55f, 0.6f, 1))));
        prims.Add(new ZooPrim(id++, root, "linkset-child-cylinder", new Vector3(0.8f, 0, 0.6f), Quaternion.Identity, new Vector3(0.5f, 0.5f, 1.0f), Cyl, new FaceColors(Palette[0])));
        prims.Add(new ZooPrim(id++, root, "linkset-child-torus", new Vector3(-0.8f, 0, 0.6f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), new Vector3(0.9f, 0.9f, 0.9f), Torus, new FaceColors(Palette[3])));
        return prims;
    }

    public static string Serialise(IEnumerable<ZooPrim> prims)
    {
        static JsonArray F(params float[] v) => new(v.Select(x => (JsonNode)JsonValue.Create(MathF.Round(x, 6))).ToArray());
        static JsonNode? C(Rgba? c) => c is { } x ? F(x.R, x.G, x.B, x.A) : null;
        static JsonNode? T(FaceTexture? t) => t is { } x ? new JsonArray(x.TextureId.ToString(), MathF.Round(x.RepeatU, 6), MathF.Round(x.RepeatV, 6), MathF.Round(x.OffsetU, 6), MathF.Round(x.OffsetV, 6), MathF.Round(x.Rotation, 6), (int)x.TexGen, x.Fullbright, MathF.Round(x.Glow, 6)) : null;
        var arr = new JsonArray();
        foreach (var p in prims)
        {
            var s = p.Shape;
            arr.Add(new JsonObject
            {
                ["local"] = p.LocalId, ["parent"] = p.Parent, ["name"] = p.Name,
                ["pos"] = F(p.Position.X, p.Position.Y, p.Position.Z), ["rot"] = F(p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W),
                ["scale"] = F(p.Scale.X, p.Scale.Y, p.Scale.Z),
                ["shape"] = JsonSerializer.SerializeToNode(s),
                ["colors"] = new JsonArray(new[] { C(p.Colors.Default) }.Concat(p.Colors.Overrides.Select(C)).ToArray()),
            });
            if (p.Light is { } pl)   // only when present, so earlier fixtures serialise unchanged
            {
                arr[^1]!["light"] = F(pl.Color.X, pl.Color.Y, pl.Color.Z, pl.Intensity, pl.Radius, pl.Falloff, pl.Cutoff);
                if (p.Projector is { } pp) arr[^1]!["proj"] = new JsonArray(pp.Texture.ToString(), pp.Fov, pp.Focus, pp.Ambiance);
            }
            if (p.Textures is { } ft)   // only when present, so earlier fixtures serialise unchanged
                arr[^1]!["tex"] = new JsonArray(new[] { (FaceTexture?)ft.Default }.Concat(ft.Overrides).Select(T).ToArray());
        }
        return new JsonObject { ["format"] = Format, ["region"] = new JsonArray(256, 256), ["prims"] = arr }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }

    public static IReadOnlyList<ZooPrim> Parse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if ((string?)root["format"] != Format)
            throw new InvalidDataException("prim zoo: unknown format");
        static float[] A(JsonNode? n) => n!.AsArray().Select(x => (float)x!).ToArray();
        static Rgba? C(JsonNode? n) => n is JsonArray a ? new Rgba((float)a[0]!, (float)a[1]!, (float)a[2]!, (float)a[3]!) : null;
        return root["prims"]!.AsArray().Select(n =>
        {
            var o = n!.AsObject();
            var pos = A(o["pos"]);
            var rot = A(o["rot"]);
            var sc = A(o["scale"]);
            var cols = o["colors"]!.AsArray();
            FaceTextures? tex = null;
            if (o["tex"] is JsonArray ta && ta.Count > 0 && ta[0] is JsonArray)
            {
                static FaceTexture? T(JsonNode? n) => n is JsonArray x ? new FaceTexture(Guid.Parse((string)x[0]!), (float)x[1]!, (float)x[2]!, (float)x[3]!, (float)x[4]!, (float)x[5]!, (TexGen)(int)x[6]!, (bool)x[7]!, (float)x[8]!) : null;
                tex = new FaceTextures(T(ta[0])!.Value, ta.Skip(1).Select(T));
            }
            return new ZooPrim((uint)o["local"]!, (uint)o["parent"]!, (string)o["name"]!, new Vector3(pos[0], pos[1], pos[2]), new Quaternion(rot[0], rot[1], rot[2], rot[3]),
                new Vector3(sc[0], sc[1], sc[2]), o["shape"].Deserialize<PrimShape>()!, new FaceColors(C(cols[0])!.Value, cols.Skip(1).Select(C)), tex,
                o["light"] is JsonArray la ? new PrimLight(new Vector3((float)la[0]!, (float)la[1]!, (float)la[2]!), (float)la[3]!, (float)la[4]!, (float)la[5]!, (float)la[6]!) : null,
                o["proj"] is JsonArray pa ? new PrimProjector(Guid.Parse((string)pa[0]!), (float)pa[1]!, (float)pa[2]!, (float)pa[3]!) : null);
        }).ToList();
    }

    /// <summary>The zoo as ObjectUpdate-shaped core input (roots before children, as a simulator sends a linkset).</summary>
    public static IEnumerable<PrimData> AsPrimData(IEnumerable<ZooPrim> prims) =>
        prims.Select(p => new PrimData(p.LocalId, new Guid((int)p.LocalId, 0x4e56, 0x5a4f, new byte[8]), p.Parent, 0, p.Position, p.Rotation, p.Scale,
            Shape: p.Shape, Colors: p.Colors, Textures: p.Textures, Light: p.Light, Projector: p.Projector));
}
