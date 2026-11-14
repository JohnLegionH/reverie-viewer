// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class TestRender
{
    private static readonly Guid SurfacesTex = new("4e560011-0000-4000-8000-0000000000c2");

    /// <summary>
    /// --tr-surfaces=&lt;texture&gt; --tr-surfaces-n=&lt;objects&gt;: many small still boxes in one merge cell, every one with its own texture mapping, so the cell has
    /// more surfaces than the engine allows one mesh (256). The official viewer has no such limit on what it draws, so every face must be drawn: the engine
    /// must hold as many merged surfaces as the cell built, and report no error.
    /// </summary>
    private async Task<int> SurfacesRun(string fixture, int objects)
    {
        var rig = MakeRig();
        var log = new CoreLog(new DiscardSink(), "test-render");
        var sim = new SimRef(256000UL << 32 | 256000UL, "Home");
        var info = new RegionInfo("Home", 1000, 1000, 256, 256, true);
        var world = new WorldState(log);
        world.BeginRegion(sim, info);
        foreach (var (x, y, h) in Ground(0).Patches()) world.ApplyPatch(sim, x, y, 16, h);
        int side = (int)Math.Ceiling(Math.Sqrt(objects));
        for (int i = 0; i < objects; i++)
        {
            float x = 40 + (i % side) * 0.5f, y = 40 + (i / side) * 0.5f;
            // each box maps its texture a little differently: its own merged surface
            var tex = new FaceTexture(SurfacesTex, 1f + i * 0.01f, 1f, 0, 0, 0, TexGen.Default, false, 0);
            world.ApplyObject(sim, Box((uint)(100 + i), 0, 0, new(x, y, (float)GroundHeight(x, y) + 2), System.Numerics.Quaternion.Identity, 0.3f, new Rgba(1, 1, 1, 1)) with { Textures = new FaceTextures(tex) });
        }
        string dir = Path.GetDirectoryName(fixture)!;
        var textures = TextureSourceFor(dir, new Dictionary<Guid, string> { [SurfacesTex] = Path.GetFileName(fixture) });
        long engineErrors0 = Main.EngineLog?.Errors ?? 0;
        var prims = new PrimLayer(rig.Scenario, world, null, textures, _budget) { SimulatedFps = 60 };
        var view = MakeView("server", rig);
        var presenter = new WorldPresenter(world, view, prims: prims, terrainTextures: null,
            environment: new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun)) { DayFractionOverride = 0.3 };
        rig.Camera.LookAtFromPosition(new Vector3(44, 30, -20), new Vector3(44, 2, -44), Vector3.Up);
        prims.Camera = rig.Camera.GlobalPosition;
        prims.ResidencyView = PrimLayer.ViewOf(rig.Camera, Width, Height);
        presenter.Flush();
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (wait.Elapsed.TotalSeconds < 90 && !(prims.MergedObjects >= objects && !prims.MergeBusy))
        {
            presenter.Frame();
            prims.UpdateBlendOrder();
            await Frames(1);
        }
        await Frames(10);
        long errors = (Main.EngineLog?.Errors ?? 0) - engineErrors0;
        _result["surfaces"] = new { objects, merged_objects = prims.MergedObjects, merged_surfaces_built = prims.MergedSurfaces, merged_surfaces_drawn = prims.MergedSurfacesDrawn, merged_meshes = prims.MergedMeshes, engine_errors = errors };
        if (prims.MergedObjects < objects) _failures.Add($"only {prims.MergedObjects} of {objects} objects were merged");
        if (prims.MergedSurfaces <= 256) _failures.Add($"the cell built {prims.MergedSurfaces} surfaces: not past the engine's limit, the case proves nothing");
        if (prims.MergedSurfacesDrawn != prims.MergedSurfaces) _failures.Add($"the cell built {prims.MergedSurfaces} surfaces and the engine holds {prims.MergedSurfacesDrawn}: faces are not drawn");
        if (errors != 0) _failures.Add($"{errors} engine errors");
        presenter.Dispose();
        view.Clear();
        textures.Dispose();
        world.Dispose();
        await Frames(2);
        return _failures.Count == 0 ? 0 : 1;
    }
}

