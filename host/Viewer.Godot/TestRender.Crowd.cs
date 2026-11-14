// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.Meshing;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;
using GVector3 = Godot.Vector3;

namespace Viewer.Godot;

/// <summary>
/// A crowd of avatars in synthetic outfits (--tr-avatars=crowd:N, with --tr-character=&lt;character folder&gt; and --tr-assets= for the walk): N avatars
/// on a grid, a mix of standing and walking in place, each wearing one of the synthetic outfits of <see cref="OutfitGenerator"/> (20 rigged
/// meshes, each with its own joint list and binds; --tr-crowd-meshes= changes the count). The avatars come in as region updates and are drawn
/// through the viewer's own path (world state, avatar presenter, prim layer with worn rigged meshes skinned in the face shader, one bone
/// texture row an avatar, the layer's frame budget), all at their highest level of detail. Once the scene has settled it records the frame
/// figures the crowd measurement of bare avatars records, plus per frame the number of avatars drawn, rigged meshes held and draw calls.
/// --tr-crowd-outfits=K gives the crowd K different outfits, dealt out in turn (default the smaller of N and 50: the layer's bind-table atlas
/// holds 1024 tables and an outfit takes one per mesh); --tr-crowd-seconds= is the recorded time (default 8); --tr-size=, --tr-seed= as the other crowd runs.
/// <para>
/// --tr-avatars=crowd:N,real makes the crowd realistic (<see cref="CrowdLooks"/>, all from the seed): each avatar arrives with an appearance message
/// (a male or female shape with its sliders varied, and server bakes at the sizes server baking gives each slot, made at run time as JPEG 2000
/// codestreams and fetched, decoded and uploaded through the viewer's own texture path), and about half wear a mesh body whose faces name the
/// bake placeholders (bake on mesh) with alpha bakes that hide the system body where they say. --tr-avatars=arrive:N is the same crowd arriving all
/// within --tr-arrive-seconds (default 1) instead of standing there when the run starts: it records the time until every avatar is finished, the
/// longest main-thread slice and the frame figures while they arrive. --tr-alter=nolook (negative control): the appearances are not sent, so the
/// avatars stay default and the checks must fail.
/// </para>
/// </summary>
public partial class TestRender
{
    private const double MainThreadSliceLimitMs = 4.0;

