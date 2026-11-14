// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Assets;
using Viewer.Core.Meshing;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// Blended faces at scale (--tr-blendstress=&lt;case&gt;:&lt;objects&gt;): many objects whose faces are all blended, in a
/// 256 m region without water, seen by a camera circling them, to measure what the engine is handed for them. Cases:
/// single (a mesh of one face, closed: a cube's six sides in one face), box6 (a prim box, its six faces), mesh8 (a mesh
/// of eight faces: an octagonal prism's sides), and opaque6 (prim boxes with no face blended: how the engine counts the
/// draws of opaque surfaces, the reference for reading the others). Each object 1 m, on a 2 m grid round the region's
/// centre, its rotation and height seeded. After 2 s unrecorded, --tr-blendstress-seconds (default 10) of frames are
/// recorded: frame time, the engine's render CPU and GPU time, its draw calls, primitives and objects in the frame (all
/// passes, and the view's own passes), the ordering's time, the blended places and face instances, and the engine's buffer and video memory the scene added.
/// </summary>
public partial class TestRender
{
    private async Task<int> BlendStress(string spec, string[] args)
    {
        var parts = spec.Split(':');
        if (parts.Length != 2 || parts[0] is not ("single" or "box6" or "mesh8" or "opaque6"))
            throw new ArgumentException("--tr-blendstress=<single|box6|mesh8|opaque6>:<objects>");
        string kind = parts[0];
        int n = int.Parse(parts[1]);
        double seconds = double.Parse(Arg(args, "--tr-blendstress-seconds=") ?? "10", System.Globalization.CultureInfo.InvariantCulture);
        int seed = int.Parse(Arg(args, "--tr-seed=") ?? "8");
        var size = (Arg(args, "--tr-size=") ?? "1920x1080").Split('x').Select(int.Parse).ToArray();
        FreeRunning();
        var w = await PipelineWarmup.RunAsync(this, Viewport.Msaa.Disabled, () => Frames(1));
        _result["warmup"] = new { ms = Math.Round(w.Ms, 2), pipelines = w.Pipelines, variants = w.Variants, frames = w.Frames };
        long buffer0 = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed), video0 = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed);

        // the objects: a square grid 2 m apart round the region's centre
        var rnd = new Random(seed);
        int side = (int)Math.Ceiling(Math.Sqrt(n));
        float span = (side - 1) * 2f;
        var centre = new System.Numerics.Vector3(128, 128, 30);
        var half = new Rgba(0.8f, 0.5f, 0.3f, 0.5f);
        var opaque = new Rgba(0.8f, 0.5f, 0.3f, 1f);
        Guid single = new("4e560006-0000-4000-8000-0000000000b1"), eight = new("4e560006-0000-4000-8000-0000000000b8");
        var prims = new List<ZooPrim>(n);
        for (int i = 0; i < n; i++)
        {
            var at = new System.Numerics.Vector3(centre.X - span / 2 + (i % side) * 2f, centre.Y - span / 2 + (i / side) * 2f, centre.Z + rnd.NextSingle() * 10f);
            var rot = System.Numerics.Quaternion.Normalize(new(rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f, 1f));
            var shape = kind switch
            {
                "single" => PrimShape.Box with { SculptType = PrimShape.SculptTypeMesh, SculptId = single },
                "mesh8" => PrimShape.Box with { SculptType = PrimShape.SculptTypeMesh, SculptId = eight },
                _ => PrimShape.Box,
            };
            prims.Add(new ZooPrim((uint)(20000 + i), 0, kind, at, rot, System.Numerics.Vector3.One, shape, new FaceColors(kind == "opaque6" ? opaque : half)));
        }
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("blendstress", 1000, 1000, 256, 256, SizeAuthoritative: true));
        foreach (var p in PrimZoo.AsPrimData(prims)) world.ApplyObject(FixtureSim, p);

        // the meshes, made here (every LOD the same geometry), through the viewer's own mesh path
        var geometry = new Dictionary<Guid, MeshGeometry> { [single] = Geometry(StressCube(oneFace: true)), [eight] = Geometry(StressPrism(8)) };
        var meshes = new MeshSource(new SessionMeshes(new SessionAssets(SharedAssets, _ => true,
            (id, _) => Task.FromResult(geometry.ContainsKey(id) ? AssetFetchResult.Ok(new byte[] { 1 }) : AssetFetchResult.Of(AssetFetchStatus.NotFound))),
            (id, _) => (geometry.GetValueOrDefault(id), null)));

        var rig = MakeRig(new Vector2I(size[0], size[1]));
        var vpRid = rig.Viewport.GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(vpRid, true);
        var scenario = rig.Scenario;
        var cam = rig.Camera;
        cam.Fov = 60f; cam.Near = 0.2f; cam.Far = 512f;
        var view = MakeView("server", rig);
        var layer = new PrimLayer(scenario, world, meshes, null, _budget);
        // the camera circles the grid once in 20 s, 40 m above its middle, looking at its middle
        var target = new Vector3(centre.X, centre.Z + 5f, -centre.Y);
        float radius = span * 0.75f + 20f;
        void Place(double t)
        {
            double a = t / 20.0 * Math.Tau;
            cam.Position = target + new Vector3(radius * (float)Math.Cos(a), 40f, radius * (float)Math.Sin(a));
            cam.LookAt(target, Vector3.Up);
            layer.Camera = cam.GlobalPosition;
            layer.ResidencyView = PrimLayer.ViewOf(cam, size[0], size[1]);
            layer.UpdateLods();
            layer.ViewCamera = cam;
            layer.UpdateBlendOrder();
        }
        Place(0);
        var presenter = new WorldPresenter(world, view, prims: layer);
        await SettleMeshes(presenter, layer, meshes);
        long buffer1 = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed), video1 = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed);

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 2)
        {
            presenter.Frame();
            Place(clock.Elapsed.TotalSeconds);
            await Frames(1);
        }
        int cap = (int)(seconds * 5000) + 1024;
        var frameMs = new List<double>(cap);
        var cpuMs = new List<double>(cap);
        var gpuMs = new List<double>(cap);
        var orderMs = new List<double>(cap);
        var draws = new List<double>(cap);
        var primitives = new List<double>(cap);
        var objects = new List<double>(cap);
        var viewDraws = new List<double>(cap);
        var viewPrimitives = new List<double>(cap);
        var viewObjects = new List<double>(cap);
        var shadowDraws = new List<double>(cap);
        int[] gc0 = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        var pipelines0 = PipelineWarmup.Counters();
        var measure = Stopwatch.StartNew();
        long last = Stopwatch.GetTimestamp();
        while (measure.Elapsed.TotalSeconds < seconds)
        {
            presenter.Frame();
            Place(clock.Elapsed.TotalSeconds);
            orderMs.Add(layer.BlendOrderMs.Count > 0 ? layer.BlendOrderMs[^1] : 0);
            await Frames(1);
            long now = Stopwatch.GetTimestamp();
            frameMs.Add((now - last) * 1000.0 / Stopwatch.Frequency);
            last = now;
            cpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(vpRid));
            gpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(vpRid));
            draws.Add(RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame));
            primitives.Add(RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame));
            objects.Add(RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame));
            viewDraws.Add(RenderingServer.ViewportGetRenderInfo(vpRid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame));
            viewPrimitives.Add(RenderingServer.ViewportGetRenderInfo(vpRid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.PrimitivesInFrame));
            viewObjects.Add(RenderingServer.ViewportGetRenderInfo(vpRid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.ObjectsInFrame));
            shadowDraws.Add(RenderingServer.ViewportGetRenderInfo(vpRid, RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.DrawCallsInFrame));
        }
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        img.SavePng(System.IO.Path.Combine(_out, _tag + ".png"));
        var pipelines = PipelineWarmup.Counters().Zip(pipelines0, (a, b) => a - b).ToArray();
        int surfaces = kind switch { "single" => 1, "mesh8" => 8, _ => 6 };
        // the check (--tr-blendstress-expect-surfaces=N): every recorded frame hands the engine N surfaces to draw in the
        // view (its count of objects in the frame counts each instance's surfaces); one object with k blended faces
        // must be k, each face once, not each face instance drawing every surface
        if (Arg(args, "--tr-blendstress-expect-surfaces=") is { } expect)
        {
            int want = int.Parse(expect);
            var wrong = viewObjects.Where(v => (int)v != want).ToList();
            if (viewObjects.Count == 0) _failures.Add("no frame recorded");
            else if (wrong.Count > 0) _failures.Add($"surfaces handed to the engine: {wrong.Count} of {viewObjects.Count} frames not {want} (seen {string.Join(",", wrong.Distinct().Take(5))})");
        }
        _result["mode"] = "blendstress";
        _result["blendstress"] = new
        {
            kind, objects = n, surfaces_per_object = surfaces, blended_faces = kind == "opaque6" ? 0 : n * surfaces,
            seconds, frames = frameMs.Count, size = $"{size[0]}x{size[1]}", seed,
            instances = layer.Instances, blend_objects = layer.BlendObjects, blend_places = layer.BlendPlaces, blend_face_instances = layer.BlendFaceInstances,
            blend_part_meshes = layer.BlendPartMeshes, blend_part_bytes = layer.BlendPartBytes, blend_whole_mesh_fallbacks = layer.BlendWholeMeshFallbacks,
            mesh_lods = layer.MeshLodsHeld, mesh_bytes = layer.MeshBytesHeld, prim_meshes = layer.Meshes,
            buffer_bytes_scene = buffer1 - buffer0, video_bytes_scene = video1 - video0,
            frame_ms = Dist(frameMs), over_16_7_ms = frameMs.Count(f => f > 16.7),
            render_cpu_ms = Dist(cpuMs), render_gpu_ms = Dist(gpuMs), order_ms = Dist(orderMs),
            draw_calls = Dist(draws), primitives = Dist(primitives), objects_drawn = Dist(objects),
            view_draw_calls = Dist(viewDraws), view_primitives = Dist(viewPrimitives), view_objects = Dist(viewObjects), shadow_draw_calls = Dist(shadowDraws),
            collections = new { gen0 = GC.CollectionCount(0) - gc0[0], gen1 = GC.CollectionCount(1) - gc0[1], gen2 = GC.CollectionCount(2) - gc0[2] },
            pipelines_compiled = PipelineWarmup.Named(pipelines),
        };
        presenter.Dispose();
        layer.Clear();
        meshes.Dispose();
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }

    private static MeshGeometry Geometry(List<MeshFace> faces) => new([new MeshLod(faces), new MeshLod(faces), new MeshLod(faces), new MeshLod(faces)], null);

    /// <summary>A unit cube's six sides (SL axes, -0.5..0.5, wound outward), as six faces or all in one.</summary>
    private static List<MeshFace> StressCube(bool oneFace)
    {
        var quads = new List<(System.Numerics.Vector3 N, System.Numerics.Vector3 U, System.Numerics.Vector3 V)>
        {
            (System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ),
            (-System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitY),
            (System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitX),
            (-System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ),
            (System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY),
            (-System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitX),
        };
        var faces = quads.Select(q => Quad(q.N * 0.5f, q.U * 0.5f, q.V * 0.5f, q.N)).ToList();
        return oneFace ? [Merge(faces)] : faces;
    }

    /// <summary>A prism's <paramref name="sides"/> sides round the Z axis (radius 0.5, height 1), one face each.</summary>
    private static List<MeshFace> StressPrism(int sides)
    {
        var faces = new List<MeshFace>(sides);
        for (int s = 0; s < sides; s++)
        {
            float a0 = MathF.Tau * s / sides, a1 = MathF.Tau * (s + 1) / sides, am = (a0 + a1) / 2;
            var p0 = new System.Numerics.Vector3(0.5f * MathF.Cos(a0), 0.5f * MathF.Sin(a0), 0);
            var p1 = new System.Numerics.Vector3(0.5f * MathF.Cos(a1), 0.5f * MathF.Sin(a1), 0);
            var n = new System.Numerics.Vector3(MathF.Cos(am), MathF.Sin(am), 0);
            faces.Add(Quad((p0 + p1) / 2, (p1 - p0) / 2, new(0, 0, 0.5f), n));
        }
        return faces;
    }

    private static MeshFace Quad(System.Numerics.Vector3 c, System.Numerics.Vector3 u, System.Numerics.Vector3 v, System.Numerics.Vector3 n)
    {
        System.Numerics.Vector3[] p = [c - u - v, c + u - v, c + u + v, c - u + v];
        return new MeshFace(p.SelectMany(x => new[] { x.X, x.Y, x.Z }).ToArray(), Enumerable.Range(0, 4).SelectMany(_ => new[] { n.X, n.Y, n.Z }).ToArray(),
            [0, 0, 1, 0, 1, 1, 0, 1], [0, 1, 2, 0, 2, 3]);
    }

    private static MeshFace Merge(List<MeshFace> faces)
    {
        var idx = new List<int>();
        int at = 0;
        foreach (var f in faces) { idx.AddRange(f.Indices.Select(i => i + at)); at += f.Vertices; }
        return new MeshFace(faces.SelectMany(f => f.Positions).ToArray(), faces.SelectMany(f => f.Normals).ToArray(), faces.SelectMany(f => f.Uvs).ToArray(), idx.ToArray());
    }
}
