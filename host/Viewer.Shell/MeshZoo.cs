// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// L4 "mesh zoo": the three committed mesh assets (fixtures/meshes/*.llmesh, built with LibreMetaverse's own
/// encoder) as mesh objects in a row, 2 m each, with per-face legacy colours (the cube's six faces differ, the wave's two
/// faces differ, one semi-transparent). The fixture (fixtures/meshes/mesh-zoo.meshzoo.json) is this serialisation plus
/// the asset map; a test checks it against <see cref="Make"/>.
/// </summary>
public static class MeshZoo
{
    public const string Format = "nv-meshzoo-1";
    public static readonly Vector3 Origin = new(100, 100, 25);

    public static readonly IReadOnlyDictionary<Guid, string> Assets = new Dictionary<Guid, string>
    {
        [new Guid("4e560006-0000-4000-8000-000000000001")] = "mesh-cube-6face.llmesh",
        [new Guid("4e560006-0000-4000-8000-000000000002")] = "mesh-sphere.llmesh",
        [new Guid("4e560006-0000-4000-8000-000000000003")] = "mesh-two-face-wave.llmesh",
    };

    private static PrimShape MeshShape(Guid id) => PrimShape.Box with { SculptType = PrimShape.SculptTypeMesh, SculptId = id };

    public static IReadOnlyList<ZooPrim> Make()
    {
        var ids = Assets.Keys.ToList();
        var faces6 = new Rgba?[] { new(0.90f, 0.30f, 0.25f, 1), new(0.30f, 0.45f, 0.90f, 1), new(0.40f, 0.80f, 0.35f, 1), new(0.95f, 0.65f, 0.20f, 1), new(0.60f, 0.40f, 0.85f, 1), new(0.25f, 0.70f, 0.75f, 1) };
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
        return
        [
            new ZooPrim(3001, 0, "mesh-cube-6face", Origin + new Vector3(-2.4f, 0, 0), tilt, new Vector3(2, 2, 2), MeshShape(ids[0]), new FaceColors(Rgba.White, faces6)),
            new ZooPrim(3002, 0, "mesh-sphere", Origin, Quaternion.Identity, new Vector3(2, 2, 2), MeshShape(ids[1]), new FaceColors(new Rgba(0.85f, 0.85f, 0.30f, 1))),
            new ZooPrim(3003, 0, "mesh-two-face-wave", Origin + new Vector3(2.4f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.9f), new Vector3(2, 2, 2), MeshShape(ids[2]),
                new FaceColors(new Rgba(0.30f, 0.75f, 0.60f, 1), [null, new Rgba(0.90f, 0.45f, 0.70f, 0.55f)])),
        ];
    }

    public static string Serialise(IReadOnlyList<ZooPrim> prims)
    {
        var root = JsonNode.Parse(PrimZoo.Serialise(prims))!.AsObject();
        root["format"] = Format;
        var assets = new JsonObject();
        foreach (var (id, file) in Assets) assets[id.ToString()] = file;
        root["assets"] = assets;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }

    public static (IReadOnlyList<ZooPrim> Prims, IReadOnlyDictionary<Guid, string> Assets) Parse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if ((string?)root["format"] != Format)
            throw new InvalidDataException("mesh zoo: unknown format");
        var assets = root["assets"]!.AsObject().ToDictionary(kv => Guid.Parse(kv.Key), kv => (string)kv.Value!);
        root["format"] = PrimZoo.Format;
        root.Remove("assets");
        return (PrimZoo.Parse(root.ToJsonString()), assets);
    }
}
