// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Core.Meshing;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;
using Viewer.Shell.Travel;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// --tr-neighbours=on|off: two regions side by side, drawn as the viewer draws a region and its eastern neighbour. The terrain is one
    /// surface across the border (the east region's heights continue the home region's), and each region has a row of boxes near the border
    /// (red in the home region, blue in the east one) that line up across it. The camera stands in the home region and looks east over the
    /// border. The picture is compared with --tr-golden (the usual terrain tolerance). With "off" only the home region is presented: the
    /// negative control, which must fail the same diff because the east half of the picture is empty.
    /// </summary>
    private async Task<int> NeighboursRun(bool neighbours, string? golden, string? water = null, bool waterPlan = true, string? glow = null, string? ban = null, string? seam = null)
    {
        // --tr-seam=border|slope: the ground alone (no boxes), seen from a low angle, and the picture is checked for sky showing through the
        // land: in no column of the picture may a sky-coloured pixel lie below the first ground pixel. "border" looks along the line where
        // the two regions meet; "slope" looks across the lines where one region's 64 m terrain chunks meet, on a rise.
        // --tr-ban=east: a parcel in the east region that keeps the agent out by its access list (64 m by 64 m from the border, the 50 m wall) shows its wall, with the agent standing in the home region 4 m short of the border;
        // --tr-ban=east-at-anchor: the wall built in the east region's coordinates but placed in the home region's frame, the way it was before (the control)
        // --tr-glow=east: one box of the east region glows (face glow 0.8); --tr-glow=east-unlinked: the same, but the glow pass is not told of the east region (the control)
        // --tr-water=<home>,<east>: both regions have water, at those heights (m). --tr-water-plan=off: the water is one plane at the home height (the control).
        float[]? heights = water?.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var rig = MakeRig();
        var log = new CoreLog(new DiscardSink(), "test-render");
        var homeSim = new SimRef(256000UL << 32 | 256000UL, "Home");
        var eastSim = new SimRef(256256UL << 32 | 256000UL, "East");
        var homeInfo = new RegionInfo("Home", 1000, 1000, 256, 256, true) { WaterHeight = heights?[0] };
        var eastInfo = new RegionInfo("East", 1001, 1000, 256, 256, true) { WaterHeight = heights?[1] };

        var home = new WorldState(log);
        home.BeginRegion(homeSim, homeInfo);
        foreach (var (x, y, h) in Ground(0).Patches())
            home.ApplyPatch(homeSim, x, y, 16, h);
        var east = new WorldState(log);
        east.SetOriginOffset(new System.Numerics.Vector3(256, 0, 0));
        east.BeginRegion(eastSim, eastInfo);
        foreach (var (x, y, h) in Ground(256).Patches())
            east.ApplyPatch(eastSim, x, y, 16, h);
        var red = new Rgba(0.85f, 0.15f, 0.12f, 1f);
        var blue = new Rgba(0.15f, 0.3f, 0.9f, 1f);
        for (uint i = 0; i < 4 && seam is null; i++)
        {
            // heights are the ground's plus half the box, so the boxes stand on it
            float yPos = 40 + i * 50;
            home.ApplyObject(homeSim, Box(100 + i, 0, 0, new System.Numerics.Vector3(236, yPos, (float)GroundHeight(236, yPos) + 5), System.Numerics.Quaternion.Identity, 10, red));
            east.ApplyObject(eastSim, Box(200 + i, 0, 0, new System.Numerics.Vector3(20, yPos, (float)GroundHeight(276, yPos) + 5), System.Numerics.Quaternion.Identity, 10, blue));
        }

        if (glow is not null)
            east.ApplyObject(eastSim, Box(200, 0, 0, new System.Numerics.Vector3(20, 40, (float)GroundHeight(276, 40) + 5), System.Numerics.Quaternion.Identity, 10, blue)
                with { Textures = new FaceTextures(new FaceTexture(Guid.Empty, 1, 1, 0, 0, 0, TexGen.Default, true, 0.8f)) });
        var homeView = MakeView("server", rig);
        // a face counts as glowing only in a layer that has a texture source (as in the viewer); this one serves nothing, so glowing faces stay on their placeholder
        var noTextures = glow is null ? null : TextureSourceFor(_out, new Dictionary<Guid, string>());
        var homePrims = new PrimLayer(rig.Scenario, home, null, noTextures, _budget) { SimulatedFps = 60 };
        homePrims.Camera = rig.Camera.GlobalPosition;
        // the ground outside each region comes from the other one (when it is presented), so the border meets in one surface
        var homeRw = new RegionWorld(homeSim, homeInfo, home, true, System.Numerics.Vector3.Zero, home.OriginOffset);
        var eastRw = new RegionWorld(eastSim, eastInfo, east, false, new System.Numerics.Vector3(256, 0, 0), east.OriginOffset);
        var presented = neighbours ? new[] { homeRw, eastRw } : new[] { homeRw };
        var homePresenter = new WorldPresenter(home, homeView, prims: homePrims,
            environment: new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun))
        { DayFractionOverride = 0.3, OutsideHeight = (gx, gy) => OuterTerrain.HeightAt(presented, homeRw, gx, gy) };
        if (neighbours && waterPlan && heights is not null)
            homePresenter.NeighbourWater = [new Viewer.Core.World.WaterRect(256, 0, 512, 256, heights[1])];
        NeighbourStack? stack = null;
        if (neighbours)
        {
            var info = new RegionWorld(eastSim, eastInfo, east, false, new System.Numerics.Vector3(256, 0, 0), east.OriginOffset);
            stack = new NeighbourStack(rig.Scenario, rig.Root, info, Guid.Empty, null, null, noTextures, null, null, noTextures is null ? null : homePrims.Hub, 1f, (1000, 1000), (gx, gy) => OuterTerrain.HeightAt(presented, eastRw, gx, gy));
        }
        // the camera: in the home region, 90 m short of the border, looking east and a little down
        if (seam == "border") rig.Camera.LookAtFromPosition(new Vector3(244, (float)GroundHeight(244, 8) + 3f, -8), new Vector3(257, (float)GroundHeight(257, 120) + 1f, -120), Vector3.Up);   // 12 m west of the border, along it
        else if (seam == "slope") rig.Camera.LookAtFromPosition(new Vector3(30, (float)GroundHeight(30, 100) + 3f, -100), new Vector3(200, (float)GroundHeight(200, 110) + 1f, -110), Vector3.Up);   // across the chunk lines at x = 64 and 128
        else if (ban is not null) rig.Camera.LookAtFromPosition(new Vector3(228, 36, -92), new Vector3(262, 34, -97), Vector3.Up);   // close to the wall, which fades with the distance from the agent
        else rig.Camera.LookAtFromPosition(new Vector3(150, 70, -128), new Vector3(300, 25, -128), Vector3.Up);
        homePrims.Camera = rig.Camera.GlobalPosition;
        homePrims.ResidencyView = PrimLayer.ViewOf(rig.Camera, Width, Height);
        homePresenter.Flush();
        stack?.Presenter.Flush();
        BanWallLayer? wall = null;
        if (ban is not null)
        {
            var bitmap = new byte[BanWall.CellsPerEdge * BanWall.CellsPerEdge / 8];
            for (int y = 16; y < 32; y++)   // cells 4 m wide: x 0..64 m, y 64..128 m of the east region
                for (int x = 0; x < 16; x++)
                {
                    int bit = x + y * BanWall.CellsPerEdge;
                    bitmap[bit / 8] |= (byte)(1 << (bit % 8));
                }
            var state = new Viewer.Shell.Travel.BanLineState { Mode = Viewer.Shell.Travel.BanLineMode.OnProximity };
            state.Collision(new ParcelCollision(ParcelDenial.NotOnList, bitmap, false, eastSim), 0);
            wall = new BanWallLayer();
            rig.Root.AddChild(wall);
            var offset = ban == "east-at-anchor" ? default : new System.Numerics.Vector2(256, 0);
            wall.RegionOffset = offset;
            wall.Update(state, new System.Numerics.Vector2(252, 96) - offset, (x, y) => TerrainHeight(east, x, y), 0.5);
            _result["ban"] = new { ban, pieces = wall.Pieces, shown = wall.Shown, offset = new[] { offset.X, offset.Y } };
            if (!wall.Shown || wall.Pieces == 0) _failures.Add("the ban wall was not drawn");
        }
        GlowPass? glowPass = null;
        if (glow is not null)
        {
            glowPass = new GlowPass(homePrims, rig.World, rig.Environment) { OtherPrims = glow == "east" && stack is not null ? [stack.Prims] : [] };
            rig.Root.AddChild(glowPass);
        }
        await Frames(8);
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        Directory.CreateDirectory(_out);
        img.SavePng(Path.Combine(_out, _tag + ".png"));
        if (seam is not null)
        {
            int sky = SkyUnderGround(img.GetData(), Width, Height);
            _result["seam"] = new { kind = seam, sky_under_ground = sky };
            if (sky > 0)
                _failures.Add($"sky shows through the land: {sky} sky-coloured pixels below the ground ({seam})");
        }

        // frame time of the view, for the comparison of two regions with one (reported, not gated): 120 frames, each from the call to its end
        var frameMs = new List<double>();
        var gpuMs = new List<double>();
        var vpRid = rig.Viewport.GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(vpRid, true);
        await Frames(2);
        for (int i = 0; i < 120; i++)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            homePresenter.Frame();
            stack?.Presenter.Frame();
            await Frames(1);
            gpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(vpRid));   // the GPU's own time for the view, not the paced frame time
            frameMs.Add((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }
        frameMs.Sort();

        _result["neighbours"] = new
        {
            frame_ms = new { p50 = Math.Round(frameMs[frameMs.Count / 2], 2), p95 = Math.Round(frameMs[(int)(frameMs.Count * 0.95)], 2), max = Math.Round(frameMs[^1], 2), frames = frameMs.Count },
            gpu_ms = Dist(gpuMs),
            on = neighbours,
            home_chunks = homeView.ChunkCount,
            east_chunks = stack?.View.ChunkCount ?? 0,
            home_instances = homePrims.Instances,
            east_instances = stack?.Prims.Instances ?? 0,
            east_offset = new[] { stack?.World.OriginOffset.X ?? 0, stack?.World.OriginOffset.Y ?? 0 },
        };
        if (glow is not null)
        {
            await Frames(4);
            _result["glow"] = new { active = glowPass!.Active, east_glow_objects = stack?.Prims.GlowObjects ?? 0, home_glow_objects = homePrims.GlowObjects };
            if (!glowPass.Active) _failures.Add("the glow pass is not drawing: a glowing face in the east region did not light it");
        }
        if (neighbours && seam is null && (stack is null || stack.View.ChunkCount == 0 || stack.Prims.Instances != 4))
            _failures.Add($"the east region was not drawn: {stack?.View.ChunkCount ?? 0} chunks, {stack?.Prims.Instances ?? 0} instances");
        if (golden is not null)
        {
            if (!Path.IsPathFullyQualified(golden) || !File.Exists(golden))
                _failures.Add("golden not found: " + Path.GetFileName(golden));
            else
            {
                var g = Image.LoadFromFile(golden);
                g.Convert(Image.Format.Rgba8);
                var diff = g.GetWidth() != Width || g.GetHeight() != Height
                    ? new DiffResult(false, g.GetWidth(), g.GetHeight(), 0, 0, 0, 0, "golden size differs")
                    : ImageDiff.Compare(img.GetData(), g.GetData(), Width, Height, 4, DiffTolerance.Terrain);
                _result["diff"] = new Dictionary<string, object?>
                {
                    ["golden"] = Path.GetFileName(golden), ["pass"] = diff.Pass, ["differing"] = diff.Differing,
                    ["differing_fraction"] = diff.DifferingFraction, ["mean_abs_error"] = Math.Round(diff.MeanAbsError, 4),
                    ["max_channel_error"] = diff.MaxChannelError, ["tolerance"] = DiffTolerance.Terrain, ["summary"] = diff.ToString(),
                };
                if (!diff.Pass)
                    _failures.Add("diff " + diff);
            }
        }
        stack?.Dispose();
        homePresenter.Dispose();
        homeView.Clear();
        home.Dispose();
        east.Dispose();
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>How many sky-coloured pixels lie below the first ground pixel of their column (RGBA8 data). The sky is blue-dominant; the
    /// ground of these scenes is green and cream.</summary>
    private static int SkyUnderGround(byte[] rgba, int width, int height)
    {
        static bool Sky(byte r, byte g, byte b) => b > r + 40 && b > g + 10;
        int count = 0;
        for (int x = 0; x < width; x++)
        {
            bool ground = false;
            for (int y = 0; y < height; y++)
            {
                int i = (y * width + x) * 4;
                bool sky = Sky(rgba[i], rgba[i + 1], rgba[i + 2]);
                if (!sky) ground = true;
                else if (ground) count++;
            }
        }
        return count;
    }

    /// <summary>The ground the two regions share: one smooth surface, so the border cannot be seen in the terrain itself. <paramref name="originX"/>
    /// is the region's first column in the shared frame (0 for the home region, 256 for the east one).</summary>
    private static TerrainFixture Ground(int originX)
    {
        var h = new float[256 * 256];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                h[y * 256 + x] = (float)Math.Round(GroundHeight(originX + x, y), 2);
        return new TerrainFixture("neighbours-ground", 256, 256, 16, h);
    }

    private static double GroundHeight(float x, float y) =>
        22 + 5 * Math.Sin(x / 41.0) * Math.Cos(y / 33.0) + 3 * Math.Sin((x + 2 * y) / 17.0);
}
