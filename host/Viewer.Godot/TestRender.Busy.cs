// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// A synthetic busy view (--tr-busy=objects:N,glow:percent,lights:L,projectors:K, <see cref="BusyScene"/>): N boxes through a 200 m square, a share of
/// them glowing, L prim lights of which K are projectors, and an eye circling on a fixed path, so the glow pass's object choice reruns every frame the eye moves.
/// The textures come from --tr-fixture= (the projector scene's, which holds the projectors' picture and the plain white image). After 2 s unrecorded,
/// --tr-busy-seconds (default 10) of frames are recorded: frame time, the engine's render CPU and GPU time, the glow pass's own CPU and GPU time and draw calls,
/// the draw calls of the projector shadow passes, the time of each run of the object choice, and the process's peak memory. The engine times the view as one,
/// so the shadow passes' cost is read from a second run with --tr-shadows=off. A smoke case at a small size checks the counts (no golden: the picture moves).
/// </summary>
public partial class TestRender
{
    private async Task<int> BusyRun(string spec, string[] args)
    {
        var busy = BusyScene.Parse(spec);
        string fixture = Arg(args, "--tr-fixture=") ?? throw new ArgumentException("--tr-busy needs --tr-fixture= (the projector scene, for its textures)");
        double seconds = double.Parse(Arg(args, "--tr-busy-seconds=") ?? "10", System.Globalization.CultureInfo.InvariantCulture);
        int seed = int.Parse(Arg(args, "--tr-seed=") ?? "8");
        var size = (Arg(args, "--tr-size=") ?? "1920x1080").Split('x').Select(int.Parse).ToArray();
        _pacingGuard = Arg(args, "--tr-pacing-guard") is not null;
        if (Arg(args, "--tr-glow-all") is not null) PrimLayer.TestGlowDrawAll = true;   // the glow picture draws every object, as before the cheaper route: the run to compare the pass's cost with
        if (Arg(args, "--tr-shadows=") == "off") ProjectorLights.Shadows = false;   // the projector shadows off: the second run that gives the shadow passes' cost
        // --tr-busy-still: the eye stays where it starts and the run lasts --tr-busy-frames frames (120), so two runs show the same picture;
        // --tr-busy-poke: in a still run, some objects move and some change colour at frame 10, and the picture of frame 12 is kept (--tr-golden-poke= compares it);
        // --tr-golden= compares the last frame's picture with a saved one
        // --tr-busy-churn: the eye stays put and for --tr-busy-seconds about a tenth of the objects move all the time (a sway that depends only on the step, 60 steps a second, each moving object updated every fourth step)
        // and four objects change colour each second. At two fixed steps the churn stops, the changes are drained until the layer has presented them all, and the picture is kept
        // (--tr-churn-golden=<prefix of an earlier run's pictures> compares each with the same checkpoint of that run: a merged run must show what an unmerged one shows).
        bool churn = args.Contains("--tr-busy-churn");
        string? churnGolden = Arg(args, "--tr-churn-golden=");
        bool still = args.Contains("--tr-busy-still") || churn;
        bool poke = args.Contains("--tr-busy-poke");
        int stillFrames = int.Parse(Arg(args, "--tr-busy-frames=") ?? "120");
        string? golden = Arg(args, "--tr-golden="), goldenPoke = Arg(args, "--tr-golden-poke=");
        long engineErrors0 = Main.EngineLog?.Errors ?? 0;   // the engine reports what the viewer did wrong (an instance with no room for its shader parameters, a material bound before its texture exists) as errors
        FreeRunning();
        GlowPass.MeasureTimes = true;
        PrimLayer.MeasureChoice = true;
        var w = await PipelineWarmup.RunAsync(this, Viewport.Msaa.Disabled, () => Frames(1));
        _result["warmup"] = new { ms = Math.Round(w.Ms, 2), pipelines = w.Pipelines, variants = w.Variants, frames = w.Frames };
        var compiled0 = PipelineWarmup.Counters();   // pipelines the engine compiles after the warm-up, to the end of the run
        int materials0 = WrapperCounts()?.GetValueOrDefault("StandardMaterial3D") ?? -1;   // the warm-up's own materials stay for the process; the view's and the layer's must go with the scene

        var loaded = Load(fixture);   // its textures only: the scene is the synthetic one
        var prims = BusyScene.Make(busy, seed);
        var counts = BusyScene.Count(prims);
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("busy", 1000, 1000, 256, 256, SizeAuthoritative: true));
        foreach (var p in PrimZoo.AsPrimData(prims)) world.ApplyObject(FixtureSim, p);

