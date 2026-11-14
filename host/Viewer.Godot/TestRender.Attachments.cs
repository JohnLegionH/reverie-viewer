// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Assets;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// Avatars wearing rigid objects (--tr-avatars=attach|attachcrowd, with --tr-character=&lt;character folder&gt;): attach draws two
/// avatars, one standing and one mid-walk, each wearing the same boxes on seven attachment points (two on one point, a
/// rotated offset, a three-prim link set, a Bento point), from the front and from behind, through the viewer's own path (the
/// world state, the avatar presenter, the prim layer); attachcrowd measures N walking avatars each wearing eight boxes on an
/// empty stage. Written against what the viewer drew before attachments were drawn, so it also runs on a build that skips them
/// (and then fails for the boxes it does not draw).
/// </summary>
public partial class TestRender
{
    private static readonly Guid AttachAgentA = Guid.Parse("a77ac4a0-0000-4000-8000-00000000000a"), AttachAgentB = Guid.Parse("a77ac4b0-0000-4000-8000-00000000000b");

    /// <summary>Attachment points of the standard skeleton by id, with the names the scene expects them to have (checked against the loaded folder).</summary>
    private static readonly (int Id, string Name)[] AttachPoints =
    [
        (1, "Chest"), (2, "Skull"), (3, "Left Shoulder"), (5, "Left Hand"), (6, "Right Hand"), (10, "Pelvis"), (43, "Tail Base"),
    ];

    private static Guid PrimId(uint local) => new((int)local, 0, 0, new byte[8]);

    /// <summary>Two worn mesh assets: a plain cube, and the same cube with a skin block (a rigged mesh, which is not drawn on an avatar yet).</summary>
    private static readonly Guid PlainMeshId = Guid.Parse("a77a0001-0000-4000-8000-000000000001"), RiggedMeshId = Guid.Parse("a77a0001-0000-4000-8000-000000000002");

    private MeshSource WornMeshes()
    {
        var cube = StressCube(oneFace: false);
        var geometry = new Dictionary<Guid, MeshGeometry>
        {
            [PlainMeshId] = Geometry(cube),
            [RiggedMeshId] = Geometry(cube) with { Skin = new MeshSkin(JointCount: 4, HasBindShapeMatrix: true, WeightedVertices: 24) },
        };
        return new MeshSource(new SessionMeshes(new SessionAssets(SharedAssets, _ => true,
            (id, _) => Task.FromResult(geometry.ContainsKey(id) ? AssetFetchResult.Ok(new byte[] { 1 }) : AssetFetchResult.Of(AssetFetchStatus.NotFound))),
            (id, _) => (geometry.GetValueOrDefault(id), null)));
    }

    private static PrimData WornMesh(uint local, uint wearer, int point, System.Numerics.Vector3 position, float side, Rgba colour, Guid asset) =>
        new(local, PrimId(local), wearer, point, position, System.Numerics.Quaternion.Identity, new System.Numerics.Vector3(side, side, side),
            Shape: PrimShape.Box with { SculptType = PrimShape.SculptTypeMesh, SculptId = asset }, Colors: new FaceColors(colour));

    /// <summary>One prim-layer figure by name from its description (null when the layer does not report it).</summary>
    private static int? Figure(object description, string name) =>
        JsonSerializer.SerializeToElement(description).TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static PrimData Box(uint local, uint parent, int point, System.Numerics.Vector3 position, System.Numerics.Quaternion rotation, float side, Rgba colour) =>
        new(local, PrimId(local), parent, point, position, rotation, new System.Numerics.Vector3(side, side, side), Shape: PrimShape.Box, Colors: new FaceColors(colour));

