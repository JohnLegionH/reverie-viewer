// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using Viewer.Core.Assets;
using Viewer.Core.Meshing;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The local-only recaptures (with prim shape data) and the local asset cache. Only derived expectations are
/// committed (fixtures/expectations/recaptures.json); every test is a real SKIP when the capture or cache is absent.
/// </summary>
public class RecaptureTests
{
    private static JsonElement Expected(string tag)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "fixtures", "expectations", "recaptures.json")));
        return doc.RootElement.GetProperty(tag).Clone();
    }

    private static void ReplaysToTheLiveState(string file, string tag)
    {
        var c = CaptureFile.Read(LiveCaptures.PathOf(file));
        var r = ReplayRunner.Run(c);
        // These captures were recorded when a worn child that arrived one update before its root left a pending entry under the
        // root's id for good, so their footers say 1 pending attachment. That child is bound through its root (the root hangs from
        // the avatar), and the entry is now cleared when the root arrives, as the official viewer attaches a child with its parent.
        // Everything else in the footer must still match, and nothing may be left pending.
        Assert.Equal(c.Footer!.Live with { PendingAttachmentsUnbound = 0 }, r.Summary);
        Assert.Equal(0, r.Summary.PendingAttachmentsUnbound);
        var e = Expected(tag);
        Assert.Equal(e.GetProperty("StructuralHash").GetString(), r.Summary.StructuralHash);
        Assert.Equal(e.GetProperty("FullHash").GetString(), r.Summary.FullHash);
        using var s = ReplayRunner.Load(c);
        var objs = s.World.Snapshot().Objects.Values.ToList();
        Assert.Equal(e.GetProperty("ObjectsWithShape").GetInt32(), objs.Count(o => o.Shape is not null));
        Assert.Equal(e.GetProperty("MeshObjects").GetInt32(), objs.Count(o => o.Shape is { SculptType: PrimShape.SculptTypeMesh }));
        Assert.Equal(e.GetProperty("SculptObjects").GetInt32(), objs.Count(o => o.Shape is { SculptType: > 0 and < PrimShape.SculptTypeMesh }));
        Assert.Equal(e.GetProperty("MeshAssets").GetInt32(), objs.Where(o => o.Shape is { SculptType: PrimShape.SculptTypeMesh }).Select(o => o.Shape!.SculptId).Distinct().Count());
    }

    [CaptureFact("capture-region-a-v2.jsonl.gz")]
    public void RegionA_v2_replays_to_the_live_state_with_shape_data() => ReplaysToTheLiveState("capture-region-a-v2.jsonl.gz", "region-a-v2");

    [CaptureFact("capture-region-a-v3.jsonl.gz")]
    public void RegionA_v3_replays_to_the_live_state_with_texture_entries() => ReplaysToTheLiveState("capture-region-a-v3.jsonl.gz", "region-a-v3");

    [LocalFixturesFact("capture-region-a-v3.jsonl.gz", "assets")]
    public void Every_texture_the_region_a_v3_scene_references_is_cached_and_passes_the_decode_guard()
    {
        var cache = new LocalAssetCache(LiveCaptures.PathOf("assets"));
        using var s = ReplayRunner.Load(CaptureFile.Read(LiveCaptures.PathOf("capture-region-a-v3.jsonl.gz")));
        var objs = s.World.Snapshot().Objects.Values.ToList();
        Assert.Equal(Expected("region-a-v3").GetProperty("ObjectsWithTextureEntries").GetInt32(), objs.Count(o => o.Textures is not null));
        var sculptMaps = objs.Where(o => o.Shape is { SculptType: > 0 and < PrimShape.SculptTypeMesh }).Select(o => o.Shape!.SculptId).ToHashSet();
        var ids = objs.Where(o => o.Textures is not null)
            .SelectMany(o => new[] { (Viewer.Core.World.FaceTexture?)o.Textures!.Default }.Concat(o.Textures!.Overrides))
            .Where(t => t is { } x && x.TextureId != Guid.Empty).Select(t => t!.Value.TextureId).Distinct().Where(id => !sculptMaps.Contains(id)).ToList();
        Assert.Equal(Expected("region-a-v3").GetProperty("Textures").GetInt32(), ids.Count);
        foreach (var id in ids)
        {
            Assert.True(cache.HasTexture(id), id + " not cached");
            var d = LmvTexture.Decode(File.ReadAllBytes(cache.TexturePath(id)));
            Assert.True(d.Ok, id + ": " + d.Refusal);
        }
    }

    [CaptureFact("capture-region-a-v5.jsonl.gz")]
    public void RegionA_v5_replays_to_the_live_state_with_water_and_environment() => ReplaysToTheLiveState("capture-region-a-v5.jsonl.gz", "region-a-v5");

    /// <summary>Region A v9 records the material data: faces' legacy and PBR material references, the region's legacy
    /// materials and its glTF overrides; they replay into the world state.</summary>
    [CaptureFact("capture-region-a-v9.jsonl.gz")]
    public void RegionA_v9_replays_to_the_live_state_with_its_material_data()
    {
        ReplaysToTheLiveState("capture-region-a-v9.jsonl.gz", "region-a-v9");
        var e = Expected("region-a-v9");
        using var s = ReplayRunner.Load(CaptureFile.Read(LiveCaptures.PathOf("capture-region-a-v9.jsonl.gz")));
        var objs = s.World.Snapshot().Objects.Values.ToList();
        var faces = objs.Where(o => o.Materials is not null).SelectMany(o => o.Materials!.All).Where(f => f is { IsNone: false }).Select(f => f!.Value).ToList();
        Assert.Equal(e.GetProperty("ObjectsWithMaterials").GetInt32(), objs.Count(o => o.Materials is not null));
        Assert.Equal(e.GetProperty("PbrFaces").GetInt32(), faces.Count(f => f.RenderMaterialId != Guid.Empty));
        Assert.Equal(e.GetProperty("PbrMaterials").GetInt32(), faces.Select(f => f.RenderMaterialId).Where(g => g != Guid.Empty).Distinct().Count());
        Assert.Equal(e.GetProperty("LegacyMaterialIds").GetInt32(), faces.Select(f => f.MaterialId).Where(g => g != Guid.Empty).Distinct().Count());
        Assert.Equal(e.GetProperty("LegacyMaterialsServed").GetInt32(), s.World.LegacyMaterials().Count);
        Assert.Equal(e.GetProperty("ObjectsWithOverrides").GetInt32(), objs.Count(o => o.MaterialOverrides is not null));
    }

    [LocalFixturesFact("capture-region-a-v9.jsonl.gz", "assets")]
    public void Every_material_asset_and_material_texture_region_a_v9_names_is_cached_and_reads()
    {
        var cache = new LocalAssetCache(LiveCaptures.PathOf("assets"));
        using var s = ReplayRunner.Load(CaptureFile.Read(LiveCaptures.PathOf("capture-region-a-v9.jsonl.gz")));
        var objs = s.World.Snapshot().Objects.Values.ToList();
        var pbr = objs.Where(o => o.Materials is not null).SelectMany(o => o.Materials!.All)
            .Where(f => f is { } x && x.RenderMaterialId != Guid.Empty).Select(f => f!.Value.RenderMaterialId).Distinct().ToList();
        var textures = new HashSet<Guid>();
        foreach (var id in pbr)
        {
            Assert.True(cache.HasMaterial(id), id + " not cached");
            var named = LmvMaterials.TexturesOfMaterialAsset(id, File.ReadAllBytes(cache.MaterialPath(id)));
            Assert.NotNull(named);
            textures.UnionWith(named!);
        }
        foreach (var m in s.World.LegacyMaterials().Values)
            textures.UnionWith(new[] { m.Normal.TextureId, m.Specular.TextureId }.Where(g => g != Guid.Empty));
        foreach (var o in objs.Where(o => o.MaterialOverrides is not null))
            foreach (var f in o.MaterialOverrides!.Faces)
            {
                using var doc = JsonDocument.Parse(f.Json);
                if (doc.RootElement.TryGetProperty("tex", out var tex))
                    foreach (var t in tex.EnumerateArray())
                        if (Guid.TryParse(t.GetString(), out var g) && g != Guid.Empty && g != Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"))
                            textures.Add(g);
            }
        Assert.Equal(Expected("region-a-v9").GetProperty("MaterialTextures").GetInt32(), textures.Count);
        foreach (var id in textures)
        {
            Assert.True(cache.HasTexture(id), id + " not cached");
            var d = LmvTexture.Decode(File.ReadAllBytes(cache.TexturePath(id)));
            Assert.True(d.Ok, id + ": " + d.Refusal);   // through the guard
        }
    }

    /// <summary>Region B v3, captured hardened the environment fetch, holds the grid's answer.</summary>
    [CaptureFact("capture-region-b-v3.jsonl.gz")]
    public void RegionB_v3_recorded_the_environment_answer_through_the_hardened_fetch_and_it_parses()
    {
        var answers = CaptureFile.Read(LiveCaptures.PathOf("capture-region-b-v3.jsonl.gz")).Records.Select(r => r.Event).OfType<EnvironmentReceived>().ToList();
        var answer = Assert.Single(answers);
        Assert.Null(answer.Why);
        Assert.NotNull(answer.Llsd);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(answer.Llsd!), 1, 512 * 1024);   // under the byte cap
        Assert.NotNull(RegionEnvironment.FromLlsd(answer.Llsd!, out var why));
        Assert.True(string.IsNullOrEmpty(why), why);
    }

    [CaptureFact("capture-region-a-v4.jsonl.gz")]
    public void RegionA_v4_replays_to_the_live_state_with_terrain_settings() => ReplaysToTheLiveState("capture-region-a-v4.jsonl.gz", "region-a-v4");

    [LocalFixturesFact("capture-region-a-v4.jsonl.gz", "assets")]
    public void RegionA_v4_records_the_region_terrain_settings_and_sculpt_flags_and_its_detail_textures_are_cached()
    {
        var e = Expected("region-a-v4");
        using var s = ReplayRunner.Load(CaptureFile.Read(LiveCaptures.PathOf("capture-region-a-v4.jsonl.gz")));
        var t = s.World.Region!.Terrain;
        Assert.NotNull(t);   // the handshake's settings are in the capture
        Assert.Equal(e.GetProperty("TerrainStart").EnumerateArray().Select(x => x.GetSingle()), t!.StartHeights);
        Assert.Equal(e.GetProperty("TerrainRange").EnumerateArray().Select(x => x.GetSingle()), t.HeightRanges);
        Assert.Equal(e.GetProperty("TerrainDetails").GetInt32(), t.Details.Count(d => d != Guid.Empty));
        var cache = new LocalAssetCache(LiveCaptures.PathOf("assets"));
        foreach (var id in t.Details.Where(d => d != Guid.Empty))
        {
            Assert.True(cache.HasTexture(id), id + " not cached");
            var d = LmvTexture.Decode(File.ReadAllBytes(cache.TexturePath(id)));
            Assert.True(d.Ok, id + ": " + d.Refusal);   // through the guard
        }
        // the capture now records sculpt flags; region A's 15 sculpts set none
        var sculpts = s.World.Snapshot().Objects.Values.Where(o => o.Shape is { IsSculpt: true }).ToList();
        Assert.Equal(e.GetProperty("SculptObjects").GetInt32(), sculpts.Count);
        Assert.Equal(e.GetProperty("SculptFlagsSet").GetInt32(), sculpts.Count(o => o.Shape!.SculptFlags != 0));
    }

    [CaptureFact("capture-region-a-v3.jsonl.gz")]
    public void An_older_capture_without_terrain_settings_still_replays_with_none() =>
        Assert.Null(ReplayRunner.Load(CaptureFile.Read(LiveCaptures.PathOf("capture-region-a-v3.jsonl.gz"))).World.Region!.Terrain);

    [CaptureFact("capture-region-b-v2.jsonl.gz")]
    public void RegionB_v2_replays_to_the_live_state_with_shape_data() => ReplaysToTheLiveState("capture-region-b-v2.jsonl.gz", "region-b-v2");

    [LocalFixturesFact("capture-region-a-v2.jsonl.gz", "assets")]
    public async Task Every_mesh_the_region_a_recapture_references_is_cached_and_decodes()
    {
        var cache = new LocalAssetCache(LiveCaptures.PathOf("assets"));
        using var s = ReplayRunner.Load(CaptureFile.Read(LiveCaptures.PathOf("capture-region-a-v2.jsonl.gz")));
        var ids = s.World.Snapshot().Objects.Values.Where(o => o.Shape is { SculptType: PrimShape.SculptTypeMesh }).Select(o => o.Shape!.SculptId).Distinct().ToList();
        int lods = 0;
        foreach (var id in ids)
        {
            var r = await cache.FetchMeshAsync(id, CancellationToken.None);
            Assert.Equal(AssetFetchStatus.Ok, r.Status);
            var d = LmvMesh.Decode(id, r.Bytes.ToArray());
            Assert.True(d.Ok, id + ": " + d.Refusal);
            lods += d.Geometry!.Lods.Count(l => l is not null);
        }
        Assert.Equal(Expected("region-a-v2").GetProperty("MeshAssets").GetInt32(), ids.Count);
        Assert.True(lods >= ids.Count);
        Assert.Equal(AssetFetchStatus.NotFound, (await cache.FetchMeshAsync(Guid.NewGuid(), CancellationToken.None)).Status);   // a miss
    }
}

