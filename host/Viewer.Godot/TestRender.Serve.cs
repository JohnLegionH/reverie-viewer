// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The batch mode of the render harness (--tr-serve=&lt;folder&gt;). One viewer process, started once, runs the cases it is handed one after
/// another instead of one process per case, so the cost of a start (the engine, the renderer, the viewer's shaders) is paid once.
/// The protocol is files in the folder, so the tool that hands the cases out (tools\render-serve.ps1) needs nothing but the file system:
///   ready             written when the loop is up (the process id)
///   &lt;tag&gt;.request    the first line is "timeout=&lt;seconds&gt;", every further line is one argument of the case, as it would follow
///                     "--test-render" on the command line of a one-case process; written under another name and renamed, so it is whole
///   current           the case being run: tag, the time it started (UTC, Unix seconds) and its timeout, so a launcher can tell a case that hangs
///   &lt;tag&gt;.done       written when the case has finished and its &lt;tag&gt;.json is on disk: the case's exit code
///   quit.request      the process leaves its loop and exits
/// Each case runs in a new <see cref="TestRender"/> node (every setting the case reads from its arguments is a field of that node, so
/// none of them reaches the next case), and the process-wide test switches that cases set are put back to their start values first
/// (<see cref="CaptureProcessDefaults"/>; a test scans the harness for any that is missing there). A case that ends the process
/// (a hung main thread, a native crash, the not-drawing exit) is the launcher's to report; it starts a new process for the next case.
/// </summary>
public partial class TestRender
{
    /// <summary>Set by the serving loop before the node enters the tree: the case's arguments (null in a one-case process, which reads the command line).</summary>
    internal string[]? CaseArgs;

    /// <summary>Set by the serving loop: completed with the case's exit code when the case has written its result (null in a one-case process, which quits instead).</summary>
    internal TaskCompletionSource<int>? Completed;

    private Stopwatch _caseClock = Stopwatch.StartNew();
    private double _processAgeAtStartMs;
    private readonly List<(string Name, double Ms)> _phases = new();

    /// <summary>The time since this process started, in milliseconds (the engine and renderer start-up is in it).</summary>
    private static double ProcessAgeMs()
    {
        using var self = Process.GetCurrentProcess();
        return (DateTime.Now - self.StartTime).TotalMilliseconds;
    }

    /// <summary>Records that the case has reached a stage; <c>phases</c> in the result lists the stages with the milliseconds since the case started.</summary>
    private void Phase(string name) => _phases.Add((name, Math.Round(_caseClock.Elapsed.TotalMilliseconds, 1)));

    private object PhaseReport() => new
    {
        process_age_at_start_ms = Math.Round(_processAgeAtStartMs, 1),
        served = CaseArgs is not null,
        stages = _phases.Select(p => new { name = p.Name, ms = p.Ms }).ToArray(),
    };

    // ---------------- the process-wide switches a case may set ----------------

    /// <summary>One process-wide test switch: its name, and what puts it back to the value it had when the process started.</summary>
    internal sealed record ProcessDefault(string Name, Action Restore);

