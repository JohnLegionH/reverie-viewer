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
    private static readonly Guid TeardownTex = new("4e560011-0000-4000-8000-0000000000c1");

    /// <summary>
    /// --tr-teardown=&lt;fixture&gt;: two regions drawing textured boxes, some opaque and still (drawn merged) and some blended (drawn as
    /// separate face instances), are torn down in the order the viewer's logout uses: the neighbour first, then the home presenter, its
    /// terrain view, and the texture source. The engine reports a face instance or a merged cell freed after the texture it draws with as an
    /// error; the case fails if any error was reported, or if the scene never reached merged and blended drawing.
    /// </summary>
    private async Task<int> TeardownRun(string fixture, int extra)
    {
        var rig = MakeRig();
        var log = new CoreLog(new DiscardSink(), "test-render");
        var homeSim = new SimRef(256000UL << 32 | 256000UL, "Home");
        var eastSim = new SimRef(256256UL << 32 | 256000UL, "East");
        var homeInfo = new RegionInfo("Home", 1000, 1000, 256, 256, true);
        var eastInfo = new RegionInfo("East", 1001, 1000, 256, 256, true);
        var home = new WorldState(log);
        home.BeginRegion(homeSim, homeInfo);
        foreach (var (x, y, h) in Ground(0).Patches()) home.ApplyPatch(homeSim, x, y, 16, h);
        var east = new WorldState(log);
        east.SetOriginOffset(new System.Numerics.Vector3(256, 0, 0));
        east.BeginRegion(eastSim, eastInfo);
        foreach (var (x, y, h) in Ground(256).Patches()) east.ApplyPatch(eastSim, x, y, 16, h);

        PrimData Textured(uint local, System.Numerics.Vector3 at, float alpha) =>
            Box(local, 0, 0, at, System.Numerics.Quaternion.Identity, 1.5f, new Rgba(1, 1, 1, alpha)) with { Textures = new FaceTextures(FaceTexture.Plain(TeardownTex)) };
        // a block of small still boxes (they merge) and a row of translucent ones (they blend), in each region
        void Fill(WorldState w, SimRef sim, float originX)
        {
            for (uint i = 0; i < 40; i++)
            {
                float x = 40 + (i % 8) * 3, y = 40 + (i / 8) * 3;
                w.ApplyObject(sim, Textured(100 + i, new(x, y, (float)GroundHeight(originX + x, y) + 3), 1f));
            }
            for (uint i = 0; i < 10; i++)
            {
                float x = 40 + i * 3, y = 100;
                w.ApplyObject(sim, Textured(200 + i, new(x, y, (float)GroundHeight(originX + x, y) + 3), 0.5f));
            }
            // many more small opaque boxes on a 2 m grid (the region of a busy place)
            int side = (int)Math.Ceiling(Math.Sqrt(Math.Max(extra, 1)));
            for (int i = 0; i < extra; i++)
            {
                float x = 2 + ((i % side) * 2f) % 250, y = 130 + ((i / side) * 2f) % 120;
                w.ApplyObject(sim, Textured((uint)(1000 + i), new(x, y, (float)GroundHeight(originX + x, y) + 2), 1f));
            }
        }
        Fill(home, homeSim, 0);
        Fill(east, eastSim, 256);

        string dir = Path.GetDirectoryName(fixture)!;
        var textures = TextureSourceFor(dir, new Dictionary<Guid, string> { [TeardownTex] = Path.GetFileName(fixture) });
        long engineErrors0 = Main.EngineLog?.Errors ?? 0;

        var homePrims = new PrimLayer(rig.Scenario, home, null, textures, _budget) { SimulatedFps = 60 };
        var eastRw = new RegionWorld(eastSim, eastInfo, east, false, new System.Numerics.Vector3(256, 0, 0), east.OriginOffset);
        var homeView = MakeView("server", rig);
        var homePresenter = new WorldPresenter(home, homeView, prims: homePrims, terrainTextures: null,
            environment: new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun)) { DayFractionOverride = 0.3 };
        var stack = new NeighbourStack(rig.Scenario, rig.Root, eastRw, Guid.Empty, null, null, textures, null, null, homePrims.Hub, 1f, (1000, 1000));
        stack.Prims.SimulatedFps = 60;
        rig.Camera.LookAtFromPosition(new Vector3(100, 40, -20), new Vector3(100, 5, -80), Vector3.Up);
        homePrims.Camera = rig.Camera.GlobalPosition;
        homePrims.ResidencyView = PrimLayer.ViewOf(rig.Camera, Width, Height);
        homePresenter.Flush();
        stack.Presenter.Flush();

        // wait for the result: both layers draw merged cells and blended face instances
        bool Ready() => homePrims.MergedObjects > 0 && homePrims.BlendFaceInstances > 0 && stack.Prims.MergedObjects > 0 && stack.Prims.BlendFaceInstances > 0;
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (wait.Elapsed.TotalSeconds < 60 + extra / 100.0 && !(Ready() && stack.Prims.Instances >= extra && homePrims.Instances >= extra))
        {
            homePresenter.Frame();
            homePrims.UpdateBlendOrder();
            stack.Prims.UpdateBlendOrder();
            stack.Presenter.Frame();
            await Frames(1);
        }
        _result["teardown"] = new
        {
            home_merged = homePrims.MergedObjects, home_blend_faces = homePrims.BlendFaceInstances,
            east_merged = stack.Prims.MergedObjects, east_blend_faces = stack.Prims.BlendFaceInstances,
        };
        if (!Ready()) _failures.Add("the scene never reached merged and blended drawing in both regions");

        // Godot frees a texture only after everything that draws with it: the layers' instances (prim, blended face and merged cell) go first
        // the home region frees its instances in one call (the logout's way); the dropped neighbour a slice a frame, as when it is detached mid-session
        var sync = System.Diagnostics.Stopwatch.StartNew();
        homePrims.FreeInstances();
        double syncMs = sync.Elapsed.TotalMilliseconds;
        int east0 = stack.Prims.Instances;
        stack.BeginRetire();
        var frames = new List<double>();
        long retireStart = System.Diagnostics.Stopwatch.GetTimestamp();
        double Now() => (System.Diagnostics.Stopwatch.GetTimestamp() - retireStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        for (int i = 0; i < 5000; i++)
        {
            long a = System.Diagnostics.Stopwatch.GetTimestamp();
            bool done = stack.RetireStep(2.0, Now);
            await Frames(1);
            frames.Add((System.Diagnostics.Stopwatch.GetTimestamp() - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            if (done) break;
        }
        _result["retire"] = new { objects = east0, sync_free_ms = Math.Round(syncMs, 1), frames = frames.Count, longest_frame_ms = Math.Round(frames.Count == 0 ? 0 : frames.Max(), 1), total_ms = Math.Round(frames.Sum(), 1) };
        if (!stack.RetireStep(0, Now) || stack.Retiring) _failures.Add("the dropped region was not let go of");
        if (frames.Count > 0 && frames.Max() > 100) _failures.Add($"a frame took {frames.Max():F1} ms while the dropped region was let go of (limit 100 ms)");
        if (extra >= 5000 && frames.Count < 3) _failures.Add($"{east0} objects were let go of in {frames.Count} frames: not spread");
        foreach (var (name, layer) in new[] { ("neighbour", stack.Prims), ("home", homePrims) })   // (what TakeInstances handed over is forgotten by the layer)
            if (layer.BlendFaceInstances != 0 || layer.MergedCellInstances != 0 || layer.Instances != 0)
                _failures.Add($"{name}: after FreeInstances {layer.Instances} prim, {layer.BlendFaceInstances} blended face and {layer.MergedCellInstances} merged cell instances are still alive");
        // the logout's order: the neighbour, the home presenter, its view, then the texture source
        stack.Dispose();
        homePresenter.Dispose();
        homeView.Clear();
        textures.Dispose();
        home.Dispose();
        east.Dispose();
        await Frames(2);   // the engine reports a freed texture still bound when it next draws or frees
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        long engineErrors = (Main.EngineLog?.Errors ?? 0) - engineErrors0;
        _result["engine_errors"] = engineErrors;
        if (engineErrors != 0) _failures.Add($"{engineErrors} engine errors while the world was torn down (the log names them)");
        return _failures.Count == 0 ? 0 : 1;
    }
}