        var rig = MakeRig(new Vector2I(size[0], size[1]));
        var vpRid = rig.Viewport.GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(vpRid, true);
        var cam = rig.Camera;
        cam.Fov = 60f; cam.Near = 0.2f; cam.Far = 512f;
        var view = MakeView("server", rig);
        var layer = new PrimLayer(rig.Scenario, world, null, loaded.Textures, _budget) { SimulatedFps = 60, MaterialAssets = loaded.MaterialAssets };
        void Place(double t)
        {
            var (eye, target) = BusyScene.EyeAt(t);
            cam.Position = new Vector3(eye.X, eye.Z, -eye.Y);
            cam.LookAt(new Vector3(target.X, target.Z, -target.Y), Vector3.Up);
            layer.Camera = cam.GlobalPosition;
            layer.ResidencyView = PrimLayer.ViewOf(cam, size[0], size[1]);
            layer.UpdateLods();
            layer.ViewCamera = cam;
            layer.UpdateBlendOrder();
        }
        Place(0);
        var presenter = new WorldPresenter(world, view, prims: layer);
        var glowPass = new GlowPass(layer, rig.World, rig.Environment);
        rig.Root.AddChild(glowPass);
        if (CompileWatch.Active is { } cw0) cw0.Phase = "settle";
        await SettleMeshes(presenter, layer, null, loaded.Textures);