    private async Task<int> AvatarOutfitCrowd(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? assets, string[] args, int n, bool real = false, bool arrival = false)
    {
        long engineErrors0 = Main.EngineLog?.Errors ?? 0;   // the engine reports what the viewer did wrong (a texture freed while a material still binds it) as errors
        WorldPresenter.NoSharedBudget = Arg(args, "--tr-alter=nobudget") is not null;   // the control: the work as it was before the shared budget
        AvatarLayer.PoseOnWorkers = Arg(args, "--tr-alter=poseonmain") is null;          // the control: poses worked out on the main thread, as before the workers
        SkinTextures.DeviceAtlas = Arg(args, "--tr-alter=imageatlas") is null;           // the control: the bind-table atlas handed to the engine whole, as before
        if (Arg(args, "--tr-no-jit-warmup") is null && !WorldPresenter.NoSharedBudget) JitWarmup.Start();   // the presentation code compiled ahead on a thread of its own, as the viewer does
        double seconds = double.Parse(Arg(args, "--tr-crowd-seconds=") ?? "8", System.Globalization.CultureInfo.InvariantCulture);
        double arriveSeconds = double.Parse(Arg(args, "--tr-arrive-seconds=") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        var size = (Arg(args, "--tr-size=") ?? "1920x1080").Split('x').Select(int.Parse).ToArray();
        int seed = int.Parse(Arg(args, "--tr-seed=") ?? "8");
        int meshCount = int.Parse(Arg(args, "--tr-crowd-meshes=") ?? "20");
        int outfitCount = int.Parse(Arg(args, "--tr-crowd-outfits=") ?? Math.Min(n, 50).ToString());
        bool opaque = Arg(args, "--tr-crowd-alpha=") == "opaque";   // every face opaque, as a comparison with the mix of blended and opaque faces
        // --tr-alter=norigged (negative control): the meshes carry no full skin, as a build that does not skin them would see them; none is drawn and the checks must fail
        bool noSkin = Arg(args, "--tr-alter=") == "norigged";
        // --tr-alter=nolook (negative control): the realistic crowd's appearances are not sent
        bool noLook = real && Arg(args, "--tr-alter=") == "nolook";
        // --tr-alter=legacybakes (negative control for the arrival check): bakes are decoded whole and uploaded on the main thread, as many a frame as are ready
        bool legacyBakes = real && Arg(args, "--tr-alter=") == "legacybakes";
        // --tr-alter=hiddentextures (negative control for the held-once check): bake slots a worn mesh or their own alpha hides still get a texture of the presenter's, as before
        bool hiddenTextures = real && Arg(args, "--tr-alter=") == "hiddentextures";
        // --tr-bake-cache=<folder>: the bake pictures are read from this folder (made and kept there on the first run) instead of being encoded in this process
        string? bakeCache = real ? Arg(args, "--tr-bake-cache=") : null;
        // --tr-bake-sizing=full: every bake at its full size, as before bakes were sized by the avatar's pixel area (the comparison for the memory figures)
        bool fullBakes = real && Arg(args, "--tr-bake-sizing=") == "full";
        // --tr-bake-phase: after the crowd settles, the camera goes up to the farthest avatar and back, and its bakes must follow
        bool bakePhase = real && !arrival && Arg(args, "--tr-bake-phase") is not null;
        float spacing = float.Parse(Arg(args, "--tr-crowd-spacing=") ?? "2.5", System.Globalization.CultureInfo.InvariantCulture);
        int settleCap = noSkin || noLook ? 15 : real ? 300 : 180;
        if (outfitCount < 1 || outfitCount > n) { _failures.Add($"--tr-crowd-outfits={outfitCount} must be from 1 to {n}"); return 1; }
        if (ClipFrom(assets, WalkId) is null) { _failures.Add("the walk animation is not in the asset cache (--tr-assets=)"); return 1; }
        FreeRunning();
        var memory = new MemoryStages(Arg(args, "--tr-mem-collect") is not null);
        memory.Snap("start");
        var warm = await PipelineWarmup.RunAsync(this, Viewport.Msaa.Disabled, () => Frames(1));
        memory.Snap("warmed up");
        _result["warmup"] = new { ms = Math.Round(warm.Ms, 2), pipelines = warm.Pipelines, variants = warm.Variants, frames = warm.Frames };
        // the arrival's own code run once, off screen, as the viewer does at start-up (--tr-no-arrival-warmup: the arrival is the code's first run)
        ArrivalWarmup.Result? arrivalWarm = Arg(args, "--tr-no-arrival-warmup") is null && Arg(args, "--tr-alter=") != "nobudget"
            ? await ArrivalWarmup.RunAsync(this, Viewport.Msaa.Disabled, new AvatarPresenter.LoadedBody(body, surfaces), () => Frames(1)) : null;

        // the outfits: made in parallel (each from its own seed, so the result does not depend on the order), then turned into the rigged meshes the layer reads
        var sw = Stopwatch.StartNew();
        var outfits = new Outfit[outfitCount];
        System.Threading.Tasks.Parallel.For(0, outfitCount, k => outfits[k] = OutfitGenerator.Generate(body.Skeleton, new OutfitOptions(meshCount, seed * 1000 + k)));
        var pieces = new RiggedPiece[outfitCount][];
        for (int k = 0; k < outfitCount; k++) pieces[k] = outfits[k].Meshes.Select((m, i) => OutfitPiece(body.Skeleton, m, k, i, opaque)).ToArray();
        if (noSkin) pieces = pieces.Select(set => set.Select(p => p with { Geometry = p.Geometry with { Skin = p.Geometry.Skin! with { Data = null } } }).ToArray()).ToArray();
        double generateMs = sw.Elapsed.TotalMilliseconds;
        // the generator's own data is not kept: the pieces hold what the viewer's path reads, and only the totals the report gives are needed
        var outfitVertices = outfits.Select(o => (long)o.VertexCount).ToArray();
        var outfitTriangles = outfits.Select(o => (long)o.TriangleCount).ToArray();
        var outfitJoints = outfits.Select(o => (long)o.ListedJoints).ToArray();
        outfits = null!;
        memory.Snap("outfits made");

        // the realistic crowd: every avatar's look from the seed, and the bake pictures (a few variants a slot, encoded in parallel) under each avatar's own ids
        CrowdLook[]? looks = null;
        Dictionary<Guid, byte[]>? bakeBytes = null;
        CrowdBakePool? poolMade = null;
        double picturesMs = 0;
        if (real)
        {
            sw.Restart();
            looks = CrowdLooks.MakeAll(seed, n);
            poolMade = CrowdBakeImages.MakePool(seed, looks, bakeCache, thirtyLayers: Arg(args, "--tr-bake-layers=") == "30", layers: int.TryParse(Arg(args, "--tr-bake-layers="), out var bakeLayers) && bakeLayers != 30 ? bakeLayers : 0);   // 30: the encoder's default layer count, as the pictures were before they carried the grid's five (the control of the arrival figures)
            var pool = poolMade.Pictures;
            bakeBytes = looks.SelectMany(l => l.Bakes).ToDictionary(b => b.Id, b => pool[(b.Slot, b.Variant, b.Hides)]);
            picturesMs = sw.Elapsed.TotalMilliseconds;
            memory.Snap("bake pictures made");
        }

        var rig = MakeRig(new Vector2I(size[0], size[1]));
        var vpRid = rig.Viewport.GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(vpRid, true);
        var scenario = rig.Scenario;
        var centre = new Vector3(128, 128, 20);
        var floor = Floor(scenario, centre, 120f);
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("crowd", 1000, 1000, 256, 256, true));
        using var animations = AnimationSourceFor(assets);
        var clock = Stopwatch.StartNew();
        double Now() => clock.Elapsed.TotalSeconds;
        var view = MakeView("server", rig);
        using var textures = real ? TextureSourceFromBytes(bakeBytes!) : null;
        using var meshes = RiggedMeshes(pieces.SelectMany(p => p));
        var prims = new PrimLayer(scenario, world, meshes, textures, _budget);
        var avatars = new AvatarPresenter(scenario, world, Guid.Empty, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, textures, prims.Residency, Now) { LegacyBakeUploads = legacyBakes, KeepHiddenBakeTextures = hiddenTextures };
        var presenter = new WorldPresenter(world, view, prims: prims, avatars: avatars);

        var slots = CrowdLayout.Place(n, outfitCount, spacing);
        var avatarSize = new Vector3(0.45f, 0.6f, 1.9f);
        uint WearerOf(int i) => (uint)(10_000 + i);
        Guid AgentOf(int i) => new Guid(unchecked((int)0xc20d0000), 0, 0, [0, 0, 0, 0, (byte)(i >> 24), (byte)(i >> 16), (byte)(i >> 8), (byte)i]);
        var upperPlaceholder = BakeChannels.IdOf("upper");
        var lowerPlaceholder = BakeChannels.IdOf("lower");
        void Add(int i)
        {
            uint wearer = WearerOf(i);
            var agent = AgentOf(i);
            world.ApplyAvatar(FixtureSim, new(wearer, agent, $"Crowd {i + 1}", new(centre.X + slots[i].X, centre.Y + slots[i].Y, centre.Z + 0.95f), Quaternion.Identity, avatarSize));
            if (real && !noLook) world.ApplyAppearance(FixtureSim, CrowdLooks.Appearance(agent, looks![i]), Guid.Empty);
            if (slots[i].Walking) world.ApplyAnimations(FixtureSim, agent, [new(WalkId, 2)]);
            for (int m = 0; m < meshCount; m++)
            {
                var prim = WornRigged((uint)(1_000_000 + i * 100 + m), wearer, pieces[slots[i].Outfit][m]);
                // a mesh body: its first mesh names the upper and lower bake placeholders on its faces, shown with the wearer's own bakes (bake on mesh)
                if (real && !noLook && looks![i].MeshBody && m == 0)
                    prim = prim with { Textures = new FaceTextures(FaceTexture.Plain(upperPlaceholder), [null, FaceTexture.Plain(lowerPlaceholder)]), Colors = new FaceColors(new Rgba(1, 1, 1, 1)) };
                world.ApplyObject(FixtureSim, prim, eventIsAttachment: true);
            }
        }
        if (!arrival)
            for (int i = 0; i < n; i++) Add(i);
        int expected = n * meshCount, walking = slots.Count(s => s.Walking);

        // the camera back from the grid's south-east corner and high enough to see every avatar, as the bare-avatar crowd does
        float span = ((int)Math.Ceiling(Math.Sqrt(n)) - 1) * spacing;
        float back = span * 0.9f + 9f;
        var cam = rig.Camera;
        cam.Fov = 60f; cam.Near = 0.2f; cam.Far = 512f;
        var target = new GVector3(centre.X, centre.Z + 1f, -centre.Y);
        cam.Position = target + new GVector3(back * 0.5f, back * 0.45f + 2f, back * 0.75f);
        cam.LookAt(target, GVector3.Up);
        if (real)
        {
            prims.Camera = cam.GlobalPosition;
            prims.ResidencyView = PrimLayer.ViewOf(cam, size[0], size[1]);
            if (!fullBakes) avatars.BakeView = prims.ResidencyView;   // the bakes sized by how big each avatar is on screen
        }

        // settled: every avatar drawn, every worn rigged mesh held and shown, nothing waiting for the layer's steps or the walk; the realistic crowd also
        // has every avatar shaped and morphed, its four body bakes on the body, and no texture or mesh still being waited for
        int Shown() => Figure(prims.Describe(), "rigged_shown") ?? -1;
        bool RealSettled()
        {
            if (!real || noLook) return true;
            var d = prims.Describe();
            return avatars.Shaped == n && avatars.Morphed == n && avatars.BakeSlotsSettled >= 4 * n && avatars.BakesAtWantedLevels && (Figure(d, "textures_waiting") ?? 0) == 0 && (Figure(d, "meshes_waiting") ?? 0) == 0
                && (Figure(d, "rigged_lods_preparing") ?? 0) == 0;
        }
        bool Settled() => avatars.Drawn == n && prims.Instances == expected && prims.Dirty == 0 && animations.Waiting == 0 && animations.InFlight == 0 && animations.Ready >= 1 && Shown() == expected && RealSettled();
        // --tr-warm-first: the viewer's own avatar is already there, finished, when the crowd arrives (the first avatar of a session is made while the
        // region loads, not in a burst); the rest of the crowd then arrives within the arrival time. Without it the first frame of the arrival is the code's first run.
        bool warmFirst = arrival && Arg(args, "--tr-warm-first") is not null;
        int addedBefore = 0;
        if (warmFirst)
        {
            Add(0);
            addedBefore = 1;
            var warmClock = Stopwatch.StartNew();
            while (warmClock.Elapsed < TimeSpan.FromSeconds(30)
                && !(avatars.Drawn >= 1 && avatars.Shaped >= 1 && avatars.Morphed >= 1 && avatars.BakeTextures >= 4 && avatars.BakesAtWantedLevels && prims.Dirty == 0 && Shown() >= meshCount))
            { presenter.Frame(); await Frames(1); }
            for (int i = 0; i < 30; i++) { presenter.Frame(); await Frames(1); }
        }
        var arriveSlice = new List<double>();   // the avatar presenter's frame, main thread, while the crowd settles
        CpuMeter.ThreadCycleMs();   // calibrates the cycle counter now, not inside the first slice
        var arriveSliceCpu = new List<double>();   // the same slices in processor time of the main thread (a busy machine moves wall-clock time, not this)
        var arriveIngest = new List<double>();  // the region updates handed to the world in the same frame
        var arriveFrame = new List<double>();   // the time between frames
        var arriveCpu = new List<double>(); var arriveGpu = new List<double>(); var arriveMarked = new List<bool>();   // the engine's cost of each of those frames, and which the window was not drawing in
        var gcPause0 = GC.GetTotalPauseDuration(); int gcGen2At0 = GC.CollectionCount(2);
        int gcGen0At0 = GC.CollectionCount(0), gcGen1At0 = GC.CollectionCount(1); long allocAt0 = GC.GetTotalAllocatedBytes();
        var meter0 = Enum.GetValues<AllocKind>().Select(k => (Main: AllocationMeter.Shared.BytesOf(k, true), Other: AllocationMeter.Shared.BytesOf(k, false))).ToArray();
        var cpu0 = CpuMeter.Take();   // processor time of the process and of this (main) thread, for the arrival report
        double firstAvatarS = -1, firstBakeS = -1;   // seconds from the start of the arrival until the first avatar is drawn and the first bake texture is on an avatar
        var probe = arrival && Arg(args, "--tr-frame-probe") is not null ? new ArrivalProbe() : null;   // where the arrival's time and allocation go
        FrameProbe.Enabled = probe is not null;
        string gcMode = Arg(args, "--tr-gc=") ?? "default";   // experiment: sustained (the latency mode for the burst) or nogc (a no-collection region around each slice)
        var latencyBefore = System.Runtime.GCSettings.LatencyMode;
        if (gcMode == "sustained") System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        bool noBudget = Arg(args, "--tr-alter=nobudget") is not null;
   // the negative control: the burst mode is never set
        int noGcStarted = 0, noGcRefused = 0, noGcEnded = 0;
        if (probe is not null) FrameProbe.TakeFrame();
        var pipelines0 = PipelineWarmup.Counters();
        var settle = Stopwatch.StartNew();
        int added = arrival ? addedBefore : n;
        long lastFrame = Stopwatch.GetTimestamp();
        double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        while ((added < n || !Settled()) && settle.Elapsed < TimeSpan.FromSeconds(settleCap))
        {
            long t0 = Stopwatch.GetTimestamp();
            if (arrival)
            {
                // the crowd arrives evenly over the arrival time, by the clock
                int due = Math.Min(n, (int)Math.Ceiling(n * settle.Elapsed.TotalSeconds / arriveSeconds));
                while (added < due) Add(added++);
            }
            long t1 = Stopwatch.GetTimestamp();
            bool inNoGc = false;
            if (gcMode == "nogc")
            {
                try { inNoGc = GC.TryStartNoGCRegion(double.Parse(Arg(args, "--tr-nogc-mib=") ?? "16", System.Globalization.CultureInfo.InvariantCulture) is var mib ? (long)(mib * 1048576) : 16L << 20, disallowFullBlockingGC: true); if (inNoGc) noGcStarted++; else noGcRefused++; } catch (Exception) { noGcRefused++; }
                t1 = Stopwatch.GetTimestamp();   // the slice starts after the region is entered
            }
            probe?.Begin();
            double cpuA = CpuMeter.ThreadCycleMs();
            presenter.Frame();
            double cpuB = CpuMeter.ThreadCycleMs();
            long t2 = Stopwatch.GetTimestamp();
            arriveSliceCpu.Add(cpuB - cpuA);
            if (inNoGc) { try { if (System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion) { GC.EndNoGCRegion(); noGcEnded++; } } catch (Exception) { } }
            probe?.End(cpuB - cpuA);   // classified on the main thread's processor time, as the sections are
            arriveIngest.Add(ToMs(t1 - t0));
            arriveSlice.Add(ToMs(t2 - t1));
            await Frames(1);
            long t3 = Stopwatch.GetTimestamp();
            arriveFrame.Add(ToMs(t3 - lastFrame));
            if (firstAvatarS < 0 && avatars.Drawn >= 1) firstAvatarS = settle.Elapsed.TotalSeconds;
            if (firstBakeS < 0 && avatars.BakeTextures >= 1) firstBakeS = settle.Elapsed.TotalSeconds;
            arriveCpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(vpRid));
            arriveGpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(vpRid));
            arriveMarked.Add(_keeper?.TakeMark() ?? false);
            lastFrame = t3;
        }
        double settleSeconds = settle.Elapsed.TotalSeconds;
        if (arrival) JudgeFrames(arriveFrame, arriveCpu, arriveGpu, arriveSlice, arriveMarked);   // frame_pacing: the arrival's frames are the measured ones
        var arrivalCpu = CpuMeter.Take().Since(cpu0);
        double arrivalStepMax = prims.WorstStepMs; int arrivalStepsOver = prims.StepsOverBudget;
        double arrivalPauseMs = (GC.GetTotalPauseDuration() - gcPause0).TotalMilliseconds; int arrivalGen2 = GC.CollectionCount(2) - gcGen2At0;
        int arrivalGen0 = GC.CollectionCount(0) - gcGen0At0, arrivalGen1 = GC.CollectionCount(1) - gcGen1At0; double arrivalAllocMib = (GC.GetTotalAllocatedBytes() - allocAt0) / 1048576.0;
        FrameProbe.Enabled = false;
        System.Runtime.GCSettings.LatencyMode = latencyBefore;
        var allocByKind = Enum.GetValues<AllocKind>().Select((k, i) => new { kind = k.ToString(), main_mib = Math.Round((AllocationMeter.Shared.BytesOf(k, true) - meter0[i].Main) / 1048576.0, 1), other_threads_mib = Math.Round((AllocationMeter.Shared.BytesOf(k, false) - meter0[i].Other) / 1048576.0, 1) })
            .Where(x => x.main_mib > 0 || x.other_threads_mib > 0).ToArray();
        if (added < n || !Settled()) _failures.Add($"not settled in {settleCap} s: arrived {added} of {n}, avatars drawn {avatars.Drawn} of {n}, rigged meshes held {prims.Instances} of {expected}, shown {Shown()}, dirty {prims.Dirty}, animations ready {animations.Ready}"
            + (real ? $", shaped {avatars.Shaped}, morphed {avatars.Morphed}, bake textures {avatars.BakeTextures} of {4 * n}" : ""));
        memory.Snap("settled");
        for (int i = 0; i < 120; i++) { presenter.Frame(); await Frames(1); }   // a few seconds of the walk under way before recording

        int cap = (int)(seconds * 5000) + 1024;
        var frameMs = new List<double>(cap);
        var cpuMs = new List<double>(cap);
        var gpuMs = new List<double>(cap);
        var draws = new List<double>(cap);
        var avatarsDrawn = new List<double>(cap);
        var riggedHeld = new List<double>(cap);
        var poseMainMs = new List<double>(cap);
        var sceneMs = new List<double>(cap);
        var markedFrames = new List<bool>(cap);
        int[] gc0 = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        prims.ResetStepTimes();
        var measure = Stopwatch.StartNew();
        long last = Stopwatch.GetTimestamp();
        while (measure.Elapsed.TotalSeconds < seconds)
        {
            long s0 = Stopwatch.GetTimestamp();
            presenter.Frame();
            sceneMs.Add((Stopwatch.GetTimestamp() - s0) * 1000.0 / Stopwatch.Frequency);
            await Frames(1);
            long now = Stopwatch.GetTimestamp();
            frameMs.Add((now - last) * 1000.0 / Stopwatch.Frequency);
            last = now;
            markedFrames.Add(_keeper?.TakeMark() ?? false);
            cpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(vpRid));
            gpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(vpRid));
            draws.Add(RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame));
            avatarsDrawn.Add(avatars.Drawn);
            riggedHeld.Add(prims.RiggedObjects);
            poseMainMs.Add(avatars.PoseMsLastFrame);
        }
        if (!arrival) JudgeFrames(frameMs, cpuMs, gpuMs, sceneMs, markedFrames);   // frame_pacing: a standing crowd's frames are the measured ones
        memory.Snap("recorded");
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        img.SavePng(Path.Combine(_out, _tag + ".png"));
        var bakeSlotNames = new[] { "head", "upper", "lower", "eyes" };
        SortedDictionary<string, int> LevelHistogram()
        {
            var h = new SortedDictionary<string, int>();
            for (int i = 0; i < n; i++)
                if (avatars.Scene.ByLocalId(WearerOf(i)) is { } e)
                    foreach (var slot in bakeSlotNames)
                    {
                        string key = avatars.BakeLevelOf(e, slot).ToString();
                        h[key] = h.GetValueOrDefault(key) + 1;
                    }
            return h;
        }
        object? phaseReport = null;
        if (bakePhase)
        {
            // the farthest avatar's bakes must be coarse, come up to the full picture as the camera goes to it, and come down again as the camera goes back
            var farHist = LevelHistogram();
            double farMib = avatars.BakeGpuBytes / 1048576.0;
            int pick = Enumerable.Range(0, n).OrderByDescending(i => avatars.Scene.ByLocalId(WearerOf(i)) is { } pe ? bakeSlotNames.Sum(s => avatars.BakeLevelOf(pe, s)) : -1).First();
            var pickEntry = avatars.Scene.ByLocalId(WearerOf(pick))!;
            avatars.Scene.Place(pickEntry, out var pickSoles, out _);
            var pickAt = new GVector3(pickSoles.X, pickSoles.Z + 1.1f, -pickSoles.Y);
            var homePosition = cam.Position;
            void Aim(GVector3 position)
            {
                cam.Position = position;
                cam.LookAt(pickAt, GVector3.Up);
                prims.Camera = cam.GlobalPosition;
                prims.ResidencyView = PrimLayer.ViewOf(cam, size[0], size[1]);
                if (!fullBakes) avatars.BakeView = prims.ResidencyView;
            }
            async Task<bool> SettleBakes()
            {
                var w = Stopwatch.StartNew();
                while (!avatars.BakesAtWantedLevels && w.Elapsed < TimeSpan.FromSeconds(40)) { presenter.Frame(); await Frames(1); }
                for (int i = 0; i < 10; i++) { presenter.Frame(); await Frames(1); }
                return avatars.BakesAtWantedLevels;
            }
            int[] LevelsOfPick() => bakeSlotNames.Select(s => avatars.BakeLevelOf(pickEntry, s)).ToArray();
            var before = LevelsOfPick();
            int changes0 = avatars.BakeLevelChanges;
            Aim(pickAt + new GVector3(1.5f, 0.2f, 0f));
            bool nearSettled = await SettleBakes();
            var near = LevelsOfPick();
            double nearMib = avatars.BakeGpuBytes / 1048576.0;
            Aim(homePosition);
            bool backSettled = await SettleBakes();
            var comeBack = LevelsOfPick();
            phaseReport = new
            {
                avatar = pick, far_levels = before, near_levels = near, back_levels = comeBack, level_changes = avatars.BakeLevelChanges - changes0,
                far_histogram = farHist, far_gpu_mib = Math.Round(farMib, 2), near_gpu_mib = Math.Round(nearMib, 2), settled_near = nearSettled, settled_back = backSettled,
            };
            if (!fullBakes)
            {
                if (!farHist.Keys.Any(k => k != "0" && k != "-1")) _failures.Add($"no bake is sized down in the crowd's view: levels {string.Join(", ", farHist.Select(kv => kv.Key + ":" + kv.Value))}");
                if (before.All(l => l <= 0)) _failures.Add($"the farthest avatar's bakes are not coarser than full: {string.Join(",", before)}");
                if (near.Any(l => l != 0)) _failures.Add($"the avatar the camera went up to has bakes below full size: {string.Join(",", near)}");
                if (comeBack.All(l => l <= 0)) _failures.Add($"the bakes did not come down again as the camera went back: {string.Join(",", comeBack)}");
            }
        }
        var description = prims.Describe();
        int shown = Figure(description, "rigged_shown") ?? -1, skipped = Figure(description, "skipped_rigged_attachments") ?? -1;
        long vertices = 0, triangles = 0, listed = 0;
        for (int i = 0; i < n; i++) { int k = slots[i].Outfit; vertices += outfitVertices[k]; triangles += outfitTriangles[k]; listed += outfitJoints[k]; }

        object? realReport = null;
        if (real)
        {
            // each avatar against its look: shaped, morphed, all four body bakes on the body, the system body hidden exactly where the look says
            // (the alpha bakes' slots and the mesh body's), and the slots whose own bake is transparent exactly the alpha bakes'
            int baked = 0, hiddenOk = 0, alphaOk = 0, meshBodies = 0, males = 0;
            var wrong = new List<string>();
            var digest = new StringBuilder();
            long bakeBytesResident = 0;
            var residency = prims.Residency;
            for (int i = 0; i < n; i++)
            {
                var look = looks![i];
                if (look.MeshBody) meshBodies++;
                if (look.Male) males++;
                var e = avatars.Scene.ByLocalId(WearerOf(i));
                var hidden = e is null ? [] : avatars.HiddenSlots(e);
                var transparent = e is null ? [] : avatars.TransparentSlots(e);
                var want = look.HiddenSlots;
                var wantAlpha = look.AlphaSlots;
                bool h = want.SetEquals(hidden), a = wantAlpha.SetEquals(transparent);
                if (h) hiddenOk++; else if (wrong.Count < 5) wrong.Add($"avatar {i}: hidden [{string.Join(",", hidden)}] expected [{string.Join(",", want.Order())}]");
                if (a) alphaOk++; else if (wrong.Count < 5) wrong.Add($"avatar {i}: transparent [{string.Join(",", transparent)}] expected [{string.Join(",", wantAlpha.Order())}]");
                // each body bake is on the body: held by the residency, or (a bake whose own alpha hides its part is drawn from nothing) decided as hiding it
                if (e?.Handle is not null && look.Bakes.Where(b => b.Slot is "head" or "upper" or "lower" or "eyes").All(b => residency?.BytesOf(b.Id) > 0 || transparent.Contains(b.Slot))) baked++;
                foreach (var b in look.Bakes) bakeBytesResident += residency?.BytesOf(b.Id) ?? 0;
                digest.Append(i).Append('|').Append(slots[i].X).Append('|').Append(slots[i].Y).Append('|').Append(slots[i].Walking).Append('|').Append(look.Male).Append('|').Append(look.MeshBody)
                    .Append('|').Append(Convert.ToHexString(look.Params.Raw.ToArray())).Append('|').Append(string.Join(",", look.Bakes.Select(b => b.Id)))
                    .Append('|').Append(string.Join(",", hidden)).Append('|').Append(string.Join(",", transparent)).Append('\n');
            }
            // a bake held on the GPU by both the prim layer (a mesh body's faces) and the presenter is held twice
            int heldTwice = looks!.SelectMany(l => l.Bakes).Select(b => b.Id).Distinct().Count(id => avatars.HoldsBakeTexture(id) && prims.HasGpuTexture(id));
            string sceneDigest =Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digest.ToString())))[..16];
            realReport = new
            {
                looks = n, shaped = avatars.Shaped, morphed = avatars.Morphed, bake_textures = avatars.BakeTextures, bake_slots_settled = avatars.BakeSlotsSettled, bake_held_twice = heldTwice, bake_pool = new { encoded = poolMade!.Encoded, loaded = poolMade.Loaded, cached = bakeCache is not null }, avatars_with_all_body_bakes = baked,
                hidden_as_looked = hiddenOk, transparent_as_looked = alphaOk, mesh_bodies = meshBodies, males, females = n - males, scene_digest = sceneDigest, wrong,
                bake_pictures_ms = Math.Round(picturesMs, 1), bake_codestream_mib = Math.Round(bakeBytes!.Values.Sum(b => (long)b.Length) / 1048576.0, 2),
                bake_resident_mib = Math.Round(bakeBytesResident / 1048576.0, 2), bake_gpu_mib = Math.Round(avatars.BakeGpuBytes / 1048576.0, 2), bake_levels = LevelHistogram(),
                bake_uploads = avatars.BakeUploads, bake_level_changes = avatars.BakeLevelChanges, bake_upload_ms_max = Math.Round(avatars.BakeUploadMsMax, 2),
                bake_upload_ms_total = Math.Round(avatars.BakeUploadMsTotal, 1), bake_sizing = fullBakes ? "full" : legacyBakes ? "legacy" : "official", bake_phase = phaseReport, residency_resident_mib = Math.Round((residency?.ResidentBytes ?? 0) / 1048576.0, 2),
                texture_mem_mib = Math.Round(RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed) / 1048576.0, 1),
                video_mem_mib = Math.Round(RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed) / 1048576.0, 1),
                altered = noLook ? "nolook" : null,
            };
            if (avatars.Shaped != n) _failures.Add($"{avatars.Shaped} of {n} avatars are shaped");
            if (avatars.Morphed != n) _failures.Add($"{avatars.Morphed} of {n} avatars draw a morphed body");
            if (heldTwice != 0) _failures.Add($"{heldTwice} bakes are held on the GPU by both the prim layer and the avatar presenter");
            if (baked != n) _failures.Add($"{baked} of {n} avatars have all four body bakes on the body");
            if (hiddenOk != n || alphaOk != n) _failures.Add($"the default body is hidden as the looks say for {hiddenOk} of {n} avatars, transparent bakes for {alphaOk}: {string.Join("; ", wrong)}");
        }
        object? arrivalReport = null;
        if (arrival)
        {
            double maxSlice = arriveSlice.Count == 0 ? 0 : arriveSlice.Max();
            arrivalReport = new
            {
                avatars = n, arrive_seconds = arriveSeconds, time_to_finished_s = Math.Round(settleSeconds, 2), frames = arriveSlice.Count,
                slice_ms = Dist(arriveSlice), ingest_ms = Dist(arriveIngest), slice_max_ms = Math.Round(maxSlice, 2), slice_limit_ms = MainThreadSliceLimitMs,
                skin_atlas_max_slice_ms = probe is null ? (double?)null : Math.Round(probe.SkinMaxMs, 2),
                first_slices_ms = arriveSlice.Take(10).Select(s => Math.Round(s, 2)).ToArray(), pipelines_in_arrival = PipelineWarmup.Named(PipelineWarmup.Counters().Zip(pipelines0, (a, b) => a - b).ToArray()),
                arrival_warmup = arrivalWarm is null ? null : new { ms = Math.Round(arrivalWarm.Ms, 1), avatars = arrivalWarm.Avatars, frames = arrivalWarm.Frames },
                slices_over_limit = arriveSlice.Count(s => s > MainThreadSliceLimitMs),
                slice_cpu_ms = Dist(arriveSliceCpu), slice_cpu_max_ms = Math.Round(arriveSliceCpu.DefaultIfEmpty(0).Max(), 2), slices_cpu_over_limit = arriveSliceCpu.Count(s => s > MainThreadSliceLimitMs),
                avatar_presenter_frame_max_ms = Math.Round(avatars.FrameMsMax, 2), avatar_presenter_frames_over_4ms = avatars.FramesOver4Ms,
                prim_layer_worst_step_ms = Math.Round(arrivalStepMax, 2), prim_layer_steps_over_budget = arrivalStepsOver, units_deferred_by_budget = new { surfaces_handed_to_the_engine = prims.HandOversDeferred, avatar_arrival_units = avatars.ArrivalUnitsDeferred }, cpu_ms = new { process = arrivalCpu.ProcessMs, main_thread = arrivalCpu.ThreadMs, other_threads = arrivalCpu.OtherMs }, first_avatar_drawn_s = Math.Round(firstAvatarS, 3), first_bake_texture_s = Math.Round(firstBakeS, 3), gpu_ms = new { total = Math.Round(arriveGpu.Sum(), 1), dist = Dist(arriveGpu) }, gc_pause_ms = Math.Round(arrivalPauseMs, 1), gen2_collections = arrivalGen2, collections = new { gen0 = arrivalGen0, gen1 = arrivalGen1, gen2 = arrivalGen2 }, allocated_mib = Math.Round(arrivalAllocMib, 1), gc_mode = gcMode, shared_budget = !noBudget, no_gc_regions = new { started = noGcStarted, refused = noGcRefused, ended = noGcEnded }, warm_first = warmFirst, jit_warmup = new { done = JitWarmup.Done, ms = JitWarmup.Milliseconds, methods = JitWarmup.Methods }, allocated_by_work = allocByKind, frame_probe = probe?.Report(), frame_ms = Dist(arriveFrame), over_16_7_ms = arriveFrame.Count(f => f > 16.7),
                label = "indicative: taken under the build claim, not a measurement",
            };
            // the 4 ms rule is a figure here and a gate only with --tr-gate-slice: the arrival does not meet it yet (see the findings)
            // growing the bind-table atlas and handing it to the engine never puts a slice over the rule by itself (needs --tr-frame-probe)
            if (Arg(args, "--tr-gate-skin") is not null)
            {
                if (probe is null) _failures.Add("--tr-gate-skin needs --tr-frame-probe");
                else if (probe.SkinMaxMs > MainThreadSliceLimitMs) _failures.Add($"a slice spent {probe.SkinMaxMs:F2} ms on the bind-table atlas, over the {MainThreadSliceLimitMs} ms rule");
            }
            // the first run of the arrival's code is paid in the start-up warm-up: the first ten slices stay within the rule and no pipeline is compiled
            if (Arg(args, "--tr-gate-first") is not null)
            {
                double firstMax = arriveSlice.Take(10).DefaultIfEmpty(0).Max();
                long compiled = PipelineWarmup.Counters().Zip(pipelines0, (a, b) => a - b).Sum();
                if (firstMax > MainThreadSliceLimitMs) _failures.Add($"the first ten slices of the arrival reached {firstMax:F2} ms, over the {MainThreadSliceLimitMs} ms rule: {string.Join(", ", arriveSlice.Take(10).Select(s => s.ToString("F1")))}");
                if (compiled > 0) _failures.Add($"{compiled} pipelines were compiled while the crowd arrived, after the start-up warm-up");
            }
            if (Arg(args, "--tr-gate-slice") is not null && maxSlice > MainThreadSliceLimitMs) _failures.Add($"the longest main-thread slice while the crowd arrived was {maxSlice:F2} ms, over the {MainThreadSliceLimitMs} ms rule ({arriveSlice.Count(s => s > MainThreadSliceLimitMs)} slices over)");
        }
        // --tr-alter=keeppixels (negative control for the held-pixels check): one bake level is asked for and never released, as a build that kept decoded pixels would
        if (real && Arg(args, "--tr-alter=") == "keeppixels" && looks is not null)
        {
            var keepId = looks[0].Bakes[0].Id;
            var keepWait = Stopwatch.StartNew();
            while (textures!.TryGetLevel(keepId, 0, out _) == MeshAvailability.Pending && keepWait.Elapsed.TotalSeconds < 10) await Frames(1);
        }
        long heldPixelBytes = textures?.HeldPixelBytes ?? 0;
        int heldLevels = textures?.HeldLevels ?? 0;
        if (heldPixelBytes != 0 || heldLevels != 0)
        {
            var owners = looks is null ? new Dictionary<Guid, string>() : looks.Select((l, i) => (l, i)).SelectMany(x => x.l.Bakes.Select(b => (b.Id, Who: $"avatar {x.i} {b.Slot}{(x.l.MeshBody ? " (mesh body)" : "")}"))).ToDictionary(x => x.Id, x => x.Who);
            string whose = string.Join("; ", (textures?.HeldLevelKeys() ?? []).Select(k => $"{(owners.TryGetValue(k.Id, out var w) ? w : "not a bake")} level {k.Level}"));
            _failures.Add($"{heldLevels} decoded levels ({heldPixelBytes / 1048576.0:F1} MiB of pixels) are still held on the CPU side after every level was uploaded: {whose}");
        }
        memory.Snap("recorded, after a full collection", collect: true);
        // --tr-dump-wait: the process stays up, drawing, until a heap dump has been taken from outside (a marker file names its process id; another ends the wait)
        if (Arg(args, "--tr-dump-wait") is not null)
        {
            File.WriteAllText(Path.Combine(_out, _tag + ".dump-ready"), System.Environment.ProcessId.ToString());
            var dumpWait = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(_out, _tag + ".dump-done")) && dumpWait.Elapsed.TotalSeconds < 150) await Frames(1);
        }
        _result["mode"] = arrival ? "avatars-arrival" : real ? "avatars-crowd-real" : "avatars-crowd";
        _result["avatars_crowd"] = new
        {
            avatars = n, avatars_drawn = avatars.Drawn, walking, standing = n - walking, outfits = outfitCount, meshes_per_outfit = meshCount,
            rigged_expected = expected, rigged_held = prims.RiggedObjects, rigged_drawn = shown, rigged_skipped = skipped, stage = "empty", size = $"{size[0]}x{size[1]}", seed,
            vertices_total = vertices, triangles_total = triangles, listed_joints_total = listed, generate_ms = Math.Round(generateMs, 1), settle_seconds = Math.Round(settleSeconds, 2),
            seconds, frames = frameMs.Count, frame_ms = Dist(frameMs), over_16_7_ms = frameMs.Count(f => f > 16.7), scene_frame_ms = Dist(sceneMs), scene_frames_over_limit = sceneMs.Count(s => s > MainThreadSliceLimitMs),
            render_cpu_ms = Dist(cpuMs), render_gpu_ms = Dist(gpuMs), draw_calls = Dist(draws), avatars_per_frame = Dist(avatarsDrawn), rigged_per_frame = Dist(riggedHeld), pose_main_ms = Dist(poseMainMs), pose_on_workers = !AvatarLayer.PoseOnWorkers ? false : avatars.PosedOnWorkers,
            worst_step_ms = Math.Round(prims.WorstStepMs, 2), steps_over_budget = prims.StepsOverBudget, process_memory = PeakMemory(),
            tables_in_use = avatars.SkinTextures?.TablesInUse, table_capacity = avatars.SkinTextures?.TableCapacity,
            table_limit = avatars.SkinTextures?.MaxTables, atlas_bytes = avatars.SkinTextures?.AtlasBytes, atlas_mib = Math.Round((avatars.SkinTextures?.AtlasBytes ?? 0) / 1048576.0, 2),
            collections = new { gen0 = GC.CollectionCount(0) - gc0[0], gen1 = GC.CollectionCount(1) - gc0[1], gen2 = GC.CollectionCount(2) - gc0[2] },
            held_pixel_mib = Math.Round(heldPixelBytes / 1048576.0, 2), held_levels = heldLevels, prims = description, presenter = avatars.Describe(), png = _tag + ".png", real = realReport, arrival = arrivalReport, memory = memory.Stages,
        };
        if (avatars.Drawn != n) _failures.Add($"{avatars.Drawn} of {n} avatars are drawn");
        if (prims.RiggedObjects != expected) _failures.Add($"{prims.RiggedObjects} of {expected} worn rigged meshes are held");
        if (shown != expected) _failures.Add($"{shown} of {expected} worn rigged meshes are drawn");
        if (skipped != 0) _failures.Add($"{skipped} rigged meshes worn are counted and not drawn, expected none");
        if (avatarsDrawn.Min() != n) _failures.Add($"a recorded frame drew {avatarsDrawn.Min()} of {n} avatars");
        if (riggedHeld.Min() != expected) _failures.Add($"a recorded frame held {riggedHeld.Min()} of {expected} rigged meshes");
        if (args.Contains("--tr-leave"))
        {
            // --tr-leave: the avatars leave the region (each is killed, with what it wore) and the presenter runs until none is drawn and nothing is held
            var leaving = Enumerable.Range(0, n).Select(WearerOf).Concat(Enumerable.Range(0, n).SelectMany(i => Enumerable.Range(0, meshCount).Select(m => (uint)(1_000_000 + i * 100 + m)))).ToList();
            world.Kill(FixtureSim, leaving);
            var leave = Stopwatch.StartNew();
            while ((avatars.Drawn > 0 || prims.Instances > 0) && leave.Elapsed < TimeSpan.FromSeconds(10)) { presenter.Frame(); await Frames(1); }
            for (int i = 0; i < 10; i++) { presenter.Frame(); await Frames(1); }
            _result["leave"] = new { seconds = Math.Round(leave.Elapsed.TotalSeconds, 2), avatars_drawn = avatars.Drawn, instances = prims.Instances, avatar_resources = avatars.LiveResources };
            if (avatars.Drawn != 0 || prims.Instances != 0) _failures.Add($"after the avatars left, {avatars.Drawn} are still drawn and {prims.Instances} objects held");
        }
        presenter.Dispose();
        if (avatars.LiveResources != 0) _failures.Add($"{avatars.LiveResources} avatar resources live after the presenter went");
        prims.Clear();
        view.Clear();
        if (view is Node node) { node.GetParent()?.RemoveChild(node); node.Free(); }
        Rids.Free(floor.Instance);
        Rids.Free(floor.Mesh);
        floor.Material.Dispose();
        world.Dispose();
        long engineErrors = (Main.EngineLog?.Errors ?? 0) - engineErrors0;
        _result["engine_errors"] = engineErrors;
        if (engineErrors != 0) _failures.Add($"{engineErrors} engine errors during the crowd run and its teardown (the log names them)");
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>One synthetic outfit mesh as the rigged mesh the layer reads: its joint list by name, its binds, its faces with four influences a vertex.
    /// Blended faces are drawn at 60 % opacity through their colour; masked faces are drawn opaque (no texture here to cut them out).</summary>
    private static RiggedPiece OutfitPiece(CharacterSkeleton skeleton, OutfitMesh mesh, int outfit, int number, bool opaque)
    {
        var names = mesh.JointList.Select(j => skeleton.Joints[j].Name).ToArray();
        var block = new MeshSkinBlock(names, mesh.InverseBind, Matrix4x4.Identity, [], 0f, false, false);
        var faces = new List<(MeshFace, FaceInfluences)>();
        foreach (var f in mesh.Faces)
        {
            var positions = new float[f.VertexCount * 3];
            var normals = new float[f.VertexCount * 3];
            var uvs = new float[f.VertexCount * 2];
            for (int v = 0; v < f.VertexCount; v++)
            {
                positions[v * 3] = f.Positions[v].X; positions[v * 3 + 1] = f.Positions[v].Y; positions[v * 3 + 2] = f.Positions[v].Z;
                normals[v * 3] = f.Normals[v].X; normals[v * 3 + 1] = f.Normals[v].Y; normals[v * 3 + 2] = f.Normals[v].Z;
                uvs[v * 2] = f.Uvs[v].X; uvs[v * 2 + 1] = f.Uvs[v].Y;
            }
            faces.Add((new MeshFace(positions, normals, uvs, f.Indices), new FaceInfluences(f.VertexCount, f.Joints, f.Weights, 0)));
        }
        Rgba Colour(OutfitFace f) => new(f.Tint.X, f.Tint.Y, f.Tint.Z, f.Alpha == OutfitFaceAlpha.Blend && !opaque ? 0.6f : 1f);
        var colours = new FaceColors(Colour(mesh.Faces[0]), mesh.Faces.Skip(1).Select(f => (Rgba?)Colour(f)).ToArray());
        var id = new Guid(unchecked((int)0xc20d1000) + outfit, 0, 0, [0, 0, 0, 0, 0, 0, (byte)(number >> 8), (byte)number]);
        return new(id, RiggedGeometry(faces, block), 9, colours);
    }
}
