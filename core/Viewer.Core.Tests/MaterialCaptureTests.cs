// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;
using System.Text.Json;
using LibreMetaverse;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;
using LmvLegacy = LibreMetaverse.Materials.LegacyMaterial;
using LmvAlpha = LibreMetaverse.Materials.LegacyMaterialAlphaMode;

namespace Viewer.Core.Tests;

/// <summary>
/// Material data from the grid to the world state and through a capture: the texture entry's per-face material
/// references, the region's legacy materials (RenderMaterials) and its glTF overrides (the GLTF override message).
/// Nothing is drawn with them yet; these tests hold the data the material rendering reads.
/// </summary>
public class MaterialCaptureTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly SimRef Elsewhere = new(256256UL << 32 | 256000UL, "Elsewhere");
    private static readonly Guid Legacy1 = Guid.Parse("6f1d7c1e-3a52-4d1b-9a0e-2c55f0b7a101");
    private static readonly Guid Pbr1 = Guid.Parse("0b2f6a44-8e1c-4b7d-a3f2-6d1e9c0a5b02");
    private static readonly Guid NormalMap = Guid.Parse("9c0e1a77-5b3d-4f2e-8a61-1d4b7e2c9f03");
    private static readonly Guid SpecMap = Guid.Parse("2e7b9d10-6c4a-4e8f-b1d3-7a5c0f2e8d04");

    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);

    // ---------------- adapter ----------------

    [Fact]
    public void The_texture_entry_material_references_translate_per_face()
    {
        var te = new Primitive.TextureEntry(UUID.Random());
        te.DefaultTexture!.Bump = Bumpiness.Woodgrain;
        te.DefaultTexture.Shiny = Shininess.Medium;
        var f2 = te.CreateFace(2);
        f2.MaterialID = new UUID(Legacy1);
        f2.Shiny = Shininess.High;
        var f3 = te.CreateFace(3);
        f3.RenderMaterialID = new UUID(Pbr1);

        var m = LmvMaterials.ToMaterials(te);
        Assert.NotNull(m);
        Assert.Equal(new FaceMaterial((byte)Bumpiness.Woodgrain, 2, Guid.Empty, Guid.Empty), m!.Default);
        Assert.Equal(Legacy1, m[2].MaterialId);
        Assert.Equal(3, m[2].Shiny);
        Assert.Equal(Pbr1, m[3].RenderMaterialId);
        Assert.Equal(m.Default, m[7]);   // a face with no entry of its own takes the default
        // through the prim translation too
        var p = new Primitive { LocalID = 7, ID = UUID.Random(), Textures = te, PrimData = new Primitive.ConstructionData { PCode = PCode.Prim } };
        Assert.Equal(m, LmvTransport.ToPrimData(p).Materials);
    }

    [Fact]
    public void A_texture_entry_without_material_references_gives_none()
    {
        var te = new Primitive.TextureEntry(UUID.Random());
        te.CreateFace(1).RGBA = new Color4(1, 0, 0, 1);
        Assert.Null(LmvMaterials.ToMaterials(te));
        Assert.Null(LmvMaterials.ToMaterials(null));
    }

    [Fact]
    public void A_legacy_material_translates_with_its_maps_colour_and_alpha_mode()
    {
        var lm = new LmvLegacy
        {
            ID = new UUID(Legacy1), NormalMap = new UUID(NormalMap), NormalMapRepeatX = 2, NormalMapRepeatY = 3, NormalMapOffsetX = 0.25, NormalMapRotation = 1.5,
            SpecularMap = new UUID(SpecMap), SpecularMapRepeatX = 4, SpecularMapRepeatY = 4, SpecularMapOffsetY = 0.5,
            SpecularColor = new Color4(1, 0.5f, 0.25f, 1), SpecularExponent = 51, EnvironmentIntensity = 12,
            DiffuseAlphaMode = LmvAlpha.Mask, AlphaMaskCutoff = 128,
        };
        var m = LmvMaterials.ToLegacyMaterial(lm);
        Assert.Equal(Legacy1, m.Id);
        Assert.Equal(new MaterialMap(NormalMap, 0.25f, 0, 2, 3, 1.5f), m.Normal);
        Assert.Equal(new MaterialMap(SpecMap, 0, 0.5f, 4, 4, 0), m.Specular);
        Assert.Equal(new Rgba(1, 0.5f, 0.25f, 1), m.SpecularColor);
        Assert.Equal(51, m.SpecularExponent);
        Assert.Equal(12, m.EnvironmentIntensity);
        Assert.Equal(LegacyAlphaMode.Mask, m.DiffuseAlphaMode);
        Assert.Equal(128, m.AlphaMaskCutoff);
        Assert.Equal(LegacyAlphaMode.Emissive, LmvMaterials.ToLegacyMaterial(new LmvLegacy { DiffuseAlphaMode = LmvAlpha.Emissive }).DiffuseAlphaMode);
        Assert.Equal(LegacyAlphaMode.Blend, LmvMaterials.ToLegacyMaterial(new LmvLegacy { DiffuseAlphaMode = LmvAlpha.Blend }).DiffuseAlphaMode);
    }

    [Fact]
    public void The_override_message_gives_each_named_face_its_override_as_json()
    {
        string msg = "{'id':i4321,'te':[i0,i2],'od':[{'bc':[r1,r0.5,r0.25,r1],'mf':r0,'tex':[u" + Pbr1 + "]},{'am':i1,'ds':1}]}";
        var faces = LmvMaterials.ParseOverrideMessage(msg, out uint local);
        Assert.Equal(4321u, local);
        Assert.NotNull(faces);
        Assert.Equal([0, 2], faces!.Select(f => f.Face));
        using var a = JsonDocument.Parse(faces[0].Json);
        Assert.Equal(0.5, a.RootElement.GetProperty("bc")[1].GetDouble());
        Assert.Equal(0, a.RootElement.GetProperty("mf").GetDouble());
        Assert.Equal(Pbr1, Guid.Parse(a.RootElement.GetProperty("tex")[0].GetString()!));
        using var b = JsonDocument.Parse(faces[1].Json);
        Assert.Equal(1, b.RootElement.GetProperty("am").GetInt32());
        Assert.True(b.RootElement.GetProperty("ds").GetBoolean());
        Assert.False(b.RootElement.TryGetProperty("bc", out _));   // only what the override sets
    }

    [Fact]
    public void An_override_message_with_no_faces_clears_and_a_malformed_one_is_refused()
    {
        var cleared = LmvMaterials.ParseOverrideMessage("{'id':i77,'te':[],'od':[]}", out uint local);
        Assert.Equal(77u, local);
        Assert.Empty(cleared!);
        Assert.Null(LmvMaterials.ParseOverrideMessage("not notation at all {", out _));
        Assert.Null(LmvMaterials.ParseOverrideMessage("{'te':[i0]}", out _));   // no object id
    }

    // ---------------- world state ----------------

    private static (WorldState World, List<WorldChange> Changes) NewWorld()
    {
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.BeginRegion(Here, new RegionInfo(Here.Name, Here.GridX, Here.GridY, 256, 256, true));
        var changes = new List<WorldChange>();
        w.Changed += changes.Add;
        return (w, changes);
    }

    private static PrimData Prim(uint local, FaceMaterials? m = null) =>
        new(local, G(local), 0, 0, Vector3.One, Quaternion.Identity, Vector3.One, Materials: m);

    private static readonly FaceMaterialOverride[] Red = [new(0, "{\"bc\":[1,0,0,1]}")];
    private static readonly FaceMaterialOverride[] Blue = [new(0, "{\"bc\":[0,0,1,1]}"), new(1, "{\"am\":2}")];

    [Fact]
    public void An_object_keeps_its_face_material_references()
    {
        var (w, _) = NewWorld();
        var m = new FaceMaterials(new FaceMaterial(0, 0, Guid.Empty, Pbr1), [null, new FaceMaterial(3, 1, Legacy1, Guid.Empty)]);
        w.ApplyObject(Here, Prim(5, m));
        var o = w.Snapshot().Objects[5];
        Assert.Equal(m, o.Materials);
        Assert.Equal(Legacy1, o.Materials![1].MaterialId);
    }

    [Fact]
    public void Overrides_that_arrive_first_wait_for_their_object_and_survive_its_later_updates()
    {
        var (w, changes) = NewWorld();
        w.ApplyMaterialOverrides(Here, 9, Red);
        Assert.Empty(changes);   // nothing to change yet
        w.ApplyObject(Here, Prim(9));
        Assert.Equal("{\"bc\":[1,0,0,1]}", w.Snapshot().Objects[9].MaterialOverrides![0]);
        w.ApplyObject(Here, Prim(9) with { Position = new Vector3(2, 2, 2) });   // a full update does not carry overrides
        Assert.Equal(new MaterialOverrides(Red), w.Snapshot().Objects[9].MaterialOverrides);
    }

    [Fact]
    public void New_overrides_replace_the_old_ones_a_repeat_changes_nothing_and_no_faces_clears_them()
    {
        var (w, changes) = NewWorld();
        w.ApplyObject(Here, Prim(9));
        changes.Clear();
        w.ApplyMaterialOverrides(Here, 9, Red);
        w.ApplyMaterialOverrides(Here, 9, Blue);
        Assert.Equal(2, changes.Count(c => c.Kind == WorldChangeKind.ObjectChanged));
        var o = w.Snapshot().Objects[9].MaterialOverrides!;
        Assert.Equal("{\"am\":2}", o[1]);
        Assert.Null(o[3]);
        w.ApplyMaterialOverrides(Here, 9, Blue.ToList());   // a repeated message
        Assert.Equal(2, changes.Count);
        w.ApplyMaterialOverrides(Here, 9, []);
        Assert.Null(w.Snapshot().Objects[9].MaterialOverrides);
        Assert.Equal(3, changes.Count);
    }

    [Fact]
    public void A_killed_object_takes_its_waiting_overrides_and_a_region_change_drops_everything()
    {
        var (w, _) = NewWorld();
        w.ApplyMaterialOverrides(Here, 12, Red);
        w.Kill(Here, [12]);
        w.ApplyMaterialOverrides(Here, 13, Red);
        w.ApplyLegacyMaterials(Here, [Material(Legacy1)]);
        w.BeginRegion(Elsewhere, new RegionInfo(Elsewhere.Name, Elsewhere.GridX, Elsewhere.GridY, 256, 256, true));
        w.ApplyObject(Elsewhere, Prim(13));
        Assert.Null(w.Snapshot().Objects[13].MaterialOverrides);   // the old region's waiting override is gone
        Assert.Null(w.LegacyMaterial(Legacy1));
        w.ApplyMaterialOverrides(Here, 13, Red);   // a late message from the old region
        Assert.Null(w.Snapshot().Objects[13].MaterialOverrides);
    }

    private static LegacyMaterial Material(Guid id, byte exponent = 51) =>
        new(id, new MaterialMap(NormalMap, 0, 0, 1, 1, 0), new MaterialMap(SpecMap, 0, 0, 1, 1, 0), new Rgba(1, 1, 1, 1), exponent, 0, LegacyAlphaMode.None, 0);

    [Fact]
    public void The_region_legacy_materials_are_kept_by_id_and_announced_once()
    {
        var (w, changes) = NewWorld();
        w.ApplyLegacyMaterials(Here, [Material(Legacy1)]);
        w.ApplyLegacyMaterials(Here, [Material(Legacy1)]);   // the same again
        Assert.Single(changes, c => c.Kind == WorldChangeKind.LegacyMaterialsReceived);
        Assert.Equal(51, w.LegacyMaterial(Legacy1)!.SpecularExponent);
        w.ApplyLegacyMaterials(Here, [Material(Legacy1, 80)]);   // changed values replace
        Assert.Equal(80, w.LegacyMaterials()[Legacy1].SpecularExponent);
        Assert.Equal(2, changes.Count(c => c.Kind == WorldChangeKind.LegacyMaterialsReceived));
    }

    // ---------------- capture ----------------

    private static IEnumerable<TransportEvent> Session()
    {
        yield return new LoginProgressed(LoginPhase.ConnectingToSim, null);
        yield return new SimConnected(Here, 256, 256, true);
        yield return new MaterialOverridesReceived(Here, 41, Red);   // before its object
        yield return new ObjectUpdated(Here, Prim(40, new FaceMaterials(new FaceMaterial(1, 2, Legacy1, Guid.Empty), [null, null, new FaceMaterial(0, 0, Guid.Empty, Pbr1)])), false, null);
        yield return new ObjectUpdated(Here, Prim(41, new FaceMaterials(new FaceMaterial(0, 0, Guid.Empty, Pbr1))), false, null);
        yield return new ObjectUpdated(Here, Prim(42), false, null);
        yield return new LegacyMaterialsReceived(Here, [Material(Legacy1)]);
        yield return new EventQueueRunning(Here);
        yield return new MovementComplete(Here);
        yield return new MaterialOverridesReceived(Here, 40, Blue);
        yield return new RenderMaterialsReceived(Here, 42, [new RenderMaterialEntry(1, Pbr1)]);   // a PBR id from the extra parameters
        yield return new RenderMaterialsReceived(Here, 43, null);
    }

    private static string Record()
    {
        var path = TestTemp.NewFilePath("materials", ".jsonl.gz");
        var fake = new FakeTransport { OnLogin = (_, _) => Task.FromResult(FakeTransport.Ok(G(1), 256, 256)) };
        var aliases = new CaptureFile.Aliases(Guid.Empty);
        using var writer = new CaptureFile.Writer(path, new CaptureHeader(CaptureFile.FormatVersion, "materials", Guid.Empty, 0, 0, "test"));
        var rec = new RecordingTransport(fake, writer, aliases);
        using var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, rec, new CoreLog(new ListSink(), "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        foreach (var e in Session())
        {
            if (e is SimConnected)
                fake.ConnectedSimulatorCount = 1;
            if (e is EventQueueRunning)
                fake.CurrentSim = Here;
            fake.Raise(e);
        }
        writer.WriteFooter(new CaptureFooter(rec.Stop(() => StateSummary.Of(session.World, aliases.Id))));
        return path;
    }

    [Fact]
    public void Material_data_round_trips_through_a_capture_and_its_replay()
    {
        var path = Record();
        try
        {
            var c = CaptureFile.Read(path);
            Assert.Equal(Session().Count(), c.Records.Count);
            var events = c.Records.Select(r => r.Event).ToList();
            var expected = Session().ToList();
            Assert.Equal(((ObjectUpdated)expected[3]).Prim.Materials, ((ObjectUpdated)events[3]).Prim.Materials);
            Assert.Equal(((LegacyMaterialsReceived)expected[6]).Materials, ((LegacyMaterialsReceived)events[6]).Materials);
            Assert.Equal(Blue, ((MaterialOverridesReceived)events[9]).Faces);
            Assert.Equal([new RenderMaterialEntry(1, Pbr1)], ((RenderMaterialsReceived)events[10]).Faces);
            Assert.Null(((RenderMaterialsReceived)events[11]).Faces);

            using var s = ReplayRunner.Load(c);
            var objs = s.World.Snapshot().Objects;
            Assert.Equal(Legacy1, objs[40].Materials!.Default.MaterialId);
            Assert.Equal(Pbr1, objs[40].Materials![2].RenderMaterialId);
            Assert.Equal(new MaterialOverrides(Blue), objs[40].MaterialOverrides);
            Assert.Equal(new MaterialOverrides(Red), objs[41].MaterialOverrides);   // the one that came before its object
            Assert.Equal(Pbr1, objs[42].Materials![1].RenderMaterialId);
            Assert.Equal(Guid.Empty, objs[42].Materials![0].RenderMaterialId);
            Assert.Equal(Material(Legacy1), s.World.LegacyMaterial(Legacy1));
            Assert.Equal(c.Footer!.Live, ReplayRunner.Run(c).Summary);
        }
        finally { TestTemp.Delete(path); }
    }

    [Fact]
    public void A_prim_without_materials_is_written_exactly_as_before()
    {
        var path = Record();
        try
        {
            var lines = CaptureFile.ReadText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var plain = lines.Single(l => l.Contains("\"e\":\"object\"") && l.Contains("\"local\":42,"));
            Assert.DoesNotContain("\"mat\"", plain);   // older captures and their hashes stay as they were
            Assert.Contains("\"mat\"", lines.Single(l => l.Contains("\"e\":\"object\"") && l.Contains("\"local\":40,")));
        }
        finally { TestTemp.Delete(path); }
    }

    // ---------------- material assets ----------------

    private static string Gltf => "{\"asset\":{\"version\":\"2.0\"},\"images\":[{\"uri\":\"" + NormalMap + "\"},{\"uri\":\"" + SpecMap + "\"}]," +
        "\"textures\":[{\"source\":0},{\"source\":1}],\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}},\"emissiveTexture\":{\"index\":1}}]}";

    /// <summary>A material asset as the grid stores it: LLSD holding the version, the type and the glTF as a string.</summary>
    private static byte[] XmlAsset(string version = "1.1", string type = "GLTF 2.0") => System.Text.Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" ?><llsd><map><key>version</key><string>" + version + "</string><key>type</key><string>" + type +
        "</string><key>data</key><string>" + System.Security.SecurityElement.Escape(Gltf) + "</string></map></llsd>");

    [Fact]
    public async Task A_material_asset_is_cached_raw_and_names_its_textures()
    {
        byte[] bytes = XmlAsset();
        Assert.Equal(Gltf, LmvMaterials.GltfOfMaterialAsset(bytes));
        Assert.Equal(new HashSet<Guid> { NormalMap, SpecMap }, LmvMaterials.TexturesOfMaterialAsset(Pbr1, bytes)!.ToHashSet());
        var notation = System.Text.Encoding.UTF8.GetBytes("{'version':'1.0','type':'GLTF 2.0','data':'" + Gltf + "'}");   // the glTF holds no single quote
        Assert.Equal(Gltf, LmvMaterials.GltfOfMaterialAsset(notation));
        var binary = LibreMetaverse.StructuredData.OSDParser.SerializeLLSDBinary(new LibreMetaverse.StructuredData.OSDMap
        {
            ["version"] = "1.1", ["type"] = "GLTF 2.0", ["data"] = Gltf,
        });
        Assert.Equal(Gltf, LmvMaterials.GltfOfMaterialAsset(binary));
        // the form the grid stores: the same body under the header written with spaces and capitals
        int body = Array.IndexOf(binary, (byte)'\n') + 1;
        var gridForm = System.Text.Encoding.ASCII.GetBytes("<? LLSD/Binary ?>\n").Concat(binary.Skip(body)).ToArray();
        Assert.Equal(Gltf, LmvMaterials.GltfOfMaterialAsset(gridForm));

        var dir = TestTemp.NewFolder("material-cache");
        try
        {
            var cache = new Viewer.Core.Assets.LocalAssetCache(dir);
            Assert.False(cache.HasMaterial(Pbr1));
            cache.PutMaterial(Pbr1, bytes);
            var r = await cache.FetchMaterialAsync(Pbr1, CancellationToken.None);
            Assert.Equal(Viewer.Core.Assets.AssetFetchStatus.Ok, r.Status);
            Assert.Equal(bytes, r.Bytes.ToArray());
            Assert.Equal(Viewer.Core.Assets.AssetFetchStatus.NotFound, (await cache.FetchMaterialAsync(Legacy1, CancellationToken.None)).Status);
        }
        finally { TestTemp.Delete(dir); }
    }

    [Fact]
    public void A_material_asset_is_asked_for_by_its_material_id()
    {
        var cap = new Uri("https://grid.example.org/CAPS/abc/");
        Assert.Equal("https://grid.example.org/CAPS/abc/?material_id=" + Pbr1.ToString("D"), LmvMaterials.MaterialUri(cap, Pbr1).ToString());
        Assert.EndsWith("?x=1&material_id=" + Pbr1.ToString("D"), LmvMaterials.MaterialUri(new Uri("https://grid.example.org/c?x=1"), Pbr1).ToString());
    }

    [Fact]
    public void Bytes_that_are_not_a_material_or_too_large_are_refused()
    {
        Assert.Null(LmvMaterials.TexturesOfMaterialAsset(Pbr1, System.Text.Encoding.UTF8.GetBytes("not a material")));
        Assert.Null(LmvMaterials.TexturesOfMaterialAsset(Pbr1, System.Text.Encoding.UTF8.GetBytes(Gltf)));   // bare glTF, no wrapper
        Assert.Null(LmvMaterials.GltfOfMaterialAsset(XmlAsset(version: "2.0")));
        Assert.Null(LmvMaterials.GltfOfMaterialAsset(XmlAsset(type: "GLTF 3.0")));
        Assert.Null(LmvMaterials.TexturesOfMaterialAsset(Pbr1, []));
        Assert.Null(LmvMaterials.TexturesOfMaterialAsset(Pbr1, new byte[LmvMaterials.MaterialCapBytes + 1]));
    }

    // ---------------- the legacy material fetch ----------------

    private sealed class Region(string name) { public override string ToString() => name; }

    [Fact]
    public async Task Legacy_material_ids_are_asked_once_per_region_in_batches_of_at_most_fifty()
    {
        var asked = new List<IReadOnlyList<Guid>>();
        var delivered = new List<LegacyMaterial>();
        var gate = new object();
        using var f = new LegacyMaterialFetcher<Region>(
            (_, ids, _) => { lock (gate) asked.Add(ids); return Task.FromResult<IReadOnlyList<LegacyMaterial>>(ids.Select(i => Material(i)).ToList()); },
            (_, m) => { lock (gate) delivered.AddRange(m); }, gather: TimeSpan.FromMilliseconds(20));
        var r = new Region("a");
        var ids = Enumerable.Range(1, 120).Select(i => G((uint)(1000 + i))).ToList();
        f.Want(r, ids);
        f.Want(r, ids.Take(30).Append(Guid.Empty));   // repeats and the empty id are not asked again
        await Until(() => { lock (gate) return delivered.Count == 120; });
        lock (gate)
        {
            Assert.All(asked, b => Assert.InRange(b.Count, 1, LegacyMaterialFetcher<Region>.MaxPerRequest));
            Assert.Equal(120, asked.SelectMany(b => b).Distinct().Count());
            Assert.Equal(120, asked.Sum(b => b.Count));
        }
        Assert.Equal(0, f.Queued);
    }

    private static int DeliveredCount(List<string> delivered) { lock (delivered) return delivered.Count; }

    [Fact]
    public async Task An_answer_for_a_region_left_behind_is_dropped_and_logout_ends_the_fetch()
    {
        var release = new TaskCompletionSource();
        var delivered = new List<string>();
        using var f = new LegacyMaterialFetcher<Region>(
            async (region, ids, _) =>
            {
                if (region.ToString() == "old") await release.Task;   // ignores its cancellation on purpose
                return ids.Select(i => Material(i)).ToList();
            },
            (region, _) => { lock (delivered) delivered.Add(region.ToString()!); }, gather: TimeSpan.Zero);
        f.Want(new Region("old"), [Legacy1]);
        await Until(() => f.Requests == 1);
        f.Want(new Region("new"), [Pbr1]);   // the agent moved on
        release.SetResult();
        // the fetcher counts an answer as delivered just before it calls the delivery callback, so the list is waited for as well
        await Until(() => f.Dropped == 1 && f.Delivered == 1 && DeliveredCount(delivered) == 1);
        lock (delivered) Assert.Equal(["new"], delivered);

        f.CancelAll();
        f.Want(new Region("later"), [NormalMap]);
        Assert.Equal(0, f.Queued);   // nothing is asked after logout
        Assert.Equal(2, f.Requests);
    }

    [Fact]
    public async Task A_failed_request_is_counted_and_the_next_ids_are_still_asked()
    {
        int calls = 0;
        var delivered = new List<LegacyMaterial>();
        using var f = new LegacyMaterialFetcher<Region>(
            (_, ids, _) => Interlocked.Increment(ref calls) == 1
                ? throw new HttpRequestException("refused")
                : Task.FromResult<IReadOnlyList<LegacyMaterial>>(ids.Select(i => Material(i)).ToList()),
            (_, m) => { lock (delivered) delivered.AddRange(m); }, gather: TimeSpan.Zero);
        var r = new Region("a");
        f.Want(r, [Legacy1]);
        await Until(() => f.Failed == 1);
        f.Want(r, [Legacy1, Pbr1]);   // the failed id is not asked again; the new one is
        await Until(() => { lock (delivered) return delivered.Count == 1; });
        lock (delivered) Assert.Equal(Pbr1, delivered.Single().Id);
    }

    private static async Task Until(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "condition not met within 20 s");
            await Task.Delay(20);
        }
    }
}