    /// <summary>
    /// The value of every process-wide test switch the harness sets, read now, so a case that sets one cannot reach the next case.
    /// The names are what <c>RenderServeScanTests</c> checks the harness's assignments against.
    /// </summary>
    internal static List<ProcessDefault> CaptureProcessDefaults()
    {
        var list = new List<ProcessDefault>();
        void Keep<T>(string name, Func<T> get, Action<T> set) { T value = get(); list.Add(new ProcessDefault(name, () => set(value))); }
        Keep("PipelineWarmup.LeakOneForTest", () => PipelineWarmup.LeakOneForTest, v => PipelineWarmup.LeakOneForTest = v);
        Keep("PipelineWarmup.WithoutLightsForTest", () => PipelineWarmup.WithoutLightsForTest, v => PipelineWarmup.WithoutLightsForTest = v);
        Keep("SkinTextures.DeviceAtlas", () => SkinTextures.DeviceAtlas, v => SkinTextures.DeviceAtlas = v);
        Keep("ReverieRestyle.Enabled", () => ReverieRestyle.Enabled, v => ReverieRestyle.Enabled = v);
        Keep("EnvironmentLayer.ProbeHazeOff", () => EnvironmentLayer.ProbeHazeOff, v => EnvironmentLayer.ProbeHazeOff = v);
        Keep("EnvironmentLayer.HazeOff", () => EnvironmentLayer.HazeOff, v => EnvironmentLayer.HazeOff = v);
        Keep("EnvironmentLayer.DynamicExposure", () => EnvironmentLayer.DynamicExposure, v => EnvironmentLayer.DynamicExposure = v);
        Keep("HdrTonemapEffect.ExposureInstant", () => HdrTonemapEffect.ExposureInstant, v => HdrTonemapEffect.ExposureInstant = v);
        Keep("HdrTonemapEffect.ReadExposure", () => HdrTonemapEffect.ReadExposure, v => HdrTonemapEffect.ReadExposure = v);
        Keep("AvatarLayer.PoseOnWorkers", () => AvatarLayer.PoseOnWorkers, v => AvatarLayer.PoseOnWorkers = v);
        Keep("AvatarPresenter.TestMotionsOff", () => AvatarPresenter.TestMotionsOff, v => AvatarPresenter.TestMotionsOff = v);
        Keep("WorldPresenter.NoSharedBudget", () => WorldPresenter.NoSharedBudget, v => WorldPresenter.NoSharedBudget = v);
        Keep("WorldPresenter.SettleWindowMs", () => WorldPresenter.SettleWindowMs, v => WorldPresenter.SettleWindowMs = v);
        Keep("WorldPresenter.TerrainOnMainThread", () => WorldPresenter.TerrainOnMainThread, v => WorldPresenter.TerrainOnMainThread = v);
        Keep("PrimLayer.TestForceGlow", () => PrimLayer.TestForceGlow, v => PrimLayer.TestForceGlow = v);
        Keep("PrimLayer.AutoMaskAlpha", () => PrimLayer.AutoMaskAlpha, v => PrimLayer.AutoMaskAlpha = v);
        Keep("PrimLayer.TestGlowDrawAll", () => PrimLayer.TestGlowDrawAll, v => PrimLayer.TestGlowDrawAll = v);
        Keep("PrimLayer.TestLights", () => PrimLayer.TestLights, v => PrimLayer.TestLights = v);
        Keep("PrimLayer.MeasureChoice", () => PrimLayer.MeasureChoice, v => PrimLayer.MeasureChoice = v);
        Keep("PrimLayer.MergeStill", () => PrimLayer.MergeStill, v => PrimLayer.MergeStill = v);
        Keep("PrimLayer.MergeStillSeconds", () => PrimLayer.MergeStillSeconds, v => PrimLayer.MergeStillSeconds = v);
        Keep("PrimLayer.MergeCellMetres", () => PrimLayer.MergeCellMetres, v => PrimLayer.MergeCellMetres = v);
        Keep("PrimLayer.CensusOn", () => PrimLayer.CensusOn, v => PrimLayer.CensusOn = v);
        Keep("PrimLayer.CarryMoving", () => PrimLayer.CarryMoving, v => PrimLayer.CarryMoving = v);
        Keep("GlowPass.TestCameraBits", () => GlowPass.TestCameraBits, v => GlowPass.TestCameraBits = v);
        Keep("GlowPass.MeasureTimes", () => GlowPass.MeasureTimes, v => GlowPass.MeasureTimes = v);
        Keep("GlowPass.Disabled", () => GlowPass.Disabled, v => GlowPass.Disabled = v);
        Keep("ProjectorLights.Shadows", () => ProjectorLights.Shadows, v => ProjectorLights.Shadows = v);
        Keep("ProjectorLights.TestViewLayerOnly", () => ProjectorLights.TestViewLayerOnly, v => ProjectorLights.TestViewLayerOnly = v);
        Keep("MeshLodPreparer.CapOverride", () => MeshLodPreparer.CapOverride, v => MeshLodPreparer.CapOverride = v);
        Keep("FrameProbe.Enabled", () => FrameProbe.Enabled, v => FrameProbe.Enabled = v);
        Keep("KeptCollections.FreshForTest", () => KeptCollections.FreshForTest, v => KeptCollections.FreshForTest = v);
        Keep("KeptCollections.SkipClearForTest", () => KeptCollections.SkipClearForTest, v => KeptCollections.SkipClearForTest = v);
        Keep("LoggedErrorReleases.KeepForTest", () => LoggedErrorReleases.KeepForTest, v => LoggedErrorReleases.KeepForTest = v);
        Keep("CompileWatch.Active", () => CompileWatch.Active, v => CompileWatch.Active = v);
        Keep("Dock.PulseEnabled", () => Dock.PulseEnabled, v => Dock.PulseEnabled = v);
        Keep("GCSettings.LatencyMode", () => System.Runtime.GCSettings.LatencyMode, v => System.Runtime.GCSettings.LatencyMode = v);
        // the harness's own static state: the fetch counters and the asset store start empty in every case, and the alter flags start off
        Keep("TestRender.s_alterVertex", () => s_alterVertex, v => s_alterVertex = v);
        Keep("TestRender.s_alterSculpt", () => s_alterSculpt, v => s_alterSculpt = v);
        Keep("TestRender.s_alterWater", () => s_alterWater, v => s_alterWater = v);
        Keep("TestRender.s_alterOverride", () => s_alterOverride, v => s_alterOverride = v);
        Keep("TestRender.s_alterNoLegacy", () => s_alterNoLegacy, v => s_alterNoLegacy = v);
        Keep("TestRender.s_alterFallbackUnshared", () => s_alterFallbackUnshared, v => s_alterFallbackUnshared = v);
        Keep("TestRender.s_holdTexture", () => s_holdTexture, v => s_holdTexture = v);
        Keep("TestRender.s_assetStoreBytes", () => s_assetStoreBytes, v => s_assetStoreBytes = v);
        list.Add(new ProcessDefault("TestRender.s_sharedAssets", () => s_sharedAssets = null));
        list.Add(new ProcessDefault("TestRender.s_meshFetches", () => s_meshFetches.Clear()));
        list.Add(new ProcessDefault("TestRender.s_textureFetches", () => s_textureFetches.Clear()));
        return list;
    }

