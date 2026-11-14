// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Godot.Presentation;
using Viewer.Shell;
using GVector3 = Godot.Vector3;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// A replayed capture's own avatar as the viewer draws it: the capture (--tr-fixture=, a *.jsonl.gz) replayed into the world
    /// state, its meshes, textures and animations from the asset cache (--tr-assets=), through the avatar presenter and the prim
    /// layer (worn meshes, attachments, bakes, alpha, shape). Pictures from the front and the back (and a close one of the head)
    /// on a plain floor, and a report: the avatar's computed height and shape, the layers hidden, and every attachment with its
    /// point. Not a golden case: it reads a local-only capture and judges nothing by itself beyond "the avatar was drawn".
    /// </summary>
    private async Task<int> AvatarCaptureLook(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? assets, string[] args)
    {
        string fixture = Arg(args, "--tr-fixture=") ?? throw new ArgumentException("--tr-avatars=capturelook needs --tr-fixture=<capture>");
        if (assets is null) throw new ArgumentException("--tr-avatars=capturelook needs --tr-assets=");
        _assetDir = assets;
        var loaded = Load(fixture);
        var world = loaded.World;
        var rig = MakeRig(new Vector2I(1280, 720));
        var scenario = rig.Scenario;
        using var animations = AnimationSourceFor(assets);
        var meshes = CacheMeshSource(assets);
        var textures = CacheTextureSource(assets);
        double now = 0;
        var view = MakeView("server", rig);
        var prims = new PrimLayer(scenario, world, meshes, textures, _budget) { MaterialAssets = CacheMaterialSource(assets) };
        var avatars = new AvatarPresenter(scenario, world, loaded.SelfAgent, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, textures, prims.Residency, () => now);
        var presenter = new WorldPresenter(world, view, prims: prims, avatars: avatars);
        // the capture was replayed before the presenters existed: hand them the state as changes, as if it had just arrived
        var first = world.Snapshot();
        foreach (var a in first.Avatars.Values)
        {
            presenter.Apply(new Viewer.Core.World.WorldChange(Viewer.Core.World.WorldChangeKind.AvatarAdded, a.Id));
            presenter.Apply(new Viewer.Core.World.WorldChange(Viewer.Core.World.WorldChangeKind.AppearanceChanged, a.Id, a.AgentId));
            presenter.Apply(new Viewer.Core.World.WorldChange(Viewer.Core.World.WorldChangeKind.AnimationsChanged, a.Id, a.AgentId));
        }
        var sw = Stopwatch.StartNew();
        bool Settled() => avatars.Scene.Self is not null && avatars.Drawn >= 1 && prims.Dirty == 0 && animations.Waiting == 0 && animations.InFlight == 0 && avatars.BakeTextures >= 1 && avatars.Morphed >= 1
            && (Figure(prims.Describe(), "textures_waiting") ?? 0) == 0 && (Figure(prims.Describe(), "meshes_waiting") ?? 0) == 0 && (Figure(prims.Describe(), "rigged_lods_preparing") ?? 0) == 0;
        while (!Settled() && sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            presenter.Frame();
            await Frames(1);
        }
        for (int i = 0; i < 240; i++) { presenter.Frame(); await Frames(1); }   // the textures that were still being asked for
        while (!Settled() && sw.Elapsed < TimeSpan.FromSeconds(60)) { presenter.Frame(); await Frames(1); }
        var self = avatars.Scene.Self;
        if (self is null || !avatars.Scene.Place(self, out var soles, out float yaw)) { _failures.Add($"the capture's own avatar is not placed: self {loaded.SelfAgent}, scene avatars {avatars.Scene.Count}, self entry {(self is null ? "none" : "yes")}, world avatars {world.Snapshot().Avatars.Count}, drawn {avatars.Drawn}, dirty {prims.Dirty}, bake textures {avatars.BakeTextures}, {System.Text.Json.JsonSerializer.Serialize(avatars.Describe())}"); return 1; }
        if (!Settled()) _failures.Add($"not settled in 60 s: drawn {avatars.Drawn}, dirty {prims.Dirty}, animations ready {animations.Ready} waiting {animations.Waiting}, bake textures {avatars.BakeTextures}");
        var floor = Floor(scenario, soles, 8f);
        var cam = rig.Camera;
        cam.Near = 0.1f; cam.Far = 100f;
        async Task<string> Shoot(string name, float dir, float distance, float fov, float lookZ, float camZ)
        {
            float a = yaw + dir;
            var from = soles + new System.Numerics.Vector3(MathF.Cos(a) * distance, MathF.Sin(a) * distance, camZ);
            var at = soles + new System.Numerics.Vector3(0, 0, lookZ);
            cam.Fov = fov;
            cam.Position = new GVector3(from.X, from.Z, -from.Y);
            cam.LookAt(new GVector3(at.X, at.Z, -at.Y), GVector3.Up);
            presenter.Frame();
            await Frames(8);
            presenter.Frame();
            var img = rig.Texture.GetImage();
            img.Convert(Image.Format.Rgba8);
            string png = Path.Combine(_out, _tag + name + ".png");
            img.SavePng(png);
            return Path.GetFileName(png);
        }
        now = 0.6;
        var front = await Shoot("-front", 0, 7.5f, 40, 1.1f, 1.2f);
        var back = await Shoot("-back", MathF.PI, 7.5f, 40, 1.1f, 1.2f);
        var head = await Shoot("-head", 0, 1.6f, 30, 1.6f, 1.7f);
        var vpSet = world.Appearance(loaded.SelfAgent)?.VisualParams;
        var plain = vpSet is { } v0 ? AvatarShape.From(new AvatarRig(body.Skeleton), body.Shape!, v0).Measure(null) : null;   // the shape alone, no worn mesh's joint overrides
        // which of the avatar's transmitted parameters are morph targets, which move the skeleton, and which morphs are away from their defaults
        object? sliders = null;
        if (vpSet is { } v1 && body.Shape is { } def1)
        {
            var sh = AvatarShape.From(new AvatarRig(body.Skeleton), def1, v1);
            var morphIds = def1.Morphs.Select(m => m.Id).ToHashSet();
            var transmitted = v1.WireOrder.ToArray();
            var away = def1.Morphs.GroupBy(m => m.Id).Select(g => g.First())
                .Select(m => (m, eff: BodyMorph.Effective(m, sh.Weights, sh.Male)))
                .Where(t => MathF.Abs(t.eff - t.m.Default) > 1e-4f).OrderByDescending(t => MathF.Abs(t.eff - t.m.Default) / MathF.Max(1e-6f, t.m.Max - t.m.Min)).ToArray();
            var vol = BodyMorph.VolumeEffects(def1.Morphs, sh.Weights, sh.Male);
            sliders = new
            {
                transmitted = transmitted.Length,
                morph_params = transmitted.Count(id => morphIds.Contains(id)),
                skeleton_params = transmitted.Count(id => def1.TryGet(id, out var sp) && sp.Bones.Count > 0),
                driver_params = transmitted.Count(id => def1.TryGet(id, out var sp) && sp.Driven.Count > 0),
                morphs_away_from_default = away.Length,
                top = away.Take(24).Select(t => new { id = t.m.Id, name = t.m.Name, mesh = t.m.MeshType, weight = MathF.Round(t.eff, 3), @default = t.m.Default, range = new[] { t.m.Min, t.m.Max } }).ToArray(),
                volume_morphs = vol.Where(kv => kv.Value.Scale.Length() > 1e-4f || kv.Value.Pos.Length() > 1e-4f).Select(kv => new { volume = kv.Key, scale = new[] { MathF.Round(kv.Value.Scale.X, 4), MathF.Round(kv.Value.Scale.Y, 4), MathF.Round(kv.Value.Scale.Z, 4) }, pos = new[] { MathF.Round(kv.Value.Pos.X, 4), MathF.Round(kv.Value.Pos.Y, 4), MathF.Round(kv.Value.Pos.Z, 4) } }).ToArray(),
                height_official_m = plain?.Height, height_official_ft = plain is { } po ? MathF.Round(po.OfficialFeet, 2) : (float?)null,
                height_firestorm_m = plain?.FirestormHeight, height_firestorm_ft = plain is { } pf ? MathF.Round(pf.FirestormFeet, 2) : (float?)null,
                body_morphed = avatars.Morphed,
            };
        }
        var snap = world.Snapshot();
        var attachments = snap.Objects.Values.Where(o => o.AttachmentPoint is not null)
            .Select(o => new { point = o.AttachmentPoint, local = new[] { o.Position.X, o.Position.Y, o.Position.Z }, wearer_is_self = o.WearerAgentId == loaded.SelfAgent }).ToArray();
        _result["mode"] = "avatars-capturelook";
        _result["capture_look"] = new
        {
            fixture = Path.GetFileName(fixture), self_placed = true, soles = new[] { soles.X, soles.Y, soles.Z }, yaw = MathF.Round(yaw, 3),
            presenter = avatars.Describe(), shape_without_overrides_m = plain?.Height, shape_without_overrides_ft = plain is { } p0 ? p0.Height / 0.3048f : (float?)null, sliders, prims = prims.Describe(), attachments, front, back, head,
        };
        presenter.Dispose();
        view.Clear();
        if (view is Node n) { n.GetParent()?.RemoveChild(n); n.Free(); }
        Rids.Free(floor.Instance);
        Rids.Free(floor.Mesh);
        floor.Material.Dispose();
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }
}
