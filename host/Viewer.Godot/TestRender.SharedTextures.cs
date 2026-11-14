// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Core.Residency;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class TestRender
{
    private static readonly Guid SharedTexA = new("4e560010-0000-4000-8000-0000000000a1");
    private static readonly Guid SharedTexB = new("4e560010-0000-4000-8000-0000000000b2");

    /// <summary>
    /// --tr-sharedtex=shared|private: the home region and its east neighbour both draw a box with the same texture, and the east region also draws a
    /// box with a texture of its own. Asserted: the shared texture is fetched once, held in one GPU slot and one residency entry that both
    /// regions use; the east region's own texture is a second entry; when the east region leaves, the shared texture stays resident and the
    /// east region's own texture goes. "private" gives the east region a texture hub of its own (the way it was before): the same assertions
    /// must fail, which is the case's negative control. The image is the fixture given by --tr-fixture (a codestream file).
    /// </summary>
    private async Task<int> SharedTexturesRun(bool shared, string fixture)
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

        // the same local id (100) in both regions: the residency must tell them apart
        PrimData Textured(uint local, System.Numerics.Vector3 at, Guid tex) =>
            Box(local, 0, 0, at, System.Numerics.Quaternion.Identity, 10, new Rgba(1, 1, 1, 1)) with { Textures = new FaceTextures(FaceTexture.Plain(tex)) };
        home.ApplyObject(homeSim, Textured(100, new(236, 60, (float)GroundHeight(236, 60) + 5), SharedTexA));
        east.ApplyObject(eastSim, Textured(100, new(20, 60, (float)GroundHeight(276, 60) + 5), SharedTexA));
        east.ApplyObject(eastSim, Textured(101, new(20, 100, (float)GroundHeight(276, 100) + 5), SharedTexB));

        string dir = Path.GetDirectoryName(fixture)!;
        var images = new Dictionary<Guid, string> { [SharedTexA] = Path.GetFileName(fixture), [SharedTexB] = Path.GetFileName(fixture) };
        s_textureFetches.Clear();
        var textures = TextureSourceFor(dir, images);

        var homePrims = new PrimLayer(rig.Scenario, home, null, textures, _budget) { SimulatedFps = 60 };
        var homeRw = new RegionWorld(homeSim, homeInfo, home, true, System.Numerics.Vector3.Zero, home.OriginOffset);
        var eastRw = new RegionWorld(eastSim, eastInfo, east, false, new System.Numerics.Vector3(256, 0, 0), east.OriginOffset);
        var homeView = MakeView("server", rig);
        var homePresenter = new WorldPresenter(home, homeView, prims: homePrims, terrainTextures: null,
            environment: new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun)) { DayFractionOverride = 0.3 };
        var stack = new NeighbourStack(rig.Scenario, rig.Root, eastRw, Guid.Empty, null, null, textures, null, null, shared ? homePrims.Hub : null, 1f, (1000, 1000));
        stack.Prims.SimulatedFps = 60;
        rig.Camera.LookAtFromPosition(new Vector3(150, 70, -100), new Vector3(300, 25, -100), Vector3.Up);
        homePrims.Camera = rig.Camera.GlobalPosition;
        homePrims.ResidencyView = PrimLayer.ViewOf(rig.Camera, Width, Height);
        homePresenter.Flush();
        stack.Presenter.Flush();

        // wait for the result, not the clock: until both regions show the shared texture and the east region its own
        bool Settled() => homePrims.HasGpuTexture(SharedTexA) && stack.Prims.HasGpuTexture(SharedTexA) && stack.Prims.HasGpuTexture(SharedTexB);
        for (int i = 0; i < 600 && !Settled(); i++)
        {
            homePresenter.Frame();
            stack.Presenter.Frame();
            await Frames(1);
        }
        for (int i = 0; i < 20; i++) { homePresenter.Frame(); stack.Presenter.Frame(); await Frames(1); }

        var hub = homePrims.Hub;
        int slotsBefore = homePrims.Hub.SlotCount + (shared ? 0 : stack.Prims.Hub.SlotCount);
        int fetchesA = s_textureFetches.GetValueOrDefault(SharedTexA);
        int entries = hub.Residency?.Textures ?? -1;
        int spaces = hub.Residency is { } rr ? rr.All.Count() : -1;
        _result["sharedtex"] = new { shared, fetches_a = fetchesA, slots = slotsBefore, residency_entries = entries, residency_users = hub.Residency?.Users ?? -1, spaces };
        if (!Settled()) _failures.Add("the textures did not all arrive");
        if (fetchesA != 1) _failures.Add($"the shared texture was fetched {fetchesA} times, not once");
        if (slotsBefore != 2) _failures.Add($"{slotsBefore} GPU textures held for 2 textures (one is used by both regions)");
        if (entries != 2) _failures.Add($"{entries} residency entries for 2 textures");

        // the east region leaves: the shared texture stays, the east region's own texture goes
        long residentBefore = hub.Residency?.ResidentBytes ?? 0;
        stack.Dispose();
        long residentAfter = hub.Residency?.ResidentBytes ?? 0;
        bool sharedStays = hub.Residency?.StateOf(SharedTexA) == ResidencyState.Resident;
        bool ownGone = hub.Residency?.StateOf(SharedTexB) != ResidencyState.Resident;
        _result["sharedtex_leave"] = new { resident_before = residentBefore, resident_after = residentAfter, shared_stays = sharedStays, own_gone = ownGone };
        if (!sharedStays) _failures.Add("the shared texture did not stay resident when the east region left");
        if (!ownGone) _failures.Add("the east region's own texture stayed resident after the region left");
        if (residentAfter >= residentBefore) _failures.Add("no memory was freed when the east region left");

        homePresenter.Dispose();
        homeView.Clear();
        home.Dispose();
        east.Dispose();
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        return _failures.Count == 0 ? 0 : 1;
    }
}