        if (CompileWatch.Active is { } cw1) cw1.Phase = "first frames";
        var clock = Stopwatch.StartNew();
        for (int warm = 0; still ? warm < 90 : clock.Elapsed.TotalSeconds < 2; warm++)
        {
            presenter.Frame();
            Place(still ? 0 : clock.Elapsed.TotalSeconds);
            await Frames(1);
        }
        if (PrimLayer.MergeStill)
        {
            if (CompileWatch.Active is { } cw2) cw2.Phase = "merging";
            // the still objects are merged before the recorded frames: frames run until the layer has merged and nothing is being built
            var settle = Stopwatch.StartNew();
            int quiet = 0;
            while (settle.Elapsed.TotalSeconds < 60 && quiet < 30)
            {
                presenter.Frame();
                Place(still ? 0 : clock.Elapsed.TotalSeconds);
                await Frames(1);
                quiet = layer.MergeableObjects > 0 && layer.MergedObjects >= 0.9 * layer.MergeableObjects && !layer.MergeBusy ? quiet + 1 : 0;
            }
            _result["merge_settle_seconds"] = Math.Round(settle.Elapsed.TotalSeconds, 2);
        }
        var pokeData = PrimZoo.AsPrimData(prims).ToDictionary(p => p.LocalId);
        var moved = new List<uint>(); var recoloured = new List<uint>();
        if (poke)
            foreach (var p in prims)
                if (p.Name == BusyScene.ObjectName) { int k = (int)(p.LocalId - BusyScene.ObjectBase); if (k % 7 == 0) moved.Add(p.LocalId); else if (k % 7 == 3) recoloured.Add(p.LocalId); }
        // churn: the state of every object at a given step is a function of the step alone, so two runs agree at a checkpoint whatever their frame rates
        var cur = new Dictionary<uint, PrimData>(pokeData);
        var swayers = new List<uint>();
        if (churn) foreach (var p in prims) if (p.Name == BusyScene.ObjectName && (p.LocalId - BusyScene.ObjectBase) % 10 == 0) swayers.Add(p.LocalId);
        int colouredSecond = -1, churnStep = 0, churnApplied = 0;
        int[] checkpointSteps = [88, 332];
        var checkpoints = new List<object>();
        var checkpointFailures = new List<string>();
        void ChurnTo(int step)
        {
            foreach (var id in swayers)
            {
                var d = cur[id] with { Position = pokeData[id].Position + new System.Numerics.Vector3(MathF.Sin(step * 0.15f + id) , 0f, 0f) };
                cur[id] = d; world.ApplyObject(FixtureSim, d); churnApplied++;
            }
            for (int sec = colouredSecond + 1; sec <= step / 60; sec++)
                for (int j = 0; j < 4; j++)
                {
                    uint id = (uint)(BusyScene.ObjectBase + (sec * 37 + j * 997) % busy.Objects);
                    var d = cur[id] with { Colors = new FaceColors(new Rgba(0.2f + 0.15f * ((sec + j) % 5), 0.9f - 0.1f * (sec % 7), 0.3f, 1f)) };
                    cur[id] = d; world.ApplyObject(FixtureSim, d); churnApplied++;
                }
            colouredSecond = Math.Max(colouredSecond, step / 60);
            churnStep = step;
        }
        Image? pokeImage = null;
        int pokeFrame = -1, mergedBeforePoke = 0;
        double worstMergeBefore = 0;
        layer.GlowChoiceMs.Clear();
        int cap = (int)(seconds * 5000) + 1024;
        var frameMs = new List<double>(cap); var cpuMs = new List<double>(cap); var gpuMs = new List<double>(cap);
        var passCpu = new List<double>(cap); var passGpu = new List<double>(cap); var passDraws = new List<double>(cap);
        var draws = new List<double>(cap); var viewDraws = new List<double>(cap); var shadowDraws = new List<double>(cap);
        var mainMs = new List<double>(cap); var marked = new List<bool>(cap);   // the main thread's own work in each frame; frames the window was not drawing in
        bool minimized = false;
        int[] gc0 = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        if (CompileWatch.Active is { } cw3) cw3.Phase = "measured";
        var measure = Stopwatch.StartNew();
        long last = Stopwatch.GetTimestamp();
        int mergeBuilds0 = layer.MergeBuilds, takenApart0 = layer.MergeTakenApart, backlogMax = 0;
        double drainSeconds = 0;   // the checkpoints' drain frames are not churn time
        for (int frame = 0; still && !churn ? frame < stillFrames : measure.Elapsed.TotalSeconds - drainSeconds < seconds; frame++)
        {
            if (churn)
            {
                int target = (int)((measure.Elapsed.TotalSeconds - drainSeconds) * 60);
                backlogMax = Math.Max(backlogMax, presenter.Coordinator.Backlog);
                int cp = checkpoints.Count;
                if (cp < checkpointSteps.Length && target >= checkpointSteps[cp])
                {
                    // a checkpoint: the churn is set to this step exactly, stops, and the frames run until the layer has presented every change; then the picture is kept
                    ChurnTo(checkpointSteps[cp]);
                    int calm = 0, drained = 0;
                    double drainStart = measure.Elapsed.TotalSeconds;
                    int backlogAtStop = presenter.Coordinator.Backlog;
                    for (; drained < 1500 && calm < 3; drained++)
                    {
                        presenter.Frame(); Place(0); await Frames(1);
                        calm = layer.Dirty == 0 && presenter.Coordinator.Backlog == 0 ? calm + 1 : 0;
                    }
                    drainSeconds += measure.Elapsed.TotalSeconds - drainStart;
                    if (calm < 3) _failures.Add($"checkpoint {cp + 1}: the changes were not all presented after {drained} frames (backlog {presenter.Coordinator.Backlog}, dirty {layer.Dirty})");
                    var shot = rig.Texture.GetImage(); shot.Convert(Image.Format.Rgba8);
                    shot.SavePng(System.IO.Path.Combine(_out, $"{_tag}-cp{cp + 1}.png"));
                    var cmp = churnGolden is null ? null : Compare(shot, $"{churnGolden}-cp{cp + 1}.png", $"checkpoint {cp + 1}");
                    checkpoints.Add(new { step = checkpointSteps[cp], backlog_at_stop = backlogAtStop, drained_frames = drained, merged_objects = layer.MergedObjects, picture = cmp });
                    last = Stopwatch.GetTimestamp();
                    continue;
                }
                if (target / 4 > churnStep / 4) ChurnTo(target);   // each moving object is updated 15 times a second
            }
            long iterationStart = Stopwatch.GetTimestamp();
            if (_minimizeAt > 0 && !minimized && measure.Elapsed.TotalSeconds >= _minimizeAt)
            {
                minimized = true;   // test only (--tr-minimize-at): the window is minimised once, to show it is brought back and its frames set aside
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Minimized);
            }
            if (poke && frame == 10)
            {
                foreach (var id in moved) world.ApplyObject(FixtureSim, pokeData[id] with { Position = pokeData[id].Position + new System.Numerics.Vector3(1.5f, 0f, 0f) });
                foreach (var id in recoloured) world.ApplyObject(FixtureSim, pokeData[id] with { Colors = new FaceColors(new Rgba(0.9f, 0.2f, 0.2f, 1f)) });
                worstMergeBefore = layer.WorstMergeMs;
                mergedBeforePoke = layer.MergedObjects;
            }
            presenter.Frame();
            Place(still ? 0 : clock.Elapsed.TotalSeconds);
            mainMs.Add((Stopwatch.GetTimestamp() - iterationStart) * 1000.0 / Stopwatch.Frequency);
            await Frames(1);
            // the picture of the first frame after the poke in which every change is presented (the presenter spreads them over frames)
            if (poke && pokeImage is null && frame > 10 && layer.Dirty == 0) { pokeImage = rig.Texture.GetImage(); pokeImage.Convert(Image.Format.Rgba8); pokeFrame = frame; }
            long now = Stopwatch.GetTimestamp();
            frameMs.Add((now - last) * 1000.0 / Stopwatch.Frequency);
            marked.Add(_keeper?.TakeMark() ?? false);
            last = now;
            cpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(vpRid));
            gpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(vpRid));
            var (gc, gg) = glowPass.PassMs();
            passCpu.Add(gc); passGpu.Add(gg);
            passDraws.Add(glowPass.PassDrawCalls());
            draws.Add(RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame));
            viewDraws.Add(RenderingServer.ViewportGetRenderInfo(vpRid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame));
            shadowDraws.Add(RenderingServer.ViewportGetRenderInfo(vpRid, RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.DrawCallsInFrame));
        }
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        System.IO.Directory.CreateDirectory(_out);
        img.SavePng(System.IO.Path.Combine(_out, _tag + ".png"));
        object? Compare(Image shown, string? goldenPath, string label)
        {
            if (goldenPath is null) return null;
            if (!System.IO.Path.IsPathFullyQualified(goldenPath) || !System.IO.File.Exists(goldenPath)) { _failures.Add(label + " golden not found: " + System.IO.Path.GetFileName(goldenPath)); return null; }
            var g = Image.LoadFromFile(goldenPath);
            g.Convert(Image.Format.Rgba8);
            var diff = g.GetWidth() != shown.GetWidth() || g.GetHeight() != shown.GetHeight()
                ? new DiffResult(false, g.GetWidth(), g.GetHeight(), 0, 0, 0, 0, "golden size differs")
                : ImageDiff.Compare(shown.GetData(), g.GetData(), shown.GetWidth(), shown.GetHeight(), 4, DiffTolerance.Terrain);
            if (!diff.Pass) _failures.Add(label + " diff " + diff);
            return new { golden = System.IO.Path.GetFileName(goldenPath), pass = diff.Pass, differing = diff.Differing, max_channel_error = diff.MaxChannelError, summary = diff.ToString() };
        }
        var pictureDiff = Compare(img, golden, "final");
        object? pokeDiff = null;
        if (pokeImage is not null)
        {
            pokeImage.SavePng(System.IO.Path.Combine(_out, _tag + "-poke.png"));
            pokeDiff = Compare(pokeImage, goldenPoke, "after poke");
        }
        var (omni, spot) = layer.LightKinds;
        var self = Process.GetCurrentProcess();
        const double MiB = 1048576.0;
        var judged = JudgeFrames(frameMs, cpuMs, gpuMs, mainMs, marked);
        _result["mode"] = "busy";
        _result["busy"] = new
        {
            asked = new { objects = busy.Objects, glow_percent = busy.GlowPercent, lights = busy.Lights, projectors = busy.Projectors },
            counts = new { objects = counts.Objects, glowing = counts.Glowing, lights = counts.Lights, projectors = counts.Projectors },
            held = new { glow_objects = layer.GlowObjects, omni_lights = omni, spot_lights = spot },
            shadows = ProjectorLights.Shadows, seconds, frames = frameMs.Count, size = $"{size[0]}x{size[1]}", seed,
            compiled_after_warmup = PipelineWarmup.Named(PipelineWarmup.Counters().Zip(compiled0, (a, b) => a - b).ToArray()),
            pick = layer.MergedCellKeys.Count > 0 && layer.ObjectOfMergedTriangle(layer.MergedCellKeys[0], 0, 0) is { } hit ? new { local = hit.Local, face = hit.Face } : null,   // the map from merged geometry back to an object and face
            picture = pictureDiff, picture_after_poke = pokeDiff, poked = new { capture_frame = pokeFrame, merged_objects_before = mergedBeforePoke, moved = moved.Count, recoloured = recoloured.Count, worst_merge_ms_before = Math.Round(worstMergeBefore, 3), worst_merge_ms_after = Math.Round(layer.WorstMergeMs, 3) },
            churn = churn ? new
            {
                final_step = churnStep, churn_seconds = Math.Round(measure.Elapsed.TotalSeconds - drainSeconds, 2), backlog_max = backlogMax, object_updates = churnApplied, swaying_objects = swayers.Count, checkpoints,
                merging = PrimLayer.MergeStill, cell_builds = layer.MergeBuilds - mergeBuilds0, cell_builds_per_second = Math.Round((layer.MergeBuilds - mergeBuilds0) / Math.Max(0.001, measure.Elapsed.TotalSeconds - drainSeconds), 2),
                cells_taken_apart = layer.MergeTakenApart - takenApart0, merged_objects_at_end = layer.MergedObjects,
                main_thread_ms = DistOrNull(mainMs), worst_merge_slice_ms = Math.Round(layer.WorstMergeMs, 3),
            } : null,
            census = _census || PrimLayer.MergeStill ? CensusReport(layer, glowPass, rig, 0) : null,
            glow_active = glowPass.Active, glow_picture_objects = layer.GlowPictureObjects,
            frame_ms = DistOrNull(judged.FrameMs), frame_ms_series = frameMs.Select(f => Math.Round(f, 2)).ToList(), over_16_7_ms = judged.FrameMs.Count(f => f > 16.7),
            render_cpu_ms = DistOrNull(judged.CpuMs), render_gpu_ms = DistOrNull(judged.GpuMs),
            pass_cpu_ms = DistOrNull(passCpu), pass_gpu_ms = DistOrNull(passGpu), pass_draw_calls = DistOrNull(passDraws),
            draw_calls = DistOrNull(draws), view_draw_calls = DistOrNull(viewDraws), shadow_draw_calls = DistOrNull(shadowDraws),
            glow_choice_runs = layer.GlowChoiceMs.Count, glow_choice_ms = DistOrNull(layer.GlowChoiceMs),
            peak_working_set_mib = Math.Round(self.PeakWorkingSet64 / MiB, 1), peak_private_mib = Math.Round(self.PeakPagedMemorySize64 / MiB, 1),
            collections = new { gen0 = GC.CollectionCount(0) - gc0[0], gen1 = GC.CollectionCount(1) - gc0[1], gen2 = GC.CollectionCount(2) - gc0[2] },
        };
        PrimLayer.MeasureChoice = false;
        presenter.Dispose();
        layer.Clear();
        world.Dispose();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        int materials1 = WrapperCounts()?.GetValueOrDefault("StandardMaterial3D") ?? -1;
        _result["standard_materials_left"] = materials0 < 0 ? null : materials1 - materials0;
        if (materials0 >= 0 && materials1 > materials0) _failures.Add($"{materials1 - materials0} StandardMaterial3D objects made by the scene are still alive after it was torn down");
        long engineErrors = (Main.EngineLog?.Errors ?? 0) - engineErrors0;
        _result["engine_errors"] = engineErrors;
        if (engineErrors != 0) _failures.Add($"{engineErrors} engine errors during the busy view (the log names them)");
        return _failures.Count == 0 ? 0 : 1;
    }
}