public partial class TestRender
{
    /// <summary>
    /// --tr-primlod: cylinders of one size at four distances are each built at the detail the official viewer's rule gives their distance (calcLOD, llvovolume.cpp
    /// 1455-1560, with the volume's scale bias for the radius), not all at one fixed detail: the layer reports the level each was built at and the engine holds a mesh
    /// for each level.
    /// </summary>
    private async Task<int> PrimLodRun()
    {
        var rig = MakeRig();
        var log = new CoreLog(new DiscardSink(), "test-render");
        var sim = new SimRef(256000UL << 32 | 256000UL, "Home");
        var info = new RegionInfo("Home", 1000, 1000, 256, 256, true);
        var world = new WorldState(log);
        world.BeginRegion(sim, info);
        foreach (var (x, y, h) in Ground(0).Patches()) world.ApplyPatch(sim, x, y, 16, h);
        var cylinder = new Viewer.Core.Meshing.PrimShape { ProfileCurve = 0, PathCurve = 16 };
        float[] ys = [44f, 65f, 100f, 240f];   // 4, 25, 60 and 200 m from a camera at y = 40
        var scale = new System.Numerics.Vector3(1.5f, 1.5f, 1.5f);
        for (int i = 0; i < ys.Length; i++)
            world.ApplyObject(sim, Box((uint)(100 + i), 0, 0, new(100f, ys[i], (float)GroundHeight(100f, ys[i]) + 2), System.Numerics.Quaternion.Identity, 1.5f, new Rgba(1, 1, 1, 1)) with { Shape = cylinder });
        var prims = new PrimLayer(rig.Scenario, world, null, null, _budget) { SimulatedFps = 60 };
        var view = MakeView("server", rig);
        var presenter = new WorldPresenter(world, view, prims: prims, terrainTextures: null,
            environment: new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun)) { DayFractionOverride = 0.3 };
        float eyeZ = (float)GroundHeight(100f, 40f) + 2;
        rig.Camera.LookAtFromPosition(new Vector3(100, eyeZ, -40), new Vector3(100, eyeZ, -240), Vector3.Up);
        prims.SetCamera(rig.Camera);
        presenter.Flush();
        for (int i = 0; i < 30; i++)
        {
            prims.SetCamera(rig.Camera);
            presenter.Frame();
            prims.UpdateLods();
            await Frames(1);
        }
        var drawn = prims.PrimObjectLods;
        var expected = new int[ys.Length];
        for (int i = 0; i < ys.Length; i++)
        {
            float distance = rig.Camera.GlobalPosition.DistanceTo(new Vector3(100f, (float)GroundHeight(100f, ys[i]) + 2, -ys[i]));
            expected[i] = System.Math.Clamp(Viewer.Core.Meshing.MeshLodRule.Detail(distance, Viewer.Core.Meshing.PrimLodGroup.Radius(cylinder, scale), prims.LodFactor, prims.LodFovY), 0, 3);
        }
        _result["primlod"] = new { expected, drawn = Enumerable.Range(0, ys.Length).Select(i => drawn.TryGetValue((uint)(100 + i), out var l) ? l : -1).ToArray(), meshes = prims.Meshes };
        if (expected.Distinct().Count() < 3) _failures.Add("the distances do not reach three levels: the case proves nothing");
        for (int i = 0; i < ys.Length; i++)
            if (!drawn.TryGetValue((uint)(100 + i), out var l) || l != expected[i]) _failures.Add($"the cylinder at {ys[i] - 40} m was built at level {(drawn.TryGetValue((uint)(100 + i), out var got) ? got : -1)}, the rule says {expected[i]}");
        if (prims.Meshes < expected.Distinct().Count()) _failures.Add($"{prims.Meshes} meshes for {expected.Distinct().Count()} levels");
        presenter.Dispose();
        view.Clear();
        world.Dispose();
        await Frames(2);
        return _failures.Count == 0 ? 0 : 1;
    }
}
