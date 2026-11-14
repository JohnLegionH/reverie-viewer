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
    /// <summary>
    /// --tr-sharedmeasure=N: the home region and its east neighbour each draw N boxes, and box k of each region has the same texture k (so N textures are
    /// used by both regions). Reported once every texture is on the GPU in both regions: the texture memory the engine reports, what the residency
    /// manager(s) hold, the GPU textures and residency entries, the managed bytes allocated while the two regions loaded, and the GPU time of the view
    /// over 120 frames. The image is the fixture given by --tr-fixture (a codestream file). A measurement, not a check: it fails only when a texture did not arrive.
    /// </summary>
    private async Task<int> SharedMeasureRun(string fixture, int count)
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
        var ids = Enumerable.Range(0, count).Select(k => new Guid(0x4e560020 + k, 0, 0x4000, [0x80, 0, 0, 0, 0, 0, 0, (byte)k])).ToArray();
        PrimData Textured(uint local, System.Numerics.Vector3 at, Guid tex) =>
            Box(local, 0, 0, at, System.Numerics.Quaternion.Identity, 10, new Rgba(1, 1, 1, 1)) with { Textures = new FaceTextures(FaceTexture.Plain(tex)) };
        for (int k = 0; k < count; k++)
        {
            float y = 50 + k * (160f / Math.Max(1, count - 1));
            home.ApplyObject(homeSim, Textured((uint)(100 + k), new(236, y, (float)GroundHeight(236, y) + 5), ids[k]));
            east.ApplyObject(eastSim, Textured((uint)(100 + k), new(20, y, (float)GroundHeight(276, y) + 5), ids[k]));
        }
        string dir = Path.GetDirectoryName(fixture)!;
        var images = ids.ToDictionary(i => i, _ => Path.GetFileName(fixture));
        s_textureFetches.Clear();
        var textures = TextureSourceFor(dir, images);
        long alloc0 = GC.GetTotalAllocatedBytes(false);
        var homePrims = new PrimLayer(rig.Scenario, home, null, textures, _budget) { SimulatedFps = 60 };
        var homeRw = new RegionWorld(homeSim, homeInfo, home, true, System.Numerics.Vector3.Zero, home.OriginOffset);
        var eastRw = new RegionWorld(eastSim, eastInfo, east, false, new System.Numerics.Vector3(256, 0, 0), east.OriginOffset);
        var homeView = MakeView("server", rig);
        var homePresenter = new WorldPresenter(home, homeView, prims: homePrims, terrainTextures: null,
            environment: new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun)) { DayFractionOverride = 0.3 };
        var stack = new NeighbourStack(rig.Scenario, rig.Root, eastRw, Guid.Empty, null, null, textures, null, null, homePrims.Hub, 1f, (1000, 1000));
        stack.Prims.SimulatedFps = 60;
        rig.Camera.LookAtFromPosition(new Vector3(150, 70, -128), new Vector3(300, 25, -128), Vector3.Up);
        homePrims.Camera = rig.Camera.GlobalPosition;
        homePrims.ResidencyView = PrimLayer.ViewOf(rig.Camera, Width, Height);
        homePresenter.Flush();
        stack.Presenter.Flush();
        bool Settled() => ids.All(id => homePrims.HasGpuTexture(id) && stack.Prims.HasGpuTexture(id));
        int frames = 0;
        // an object update (here: a tiny size change of the east region's boxes every 20 frames, in both builds) re-presents the object, as live traffic does
        for (; frames < 1500 && !Settled(); frames++)
        {
            if (frames % 20 == 10)
                for (int k = 0; k < count; k++)
                {
                    float y = 50 + k * (160f / Math.Max(1, count - 1));
                    east.ApplyObject(eastSim, Textured((uint)(100 + k), new(20, y, (float)GroundHeight(276, y) + 5), ids[k]) with { Scale = new System.Numerics.Vector3(10 + (frames / 20 % 2) * 0.01f, 10, 10) });
                }
            homePresenter.Frame(); stack.Presenter.Frame(); await Frames(1);
        }
        for (int i = 0; i < 30; i++) { homePresenter.Frame(); stack.Presenter.Frame(); await Frames(1); }
        long allocated = GC.GetTotalAllocatedBytes(false) - alloc0;
        var gpuMs = new List<double>();
        var vp = rig.Viewport.GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(vp, true);
        await Frames(2);
        for (int i = 0; i < 120; i++)
        {
            homePresenter.Frame();
            stack.Presenter.Frame();
            await Frames(1);
            gpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp));
        }
        long resident = (homePrims.Residency?.ResidentBytes ?? 0);
        _result["sharedmeasure"] = new
        {
            textures = count, settled = Settled(), frames_to_settle = frames,
            engine_texture_bytes = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed),
            resident_bytes = resident, gpu_textures = homePrims.Hub.SlotCount, residency_entries = (homePrims.Residency?.Textures ?? 0),
            fetches = s_textureFetches.Values.Sum(), allocated_bytes = allocated, gpu_ms = Dist(gpuMs),
        };
        if (!Settled()) _failures.Add("the textures did not all arrive");
        stack.Dispose();
        homePresenter.Dispose();
        homeView.Clear();
        home.Dispose();
        east.Dispose();
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        return _failures.Count == 0 ? 0 : 1;
    }
}