    // ---------------- the loop ----------------

    private static string? ReadyOrder(string folder)
    {
        // oldest request first; a request still being written has another name until it is whole
        return Directory.EnumerateFiles(folder, "*.request").OrderBy(File.GetLastWriteTimeUtc).ThenBy(f => f, StringComparer.Ordinal).FirstOrDefault();
    }

    private async Task ServeLoop(string folder, string[] launchArgs)
    {
        int exit = 0;
        try
        {
            Directory.CreateDirectory(folder);
            var defaults = CaptureProcessDefaults();
            Console.Error.WriteLine($"TEST-RENDER serve: waiting for cases in {folder}");
            File.WriteAllText(Path.Combine(folder, "ready"), System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            int served = 0;
            while (true)
            {
                string? request = ReadyOrder(folder);
                if (request is null) { await ToSignal(GetTree().CreateTimer(0.02), SceneTreeTimer.SignalName.Timeout); continue; }
                if (Path.GetFileName(request) == "quit.request") break;
                string[] lines = File.ReadAllLines(request);
                File.Move(request, request + ".taken", true);
                double timeout = lines.Length > 0 && lines[0].StartsWith("timeout=", StringComparison.Ordinal)
                    ? double.Parse(lines[0]["timeout=".Length..], CultureInfo.InvariantCulture) : 240;
                string[] caseArgs = lines.Skip(1).Where(l => l.Length > 0).ToArray();
                string tag = caseArgs.FirstOrDefault(a => a.StartsWith("--tr-tag=", StringComparison.Ordinal))?["--tr-tag=".Length..] ?? "render";
                File.WriteAllText(Path.Combine(folder, "current"), $"{tag}\t{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\t{timeout.ToString(CultureInfo.InvariantCulture)}");
                foreach (var d in defaults) d.Restore();
                EnvironmentLayer.ResetClassicLight();
                var node = new TestRender { CaseArgs = caseArgs, Completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously) };
                AddChild(node);
                int caseExit = await node.Completed.Task;
                // the case's own stage (its viewport and what hangs on it) goes with its node; the engine drains what it freed before the next case
                RemoveChild(node);
                node.QueueFree();
                await Frames(2);
                VariantDisposal.DrainAll("serve case");
                served++;
                // written beside and renamed into place, so the script never sees the file before the exit code is in it
                string done = Path.Combine(folder, tag + ".done");
                File.WriteAllText(done + ".tmp", caseExit.ToString(CultureInfo.InvariantCulture));
                File.Move(done + ".tmp", done, overwrite: true);
            }
            Console.Error.WriteLine($"TEST-RENDER serve: {served} cases run, leaving");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("TEST-RENDER serve: stopped by " + e.GetType().Name + ": " + e.Message);
            exit = 2;
        }
        GetTree().Quit(exit);
    }
}