/// <summary>A real SKIP, with its reason, unless every named local-only fixture (file or directory) is present.</summary>
public sealed class LocalFixturesFactAttribute : FactAttribute
{
    public LocalFixturesFactAttribute(params string[] names)
    {
        var missing = names.Where(n => !File.Exists(LiveCaptures.PathOf(n)) && !Directory.Exists(LiveCaptures.PathOf(n))).ToList();
        if (missing.Count > 0)
            Skip = "local-only " + string.Join(", ", missing.Select(m => "fixtures-local\\" + m)) + " absent (grid content, never committed; ADR-011)";
    }
}

/// <summary>The local asset cache as a replay asset source (always runs: it uses a temporary directory).</summary>
public class LocalAssetCacheTests
{
    [Fact]
    public async Task It_stores_by_id_serves_hits_and_reports_misses_as_not_found_inside_its_own_directory()
    {
        string dir = TestTemp.NewFolderPath("cache");   // deleted at the end of the run by the helper
        var cache = new LocalAssetCache(dir);
        var mesh = MeshZooBuilder.Make()[0];
        cache.PutMesh(mesh.Id, mesh.Bytes);
        cache.PutTexture(mesh.Id, new byte[] { 1, 2, 3 });
        Assert.Equal(new[] { cache.MeshPath(mesh.Id), cache.TexturePath(mesh.Id) }.Order(), Directory.GetFiles(dir).Order());
        var hit = await cache.FetchMeshAsync(mesh.Id, CancellationToken.None);
        Assert.Equal(mesh.Bytes, hit.Bytes.ToArray());
        Assert.True(LmvMesh.Decode(mesh.Id, hit.Bytes.ToArray()).Ok);
        Assert.Equal(AssetFetchStatus.NotFound, (await cache.FetchMeshAsync(Guid.NewGuid(), CancellationToken.None)).Status);
        Assert.Equal(AssetFetchStatus.NotFound, (await cache.FetchTextureAsync(Guid.NewGuid(), CancellationToken.None)).Status);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(AssetFetchStatus.Cancelled, (await cache.FetchMeshAsync(mesh.Id, cancelled.Token)).Status);
        // Through a session's asset access, as the replay renders use it.
        using var meshes = new SessionMeshes(new SessionAssets(new SharedAssetStore(), _ => true, cache.FetchMeshAsync), (id, b) => { var r = LmvMesh.Decode(id, b); return (r.Geometry, r.Refusal); });
        Assert.Equal(AssetFetchStatus.Ok, (await meshes.GetAsync(mesh.Id, CancellationToken.None)).Status);
        Assert.Equal(AssetFetchStatus.NotFound, (await meshes.GetAsync(Guid.NewGuid(), CancellationToken.None)).Status);
    }
}