    /// <summary>What each of the two avatars of the scene wears, as updates from the region: ten prims on seven points. Returns the count.</summary>
    private static int WearScene(WorldState world, uint wearer, uint firstLocal)
    {
        var none = System.Numerics.Quaternion.Identity;
        System.Numerics.Quaternion About(System.Numerics.Vector3 axis, float degrees) => System.Numerics.Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180f);
        uint next = firstLocal;
        int count = 0;
        void Put(uint parent, int point, System.Numerics.Vector3 pos, System.Numerics.Quaternion rot, float side, Rgba colour, out uint local)
        {
            local = next++;
            world.ApplyObject(FixtureSim, Box(local, parent, point, pos, rot, side, colour), eventIsAttachment: true);
            count++;
        }
        Put(wearer, 2, new(0, 0, 0.17f), none, 0.16f, new(0.85f, 0.15f, 0.15f, 1), out _);                          // head: a red box above the skull
        Put(wearer, 2, new(0, 0, 0.34f), About(System.Numerics.Vector3.UnitZ, 45), 0.08f, new(0.95f, 0.95f, 0.95f, 1), out _);   // a second object on the same point
        Put(wearer, 5, new(0, 0, -0.08f), none, 0.10f, new(0.15f, 0.7f, 0.2f, 1), out _);                           // left hand: green
        Put(wearer, 6, new(0, 0, -0.08f), none, 0.10f, new(0.15f, 0.3f, 0.85f, 1), out _);                          // right hand: blue
        Put(wearer, 1, new(0.1f, 0, 0), About(System.Numerics.Vector3.UnitZ, 45), 0.16f, new(0.95f, 0.8f, 0.1f, 1), out _);   // chest: yellow, offset and turned
        Put(wearer, 10, new(0.14f, 0, 0), none, 0.14f, new(0.8f, 0.2f, 0.75f, 1), out _);                           // pelvis: magenta
        Put(wearer, 43, new(-0.12f, 0, 0), none, 0.12f, new(0.1f, 0.8f, 0.85f, 1), out _);                          // a Bento point (tail base): cyan
        // a link set of three on the left shoulder: the root turned 30 degrees about x, its children placed in the root's frame
        Put(wearer, 3, new(0, 0, 0.16f), About(System.Numerics.Vector3.UnitX, 30), 0.07f, new(0.95f, 0.5f, 0.1f, 1), out uint root);
        Put(root, 3, new(0, 0, 0.12f), none, 0.07f, new(0.5f, 0.2f, 0.8f, 1), out _);
        Put(root, 3, new(0, 0.12f, 0), none, 0.07f, new(0.1f, 0.55f, 0.55f, 1), out _);
        return count;
    }

    /// <summary>
    /// Two avatars, one standing and one mid-walk (the walker 0.6 s into the grid's walk), each wearing the same ten boxes, drawn
    /// from the front (--tr-golden=) and from behind (--tr-golden-back=). --tr-alter=still (negative control): the walker has no
    /// animation, so it stands and its boxes stand with it; the diff must fail.
    /// </summary>
    private async Task<int> AvatarAttach(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? assets, string? golden, string? goldenBack, string? alter)
    {
        var rig = MakeRig();
        var scenario = rig.Scenario;
        var floor = Floor(scenario, new System.Numerics.Vector3(128, 128, 20), 8f);
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("attachments", 1000, 1000, 256, 256, true));
        using var animations = AnimationSourceFor(assets);
        double now = 0;
        var view = MakeView("server", rig);
        var avatars = new AvatarPresenter(scenario, world, Guid.Empty, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, null, null, () => now);
        using var meshes = WornMeshes();
        var prims = new PrimLayer(scenario, world, meshes, null, _budget);
        var presenter = new WorldPresenter(world, view, prims: prims, avatars: avatars);
        var names = AttachPoints.Select(p => body.Skeleton.AttachmentPoints.FirstOrDefault(a => a.Id == p.Id)?.Name).ToArray();
        for (int i = 0; i < AttachPoints.Length; i++)
            if (names[i] != AttachPoints[i].Name) _failures.Add($"attachment point {AttachPoints[i].Id} is '{names[i]}' in this character folder, the scene expects '{AttachPoints[i].Name}'");
        // the region places an avatar by its centre: 0.95 m above the floor for its 1.9 m; both face east, the camera stands east of them
        var size = new System.Numerics.Vector3(0.45f, 0.6f, 1.9f);
        var east = System.Numerics.Quaternion.Identity;
        world.ApplyAvatar(FixtureSim, new(800, AttachAgentA, "Standing Test", new(128, 127.25f, 20.95f), east, size));
        world.ApplyAvatar(FixtureSim, new(900, AttachAgentB, "Walking Test", new(128, 128.75f, 20.95f), east, size));
        world.ApplyAnimations(FixtureSim, AttachAgentB, alter == "still" ? [] : [new(WalkId, 2)]);
        int wornA = WearScene(world, 800, 8001), wornB = WearScene(world, 900, 9001);
        // a plain mesh on the right shoulder (drawn like a prim), each; a worn mesh whose skin cannot be read is drawn as a plain mesh too, and is covered by the rigged cases' controls
        foreach (var (wearer, first) in new[] { (800u, 8051u), (900u, 9051u) })
        {
            world.ApplyObject(FixtureSim, WornMesh(first, wearer, 4, new(0, 0, 0.16f), 0.10f, new(0.6f, 0.9f, 0.2f, 1), PlainMeshId), eventIsAttachment: true);
        }
        const int plainMeshes = 2;   // one worn plain mesh each
        int expected = wornA + wornB + plainMeshes;
        var cam = rig.Camera;
        cam.Fov = 40f; cam.Near = 0.1f; cam.Far = 100f;
        void Aim(bool front)
        {
            cam.Position = new Vector3(front ? 133.3f : 122.7f, 21.3f, -128f);
            cam.LookAt(new Vector3(128f, 20.95f, -128f), Vector3.Up);
        }
        Aim(true);
        var sw = Stopwatch.StartNew();
        bool Settled() => avatars.Drawn == 2 && prims.Instances == expected && prims.Dirty == 0 && animations.Waiting == 0 && animations.InFlight == 0
            && animations.Ready >= (alter == "still" ? 0 : 1);
        while (!Settled() && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            presenter.Frame();
            await Frames(1);
        }
        if (!Settled()) _failures.Add($"not settled in 30 s: avatars drawn {avatars.Drawn} of 2, boxes drawn {prims.Instances} of {expected}, dirty {prims.Dirty}, animations ready {animations.Ready}");
        presenter.Frame();
        now = 0.6;
        presenter.Frame();
        await Frames(8);
        presenter.Frame();
        var front = rig.Texture.GetImage();
        front.Convert(Image.Format.Rgba8);
        front.SavePng(Path.Combine(_out, _tag + ".png"));
        Aim(false);
        presenter.Frame();
        await Frames(8);
        presenter.Frame();
        var back = rig.Texture.GetImage();
        back.Convert(Image.Format.Rgba8);
        string backPng = Path.Combine(_out, _tag + "-back.png");
        back.SavePng(backPng);
        _result["mode"] = "avatars-attach";
        _result["avatars_attach"] = new
        {
            expected_boxes = expected, boxes_drawn = prims.Instances, avatars_drawn = avatars.Drawn, points = AttachPoints.Select((p, i) => new { id = p.Id, name = names[i] }).ToArray(),
            prims = prims.Describe(), presenter = avatars.Describe(), altered = alter, png = _tag + ".png", png_back = Path.GetFileName(backPng),
        };
        if (boxesDrawnShortfall(prims.Instances, expected) is string shortfall) _failures.Add(shortfall);
        var description = prims.Describe();
        if (Figure(description, "skipped_rigged_attachments") != 0 || Figure(description, "rigged_wearers") != 0)
            _failures.Add($"rigged meshes worn: counted {Figure(description, "skipped_rigged_attachments")?.ToString() ?? "not reported"}, avatars wearing them {Figure(description, "rigged_wearers")?.ToString() ?? "not reported"}; expected none (no rigged mesh is worn in this scene)");
        if (golden is not null) DiffGolden(front, golden);
        if (goldenBack is not null) DiffGolden(back, goldenBack, "diff_back");
        presenter.Dispose();
        if (avatars.LiveResources != 0) _failures.Add($"{avatars.LiveResources} avatar resources live after the presenter went");
        prims.Clear();
        view.Clear();
        if (view is Node n) { n.GetParent()?.RemoveChild(n); n.Free(); }
        Rids.Free(floor.Instance);
        Rids.Free(floor.Mesh);
        floor.Material.Dispose();
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;

        static string? boxesDrawnShortfall(int drawn, int wanted) => drawn == wanted ? null : $"{drawn} of {wanted} worn boxes are drawn";
    }

    /// <summary>The process's peak working set and peak private (committed) memory so far, in MiB.</summary>
    private static object PeakMemory()
    {
        using var p = Process.GetCurrentProcess();
        return new { peak_working_set_mib = Math.Round(p.PeakWorkingSet64 / 1048576.0, 1), peak_private_mib = Math.Round(p.PeakPagedMemorySize64 / 1048576.0, 1) };
    }

    /// <summary>The eight points each crowd avatar wears a box on.</summary>
    private static readonly int[] CrowdPoints = [2, 5, 6, 1, 10, 7, 8, 3];

    /// <summary>
    /// N avatars walking on 1 m circles (the grid's walk, spread on a 2.5 m grid) each wearing eight boxes, on an empty stage, from
    /// updates through the viewer's own path (the world state, the avatar presenter, the prim layer). After 2 s unrecorded,
    /// --tr-crowd-seconds (default 10) of frames are recorded, as the plain crowd does, and the boxes drawn are counted.
    /// </summary>
    private async Task<int> AvatarAttachCrowd(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? assets, string[] args)
    {
        int n = int.Parse(Arg(args, "--tr-crowd=") ?? "25");
        double seconds = double.Parse(Arg(args, "--tr-crowd-seconds=") ?? "10", System.Globalization.CultureInfo.InvariantCulture);
        var size = (Arg(args, "--tr-size=") ?? "1920x1080").Split('x').Select(int.Parse).ToArray();
        int seed = int.Parse(Arg(args, "--tr-seed=") ?? "8");
        var walk = ClipFrom(assets, WalkId);
        if (walk is null) { _failures.Add("the walk animation is not in the asset cache (--tr-assets=)"); return 1; }
        FreeRunning();
        var w = await PipelineWarmup.RunAsync(this, Viewport.Msaa.Disabled, () => Frames(1));
        _result["warmup"] = new { ms = Math.Round(w.Ms, 2), pipelines = w.Pipelines, variants = w.Variants, frames = w.Frames };
        var rig = MakeRig(new Vector2I(size[0], size[1]));
        var vpRid = rig.Viewport.GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(vpRid, true);
        var scenario = rig.Scenario;
        var cam = rig.Camera;
        cam.Fov = 60f; cam.Near = 0.2f; cam.Far = 512f;
        var centre = new System.Numerics.Vector3(128, 128, 20);
        var floor = Floor(scenario, centre, 120f);
        using var animations = AnimationSourceFor(assets);
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("attachments", 1000, 1000, 256, 256, true));
        double now = 0;
        var view = MakeView("server", rig);
        var avatars = new AvatarPresenter(scenario, world, Guid.Empty, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, null, null, () => now);
        var prims = new PrimLayer(scenario, world, null, null, _budget);
        var presenter = new WorldPresenter(world, view, prims: prims, avatars: avatars);
        int side = (int)Math.Ceiling(Math.Sqrt(n));
        float span = (side - 1) * 2.5f;
        var rnd = new Random(seed);
        var spots = new List<(System.Numerics.Vector3 C, float Start, uint Local, Guid Agent)>();
        var avatarSize = new System.Numerics.Vector3(0.45f, 0.6f, 1.9f);
        uint nextPrim = 100000;
        var colours = new Rgba[] { new(0.85f, 0.15f, 0.15f, 1), new(0.15f, 0.7f, 0.2f, 1), new(0.15f, 0.3f, 0.85f, 1), new(0.95f, 0.8f, 0.1f, 1), new(0.8f, 0.2f, 0.75f, 1), new(0.1f, 0.8f, 0.85f, 1), new(0.95f, 0.5f, 0.1f, 1), new(0.9f, 0.9f, 0.9f, 1) };
        int worn = 0;
        for (int i = 0; i < n; i++)
        {
            float x = centre.X - span / 2 + (i % side) * 2.5f, y = centre.Y - span / 2 + (i / side) * 2.5f;
            float start = (float)(rnd.NextDouble() * Math.Tau);
            uint local = (uint)(1000 + i);
            var agent = new Guid(i + 1, 0, 0, new byte[8]);
            spots.Add((new(x, y, centre.Z), start, local, agent));
            world.ApplyAvatar(FixtureSim, new(local, agent, "Crowd " + i, new(x + MathF.Cos(start), y + MathF.Sin(start), centre.Z + 0.95f), System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, start + MathF.PI / 2), avatarSize));
            world.ApplyAnimations(FixtureSim, agent, [new(WalkId, 2)]);
            for (int k = 0; k < CrowdPoints.Length; k++)
            {
                world.ApplyObject(FixtureSim, Box(nextPrim++, local, CrowdPoints[k], new(0, 0, 0.05f), System.Numerics.Quaternion.Identity, 0.1f, colours[k]), eventIsAttachment: true);
                worn++;
            }
        }
        float back = span * 0.9f + 9f;
        var target = new Vector3(centre.X, centre.Z + 1f, -centre.Y);
        cam.Position = target + new Vector3(back * 0.5f, back * 0.45f + 2f, back * 0.75f);
        cam.LookAt(target, Vector3.Up);
        const float Radius = 1f, Speed = 0.6f;   // radians a second round each circle
        void Step(double t)
        {
            now = t;
            foreach (var (c, start, local, _) in spots)
            {
                float a = start + (float)(t * Speed);
                var pos = new System.Numerics.Vector3(c.X + Radius * MathF.Cos(a), c.Y + Radius * MathF.Sin(a), c.Z + 0.95f);
                var vel = new System.Numerics.Vector3(-MathF.Sin(a), MathF.Cos(a), 0) * (Radius * Speed);
                world.ApplyMove(FixtureSim, local, true, pos, System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, a + MathF.PI / 2), vel);
            }
        }
        var clock = Stopwatch.StartNew();
        // settle: every avatar drawn and every box presented, then two seconds more unrecorded
        var settle = Stopwatch.StartNew();
        while ((avatars.Drawn < n || prims.Dirty > 0 || animations.Waiting + animations.InFlight > 0) && settle.Elapsed < TimeSpan.FromSeconds(60))
        {
            Step(clock.Elapsed.TotalSeconds);
            presenter.Frame();
            await Frames(1);
        }
        var warm = Stopwatch.StartNew();
        while (warm.Elapsed.TotalSeconds < 2)
        {
            Step(clock.Elapsed.TotalSeconds);
            presenter.Frame();
            await Frames(1);
        }
        // --tr-gc-hammer=1 (stress): another thread forces full compacting collections and runs the finalizers all the while, as the
        // surface stress does, so a Godot value the per-frame engine calls leave for the collector would show
        long hammered = 0;
        using var stopHammer = new System.Threading.CancellationTokenSource();
        var hammer = Arg(args, "--tr-gc-hammer=") == "1" ? new System.Threading.Thread(() =>
        {
            var keep = new List<byte[]>();
            while (!stopHammer.IsCancellationRequested)
            {
                for (int k = 0; k < 64; k++) keep.Add(new byte[4096 + k]);
                keep.Clear();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                System.Threading.Interlocked.Increment(ref hammered);
            }
        }) { IsBackground = true, Name = "attachstress-gc" } : null;
        hammer?.Start();
        int cap = (int)(seconds * 5000) + 1024;
        var frameMs = new List<double>(cap);
        var sceneMs = new List<double>(cap);
        var cpuMs = new List<double>(cap);
        var gpuMs = new List<double>(cap);
        var draws = new List<double>(cap);
        int[] gc0 = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        var pipelines0 = PipelineWarmup.Counters();
        var measure = Stopwatch.StartNew();
        long last = Stopwatch.GetTimestamp();
        while (measure.Elapsed.TotalSeconds < seconds)
        {
            long s0 = Stopwatch.GetTimestamp();
            Step(clock.Elapsed.TotalSeconds);
            presenter.Frame();
            sceneMs.Add((Stopwatch.GetTimestamp() - s0) * 1000.0 / Stopwatch.Frequency);
            await Frames(1);
            long t = Stopwatch.GetTimestamp();
            frameMs.Add((t - last) * 1000.0 / Stopwatch.Frequency);
            last = t;
            cpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(vpRid));
            gpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(vpRid));
            draws.Add(RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame));
        }
        stopHammer.Cancel();
        hammer?.Join();
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        img.SavePng(Path.Combine(_out, _tag + ".png"));
        var pipelines = PipelineWarmup.Counters().Zip(pipelines0, (a, b) => a - b).ToArray();
        _result["mode"] = "attachcrowd";
        _result["attachcrowd"] = new
        {
            avatars = n, boxes_worn = worn, boxes_drawn = prims.Instances, avatars_drawn = avatars.Drawn, gc_hammer_collections = hammered, seconds, frames = frameMs.Count, size = $"{size[0]}x{size[1]}", seed,
            frame_ms = Dist(frameMs), over_16_7_ms = frameMs.Count(f => f > 16.7), scene_frame_ms = Dist(sceneMs),
            render_cpu_ms = Dist(cpuMs), render_gpu_ms = Dist(gpuMs), draw_calls = Dist(draws),
            collections = new { gen0 = GC.CollectionCount(0) - gc0[0], gen1 = GC.CollectionCount(1) - gc0[1], gen2 = GC.CollectionCount(2) - gc0[2] },
            pipelines_compiled = PipelineWarmup.Named(pipelines), prims = prims.Describe(),
            process_memory = PeakMemory(),
        };
        if (prims.Instances != worn) _failures.Add($"{prims.Instances} of {worn} worn boxes are drawn");
        presenter.Dispose();
        prims.Clear();
        view.Clear();
        if (view is Node vn) { vn.GetParent()?.RemoveChild(vn); vn.Free(); }
        Rids.Free(floor.Instance);
        Rids.Free(floor.Mesh);
        floor.Material.Dispose();
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// What a worn object's textures are judged by: where its wearer is now, not where the object was presented. One avatar wears a
    /// textured box; the camera looks north from the south of the stage; the avatar starts behind the camera (out of view), then
    /// walks into view. The box's texture is not in view at first and is once the avatar is.
    /// </summary>
    private async Task<int> AvatarAttachTextures(AvatarBody body, PreparedSkinnedSurface[] surfaces, string[] args)
    {
        string? texDir = Arg(args, "--tr-textures=");
        if (texDir is null) { _failures.Add("--tr-textures= is needed (the texture zoo's images)"); return 1; }
        var rig = MakeRig();
        var scenario = rig.Scenario;
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("attachments", 1000, 1000, 256, 256, true));
        double now = 0;
        var view = MakeView("server", rig);
        using var textures = TextureSourceFor(texDir, TextureZoo.Images);
        var avatars = new AvatarPresenter(scenario, world, Guid.Empty, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), null, null, null, () => now);
        var prims = new PrimLayer(scenario, world, null, textures, _budget);
        var presenter = new WorldPresenter(world, view, prims: prims, avatars: avatars);
        var texture = TextureZoo.Images.Keys.First();
        var cam = rig.Camera;
        cam.Fov = 40f; cam.Near = 0.1f; cam.Far = 200f;
        cam.Position = new Vector3(128f, 22f, -100f);
        cam.LookAt(new Vector3(128f, 22f, -140f), Vector3.Up);   // north
        prims.Camera = cam.GlobalPosition;
        prims.ResidencyView = PrimLayer.ViewOf(cam, (int)rig.Viewport.Size.X, (int)rig.Viewport.Size.Y);
        var size = new System.Numerics.Vector3(0.45f, 0.6f, 1.9f);
        var east = System.Numerics.Quaternion.Identity;
        world.ApplyAvatar(FixtureSim, new(800, AttachAgentA, "Walker Test", new(128, 60, 20.95f), east, size));
        world.ApplyObject(FixtureSim, new PrimData(8001, PrimId(8001), 800, 2, new(0, 0, 0.17f), System.Numerics.Quaternion.Identity, new System.Numerics.Vector3(0.3f, 0.3f, 0.3f),
            Shape: PrimShape.Box, Colors: new FaceColors(new Rgba(1, 1, 1, 1)), Textures: new FaceTextures(FaceTexture.Plain(texture))), eventIsAttachment: true);
        var sw = Stopwatch.StartNew();
        while ((!prims.Draws(8001) || avatars.Drawn != 1) && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            presenter.Frame();
            await Frames(1);
        }
        for (int i = 0; i < 10; i++) { presenter.Frame(); await Frames(1); }
        bool beforeVisible = prims.Residency!.IsVisible(texture);
        // walk north into the camera's view, 1.5 m a step
        for (int y = 60; y <= 120; y += 2)
        {
            now += 0.1;
            world.ApplyMove(FixtureSim, 800, true, new(128, y, 20.95f), east, System.Numerics.Vector3.Zero);
            presenter.Frame();
            await Frames(1);
        }
        for (int i = 0; i < 20; i++) { now += 0.1; presenter.Frame(); await Frames(1); }
        bool afterVisible = prims.Residency!.IsVisible(texture);
        _result["mode"] = "avatars-attach-textures";
        _result["avatars_attach_textures"] = new { drawn = prims.Draws(8001), visible_before = beforeVisible, visible_after = afterVisible, users = prims.Residency.Users };
        if (!prims.Draws(8001)) _failures.Add("the worn textured box is not drawn");
        if (beforeVisible) _failures.Add("the worn box's texture is in view while its wearer is behind the camera");
        if (!afterVisible) _failures.Add("the worn box's texture is not in view after its wearer walked into view (its bounds stayed where it was presented)");
        presenter.Dispose();
        prims.Clear();
        view.Clear();
        if (view is Node n) { n.GetParent()?.RemoveChild(n); n.Free(); }
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }
}
