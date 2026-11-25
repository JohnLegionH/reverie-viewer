// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Meshing;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// <c>--test-render</c> (the test strategy 3.3, L4 and L3). No network. User arguments after "--":
///   --test-render --tr-out=&lt;absolute dir&gt; --tr-fixture=&lt;absolute path: *.nvterrain.gz | capture *.jsonl.gz&gt; | synthetic-1024
///   [--tr-tag=&lt;name&gt;] [--tr-frames=N (8)] [--tr-view=server|nodes (server)] [--tr-golden=&lt;absolute png&gt;] [--tr-alter=hill]
///   [--tr-measure=&lt;frames&gt;] [--tr-lifecycle=&lt;cycles&gt;]
/// Render: the fixture is fed into a core WorldState (a terrain fixture as LandPatch-shaped patches; a capture replayed
/// through the core), presented at a fixed 640x360 with a fixed camera for N frames, and written to &lt;out&gt;\&lt;tag&gt;.png with a
/// JSON sidecar (backend actually initialised, adapter, driver, fixture hash, timings, diff). With --tr-golden the image
/// is diffed against it with the declared tolerance (<see cref="DiffTolerance.Terrain"/>). --tr-alter=hill raises a hill
/// through the core (a terraform-style patch replacement) first: the negative control.
/// Measure: both representations (scene nodes, RenderingServer), alternating, frame time + draw calls.
/// Lifecycle: repeated region changes (1024 m -> 256 m ->...), resources must return to the baseline (no leaked RIDs).
/// Writes nothing anywhere but &lt;out&gt; (ADR-019). Exit 0 pass, 1 fail, 2 error, 3/4 backend refused (R-1 asserted).
/// </summary>
public partial class TestRender : Node
{
    public const int Width = 640, Height = 360;
    /// <summary>A path frame longer than this is a hitch (hitch_ms_total, hitch_frames).</summary>
    public const double HitchMs = 50;
    /// <summary>The camera path's render size (--tr-size=WxH, path runs only); goldens keep 640x360.</summary>
    private int _pathW = Width, _pathH = Height;
    private bool _settleCheck;   // the terrain settle check
    private string? _gcLatency;
    /// <summary>--tr-gc-events records each collection's generation and reason in path runs;
    /// --tr-gc-events=ticks also the runtime's allocation samples (GcEventRecorder; it allocates, so it is opt-in).</summary>
    private string? _gcEvents;

    private readonly Dictionary<string, object?> _result = new();
    private readonly List<string> _failures = new();
    private string _out = "", _tag = "render", _camera = "near", _sky = "";
    private bool _census;   // --tr-census: the layer's draws counted by kind and material
    private bool _frameProbe;   // --tr-frame-probe: a path run also records where each main-thread slice's time went
    private bool _busiest;   // --tr-busiest: a path run also records the draw calls of each frame and the pose of the frame with the most
    private const float ProjectorAmbient = 0.02f;   // the projector scene's ambient light
    private int _glowTimeFrames;   // --tr-glowtime=N: the glow scene timed over N frames with the pass on and off (--tr-size= sets the size)
    /// <summary>A test render's camera from --tr-look=ex,ey,ez,tx,ty,tz (region coordinates: the eye, then the point it
    /// looks at; 60 degrees), for review views of a scene at chosen distances; null: the fixture's own camera.</summary>
    private (System.Numerics.Vector3 Eye, System.Numerics.Vector3 Target)? _look;
    private int _renderW = Width, _renderH = Height;
    private string? _meshDir, _assetDir, _texDir;
    /// <summary>Also render a 2 x 4 contact sheet from 8 bearings around the scene (review aid, not a test).</summary>
    private bool _orbit;
    /// <summary>The fixed day fraction for test renders (--tr-day=, default 0.3).</summary>
    private string? _day;
    /// <summary>The time-of-day choice for a test render (--tr-timeofday=midday and the like); null keeps the fixed day fraction.</summary>
    private Viewer.Core.World.SkyChoice? _skyChoice;
    private bool _waterProbes;   // --tr-water-probes: open water below the horizon against the official water colour
    private bool _underwaterProbes;   // --tr-underwater-probes: from under open water, rays down and up against the official underwater colours
    private bool _underwaterOff;   // --tr-underwater=off: the below-surface view left out (the negative control of the underwater probes)
    private float? _underwaterGround;   // --tr-underwater-ground: the camera under the water, straight down at flat ground; its distance
    private bool _waterFlat;   // --tr-water-flat: the water without ripples, refraction offset or glint (the flat-water formula the probes check)
    private float _waterTime;   // --tr-water-time=<seconds>: added to the water's clock
    private (int X, int Y, int W, int H)? _waterLook;   // --tr-water-look=x,y,w,h: the picture's water rectangle to measure
    private Viewer.Core.World.PersonalLighting? _personal;   // --tr-personal=Field=value,...: Personal Lighting settings (field names as the window's, e.g. HazeDensity=2.5,SceneGamma=2)
    private bool _personalSkipApply;   // --tr-personal-skip-apply: the settings are only used for the expected colours (the probes must then fail)
    private string? _waterCompare;   // --tr-water-compare=<png>: another picture of the same view, to measure how much the water moved
    private bool _skyProbes;   // --tr-sky-probes: the picture's sky against the official colour at the elevation each probed row looks at
    private double? _maxClipped;   // --tr-max-clipped=: the most of a picture that may be pure white
    /// <summary>The lifecycle's water case (--tr-environment=&lt;LLSD file&gt;): every region gets a water height and this
    /// environment; --tr-alter=keepwater is its negative control.</summary>
    private string? _envFile, _lifecycleAlter, _lifecycleCharacter, _lifecycleAssets;
    /// <summary>Where the run is (&lt;tag&gt;.heartbeat in the output folder), so a run killed at its timeout still shows
    /// the step it was in. Measurement only.</summary>
    private RunHeartbeat? _beat;
    /// <summary>Test only (--tr-hang-test=&lt;step&gt;[:&lt;cycle&gt;]): the main thread stops for good when that step
    /// starts, so the tools' handling of a hung run (the dump and the heartbeat at the timeout) can be checked.</summary>
    private string? _hangAt;
    private bool _hung;
    /// <summary>Counts frames drawn against frames processed; a window that stops drawing while the loop runs ends the
    /// run with the "not drawing" result instead of waiting for the timeout. Null in a headless run (nothing is drawn).</summary>
    private DrawWatchdog? _watch;
    private bool _notDrawing;
    private bool _notDrawingDone;
    private long _notDrawingSince;
    /// <summary>True when the launcher (tools\godot.ps1) takes a dump of this process on request: the run then waits for
    /// it (bounded) before quitting.</summary>
    private bool _launcherDumps;
    /// <summary>Test only (--tr-minimize-at=&lt;seconds&gt;): the window is minimised that many seconds into the camera
    /// path, which stops the engine drawing, so the "not drawing" handling can be checked.</summary>
    private double _minimizeAt;
    /// <summary>Keeps a test window at the bottom of the stacking order and brings a minimised one back without focus (null when the
    /// run has no test window). A measuring loop asks it which frames to set aside.</summary>
    private TestWindow.Keeper? _keeper;
    /// <summary>An exit code the run ends with whatever its checks said (NOT_DRAWING: more than a tenth of a measuring run's frames were set aside).</summary>
    private int _exitOverride;
    /// <summary>Test only (--tr-leak-at-quit): the run makes one engine resource and never frees it, the negative control of the check at quit.</summary>
    private bool _leakAtQuit;
    private const double LauncherDumpWaitSeconds = 330;

    public override async void _Ready()
    {
        int exit;
        string[] args = CaseArgs ?? OS.GetCmdlineUserArgs();
        // --tr-serve=<folder>: this process runs the cases it is handed, one after another (TestRender.Serve.cs), instead of one case and quit
        if (CaseArgs is null && Arg(args, "--tr-serve=") is { Length: > 0 } serveFolder)
        {
            await ServeLoop(serveFolder, args);
            return;
        }
        _caseClock = Stopwatch.StartNew();
        _processAgeAtStartMs = ProcessAgeMs();
        try
        {
            string? outDir = Arg(args, "--tr-out=");
            if (string.IsNullOrWhiteSpace(outDir) || !Path.IsPathFullyQualified(outDir))
            {
                Console.Error.WriteLine("TEST-RENDER ERROR: no output directory. Run as: Reverie.exe [engine args] -- --test-render --tr-out=<absolute dir> ... Nothing was written.");
                GetTree().Quit(2);
                return;
            }
            _out = outDir;
            _tag = Arg(args, "--tr-tag=") is { Length: > 0 } t ? t : "render";
            _hangAt = Arg(args, "--tr-hang-test=");
            Directory.CreateDirectory(_out);
            _beat = new RunHeartbeat(Path.Combine(_out, _tag + ".heartbeat"));
            _beat.SetPhase("run");
            if (double.TryParse(Arg(args, "--tr-minimize-at="), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double minAt)) _minimizeAt = minAt;
            _launcherDumps = CaseArgs is null && System.Environment.GetEnvironmentVariable("NV_DUMP_BY_LAUNCHER") == "1";   // a served case is not watched by the launcher under its own tag
            _leakAtQuit = args.Contains("--tr-leak-at-quit");
            if (DisplayServer.GetName() != "headless")
            {
                if (TestWindow.FullSize(OS.GetCmdlineUserArgs()) is not null)   // the window is the process's: its size comes from the launch line, not from a served case's arguments
                    _keeper = new TestWindow.Keeper(Stopwatch.Frequency) { RestoreEnabled = !args.Contains("--tr-no-restore") };
                _watch = new DrawWatchdog(Stopwatch.Frequency);
                RenderingServer.Singleton.FramePostDraw += OnFrameDrawn;
            }
            if (Arg(args, CrashTest.Argument) is { Length: > 0 } crashKind) CrashTest.Run(crashKind);
            Mark("startup");
            Phase("start");
            CompileWatch.Active = new CompileWatch();
            PipelineWarmup.WithoutLightsForTest = Arg(args, "--tr-warmup=") == "nolights";
            exit = await Run(args);
            Phase("run done");
            if (_exitOverride != 0) exit = _exitOverride;
            FinishCompileWatch(args.Contains("--tr-compile-gate"), ref exit);
        }
        catch (Exception e)
        {
            _failures.Add("exception: " + e.GetType().Name + ": " + e.Message + " at " + (e.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim());
            exit = 2;
        }
        if (_leakAtQuit) Rids.MeshCreate("quit-check negative control");
        // the disposal queue, as the run left it, then emptied (nothing left to a finalizer)
        try
        {
            Mark("result");
            _result["variant_disposal_at_end"] = VariantDisposal.Report();
            VariantDisposal.DrainAll("test end");
            _result["variant_disposal"] = VariantDisposal.Report();
        }
        catch (Exception e) { _failures.Add("variant disposal report: " + e.GetType().Name + ": " + e.Message); }
        // The check at quit: the engine corrupts its heap when it quits with resources the viewer made still live, so
        // a leak fails the run here, naming what is live, instead of ending in a crash. What is found is freed.
        try
        {
            string live = Rids.CheckAtQuit(out int leaked);
            _result["live_at_quit"] = new { count = leaked, summary = live };
            if (leaked > 0)
            {
                _failures.Add("resources still live at quit: " + live);
                if (exit == 0) exit = 1;
            }
        }
        catch (Exception e) { _failures.Add("check at quit: " + e.GetType().Name + ": " + e.Message); if (exit == 0) exit = 2; }
        if (_beat is { } beat)
        {
            beat.SetPhase("done");
            beat.Dispose();   // written, and its writer stopped
            // what the heartbeat cost this run (the writer's own thread; the main thread only records and signals)
            _result["heartbeat"] = new { frames = beat.Frame, steps = beat.Steps, writes = beat.Writes, write_failures = beat.WriteFailures,
                write_ms_total = Math.Round(beat.WriteMsTotal, 3), write_ms_max = Math.Round(beat.WriteMsMax, 3) };
            _beat = null;
        }
        _result["window_os_visible"] = TestWindow.OsVisible();   // at the end of the run: whether the operating system shows the window
        if (_keeper is { } keeper) _result["window_keeper"] = new { minimized_seen = keeper.Minimized, marked_frames = keeper.MarkedFrames, restore = keeper.RestoreEnabled };
        _result["failures"] = _failures;
        _result["exit_code"] = exit;
        _result["verdict"] = exit switch { 0 => "PASS", 1 => "FAIL", 3 or 4 => "REFUSED", NotDrawingExit => "NOT_DRAWING", _ => "ERROR" };
        Phase("result");
        _result["phases"] = PhaseReport();
        if (_out.Length > 0)
        {
            Directory.CreateDirectory(_out);
            File.WriteAllText(Path.Combine(_out, _tag + ".json"), JsonSerializer.Serialize(_result, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.Error.WriteLine($"TEST-RENDER {_tag}: {_result["verdict"]} exit={exit}{(_failures.Count > 0 ? " " + string.Join(" | ", _failures) : "")}");
        if (_watch is not null) RenderingServer.Singleton.FramePostDraw -= OnFrameDrawn;
        if (Completed is { } done) done.TrySetResult(exit);   // a served case hands its exit code back to the serving loop; the process goes on
        else GetTree().Quit(exit);
    }

    /// <summary>Counts every frame into the heartbeat (it writes every <see cref="RunHeartbeat.FramesPerWrite"/>).</summary>
    public override void _Process(double delta)
    {
        _keeper?.Tick(Stopwatch.GetTimestamp());
        _beat?.CountFrame();
        if (_watch is null) return;
        if (_notDrawingDone) return;
        long now = Stopwatch.GetTimestamp();
        if (_notDrawing)
        {
            // stopped: wait for the launcher's dump (it writes <tag>.notdrawing.dumped when done), then quit
            if (Stopwatch.GetElapsedTime(_notDrawingSince, now).TotalSeconds >= LauncherDumpWaitSeconds
                || (_watch.Processed % 30 == 0 && File.Exists(Path.Combine(_out, _tag + ".notdrawing.dumped"))))
                FinishNotDrawing();
            _watch.FrameProcessed(now);
            return;
        }
        if (_watch.FrameProcessed(now)) StopNotDrawing(now);
    }

    private void OnFrameDrawn()
    {
        _watch?.FrameDrawn(Stopwatch.GetTimestamp());
        _beat?.CountDrawn();
        CompileWatch.Active?.Frame();
    }

    /// <summary>--tr-compile-gate: the run fails when the engine compiled, after the warm-up, a pipeline that a frame waits for (canvas, mesh,
    /// surface or draw source) or when a frame holding a background (specialization) compile was long. The watch itself runs in every
    /// run and adds its figures to the result as compile_watch.</summary>
    private void FinishCompileWatch(bool gate, ref int exit)
    {
        if (CompileWatch.Active is not { } w) return;
        _result["compile_watch"] = w.Report();
        if (!gate) return;
        if (w.WaitingCompilations > 0) _failures.Add($"{w.WaitingCompilations} pipelines were compiled after the warm-up in frames that wait for the compile");
        if (w.LongBackgroundFrames > 0) _failures.Add($"{w.LongBackgroundFrames} long frames held a background pipeline compile");
        if (exit == 0 && _failures.Count > 0) exit = 1;
    }

    /// <summary>The main window as the engine reports it: mode (minimised, windowed, ...), whether it can draw, focus,
    /// size, position and monitor.</summary>
    private string WindowState()
    {
        int id = (int)DisplayServer.MainWindowId;
        return $"mode={DisplayServer.WindowGetMode(id)} can_draw={DisplayServer.WindowCanDraw(id)} focused={DisplayServer.WindowIsFocused(id)} "
            + $"visible={GetTree().Root.Visible} size={DisplayServer.WindowGetSize(id).X}x{DisplayServer.WindowGetSize(id).Y} "
            + $"position={DisplayServer.WindowGetPosition(id).X},{DisplayServer.WindowGetPosition(id).Y} screen={DisplayServer.WindowGetCurrentScreen(id)}";
    }

    /// <summary>The loop runs but nothing is drawn: say so at once, with the window's state, instead of waiting for the
    /// run's timeout. A report file is written, and the launcher is asked for a full dump of this process.</summary>
    private void StopNotDrawing(long now)
    {
        _notDrawing = true;
        _notDrawingSince = now;
        string window = WindowState();
        _beat?.SetWindow(window);
        double age = _watch!.SecondsSinceDrawn(now, Stopwatch.Frequency);
        string message = $"not drawing: no frame drawn for {age:F1} s while the loop ran ({_watch.Processed} processed, {_watch.Drawn} drawn); window {window}";
        _failures.Add(message);
        _result["not_drawing"] = new { seconds_since_drawn = Math.Round(age, 1), processed = _watch.Processed, drawn = _watch.Drawn, window };
        Console.Error.WriteLine("TEST-RENDER " + _tag + ": " + message);
        try
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine(message);
            report.AppendLine("step: " + _beat?.Step + " (step " + _beat?.Steps + ")");
            report.AppendLine("--- heartbeat");
            report.AppendLine(_beat?.Text() ?? "(none)");
            report.AppendLine("--- main thread, where the watchdog ran");
            report.AppendLine(System.Environment.StackTrace);
            report.AppendLine(_launcherDumps ? "A full dump of this process is requested from the launcher (dump lines in the run's log)." : "No launcher dump: the process was not started by tools\\godot.ps1.");
            File.WriteAllText(Path.Combine(_out, _tag + ".notdrawing.txt"), report.ToString());
        }
        catch (Exception e) { Console.Error.WriteLine("TEST-RENDER " + _tag + ": could not write the not-drawing report: " + e.Message); }
        if (!_launcherDumps) FinishNotDrawing();
    }

    private void FinishNotDrawing()
    {
        _notDrawingDone = true;
        // as the normal end of a run: nothing is left to a finalizer when the engine shuts down
        try { VariantDisposal.DrainAll("test end"); }
        catch (Exception e) { _failures.Add("variant disposal drain: " + e.GetType().Name + ": " + e.Message); }
        if (_beat is { } beat) { beat.SetPhase("not drawing"); beat.Dispose(); _beat = null; }
        _result["failures"] = _failures;
        _result["exit_code"] = NotDrawingExit;
        _result["verdict"] = "NOT_DRAWING";
        try { File.WriteAllText(Path.Combine(_out, _tag + ".json"), JsonSerializer.Serialize(_result, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { Console.Error.WriteLine("TEST-RENDER " + _tag + ": could not write the sidecar: " + e.Message); }
        Console.Error.WriteLine($"TEST-RENDER {_tag}: NOT_DRAWING exit={NotDrawingExit}");
        // The result is written. The engine's own quit is not used: the path loop is abandoned mid-run with the world, the
        // rig and the decode workers live, and a quit from there ended the process in a crash during shutdown (an access
        // violation or heap corruption, 6 of 6 runs). The process ends at once with the run's exit code instead.
        Console.Error.Flush();
        if (OperatingSystem.IsWindows()) TerminateProcess(GetCurrentProcess(), NotDrawingExit);
        System.Environment.Exit(NotDrawingExit);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    /// <summary>
    /// --tr-hold-for-close: writes <c>&lt;tag&gt;.ready</c> (this process's id) and keeps presenting frames until the window is
    /// closed from outside, as the close button does; the engine then quits by its own path with the scene live, and
    /// <see cref="_ExitTree"/> frees what the viewer's own exit frees. A window nobody closes within
    /// <see cref="CloseWaitSeconds"/> fails the run.
    /// </summary>
    private async Task HoldForClose(WorldPresenter presenter, params Action[] afterwards)
    {
        _closeDispose = () => { presenter.Dispose(); foreach (var free in afterwards) free(); };
        File.WriteAllText(Path.Combine(_out, _tag + ".ready"), System.Environment.ProcessId.ToString());
        Console.Error.WriteLine($"TEST-RENDER {_tag}: scene loaded; waiting for the window to be closed");
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed.TotalSeconds < CloseWaitSeconds)
        {
            presenter.Frame();
            await Frames(1);
        }
        _closeDispose = null;
        _failures.Add($"the window was not closed within {CloseWaitSeconds:F0} s");
    }

    /// <summary>The viewer's own exit (<c>Main._ExitTree</c>) for a run held for its close: the presentation freed, nothing held left to a finalizer.</summary>
    public override void _ExitTree()
    {
        if (_closeDispose is not { } dispose) return;
        _closeDispose = null;
        try
        {
            dispose();
            VariantDisposal.DrainAll("shutdown");
            string live = Rids.CheckAtQuit(out int leaked);
            if (leaked > 0)
            {
                // the engine's quit that follows decides the exit code, so this run is marked by a line and a file the close-cycle driver reads
                Console.Error.WriteLine("TEST-RENDER " + _tag + ": resources still live at quit: " + live);
                File.WriteAllText(Path.Combine(_out, _tag + ".live-at-quit.txt"), live);
            }
        }
        catch (Exception e) { Console.Error.WriteLine("TEST-RENDER " + _tag + ": exit cleanup failed: " + e.GetType().Name + ": " + e.Message); }
    }

    /// <summary>The exit code of a run that stopped drawing (0 pass, 1 fail, 2 error, 3 and 4 refused).</summary>
    public const int NotDrawingExit = 5;

    private async Task<int> Run(string[] args)
    {
        // --tr-soak=<minutes> (TestRender.Soak.cs): the same cycle of scenes over and over in this process; each step is a run of its own arguments
        if (SoakOptions.Parse(args) is { } soakOptions) return await Soak(args, soakOptions);
        // R-1 on every render run: the backend that actually initialised must be approved and above the floor.
        Mark("backend check");
        var decision = StartupCheck.Run(Main.Log);
        _result["backend"] = new Dictionary<string, object?>
        {
            ["driver"] = RenderingServer.GetCurrentRenderingDriverName(),
            ["method"] = RenderingServer.GetCurrentRenderingMethod(),
            ["adapter"] = RenderingServer.GetVideoAdapterName(),
            ["api_version"] = RenderingServer.GetVideoAdapterApiVersion(),
            ["driver_info"] = OS.GetVideoAdapterDriverInfo(),
            ["floor"] = decision.Verdict.ToString(),
        };
        _result["godot_version"] = Engine.GetVersionInfo()["string"].AsString();
        Phase("backend checked");
        _result["window"] = Main.TestWindowInfo;
        if (!decision.Approved)
        {
            _failures.Add("backend refused: " + decision.Verdict);
            return decision.ExitCode;
        }

        // the draw census and the merge of still objects (PrimLayer.Merge.cs): --tr-census counts the layer's draws; --tr-merge forces merging on (it is on by default; --merge=off turns it off)
        // (--tr-merge-cell= metres, default 32; --tr-merge-still= seconds an object must be unchanged, default 1)
        _census = args.Contains("--tr-census");
        PrimLayer.CensusOn = _census;
        PrimLayer.CarryMoving = false;   // a picture must not depend on the clock: objects stay where the scene states them
        PrimLayer.MergeStill = args.Contains("--tr-merge") || Viewer.Core.Platform.MergeSetting.Resolve(args, null);   // on unless --merge=off; the user's settings file is not read in a test run
        if (Arg(args, "--tr-merge-cell=") is { } mergeCell) PrimLayer.MergeCellMetres = float.Parse(mergeCell, System.Globalization.CultureInfo.InvariantCulture);
        PrimLayer.MergeStillSeconds = double.Parse(Arg(args, "--tr-merge-still=") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        if (Arg(args, "--tr-neighbours=") is { } neighbourMode)
            return await NeighboursRun(neighbourMode == "on", Arg(args, "--tr-golden="), Arg(args, "--tr-water="), Arg(args, "--tr-water-plan=") != "off", Arg(args, "--tr-glow="), Arg(args, "--tr-ban="), Arg(args, "--tr-seam="));   // two regions side by side against a golden (TestRender.Neighbours.cs)
        if (Arg(args, "--tr-trees=") is { } treeMode)
            { _envFile = Arg(args, "--tr-environment="); return await TreesRun(treeMode is "on" or "missing", treeMode != "missing", Arg(args, "--tr-golden="), Arg(args, "--tr-sharedenv=")); }   // the built-in trees (TestRender.Trees.cs)
        if (Arg(args, "--tr-haze=") is { } hazeKind)
        {
            _envFile = Arg(args, "--tr-environment=");
            return await HazeRun(hazeKind);   // the official haze pass against its model (TestRender.Haze.cs)
        }
        if (Arg(args, "--tr-sharedtex=") is { } sharedMode)
            return await SharedTexturesRun(sharedMode == "shared", Arg(args, "--tr-fixture=") ?? throw new ArgumentException("--tr-sharedtex needs --tr-fixture=<a codestream file>"));   // one texture hub for two regions (TestRender.SharedTextures.cs)
        if (Arg(args, "--tr-sharedmeasure=") is { } measureCount)
            return await SharedMeasureRun(Arg(args, "--tr-fixture=") ?? throw new ArgumentException("--tr-sharedmeasure needs --tr-fixture=<a codestream file>"), int.Parse(measureCount));   // two regions' textures measured (TestRender.SharedMeasure.cs)
        if (Arg(args, "--tr-loginscreen=") is { } loginTable)
            return await LoginScreenRun(loginTable, Arg(args, "--tr-golden="));   // the login screen against its golden (TestRender.LoginScreen.cs)
        if (Arg(args, "--tr-logincard=") is { } loginCard)
            return await LoginCardRun(loginCard, Arg(args, "--tr-golden="), Arg(args, "--tr-alter=") == "unbounded");   // the login card at the test window's size (TestRender.LoginCard.cs)
        if (Arg(args, "--tr-ui=") is { } uiScene)
            return await UiSceneRun(uiScene, Arg(args, "--tr-theme=") ?? ThemePresets.Classic, Arg(args, "--tr-alter=") == "wrongtoken", Arg(args, "--tr-golden="));   // one interface scene against its golden (TestRender.Ui.cs)
        if (Arg(args, "--tr-handles=") is { } handles)
            return await HandlesRun(handles, Arg(args, "--tr-golden="), Arg(args, "--tr-alter="));   // the edit tools' handles and grid against their goldens (TestRender.Handles.cs)
        if (Arg(args, "--tr-uichrome=") is { } uiChrome)
            return await UiChromeRun(uiChrome, Arg(args, "--tr-golden="), Arg(args, "--tr-alter="), Arg(args, "--tr-pointer="));   // the in-world frame at an interface size in a window size (TestRender.UiChrome.cs)
        if (Arg(args, "--tr-floaterdrag=") is { } floaterDrag)
            return await FloaterDragRun(floaterDrag);   // the Inventory window resized, moved and closed by pointer events (TestRender.FloaterDrag.cs)
        if (Arg(args, "--tr-colourwindow=") is { } colourWindow)
            return await ColourWindowRun(colourWindow);   // the colour picker window's OK and Cancel in view and pressed by pointer events (TestRender.FloaterDrag.cs)
        if (Arg(args, "--tr-proportions=") is { } proportions)
            return await ProportionsRun(proportions, Arg(args, "--tr-alter="));   // a sphere and a cube drawn in the window as a circle and a square (TestRender.Proportions.cs)
        if (args.Contains("--tr-skinnormal"))
            return await SkinNormalRun(Arg(args, "--tr-alter="));   // a skinned normal through an unevenly scaled joint, against the official avatar shaders' rule (TestRender.SkinNormal.cs)
        if (Arg(args, "--tr-skinlight=") is { } skinKind)
            return await SkinLightRun(skinKind);   // flat skin patches on the avatar shader against the official classic lighting (TestRender.SkinLight.cs)
        if (Arg(args, "--tr-worldlight=") is { } worldKind)
        { _envFile = Arg(args, "--tr-environment="); return await WorldLightRun(worldKind); }   // flat patches on the world and avatar face shaders against the official lighting, classic and tone-mapped (TestRender.WorldLight.cs)
        if (Arg(args, "--tr-sundisc=") is { } sunKind)
            return await SunDiscRun(sunKind);   // the sun's disc drawn from the sky's sun picture against the official square (TestRender.SunDisc.cs)
        if (args.Contains("--tr-skycycle"))
            return await SkyCycleRun();   // the five skies applied four times (TestRender.SkyCycle.cs)
        if (Arg(args, "--tr-engineerrors") is { } errorsSpec)
            return await EngineErrorsCheck(errorsSpec);   // no scene needed
        if (Arg(args, "--tr-teardown=") is { } teardownFixture)
            return await TeardownRun(teardownFixture, int.Parse(Arg(args, "--tr-teardown-objects=") ?? "0"));   // the world torn down in the logout order, no engine errors (TestRender.Teardown.cs)
        if (args.Contains("--tr-primlod"))
            return await PrimLodRun();   // prims built at the level of detail their distance gives (TestRender.Surfaces.cs)
        if (Arg(args, "--tr-surfaces=") is { } surfacesTexture)
            return await SurfacesRun(surfacesTexture, int.Parse(Arg(args, "--tr-surfaces-n=") ?? "300"));   // a merged cell past the engine's surface limit (TestRender.Surfaces.cs)
        if (args.Contains("--tr-viewreuse"))
            return await ViewReuseRun();   // one terrain view, three presenters in turn (TestRender.ViewReuse.cs)
        if (Arg(args, "--tr-motions-off=") is { } off)   // test-only: built-in avatar motions left out (breathe, sway, eye, head, hands, pelvis, fly, or all), to see what each moves
            AvatarPresenter.TestMotionsOff = Viewer.Core.Avatars.DefaultMotionNames.Parse(off) ?? throw new ArgumentException("--tr-motions-off names head, eye, sway, breathe, pelvis, hands, fly or all");
        if (Arg(args, "--tr-avatars=") is { } avatarMode)
            return await AvatarRun(args, avatarMode);   // the avatar cases (TestRender.Avatars.cs)
        if (args.Contains("--tr-nametags-avatars"))
            return await NameTagAvatarsRender(args);   // tags over avatars the viewer draws (TestRender.NameTags.cs)
        if (args.Contains("--tr-nametags"))
            return await NameTagRender(args);   // name tags over stand-in avatars (TestRender.NameTags.cs)
        if (Arg(args, "--tr-blendstress=") is { } blendSpec)
            return await BlendStress(blendSpec, args);   // blended faces at scale (TestRender.BlendStress.cs)
        if (Arg(args, "--tr-busy=") is { } busySpec)
            return await BusyRun(busySpec, args);   // the synthetic busy view (TestRender.Busy.cs)
        string fixture = Arg(args, "--tr-fixture=") ?? throw new ArgumentException("--tr-fixture= is required");
        string viewKind = Arg(args, "--tr-view=") ?? "server";
        _camera = Arg(args, "--tr-camera=") ?? "near";
        _banLines = Arg(args, "--tr-banlines=");
        _interact = Arg(args, "--tr-interact=");
        ParseBanAgent(args);
        ParseDialog(args);
        _sky = Arg(args, "--tr-sky=") ?? "";   // the glow scene's sky: dark or bright
        if (Arg(args, "--tr-look=") is { } look)
        {
            var v = look.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (v.Length != 6) throw new ArgumentException("--tr-look=ex,ey,ez,tx,ty,tz (region coordinates)");
            _look = (new(v[0], v[1], v[2]), new(v[3], v[4], v[5]));
        }
        _meshDir = Arg(args, "--tr-meshes=");
        _assetDir = Arg(args, "--tr-assets=");
        _textureServer = Arg(args, "--tr-texture-server=");
        _textureDelayMs = int.Parse(Arg(args, "--tr-texture-delay-ms=") ?? "0");
        _firstRequest = Arg(args, "--tr-first-request=");
        // the shared asset store's bound for this run (MiB; default the viewer's), and a content store
        // (encrypted, in a new folder under the run's output folder) on the texture-server path, as the live viewer has one
        if (Arg(args, "--tr-asset-store-mib=") is { } sm) s_assetStoreBytes = long.Parse(sm) << 20;
        _contentStore = Arg(args, "--tr-content-store") is not null;
        // the PBR faces' legacy fallback (on by default; off: the control), and one texture whose fetch is held until the
        // rest of the scene has settled (render runs: the fallback is observed while it is held)
        _legacyFallback = Arg(args, "--tr-legacy-fallback=") != "off";
        s_holdTexture = Arg(args, "--tr-hold-texture=") is { } held ? Guid.Parse(held) : null;
        _texDir = Arg(args, "--tr-textures=");
        _orbit = Arg(args, "--tr-orbit=") is not null;
        // a camera path that goes out through its waypoints and comes back the same way (views it left, again)
        _returnPath = Arg(args, "--tr-return") is not null;
        // --tr-sky-sweep=<seconds>: during a camera path the time of day steps through every choice, that long on each (the region's own clock last)
        _skySweepSeconds = double.TryParse(Arg(args, "--tr-sky-sweep="), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sweepSeconds) ? sweepSeconds : 0;
        _holdForClose = Arg(args, "--tr-hold-for-close") is not null;
        _day = Arg(args, "--tr-day=");
        if (Arg(args, "--tr-timeofday=") is { } skyName)
            _skyChoice = Viewer.Core.World.SkyChoices.TryParse(skyName, out var skyParsed) ? skyParsed : throw new ArgumentException("--tr-timeofday= is not a time-of-day choice: " + skyName);
        _envFile = Arg(args, "--tr-environment=");
        _skyProbes = args.Contains("--tr-sky-probes");
        if (_skyProbes) EnvironmentLayer.DynamicExposure = false;   // the probes compare the sky's colour with the official chain at exposure 1; the automatic exposure has its own case (the haze one)
        _waterProbes = args.Contains("--tr-water-probes");
        _underwaterProbes = args.Contains("--tr-underwater-probes");
        _underwaterOff = Arg(args, "--tr-underwater=") == "off";
        _underwaterGround = args.Contains("--tr-underwater-ground") ? 0f : null;
        _waterFlat = args.Contains("--tr-water-flat");
        _waterTime = float.Parse(Arg(args, "--tr-water-time=") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        if (Arg(args, "--tr-water-look=") is { } wl)
        {
            var q = wl.Split(',').Select(int.Parse).ToArray();
            if (q.Length != 4) throw new ArgumentException("--tr-water-look=x,y,w,h (pixels)");
            _waterLook = (q[0], q[1], q[2], q[3]);
        }
        _waterCompare = Arg(args, "--tr-water-compare=");
        if (Arg(args, "--tr-personal=") is { } pl)
        {
            _personal = new Viewer.Core.World.PersonalLighting();
            foreach (var pair in pl.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=');
                if (kv.Length != 2 || !Enum.TryParse<Viewer.Core.World.PersonalField>(kv[0], true, out var pf)) throw new ArgumentException("--tr-personal=Field=value,... with the window's field names");
                _personal.SetValue(pf, double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        _personalSkipApply = args.Contains("--tr-personal-skip-apply");
        ParseCloudArgs(args);
        _maxClipped = Arg(args, "--tr-max-clipped=") is { } maxClippedText ? double.Parse(maxClippedText, System.Globalization.CultureInfo.InvariantCulture) : null;
        if (Arg(args, "--tr-budget=") is "default")
        {
            if (Arg(args, "--tr-path-seconds=") is null && Arg(args, "--tr-path=") is null)
                throw new ArgumentException("--tr-budget=default applies to camera path runs only (test renders keep a fixed budget)");
            long? vram = Arg(args, "--tr-vram-mib=") is { } sim ? long.Parse(sim) * Viewer.Core.Residency.TextureBudget.MiB : VideoMemory.Detect(RenderingServer.GetVideoAdapterName());
            _liveBudget = new Viewer.Core.Residency.LiveTextureBudget(vram, live: Arg(args, "--tr-budget-live=") != "off");
            _budget = _liveBudget.Budget;
            _result["video_memory_mib"] = vram is { } v ? v / Viewer.Core.Residency.TextureBudget.MiB : -1;
            _result["video_memory_simulated"] = Arg(args, "--tr-vram-mib=") is not null;
        }
        else if (Arg(args, "--tr-budget=") is { } budget) _budget = ParseBytes(budget);
        _evictCycles = int.Parse(Arg(args, "--tr-evict-cycles=") ?? "0");
        _legacyUpload = Arg(args, "--tr-legacy-upload") is not null;
        _part1Upload = Arg(args, "--tr-part1-upload") is not null;
        _meshMainThread = Arg(args, "--tr-mesh-main-thread") is not null;
        _settleCheck = Arg(args, "--tr-settle-check") is not null;   // one handover per chunk   /: the negative control of --tr-meshprep
        // every presenter builds its terrain chunks on the main thread, as before (the negative control
        // of --tr-terrainprep)
        WorldPresenter.TerrainOnMainThread = Arg(args, "--tr-terrain-main-thread") is not null;
        _result["terrain_on_main_thread"] = WorldPresenter.TerrainOnMainThread;
        // the settle window of every presenter (0: chunks start at once, the earlier behaviour; the
        // negative control of the harness's settle case)
        if (Arg(args, "--tr-terrain-settle-ms=") is { } settle) WorldPresenter.SettleWindowMs = double.Parse(settle, System.Globalization.CultureInfo.InvariantCulture);
        _prepAlter = Arg(args, "--tr-alter=") == "prepbyte";   // the byte check's negative control
        // measurement (test only): the mesh preparations' cap for every layer this run makes (a number, or unlimited)
        if (Arg(args, "--tr-mesh-prep-cap=") is { } cap) MeshLodPreparer.CapOverride = cap == "unlimited" ? 0 : int.Parse(cap);
        _result["mesh_prep_cap"] = MeshLodPreparer.CapOverride ?? MeshLodPreparer.MaxConcurrent;
        _gcLatency = Arg(args, "--tr-gc-latency=");
        _gcEvents = Arg(args, "--tr-gc-events=") ?? (Arg(args, "--tr-gc-events") is not null ? "on" : null);
        _glowTimeFrames = int.Parse(Arg(args, "--tr-glowtime=") ?? "0");
        if (Arg(args, "--tr-glowtimes") is not null) GlowPass.MeasureTimes = true;   // a path run also times the glow pass's viewports (the figures are in the result's glow section)
        if (_glowTimeFrames > 0) GlowPass.MeasureTimes = true;
        _busiest = Arg(args, "--tr-busiest") is not null;
        _frameProbe = Arg(args, "--tr-frame-probe") is not null;
        if (Arg(args, "--tr-glow-all") is not null) PrimLayer.TestGlowDrawAll = true;
        PrimLayer.TestLights = Arg(args, "--tr-lights=");   // projector scene: spot (default), omni, sun, moon or ambient lighting only; none = no lights at all (the probes' negative control)
        ProjectorLights.TestViewLayerOnly = Arg(args, "--tr-alter=") == "nolightglow";
        if (Arg(args, "--tr-shadows=") == "off") ProjectorLights.Shadows = false;   // the projector shadow setting off (the shadow probes' negative control)
        if (Arg(args, "--tr-automask=") == "off") PrimLayer.AutoMaskAlpha = new Viewer.Core.World.AutoMaskSettings(false, false);   // the automatic alpha mask settings off (the cut-out case's negative control)
        if (Arg(args, "--tr-glow-camera-bits=") is { } cb) GlowPass.TestCameraBits = uint.Parse(cb);   // test-only: every object drawn in the glow picture
        if (Arg(args, "--tr-glow-force=") is { } force)
        {
            // test-only: one face near the --tr-look target glows with this value, so a view with no glowing face can be timed with the pass drawn
            if (_look is not { } fl) throw new ArgumentException("--tr-glow-force= needs --tr-look=");
            PrimLayer.TestForceGlow = (new Vector3(fl.Target.X, fl.Target.Z, -fl.Target.Y), 12f, float.Parse(force, System.Globalization.CultureInfo.InvariantCulture));
        }
        if (Arg(args, "--tr-size=") is { } size)
        {
            if (Arg(args, "--tr-path-seconds=") is null && Arg(args, "--tr-path=") is null && _look is null && _glowTimeFrames == 0)
                throw new ArgumentException("--tr-size= applies to camera path runs, --tr-look views and --tr-glowtime runs only (goldens render at 640x360)");
            var wh = size.Split('x');
            _pathW = int.Parse(wh[0]); _pathH = int.Parse(wh[1]);
            if (_look is not null || _glowTimeFrames > 0) (_renderW, _renderH) = (_pathW, _pathH);
            if (_pathW < 64 || _pathH < 64 || _pathW > 7680 || _pathH > 4320) throw new ArgumentException("--tr-size= out of range (64x64 to 7680x4320)");
        }
        if (Arg(args, "--tr-uploadbench=") is { } repeats)
            return await UploadBench(int.Parse(repeats));
        if (Arg(args, "--tr-meshlod") is not null)
            return await MeshLodCheck(fixture);
        if (Arg(args, "--tr-meshprep") is not null)
            return await MeshPrepCheck(fixture);
        if (Arg(args, "--tr-terrainprep") is not null)
            return await TerrainPrepCheck(fixture);
        if (Arg(args, "--tr-decodedbound") is { } boundSpec)
            return await DecodedBoundCheck(fixture, boundOff: boundSpec == "=off");
        if (Arg(args, "--tr-surfacestress=") is { } stressSeconds)
            return await SurfaceStress(double.Parse(stressSeconds, System.Globalization.CultureInfo.InvariantCulture));
        if (Arg(args, "--tr-handoverstress=") is { } handoverSpec)
            return await HandoverStress(handoverSpec);
        if (Arg(args, "--tr-disposestress=") is { } disposeSpec)
            return await DisposeStress(disposeSpec);
        if (Arg(args, "--tr-keptcollections") is { } keptSpec)
            return await KeptCollectionsCheck(keptSpec);
        if (Arg(args, "--tr-readback") is { } rb)
            return await ReadbackCheck(fixture, contentStore: rb != "=nostore");
        if (Arg(args, "--tr-mipcheck") is not null)
            return MipCheck();
        // path runs warm the pipelines first unless --tr-warmup=off; without a path, --tr-warmup[=off]
        // is the warm-up check itself (--tr-warmup-scenes= lists the scenes it presents, ';'-separated absolute paths)
        _warmupOff = Arg(args, "--tr-warmup=") == "off";
        _pacingGuard = Arg(args, "--tr-pacing-guard") is not null;
        _warmupTwice = Arg(args, "--tr-warmup=") == "twice";
        PipelineWarmup.LeakOneForTest = Arg(args, "--tr-warmup=") == "leak";   // the end state's negative control
        if (Arg(args, "--tr-warmup") is not null && Arg(args, "--tr-path-seconds=") is null && Arg(args, "--tr-path=") is null)
            return await WarmupCheck(fixture, Arg(args, "--tr-warmup-scenes="), warm: !_warmupOff);
        _result["texture_budget_bytes"] = _budget;
        _lifecycleAlter = Arg(args, "--tr-alter=");
        _lifecycleGc = Arg(args, "--tr-lifecycle-gc=");
        _lifecycleDetails = Arg(args, "--tr-lifecycle-details") is not null;
        _lifecycleCharacter = Arg(args, "--tr-character=");   // avatars in every region too (local-only character data)
        _lifecycleAssets = Arg(args, "--tr-assets=");
        if (Arg(args, "--tr-lifecycle=") is { } cycles)
            return await Lifecycle(int.Parse(cycles), viewKind);
        if (Arg(args, "--tr-measure=") is { } frames)
            return await Measure(fixture, int.Parse(frames));
        if (Arg(args, "--tr-path-seconds=") is { } pathSeconds)
            return await CameraPath(fixture, 0, int.Parse(Arg(args, "--tr-seed=") ?? "8"), double.Parse(pathSeconds, System.Globalization.CultureInfo.InvariantCulture));
        if (Arg(args, "--tr-path=") is { } pathFrames)
            return await CameraPath(fixture, int.Parse(pathFrames), int.Parse(Arg(args, "--tr-seed=") ?? "8"));
        if (fixture.EndsWith(".orientation.json", StringComparison.OrdinalIgnoreCase))
            return await Orientation(fixture);
        return await Render(fixture, viewKind, int.Parse(Arg(args, "--tr-frames=") ?? "8"), Arg(args, "--tr-golden="), Arg(args, "--tr-alter="));
    }

    // ---------------- fixtures into the core ----------------

    private sealed class Loaded(WorldState world, IDisposable? owner, string description, string sha)
    {
        public WorldState World { get; } = world;
        public string Description { get; } = description;
        public string Sha256 { get; } = sha;
        /// <summary>The agent id of the capture's own avatar (a replayed capture only).</summary>
        public Guid SelfAgent { get; init; }
        /// <summary>The fixture's meshes (mesh zoo), fetched through SessionAssets and decoded by the guarded decoder.</summary>
        public MeshSource? Meshes { get; init; }
        /// <summary>The fixture's textures (texture zoo), fetched through SessionAssets and decoded by the guarded decoder.</summary>
        public TextureSource? Textures { get; init; }
        /// <summary>The fixture's PBR material assets (material zoo), read from its files.</summary>
        public MaterialSource? MaterialAssets { get; init; }
        /// <summary>A scene fixture's fixed time of day.</summary>
        public double? DayFraction { get; init; }
        /// <summary>Where a fixture's terrain detail textures come from (synthetic fixture).</summary>
        public TextureSource? TerrainTextures { get; init; }
        /// <summary>The colour-accuracy fixture (its own camera and checks).</summary>
        public bool IsColourChart { get; init; }
        public void Dispose() { Textures?.Dispose(); TerrainTextures?.Dispose(); Meshes?.Dispose(); MaterialAssets?.Dispose(); owner?.Dispose(); if (owner is null) World.Dispose(); }
    }

    private static Loaded Load(string fixture)
    {
        if (fixture == HeavyScene.FixtureName)
        {
            // the synthetic heavy scene; its six base images are generated and encoded in memory (nothing written)
            var bases = Enumerable.Range(0, HeavyScene.Bases).Select(b => { int n = HeavyScene.SizeOfBase(b); return LmvTexture.Encode(n, n, HeavyScene.BasePixels(b), false); }).ToArray();
            var z = FromZoo(HeavyScene.Make(), $"synthetic heavy scene: {HeavyScene.ObjectCount} objects, {HeavyScene.TextureCount} texture ids, {HeavyScene.FullResolutionBytes / 1048576} MiB at full resolution", "generated");
            var texSource = new TextureSource(new SessionAssets(SharedAssets, _ => true, (id, _) =>
            {
                for (int k = 0; k < HeavyScene.TextureCount; k++)
                    if (HeavyScene.TextureId(k) == id) return Task.FromResult(AssetFetchResult.Ok(bases[HeavyScene.BaseOf(k)]));
                return Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.NotFound));
            })) { Timeline = new TextureTimeline() };   // decode times for the path's process report (timestamps only)
            return new Loaded(z.World, null, z.Description, z.Sha256) { Textures = texSource };
        }
        if (fixture.StartsWith("synthetic-", StringComparison.Ordinal))
        {
            int size = int.Parse(fixture["synthetic-".Length..]);
            return FromTerrain(SyntheticTerrain.Make(fixture, size, size), "generated " + fixture, "generated");
        }
        if (!Path.IsPathFullyQualified(fixture) || !File.Exists(fixture))
            throw new FileNotFoundException("fixture not found (absolute path required)");
        var bytes = File.ReadAllBytes(fixture);
        if (fixture.EndsWith(".texzoo.json", StringComparison.OrdinalIgnoreCase))
        {
            var (prims, images, meshes) = TextureZoo.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            string dir = Path.GetDirectoryName(fixture)!;
            var z = FromZoo(prims, "texture zoo " + Path.GetFileName(fixture), TerrainFixture.Sha256(bytes));
            // a scene with water (the layered-transparency water scene): the region's water height and a fixed time of day
            var zj = System.Text.Json.Nodes.JsonNode.Parse(bytes)!.AsObject();
            if (zj["water_height"] is { } wh) z.World.BeginRegion(FixtureSim, z.World.Region! with { WaterHeight = (float)wh });
            return new Loaded(z.World, null, z.Description, z.Sha256)
            {
                Meshes = MeshSourceFor(dir, meshes), Textures = TextureSourceFor(dir, images), DayFraction = zj["day_fraction"] is { } df ? (double)df! : null,
            };
        }
        if (fixture.EndsWith(".matzoo.json", StringComparison.OrdinalIgnoreCase))
        {
            // the material zoo: its prims, then each object's PBR material ids and glTF overrides through the core, as the
            // grid delivers them (--tr-alter=override leaves the overrides out: the negative control)
            var mz = MaterialZoo.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            string dir = Path.GetDirectoryName(fixture)!;
            var z = FromZoo(mz.Prims, "material zoo " + Path.GetFileName(fixture), TerrainFixture.Sha256(bytes));
            var images = mz.Images;
            if (s_alterFallbackUnshared)
            {
                // the override box's legacy texture: an image under an id no other face uses
                var o = z.World.Snapshot().Objects.Values.First(x => x.Id.LocalId == 6008);
                var t = o.Textures!.Default;
                z.World.ApplyObject(FixtureSim, new PrimData(o.Id.LocalId, o.FullId, o.ParentLocalId, 0, o.Position, o.Rotation, o.Scale, Shape: o.Shape, Colors: o.Colors,
                    Textures: new FaceTextures(t with { TextureId = UnsharedLegacy })));
                images = new Dictionary<Guid, string>(mz.Images) { [UnsharedLegacy] = mz.Images[t.TextureId] };
            }
            // each object's legacy material id through its texture entry (every face), then the region's legacy materials
            // as the RenderMaterials answer delivers them (--tr-alter=nolegacy leaves the answer out: the negative control)
            foreach (var (local, id) in mz.FaceMaterials)
            {
                var o = z.World.Snapshot().Objects.Values.First(x => x.Id.LocalId == local);
                z.World.ApplyObject(FixtureSim, new PrimData(o.Id.LocalId, o.FullId, o.ParentLocalId, 0, o.Position, o.Rotation, o.Scale, Shape: o.Shape, Colors: o.Colors,
                    Textures: o.Textures, Materials: new FaceMaterials(new FaceMaterial(0, 0, id, Guid.Empty))));
            }
            if (!s_alterNoLegacy && mz.LegacyMaterials.Count > 0) z.World.ApplyLegacyMaterials(FixtureSim, mz.LegacyMaterials);
            foreach (var (local, faces) in mz.RenderMaterials) z.World.ApplyRenderMaterials(FixtureSim, local, faces);
            if (!s_alterOverride)
                foreach (var (local, faces) in mz.Overrides) z.World.ApplyMaterialOverrides(FixtureSim, local, faces);
            return new Loaded(z.World, null, z.Description, z.Sha256)
            {
                Meshes = MeshSourceFor(dir, mz.Meshes), Textures = TextureSourceFor(dir, images), MaterialAssets = MaterialSourceFor(dir, mz.Materials),
            };
        }
        if (fixture.EndsWith(".scene.json", StringComparison.OrdinalIgnoreCase))
        {
            // a terrain fixture plus a water height, an environment (LLSD) and a fixed day fraction
            var j = System.Text.Json.Nodes.JsonNode.Parse(bytes)!.AsObject();
            if ((string?)j["format"] != "nv-scene-1") throw new InvalidDataException("scene: unknown format");
            string dir = Path.GetDirectoryName(fixture)!;
            string terrainPath = Path.GetFullPath(Path.Combine(dir, (string)j["terrain"]!));
            var baseScene = Load(terrainPath);
            float water = (float)j["water_height"]! + (s_alterWater ? 3f : 0f);
            string? llsd = j["environment"] is { } ef ? File.ReadAllText(Path.Combine(dir, (string)ef!)) : null;
            var region = baseScene.World.Region! with { WaterHeight = water, EnvironmentLlsd = llsd };
            baseScene.World.BeginRegion(FixtureSim, region);
            var t = TerrainFixture.Parse(File.ReadAllBytes(terrainPath));
            foreach (var (x, y, h) in t.Patches())
                baseScene.World.ApplyPatch(FixtureSim, x, y, t.PatchSize, h);
            return new Loaded(baseScene.World, null, "scene " + Path.GetFileName(fixture), TerrainFixture.Sha256(bytes))
            {
                TerrainTextures = baseScene.TerrainTextures, DayFraction = j["day_fraction"] is { } df ? (double)df! : null,
            };
        }
        if (fixture.EndsWith(".colourchart.json", StringComparison.OrdinalIgnoreCase))
        {
            var (prims, images) = ColourChart.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            var z = FromZoo(prims, "colour chart " + Path.GetFileName(fixture), TerrainFixture.Sha256(bytes));
            return new Loaded(z.World, null, z.Description, z.Sha256) { Textures = TextureSourceFor(Path.GetDirectoryName(fixture)!, images), IsColourChart = true };
        }
        if (fixture.EndsWith(".sculptzoo.json", StringComparison.OrdinalIgnoreCase))
        {
            var (prims, images) = SculptZoo.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            var z = FromZoo(prims, "sculpt zoo " + Path.GetFileName(fixture), TerrainFixture.Sha256(bytes));
            return new Loaded(z.World, null, z.Description, z.Sha256) { Textures = TextureSourceFor(Path.GetDirectoryName(fixture)!, images) };
        }
        if (fixture.EndsWith(".meshzoo.json", StringComparison.OrdinalIgnoreCase))
        {
            var (prims, assets) = MeshZoo.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            var z = FromZoo(prims, "mesh zoo " + Path.GetFileName(fixture), TerrainFixture.Sha256(bytes));
            return new Loaded(z.World, null, z.Description, z.Sha256) { Meshes = MeshSourceFor(Path.GetDirectoryName(fixture)!, assets) };
        }
        if (fixture.EndsWith(".primzoo.json", StringComparison.OrdinalIgnoreCase))
            return FromZoo(PrimZoo.Parse(System.Text.Encoding.UTF8.GetString(bytes)), "prim zoo " + Path.GetFileName(fixture), TerrainFixture.Sha256(bytes));
        if (fixture.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase))
        {
            var session = ReplayRunner.Load(CaptureFile.Read(fixture));
            return new Loaded(session.World, session, "capture " + Path.GetFileName(fixture), TerrainFixture.Sha256(bytes)) { SelfAgent = session.AgentId };
        }
        var terrainFixture = FromTerrain(TerrainFixture.Parse(bytes), "terrain " + Path.GetFileName(fixture), TerrainFixture.Sha256(bytes));
        if (!Path.GetFileName(fixture).StartsWith("synthetic-", StringComparison.OrdinalIgnoreCase))
            return terrainFixture;
        // the synthetic fixture carries terrain settings and committed synthetic detail textures
        terrainFixture.World.BeginRegion(FixtureSim, terrainFixture.World.Region! with { Terrain = TerrainDetails.SyntheticTerrain });
        var t2 = TerrainFixture.Parse(bytes);
        foreach (var (x, y, h) in t2.Patches())
            terrainFixture.World.ApplyPatch(FixtureSim, x, y, t2.PatchSize, h);
        return new Loaded(terrainFixture.World, null, terrainFixture.Description, terrainFixture.Sha256) { TerrainTextures = TextureSourceFor(Path.GetDirectoryName(fixture)!, TerrainDetails.SyntheticFiles) };
    }

    private static readonly object FixtureSim = new();

    private static Loaded FromTerrain(TerrainFixture t, string description, string sha)
    {
        var world = new WorldState(new CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new RegionInfo("fixture", 1000, 1000, t.SizeX, t.SizeY, SizeAuthoritative: true));
        foreach (var (x, y, h) in t.Patches())
            world.ApplyPatch(FixtureSim, x, y, t.PatchSize, h);
        return new Loaded(world, null, description, sha);
    }

    /// <summary>A zoo fixture as ObjectUpdates into a core WorldState (256 m region, no terrain).</summary>
    private static Loaded FromZoo(IReadOnlyList<ZooPrim> zoo, string description, string sha)
    {
        var world = new WorldState(new CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new RegionInfo("zoo", 1000, 1000, 256, 256, SizeAuthoritative: true));
        foreach (var p in PrimZoo.AsPrimData(zoo))
            world.ApplyObject(FixtureSim, p);
        return new Loaded(world, null, description, sha);
    }

    /// <summary>One prim parameter changed (box-twist's twist 90 -> 45 degrees), delivered through the core.</summary>
    private static void AlterPrim(WorldState world)
    {
        var o = world.Snapshot().Objects.Values.First(x => x.Shape is { PathTwist: 0.5, ProfileCurve: 1 });
        world.ApplyObject(FixtureSim, new PrimData(o.Id.LocalId, o.FullId, o.ParentLocalId, 0, o.Position, o.Rotation, o.Scale, Shape: o.Shape! with { PathTwist = 0.25 }, Colors: o.Colors));
    }

    /// <summary>Negative control (--tr-alter=vertex): one vertex of the cube's high LOD (its top face's centre) raised 1 m.</summary>
    private static bool s_alterVertex;

    /// <summary>A mesh source over asset files in <paramref name="dir"/>: bytes through a SessionAssets (authorised, per session),
    /// decoded by the adapter's guarded decoder, exactly the live path minus the network.</summary>
    internal static MeshSource MeshSourceFor(string dir, IReadOnlyDictionary<Guid, string> assets, ContentView? content = null)
    {
        Func<Guid, CancellationToken, Task<AssetFetchResult>> fetch = (id, _) => Task.FromResult(
            s_meshFetches.AddOrUpdate(id, 1, (_, n) => n + 1) > 0 && assets.TryGetValue(id, out var f) && File.Exists(Path.Combine(dir, f)) ? AssetFetchResult.Ok(File.ReadAllBytes(Path.Combine(dir, f))) : AssetFetchResult.Of(AssetFetchStatus.NotFound));
        if (content is not null) fetch = content.Caching("mesh", fetch);   // the content store first, as live
        var sessionAssets = new SessionAssets(SharedAssets, _ => true, fetch);
        return new MeshSource(new SessionMeshes(sessionAssets, (id, b) =>
        {
            var r = LmvMesh.Decode(id, b);
            if (s_alterVertex && r.Geometry?.Lods[3] is { } high && id == MeshZoo.Assets.Keys.First())
            {
                var top = high.Faces[0];
                int best = 0;
                for (int i = 1; i < top.Vertices; i++)
                    if (Math.Abs(top.Positions[i * 3]) + Math.Abs(top.Positions[i * 3 + 1]) < Math.Abs(top.Positions[best * 3]) + Math.Abs(top.Positions[best * 3 + 1]))
                        best = i;
                top.Positions[best * 3 + 2] += 1.0f;
            }
            return (r.Geometry, r.Refusal);
        }));
    }

    /// <summary>Textures from the local-only asset cache, through SessionAssets and the guarded decoder.
    /// The same cache served over HTTP by a local test server on loopback
    /// (127.0.0.1, an ephemeral port), fetched through the real fetch path: partial (PartialTextureFetcher, range requests)
    /// or whole (one bounded GET per texture, the whole-file control).
    /// Requests go through the session's texture client (TextureHttp: at most 8 in flight, as live)
    /// and are timed into a TextureTimeline (requests before each texture's first image); --tr-texture-delay-ms=N makes
    /// the server wait N ms before every answer (the grid's ~105 ms); --tr-first-request= picks the first-request
    /// rule measured (chunk: 600 bytes; level1024: the level's bytes for a 1024x1024 texture; &lt;n&gt;k: a fixed size).</summary>
    private TextureSource CacheTextureSource(string dir)
    {
        var cache = new LocalAssetCache(dir);
        if (_textureServer is null)
            return new TextureSource(new SessionAssets(SharedAssets, _ => true, cache.FetchTextureAsync));
        if (_textureServer is not ("partial" or "whole")) throw new ArgumentException("--tr-texture-server=partial|whole");
        _server = new LoopbackTextureServer(id => cache.HasTexture(id) ? File.ReadAllBytes(cache.TexturePath(id)) : null)
        {
            ResponseDelay = TimeSpan.FromMilliseconds(_textureDelayMs),
        };
        _http = new Viewer.Core.Net.TextureHttp(new Viewer.Core.Net.OutboundPolicy(new Viewer.Core.Net.OutboundOptions()), new CoreLog(new DiscardSink(), "test-render"));
        _timeline = new TextureTimeline();
        var server = _server;
        var http = _http;
        var timeline = _timeline;
        int cap = Viewer.Core.Imaging.J2kLimits.Default.MaxBytes;
        // each texture's fetch class, written by the residency manager: the order its requests wait in
        var classes = new Viewer.Core.Net.TextureFetchClasses();
        Func<Guid, CancellationToken, Task<AssetFetchResult>> whole = (id, ct) =>
            http.WholeAsync(server.UriFor(id), cap, ct, () => timeline.BeginRequest(id, "whole"), classes.Of(id));
        _serverCache = cache;
        if (_contentStore)
        {
            var view = RunContentView();
            var network = whole;
            whole = async (id, ct) =>
            {
                if (view.TryGet("texture", id, out var hit)) { Interlocked.Increment(ref _storeReads); return AssetFetchResult.Ok(hit); }
                var r = await network(id, ct).ConfigureAwait(false);
                if (r.Status == AssetFetchStatus.Ok) view.Put("texture", id, r.Bytes.Span);
                return r;
            };
        }
        _fetcher = _textureServer == "partial"
            ? new PartialTextureFetcher((id, from, to, c, ct) => http.RangeAsync(server.UriFor(id), from, to, c, ct, () => timeline.BeginRequest(id, "range"), classes.Of(id)),
                whole, _contentView, firstRequest: FirstRequestRule(_firstRequest))
            : null;
        return new TextureSource(new SessionAssets(SharedAssets, _ => true, whole, fetchLevel: _fetcher is { } f ? f.FetchLevelAsync : null)) { Timeline = timeline, FetchClasses = classes };
    }

    /// <summary>The first-request rules the harness measures (null: the shipped rule).</summary>
    private static Func<int, long?>? FirstRequestRule(string? name) => name switch
    {
        null or "" or "default" => null,
        "chunk" => level => level <= 0 ? null : Viewer.Core.Imaging.TextureByteRange.FirstChunk,
        "level1024" => level => Viewer.Core.Imaging.TextureByteRange.BytesFor(1024, 1024, level),
        _ when name.EndsWith('k') && int.TryParse(name[..^1], out int k) && k > 0 => level => level <= 0 ? null : k * 1024L,
        _ => throw new ArgumentException("--tr-first-request=default|chunk|level1024|<n>k"),
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> s_meshFetches = new();
    private bool _returnPath;
    /// <summary>Test only (--tr-hold-for-close): a camera path run that, once its scene is loaded and settled, keeps drawing until
    /// the window is closed from outside (what the close button does) and then lets the engine quit with the scene live.</summary>
    private bool _holdForClose;
    /// <summary>Seconds each time-of-day choice stays during a camera path (--tr-sky-sweep=); 0: the fixed day fraction, as before.</summary>
    private double _skySweepSeconds;
    private Action? _closeDispose;
    private const double CloseWaitSeconds = 120;
    private string? _textureServer;
    private int _textureDelayMs;
    private string? _firstRequest;
    private bool _contentStore;
    private ContentView? _contentView;
    /// <summary>The run's content store view (--tr-content-store), made once in the run's own output folder
    /// (ADR-019), for its textures and, as live, its meshes.</summary>
    private ContentView RunContentView() => _contentView ??= new ContentStore(new ContentStoreOptions { Root = Path.Combine(_out, _tag + "-content-" + Guid.NewGuid().ToString("N")) }).View("texture-server", "tr");
    private LocalAssetCache? _serverCache;
    private long _storeReads;
    private static long? s_assetStoreBytes;
    private static SharedAssetStore? s_sharedAssets;

    /// <summary>One shared asset store for the whole run, for every scene's textures and meshes, as the
    /// live viewer has one for the process (it survives region changes).</summary>
    private static SharedAssetStore SharedAssets => s_sharedAssets ??= new SharedAssetStore(s_assetStoreBytes);
    private TextureTimeline? _timeline;
    private LoopbackTextureServer? _server;
    private Viewer.Core.Net.TextureHttp? _http;
    private PartialTextureFetcher? _fetcher;

    /// <summary>The resident texture bytes in view (a user in the residency camera's frustum), out of view, and
    /// pinned (the terrain's detail layers: counted against the budget with no users and no levels).</summary>
    private static (long InView, long OutOfView, long Pinned) ResidentSplit(Viewer.Core.Residency.TextureResidency res)
    {
        long inView = 0, outOfView = 0, pinned = 0;
        foreach (var id in res.All)
        {
            if (res.StateOf(id) != Viewer.Core.Residency.ResidencyState.Resident) continue;
            long b = res.BytesOf(id);
            if (res.LevelsOf(id) == 0) pinned += b;
            else if (res.IsVisible(id)) inView += b;
            else outOfView += b;
        }
        return (inView, outOfView, pinned);
    }

    private static object SplitReport((long InView, long OutOfView, long Pinned) s) =>
        new { in_view_bytes = s.InView, out_of_view_bytes = s.OutOfView, pinned_bytes = s.Pinned };

    /// <summary>The textures with levels (not pinned) against the level their size on screen wants, in view and out of
    /// view: resident at or finer than wanted, coarser (and by how many levels in all), on a placeholder; the wanted and
    /// resident levels' counts (measurement).</summary>
    private static object LevelsAgainstWanted(Viewer.Core.Residency.TextureResidency res)
    {
        object Of(bool inView)
        {
            var ids = res.All.Where(id => res.LevelsOf(id) > 0 && res.IsVisible(id) == inView && !res.HasFailed(id)).ToList();
            var resident = ids.Where(id => res.StateOf(id) == Viewer.Core.Residency.ResidencyState.Resident).ToList();
            return new
            {
                textures = ids.Count, resident = resident.Count,
                at_or_finer = resident.Count(id => res.LevelOf(id) <= res.DesiredLevelOf(id)),
                coarser = resident.Count(id => res.LevelOf(id) > res.DesiredLevelOf(id)),
                levels_short = resident.Sum(id => System.Math.Max(0, res.LevelOf(id) - res.DesiredLevelOf(id))),
                placeholders = ids.Count - resident.Count,
                wanted = ids.GroupBy(res.DesiredLevelOf).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                resident_levels = resident.GroupBy(res.LevelOf).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            };
        }
        return new { in_view = Of(true), out_of_view = Of(false) };
    }

    /// <summary>The mesh objects at each LOD, the triangles the mesh and sculpt objects draw, and the mesh LODs' bytes by
    /// LOD (measurement).</summary>
    private static object MeshDetail(PrimLayer prims) => new
    {
        objects_by_lod = prims.MeshObjectsByLod, mesh_triangles = prims.MeshTrianglesDrawn, sculpt_triangles = prims.SculptTrianglesDrawn,
        bytes_by_lod = prims.MeshBytesByLod, lod_factor = prims.LodFactor,
    };

    /// <summary>The resident textures, each with its level and bytes.</summary>
    private static object[] ResidentLevels(Viewer.Core.Residency.TextureResidency res) =>
        res.All.Where(id => res.StateOf(id) == Viewer.Core.Residency.ResidencyState.Resident).OrderBy(id => id)
            .Select(id => (object)new { id = id.ToString(), level = res.LevelOf(id), bytes = res.BytesOf(id) }).ToArray();

    /// <summary>Each texture's first image as the user sees it, timed from the path's start
    /// (not from its own request): the median and p90 of the first uploads, and the time until 90 % of the textures asked
    /// for have one (null when fewer ever do); the same for the first decoded image; the first uploads of the detail maps
    /// alone (PBR normal, metallic-roughness and emissive textures fetched after base colours); and every texture's times.</summary>
    private static object FirstImageFromStart(TextureTimeline timeline, long pathStart, Func<Guid, bool>? isDetail = null)
    {
        var all = timeline.Since(pathStart);
        isDetail ??= _ => false;
        var details = all.Where(x => isDetail(x.Id)).ToList();
        object Of(Func<(Guid Id, double? AskedMs, double? DecodedMs, double? UploadedMs), double?> stage, IReadOnlyList<(Guid Id, double? AskedMs, double? DecodedMs, double? UploadedMs)>? set = null)
        {
            set ??= all;
            var v = set.Select(stage).Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x).ToArray();
            int need = (int)Math.Ceiling(0.9 * set.Count);
            double Rank(double p) => v[Math.Clamp((int)Math.Ceiling(p * v.Length) - 1, 0, v.Length - 1)];
            return new
            {
                n = v.Length, p50 = v.Length == 0 ? (double?)null : Math.Round(Rank(0.5), 1), p90 = v.Length == 0 ? (double?)null : Math.Round(Rank(0.9), 1),
                at_90pct_of_asked = need > 0 && v.Length >= need ? Math.Round(v[need - 1], 1) : (double?)null,
            };
        }
        return new
        {
            asked = all.Count(x => x.AskedMs.HasValue), on_screen = Of(x => x.UploadedMs), decoded = Of(x => x.DecodedMs),
            detail_maps_on_screen = Of(x => x.UploadedMs, details),
            per_texture = all.OrderBy(x => x.Id).Select(x => new { id = x.Id.ToString(), asked = R(x.AskedMs), decoded = R(x.DecodedMs), on_screen = R(x.UploadedMs), detail = isDetail(x.Id) }).ToArray(),
        };
        static double? R(double? v) => v is { } x ? Math.Round(x, 1) : null;
    }

    /// <summary>Each face's first image as the user sees it, timed from the path's start: the faces in view during the
    /// path, how many showed their own image, the median and p90 of those times, the time until 90 % of the faces in view
    /// showed theirs (null when fewer ever did), and every face's times.</summary>
    private static object FaceImagesReport(Viewer.Core.Residency.FaceFirstImageSummary s)
    {
        static double? R(double? v) => v is { } x ? Math.Round(x, 1) : null;
        return new
        {
            in_view = s.InView, shown = s.Shown, p50 = R(s.P50), p90 = R(s.P90), at_90pct_of_in_view = R(s.At90PctOfInView),
            textures = s.Faces.Select(x => x.Texture).Distinct().Count(),
            per_face = s.Faces.Select(x => new { obj = x.Object, face = x.Face, texture = x.Texture.ToString(), in_view = R(x.InViewMs), on_screen = R(x.ImageMs) }).ToArray(),
            // faces whose first image was a fallback (a PBR face's legacy texture): that texture, and when the face first
            // showed its own image in view
            fallbacks = s.Fallbacks.Select(x => new { obj = x.Object, face = x.Face, shown = x.Shown.ToString(), own_on_screen = R(x.OwnImageMs) }).ToArray(),
        };
    }

    /// <summary>What the texture server sent and how long each texture took to its first image.</summary>
    private object? TextureServerReport(TextureSource textures)
    {
        if (_server is null) return null;
        var perTexture = _server.BodyBytesPerTexture.Values.Select(v => (double)v).ToList();
        return new
        {
            mode = _textureServer, requests = _server.Requests, range_requests = _server.RangeRequests, body_bytes = _server.BodyBytes,
            textures = perTexture.Count, bytes_per_texture = perTexture.Count == 0 ? null : Dist(perTexture),
            first_image_ms = textures.FirstImageMs() is { Count: > 0 } fi ? Dist(fi.ToList()) : null,
            prefix_decodes = textures.PrefixDecodes, more_fetches = textures.MoreFetches, prefix_failures = textures.PrefixFailures(), fetcher = _fetcher?.Stats,
            delay_ms = _textureDelayMs, first_request = _firstRequest ?? "default", max_concurrent = _server.MaxConcurrent,
            // the request slots: requests per class (base colour, detail map) and their waits for a slot
            fetch_queue = _http?.Queue.Stats is { } q ? new { base_colour = QueueClass(q.BaseColour), pbr_base_colour = QueueClass(q.PbrBaseColour), detail = QueueClass(q.Detail), aging_ms = _http.Queue.Aging.TotalMilliseconds } : null,
            memory = MemoryReport(textures),
            content_store = _contentView is null ? null : new { whole_reads = Interlocked.Read(ref _storeReads), partial_fetcher_reads = _fetcher?.Stats.FromStore },
            // textures the server sent more bytes of than their file has: fetched again from the network
            refetched_from_network = _server.BodyBytesPerTexture.Count(kv => _serverCache is { } c && c.HasTexture(kv.Key) && kv.Value > new FileInfo(c.TexturePath(kv.Key)).Length),
            timeline = _timeline?.Summary() is { } t ? new
            {
                first_image = t.Stages["first-image"], to_screen = t.Stages["to-screen"], requests = t.Requests, bytes = t.Bytes, max_in_flight = t.MaxInFlight,
                requests_before_first_image = t.RequestsBeforeFirstImage.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                more_than_one = t.RequestsBeforeFirstImage.Where(kv => kv.Key > 1).Sum(kv => kv.Value),
            } : null,
        };
    }

    private static object QueueClass(Viewer.Core.Net.FetchClassStats s) =>
        new { requests = s.Requests, waited = s.Waited, wait_p50_ms = s.WaitP50Ms, wait_p95_ms = s.WaitP95Ms, wait_max_ms = s.WaitMaxMs, aged = s.Aged };

    /// <summary>The process's peak memory, the runtime's allocations and collections since it
    /// started, the managed bytes each level decode allocated, and the partial fetcher's peak held bytes. The decode times
    /// come from the texture source's timeline (the heavy scene has one too).</summary>
    private object MemoryReport(TextureSource textures)
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        const double MiB = 1048576.0;
        long decodes = textures.Decodes;
        return new
        {
            peak_working_set_mib = Math.Round(self.PeakWorkingSet64 / MiB, 1),
            peak_private_mib = Math.Round(self.PeakPagedMemorySize64 / MiB, 1),
            gc_allocated_mib = Math.Round(GC.GetTotalAllocatedBytes(false) / MiB, 1),
            gc_collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
            gc_pause_ms = Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds, 1),
            // objects waiting for their finalizers at the last collection
            gc_finalization_pending = GC.GetGCMemoryInfo(GCKind.Any).FinalizationPendingCount,
            level_decodes = decodes,
            decode_allocated_mib = Math.Round(textures.DecodeAllocatedBytes / MiB, 1),
            decode_allocated_kib_per_level = decodes == 0 ? 0 : Math.Round(textures.DecodeAllocatedBytes / 1024.0 / decodes, 1),
            decode_ms = textures.Timeline?.Summary().Stages["decode"],
            fetcher_peak_held_bytes = _fetcher?.PeakHeldBytes,
            fetcher_held_bytes_at_end = _fetcher?.HeldBytes, fetcher_evicted_from_memory = _fetcher?.EvictedFromMemory,
            pixel_pool = Viewer.Core.Imaging.PixelPool.Shared.Stats,
        };
    }

    /// <summary>The process's peak memory and the runtime's collections at the end
    /// of a stress run (no texture source there).</summary>
    private static object StressMemory()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        const double MiB = 1048576.0;
        return new
        {
            peak_working_set_mib = Math.Round(self.PeakWorkingSet64 / MiB, 1),
            peak_private_mib = Math.Round(self.PeakPagedMemorySize64 / MiB, 1),
            gc_allocated_mib = Math.Round(GC.GetTotalAllocatedBytes(false) / MiB, 1),
            gc_collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
            gc_pause_ms = Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds, 1),
            gc_finalization_pending = GC.GetGCMemoryInfo(GCKind.Any).FinalizationPendingCount,
        };
    }

    /// <summary>Meshes from the local-only asset cache (the replay asset source), through SessionAssets and the guarded decoder.
    /// With <paramref name="content"/> (--tr-content-store), through the run's content store first, as the live
    /// viewer's meshes are (Main): a mesh the bounded shared asset store let go is read back from disk, not fetched again.</summary>
    private static MeshSource CacheMeshSource(string dir, ContentView? content = null)
    {
        var cache = new LocalAssetCache(dir);
        // every mesh fetched from the cache (the harness's stand-in for the grid) is counted, per id
        Func<Guid, CancellationToken, Task<AssetFetchResult>> fetch = (id, ct) => { s_meshFetches.AddOrUpdate(id, 1, (_, n) => n + 1); return cache.FetchMeshAsync(id, ct); };
        if (content is not null) fetch = content.Caching("mesh", fetch);
        return new MeshSource(new SessionMeshes(new SessionAssets(SharedAssets, _ => true, fetch),
            (id, b) => { var r = LmvMesh.Decode(id, b); return (r.Geometry, r.Refusal); }));
    }

    /// <summary>Textures from files in <paramref name="dir"/>, through a SessionAssets (the live path minus the network).</summary>
    internal static TextureSource TextureSourceFor(string dir, IReadOnlyDictionary<Guid, string> images) =>
        new(new SessionAssets(SharedAssets, _ => true, async (id, _) =>
        {
            s_textureFetches.AddOrUpdate(id, 1, (_, n) => n + 1);
            if (id == s_holdTexture) await s_holdGate.Task.ConfigureAwait(false);   // --tr-hold-texture: answered once released
            return s_alterSculpt && id == SculptZoo.SphereMap ? AssetFetchResult.Ok(AlteredSphereMap()) :
                images.TryGetValue(id, out var f) && File.Exists(Path.Combine(dir, f)) ? AssetFetchResult.Ok(File.ReadAllBytes(Path.Combine(dir, f))) : AssetFetchResult.Of(AssetFetchStatus.NotFound);
        }));

    /// <summary>A texture source over codestreams made in memory (the realistic crowd's bakes): bytes through a SessionAssets, decoded by the guarded
    /// decoder, as the live path minus the network. An id the map does not hold is not found.</summary>
    internal static TextureSource TextureSourceFromBytes(IReadOnlyDictionary<Guid, byte[]> codestreams) =>
        new(new SessionAssets(SharedAssets, _ => true, (id, _) =>
        {
            s_textureFetches.AddOrUpdate(id, 1, (_, n) => n + 1);
            return Task.FromResult(codestreams.TryGetValue(id, out var b) ? AssetFetchResult.Ok(b) : AssetFetchResult.Of(AssetFetchStatus.NotFound));
        }));

    /// <summary>Fetches of each fixture texture asked for in this run (render runs).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> s_textureFetches = new();

    /// <summary>--tr-hold-texture=&lt;id&gt;: that fixture texture's fetch waits until the run releases it.</summary>
    private static Guid? s_holdTexture;
    private static readonly TaskCompletionSource s_holdGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>--tr-legacy-fallback=off: PBR faces drawn on the placeholder until their base colour is on screen.</summary>
    private bool _legacyFallback = true;

    /// <summary>--tr-alter=fallback-unshared: the material zoo's override box gets a legacy texture of its own that no
    /// other face uses (a copy of an existing image under a new id), so its fallback has nothing on screen to show and
    /// must not fetch it.</summary>
    private static bool s_alterFallbackUnshared;
    internal static readonly Guid UnsharedLegacy = new("4e560009-0000-4000-8000-0000000000f1");

    /// <summary>PBR material assets from the local-only asset cache (captures).</summary>
    private static MaterialSource CacheMaterialSource(string dir) => new(new LocalAssetCache(dir).FetchMaterialAsync);

    /// <summary>PBR material assets from files in <paramref name="dir"/> (the live path minus the network).</summary>
    internal static MaterialSource MaterialSourceFor(string dir, IReadOnlyDictionary<Guid, string> materials) =>
        new((id, _) => Task.FromResult(materials.TryGetValue(id, out var f) && File.Exists(Path.Combine(dir, f))
            ? AssetFetchResult.Ok(File.ReadAllBytes(Path.Combine(dir, f))) : AssetFetchResult.Of(AssetFetchStatus.NotFound)));

    /// <summary>Negative control (--tr-alter=override): the material zoo without its glTF overrides.</summary>
    private static bool s_alterOverride;
    private static bool s_alterNoLegacy;

    /// <summary>Negative control (--tr-alter=sculptmap): one 16x16 region of the sphere map pushed out to x = +0.5.</summary>
    private static bool s_alterSculpt;

    /// <summary>Negative control (--tr-alter=water): the scene's water 3 m higher.</summary>
    private static bool s_alterWater;

    private static byte[] AlteredSphereMap()
    {
        var px = SculptZoo.Pixels(SculptZoo.SphereMap);
        for (int y = 24; y < 40; y++)
            for (int x = 0; x < 16; x++)
                px[(y * 64 + x) * 4] = 255;
        return LmvTexture.EncodeLossless(64, 64, px);
    }

    /// <summary>Negative control (--tr-alter=offset): the "plain" prim's texture offset changed through the core.</summary>
    private static void AlterOffset(WorldState world)
    {
        var o = world.Snapshot().Objects.Values.First(x => x.Textures is not null && x.Id.LocalId == 5001);
        var t = o.Textures!.Default;
        world.ApplyObject(FixtureSim, new PrimData(o.Id.LocalId, o.FullId, o.ParentLocalId, 0, o.Position, o.Rotation, o.Scale, Shape: o.Shape, Colors: o.Colors,
            Textures: new FaceTextures(t with { OffsetU = t.OffsetU + 0.5f })));
    }

    /// <summary>
    /// Eight views orbiting the scene's centre (bearings 0 N, 45 NE... 315 NW: the direction from the centre
    /// to the camera), slightly elevated, perspective, assembled into one 1280x360 sheet (2 rows x 4 tiles of 320x180),
    /// each tile labelled with its bearing. Saved as &lt;tag&gt;-sheet.png. A review aid only (no checks, no golden).
    /// </summary>
    private async Task ContactSheet(Rig rig, WorldState world, PrimLayer prims)
    {
        var objs = world.Snapshot().Objects.Values.Where(o => o.AttachmentPoint is null && o.ParentLocalId == 0).ToList();
        System.Numerics.Vector3 centre, lo, hi;
        if (objs.Count > 0)
        {
            lo = new(objs.Min(o => o.Position.X), objs.Min(o => o.Position.Y), objs.Min(o => o.Position.Z));
            hi = new(objs.Max(o => o.Position.X), objs.Max(o => o.Position.Y), objs.Max(o => o.Position.Z));
        }
        else
        {
            var r = world.Region!;
            lo = new(0, 0, 0);
            hi = new(r.SizeX, r.SizeY, 40);
        }
        centre = (lo + hi) / 2;
        float radius = MathF.Max(6f, (hi - lo).Length() * 0.9f);
        if (objs.Count > 20)
        {
            // a real scene: frame the bulk of it (median centre, 80th-percentile distance), not its outliers
            float Med(IEnumerable<float> v) { var l = v.OrderBy(x => x).ToList(); return l[l.Count / 2]; }
            centre = new(Med(objs.Select(o => o.Position.X)), Med(objs.Select(o => o.Position.Y)), Med(objs.Select(o => o.Position.Z)));
            var d = objs.Select(o => System.Numerics.Vector3.Distance(o.Position, centre)).OrderBy(x => x).ToList();
            radius = MathF.Max(6f, d[(int)(d.Count * 0.8)] * 1.6f);
        }
        var target = new Vector3(centre.X, centre.Z, -centre.Y);
        var cam = rig.Camera;
        cam.Projection = Camera3D.ProjectionType.Perspective;
        cam.Fov = 50f;
        cam.Near = 0.1f;
        cam.Far = radius * 6f;
        const int tw = 320, th = 180;
        var sheet = Image.CreateEmpty(tw * 4, th * 2, false, Image.Format.Rgba8);
        string[] names = ["0 N", "45 NE", "90 E", "135 SE", "180 S", "225 SW", "270 W", "315 NW"];
        for (int k = 0; k < 8; k++)
        {
            double a = k * Math.PI / 4;   // bearing: 0 = north (+Y), 90 = east (+X)
            var sl = centre + new System.Numerics.Vector3((float)Math.Sin(a) * radius, (float)Math.Cos(a) * radius, radius * 0.35f);
            cam.Position = new Vector3(sl.X, sl.Z, -sl.Y);
            cam.LookAt(target, Vector3.Up);
            prims.Camera = cam.GlobalPosition;
            prims.UpdateLods();
            prims.ResidencyView = PrimLayer.ViewOf(cam, Width, Height);   // textures follow this bearing (a tight budget shows eviction)
            for (int s = 0; s < 90; s++)
            {
                prims.UpdateResidency();
                while (prims.Step()) { }
                if (s >= 8 && prims.LastResidencyActions == 0 && prims.Residency is not { Loading: > 0 } && prims.Residency is not { Refining: > 0 }) break;
                await Frames(1);
            }
            while (prims.Step()) { }
            await Frames(4);
            var img = rig.Texture.GetImage();
            img.Convert(Image.Format.Rgba8);
            img.Resize(tw, th, Image.Interpolation.Bilinear);
            sheet.BlitRect(img, new Rect2I(0, 0, tw, th), new Vector2I((k % 4) * tw, (k / 4) * th));
            img.Dispose();
            Label(sheet, names[k], (k % 4) * tw + 6, (k / 4) * th + 6);
        }
        Directory.CreateDirectory(_out);
        string path = Path.Combine(_out, _tag + "-sheet.png");
        sheet.SavePng(path);
        sheet.Dispose();
        _result["contact_sheet"] = Path.GetFileName(path);
    }

    /// <summary>A tiny 3x5 pixel font (digits and N, E, S, W), drawn at 3x scale, white on a dark box.</summary>
    private static void Label(Image img, string text, int x, int y)
    {
        static string Glyph(char c) => c switch
        {
            '0' => "111101101101111", '1' => "010110010010111", '2' => "111001111100111", '3' => "111001111001111",
            '4' => "101101111001001", '5' => "111100111001111", '6' => "111100111101111", '7' => "111001001001001",
            '8' => "111101111101111", '9' => "111101111001111", 'N' => "101111111111101", 'E' => "111100110100111",
            'S' => "111100111001111", 'W' => "101101111111101", _ => "000000000000000",
        };
        const int k = 3;
        int w = text.Length * 4 * k + k, h = 7 * k;
        img.FillRect(new Rect2I(x, y, w, h), new Color(0, 0, 0, 0.75f));
        for (int i = 0; i < text.Length; i++)
        {
            var g = Glyph(text[i]);
            for (int r = 0; r < 5; r++)
                for (int c = 0; c < 3; c++)
                    if (g[r * 3 + c] == '1')
                        img.FillRect(new Rect2I(x + k + (i * 4 + c) * k, y + k + r * k, k, k), Colors.White);
        }
    }

    /// <summary>Presents until every mesh the prims wait for has arrived (bounded).</summary>
    /// <summary>
    /// With one texture's fetch held: runs the scene until nothing but that texture is outstanding (every other texture
    /// on the GPU, every mesh and material read, nothing left to present), draws two frames, saves the image as
    /// &lt;tag&gt;-held.png and returns what the PBR faces draw then: their legacy texture, the placeholder, or their PBR
    /// material, and the held texture's state.
    /// </summary>
    private async Task<object> HeldPhase(WorldPresenter presenter, PrimLayer prims, MeshSource? meshes, TextureSource textures, Guid hold, Rig rig)
    {
        presenter.Flush();
        presenter.Flush();
        var until = DateTime.UtcNow.AddSeconds(20);
        var r = prims.Residency!;
        bool Others() => r.All.Where(id => id != hold).All(id => r.StateOf(id) == Viewer.Core.Residency.ResidencyState.Resident && r.PendingLevelOf(id) < 0 || r.HasFailed(id));
        bool Settled() => Others() && prims.Dirty == 0 && (prims.MaterialAssets is not { } ma || (ma.Pending == 0 && prims.MaterialsWaiting == 0)) && textures.Pending <= 1
            && (meshes is null || (meshes.Pending == 0 && prims.MeshesWaiting == 0)) && prims.MeshPreparer is not { InFlight: > 0 };
        while (!Settled() && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
            while (prims.MeshPreparer is { InFlight: > 0 } && DateTime.UtcNow < until) await Task.Delay(2);   // in local-id order, as SettleMeshes
            while (prims.Step()) { }
            presenter.Flush();
        }
        bool settled = Settled();
        await Frames(2);
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        Directory.CreateDirectory(_out);
        img.SavePng(Path.Combine(_out, _tag + "-held.png"));
        img.Dispose();
        return new
        {
            texture = hold.ToString(), state = r.StateOf(hold).ToString(), others_settled = settled,
            fallback_faces = prims.FallbackFaces, waiting_faces = prims.PbrFacesWaiting, pbr_faces = prims.PbrFaces,
            fetches = s_textureFetches.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            png = _tag + "-held.png",
        };
    }

    private static async Task SettleMeshes(WorldPresenter presenter, PrimLayer prims, MeshSource? meshes, TextureSource? textures = null)
    {
        presenter.Flush();
        presenter.Flush();   // the first flush presents the faces (registering their textures' users); this one lets the manager grant them
        var until = DateTime.UtcNow.AddSeconds(20);
        // textures load only when the residency manager grants them, so settling also runs its updates until it
        // has nothing left to start and nothing in flight (under a tight budget some textures stay on placeholders)
        bool Busy() => (meshes is not null && (meshes.Pending > 0 || prims.MeshesWaiting > 0)) || (textures is not null && (textures.Pending > 0 || prims.TexturesWaiting > 0))
            || (prims.MaterialAssets is { } ma && (ma.Pending > 0 || prims.MaterialsWaiting > 0))
            || presenter.TerrainPending || (prims.Residency is { } r && (r.Loading > 0 || r.Refining > 0 || prims.LastResidencyActions > 0));
        while (Busy() && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
            // settle hands over the LODs prepared off the main thread in one batch, in local-id order, as the
            // synchronous builds were made; otherwise which of two objects drawn at equal depth wins follows which thread
            // finished first, and a golden's few tie pixels change from run to run
            while (prims.MeshPreparer is { InFlight: > 0 } && DateTime.UtcNow < until) await Task.Delay(2);
            while (prims.Step()) { }
            presenter.Flush();
        }
    }

    /// <summary>The texture budget of test renders: explicit (--tr-budget=bytes, or with a k / m suffix for KiB / MiB),
    /// otherwise the fixed fallback, so goldens never depend on the machine's GPU.</summary>
    private long _budget = Viewer.Core.Residency.TextureBudget.TestRenderFixed;

    /// <summary>Measurement (--tr-budget=default, camera paths only): the app's budget instead of a fixed
    /// one, from the detected video memory or a simulated card (--tr-vram-mib=N); --tr-budget-live=off keeps it static.</summary>
    private Viewer.Core.Residency.LiveTextureBudget? _liveBudget;

    /// <summary>Measurement control (--tr-legacy-upload): mipmaps on the main thread at upload.</summary>
    private bool _legacyUpload;

    private static long ParseBytes(string s) =>
        s.EndsWith('m') ? (long)(double.Parse(s[..^1], System.Globalization.CultureInfo.InvariantCulture) * 1024 * 1024)
        : s.EndsWith('k') ? (long)(double.Parse(s[..^1], System.Globalization.CultureInfo.InvariantCulture) * 1024)
        : long.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsZoo(WorldState w) => w.TerrainPatchCount == 0 && w.Snapshot().Objects.Count > 0;

    /// <summary>Negative control: a 40 m hill near the region centre, delivered as replacement patches through the core.</summary>
    private static void AlterHill(WorldState world)
    {
        var r = world.Region!;
        float cx = r.SizeX * 0.5f, cy = r.SizeY * 0.5f, radius = Math.Min(r.SizeX, r.SizeY) * 0.12f;
        for (int py = 0; py * 16 < r.SizeY; py++)
            for (int px = 0; px * 16 < r.SizeX; px++)
            {
                if (!world.TryGetPatch(px, py, out var h))
                    continue;
                var copy = (float[])h.Clone();
                bool touched = false;
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        float dx = px * 16 + x - cx, dy = py * 16 + y - cy;
                        float d2 = (dx * dx + dy * dy) / (radius * radius);
                        if (d2 < 4f) { copy[y * 16 + x] += 40f * MathF.Exp(-d2); touched = true; }
                    }
                if (touched)
                    world.ApplyPatch(FixtureSim, px, py, 16, copy);
            }
    }

    // ---------------- the fixed stage ----------------

    // Scenario and Texture are read from the viewport once, here, and kept: reading a resource back from the engine on every use
    // hands out a fresh managed wrapper each time, and one whose finalizer is running at that moment fails the call
    private sealed record Rig(SubViewport Viewport, Camera3D Camera, Node3D Root, WorldEnvironment Environment, DirectionalLight3D Sun, Rid Scenario, ViewportTexture Texture, World3D World);

    private Rig MakeRig(Vector2I? size = null)
    {
        var vp = new SubViewport
        {
            Size = size ?? new Vector2I(Width, Height),
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa3D = Viewport.Msaa.Disabled,
            ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
            UseTaa = false,
            Scaling3DScale = 1f,
        };
        AddChild(vp);
        var root = new Node3D { Name = "stage" };
        vp.AddChild(root);
        var env = Stage.Environment();
        var sun = Stage.Sun();
        root.AddChild(env);
        root.AddChild(sun);
        var cam = new Camera3D { Current = true };
        root.AddChild(cam);
        var world = vp.FindWorld3D();
        return new Rig(vp, cam, root, env, sun, world.Scenario, vp.GetTexture(), world);
    }

    private static ITerrainView MakeView(string kind, Rig rig)
    {
        if (kind == "nodes")
        {
            var v = new NodeTerrainView { Name = "terrain" };
            rig.Root.AddChild(v);
            return v;
        }
        if (kind == "server")
            return new ServerTerrainView(rig.Scenario);
        throw new ArgumentException("--tr-view must be server or nodes");
    }

    /// <summary>The warm-up check. The engine's object, resource and node counts and
    /// its render buffer memory before the warm-up and after it (equal: everything it made is freed; video memory reported),
    /// then each scene presented as the goldens present it (the prim, sculpt, mesh and texture zoos, a terrain with water and
    /// sky), counting the pipelines compiled while presenting it: none may be left after the warm-up (--tr-warmup=off, the
    /// negative control, shows them).</summary>
    private bool _warmupOff;
    private bool _pacingGuard;   // --tr-pacing-guard (a measuring run): a path run whose frames are presentation-paced fails
    private bool _warmupTwice;   // --tr-warmup=twice: a second warm-up, whose compilations show what the first did not keep, and which must add no object or buffer memory

    private async Task<int> WarmupCheck(string fixture, string? sceneList, bool warm)
    {
        var scenes = new List<string> { fixture };
        if (sceneList is not null) scenes.AddRange(sceneList.Split(';', StringSplitOptions.RemoveEmptyEntries));
        KeptShaders.PrepareAtStartup();   // the viewer's own shaders, made at start-up (not the warm-up's)
        await Frames(2);
        var before = CountedResources();
        var counters0 = PipelineWarmup.Counters();
        PipelineWarmup.Result? w = warm ? await PipelineWarmup.RunAsync(this, Viewport.Msaa.Disabled, () => Frames(1)) : null;
        if (warm && _warmupTwice)
        {
            // a second warm-up in the same process: what the engine keeps once (the buffers it makes the first time local lights are drawn, for
            // one) is already there, so the second adds no node, render buffer memory or video memory. Object and resource counts are reported, not
            // gated: objects the first warm-up freed are still counted until the next collection (the same totals are reached at the end of
            // the scenes whatever the warm-up did).
            await Frames(2);
            var first = Resources();
            _result["warmup_second"] = (await PipelineWarmup.RunAsync(this, Viewport.Msaa.Disabled, () => Frames(1))).Pipelines;
            await Frames(2);
            var second = Resources();
            _result["warmup_repeat"] = new { objects = second.Objects - first.Objects, resources = second.Resources - first.Resources, nodes = second.Nodes - first.Nodes, buffer_mem = second.BufferMem - first.BufferMem, video_mem = second.VideoMem - first.VideoMem };
            if (second.Nodes != first.Nodes || second.BufferMem > first.BufferMem || second.VideoMem > first.VideoMem + 64 * 1024)
                _failures.Add($"a second warm-up added nodes {second.Nodes - first.Nodes}, buffer memory {second.BufferMem - first.BufferMem}, video memory {second.VideoMem - first.VideoMem}");
        }
        var after = CountedResources();
        await Frames(2);
        var afterFrames = Resources();
        if (warm)
        {
            if (after.Resources != before.Resources) _failures.Add($"warm-up: resources {before.Resources} -> {after.Resources}");
            if (after.Nodes != before.Nodes) _failures.Add($"warm-up: nodes {before.Nodes} -> {after.Nodes}");
        }
        var perScene = new List<object>();
        long left = 0;
        foreach (var sceneSpec in scenes)
        {
            // "path@0.25": the scene at that fixed time of day (a day fraction), for the sky at each time; "path#omni": the scene with that kind of
            // local light (PrimLayer.TestLights: spot is the default, omni a point light), so the scenes draw the kinds of light the warm-up covers
            var specParts = sceneSpec.Split('@');
            string scene = specParts[0];
            string? sceneLights = null;
            if (scene.Contains('#')) { var lp = scene.Split('#'); scene = lp[0]; sceneLights = lp[1]; }
            double? dayAt = specParts.Length > 1 ? double.Parse(specParts[1], System.Globalization.CultureInfo.InvariantCulture) : null;
            if (CompileWatch.Active is { } watch) watch.Phase = Path.GetFileName(scene) + (dayAt is { } dd ? $" at day {dd}" : "");
            if (scene == WarmupAvatarScene)
            {
                var (info, compiledHere) = await WarmupAvatars();
                left += compiledHere;
                perScene.Add(info);
                continue;
            }
            var savedLights = PrimLayer.TestLights;
            if (sceneLights is not null) PrimLayer.TestLights = sceneLights;
            var loaded = Load(scene);
            var rig = MakeRig();
            var view = MakeView("server", rig);
            var textures = loaded.Textures ?? loaded.TerrainTextures;
            var prims = new PrimLayer(rig.Scenario, loaded.World, loaded.Meshes, textures, _budget) { SimulatedFps = 60, MaterialAssets = loaded.MaterialAssets };
            if (Path.GetFileName(scene).StartsWith("glow", StringComparison.OrdinalIgnoreCase)) Stage.GlowCamera(rig.Camera);   // the glow scene's own camera
            else if (loaded.Textures is not null || (IsZoo(loaded.World) && loaded.Meshes is null)) Stage.ZooCamera(rig.Camera);
            else if (loaded.Meshes is not null) Stage.MeshCamera(rig.Camera, "near");
            else Stage.Overview(rig.Camera, loaded.World.Region!.SizeX, loaded.World.Region!.SizeY);
            prims.Camera = rig.Camera.GlobalPosition;
            prims.ResidencyView = PrimLayer.ViewOf(rig.Camera, Width, Height);
            var presenter = new WorldPresenter(loaded.World, view, prims: prims, terrainTextures: textures,
                environment: new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun)) { DayFractionOverride = dayAt ?? loaded.DayFraction ?? 0.3 };
            rig.Root.AddChild(new GlowPass(prims, rig.World, rig.Environment));   // the glow of faces that glow (the glow scene draws it)
            var c0 = PipelineWarmup.Counters();
            await SettleMeshes(presenter, prims, loaded.Meshes, textures);
            await Frames(8);
            var d = PipelineWarmup.Counters().Zip(c0, (a, b) => a - b).ToArray();
            long compiled = d.Sum();
            left += compiled;
            perScene.Add(new
            {
                scene = loaded.Description, pipelines = PipelineWarmup.Named(d), instances = prims.Instances, textured_materials = prims.TexturedMaterials, pbr_faces = prims.PbrFaces,
                colour_materials = prims.Materials, sculpt_objects = prims.SculptObjects, mesh_objects = prims.MeshObjects, terrain_chunks = view.ChunkCount,
                water = presenter.Environment?.HasWater ?? false, sky = presenter.Environment?.Frame is not null,
            });
            presenter.Dispose();
            view.Clear();
            loaded.Dispose();
            PrimLayer.TestLights = savedLights;
            RemoveChild(rig.Viewport);
            rig.Viewport.Free();
            await Frames(1);
        }
        _result["mode"] = "warmup";
        _result["warmup"] = w is null ? "off" : new { ms = Math.Round(w.Ms, 2), pipelines = w.Pipelines, variants = w.Variants, frames = w.Frames };
        _result["kept_shaders"] = KeptShaders.Count;
        _result["scenes"] = perScene;
        // the engine keeps a few objects of its own after its first 3D draw (measured: 3, the same with or
        // without the warm-up); what the warm-up adds may be no more than what presenting the scenes adds anyway. Compared
        // as what each step adds, never as absolute counts: the count before the warm-up differs by one between runs
        // (warmup_control_d3d12, 1411 against 1410). render.ps1 holds the warm-up run's additions
        // to the control's (--tr-warmup=off). The counts are taken with the garbage collected (CountedResources): uncollected, the run's end
        // count moved by up to seven objects between identical runs, and a ground material that every scene's terrain view once left alive
        // (one per view, about twenty in a long scene list) hid that the scenes also let go of garbage the warm-up left. Collected, the figures
        // are the same in every run on both backends: the warm-up keeps 5 objects and the warm-up with all the scenes 9.
        var end = CountedResources();
        _result["one_time_engine_objects"] = after.Objects - before.Objects;
        if (warm && after.Objects - before.Objects > end.Objects - before.Objects)
            _failures.Add($"warm-up: added {after.Objects - before.Objects} objects, more than the warm-up and the scenes add together ({end.Objects - before.Objects})");
        _result["lifecycle"] = new
        {
            before = new { objects = before.Objects, resources = before.Resources, nodes = before.Nodes, buffer_mem = before.BufferMem, video_mem = before.VideoMem },
            after = new { objects = after.Objects, resources = after.Resources, nodes = after.Nodes, buffer_mem = afterFrames.BufferMem, video_mem = afterFrames.VideoMem },
            end = new { objects = end.Objects, resources = end.Resources, nodes = end.Nodes, buffer_mem = end.BufferMem, video_mem = end.VideoMem },
            added_by_warmup = new { objects = after.Objects - before.Objects, buffer_mem = afterFrames.BufferMem - before.BufferMem },
            added_total = new { objects = end.Objects - before.Objects, buffer_mem = end.BufferMem - before.BufferMem },
        };
        _result["compiled_while_presenting"] = left;
        _result["pipelines_total"] = PipelineWarmup.Named(PipelineWarmup.Counters().Zip(counters0, (a, b) => a - b).ToArray());
        // the negative control (--tr-warmup=off) must fail here: without the warm-up the first uses compile
        if (left > 0) _failures.Add($"{(warm ? "after the warm-up" : "without the warm-up")}, {left} pipelines were compiled while presenting the scenes");
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>The warm-up check's avatar scene (its name in --tr-warmup-scenes=): three avatars from synthetic region
    /// updates, drawn as name labels (no character folder: bodies are local-only), presented as the viewer presents them.</summary>
    internal const string WarmupAvatarScene = "avatars";

    private async Task<(object Info, long Compiled)> WarmupAvatars()
    {
        var rig = MakeRig();
        var view = MakeView("server", rig);
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("avatars", 1000, 1000, 256, 256, true));
        var avatars = new AvatarPresenter(rig.Scenario, world, LiveSelf, null, null, null, null, () => 0);
        var presenter = new WorldPresenter(world, view, avatars: avatars);
        var size = new System.Numerics.Vector3(0.45f, 0.6f, 1.9f);
        world.ApplyAvatar(FixtureSim, new(800, LiveSelf, "Test User", new(128, 128, 21), System.Numerics.Quaternion.Identity, size));
        world.ApplyAvatar(FixtureSim, new(901, Guid.NewGuid(), "Example Avatar", new(131, 128, 21), System.Numerics.Quaternion.Identity, size));
        world.ApplyAvatar(FixtureSim, new(902, Guid.NewGuid(), "Another Example", new(131, 130, 21), System.Numerics.Quaternion.Identity, size));
        rig.Camera.LookAtFromPosition(new Vector3(124, 23, -128), new Vector3(130, 22, -128.5f), Vector3.Up);
        var c0 = PipelineWarmup.Counters();
        presenter.Frame();
        await Frames(8);
        presenter.Frame();
        var d = PipelineWarmup.Counters().Zip(c0, (a, b) => a - b).ToArray();
        var info = new { scene = "avatars (name labels)", pipelines = PipelineWarmup.Named(d), avatars = avatars.Scene.Count };
        presenter.Dispose();
        view.Clear();
        world.Dispose();
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        await Frames(1);
        return (info, d.Sum());
    }

    // ---------------- the engine errors' backtraces (--tr-engineerrors[=keep]) ----------------

    /// <summary>
    /// The worker thread's engine errors for <see cref="EngineErrorsCheck"/>. GodotSharp's GD.PushError names the frame above its
    /// caller (it takes a StackTrace that skips three frames and reads its first frame without a null check). Once PushError and
    /// GD.ErrPrintError have their optimised code, PushError's call is a tail call and the helper that takes the StackTrace is
    /// inlined, so a thread's own method that called PushError directly left no frame there and the call threw a
    /// NullReferenceException, ending the process (in some runs only: the optimised code arrives partway through the errors, at a
    /// moment that varies). These two methods are never inlined or optimised, so their frames always stand above the call.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining | System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
    private static void WorkerErrorsLogged(int n)
    {
        for (int i = 0; i < n; i++) WorkerErrorLogged();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining | System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
    private static void WorkerErrorLogged()
    {
        GD.PushError("engine error check: logged off the main thread");
    }

    /// <summary>
    /// Every engine error hands the logger its script backtraces (engine objects), which only a finalizer released until
    /// the logger handed them over for the main thread's pinned disposal (<see cref="LoggedErrorReleases"/>). (1) Engine
    /// errors on the main thread and on a worker thread with collections held off (a no-GC region), so no finalizer can
    /// release anything before the count: the engine objects alive after the errors, then after the main thread's drain,
    /// which must be back at the count before the errors (at most the d3d12 engine's one-time object above it). A check
    /// the runtime could not hold collections off for fails as inconclusive. (2) Errors with frames running: the frame's
    /// own drain (Main._Process) must release them. Each main-thread error's own time is reported.
    /// =keep (negative control): the logger hands nothing over, as before the release existed; it must fail.
    /// </summary>
    private async Task<int> EngineErrorsCheck(string spec)
    {
        bool keep = spec == "=keep";
        LoggedErrorReleases.KeepForTest = keep;
        const int MainErrors = 400, WorkerErrors = 200, FrameErrors = 50;
        if (Main.EngineLog is not { } route) { _failures.Add("no engine log route (the viewer's logger is not registered)"); return 1; }
        // an engine error on the main thread ("Parameter material is null", as a region change once raised by the thousand)
        static void EngineError() => RenderingServer.MaterialSetParam(new Rid(), EngineNames.Tex, default);
        // errors logged on another thread: the logger is called on that thread (WorkerErrorsLogged, below)
        static void OnWorker(int n)
        {
            var t = new System.Threading.Thread(() => WorkerErrorsLogged(n));
            t.Start();
            t.Join();
        }
        static long Objects() => (long)Performance.GetMonitor(Performance.Monitor.ObjectCount);
        static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

        // the first errors' one-time costs paid before the baseline (both kinds), and what they left released
        for (int i = 0; i < 4; i++) EngineError();
        OnWorker(4);
        LoggedErrorReleases.DrainAll();
        Collect();
        await Frames(2);
        long errors0 = route.Errors, handed0 = LoggedErrorReleases.Handed, off0 = LoggedErrorReleases.HandedOffMain, disposed0 = LoggedErrorReleases.Disposed;

        // (1) collections held off
        Collect();
        bool held;
        try { held = GC.TryStartNoGCRegion(HoldBytes); }
        catch (ArgumentOutOfRangeException) { held = false; }
        long objects0 = Objects();
        var errorMs = new List<double>(MainErrors);
        for (int i = 0; i < MainErrors; i++)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EngineError();
            errorMs.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        OnWorker(WorkerErrors);
        long objectsRaised = Objects();
        long td = System.Diagnostics.Stopwatch.GetTimestamp();
        var drain = LoggedErrorReleases.DrainAll();
        double drainMs = System.Diagnostics.Stopwatch.GetElapsedTime(td).TotalMilliseconds;
        long objectsAfter = Objects();
        bool stillHeld = System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion;
        if (stillHeld) GC.EndNoGCRegion();
        long raised = route.Errors - errors0;
        long handed = LoggedErrorReleases.Handed - handed0, offMain = LoggedErrorReleases.HandedOffMain - off0;
        long alive = objectsRaised - objects0, left = objectsAfter - objects0;
        if (!held || !stillHeld) _failures.Add("collections were not held off for the whole check: inconclusive");
        if (raised != MainErrors + WorkerErrors) _failures.Add($"{raised} engine errors reached the logger, {MainErrors + WorkerErrors} raised");
        if (alive < raised) _failures.Add($"the errors made {alive} engine objects, fewer than one per error: the check did not exercise the backtraces");
        if (left > 1) _failures.Add($"{left} engine objects outlived the errors and the main thread's drain ({alive} alive after the errors): only a finalizer would release them");
        if (handed != raised) _failures.Add($"the logger handed over {handed} of {raised} errors' backtraces");
        if (offMain != WorkerErrors) _failures.Add($"{offMain} handed over off the main thread, {WorkerErrors} logged there");

        // (2) frames running: the frame's drain releases them
        long beforeFrames = LoggedErrorReleases.Disposed;
        for (int i = 0; i < FrameErrors; i++) EngineError();
        int frames = 0;
        while (LoggedErrorReleases.Waiting > 0 && frames < 120) { await Frames(1); frames++; }
        long frameDisposed = LoggedErrorReleases.Disposed - beforeFrames;
        if (LoggedErrorReleases.Waiting > 0) _failures.Add($"{LoggedErrorReleases.Waiting} errors' backtraces still waiting after {frames} frames");
        if (!keep && frameDisposed != FrameErrors) _failures.Add($"the frame drains released {frameDisposed} of {FrameErrors} errors' backtraces");
        if (LoggedErrorReleases.DisposalsOutsideDrain != 0) _failures.Add($"{LoggedErrorReleases.DisposalsOutsideDrain} backtraces disposed outside a main-thread drain");
        _result["engineerrors"] = new
        {
            control = spec,
            raised,
            main_errors = MainErrors,
            worker_errors = WorkerErrors,
            held = held && stillHeld,
            objects_before = objects0,
            objects_after_errors = objectsRaised,
            objects_after_drain = objectsAfter,
            objects_per_error = raised > 0 ? Math.Round((double)alive / raised, 3) : 0,
            left_after_drain = left,
            handed,
            handed_off_main = offMain,
            drained = drain.Values,
            drain_ms = Math.Round(drainMs, 4),
            drain_us_per_error = raised > 0 ? Math.Round(drainMs * 1000 / raised, 3) : 0,
            error_ms = Dist(errorMs),
            error_ms_mean = Math.Round(errorMs.Average(), 4),
            frame_errors = FrameErrors,
            frame_disposed = frameDisposed,
            frames_to_release = frames,
            disposed_before = disposed0,
            releases = LoggedErrorReleases.Report(),
        };
        LoggedErrorReleases.KeepForTest = false;
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>The angle (degrees, 0 to 180) between a view ray's azimuth and the light's, both in the engine's axes (x east, y up, z south); 0 when either points straight up or down.</summary>
    private static double AzimuthFromLight(SkyFrame frame, Vector3 ray)
    {
        var d = frame.SunDirection.Z >= 0f ? frame.SunDirection : frame.MoonDirection.Z >= 0f ? frame.MoonDirection : -System.Numerics.Vector3.UnitZ;
        double lx = d.X, lz = -d.Y, ex = ray.X, ez = ray.Z;
        double ll = Math.Sqrt(lx * lx + lz * lz), le = Math.Sqrt(ex * ex + ez * ez);
        if (ll < 1e-4 || le < 1e-4) return 0;
        return Math.Acos(Math.Clamp((lx * ex + lz * ez) / (ll * le), -1, 1)) * 180.0 / Math.PI;
    }

    /// <summary>The angle (degrees) between a view ray and the light, in the engine's axes.</summary>
    private static double AngleToLight(SkyFrame frame, Vector3 ray)
    {
        var d = frame.SunDirection.Z >= 0f ? frame.SunDirection : frame.MoonDirection;
        var l = new Vector3(d.X, d.Z, -d.Y).Normalized();
        return Math.Acos(Math.Clamp(ray.Normalized().Dot(l), -1f, 1f)) * 180.0 / Math.PI;
    }

    /// <summary>
    /// --tr-sky-probes: for several elevations and three columns (so three angles from the light), the picture's pixel where the camera looks that far
    /// above the horizon against the colour the official viewer shows there (<see cref="SkyLighting.DomeShown"/>: the real dome, the 50 m lift, the real
    /// angle to the light, the classic-lighting 1.1; the core tests check it against an independent transcription of the official shaders). Both sides are the displayed (encoded)
    /// value, tolerance 0.005 (8-bit pictures, the profile's own interpolation). A probe within 3 degrees of the light is skipped (the light's disc is drawn there).
    /// Fewer than three probes in view, or any probe off, fails the run.
    /// </summary>
    private void SkyProbes(Image img, Camera3D cam, SkyFrame? frame)
    {
        if (frame is null) { _failures.Add("sky probes: no environment frame"); return; }
        int w = img.GetWidth(), h = img.GetHeight();
        var rows = new List<object>();
        int inView = 0; bool allPass = true;
        foreach (int x in new[] { w / 4, w / 2, 3 * w / 4 })
        foreach (double target in new[] { 4.0, 10.0, 20.0, 37.0, 60.0 })
        {
            int best = -1; double bestGap = 1e9, bestElev = 0; Vector3 bestRay = default;
            for (int y = 2; y < h - 2; y++)
            {
                var n = cam.ProjectRayNormal(new Vector2(x, y));
                double elev = Math.Asin(Math.Clamp(n.Y, -1f, 1f)) * 180.0 / Math.PI;
                if (Math.Abs(elev - target) < bestGap) { bestGap = Math.Abs(elev - target); best = y; bestElev = elev; bestRay = n; }
            }
            if (best < 0 || bestGap > 1.0 || AngleToLight(frame, bestRay) < 3.0) continue;
            inView++;
            var got = new float[3];
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) { var c = img.GetPixel(x + dx, best + dy); got[0] += c.R / 9f; got[1] += c.G / 9f; got[2] += c.B / 9f; }
            double az = AzimuthFromLight(frame, bestRay);
            var shownFrame = _personal is null ? frame : _personal.Apply(frame);
            // a sky that sets a probe ambiance is seen through the tone mapper: its linear dome colour, tone mapped, as a display value
            var wantV = ColourPath.IsHdr(shownFrame.ProbeAmbiance)
                ? SkyLighting.ToDisplay(ToneMap.Apply(SkyLighting.DomeColourHdr(shownFrame, (float)bestElev, (float)az), ToneMap.DefaultExposure, ToneMap.DefaultType, ColourPath.TonemapMix(shownFrame.ProbeAmbiance)))
                : SkyLighting.DomeShown(shownFrame, (float)bestElev, (float)az);
            float gamma = _personal?.Gamma ?? 1f;   // the scene gamma bends the finished picture
            var want = new[] { PersonalLighting.GammaCurve(gamma, wantV.X), PersonalLighting.GammaCurve(gamma, wantV.Y), PersonalLighting.GammaCurve(gamma, wantV.Z) };
            bool pass = true;
            for (int i = 0; i < 3; i++) if (MathF.Abs(got[i] - want[i]) > 0.005f) pass = false;
            allPass &= pass;
            rows.Add(new { elevation = Math.Round(bestElev, 2), azimuth_from_light = Math.Round(az, 1), column = x, row = best, got, want, pass });
        }
        _result["sky_probes"] = new { probes = rows, in_view = inView, pass = allPass && inView >= 3, hdr = ColourPath.IsHdr(frame.ProbeAmbiance) };
        if (inView < 3) _failures.Add($"sky probes: only {inView} probe rows in view (3 needed)");
        else if (!allPass) _failures.Add("sky probes: the picture's sky differs from the official colour at some elevation: " + System.Text.Json.JsonSerializer.Serialize(rows));
    }

    /// <summary>
    /// --tr-water-look: how much the water in a rectangle of the picture varies (the spread of its brightness, as the ripples make it), how bright its brightest
    /// 1 % is against its median (the sun's glint), its mean colour, and, with --tr-water-compare, how far it is from the same rectangle of another picture
    /// of the same view (mean absolute difference per channel, 0..1: the ripples moving). Display values.
    /// </summary>
    private void WaterLookMetrics(Image img, (int X, int Y, int W, int H) r)
    {
        var lum = new List<double>(); double mr = 0, mg = 0, mb = 0;
        for (int y = r.Y; y < r.Y + r.H; y++)
            for (int x = r.X; x < r.X + r.W; x++)
            {
                var c = img.GetPixel(x, y);
                lum.Add(0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B); mr += c.R; mg += c.G; mb += c.B;
            }
        int n = lum.Count;
        double mean = lum.Average(), sd = Math.Sqrt(lum.Sum(v => (v - mean) * (v - mean)) / n);
        lum.Sort();
        double? diff = null;
        if (_waterCompare is not null && File.Exists(_waterCompare))
        {
            var other = Image.LoadFromFile(_waterCompare); other.Convert(Image.Format.Rgba8);
            double d = 0;
            for (int y = r.Y; y < r.Y + r.H; y++)
                for (int x = r.X; x < r.X + r.W; x++)
                {
                    var a = img.GetPixel(x, y); var b = other.GetPixel(x, y);
                    d += (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 3.0;
                }
            diff = d / n;
        }
        _result["water_look"] = new { pixels = n, std = sd, median = lum[n / 2], p99 = lum[Math.Min(n - 1, (int)(n * 0.99))], max = lum[n - 1], mean = new[] { mr / n, mg / n, mb / n }, diff_vs_other = diff };
    }

    /// <summary>
    /// --tr-underwater-probes: the camera under open water (nothing but water below, only sky above), centre column. A ray going down is the
    /// water haze over the far distance; a ray going up meets the surface, where the sky above shows through, dimmed and fogged along the
    /// way there (<see cref="UnderwaterView"/>, checked in the core tests against an independent transcription of the official formulas).
    /// Display values, tolerance 0.02 (the water's colours are dark: shown values keep them apart). The water is drawn flat, so the sky is
    /// read straight along the ray.
    /// </summary>
    private void UnderwaterProbes(Image img, Camera3D cam, EnvironmentLayer? env)
    {
        if (env?.Frame is not { } frame || env.WaterApplied is not { } water || env.WaterHeight is not { } surface) { _failures.Add("underwater probes: no water or environment frame"); return; }
        float depth = surface - cam.Position.Y;
        if (depth <= 0f) { _failures.Add("underwater probes: the camera is not under the water"); return; }
        static float Shown(float c) { c = Math.Clamp(c, 0f, 1f); return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f; }
        bool night = frame.SunDirection.Z < 0;
        float slant = WaterShading.FogSlant(night ? frame.MoonDirection.Z : frame.SunDirection.Z);
        float density = UnderwaterView.FogDensity(water.FogDensity, water.FogMod, underwater: true);
        int h = img.GetHeight(), x = img.GetWidth() / 2;
        var rows = new List<object>();
        int down = 0, up = 0; bool allPass = true;
        foreach (double wanted in new[] { -22.0, -10.0, -3.0, 8.0, 14.0, 22.0 })
        {
            int best = -1; double bestGap = 1e9, elev = 0;
            for (int y = 2; y < h - 2; y++)
            {
                double e = Math.Asin(Math.Clamp(cam.ProjectRayNormal(new Vector2(x, y)).Y, -1f, 1f)) * 180.0 / Math.PI;
                if (Math.Abs(e - wanted) < bestGap) { bestGap = Math.Abs(e - wanted); best = y; elev = e; }
            }
            if (best < 0 || bestGap > 0.7) continue;
            var px = new float[3];
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) { var c = img.GetPixel(x + dx, best + dy); px[0] += c.R / 9f; px[1] += c.G / 9f; px[2] += c.B / 9f; }
            var got = new[] { px[0], px[1], px[2] };
            float rise = (float)Math.Sin(elev * Math.PI / 180);
            System.Numerics.Vector3 wantV;
            if (rise < 0f)
            {
                var (light, survives) = UnderwaterView.Fog(water.FogColor, density, slant, depth, rise, cam.Far);
                wantV = UnderwaterView.Haze(System.Numerics.Vector3.Zero, light, survives); down++;
            }
            else
            {
                var (light, survives) = UnderwaterView.Fog(water.FogColor, density, slant, depth, rise, depth / rise);
                var sky = SkyLighting.DomeColour(frame, (float)elev, (float)AzimuthFromLight(frame, cam.ProjectRayNormal(new Vector2(x, best))));
                wantV = UnderwaterView.SurfaceFromBelow(sky, light, survives); up++;
            }
            var want = new[] { Shown(wantV.X), Shown(wantV.Y), Shown(wantV.Z) };
            bool pass = true;
            for (int i = 0; i < 3; i++) if (MathF.Abs(got[i] - want[i]) > 0.02f) pass = false;
            allPass &= pass;
            rows.Add(new { elevation = Math.Round(elev, 2), row = best, got, want, pass });
        }
        bool enough = down >= 2 && up >= 2;
        _result["underwater_probes"] = new { probes = rows, down, up, pass = allPass && enough };
        if (!enough) _failures.Add($"underwater probes: {down} rays down and {up} up in view (2 of each needed)");
        else if (!allPass) _failures.Add("underwater probes: the picture differs from the official underwater colour: " + System.Text.Json.JsonSerializer.Serialize(rows));
    }

    /// <summary>Puts the camera half a metre under the water, straight above the flattest ground that lies deepest, looking down.</summary>
    private void AimAtDeepGround(WorldState scene, Camera3D cam)
    {
        var region = scene.Region!;
        float water = region.WaterHeight ?? 20f;
        float bestScore = float.MinValue, bx = 0, by = 0, bz = 0;
        for (int py = 0; py < region.SizeY / 16; py++)
            for (int px = 0; px < region.SizeX / 16; px++)
            {
                if (!scene.TryGetPatch(px, py, out var h)) continue;
                float centre = h[8 * 16 + 8], spread = 0;
                for (int j = 6; j <= 10; j++) for (int i = 6; i <= 10; i++) spread = MathF.Max(spread, MathF.Abs(h[j * 16 + i] - centre));
                float score = (water - centre) - 4f * spread;
                if (score > bestScore) { bestScore = score; bx = px * 16 + 8; by = py * 16 + 8; bz = centre; }
            }
        float eye = water - 0.5f;
        _underwaterGround = eye - bz;
        cam.Near = 0.1f;
        cam.LookAtFromPosition(new Vector3(bx, eye, -by), new Vector3(bx, bz, -by), new Vector3(0f, 0f, -1f));
    }

    /// <summary>
    /// The haze on real ground: the middle of the picture, straight down. With --tr-water-compare naming the same view without the
    /// below-surface drawing, each of its pixels hazed over the known distance (<see cref="UnderwaterView"/>) must be what is shown here.
    /// </summary>
    private void UnderwaterGroundProbe(Image img, float distance, EnvironmentLayer? env)
    {
        if (_waterCompare is null) { _result["underwater_ground"] = new { distance }; return; }   // the picture to compare with is being made
        if (env?.Frame is not { } frame || env.WaterApplied is not { } water || !File.Exists(_waterCompare)) { _failures.Add("underwater ground: no frame or no picture to compare with"); return; }
        var other = Image.LoadFromFile(_waterCompare); other.Convert(Image.Format.Rgba8);
        static float Lin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        static float Shown(float c) { c = Math.Clamp(c, 0f, 1f); return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f; }
        bool night = frame.SunDirection.Z < 0;
        float slant = WaterShading.FogSlant(night ? frame.MoonDirection.Z : frame.SunDirection.Z);
        var (light, survives) = UnderwaterView.Fog(water.FogColor, UnderwaterView.FogDensity(water.FogDensity, water.FogMod, underwater: true), slant, 0.5f, -1f, distance);
        int cx = img.GetWidth() / 2, cy = img.GetHeight() / 2;
        var got = new float[3]; var want = new float[3]; var bare = new float[3];
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                var c = img.GetPixel(cx + dx, cy + dy); var o = other.GetPixel(cx + dx, cy + dy);
                var w = UnderwaterView.Haze(new System.Numerics.Vector3(Lin(o.R), Lin(o.G), Lin(o.B)), light, survives);
                got[0] += c.R / 81f; got[1] += c.G / 81f; got[2] += c.B / 81f;
                want[0] += Shown(w.X) / 81f; want[1] += Shown(w.Y) / 81f; want[2] += Shown(w.Z) / 81f;
                bare[0] += o.R / 81f; bare[1] += o.G / 81f; bare[2] += o.B / 81f;
            }
        bool pass = true;
        for (int i = 0; i < 3; i++) if (MathF.Abs(got[i] - want[i]) > 0.015f) pass = false;
        _result["underwater_ground"] = new { distance, got, want, bare, pass };
        if (!pass) _failures.Add("underwater ground: the ground is not hazed as the official formulas give: " + System.Text.Json.JsonSerializer.Serialize(new { distance, got, want, bare }));
    }

    /// <summary>
    /// --tr-water-probes: open water (no bottom in view) at several angles below the horizon, centre column, against
    /// <see cref="WaterShading.Surface"/> (checked in the core tests against an independent transcription of the official formulas) with the
    /// sky's reflection from <see cref="SkyLighting.DomeColour"/> at the mirrored elevation and the ray's angle from the light. Linear values, tolerance 0.04 (8-bit pictures, the profile's
    /// interpolation, the ripples' small tilt). The water draws without the engine's fog. The camera must be farther than 60 m from every probed point.
    /// </summary>
    private void WaterProbes(Image img, Camera3D cam, EnvironmentLayer? env)
    {
        if (env?.Frame is not { } frame || env.WaterApplied is not { } water) { _failures.Add("water probes: no water or environment frame"); return; }
        System.Numerics.Vector3 Sky(double deg, double az) => SkyLighting.DomeColour(frame, (float)deg, (float)az);
        static float Lin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        bool night = frame.SunDirection.Z < 0;
        float slant = WaterShading.FogSlant(night ? frame.MoonDirection.Z : frame.SunDirection.Z);
        int w = img.GetWidth(), h = img.GetHeight(), x = w / 2;
        var rows = new List<object>();
        int inView = 0; bool allPass = true;
        foreach (double below in new[] { 2.0, 4.0, 8.0, 15.0, 25.0 })
        {
            int best = -1; double bestGap = 1e9, bestElev = 0;
            for (int y = 2; y < h - 2; y++)
            {
                var n = cam.ProjectRayNormal(new Vector2(x, y));
                double elev = Math.Asin(Math.Clamp(n.Y, -1f, 1f)) * 180.0 / Math.PI;
                if (Math.Abs(elev + below) < bestGap) { bestGap = Math.Abs(elev + below); best = y; bestElev = -elev; }
            }
            if (best < 0 || bestGap > 0.7) continue;
            inView++;
            var px = new float[3];
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) { var c = img.GetPixel(x + dx, best + dy); px[0] += c.R / 9f; px[1] += c.G / 9f; px[2] += c.B / 9f; }
            var got = new[] { Lin(px[0]), Lin(px[1]), Lin(px[2]) };
            // the official value, worked here and not through WaterShading.Surface: open water is the fog's light blended with the sky's reflection by the fresnel weight, and the
            // reflection is the captured sky (the sky as it is shown in the mirrored direction) times the fresnel factor and nothing else (waterF.glsl: radiance *= df2.y,
            // color = mix(fb, radiance, min(1, df2.x)); the sampled probe radiance is the rendered sky, with no further factor)
            float cosI = (float)Math.Sin(bestElev * Math.PI / 180);
            var sky = Sky(bestElev, AzimuthFromLight(frame, cam.ProjectRayNormal(new Vector2(x, best))));
            var (fogLight, _) = WaterShading.Fog(water.FogColor, water.FogDensity, slant, cosI, 4000f);
            var (fw, ff) = WaterShading.Fresnel(cosI, water.FresnelScale, water.FresnelOffset);
            var wantV = System.Numerics.Vector3.Lerp(fogLight, sky * ff, fw);
            var want = new[] { wantV.X, wantV.Y, wantV.Z };
            bool pass = true;
            for (int i = 0; i < 3; i++) if (MathF.Abs(got[i] - want[i]) > 0.04f) pass = false;
            allPass &= pass;
            rows.Add(new { below = Math.Round(bestElev, 2), row = best, got, want, pass });
        }
        _result["water_probes"] = new { probes = rows, in_view = inView, pass = allPass && inView >= 3 };
        if (inView < 3) _failures.Add($"water probes: only {inView} probe rows in view (3 needed)");
        else if (!allPass) _failures.Add("water probes: the picture's water differs from the official colour at some angle: " + System.Text.Json.JsonSerializer.Serialize(rows));
    }

    /// <summary>The layered-transparency scenes' cameras (--tr-camera=layered, layered-water): the eye (SL axes), else null.</summary>
    private System.Numerics.Vector3? Layered => _camera switch { "layered" => LayeredZoo.Eye, "layered-water" => LayeredZoo.WaterEye, _ => null };

    /// <summary>
    /// The layered-transparency probes: each probe's pixel (its direction from the camera projected), its colour, and the
    /// colour the blend arithmetic expects there (<see cref="LayeredZoo.Expected"/>, from the reference probes where the
    /// scene's lighting or water decides part of it); a probe passes within the scene's tolerance per channel.
    /// </summary>
    private static object LayeredProbes(Image img, Camera3D cam, bool water)
    {
        var probes = water ? LayeredZoo.WaterProbes : LayeredZoo.Probes;
        var at = new Dictionary<string, (Vector2I Px, int[] Rgb)>();
        foreach (var p in probes)
        {
            var d = LayeredZoo.Direction(p);
            var px = cam.UnprojectPosition(cam.GlobalPosition + new Vector3(d.X, d.Z, -d.Y) * 5f);
            var xy = new Vector2I((int)MathF.Round(px.X), (int)MathF.Round(px.Y));
            var c = img.GetPixel(xy.X, xy.Y);
            at[p.Name] = (xy, [c.R8, c.G8, c.B8]);
        }
        static System.Numerics.Vector3 Lin(int[] c) => new(LayeredZoo.ToLinear(c[0] / 255f), LayeredZoo.ToLinear(c[1] / 255f), LayeredZoo.ToLinear(c[2] / 255f));
        var expected = LayeredZoo.Expected(water, at.ToDictionary(kv => kv.Key, kv => Lin(kv.Value.Rgb)));
        int tol = water ? LayeredZoo.WaterTolerance : LayeredZoo.Tolerance;
        var rows = probes.Select(p =>
        {
            var (px, rgb) = at[p.Name];
            int[]? want = expected.TryGetValue(p.Name, out var e) ? LayeredZoo.Srgb8(e) : null;
            int? diff = want is null ? null : Enumerable.Range(0, 3).Max(i => Math.Abs(rgb[i] - want[i]));
            return new { name = p.Name, x = px.X, y = px.Y, rgb, expected = want, what = p.Expected, diff, pass = diff is null || diff <= tol };
        }).ToList();
        return new { tolerance = tol, pass = rows.All(r => r.pass), checked_probes = rows.Count(r => r.expected is not null), rows };
    }

    /// <summary>
    /// The projector scene's probes (<see cref="Viewer.Shell.ProjectorZoo.Probes"/>): a patch of pixels around each point. Inside a beam the patch
    /// must show the pattern (bright cells, contrast) and be clearly brighter than every point outside; outside, it must be dark, flat and clearly
    /// darker than its lamp's own wall patch. With the lights off nothing differs from its surround, so most probes fail.
    /// </summary>
    private static object ProjectorProbes(Image img, Camera3D cam)
    {
        const int Half = 15, Gap = 30, Contrast = 30, Flat = 12;
        var probes = Viewer.Shell.ProjectorZoo.Probes();
        double Luma(Color c) => 0.2126 * c.R8 + 0.7152 * c.G8 + 0.0722 * c.B8;
        (Vector2I Px, double Max, double Min) Patch(System.Numerics.Vector3 p)
        {
            var q = cam.UnprojectPosition(new Vector3(p.X, p.Z, -p.Y));
            var xy = new Vector2I((int)MathF.Round(q.X), (int)MathF.Round(q.Y));
            double max = 0, min = 255;
            for (int dy = -Half; dy <= Half; dy++)
                for (int dx = -Half; dx <= Half; dx++)
                {
                    double l = Luma(img.GetPixel(Math.Clamp(xy.X + dx, 0, img.GetWidth() - 1), Math.Clamp(xy.Y + dy, 0, img.GetHeight() - 1)));
                    max = Math.Max(max, l); min = Math.Min(min, l);
                }
            return (xy, max, min);
        }
        var patches = probes.ToDictionary(p => p.Name, p => Patch(p.Point));
        double outMax = probes.Where(p => !p.Inside).Max(p => patches[p.Name].Max);
        var rows = probes.Select(p =>
        {
            var (px, max, min) = patches[p.Name];
            double contrast = max - min;
            bool pass;
            string what;
            if (p.Inside)
            {
                pass = max >= outMax + Gap && contrast >= Contrast;
                what = $"inside the beam: brightest {Gap} above the brightest outside point, and the pattern's contrast at least {Contrast}";
            }
            else
            {
                double wall = patches[Viewer.Shell.ProjectorZoo.Lamps[p.Lamp].Name + "-wall-in"].Max;
                pass = max <= wall - Gap && contrast <= Flat;
                what = $"outside the beam: {Gap} darker than its lamp's wall patch, and flat (contrast at most {Flat})";
            }
            return new { name = p.Name, x = px.X, y = px.Y, inside = p.Inside, max = Math.Round(max, 1), contrast = Math.Round(contrast, 1), what, pass };
        }).ToList();
        return new { patch = 2 * Half + 1, pass = rows.All(r => r.pass), checked_probes = rows.Count, passed_probes = rows.Count(r => r.pass), rows };
    }

    /// <summary>
    /// The projector scene's shadow probes (<see cref="Viewer.Shell.ProjectorZoo.ShadowProbes"/>): two patches on the wall inside one beam, one behind the
    /// occluder and one clear of it. With shadows drawn the first must be dark and the second lit, at least <c>Gap</c> brighter; with shadows off both
    /// are lit alike, so the check fails. (The lamp's picture is soft, so the pattern's contrast is not used.)
    /// </summary>
    private static object ProjectorShadowProbes(Image img, Camera3D cam)
    {
        const int Half = 6, Gap = 40;   // small patches: the shadow is a small square in the picture
        double Luma(Color c) => 0.2126 * c.R8 + 0.7152 * c.G8 + 0.0722 * c.B8;
        var rows = Viewer.Shell.ProjectorZoo.ShadowProbes().Select(p =>
        {
            var q = cam.UnprojectPosition(new Vector3(p.Point.X, p.Point.Z, -p.Point.Y));
            var xy = new Vector2I((int)MathF.Round(q.X), (int)MathF.Round(q.Y));
            double max = 0, min = 255;
            for (int dy = -Half; dy <= Half; dy++)
                for (int dx = -Half; dx <= Half; dx++)
                {
                    double l = Luma(img.GetPixel(Math.Clamp(xy.X + dx, 0, img.GetWidth() - 1), Math.Clamp(xy.Y + dy, 0, img.GetHeight() - 1)));
                    max = Math.Max(max, l); min = Math.Min(min, l);
                }
            return (p.Name, p.Shadowed, xy, max, contrast: max - min);
        }).ToList();
        double litMax = rows.Where(r => !r.Shadowed).Max(r => r.max), darkMax = rows.Where(r => r.Shadowed).Max(r => r.max);
        var report = rows.Select(r => new
        {
            name = r.Name, x = r.xy.X, y = r.xy.Y, shadowed = r.Shadowed, max = Math.Round(r.max, 1), contrast = Math.Round(r.contrast, 1),
            what = r.Shadowed ? $"in the occluder's shadow: at least {Gap} darker than the clear patch" : $"in the light: at least {Gap} brighter than the patch in the shadow",
            pass = litMax - darkMax >= Gap,
        }).ToList();
        return new { patch = 2 * Half + 1, pass = report.All(r => r.pass), checked_probes = report.Count, passed_probes = report.Count(r => r.pass), shadows = ProjectorLights.Shadows, rows = report };
    }

    /// <summary>
    /// The glow scene's probes (<see cref="GlowZoo"/>): at each panel one probe on its face and one beside it. The control
    /// row (glow 0) must show no halo; each flat panel's probes must show the colour the glow arithmetic gives, worked
    /// from the colours the view shows with no glow (its own control panel, the sky); the textured and PBR panels with
    /// glow must show a halo brighter than the sky where the sky is dark.
    /// </summary>
    private object GlowProbes(Image img, Camera3D cam)
    {
        float aspect = (float)img.GetWidth() / img.GetHeight();
        Vector2I Px(System.Numerics.Vector3 p) { var q = cam.UnprojectPosition(new Vector3(p.X, p.Z, -p.Y)); return new((int)MathF.Round(q.X), (int)MathF.Round(q.Y)); }
        int[] Rgb(Vector2I xy) { var c = img.GetPixel(Math.Clamp(xy.X, 0, img.GetWidth() - 1), Math.Clamp(xy.Y, 0, img.GetHeight() - 1)); return [c.R8, c.G8, c.B8]; }
        static float[] F(int[] c) => [c[0] / 255f, c[1] / 255f, c[2] / 255f];
        var sky = Rgb(new Vector2I(4, 4));
        var kinds = GlowZoo.Kinds;
        int Col(string k) => Array.IndexOf(kinds, k);
        // what each flat panel puts into the glow picture: the face's own colour as the view shows it with no glow, except
        // the blended panel, which gathers its own tint (not its tint over the sky)
        static float[] Tint(Viewer.Core.World.Rgba t) => [t.R, t.G, t.B];
        var inputs = new List<(int Kind, int Row, float[] Colour)>();
        for (int r = 1; r < GlowZoo.Glows.Length; r++)
        {
            inputs.Add((Col("coloured"), r, F(Rgb(Px(GlowZoo.ProbePoints(Col("coloured"), 0).Face)))));
            inputs.Add((Col("blended"), r, Tint(GlowZoo.BlendedTint)));
            inputs.Add((Col("fullbright"), r, Tint(GlowZoo.FullbrightTint)));
        }
        var rows = new List<Dictionary<string, object?>>();
        bool dark = _sky == "dark";
        for (int k = 0; k < kinds.Length; k++)
            for (int r = 0; r < GlowZoo.Glows.Length; r++)
            {
                var (face, halo) = GlowZoo.ProbePoints(k, r);
                foreach (var (what, point) in new[] { ("face", face), ("halo", halo) })
                {
                    var xy = Px(point);
                    var rgb = Rgb(xy);
                    bool flat = kinds[k] is "coloured" or "blended" or "fullbright";
                    int[]? want = null;
                    bool pass;
                    string rule;
                    if (r == 0 && kinds[k] == "pbr")
                    {
                        pass = true;   // the PBR control is the reference the other rows are read against
                        rule = "not checked";
                    }
                    else if (r == 0)
                    {
                        // the control row: no glow, so nothing beyond the face's own colour and the sky
                        want = what == "halo" ? sky : Rgb(Px(GlowZoo.ProbePoints(k, 0).Face));
                        pass = Enumerable.Range(0, 3).Max(i => Math.Abs(rgb[i] - want[i])) <= 3;
                        rule = "no glow: the sky";
                    }
                    else if (flat)
                    {
                        // the colour is read at a whole pixel, so the arithmetic is worked at that pixel's centre
                        float u = (xy.X + 0.5f) / img.GetWidth(), v = (xy.Y + 0.5f) / img.GetHeight();
                        var displayed = F(what == "halo" ? sky : Rgb(Px(GlowZoo.ProbePoints(k, 0).Face)));
                        want = GlowZoo.Expected(inputs, aspect, (u, v), displayed);
                        pass = Enumerable.Range(0, 3).Max(i => Math.Abs(rgb[i] - want[i])) <= GlowZoo.Tolerance;
                        rule = "the glow arithmetic";
                    }
                    else if (kinds[k] == "pbr")
                    {
                        // the PBR panel's glow comes from its emissive ring: the ring and the hole beside it must be brighter than the
                        // same places with no glow
                        var plain = Rgb(Px(what == "face" ? GlowZoo.ProbePoints(k, 0).Face : GlowZoo.ProbePoints(k, 0).Halo));
                        bool needed = GlowZoo.Glows[r] >= 0.5f;
                        pass = !needed || Enumerable.Range(0, 3).Max(i => rgb[i] - plain[i]) >= 4;
                        rule = needed ? "brighter than with no glow" : "not checked";
                    }
                    else
                    {
                        // textured: a halo brighter than the sky, wherever the sky leaves room for one
                        bool needed = what == "halo" && dark && GlowZoo.Glows[r] >= 0.5f;
                        pass = !needed || Enumerable.Range(0, 3).Max(i => rgb[i] - sky[i]) >= 4;
                        rule = needed ? "a halo brighter than the dark sky" : "not checked";
                    }
                    int? diff = want is null ? null : Enumerable.Range(0, 3).Max(i => Math.Abs(rgb[i] - want[i]));
                    rows.Add(new Dictionary<string, object?> { ["name"] = $"{kinds[k]}_{GlowZoo.Glows[r]:0.0}_{what}", ["x"] = xy.X, ["y"] = xy.Y, ["rgb"] = rgb, ["expected"] = want, ["what"] = rule, ["diff"] = diff, ["pass"] = pass });
                }
            }
        return new { tolerance = GlowZoo.Tolerance, sky, pass = rows.All(r => (bool)r["pass"]!), checked_probes = rows.Count(r => (string)r["what"]! != "not checked"), rows };
    }

    /// <summary>
    /// The glow scene timed: <c>--tr-glowtime=N</c> frames drawn with the glow pass on (wall time per frame, and the engine's
    /// own CPU and GPU time for the pass's viewports together), then N frames with it off, so the pass's cost is the
    /// difference and the engine's figure for it.
    /// </summary>
    private async Task<object> GlowTime(GlowPass pass, Rig rig, PrimLayer? prims = null)
    {
        var on = new List<double>(); var cpu = new List<double>(); var gpu = new List<double>(); var off = new List<double>();
        var mainCpuOn = new List<double>(); var mainGpuOn = new List<double>(); var mainCpuOff = new List<double>(); var mainGpuOff = new List<double>();
        var drawsOn = new List<double>(); var drawsOff = new List<double>(); var passDraws = new List<double>();
        var rid = rig.Viewport.GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(rid, true);
        double Draws() => RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
        async Task<double> One()
        {
            long t0 = Stopwatch.GetTimestamp();
            await Frames(1);
            return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }
        FreeRunning();   // frames as fast as they draw, so the pass's cost shows
        await Frames(10);
        for (int i = 0; i < _glowTimeFrames; i++)
        {
            on.Add(await One());
            var (c, g) = pass.PassMs();
            cpu.Add(c); gpu.Add(g);
            passDraws.Add(pass.PassDrawCalls());
            mainCpuOn.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid)); mainGpuOn.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid));
            drawsOn.Add(Draws());
        }
        GlowPass.Disabled = true;
        await Frames(10);
        for (int i = 0; i < _glowTimeFrames; i++)
        {
            off.Add(await One());
            mainCpuOff.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid)); mainGpuOff.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid));
            drawsOff.Add(Draws());
        }
        GlowPass.Disabled = false;
        // the same view with every object drawn in the glow picture: the chosen objects must give the very same picture
        await Frames(10);
        int chosenObjects = prims?.GlowPictureObjects ?? 0;
        double chosenDraws = pass.PassDrawCalls();
        int chosenChunks = ServerTerrainView.For(rig.Scenario)?.GlowPictureChunks ?? 0;
        var chosenPicture = pass.PictureImage()?.GetData();
        PrimLayer.TestGlowDrawAll = true;
        await Frames(10);
        int allObjects = prims?.GlowPictureObjects ?? 0;
        double allDraws = pass.PassDrawCalls();
        int allChunks = ServerTerrainView.For(rig.Scenario)?.GlowPictureChunks ?? 0;
        var allPicture = pass.PictureImage()?.GetData();
        PrimLayer.TestGlowDrawAll = false;
        int differing = chosenPicture is null || allPicture is null || chosenPicture.Length != allPicture.Length ? -1 : Enumerable.Range(0, chosenPicture.Length).Count(i => chosenPicture[i] != allPicture[i]);
        return new { frames = _glowTimeFrames, picture = new { chosen_objects = chosenObjects, chosen_chunks = chosenChunks, all_chunks = allChunks, chosen_draw_calls = chosenDraws, all_objects = allObjects, all_draw_calls = allDraws, differing_bytes = differing, bytes = chosenPicture?.Length ?? 0 }, size = new[] { rig.Viewport.Size.X, rig.Viewport.Size.Y }, on_ms = Dist(on), off_ms = Dist(off), pass_cpu_ms = Dist(cpu), pass_gpu_ms = Dist(gpu),
            main_cpu_on_ms = Dist(mainCpuOn), main_gpu_on_ms = Dist(mainGpuOn), main_cpu_off_ms = Dist(mainCpuOff), main_gpu_off_ms = Dist(mainGpuOff),
            draw_calls_on = Dist(drawsOn), draw_calls_off = Dist(drawsOff), pass_draw_calls = Dist(passDraws),
            glowing_objects = prims?.GlowObjects ?? 0, forced_object = prims?.ForcedGlowObject is { } fo ? new[] { fo.Centre.X, -fo.Centre.Z, fo.Centre.Y } : null };
    }

    /// <summary>Waits until the sky probe's capture of the terrain and water has been made for the sky in force (it takes a few frames), so a picture does not depend on when it is taken.</summary>
    private async Task SettleProbe(WorldPresenter presenter)
    {
        if (presenter.Environment is not { } env) return;
        for (int i = 0; i < 1200 && !env.ProbeSettled; i++) { presenter.Frame(); await Frames(1); }
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++)
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    }

    // ---------------- render (+ diff) ----------------

    private async Task<int> Render(string fixture, string viewKind, int frames, string? golden, string? alter)
    {
        var sw = Stopwatch.StartNew();
        s_alterSculpt = alter == "sculptmap";
        s_alterWater = alter == "water";
        s_alterOverride = alter == "override";
        s_alterNoLegacy = alter == "nolegacy";
        GlowPass.Disabled = alter == "noglow";   // the glow scene's negative control: no glow drawn, the probes must fail
        s_alterFallbackUnshared = alter == "fallback-unshared";
        var loaded = Load(fixture);
        double loadMs = sw.Elapsed.TotalMilliseconds;
        Phase("scene loaded");
        s_alterVertex = alter == "vertex";
        if (alter == "hill")
            AlterHill(loaded.World);
        else if (alter == "prim")
            AlterPrim(loaded.World);
        else if (alter == "offset")
            AlterOffset(loaded.World);
        else if (alter is not null && alter != "vertex" && alter != "sculptmap" && alter != "water" && alter != "override" && alter != "nolegacy" && alter != "fallback-unshared" && alter != "noglow" && alter != "nolightglow" && alter != "cloudsoff" && alter != "nohdr")
            throw new ArgumentException("--tr-alter must be hill, prim, offset, vertex, sculptmap, water, override or fallback-unshared");
        var region = loaded.World.Region!;
        var rig = MakeRig(new Vector2I(_renderW, _renderH));
        var view = MakeView(viewKind, rig);
        // a replayed capture with --tr-assets=<local cache>: meshes come from the cache through SessionAssets (misses are
        // NotFound), and the scene gets a fixed camera framed on its objects (the LOD rule measures from it).
        var meshes = loaded.Meshes ?? (_assetDir is not null ? CacheMeshSource(_assetDir, _contentStore ? RunContentView() : null) : null);
        bool scene = loaded.Meshes is null && meshes is not null;
        // a replayed capture's textures from the same local cache (only faces with texture entries use them).
        var textures = loaded.Textures ?? loaded.TerrainTextures ?? (scene ? CacheTextureSource(_assetDir!) : null);
        // PBR material assets: the fixture's (material zoo), or a replayed capture's from the same local cache
        var prims = new PrimLayer(rig.Scenario, loaded.World, meshes, textures, _budget)
        {
            SimulatedFps = 60,   // deterministic decisions for goldens and lifecycle checks
            MaterialAssets = loaded.MaterialAssets ?? (scene ? CacheMaterialSource(_assetDir!) : null),
            LegacyFallback = _legacyFallback,
        };
        if (fixture.EndsWith(".sculptzoo.json", StringComparison.OrdinalIgnoreCase)) prims.LodFactor = 2.0f;   // the sculpt scene's golden was made at the factor 2.0
        if (loaded.IsColourChart)
        {
            // the chart's own (orthographic) camera before presenting, so discard levels are chosen for the view drawn
            Stage.ColourCamera(rig.Camera, ColourChart.Origin, ColourChart.OrthoSize);
            prims.Camera = rig.Camera.GlobalPosition;
        }
        else if (loaded.Textures is not null)
        {
            if (_camera == "detail") Stage.DetailCamera(rig.Camera);   // the detail-at-a-distance scene's own camera
            else if (Layered is { } eye) Stage.LayeredCamera(rig.Camera, eye);   // the layered-transparency scenes' own cameras
            else if (_camera == "glow") Stage.GlowCamera(rig.Camera);   // the glow scene's own camera
            else if (_camera == "projector") Stage.ProjectorCamera(rig.Camera);   // the projector scene's own camera
            else Stage.ZooCamera(rig.Camera);   // the texture zoo sits on the prim zoo's grid
            prims.Camera = rig.Camera.GlobalPosition;
        }
        else if (scene)
        {
            Stage.SceneCamera(rig.Camera, loaded.World);
            prims.Camera = rig.Camera.GlobalPosition;
        }
        else if (loaded.Meshes is not null)
        {
            Stage.MeshCamera(rig.Camera, _camera);   // before presenting: the LOD rule measures from it
            prims.Camera = rig.Camera.GlobalPosition;
        }
        if (_look is { } lk)
        {
            // a review view: the camera at the eye, looking at the target (region coordinates), 60 degrees
            rig.Camera.Fov = 60f; rig.Camera.Near = 0.2f; rig.Camera.Far = 1000f;
            rig.Camera.Position = new Vector3(lk.Eye.X, lk.Eye.Z, -lk.Eye.Y);
            rig.Camera.LookAt(new Vector3(lk.Target.X, lk.Target.Z, -lk.Target.Y), Vector3.Up);
            prims.Camera = rig.Camera.GlobalPosition;
        }
        prims.ResidencyView = PrimLayer.ViewOf(rig.Camera, _renderW, _renderH);   // priorities from the render's camera
        var presenter = new WorldPresenter(loaded.World, view, prims: prims, terrainTextures: textures,
            environment: new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun) { CloudsEnabled = CloudsDrawn, WaterFlat = _waterFlat || _waterProbes || _underwaterProbes || _underwaterGround is not null, UnderwaterOff = _underwaterOff, WaterTimeOffset = _waterTime, HdrEnabled = alter != "nohdr" })   // nohdr: the tone-mapped path's negative control
        {
            CloudScrollSeconds = _clouds ? _cloudScrollSeconds : null,
            DayFractionOverride = _skyChoice is not null ? null : loaded.DayFraction ?? double.Parse(_day ?? "0.3", System.Globalization.CultureInfo.InvariantCulture),   // fixed time of day
            Sky = _skyChoice ?? Viewer.Core.World.SkyChoice.Region,
            Personal = _personalSkipApply ? null : _personal,
        };
        var glowPass = new GlowPass(prims, rig.World, rig.Environment);   // the glow of faces that glow
        rig.Root.AddChild(glowPass);
        sw.Restart();
        object? held = null;
        if (s_holdTexture is { } hold && textures is not null)
        {
            // the held texture's fetch waits: everything else settles first, then what the PBR faces draw is recorded
            // (and an image saved for viewing) before it is released and the scene settles as usual
            held = await HeldPhase(presenter, prims, meshes, textures, hold, rig);
            s_holdGate.TrySetResult();
        }
        await SettleMeshes(presenter, prims, meshes, textures);
        double buildMs = sw.Elapsed.TotalMilliseconds;
        Phase("scene built");
        bool zoo = IsZoo(loaded.World);
        if (loaded.IsColourChart)
            Stage.ColourCamera(rig.Camera, ColourChart.Origin, ColourChart.OrthoSize);
        else if (loaded.Meshes is not null || scene || _look is not null) { }
        else if (zoo && _camera != "detail" && _camera != "glow" && _camera != "projector" && Layered is null)
            Stage.ZooCamera(rig.Camera);
        else if (!zoo)
            Stage.Overview(rig.Camera, region.SizeX, region.SizeY);
        if (GlowZoo.Skies.TryGetValue(_sky, out var skyRgb))
        {
            // the glow scene's sky: a plain colour of the given sRGB value, no haze (set last, after the presenter's own)
            var e = Stage.EnvironmentOf(rig.Environment);
            e.Sky = null;
            e.BackgroundMode = global::Godot.Environment.BGMode.Color;
            e.BackgroundColor = new Color(skyRgb[0] / 255f, skyRgb[1] / 255f, skyRgb[2] / 255f);   // the engine takes the colour as it is to be shown
            e.FogEnabled = false;
        }
        if (_camera == "projector")
        {
            // the projector scene is nearly dark, so the beams are what lights it: no sun, a faint ambient light, a dark plain sky
            var e = Stage.EnvironmentOf(rig.Environment);
            e.Sky = null;
            e.BackgroundMode = global::Godot.Environment.BGMode.Color;
            e.BackgroundColor = new Color(0.01f, 0.01f, 0.02f);
            e.FogEnabled = false;
            e.AmbientLightSource = global::Godot.Environment.AmbientSource.Color;
            e.AmbientLightColor = new Color(0.5f, 0.55f, 0.7f);
            e.AmbientLightEnergy = ProjectorAmbient;
            rig.Sun.LightEnergy = 0f;
            switch (PrimLayer.TestLights)
            {
                case "sun": rig.Sun.LightEnergy = 1.1f; break;
                case "moon":   // the night's light: the same sun node, driven as the environment does (EnvironmentLayer): the moon's colour and direction
                    rig.Sun.LightEnergy = 0.5f; rig.Sun.LightColor = new Color(0.35f, 0.4f, 0.6f); break;
                case "ambient": e.AmbientLightEnergy = 0.6f; break;
            }
            if (ProjectorLights.TestViewLayerOnly) rig.Sun.Layers = 1u;
        }
        AddBanWall(rig, loaded.World);
        AddInteractionScene(rig, loaded.World);
        AddDialogPanel(rig);
        if (_underwaterGround is not null) AimAtDeepGround(loaded.World, rig.Camera);
        prims.ViewCamera = rig.Camera;   // blended faces in order for the camera drawn
        prims.UpdateBlendOrder();
        if (PrimLayer.MergeStill) await MergeSettle(prims, rig.Camera);
        await SwitchClouds(presenter);
        await SettleProbe(presenter);
        Phase("scene ready");
        await Frames(Math.Max(2, frames));
        Phase("frames drawn");
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        string png = Path.Combine(_out, _tag + ".png");
        Directory.CreateDirectory(_out);
        img.SavePng(png);
        Phase("picture saved");
        if (Layered is not null) _result["probes"] = LayeredProbes(img, rig.Camera, _camera == "layered-water");
        if (_skyProbes) SkyProbes(img, rig.Camera, presenter.Environment?.Frame);
        if (_waterProbes) WaterProbes(img, rig.Camera, presenter.Environment);
        if (_underwaterProbes) UnderwaterProbes(img, rig.Camera, presenter.Environment);
        if (_underwaterGround is { } groundDistance) UnderwaterGroundProbe(img, groundDistance, presenter.Environment);
        if (presenter.Environment is { HasWater: true } drawn) _result["underwater"] = new { drawn = drawn.UnderwaterDrawn };
        if (_waterLook is { } wlook) WaterLookMetrics(img, wlook);
        if (_cloudProbes) CloudProbes(img, rig.Camera, presenter.Environment?.Frame);
        if (_cloudFrames > 0) await CloudFrameTimes(rig.Viewport);
        if (_camera == "projector")
        {
            _result["probes"] = ProjectorProbes(img, rig.Camera);
            _result["shadow_probes"] = ProjectorShadowProbes(img, rig.Camera);
            var pe = Stage.EnvironmentOf(rig.Environment);
            var (omni, spot) = prims.LightKinds;
            // a glowing face lit only by the chosen light: its glow weight is 1, so the glow picture must hold the face as the view shows it
            // (ProjectorZoo: the panel is white and glows at 1), read where the view puts the panel; the view's own pixel is read with the glow pass off
            var (pu, pv) = Viewer.Shell.ProjectorZoo.Project(Viewer.Shell.ProjectorZoo.GlowPanel, (float)img.GetWidth() / img.GetHeight());
            int[] At(Image im) { var c = im.GetPixel(Math.Clamp((int)(pu * im.GetWidth()), 0, im.GetWidth() - 1), Math.Clamp((int)(pv * im.GetHeight()), 0, im.GetHeight() - 1)); return [c.R8, c.G8, c.B8]; }
            var picture = glowPass.PictureImage();
            int[]? inPicture = null;
            if (picture is not null) { picture.Convert(Image.Format.Rgba8); inPicture = At(picture); }
            GlowPass.Disabled = true;
            await Frames(3);
            var plain = rig.Texture.GetImage();
            plain.Convert(Image.Format.Rgba8);
            var inView = At(plain);
            GlowPass.Disabled = false;
            await Frames(2);
            _result["glow_light"] = new { lights = PrimLayer.TestLights ?? "spot", picture = inPicture, view_without_glow = inView, max_diff = inPicture is null ? -1 : Enumerable.Range(0, 3).Max(i => Math.Abs(inPicture[i] - inView[i])) };
            _result["projector"] = new { omni_lights = omni, spot_lights = spot, sun_energy = rig.Sun.LightEnergy, ambient_energy = pe.AmbientLightEnergy, ambient_source = pe.AmbientLightSource.ToString(), glow_active = glowPass.Active };
        }
        if (_camera == "glow")
        {
            _result["probes"] = GlowProbes(img, rig.Camera);
            _result["glow"] = new { objects = prims.GlowObjects, active = glowPass.Active };
            // the glow picture before and after its blur, for review
            glowPass.PictureImage()?.SavePng(Path.Combine(_out, _tag + "-glow-picture.png"));
            glowPass.BlurredImage()?.SavePng(Path.Combine(_out, _tag + "-glow-blurred.png"));
            if (_glowTimeFrames > 0) _result["glowtime"] = await GlowTime(glowPass, rig, prims);
        }
        else if (_glowTimeFrames > 0) _result["glowtime"] = await GlowTime(glowPass, rig, prims);

        if (_census || PrimLayer.MergeStill) _result["census"] = CensusReport(prims, glowPass, rig, view.ChunkCount);
        _result["mode"] = "render";
        _result["fixture"] = new Dictionary<string, object?> { ["description"] = loaded.Description, ["sha256"] = loaded.Sha256, ["altered"] = alter };
        _result["region"] = new { size_x = region.SizeX, size_y = region.SizeY, patches = loaded.World.TerrainPatchCount, expected = loaded.World.TerrainExpected, complete = loaded.World.TerrainComplete };
        _result["render"] = new Dictionary<string, object?>
        {
            ["width"] = _renderW, ["height"] = _renderH, ["frames"] = frames, ["view"] = view.Kind, ["chunks"] = view.ChunkCount,
            ["camera"] = new { position = V(rig.Camera.GlobalPosition), fov = rig.Camera.Fov },
            ["load_ms"] = Math.Round(loadMs, 1), ["build_ms"] = Math.Round(buildMs, 1), ["png"] = Path.GetFileName(png),
        };
        int objectCount = loaded.World.Snapshot().Objects.Count;
        if (_look is not null || scene)
        {
            // the scene's linksets (roots with their prim count, the largest prim's scale and how many faces carry a PBR
            // material), largest first: where to point a review view
            var objs = loaded.World.Snapshot().Objects.Values.Where(o => o.AttachmentPoint is null).ToList();
            _result["linksets"] = objs.Where(o => o.ParentLocalId == 0).Select(r =>
            {
                var parts = objs.Where(o => o.Id.LocalId == r.Id.LocalId || o.ParentLocalId == r.Id.LocalId).ToList();
                return new
                {
                    root = r.Id.LocalId, position = new[] { r.Position.X, r.Position.Y, r.Position.Z }, prims = parts.Count,
                    largest = parts.Max(o => o.Scale.Length()), meshes = parts.Count(o => o.Shape is { SculptType: Viewer.Core.Meshing.PrimShape.SculptTypeMesh }),
                    material_faces = parts.Sum(o => o.Materials is { } m ? Enumerable.Range(0, 8).Count(f => m[f].MaterialId != Guid.Empty) : 0),
                };
            }).OrderByDescending(x => x.largest * x.prims).Take(20).ToList();
        }
        // the objects with a blended face, most places first, with where their first blended face is (region
        // coordinates): where to point a review view at layered transparency
        _result["blended"] = prims.BlendReport().OrderByDescending(b => b.Places).ThenBy(b => b.Local).Take(20)
            .Select(b => new { local = b.Local, places = b.Places, at = new[] { b.Centre.X, -b.Centre.Z, b.Centre.Y } }).ToList();
        if (_orbit)
            await ContactSheet(rig, loaded.World, prims);
        if (presenter.Environment?.Frame is { } sky)
            _result["environment"] = new
            {
                source = presenter.EnvironmentSource, day_fraction = presenter.DayFractionApplied, water_height = presenter.Environment.WaterHeight, water_drawn = presenter.Environment.HasWater,
                sun_direction = new[] { sky.SunDirection.X, sky.SunDirection.Y, sky.SunDirection.Z }, sun_colour = new[] { sky.SunColor.X, sky.SunColor.Y, sky.SunColor.Z },
                probe_ambiance = sky.ProbeAmbiance, hdr_active = presenter.Environment.HdrActive, hdr_passes = HdrTonemapEffect.Ran, hdr_error = HdrTonemapEffect.SetupError,
            };
        if (presenter.DetailSources is { } ds)
            _result["terrain_textures"] = new { layers = ds, start = region.Terrain!.StartHeights.ToArray(), range = region.Terrain!.HeightRanges.ToArray() };
        _result["prims"] = new Dictionary<string, object?>
        {
            ["objects"] = objectCount, ["instances"] = prims.Instances, ["meshes"] = prims.Meshes, ["materials"] = prims.Materials,
            ["skipped_sculpt_or_mesh"] = prims.SkippedSculptOrMesh, ["skipped_no_shape"] = prims.SkippedNoShape, ["skipped_attachments"] = prims.SkippedAttachments,
            ["mesh_objects"] = prims.MeshObjects, ["mesh_resources"] = prims.MeshAssetResources, ["skipped_mesh_unavailable"] = prims.SkippedMeshUnavailable,
            ["mesh_builds"] = prims.MeshBuildReport(),   // (measurement)
            ["mesh_lods"] = prims.MeshLods.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray(), ["camera_preset"] = loaded.Meshes is null ? (scene ? "scene" : null) : _camera,
            ["gpu_textures"] = prims.GpuTextures, ["textured_materials"] = prims.TexturedMaterials, ["textures_waiting"] = prims.TexturesWaiting, ["auto_masked_faces"] = prims.AutoMaskedFaces,
            ["pbr_faces"] = prims.PbrFaces, ["pbr_faces_not_opaque"] = prims.PbrFacesNotOpaque, ["pbr_faces_beyond_geometry"] = prims.PbrFacesBeyondGeometry, ["pbr_materials"] = prims.PbrMaterials, ["materials_waiting"] = prims.MaterialsWaiting,
            ["material_assets_ready"] = prims.MaterialAssets?.Ready ?? 0, ["material_assets_unavailable"] = prims.MaterialAssets?.Unavailable ?? 0,
            ["pbr_detail_maps_in_view"] = prims.DetailMapsInView().InView, ["pbr_detail_maps_below_level"] = prims.DetailMapsInView().BelowLevel,
            ["legacy_material_faces"] = prims.LegacyMaterialFaces, ["legacy_materials"] = prims.LegacyMaterialResources, ["legacy_materials_waiting"] = prims.LegacyMaterialsWaiting,
            // the legacy fallback: on or off, what the PBR faces drew while a texture was held, and at the end; the
            // fixture textures' fetches (a fallback never asks for one)
            ["fallback"] = new Dictionary<string, object?>
            {
                ["enabled"] = prims.LegacyFallback, ["held"] = held, ["faces_end"] = prims.FallbackFaces, ["waiting_end"] = prims.PbrFacesWaiting,
                ["switches"] = prims.FallbackSwitches, ["textures"] = prims.FallbackTextures.Select(g => g.ToString()).OrderBy(x => x).ToArray(),
                ["fetches"] = s_textureFetches.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            },
            ["sculpt_objects"] = prims.SculptObjects, ["sculpt_resources"] = prims.SculptResources, ["sculpt_placeholders"] = prims.SculptPlaceholders,
            ["skipped_sculpt_empty"] = prims.SkippedSculptEmpty, ["sculpt_lods"] = prims.SculptLods,
            ["mesh_lod_histogram"] = prims.MeshLods.Values.GroupBy(l => l).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            // each texture's level on the GPU and the level its size on screen wants (-1: none on the GPU)
            ["texture_levels"] = prims.Residency is { } tr ? tr.All.OrderBy(id => id).ToDictionary(id => id.ToString(), id => new[] { tr.LevelOf(id), tr.DesiredLevelOf(id) }) : null,
            ["objects_with_shape"] = loaded.World.Snapshot().Objects.Values.Count(o => o.Shape is not null),
            ["mesh_objects_in_world"] = loaded.World.Snapshot().Objects.Values.Count(o => o.Shape is { SculptType: PrimShape.SculptTypeMesh }),
            ["sculpt_objects_in_world"] = loaded.World.Snapshot().Objects.Values.Count(o => o.Shape is { SculptType: > 0 and < PrimShape.SculptTypeMesh }),
        };
        if (prims.Residency is { } res)
            _result["residency"] = new Dictionary<string, object?>
            {
                ["budget_bytes"] = res.BudgetBytes, ["resident_bytes"] = res.ResidentBytes, ["resident"] = res.Resident, ["on_placeholders"] = res.OnPlaceholders,
                ["evictions"] = res.Evictions, ["loads"] = res.Loads, ["awaiting_grant"] = prims.TexturesAwaitingGrant,
                ["texture_mem_used"] = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed),
                ["steps_down"] = res.StepsDown, ["steps_up"] = res.StepsUp,
                ["textures"] = res.All.OrderByDescending(res.PriorityOf).Select(id => new { id = id.ToString(), state = res.StateOf(id).ToString(), priority = Math.Round(res.PriorityOf(id), 3), visible = res.IsVisible(id), bytes = res.BytesOf(id),
                    level = res.LevelOf(id), desired = res.DesiredLevelOf(id), levels = res.LevelsOf(id) }).ToArray(),
            };
        if (loaded.Meshes is not null && loaded.Textures is null)
        {
            // move the camera to the other preset in the same run: UpdateLods must re-present every mesh at its new LOD.
            var before = prims.MeshLods.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();
            Stage.MeshCamera(rig.Camera, _camera == "far" ? "near" : "far");
            prims.Camera = rig.Camera.GlobalPosition;
            prims.UpdateLods();
            int marked = prims.Dirty;
            await SettleMeshes(presenter, prims, loaded.Meshes);
            var after = prims.MeshLods.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();
            _result["camera_move"] = new Dictionary<string, object?> { ["lods_before"] = before, ["lods_after"] = after, ["marked_dirty"] = marked, ["mesh_resources_after"] = prims.MeshAssetResources };
            if (before.SequenceEqual(after) || marked != before.Length)
                _failures.Add($"camera move did not switch LODs: [{string.Join(",", before)}] -> [{string.Join(",", after)}], {marked} marked");
        }
        if (loaded.IsColourChart)
        {
            // fullbright chart and tints within +/-2 of the source sRGB values; the lit face for sanity only.
            var m = ColourChart.Measure(img.GetData(), Width, Height);
            _result["colour"] = new Dictionary<string, object?>
            {
                ["orientation"] = m.Orientation, ["chart_max_error"] = m.ChartMaxError,
                ["worst_patch"] = new { column = m.Worst.Column, row = m.Worst.Row, expected = new[] { m.Worst.Expected.R, m.Worst.Expected.G, m.Worst.Expected.B }, got = new[] { m.Worst.Got.R, m.Worst.Got.G, m.Worst.Got.B } },
                ["tint_expected"] = new[] { (int)ColourChart.Tint.R, ColourChart.Tint.G, ColourChart.Tint.B }, ["tint_got"] = new[] { m.TintGot.R, m.TintGot.G, m.TintGot.B }, ["tint_error"] = m.TintError,
                ["grey_x_grey_expected"] = (int)ColourChart.GreyTimesGrey, ["grey_x_grey_got"] = new[] { m.GreyGot.R, m.GreyGot.G, m.GreyGot.B }, ["grey_x_grey_error"] = m.GreyError,
                ["lit_mean"] = Math.Round(m.LitMean, 1), ["tolerance"] = m.FullbrightTolerance,
            };
            if (m.Orientation != "upright")
                _failures.Add($"colour chart: the top face shows the chart {m.Orientation}; SL shows it upright seen from above, north up");
            if (m.ChartMaxError > m.FullbrightTolerance)
                _failures.Add($"colour chart: patch ({m.Worst.Column},{m.Worst.Row}) expected {m.Worst.Expected} got {m.Worst.Got} (error {m.ChartMaxError} > {m.FullbrightTolerance})");
            if (m.TintError > m.FullbrightTolerance)
                _failures.Add($"white x tint: expected {ColourChart.Tint} got {m.TintGot} (error {m.TintError})");
            if (m.GreyError > m.FullbrightTolerance)
                _failures.Add($"grey x grey (sRGB composition): expected {ColourChart.GreyTimesGrey} got {m.GreyGot} (error {m.GreyError})");
            if (m.LitMean < 15 || m.LitMean > 240)
                _failures.Add($"lit chart face: mean {m.LitMean:0.0} outside 15-240");
        }
        if (zoo && prims.Instances != objectCount)
            _failures.Add($"prim zoo: {prims.Instances} of {objectCount} prims presented");
        if (!zoo && !loaded.World.TerrainComplete)
            _failures.Add($"terrain incomplete: {loaded.World.TerrainPatchCount}/{loaded.World.TerrainExpected}");

        if (golden is not null)
        {
            if (!Path.IsPathFullyQualified(golden) || !File.Exists(golden))
                _failures.Add("golden not found: " + Path.GetFileName(golden));
            else
            {
                var g = Image.LoadFromFile(golden);
                g.Convert(Image.Format.Rgba8);
                // compared at the size the picture was drawn at (a look run can ask for another size with --tr-size)
                int pw = img.GetWidth(), ph = img.GetHeight();
                // a script dialog drawn over the terrain is an interface picture: a changed button label must fail
                var tolerance = _dialogButtons > 0 ? DiffTolerance.Ui : DiffTolerance.Terrain;
                var diff = g.GetWidth() != pw || g.GetHeight() != ph
                    ? new DiffResult(false, g.GetWidth(), g.GetHeight(), 0, 0, 0, 0, "golden size differs")
                    : ImageDiff.Compare(img.GetData(), g.GetData(), pw, ph, 4, tolerance);
                _result["diff"] = new Dictionary<string, object?>
                {
                    ["golden"] = Path.GetFileName(golden), ["pass"] = diff.Pass, ["differing"] = diff.Differing,
                    ["differing_fraction"] = diff.DifferingFraction, ["mean_abs_error"] = Math.Round(diff.MeanAbsError, 4),
                    ["max_channel_error"] = diff.MaxChannelError, ["tolerance"] = tolerance, ["summary"] = diff.ToString(),
                };
                if (!diff.Pass)
                    _failures.Add("diff " + diff);
            }
        }
        // How much of the picture is pure white (every channel at least 250): a lit scene whose light is too strong turns
        // faces to flat white. --tr-max-clipped=<fraction> fails the run above that share.
        {
            var px = img.GetData();
            int clipped = 0, total = px.Length / 4;
            for (int i = 0; i + 3 < px.Length; i += 4) if (px[i] >= 250 && px[i + 1] >= 250 && px[i + 2] >= 250) clipped++;
            double share = total == 0 ? 0 : clipped / (double)total;
            _result["clipped_fraction"] = Math.Round(share, 5);
            if (_maxClipped is { } maxClipped && share > maxClipped)
                _failures.Add($"clipped: {share:P2} of the picture is pure white, above the {maxClipped:P2} allowed");
        }
        Phase("compared");
        presenter.Dispose();
        view.Clear();
        loaded.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }


    // ---------------- measure (ADR-003: scene nodes vs RenderingServer) ----------------

    private async Task<int> Measure(string fixture, int frames)
    {
        FreeRunning();
        var loaded = Load(fixture);
        var region = loaded.World.Region!;
        var runs = new List<object>();
        foreach (string kind in new[] { "nodes", "server", "nodes", "server" })
        {
            var rig = MakeRig();
            RenderingServer.ViewportSetMeasureRenderTime(rig.Viewport.GetViewportRid(), true);
            var view = MakeView(kind, rig);
            var presenter = new WorldPresenter(loaded.World, view);
                var sw = Stopwatch.StartNew();
            presenter.Flush();
            double buildMs = sw.Elapsed.TotalMilliseconds;
            Stage.Overview(rig.Camera, region.SizeX, region.SizeY);
            await Frames(10);   // warm-up
            var frameMs = new List<double>();
            var cpuMs = new List<double>();
            var gpuMs = new List<double>();
            var draws = new List<long>();
            long last = Stopwatch.GetTimestamp();
            for (int i = 0; i < frames; i++)
            {
                await Frames(1);
                long now = Stopwatch.GetTimestamp();
                frameMs.Add((now - last) * 1000.0 / Stopwatch.Frequency);
                last = now;
                cpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(rig.Viewport.GetViewportRid()));
                gpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(rig.Viewport.GetViewportRid()));
                draws.Add((long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame));
            }
            // Rebuild-all cost (a full region's terrain re-uploaded into existing chunks).
            sw.Restart();
            for (int y = 0; y * 64 < region.SizeY; y++)
                for (int x = 0; x * 64 < region.SizeX; x++)
                    presenter.Apply(new WorldChange(WorldChangeKind.TerrainPatch, new ObjectId(loaded.World.Epoch, 0), PatchX: x * 4, PatchY: y * 4));
            presenter.Flush();
            double rebuildMs = sw.Elapsed.TotalMilliseconds;
            runs.Add(new
            {
                view = kind, chunks = view.ChunkCount, build_ms = Math.Round(buildMs, 1), rebuild_all_ms = Math.Round(rebuildMs, 1),
                frame_ms = Dist(frameMs), render_cpu_ms = Dist(cpuMs), render_gpu_ms = Dist(gpuMs),
                draw_calls = new { min = draws.Min(), max = draws.Max(), p50 = Percentile(draws.Select(d => (double)d).ToList(), 50) },
                nodes = GetTree().GetNodeCount(),
            });
            presenter.Dispose();
            view.Clear();
            rig.Viewport.QueueFree();
            await Frames(3);
        }
        _result["mode"] = "measure";
        _result["fixture"] = loaded.Description;
        _result["region"] = new { size_x = region.SizeX, size_y = region.SizeY, patches = loaded.World.TerrainPatchCount };
        _result["frames_per_run"] = frames;
        _result["runs"] = runs;
        loaded.Dispose();
        return 0;
    }

    // ----------------: a camera path through a scene, residency measured ----------------

    /// <summary>
    /// A deterministic walk (fixed seed) through a replayed scene with its local assets: waypoints are seeded picks of the
    /// scene's objects, the camera flies between them at eye height looking along its way, one real frame per step with
    /// the live per-frame path (<see cref="WorldPresenter.Frame"/>: residency update, then the 4 ms presentation budget).
    /// Reports frame time, upload time per frame, resident bytes over time against the budget, evictions, reloads,
    /// thrash, placeholders at the end, the time until every in-frustum texture is resident at the final pose, and the
    /// priority-update cost per frame.
    /// </summary>
    private async Task<int> CameraPath(string fixture, int frames, int seed, double seconds = 0)
    {
        FreeRunning();
        // the pipeline warm-up before anything of the path exists, as the viewer runs it before its
        // first frame; its time and compilations are reported apart (warmup), never in the path's frames
        if (!_warmupOff)
        {
            var w = await PipelineWarmup.RunAsync(this, Viewport.Msaa.Disabled, () => Frames(1));   // the path rig's multisampling (MakeRig)
            _result["warmup"] = new { ms = Math.Round(w.Ms, 2), pipelines = w.Pipelines, variants = w.Variants, frames = w.Frames };
        }
        else _result["warmup"] = "off";
        // measurement control only: --tr-gc-latency=sustained runs the path with the .NET runtime's
        // SustainedLowLatency mode (no blocking full collections while it is set), to test the GC explanation directly
        if (_gcLatency == "sustained") System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        _result["gc"] = new { latency_mode = System.Runtime.GCSettings.LatencyMode.ToString(), server = System.Runtime.GCSettings.IsServerGC };
        var loaded = Load(fixture);
        bool heavy = loaded.Textures is not null;   // the synthetic heavy scene brings its own textures
        if (_assetDir is null && !heavy) throw new ArgumentException("--tr-path needs --tr-assets=");
        var rig = MakeRig(new Vector2I(_pathW, _pathH));
        var view = MakeView("server", rig);
        var meshes = heavy ? null : CacheMeshSource(_assetDir!, _contentStore ? RunContentView() : null);
        var textures = loaded.Textures ?? CacheTextureSource(_assetDir!);
        var prims = new PrimLayer(rig.Scenario, loaded.World, meshes, textures, _budget)
        {
            MipmapsOnMainThread = _legacyUpload, ImageOnMainThread = _part1Upload, LiveBudget = _liveBudget,
            MaterialAssets = heavy ? null : CacheMaterialSource(_assetDir!),   // a capture's PBR material assets from the local cache
            LegacyFallback = _legacyFallback,
        };
        var cam = rig.Camera;
        cam.Fov = 60f; cam.Near = 0.2f; cam.Far = 512f;
        var roots = loaded.World.Snapshot().Objects.Values.Where(o => o.ParentLocalId == 0 && o.AttachmentPoint is null).OrderBy(o => o.Id.LocalId).ToList();
        var rnd = new Random(seed);
        var region = loaded.World.Region!;
        var ground = (float?)region.WaterHeight ?? 20f;
        // six waypoints: seeded objects (or seeded points when the region is nearly empty), 3 m above the object or the water
        var points = Enumerable.Range(0, 6).Select(_ =>
        {
            System.Numerics.Vector3 p = roots.Count >= 6 ? roots[rnd.Next(roots.Count)].Position : new(rnd.NextSingle() * region.SizeX, rnd.NextSingle() * region.SizeY, ground);
            float z = Math.Max(p.Z, ground) + 3f;
            return new Vector3(p.X, z, -p.Y) + new Vector3(rnd.NextSingle() * 16 - 8, 0, rnd.NextSingle() * 16 - 8);
        }).ToList();
        Vector3 At(double t)
        {
            double s = Math.Clamp(t, 0, 1) * (points.Count - 1);
            int k = Math.Min((int)s, points.Count - 2);
            return points[k].Lerp(points[k + 1], (float)(s - k));
        }
        // --tr-return: out through the waypoints in the first half, back the same way in the second
        Vector3 Along(double t) => At(_returnPath ? (t < 0.5 ? 2 * t : 2 - 2 * t) : t);
        void Place(double t)
        {
            var p = Along(t);
            var ahead = Along(Math.Min(1, t + 0.02));
            if (ahead.DistanceTo(p) < 0.01f) ahead = p + (p - Along(Math.Max(0, t - 0.02)));   // at the end: on the way it came
            if (ahead.DistanceTo(p) < 0.01f) ahead = p + new Vector3(0, 0, -1);
            cam.Position = p;
            cam.LookAt(ahead with { Y = p.Y - 1.5f }, Vector3.Up);
            prims.Camera = cam.GlobalPosition;
            prims.ResidencyView = PrimLayer.ViewOf(cam, _pathW, _pathH);
            prims.UpdateLods();
            prims.ViewCamera = cam;
            prims.UpdateBlendOrder();   // blended faces in order for this frame's camera
        }
        Place(0);
        var envLayer = new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun);
        var presenter = new WorldPresenter(loaded.World, view, prims: prims, terrainTextures: textures, environment: envLayer) { DayFractionOverride = _skySweepSeconds > 0 ? null : 0.3 };
        var sweepSky = presenter.Sky;
        var glowPass = new GlowPass(prims, rig.World, rig.Environment);   // the glow of faces that glow
        rig.Root.AddChild(glowPass);
        var glowActive = 0; var glowCpu = new List<double>(); var glowGpu = new List<double>();
        double busiestDraws = -1; int busiestFrame = -1; float[]? busiestPose = null;   // --tr-busiest
        // every chunk handed over counted, to hold the settle gate to one handover
        // per chunk (one more only for a chunk the cap started early)
        var handed = _settleCheck ? new Dictionary<(int, int), int>() : null;
        if (handed is not null) presenter.OnTerrainHandOver = (x, y, _) => handed[(x, y)] = handed.GetValueOrDefault((x, y)) + 1;
        var uploads = new List<(PrimLayer.UploadSample U, string Phase, int Frame)>();
        var frameLog = new FrameLog(prims, envLayer);
        // what each recorded frame allocates, by kind (AllocFrames)
        AllocationMeter.Shared.MarkMainThread();
        var allocs = new AllocFrames(AllocationMeter.Shared);
        using var gcEvents = _gcEvents is null ? null : GcEventRecorder.Start(allocationTicks: _gcEvents == "ticks");
        int gcAtStart = GC.CollectionCount(0);
        var memory = new MemorySplit();   // the engine's video memory split, every path frame
        // the warm-up frames are measured too (the scene's first draws happen here)
        for (int w = 0; w < 3; w++)
        {
            var snap = frameLog.Snap(uploads.Count);
            allocs.Start();
            long w0 = Stopwatch.GetTimestamp();
            await Frames(1);
            allocs.Stop();
            double wms = (Stopwatch.GetTimestamp() - w0) * 1000.0 / Stopwatch.Frequency;
            allocs.Record("warmup", w, wms, 0, uploads, snap.Uploads);
            frameLog.Record("warmup", w, wms, 0, snap, uploads);
        }
        // the viewport rendered and the residency camera's viewport height must be the size asked for
        var actual = rig.Viewport.Size;
        int residencyHeight = prims.ResidencyView!.Value.ViewportHeight;
        if (actual.X != _pathW || actual.Y != _pathH) _failures.Add($"render size {actual.X}x{actual.Y}, asked {_pathW}x{_pathH}");
        if (residencyHeight != actual.Y) _failures.Add($"residency camera viewport height {residencyHeight} != rendered height {actual.Y}");
        _result["render_size"] = new { width = actual.X, height = actual.Y, residency_viewport_height = residencyHeight };
        var frameMs = new List<double>();
        // the engine's measured render time of the main viewport for each frame, the main thread's own work in it, and whether the
        // frame is one the window was not drawing in (set aside from the frame figures)
        RenderingServer.ViewportSetMeasureRenderTime(rig.Viewport.GetViewportRid(), true);
        var costCpu = new List<double>(); var costGpu = new List<double>(); var mainMs = new List<double>(); var marked = new List<bool>();
        var hitches = new List<(int Frame, double S, double Ms)>();   // path frames over HitchMs
        var uploadMs = new List<double>();
        var priorityMs = new List<double>();
        var timeline = new List<object>();
        long peakResident = 0, peakMesh = 0, peakCombined = 0;
        int framesOverBudget = 0;   // after the frame's uploads, before the next pass (a load larger than its reservation)
        var res = prims.Residency!;
        // paced by wall-clock time when --tr-path-seconds is given (decode latency as a user sees it); by frames otherwise
        var clock = Stopwatch.StartNew();
        long pathStart = _timeline?.Now ?? 0;   // textures' first images from the path's start
        var faceImages = new Viewer.Core.Residency.FaceFirstImages();   // each face's first image from the path's start
        object[]? atPeak = null;                // the resident textures' levels and bytes at the resident peak
        // resident texture bytes split by where the textures are: in view, out of view, pinned terrain
        // layers (at the resident peak, the most of each over the path, at the path's end and after the settle)
        (long InView, long OutOfView, long Pinned) splitAtPeak = default;
        long peakInView = 0, peakOutOfView = 0;
        // the presenter's frame (one main-thread slice) in wall-clock and in the main thread's processor time, and with --tr-frame-probe where each slice's time went
        CpuMeter.ThreadCycleMs();   // calibrates the cycle counter now, not inside the first slice
        var pathSliceCpu = new List<double>(); var pathSliceWall = new List<double>();
        var pathProbe = _frameProbe ? new ArrivalProbe() : null;
        FrameProbe.Enabled = pathProbe is not null;
        if (pathProbe is not null) FrameProbe.TakeFrame();
        long last = Stopwatch.GetTimestamp();
        // the pose of the path's last presented frame: the pose the end-of-path figures are taken at
        (double T, Transform3D Pose) lastPresented = default;
        // a timed path ends with a frame at its exact end pose: the first frame that finds the time used up is placed at
        // the path's end (fraction 1) and is presented as the path's last, so the end-of-path figures are always taken
        // at the same pose (a frame-counted path's last frame is at fraction 1 already)
        bool timeUsedUp = false, minimized = false;
        int f = 0;
        for (; seconds > 0 ? !timeUsedUp : f < frames; f++)
        {
            var snap = frameLog.Snap(uploads.Count);
            allocs.Start();
            long iterationStart = Stopwatch.GetTimestamp();
            double pathT = (double)f / Math.Max(1, frames - 1);
            if (seconds > 0)
            {
                pathT = Math.Min(1, clock.Elapsed.TotalSeconds / seconds);
                timeUsedUp = pathT >= 1;
            }
            if (_minimizeAt > 0 && !minimized && clock.Elapsed.TotalSeconds >= _minimizeAt)
            {
                minimized = true;   // test only: a window that cannot draw, mid-path
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Minimized);
            }
            Place(pathT);
            if (_skySweepSeconds > 0 && SkySweep.At(clock.Elapsed.TotalSeconds, _skySweepSeconds) is var wantSky && wantSky != sweepSky) presenter.Sky = sweepSky = wantSky;
            pathProbe?.Begin();
            double sliceCpuA = CpuMeter.ThreadCycleMs(); long sliceWallA = Stopwatch.GetTimestamp();
            presenter.Frame();
            double sliceWallMs = (Stopwatch.GetTimestamp() - sliceWallA) * 1000.0 / Stopwatch.Frequency;
            pathSliceCpu.Add(CpuMeter.ThreadCycleMs() - sliceCpuA); pathSliceWall.Add(sliceWallMs);
            pathProbe?.End(pathSliceCpu[^1]);   // classified on the main thread's processor time, as the sections are
            lastPresented = (pathT, cam.GlobalTransform);
            res.ObserveFaceImages(faceImages, clock.Elapsed.TotalMilliseconds, prims.FallbackOf);
            uploadMs.Add(prims.UploadMsThisFrame);
            int pathFrame = f;
            uploads.AddRange(prims.UploadSamples.Select(u => (u, "path", pathFrame)));
            priorityMs.Add(res.LastPriorityMs);
            if (res.ResidentBytes > peakResident) { atPeak = ResidentLevels(res); splitAtPeak = ResidentSplit(res); }
            var split = ResidentSplit(res);
            peakInView = Math.Max(peakInView, split.InView);
            peakOutOfView = Math.Max(peakOutOfView, split.OutOfView);
            peakResident = Math.Max(peakResident, res.ResidentBytes);
            peakMesh = Math.Max(peakMesh, prims.MeshBytesHeld);
            peakCombined = Math.Max(peakCombined, res.ResidentBytes + prims.MeshBytesHeld);
            if (res.ResidentBytes > res.BudgetBytes) framesOverBudget++;
            mainMs.Add((Stopwatch.GetTimestamp() - iterationStart) * 1000.0 / Stopwatch.Frequency);
            if (f % 30 == 0) timeline.Add(new { frame = f, s = Math.Round(clock.Elapsed.TotalSeconds, 2), resident_mib = Math.Round(res.ResidentBytes / 1048576.0, 1), on_placeholders = res.OnPlaceholders, loading = res.Loading, refining = res.Refining });
            await Frames(1);
            long now = Stopwatch.GetTimestamp();
            allocs.Stop();
            frameMs.Add((now - last) * 1000.0 / Stopwatch.Frequency);
            costCpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(rig.Viewport.GetViewportRid()));
            costGpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(rig.Viewport.GetViewportRid()));
            marked.Add(_keeper?.TakeMark() ?? false);
            if (glowPass.Active)
            {
                glowActive++;
                if (GlowPass.MeasureTimes) { var (gc, gg) = glowPass.PassMs(); glowCpu.Add(gc); glowGpu.Add(gg); }
            }
            if (_busiest)
            {
                double dc = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
                if (dc > busiestDraws)
                {
                    busiestDraws = dc; busiestFrame = f;
                    var cp = rig.Camera.GlobalPosition; var fw = -rig.Camera.GlobalTransform.Basis.Z;
                    busiestPose = new[] { cp.X, -cp.Z, cp.Y, cp.X + 30 * fw.X, -(cp.Z + 30 * fw.Z), cp.Y + 30 * fw.Y };   // region coordinates: the eye, then a point 30 m ahead
                }
            }
            if (frameMs[^1] > HitchMs && !marked[^1]) hitches.Add((f, clock.Elapsed.TotalSeconds, frameMs[^1]));   // (measurement)
            allocs.Record("path", f, frameMs[^1], clock.Elapsed.TotalSeconds, uploads, snap.Uploads);
            frameLog.Record("path", f, frameMs[^1], clock.Elapsed.TotalSeconds, snap, uploads);
            memory.Add(res.ResidentBytes);
            last = now;
        }
        double pathSeconds = clock.Elapsed.TotalSeconds;
        long decodedAtPathEnd = meshes?.DecodedBytes ?? 0;   // at the path's end, before the settle
        var splitAtPathEnd = ResidentSplit(res);
        var levelsAtPathEnd = LevelsAgainstWanted(res);   // (measurement)
        var meshDetailAtPathEnd = MeshDetail(prims);
        int decodedEntriesAtPathEnd = meshes?.DecodedEntries ?? 0;
        // the final pose: the in-view textures as the flight ends, then the time until each is at its desired level
        var inView = res.All.Where(res.IsVisible).ToList();
        int belowAtArrival = inView.Count(id => res.StateOf(id) == Viewer.Core.Residency.ResidencyState.Resident && res.LevelOf(id) > res.DesiredLevelOf(id));
        int placeholdersAtArrival = inView.Count(id => res.StateOf(id) != Viewer.Core.Residency.ResidencyState.Resident && !res.HasFailed(id));
        var settle = Stopwatch.StartNew();
        bool AllVisibleResident() => res.All.Where(res.IsVisible).All(id => res.StateOf(id) == Viewer.Core.Residency.ResidencyState.Resident || res.HasFailed(id));
        bool AllVisibleAtDesired() => res.All.Where(res.IsVisible).All(res.AtDesired);
        double? residentMs = AllVisibleResident() ? 0 : null;
        int settleFrames = 0;
        while (!AllVisibleAtDesired() && settle.Elapsed.TotalSeconds < 30)
        {
            var snap = frameLog.Snap(uploads.Count);
            allocs.Start();
            long s0 = Stopwatch.GetTimestamp();
            presenter.Frame();
            int settleFrame = settleFrames;
            uploads.AddRange(prims.UploadSamples.Select(u => (u, "settle", settleFrame)));
            await Frames(1);
            allocs.Stop();
            double sms = (Stopwatch.GetTimestamp() - s0) * 1000.0 / Stopwatch.Frequency;
            allocs.Record("settle", settleFrames, sms, clock.Elapsed.TotalSeconds, uploads, snap.Uploads);
            frameLog.Record("settle", settleFrames, sms, clock.Elapsed.TotalSeconds, snap, uploads);
            settleFrames++;
            if (residentMs is null && AllVisibleResident()) residentMs = settle.Elapsed.TotalMilliseconds;
        }
        double settleMs = settle.Elapsed.TotalMilliseconds;
        bool settled = AllVisibleAtDesired();
        if (_holdForClose) await HoldForClose(presenter, loaded.Dispose, () => meshes?.Dispose(), () => textures?.Dispose());
        // every terrain chunk the presenter built (all in the recorded frames) is a terrain unit
        int terrainUnits = uploads.Count(u => u.U.Kind == "terrain");
        if (presenter.ChunksBuilt == 0 || terrainUnits != presenter.ChunksBuilt) _failures.Add($"terrain: {presenter.ChunksBuilt} chunks built, {terrainUnits} terrain upload units recorded");
        if (handed is not null)
        {
            int chunks = presenter.View.ChunkCount, handovers = handed.Values.Sum(), most = handed.Count == 0 ? 0 : handed.Values.Max(), byCap = presenter.Settle.StartedByCap;
            _result["settle_check"] = new { chunks_with_terrain = chunks, chunks_handed = handed.Count, handovers, most_per_chunk = most, started_by_cap = byCap };
            if (chunks == 0 || handovers > chunks + byCap || most > 2)
                _failures.Add($"settle: {handovers} chunk handovers for {chunks} chunks with terrain ({byCap} started by the cap; at most {most} for one chunk): chunks built again from arrival order");
        }
        memory.Add(res.ResidentBytes);
        var visibleEnd = res.All.Where(res.IsVisible).ToList();
        var png = Path.Combine(_out, _tag + ".png");
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        Directory.CreateDirectory(_out);
        img.SavePng(png);
        // upload cost per unit of work by the uploaded level's longest side
        var bySize = uploads.Where(u => !u.U.Mesh && u.U.Kind != "terrain").GroupBy(u => Math.Max(u.U.Width, u.U.Height) switch { <= 64 => 64, <= 128 => 128, <= 256 => 256, <= 512 => 512, <= 1024 => 1024, _ => 2048 })
            .OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => Dist(g.Select(u => u.U.Ms).ToList()));
        // every unit over 4 ms, with what else happened: its place in the run, first upload or not, image made off
        // the main thread or here, the image part of its time, collections during it, and its frame's pipeline compilations
        var seenSizes = new HashSet<(int, int)>();
        var slowUnits = new List<object>();
        for (int i = 0; i < uploads.Count; i++)
        {
            var (u, phase, fr) = uploads[i];
            bool firstOfSize = seenSizes.Add((u.Width, u.Height));
            if (u.Ms > 4)
                slowUnits.Add(new
                {
                    index = i, phase, frame = fr, mesh = u.Mesh, kind = u.Kind, w = u.Width, h = u.Height, ms = Math.Round(u.Ms, 2), image_ms = Math.Round(u.ImageMs, 2), create_ms = Math.Round(u.Ms - u.ImageMs, 2),
                    first_upload = u.First, prepared_off_thread = u.Prepared, gcs = u.Gcs, gc_pause_ms = Math.Round(u.GcPauseMs, 2), first_of_its_size = firstOfSize, frame_pipelines = frameLog.PipelinesIn(phase, fr),
                });
        }
        // frame figures only mean something when presents were not paced from outside the scene: judged from the frames, the
        // result carries the verdict, and a measuring run (--tr-pacing-guard) fails on it instead of reporting capped figures
        FrameProbe.Enabled = false;
        var judged = JudgeFrames(frameMs, costCpu, costGpu, mainMs, marked);
        if (_census || PrimLayer.MergeStill) _result["census"] = CensusReport(prims, glowPass, rig, view.ChunkCount);   // of the last frame drawn
        _result["mode"] = "path";
        _result["fixture"] = loaded.Description;
        _result["seed"] = seed;
        _result["frames"] = f;
        if (_busiest) _result["busiest_view"] = new { frame = busiestFrame, draw_calls = busiestDraws, look = busiestPose };
        _result["glow"] = new { glowing_objects = prims.GlowObjects, frames_drawn = glowActive, frames = f, pass_cpu_ms = glowCpu.Count > 0 ? Dist(glowCpu) : null, pass_gpu_ms = glowGpu.Count > 0 ? Dist(glowGpu) : null };
        _result["path"] = new
        {
            paced_by = seconds > 0 ? "wall_clock" : "frames", path_seconds = Math.Round(pathSeconds, 2), mipmaps = _legacyUpload ? "main_thread" : "with_decode",
            frame_ms = Dist(judged.FrameMs), upload_ms = Dist(uploadMs), priority_ms = Dist(priorityMs),
            upload_unit_ms_by_size = bySize, upload_units = uploads.Count, upload_units_over_4ms = uploads.Count(u => u.U.Ms > 4),
            worst_upload = uploads.Count == 0 ? null : uploads.OrderByDescending(u => u.U.Ms).Select(u => new { mesh = u.U.Mesh, w = u.U.Width, h = u.U.Height, ms = Math.Round(u.U.Ms, 2) }).First(),
            // mesh LOD builds are upload units too (in the counts above); here apart from the textures
            texture_upload_units = uploads.Count(u => !u.U.Mesh && u.U.Kind != "terrain"), texture_upload_units_over_4ms = uploads.Count(u => !u.U.Mesh && u.U.Kind != "terrain" && u.U.Ms > 4),
            mesh_upload_units = uploads.Count(u => u.U.Mesh), mesh_upload_units_over_4ms = uploads.Count(u => u.U.Mesh && u.U.Ms > 4),
            mesh_upload_unit_ms = uploads.Any(u => u.U.Mesh) ? Dist(uploads.Where(u => u.U.Mesh).Select(u => u.U.Ms).ToList()) : null,
            slow_units = slowUnits, render_size = $"{_pathW}x{_pathH}", frames_log = frameLog.Summary(),
            slice_wall_ms = Dist(pathSliceWall), slice_cpu_ms = Dist(pathSliceCpu), slices_cpu_over_4ms = pathSliceCpu.Count(x => x > MainThreadSliceLimitMs), slices_wall_over_4ms = pathSliceWall.Count(x => x > MainThreadSliceLimitMs),
            first_slices_cpu_ms = pathSliceCpu.Take(12).Select(x => Math.Round(x, 2)).ToArray(), frame_probe = pathProbe?.Report(),
            // the upload units by kind (texture, mesh LOD, prim and sculpt first builds)
            // terrain chunk builds are units too (kind terrain, in the counts above; out of the texture
            // figures), with the chunks the presenter built in the recorded frames to compare
            terrain_upload_units = uploads.Count(u => u.U.Kind == "terrain"), terrain_upload_units_over_4ms = uploads.Count(u => u.U.Kind == "terrain" && u.U.Ms > 4),
            terrain_worst_ms = uploads.Any(u => u.U.Kind == "terrain") ? Math.Round(uploads.Where(u => u.U.Kind == "terrain").Max(u => u.U.Ms), 3) : (double?)null,
            terrain_chunks_built = presenter.ChunksBuilt,
            terrain_complete_ms = presenter.TerrainCompleteMs is { } tc ? Math.Round(tc, 2) : (double?)null,   // (measurement)
            // total hitch time, the sum of the recorded path's frame times over
            // HitchMs, their count, and each one's frame, time into the path (s) and length
            hitch_ms_total = Math.Round(hitches.Sum(h => h.Ms), 2), hitch_frames = hitches.Count,
            hitches = hitches.Select(h => new { frame = h.Frame, s = Math.Round(h.S, 3), ms = Math.Round(h.Ms, 2) }).ToList(),
            // chunks prepared off the main thread (null when built here), each one's time on its worker and its
            // snapshot's on the main thread, stale results discarded, and chunks built or encoded on the main thread
            terrain_prep = presenter.TerrainPrep is { } tp ? new
            {
                cap = tp.Cap, peak_running = tp.PeakRunning, prepared = tp.Prepared, stale = tp.Stale, discarded = tp.Discarded, failed = tp.Failed, in_flight_at_clear = tp.InFlightAtClear,
                prepare_ms = DistOrNull(tp.PrepareMs.ToList()), snapshot_ms = DistOrNull(presenter.SnapshotMs.ToList()),
            } : null,
            terrain_built_on_main_thread = TerrainGrid.BuiltOnMainThread, terrain_encoded_on_main_thread = TerrainSurface.EncodedOnMainThread,
            // the settle window and cap, chunks started, started early by the cap,
            // each start's wait from its first dirty mark, and every patch change seen (ms from the region's start)
            terrain_settle = TerrainSettleReport(presenter.Settle), terrain_chunks_in_view = presenter.View.ChunkCount,
            upload_units_by_kind = uploads.GroupBy(u => u.U.Kind).OrderBy(k => k.Key).ToDictionary(k => k.Key, k => new { units = k.Count(), over_4ms = k.Count(u => u.U.Ms > 4), worst_ms = Math.Round(k.Max(u => u.U.Ms), 3) }),
            mesh_builds = prims.MeshBuildReport(),   // the main-thread split of every mesh LOD build
            mesh_prep = prims.MeshPreparer is { } mp ? new { cap = mp.Cap, peak_running = mp.PeakRunning, prepared = mp.Prepared, dropped = mp.Dropped, failed = mp.Failed, peak_held_bytes = mp.PeakHeldBytes } : null,   // (measurement)
            budget_mib = Math.Round((_liveBudget?.Max ?? _budget) / 1048576.0, 1),
            budget = _liveBudget is { } lb ? new { source = lb.Source, initial_mib = lb.Initial / 1048576, min_mib = lb.Min / 1048576, max_mib = lb.Max / 1048576, final_mib = lb.Budget / 1048576, changes = lb.Changes, non_texture_applied_mib = lb.NonTextureApplied / 1048576 } : null,
            peak_resident_mib = Math.Round(peakResident / 1048576.0, 1), frames_over_budget_after_uploads = framesOverBudget,
            // the mesh LODs on the GPU beside the residency's textures, and every mesh fetch
            return_path = _returnPath,
            // where the path's last presented frame was (t: the fraction of the path, 1 = its end pose; position and
            // forward in full precision, so runs can be compared exactly)
            end_pose = new
            {
                t = lastPresented.T,
                position = new[] { lastPresented.Pose.Origin.X, lastPresented.Pose.Origin.Y, lastPresented.Pose.Origin.Z }.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                forward = new[] { -lastPresented.Pose.Basis.Z.X, -lastPresented.Pose.Basis.Z.Y, -lastPresented.Pose.Basis.Z.Z }.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
            },
            peak_resident_combined_mib = Math.Round(peakCombined / 1048576.0, 2),
            mesh = new
            {
                peak_bytes_mib = Math.Round(peakMesh / 1048576.0, 2), end_bytes_mib = Math.Round(prims.MeshBytesHeld / 1048576.0, 2),
                lods_held_peak = prims.PeakMeshLodsHeld, lods_held_end = prims.MeshLodsHeld, objects = prims.MeshObjects,
                lods_released = prims.MeshLodsReleased, lods_rebuilt = prims.MeshLodsRebuilt, lod_thrash = prims.MeshLodThrash,
                fetches = s_meshFetches.Values.Sum(), assets_fetched = s_meshFetches.Count, fetched_more_than_once = s_meshFetches.Count(kv => kv.Value > 1),
                // the decoded geometry the mesh source keeps for the session
                decoded_entries = meshes?.DecodedEntries ?? 0, decoded_mib = Math.Round((meshes?.DecodedBytes ?? 0) / 1048576.0, 2),
                decoded_peak_mib = Math.Round((meshes?.PeakDecodedBytes ?? 0) / 1048576.0, 2), decoded_bytes = meshes?.DecodedBytes ?? 0,
                // the decoded geometry at the path's end (before the settle); entries let go
                // by the keeper and at region changes; re-decodes (within 10 s of their let-go; with bytes from the network),
                // their time from the request to the decoded geometry, and where their bytes came from
                decoded_bound = prims.DecodedBound, decoded_kept_end = prims.DecodedKept,
                decoded_path_end_entries = decodedEntriesAtPathEnd, decoded_path_end_mib = Math.Round(decodedAtPathEnd / 1048576.0, 2), decoded_path_end_bytes = decodedAtPathEnd,
                decoded_peak_bytes = meshes?.PeakDecodedBytes ?? 0,
                let_go = meshes?.LetGo ?? 0, let_go_at_reset = meshes?.LetGoAtReset ?? 0,
                re_decodes = meshes?.ReDecodes ?? 0, re_decodes_within_10s = meshes?.ReDecodesSoon ?? 0, network_re_decodes = meshes?.NetworkReDecodes ?? 0,
                re_decode_ms = meshes is { } md && md.ReDecodeMs.Length > 0 ? Dist(md.ReDecodeMs.ToList()) : null,
                re_decode_sources = meshes?.ReDecodeSources,
            },
            evictions = res.Evictions, reloads = res.Reloads, thrash = res.Thrash, loads = res.Loads, steps_down = res.StepsDown, steps_up = res.StepsUp,
            textures = res.Textures, users = res.Users, on_placeholders_end = res.OnPlaceholders, resident_end = res.Resident,
            visible_end = visibleEnd.Count,
            final_pose_in_view = inView.Count, final_pose_below_desired_at_arrival = belowAtArrival, final_pose_placeholders_at_arrival = placeholdersAtArrival,
            final_pose_below_desired_end = visibleEnd.Count(id => res.StateOf(id) == Viewer.Core.Residency.ResidencyState.Resident && res.LevelOf(id) > res.DesiredLevelOf(id)),
            final_pose_placeholders_end = visibleEnd.Count(id => res.StateOf(id) != Viewer.Core.Residency.ResidencyState.Resident && !res.HasFailed(id)),
            final_pose_all_visible_resident_ms = residentMs is { } rm ? Math.Round(rm, 1) : (double?)null,
            final_pose_all_visible_at_desired_ms = settled ? Math.Round(settleMs, 1) : (double?)null, final_pose_frames = settleFrames,
            memory = memory.Summary(),
            // the process's peak memory, collections and decode times on every camera path (the heavy
            // scene too, not only the texture server's paths)
            process_memory = MemoryReport(textures),
            shared_asset_store = s_sharedAssets?.Stats,
            // per texture, first ask, first image and first upload (on screen) from the
            // path's start; and the resident textures' levels and bytes at the resident peak and at the path's end
            first_image_from_start = _timeline is { } tl ? FirstImageFromStart(tl, pathStart, res.IsDetailOnly) : null,
            first_image_per_face = FaceImagesReport(faceImages.Summary()),
            // the PBR faces' legacy fallback: faces drawing it at the end, faces that went from it to their PBR material,
            // the textures drawn as a fallback, and textures the server sent that no residency user ever wanted (a
            // fetch for a fallback alone would be one; must be 0)
            legacy_fallback = new
            {
                enabled = prims.LegacyFallback, faces_end = prims.FallbackFaces, waiting_end = prims.PbrFacesWaiting, switches = prims.FallbackSwitches,
                textures = prims.FallbackTextures.Select(g => g.ToString()).OrderBy(x => x).ToArray(),
                fetched_without_user = _server is { } srv ? srv.BodyBytesPerTexture.Keys.Count(id => !res.All.Contains(id)) : (int?)null,
            },
            // the PBR faces' detail maps in view at the run's end, and of those how many are not at their desired level
            detail_maps_end = prims.DetailMapsInView() is var dm ? new { in_view = dm.InView, below_desired = dm.BelowLevel } : null,
            resident_at_peak = atPeak, resident_at_end = ResidentLevels(res),
            resident_split = new
            {
                at_peak = SplitReport(splitAtPeak), path_end = SplitReport(splitAtPathEnd), end = SplitReport(ResidentSplit(res)),
                peak_in_view_bytes = peakInView, peak_out_of_view_bytes = peakOutOfView,
            },
            // at the path's end: the texture levels against the levels wanted, and the mesh detail drawn (measurement)
            levels_vs_wanted_path_end = levelsAtPathEnd, mesh_detail_path_end = meshDetailAtPathEnd,
            level_histogram_end = visibleEnd.Where(id => res.StateOf(id) == Viewer.Core.Residency.ResidencyState.Resident).GroupBy(res.LevelOf).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            timeline,
        };
        _result["texture_server"] = TextureServerReport(textures);
        // blended faces at the path's end (objects with one, places in the order, face instances) and the ordering's cost
        // per frame (measurement)
        var bo = prims.BlendOrderMs.OrderBy(x => x).ToList();
        _result["blend"] = new
        {
            objects = prims.BlendObjects, places = prims.BlendPlaces, face_instances = prims.BlendFaceInstances, offsets_set = prims.BlendOffsetsSet,
            order_frames = bo.Count, order_ms_p50 = bo.Count == 0 ? 0 : Math.Round(bo[bo.Count / 2], 4), order_ms_p95 = bo.Count == 0 ? 0 : Math.Round(bo[Math.Min(bo.Count - 1, (int)(bo.Count * 0.95))], 4),
            order_ms_max = bo.Count == 0 ? 0 : Math.Round(bo[^1], 4),
            part_meshes = prims.BlendPartMeshes, parts_made = prims.BlendPartsMade, part_bytes = prims.BlendPartBytes, whole_mesh_fallbacks = prims.BlendWholeMeshFallbacks,
        };
        // PBR and legacy-material faces drawn at the path's end, and the material assets read
        _result["materials"] = new { pbr_faces = prims.PbrFaces, pbr_faces_beyond_geometry = prims.PbrFacesBeyondGeometry, pbr_materials = prims.PbrMaterials,
            assets_ready = prims.MaterialAssets?.Ready ?? 0, assets_unavailable = prims.MaterialAssets?.Unavailable ?? 0,
            legacy_material_faces = prims.LegacyMaterialFaces, legacy_materials_waiting = prims.LegacyMaterialsWaiting };
        // the frames' allocations; the runtime's events arrive on its own thread a
        // little later, so wait for every collection of the run to be seen (generous cap)
        if (gcEvents is not null)
        {
            int expected = GC.CollectionCount(0) - gcAtStart;
            for (var wait = Stopwatch.StartNew(); gcEvents.Collections().Count(c => c.Index > gcAtStart) < expected && wait.Elapsed.TotalSeconds < 10;)
                await Task.Delay(50);
        }
        _result["alloc"] = allocs.Report(gcEvents, HitchMs, 5);
        if (allocs.Rows.Count == 0) _failures.Add("alloc: no frame recorded");
        _failures.AddRange(allocs.Failures.Take(5).Select(x => "alloc: " + x));
        // under a budget smaller than the view needs, "at its desired level" cannot be reached: reported, not failed;
        // a failure is textures left without any image (a placeholder) while the budget still has room for their coarsest level
        if (!settled && res.ResidentBytes + 64 * 1024 < res.BudgetBytes && visibleEnd.Any(id => res.StateOf(id) != Viewer.Core.Residency.ResidencyState.Resident && !res.HasFailed(id)))
            _failures.Add("final pose: in-view textures still on placeholders after 30 s with budget to spare");
        presenter.Dispose();
        view.Clear();
        textures.Dispose();
        meshes?.Dispose();
        loaded.Dispose();
        _http?.Dispose();
        _server?.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// The mipmap chain the core builds off the main thread must be byte-identical to the engine's
    /// own RGBA8 mipmaps (Image.GenerateMipmaps), or moving the work would change every golden. Sizes: powers of two,
    /// odd sizes and thin strips, with seeded pixels. Negative control: a chain with one texel changed must differ.
    /// </summary>
    private int MipCheck()
    {
        var rnd = new Random(11);
        var cases = new List<object>();
        foreach (var (w, h) in new[] { (64, 64), (1024, 1024), (512, 256), (200, 136), (37, 5), (1, 7), (3, 2), (33, 1) })
        {
            var rgba = new byte[w * h * 4];
            rnd.NextBytes(rgba);
            var ours = Viewer.Core.Imaging.MipChain.Build(w, h, rgba);
            var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba);
            img.GenerateMipmaps();
            var engine = img.GetData();
            int firstDiff = -1;
            if (engine.Length == ours.Length)
                for (int i = 0; i < ours.Length; i++) if (ours[i] != engine[i]) { firstDiff = i; break; }
            bool same = engine.Length == ours.Length && firstDiff < 0;
            if (!same) _failures.Add($"mip chain {w}x{h}: ours {ours.Length} B, engine {engine.Length} B, first difference at {firstDiff}");
            cases.Add(new { w, h, bytes = ours.Length, identical = same, engine_levels = img.GetMipmapCount() + 1 });
        }
        var bad = Viewer.Core.Imaging.MipChain.Build(64, 64, new byte[64 * 64 * 4]);
        bad[bad.Length - 1] ^= 1;
        var zero = Image.CreateFromData(64, 64, false, Image.Format.Rgba8, new byte[64 * 64 * 4]);
        zero.GenerateMipmaps();
        bool negative = zero.GetData().AsSpan().SequenceEqual(bad);
        if (negative) _failures.Add("negative control: a changed chain compared equal");
        _result["mode"] = "mipcheck";
        _result["mipcheck"] = new { cases, negative_control_rejected = !negative };
        return _failures.Count == 0 ? 0 : 1;
    }

    private bool _part1Upload;

    /// <summary>
    /// A mesh LOD no longer drawn is released promptly, not kept until
    /// a region change. Deterministic (the layer's clock advances 1/60 s per frame). (1) Near, then far, then 1.5 s: every
    /// LOD held is one some object draws (the near ones released), with no region change. (2) The camera hovers across a
    /// real LOD boundary (found by bisection to 1 cm), switching sides every frame for 3 s and every 20 frames for 3 s:
    /// nothing is made again and nothing thrashes; settled on one side for 1.5 s, the other side's LOD is released.
    /// (3) Back near: the near LODs are made again from the decoded geometry, 0 more fetches, and the image is identical to
    /// the first near image. (4) A region reset frees everything.
    /// </summary>
    private async Task<int> MeshLodCheck(string fixture)
    {
        var loaded = Load(fixture);
        if (loaded.Meshes is null) throw new ArgumentException("--tr-meshlod needs the mesh zoo (--tr-fixture=...meshzoo.json --tr-meshes=)");
        var rig = MakeRig();
        var view = MakeView("server", rig);
        var prims = new PrimLayer(rig.Scenario, loaded.World, loaded.Meshes, null, _budget) { SimulatedFps = 60 };
        var presenter = new WorldPresenter(loaded.World, view, prims: prims, environment: new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun)) { DayFractionOverride = 0.3 };
        void Cam(string preset) { Stage.MeshCamera(rig.Camera, preset); prims.Camera = rig.Camera.GlobalPosition; prims.UpdateLods(); }
        async Task Run(int frames) { for (int i = 0; i < frames; i++) { presenter.Flush(); if (i % 15 == 0) await Frames(1); } await Frames(2); }
        Image Shot() { var img = rig.Texture.GetImage(); img.Convert(Image.Format.Rgba8); return img; }
        int Fetches() => s_meshFetches.Values.Sum();
        var steps = new List<object>();
        void Note(string at) => steps.Add(new { at, held = prims.MeshLodsHeld, drawn = prims.DistinctDrawnMeshLods, mesh_resources = prims.MeshAssetResources, bytes = prims.MeshBytesHeld,
            released = prims.MeshLodsReleased, rebuilt = prims.MeshLodsRebuilt, thrash = prims.MeshLodThrash, fetches = Fetches(), lods = prims.MeshLods.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray(),
            decoded_entries = loaded.Meshes.DecodedEntries, decoded_bytes = loaded.Meshes.DecodedBytes, decoded_peak_bytes = loaded.Meshes.PeakDecodedBytes });

        Cam("near");
        await SettleMeshes(presenter, prims, loaded.Meshes);
        await Run(4);
        var near = Shot();
        int fetchesNear = Fetches();
        long footprintNear = prims.MeshBytesHeld;
        Note("near");
        if (prims.MeshObjects == 0 || prims.MeshLodsHeld != prims.DistinctDrawnMeshLods) _failures.Add($"near: {prims.MeshLodsHeld} LODs held for {prims.DistinctDrawnMeshLods} drawn");

        // (1) far, then 1.5 s
        Cam("far");
        await SettleMeshes(presenter, prims, loaded.Meshes);
        int heldAtSwitch = prims.MeshLodsHeld;
        Note("far, at the switch");
        await Run(90);
        Note("far, 1.5 s later");
        if (prims.MeshLodsHeld != prims.DistinctDrawnMeshLods || prims.MeshAssetResources != prims.MeshLodsHeld)
            _failures.Add($"far after 1.5 s: {prims.MeshLodsHeld} LODs held ({prims.MeshAssetResources} meshes) for {prims.DistinctDrawnMeshLods} drawn: LODs no longer drawn are kept (before any region change)");
        if (heldAtSwitch <= prims.DistinctDrawnMeshLods) _failures.Add($"the switch to far changed no LOD ({heldAtSwitch} held for {prims.DistinctDrawnMeshLods} drawn)");

        // (2) hover at a boundary between the far position and the near position (the far camera's orientation)
        var farPos = rig.Camera.GlobalPosition;
        Stage.MeshCamera(rig.Camera, "near");
        var nearPos = rig.Camera.GlobalPosition;
        Stage.MeshCamera(rig.Camera, "far");
        bool Changes(Vector3 p) { prims.Camera = p; prims.UpdateLods(); bool d = prims.Dirty > 0; prims.Camera = farPos; presenter.Flush(); return d; }
        float lo = 0f, hi = 1f;   // fractions from far to near: lo draws as far does, hi does not
        if (!Changes(farPos.Lerp(nearPos, hi))) _failures.Add("no LOD boundary between the far and near positions");
        while ((farPos.Lerp(nearPos, hi) - farPos.Lerp(nearPos, lo)).Length() > 0.01f)
        {
            float mid = (lo + hi) / 2;
            if (Changes(farPos.Lerp(nearPos, mid))) hi = mid; else lo = mid;
        }
        Vector3 a = farPos.Lerp(nearPos, lo), b = farPos.Lerp(nearPos, hi);
        long rebuilt0 = prims.MeshLodsRebuilt, thrash0 = prims.MeshLodThrash;
        int flips = 0;
        var lodsA = new HashSet<string>();
        var lodsB = new HashSet<string>();
        for (int f = 0; f < 360; f++)
        {
            bool sideB = f < 180 ? f % 2 == 1 : f / 20 % 2 == 1;
            prims.Camera = sideB ? b : a;
            prims.UpdateLods();
            flips += prims.Dirty > 0 ? 1 : 0;
            presenter.Flush();
            (sideB ? lodsB : lodsA).Add(string.Join(",", prims.MeshLods.OrderBy(kv => kv.Key).Select(kv => kv.Value)));
            if (f % 15 == 0) await Frames(1);
        }
        Note("hover, 6 s");
        if (flips < 180 || lodsA.Overlaps(lodsB)) _failures.Add($"hover: the camera did not cross the boundary ({flips} frames switched LOD)");
        if (prims.MeshLodsRebuilt != rebuilt0 || prims.MeshLodThrash != thrash0)
            _failures.Add($"hover: {prims.MeshLodsRebuilt - rebuilt0} LODs made again, thrash {prims.MeshLodThrash - thrash0}");
        prims.Camera = a;
        prims.UpdateLods();
        await Run(90);
        Note("settled on the far side, 1.5 s later");
        if (prims.MeshLodsHeld != prims.DistinctDrawnMeshLods) _failures.Add($"after the hover: {prims.MeshLodsHeld} LODs held for {prims.DistinctDrawnMeshLods} drawn");

        // (3) back near: made again from the decoded geometry, no fetch, the same image
        long rebuiltBefore = prims.MeshLodsRebuilt;
        Cam("near");
        await SettleMeshes(presenter, prims, loaded.Meshes);
        await Run(4);
        var again = Shot();
        Note("near again");
        bool same = near.GetData().AsSpan().SequenceEqual(again.GetData());
        if (!same) _failures.Add("near again: the image differs from the first near image");
        if (Fetches() != fetchesNear) _failures.Add($"near again: {Fetches() - fetchesNear} more mesh fetches");
        if (prims.MeshLodsRebuilt == rebuiltBefore) _failures.Add("near again: no LOD was made again (none had been released)");

        // (4) a region reset frees everything
        presenter.Apply(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(loaded.World.Epoch + 1, 0)));
        Note("region reset");
        if (prims.LiveResources != 0 || prims.MeshLodsHeld != 0 || prims.MeshBytesHeld != 0) _failures.Add($"region reset: {prims.LiveResources} resources, {prims.MeshLodsHeld} LODs held");
        presenter.Dispose();
        view.Clear();
        loaded.Dispose();

        _result["mode"] = "meshlod";
        _result["meshlod"] = new
        {
            steps, boundary = new { from = V(a), to = V(b), fraction = Math.Round(lo, 4) }, hover_frames_switching = flips, near_images_identical = same,
            near_footprint_bytes = footprintNear,
        };
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// The bound on decoded mesh geometry
    /// (build use). Deterministic (the layer's clock advances 1/60 s per frame). A reference pass with the bound off, then
    /// the pass under test on a new layer over the same world and meshes: (1) near, settled: every drawn LOD built;
    /// (2) 10.5 s later: every asset's decoded geometry let go (0 entries, 0 bytes), no LOD released from the GPU, the image
    /// unchanged; (3) far: every LOD not built yet is built from geometry decoded again from the stores (0 network fetches),
    /// the objects drawing their previous LOD meanwhile (never nothing), the image the reference pass's; (4) a region reset
    /// frees everything. --tr-decodedbound=off (the negative control) runs the pass under test with the bound off too: it
    /// must fail step (2).
    /// </summary>
    private async Task<int> DecodedBoundCheck(string fixture, bool boundOff)
    {
        if (!fixture.EndsWith(".meshzoo.json", StringComparison.OrdinalIgnoreCase) || !_contentStore)
            throw new ArgumentException("--tr-decodedbound needs the mesh zoo and the content store (--tr-fixture=...meshzoo.json --tr-content-store)");
        var loaded = Load(fixture);
        var (_, assets) = MeshZoo.Parse(File.ReadAllText(fixture));
        using var meshes = MeshSourceFor(Path.GetDirectoryName(fixture)!, assets, RunContentView());
        var rig = MakeRig();
        var view = MakeView("server", rig);
        int Fetches() => s_meshFetches.Values.Sum();
        Image Shot() { var img = rig.Texture.GetImage(); img.Convert(Image.Format.Rgba8); return img; }
        var steps = new List<object>();
        var shots = new Dictionary<string, byte[]>();
        async Task Pass(string name, bool bound)
        {
            var prims = new PrimLayer(rig.Scenario, loaded.World, meshes, null, _budget) { SimulatedFps = 60, DecodedBound = bound };
            var presenter = new WorldPresenter(loaded.World, view, prims: prims, environment: new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun)) { DayFractionOverride = 0.3 };
            void Cam(string preset) { Stage.MeshCamera(rig.Camera, preset); prims.Camera = rig.Camera.GlobalPosition; prims.UpdateLods(); }
            async Task Run(int frames) { for (int i = 0; i < frames; i++) { presenter.Flush(); if (i % 15 == 0) await Frames(1); } await Frames(2); }
            void Note(string at) => steps.Add(new { pass = name, at, bound, objects = prims.MeshObjects, instances = prims.Instances, held = prims.MeshLodsHeld, drawn = prims.DistinctDrawnMeshLods,
                waiting = prims.MeshesWaiting, on_previous_lod = prims.MeshObjectsOnPreviousLod, lods = prims.MeshLods.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray(),
                decoded_entries = meshes.DecodedEntries, decoded_bytes = meshes.DecodedBytes, decoded_kept = prims.DecodedKept, let_go = meshes.LetGo, let_go_at_reset = meshes.LetGoAtReset,
                re_decodes = meshes.ReDecodes, re_decodes_within_10s = meshes.ReDecodesSoon, network_re_decodes = meshes.NetworkReDecodes, sources = meshes.ReDecodeSources, fetches = Fetches() });
            bool test = name == "test";
            // (1) near, settled
            Cam("near");
            await SettleMeshes(presenter, prims, meshes);
            await Run(4);
            shots[name + " near"] = Shot().GetData();
            Note("near");
            int objects = prims.MeshObjects, held = prims.MeshLodsHeld;
            if (objects == 0 || held != prims.DistinctDrawnMeshLods || prims.MeshesWaiting != 0) _failures.Add($"{name} near: {objects} objects, {held} LODs held for {prims.DistinctDrawnMeshLods} drawn, {prims.MeshesWaiting} waiting");
            if (test && (meshes.DecodedEntries == 0 || prims.DecodedKept == 0)) _failures.Add($"test near: {meshes.DecodedEntries} decoded entries, {prims.DecodedKept} kept: nothing decoded to let go");
            // (2) 10.5 s later, the camera still
            await Run(630);
            shots[name + " 10.5 s"] = Shot().GetData();
            Note("10.5 s later");
            if (test)
            {
                if (meshes.DecodedEntries != 0 || meshes.DecodedBytes != 0 || prims.DecodedKept != 0)
                    _failures.Add($"test 10.5 s: {meshes.DecodedEntries} decoded entries ({meshes.DecodedBytes} bytes, {prims.DecodedKept} kept) 10.5 s after the last LOD build: not let go");
                if (prims.MeshLodsHeld != held || prims.MeshObjects != objects || prims.Instances < objects)
                    _failures.Add($"test 10.5 s: {prims.MeshLodsHeld} LODs held (near {held}), {prims.MeshObjects} objects (near {objects}): a drawn LOD was released");
                if (!shots["test 10.5 s"].AsSpan().SequenceEqual(shots["test near"])) _failures.Add("test 10.5 s: the image changed");
            }
            // (3) far: LODs not on the GPU are built from geometry decoded again; the objects keep drawing meanwhile
            int fetchesBefore = Fetches();
            long reBefore = meshes.ReDecodes;
            Cam("far");
            presenter.Flush();
            Note("far, first frame");
            if (prims.MeshObjects != objects || prims.Instances < objects) _failures.Add($"{name} far, first frame: {prims.MeshObjects} of {objects} mesh objects drawn ({prims.Instances} instances): an object drew nothing");
            await SettleMeshes(presenter, prims, meshes);
            await Run(4);
            shots[name + " far"] = Shot().GetData();
            Note("far, settled");
            if (prims.MeshLodsHeld <= held || prims.MeshesWaiting != 0) _failures.Add($"{name} far: {prims.MeshLodsHeld} LODs held (near {held}), {prims.MeshesWaiting} waiting: the far LODs were not all built");
            if (Fetches() != fetchesBefore || meshes.NetworkReDecodes != 0) _failures.Add($"{name} far: {Fetches() - fetchesBefore} network fetches, {meshes.NetworkReDecodes} re-decodes from the network");
            if (test && meshes.ReDecodes - reBefore == 0) _failures.Add("test far: nothing was decoded again (every far LOD was built from kept geometry)");
            if (!test) { presenter.Dispose(); return; }
            // (4) a region reset frees everything
            presenter.Apply(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(loaded.World.Epoch + 1, 0)));
            Note("region reset");
            if (prims.LiveResources != 0 || prims.MeshLodsHeld != 0 || meshes.DecodedEntries != 0 || meshes.DecodedBytes != 0 || prims.DecodedKept != 0)
                _failures.Add($"region reset: {prims.LiveResources} resources, {prims.MeshLodsHeld} LODs held, {meshes.DecodedEntries} decoded entries ({meshes.DecodedBytes} bytes), {prims.DecodedKept} kept");
            presenter.Dispose();
        }
        await Pass("reference", bound: false);
        await Pass("test", bound: !boundOff);
        foreach (var at in new[] { "near", "far" })
            if (!shots["test " + at].AsSpan().SequenceEqual(shots["reference " + at])) _failures.Add($"test {at}: the image differs from the reference pass's (bound off)");
        view.Clear();
        loaded.Dispose();
        _result["mode"] = "decodedbound";
        _result["decodedbound"] = new
        {
            bound_off = boundOff, shared_store_bytes = SharedAssets.Bound, steps,
            re_decodes = meshes.ReDecodes, re_decodes_within_10s = meshes.ReDecodesSoon, network_re_decodes = meshes.NetworkReDecodes, let_go = meshes.LetGo, let_go_at_reset = meshes.LetGoAtReset,
            re_decode_ms = meshes.ReDecodeMs.Length > 0 ? Dist(meshes.ReDecodeMs.ToList()) : null, re_decode_sources = meshes.ReDecodeSources,
        };
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Terrain chunks built and encoded off the main thread.
    /// (1) The fixture's terrain (the committed synthetic terrain, or a local capture) presented through the server view:
    /// every chunk handed over is byte-identical to the main-thread build of the same patches (TerrainGrid.Build and the
    /// encoder, run here afterwards), and no chunk was built or encoded on the main thread meanwhile (both counters 0).
    /// (2) A patch changed while its chunk prepares (the preparations held at a gate): the stale result is discarded,
    /// every chunk handed over at the end equals a fresh build's bytes, and the image equals a fresh presenter's.
    /// (3) A region reset while chunks prepare: nothing from the old region is handed over, and the view's resources, the
    /// chunks in flight and the preparations running return to zero.
    /// --tr-terrain-main-thread (every chunk built on the main thread, as before) must fail it.
    /// </summary>
    private async Task<int> TerrainPrepCheck(string fixture)
    {
        long builtMain0 = TerrainGrid.BuiltOnMainThread, encodedMain0 = TerrainSurface.EncodedOnMainThread;
        long builtMain = 0, encodedMain = 0;
        void MainCounts() { builtMain += TerrainGrid.BuiltOnMainThread - builtMain0; encodedMain += TerrainSurface.EncodedOnMainThread - encodedMain0; }
        void MainMark() { builtMain0 = TerrainGrid.BuiltOnMainThread; encodedMain0 = TerrainSurface.EncodedOnMainThread; }
        static bool Same(PreparedSurface? a, PreparedSurface? b) => (a is null && b is null) || (a is not null && b is not null && a.Format == b.Format && a.VertexCount == b.VertexCount
            && a.IndexCount == b.IndexCount && a.VertexData.AsSpan().SequenceEqual(b.VertexData) && a.AttributeData.AsSpan().SequenceEqual(b.AttributeData)
            && a.IndexData.AsSpan().SequenceEqual(b.IndexData) && a.Aabb.AsSpan().SequenceEqual(b.Aabb));
        // what the main thread would build now from the world's patches: the reference (run on the main thread, off the count)
        Dictionary<(int, int), PreparedSurface?> Reference(WorldState world)
        {
            MainCounts();
            var r = world.Region!;
            var grid = new TerrainGrid(r.SizeX, r.SizeY, 16, world.TryGetPatch) { Composition = r.Terrain, OriginX = r.GridX * 256.0, OriginY = r.GridY * 256.0 };
            var d = new Dictionary<(int, int), PreparedSurface?>();
            for (int cy = 0; cy < grid.ChunksY; cy++)
                for (int cx = 0; cx < grid.ChunksX; cx++)
                    d[(cx, cy)] = grid.Build(cx, cy) is { } c ? TerrainSurface.Prepare(c) : null;
            MainMark();
            return d;
        }
        Image Shot(Rig rig) { var img = rig.Texture.GetImage(); img.Convert(Image.Format.Rgba8); return img; }
        bool control = WorldPresenter.TerrainOnMainThread;

        // (1) the fixture's terrain, every chunk
        var loaded = Load(fixture);
        var region = loaded.World.Region!;
        var rig = MakeRig();
        var view = MakeView("server", rig);
        var handed = new Dictionary<(int, int), PreparedSurface?>();
        int handovers = 0;
        var presenter = new WorldPresenter(loaded.World, view) { OnTerrainHandOver = (x, y, s) => { handed[(x, y)] = s; handovers++; } };
        presenter.Flush();
        MainCounts();
        var reference = Reference(loaded.World);
        int identical = reference.Count(kv => handed.TryGetValue(kv.Key, out var s) && Same(s, kv.Value));
        int composed = reference.Count(kv => kv.Value is { AttributeData.Length: > 0 } && region.Terrain is not null);
        int removed = reference.Count(kv => kv.Value is null);
        if (control) _failures.Add("terrain chunks were built on the main thread (no preparer)");
        if (identical != reference.Count) _failures.Add($"fixture: {identical} of {reference.Count} chunks handed over byte-identical to the main-thread build");
        var prep1 = presenter.TerrainPrep;
        object Prep(TerrainPreparer? p) => p is null ? new { none = true } : (object)new { prepared = p.Prepared, stale = p.Stale, discarded = p.Discarded, failed = p.Failed, in_flight = p.InFlight, running = p.Running, in_flight_at_clear = p.InFlightAtClear, peak_running = p.PeakRunning };
        var fixturePart = new { chunks = reference.Count, identical, with_terrain = reference.Count - removed, removed, composition = region.Terrain is not null, handovers, prep = Prep(prep1),
            prepare_ms = prep1 is null ? null : DistOrNull(prep1.PrepareMs.ToList()), snapshot_ms = DistOrNull(presenter.SnapshotMs.ToList()) };
        presenter.Dispose();
        loaded.Dispose();

        // (2) a patch changed while its chunk prepares (the committed synthetic terrain)
        var world2 = FromTerrain(SyntheticTerrain.Make("terrainprep", 256, 256), "terrainprep", "generated");
        var rig2 = MakeRig();
        Stage.Overview(rig2.Camera, 256, 256);
        var view2 = MakeView("server", rig2);
        handed.Clear();
        var p2 = new WorldPresenter(world2.World, view2) { OnTerrainHandOver = (x, y, s) => handed[(x, y)] = s };
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        object? stalePart = null;
        bool imageEqual = false;
        if (p2.TerrainPrep is { } tp2)
        {
            tp2.Gate = t => hold.Task.WaitAsync(t);
            p2.Coordinator.RunFrame();   // the first chunks start, held at the gate
            var until = DateTime.UtcNow.AddSeconds(10);
            while (tp2.Running < tp2.Cap && DateTime.UtcNow < until) await Task.Delay(5);
            int heldAtChange = tp2.Running;
            // patch (1, 1) lies in chunk (0, 0), the first one started
            world2.World.TryGetPatch(1, 1, out var old);
            world2.World.ApplyPatch(FixtureSim, 1, 1, 16, old.Select(h => h + 6f).ToArray());
            p2.Coordinator.RunFrame();   // the change applied: the chunk is dirty again, its version moved on
            hold.SetResult();
            p2.Flush();
            MainCounts();
            var ref2 = Reference(world2.World);
            int identical2 = ref2.Count(kv => handed.TryGetValue(kv.Key, out var s) && Same(s, kv.Value));
            await Frames(4);
            var imgA = Shot(rig2);
            // a fresh presenter on the same world, in its own stage: the same image
            p2.Dispose();
            var view3 = MakeView("server", rig2);
            var p3 = new WorldPresenter(world2.World, view3);
            p3.Flush();
            MainCounts();
            await Frames(4);
            var imgB = Shot(rig2);
            imageEqual = imgA.GetData().AsSpan().SequenceEqual(imgB.GetData());
            p3.Dispose();
            MainMark();
            stalePart = new { held_at_change = heldAtChange, stale = tp2.Stale, chunks = ref2.Count, identical = identical2, image_equal_to_fresh = imageEqual };
            if (heldAtChange < 1) _failures.Add("stale: no chunk was preparing when its patch changed");
            if (tp2.Stale < 1) _failures.Add("stale: the result prepared from the old patch was not discarded");
            if (identical2 != ref2.Count) _failures.Add($"stale: {identical2} of {ref2.Count} chunks equal to a fresh build at the end");
            if (!imageEqual) _failures.Add("stale: the final image differs from a fresh presenter's");
        }
        else p2.Dispose();

        // (3) a region reset while chunks prepare
        var rig3 = MakeRig();
        var view4 = MakeView("server", rig3);
        int afterReset = 0;
        bool reset = false;
        var p4 = new WorldPresenter(world2.World, view4) { OnTerrainHandOver = (x, y, s) => { if (reset) afterReset++; } };
        object? resetPart = null;
        if (p4.TerrainPrep is { } tp4)
        {
            var hold4 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tp4.Gate = t => hold4.Task.WaitAsync(t);
            p4.Coordinator.RunFrame();
            var until = DateTime.UtcNow.AddSeconds(10);
            while (tp4.Running < tp4.Cap && DateTime.UtcNow < until) await Task.Delay(5);
            int inFlight = tp4.InFlight;
            reset = true;
            p4.Apply(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(world2.World.Epoch + 1, 0)));
            hold4.SetResult();
            until = DateTime.UtcNow.AddSeconds(10);
            while (tp4.Running > 0 && DateTime.UtcNow < until) await Task.Delay(5);
            for (int i = 0; i < 4; i++) p4.Coordinator.RunFrame();
            await Frames(2);
            resetPart = new { in_flight_at_reset = inFlight, handed_over_after = afterReset, live_resources = view4.LiveResources, chunks = view4.ChunkCount, in_flight = tp4.InFlight, running = tp4.Running, discarded = tp4.Discarded, in_flight_at_clear = tp4.InFlightAtClear };
            if (inFlight < 1) _failures.Add("reset: no chunk was preparing at the reset");
            if (afterReset != 0) _failures.Add($"reset: {afterReset} chunks from the old region handed over");
            if (view4.LiveResources != 0 || tp4.InFlight != 0 || tp4.Running != 0) _failures.Add($"reset: {view4.LiveResources} resources, {tp4.InFlight} in flight, {tp4.Running} running");
        }
        MainCounts();
        p4.Dispose();
        world2.Dispose();
        if (builtMain != 0 || encodedMain != 0) _failures.Add($"main thread: {builtMain} chunks built and {encodedMain} encoded on it");
        _result["mode"] = "terrainprep";
        _result["terrainprep"] = new { fixture = fixturePart, stale = stalePart, reset = resetPart, built_on_main_thread = builtMain, encoded_on_main_thread = encodedMain, control };
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// The surface data prepared off the main thread
    /// (<see cref="MeshSurfaceData"/>) is the engine's own, byte for byte. For every LOD of every mesh-zoo mesh, and
    /// synthetic faces for the edge cases (over 65,535 vertices, so 32-bit indices; normals on every axis and below the
    /// horizon), today's main-thread path (<see cref="PrimLayer.SurfaceMesh(IReadOnlyList{MeshFace})"/>, the engine packing
    /// our arrays) and the handover of the prepared data (<see cref="PrimLayer.HandOver"/>) each make a mesh; every surface
    /// read back (RenderingServer.MeshGetSurface) must be identical key by key, and the prepared bytes identical to today's.
    /// </summary>
    private async Task<int> MeshPrepCheck(string fixture)
    {
        var loaded = Load(fixture);
        if (loaded.Meshes is null) throw new ArgumentException("--tr-meshprep needs the mesh zoo (--tr-fixture=...meshzoo.json --tr-meshes=)");
        var ids = loaded.World.Snapshot().Objects.Values.Where(o => o.Shape?.SculptType == PrimShape.SculptTypeMesh).Select(o => o.Shape!.SculptId).Distinct().OrderBy(g => g).ToList();
        foreach (var id in ids) loaded.Meshes.TryGet(id, out _);
        var until = DateTime.UtcNow.AddSeconds(20);
        while (loaded.Meshes.Pending > 0 && DateTime.UtcNow < until) await Task.Delay(10);
        // every kind of surface the viewer draws. Today: the engine packing the arrays today's code gave it (the
        // harness's reference, every argument held); prepared: what the viewer now hands over.
        var cases = new List<(string Name, string Kind, Func<Rid> Today, Func<PreparedMeshLod> Prepared, int Surfaces, int Vertices)>();
        void Faces(string name, string kind, IReadOnlyList<MeshFace> faces, Func<PreparedMeshLod>? prepared = null) =>
            cases.Add((name, kind, () => EnginePacked(faces), prepared ?? (() => MeshSurfaceData.Prepare(faces)), faces.Count, faces.Sum(f => f.Vertices)));
        foreach (var id in ids)
            if (loaded.Meshes.TryGet(id, out var g) == MeshAvailability.Ready)
                for (int lod = 0; lod < MeshGeometry.LodCount; lod++)
                    if (g!.Lods[lod] is { } l) Faces($"{id.ToString()[..8]} lod {lod}", "mesh", l.Faces);
        int zooLods = cases.Count;
        Faces("synthetic 70,000 vertices", "mesh", [SyntheticFace(70_000, 1)]);
        Faces("synthetic 65,536 vertices", "mesh", [SyntheticFace(65_536, 2)]);
        Faces("synthetic 65,535 vertices", "mesh", [SyntheticFace(65_535, 3)]);
        Faces("synthetic axis normals", "mesh", [AxisNormalsFace()]);
        Faces("synthetic zero normals", "mesh", [ZeroNormalsFace()]);
        Faces("synthetic tiny spread", "mesh", [TinySpreadFace()]);
        // every prim-zoo shape, as the prim layer builds it
        var shapes = PrimZoo.Make().Select(z => z.Shape).Where(sh => sh.SculptType == 0).Distinct().ToList();
        foreach (var sh in shapes)
        {
            var faces = PrimGeometry.Build(sh, Viewer.Core.Meshing.PrimLodGroup.Scales[3]).Select(f => new MeshFace(f.Positions, f.Normals, f.Uvs, f.Indices)).ToList();
            Faces($"prim {cases.Count(c => c.Kind == "prim")}", "prim", faces, () => PrimLayer.PrimSurfaces(sh));
        }
        int primShapes = cases.Count(c => c.Kind == "prim");
        // every sculpt-zoo type (its maps decoded through the texture path) at every LOD, the missing map's stand-in too
        string sculptFixture = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fixture)!, "..", "sculpts", "sculpt-zoo.sculptzoo.json"));
        var sculpts = Load(sculptFixture);
        var sculptObjects = sculpts.World.Snapshot().Objects.Values.Where(o => o.Shape is { IsSculpt: true }).OrderBy(o => o.Id.LocalId).ToList();
        foreach (var o in sculptObjects) sculpts.Textures!.TryGet(o.Shape!.SculptId, out _);
        var sculptUntil = DateTime.UtcNow.AddSeconds(20);
        while (sculpts.Textures!.Pending > 0 && DateTime.UtcNow < sculptUntil) await Task.Delay(10);
        var sculptTypes = new HashSet<int>();
        foreach (var o in sculptObjects)
        {
            var sh = o.Shape!;
            int type = sh.SculptType | sh.SculptFlags;
            sculpts.Textures.TryGet(sh.SculptId, out var map);
            for (int lod = 0; lod < 4; lod++)
            {
                var r = SculptMesher.FromMap(map, sculpts.Textures.IsMissing(sh.SculptId), type, lod);
                if (r.Face is not { } face) continue;
                sculptTypes.Add(type);
                Faces($"sculpt {o.Id.LocalId} type {type} lod {lod} {r.Outcome}", "sculpt", [face], () => PrimLayer.SculptSurfaces(face));
            }
        }
        sculpts.Dispose();
        // terrain chunks (colours; with a composition, its UV too) and the water plane
        var terrainWorld = FromTerrain(SyntheticTerrain.Make("meshprep", 256, 256), "meshprep terrain", "generated");
        foreach (var composed in new[] { false, true })
        {
            var grid = new TerrainGrid(256, 256, 16, terrainWorld.World.TryGetPatch) { Composition = composed ? TerrainDetails.SyntheticTerrain : null };
            foreach (var (cx, cy) in new[] { (0, 0), (1, 2), (grid.ChunksX - 1, grid.ChunksY - 1) })
                if (grid.Build(cx, cy) is { } chunk)
                    cases.Add(($"terrain {cx},{cy}{(composed ? " composed" : "")}", "terrain", () => EnginePacked(held => { var arrays = new global::Godot.Collections.Array(); ChunkArrays.Fill(arrays, chunk, held); return arrays; }), () => new PreparedMeshLod([ServerTerrainView.Prepare(chunk)]), 1, chunk.VertexCount));
        }
        terrainWorld.Dispose();
        cases.Add(("water", "water", () => EnginePacked(WaterArrays), () => new PreparedMeshLod([EnvironmentLayer.WaterSurface()]), 1, 4));
        var mismatches = new List<string>();
        var rows = new List<(string Name, string Kind, int Surfaces, int Vertices, int Identical)>();
        object? layout = null;
        var layouts = new Dictionary<string, object>();
        foreach (var (name, kind, todayOf, preparedOf, surfaceCount, vertexCount) in cases)
        {
            Rid today = todayOf();
            PreparedMeshLod? prepared = null;
            try { prepared = preparedOf(); }
            catch (Exception e) { mismatches.Add($"{name}: prepare threw {e.GetType().Name}: {e.Message}"); }
            // negative control (--tr-alter=prepbyte): one bit of every prepared surface changed; every one must be caught
            if (_prepAlter && prepared is not null)
                prepared = new PreparedMeshLod(prepared.Surfaces.Select(x => { var v = (byte[])x.VertexData.Clone(); v[0] ^= 1; return x with { VertexData = v }; }).ToList());
            Rid handed = prepared is null ? default : PrimLayer.HandOver(prepared, out _);
            int same = 0;
            if (prepared is not null && prepared.Surfaces.Count != surfaceCount) mismatches.Add($"{name}: {prepared.Surfaces.Count} prepared surfaces vs {surfaceCount}");
            for (int s = 0; s < surfaceCount; s++)
            {
                var t = RenderingServer.MeshGetSurface(today, s);
                layout ??= Describe(t, today, s);
                if (!layouts.ContainsKey(kind)) layouts[kind] = Describe(t, today, s);
                if (prepared is null || s >= prepared.Surfaces.Count) continue;
                var p = prepared.Surfaces[s];
                var diffs = new List<string>();
                void Bytes(string key, byte[] mine) { var theirs = t.TryGetValue(key, out var v) ? v.AsByteArray() : []; if (!theirs.AsSpan().SequenceEqual(mine)) diffs.Add($"{key} {mine.Length} bytes vs {theirs.Length}, first difference at {FirstDiff(theirs, mine)}"); }
                Bytes("vertex_data", p.VertexData);
                Bytes("attribute_data", p.AttributeData);
                Bytes("index_data", p.IndexData);
                if ((ulong)t["format"].AsInt64() != p.Format) diffs.Add($"format {p.Format:X} vs {(ulong)t["format"].AsInt64():X}");
                if (t["vertex_count"].AsInt32() != p.VertexCount) diffs.Add($"vertex_count {p.VertexCount} vs {t["vertex_count"].AsInt32()}");
                int ic = t.TryGetValue("index_count", out var icv) ? icv.AsInt32() : 0;
                if (ic != p.IndexCount) diffs.Add($"index_count {p.IndexCount} vs {ic}");
                var ta = t["aabb"].AsAabb();
                var pa = new Aabb(new Vector3(p.Aabb[0], p.Aabb[1], p.Aabb[2]), new Vector3(p.Aabb[3], p.Aabb[4], p.Aabb[5]));
                if (ta.Position != pa.Position || ta.Size != pa.Size) diffs.Add($"aabb {pa} vs {ta}");
                // the handed-over mesh, read back from the engine, is today's surface key by key
                var h = RenderingServer.MeshGetSurface(handed, s);
                foreach (var key in t.Keys.Select(k => k.AsString()).Union(h.Keys.Select(k => k.AsString())).Distinct())
                {
                    bool inT = t.TryGetValue(key, out var tv), inH = h.TryGetValue(key, out var hv);
                    if (inT != inH) { diffs.Add($"read back: key {key} {(inT ? "missing" : "extra")}"); continue; }
                    bool eq = tv.VariantType == Variant.Type.PackedByteArray ? tv.AsByteArray().AsSpan().SequenceEqual(hv.AsByteArray()) : GD.VarToStr(tv) == GD.VarToStr(hv);
                    if (!eq) diffs.Add($"read back: {key} differs");
                }
                if (diffs.Count == 0) same++;
                else if (mismatches.Count < 40) mismatches.Add($"{name} surface {s}: {string.Join("; ", diffs)}");
            }
            rows.Add((name, kind, surfaceCount, vertexCount, same));
            Rids.Free(today);
            if (handed.IsValid) Rids.Free(handed);
        }
        if (zooLods == 0) mismatches.Add("no mesh-zoo LOD decoded");
        if (primShapes < 19) mismatches.Add($"only {primShapes} prim-zoo shapes (the zoo has 19)");
        if (sculptTypes.Count < 4) mismatches.Add($"only {sculptTypes.Count} sculpt types ({string.Join(",", sculptTypes)})");
        _failures.AddRange(mismatches);
        var lifecycle = await MeshPrepLifecycle(loaded);
        loaded.Dispose();
        _result["mode"] = "meshprep";
        _result["meshprep"] = new
        {
            zoo_lods = zooLods, prim_shapes = primShapes, sculpt_types = sculptTypes.OrderBy(x => x).ToArray(),
            by_kind = rows.GroupBy(r => r.Kind).ToDictionary(k => k.Key, k => new { cases = k.Count(), surfaces = k.Sum(r => r.Surfaces), identical = k.Sum(r => r.Identical) }),
            cases = rows.Select(r => new { name = r.Name, kind = r.Kind, surfaces = r.Surfaces, vertices = r.Vertices, identical_surfaces = r.Identical }).ToArray(),
            layout, layouts, mismatches = mismatches.Count, main_thread_control = _meshMainThread, altered = _prepAlter, lifecycle,
        };
        return _failures.Count == 0 ? 0 : 1;

        static int FirstDiff(byte[] a, byte[] b) { int n = Math.Min(a.Length, b.Length); for (int i = 0; i < n; i++) if (a[i] != b[i]) return i; return n; }
        static object Describe(global::Godot.Collections.Dictionary t, Rid mesh, int s) => new
        {
            keys = t.Keys.Select(k => k.AsString()).ToArray(), format = t["format"].AsInt64().ToString("X"),
            vertex_stride = RenderingServer.MeshSurfaceGetFormatVertexStride((RenderingServer.ArrayFormat)t["format"].AsInt64(), t["vertex_count"].AsInt32()),
            normal_tangent_stride = RenderingServer.MeshSurfaceGetFormatNormalTangentStride((RenderingServer.ArrayFormat)t["format"].AsInt64(), t["vertex_count"].AsInt32()),
            attribute_stride = RenderingServer.MeshSurfaceGetFormatAttributeStride((RenderingServer.ArrayFormat)t["format"].AsInt64(), t["vertex_count"].AsInt32()),
            index_stride = RenderingServer.MeshSurfaceGetFormatIndexStride((RenderingServer.ArrayFormat)t["format"].AsInt64(), t["vertex_count"].AsInt32()),
            vertex_bytes = t["vertex_data"].AsByteArray().Length, attribute_bytes = t.TryGetValue("attribute_data", out var ad) ? ad.AsByteArray().Length : -1,
            index_bytes = t.TryGetValue("index_data", out var id) ? id.AsByteArray().Length : -1,
            vertex_head = Convert.ToHexString(t["vertex_data"].AsByteArray().Take(48).ToArray()),
            attribute_head = t.TryGetValue("attribute_data", out var ad2) ? Convert.ToHexString(ad2.AsByteArray().Take(32).ToArray()) : null,
            aabb = t["aabb"].AsAabb().ToString(), uv_scale = t.TryGetValue("uv_scale", out var uvs) ? GD.VarToStr(uvs) : null,
        };
    }

    /// <summary>
    /// Every surface-building caller the
    /// viewer has, many times over, while another thread forces full, blocking, compacting collections and runs the
    /// finalizers, so a Godot object the GC can reach during a native call is freed while the engine uses it. Each
    /// iteration is a region: the prim zoo (every shape) and a glass box (its blended faces drawn by part meshes), the sculpt zoo (every type; its maps from --tr-textures=...\..\
    /// sculpts) through the prim layer, synthetic 256 m terrain through the server view, the water plane (with
    /// --tr-environment=), and two large faces (70,000 and 30,000 vertices, 10 times each) through the sculpt and mesh surface function
    /// (the engine's call then takes milliseconds, not microseconds) and 2,000 small ones (4 vertices: many calls); then the region is reset and everything freed; the
    /// camera alternates near and far so sculpts are built at more than one LOD. Reports iterations, surfaces built and
    /// collections forced. Surviving is the result: a crash ends the process before the sidecar is written.
    /// </summary>
    private async Task<int> SurfaceStress(double seconds)
    {
        var rig = MakeRig();
        await Frames(3);
        var view = MakeView("server", rig);
        var scenario = rig.Scenario;
        var zooPrims = PrimZoo.AsPrimData(PrimZoo.Make()).ToList();
        // and a glass box (every face blended): its blended faces drawn by part meshes, made and freed every region
        // (9901: an id neither zoo uses)
        zooPrims.AddRange(PrimZoo.AsPrimData([new ZooPrim(9901, 0, "glass-box", PrimZoo.Origin + new System.Numerics.Vector3(0, -3f, 0), System.Numerics.Quaternion.Identity,
            System.Numerics.Vector3.One, PrimShape.Box, new FaceColors(new Rgba(0.6f, 0.8f, 0.9f, 0.4f)))]));
        var sculptPrims = _texDir is null ? [] : PrimZoo.AsPrimData(SculptZoo.Make()).ToList();
        string? sculptDir = _texDir is null ? null : Path.GetFullPath(Path.Combine(_texDir, "..", "sculpts"));
        string? envLlsd = _envFile is null ? null : File.ReadAllText(_envFile);
        MeshFace[] large = [SyntheticFace(70_000, 11), SyntheticFace(30_000, 12)];
        var small = SyntheticFace(4, 13);
        long collections = 0, allocated = 0;
        using var stop = new CancellationTokenSource();
        var hammer = new Thread(() =>
        {
            var keep = new List<byte[]>();
            while (!stop.IsCancellationRequested)
            {
                for (int k = 0; k < 64; k++) keep.Add(new byte[4096 + k]);   // garbage, so every collection has work
                allocated += keep.Count;
                keep.Clear();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                Interlocked.Increment(ref collections);
            }
        }) { IsBackground = true, Name = "surfacestress-gc" };
        long surfaces0 = PrimLayer.SurfacesAdded;
        long terrainSurfaces = 0, waterSurfaces = 0, largeSurfaces = 0, smallSurfaces = 0, partMeshes = 0;
        int iterations = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        hammer.Start();
        try
        {
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                var terrain = SyntheticTerrain.Make("stress", 256, 256);
                var loaded = FromTerrain(terrain, "surface stress", "generated");
                if (envLlsd is not null)
                {
                    loaded.World.BeginRegion(FixtureSim, loaded.World.Region! with { WaterHeight = 18f, EnvironmentLlsd = envLlsd });
                    foreach (var (x, y, h) in terrain.Patches())
                        loaded.World.ApplyPatch(FixtureSim, x, y, terrain.PatchSize, h);
                }
                foreach (var p in zooPrims.Concat(sculptPrims))
                    loaded.World.ApplyObject(FixtureSim, p);
                using var textures = sculptDir is null ? null : TextureSourceFor(sculptDir, SculptZoo.Images);
                var prims = new PrimLayer(scenario, loaded.World, null, textures, _budget) { SimulatedFps = 60 };
                prims.Camera = new Vector3(SculptZoo.Origin.X, SculptZoo.Origin.Z + 3, -SculptZoo.Origin.Y + (iterations % 2 == 0 ? 7 : 60));
                var envLayer = envLlsd is null ? null : new EnvironmentLayer(scenario, rig.Environment, rig.Sun);
                var presenter = new WorldPresenter(loaded.World, view, prims: prims, environment: envLayer) { DayFractionOverride = 0.3 };
                await SettleMeshes(presenter, prims, null, textures);
                if (prims.Instances != zooPrims.Count + sculptPrims.Count)
                    _failures.Add($"iteration {iterations}: {prims.Instances} of {zooPrims.Count + sculptPrims.Count} prims and sculpts presented");
                if (prims.BlendPartMeshes == 0 && _failures.Count < 20) _failures.Add($"iteration {iterations}: the glass box drew no part mesh");
                partMeshes += prims.BlendPartMeshes;
                terrainSurfaces += view.ChunkCount;
                if (envLayer is { HasWater: true }) waterSurfaces++;
                for (int rep = 0; rep < 10; rep++)
                foreach (var f in large)
                {
                    Rid m = PrimLayer.SurfaceMesh([f]);
                    Rids.Free(m);
                    largeSurfaces++;
                }
                // many calls, as the objects a call's arguments need are at risk only while the call starts
                for (int k = 0; k < 2000; k++)
                {
                    Rid m = PrimLayer.SurfaceMesh([small]);
                    Rids.Free(m);
                    smallSurfaces++;
                }
                VariantDisposal.DrainAll("stress");   // the round's held values disposed pinned, under the collections
                await Frames(1);
                presenter.Apply(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(loaded.World.Epoch + 1, 0)));
                if (prims.BlendPartMeshes != 0 && _failures.Count < 20) _failures.Add($"iteration {iterations}: {prims.BlendPartMeshes} part meshes left after the region reset");
                presenter.Dispose();
                loaded.Dispose();
                iterations++;
                if (iterations % 10 == 0) Console.Error.WriteLine($"surfacestress: {iterations} iterations, {Interlocked.Read(ref collections)} collections, {clock.Elapsed.TotalSeconds:F1} s");
            }
        }
        finally
        {
            stop.Cancel();
            hammer.Join();
        }
        view.Clear();
        await Frames(3);
        long primSurfaces = PrimLayer.SurfacesAdded - surfaces0;
        _result["mode"] = "surfacestress";
        _result["surfacestress"] = new
        {
            seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), iterations, collections = Interlocked.Read(ref collections), allocated_arrays = allocated,
            surfaces = primSurfaces + terrainSurfaces + waterSurfaces, prim_layer_surfaces = primSurfaces - largeSurfaces - smallSurfaces, terrain_surfaces = terrainSurfaces,
            water_surfaces = waterSurfaces, large_surfaces = largeSurfaces, small_surfaces = smallSurfaces, prims_per_region = zooPrims.Count, sculpts_per_region = sculptPrims.Count, part_meshes = partMeshes, survived = true,
        };
        _result["process_memory"] = StressMemory();   // (the memory check)
        if (iterations == 0) _failures.Add("no iteration ran");
        if (sculptPrims.Count == 0) _failures.Add("no sculpts (--tr-textures= is needed for the sculpt maps)");
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Prepared
    /// surface data handed to the engine through <see cref="PrimLayer.HandOver"/> (the path mesh LODs take) many
    /// times over, while another thread forces full, blocking, compacting collections and runs the finalizers, so a Godot
    /// value the GC can reach during the dictionary's set or the engine call is freed while the engine copies it.
    /// Each round: two large surfaces (70,000 and 30,000 vertices) handed over 10 times each (few large calls), a LOD of 8
    /// small surfaces 200 times, and single small surfaces (4 vertices; default 4,000: many small calls); every mesh is freed
    /// as soon as it is made. Reports rounds, handovers, surfaces handed over and collections forced. Surviving is the
    /// result: a crash ends the process before the sidecar is written.
    /// </summary>
    private async Task<int> HandoverStress(string spec)
    {
        var parts = spec.Split(',');
        double seconds = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        int smallPerRound = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 4000;
        MakeRig();
        await Frames(3);
        var large = new[] { MeshSurfaceData.Prepare([SyntheticFace(70_000, 21)]), MeshSurfaceData.Prepare([SyntheticFace(30_000, 22)]) };
        var multi = MeshSurfaceData.Prepare(Enumerable.Range(0, 8).Select(i => SyntheticFace(4 + i, 30 + i)).ToArray());
        var small = MeshSurfaceData.Prepare([SyntheticFace(4, 23)]);
        long collections = 0, allocated = 0;
        using var stop = new CancellationTokenSource();
        var hammer = new Thread(() =>
        {
            var keep = new List<byte[]>();
            while (!stop.IsCancellationRequested)
            {
                for (int k = 0; k < 64; k++) keep.Add(new byte[4096 + k]);   // garbage, so every collection has work
                allocated += keep.Count;
                keep.Clear();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                Interlocked.Increment(ref collections);
            }
        }) { IsBackground = true, Name = "handoverstress-gc" };
        long surfaces0 = PrimLayer.SurfacesAdded, handovers = 0, largeHandovers = 0, multiHandovers = 0, smallHandovers = 0;
        int rounds = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        hammer.Start();
        try
        {
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                for (int rep = 0; rep < 10; rep++)
                    foreach (var l in large) { Rids.Free(PrimLayer.HandOver(l, out _)); largeHandovers++; }
                for (int k = 0; k < 200; k++) { Rids.Free(PrimLayer.HandOver(multi, out _)); multiHandovers++; }
                for (int k = 0; k < smallPerRound; k++) { Rids.Free(PrimLayer.HandOver(small, out _)); smallHandovers++; }
                VariantDisposal.DrainAll("stress");   // the round's held values disposed pinned, under the collections
                await Frames(1);
                rounds++;
                if (rounds % 10 == 0) Console.Error.WriteLine($"handoverstress: {rounds} rounds, {Interlocked.Read(ref collections)} collections, {clock.Elapsed.TotalSeconds:F1} s");
            }
        }
        finally
        {
            stop.Cancel();
            hammer.Join();
        }
        await Frames(3);
        handovers = largeHandovers + multiHandovers + smallHandovers;
        long surfaces = PrimLayer.SurfacesAdded - surfaces0;
        _result["mode"] = "handoverstress";
        _result["handoverstress"] = new
        {
            seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), rounds, collections = Interlocked.Read(ref collections), allocated_arrays = allocated,
            handovers, surfaces, large_handovers = largeHandovers, multi_handovers = multiHandovers, small_handovers = smallHandovers, small_per_round = smallPerRound, survived = true,
        };
        _result["process_memory"] = StressMemory();   // (the memory check)
        if (rounds == 0) _failures.Add("no round ran");
        long expected = largeHandovers + multiHandovers * multi.Surfaces.Count + smallHandovers;
        if (surfaces != expected) _failures.Add($"{surfaces} surfaces handed over, expected {expected}");
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// The sites that
    /// let go of a Godot collection right after an engine call, as many times as possible, while other threads collect
    /// without waiting for the finalizers. GodotSharp 4.7.2 destroys a <c>Godot.Collections.Array</c> or <c>Dictionary</c>
    /// (its <c>Dispose</c>, and its finalizer) by handing the engine an unpinned pointer to the wrapper's own field, and the
    /// engine writes that field back when it is done; a compacting collection on another thread during that call moves the
    /// wrapper, so the engine writes to its old address (mechanism). Each round: single small surfaces (4
    /// vertices) through <see cref="PrimLayer.SurfaceMesh(IReadOnlyList{MeshFace})"/> (encoded on the main thread
    /// and handed over: a surface dictionary per call, as every prim, sculpt, water and terrain surface; before, a surface
    /// array, a blend-shape array and a LOD dictionary) and through <see cref="PrimLayer.HandOver(PreparedMeshLod, out double)"/>
    /// (a surface dictionary per call), default 4,000 each, every mesh freed at once. One
    /// thread runs gen-0 collections about once a millisecond (new wrappers live in gen 0, so each one moves them), another full blocking
    /// compacting collections; neither waits for the finalizers, so the finalizer thread's destroys meet collections too.
    /// Surviving is the result: a crash ends the process before the sidecar is written.
    /// </summary>
    private async Task<int> DisposeStress(string spec)
    {
        var parts = spec.Split(',');
        double seconds = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        int perRound = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 4000;
        MakeRig();
        await Frames(3);
        var small = SyntheticFace(4, 41);
        var prepared = MeshSurfaceData.Prepare([SyntheticFace(4, 42)]);
        long gen0 = 0, full = 0;
        using var stop = new CancellationTokenSource();
        var young = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                GC.Collect(0, GCCollectionMode.Forced, blocking: true);
                Interlocked.Increment(ref gen0);
                Thread.Sleep(1);   // about one a millisecond: back to back, they starve the main thread (1 round in 55 s)
            }
        }) { IsBackground = true, Name = "disposestress-gen0" };
        var old = new Thread(() =>
        {
            var keep = new List<byte[]>();
            while (!stop.IsCancellationRequested)
            {
                for (int k = 0; k < 64; k++) keep.Add(new byte[4096 + k]);   // garbage, so every collection has work
                keep.Clear();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                Interlocked.Increment(ref full);
            }
        }) { IsBackground = true, Name = "disposestress-full" };
        long surfaceCalls = 0, handovers = 0, surfaces0 = PrimLayer.SurfacesAdded;
        int rounds = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        young.Start();
        old.Start();
        try
        {
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                for (int k = 0; k < perRound; k++) { Rids.Free(PrimLayer.SurfaceMesh([small])); surfaceCalls++; }
                for (int k = 0; k < perRound; k++) { Rids.Free(PrimLayer.HandOver(prepared, out _)); handovers++; }
                VariantDisposal.DrainAll("stress");   // the round's held values disposed pinned, under the collections
                await Frames(1);
                rounds++;
                if (rounds % 10 == 0) Console.Error.WriteLine($"disposestress: {rounds} rounds, {Interlocked.Read(ref gen0)} gen-0 and {Interlocked.Read(ref full)} full collections, {clock.Elapsed.TotalSeconds:F1} s");
            }
        }
        finally
        {
            stop.Cancel();
            young.Join();
            old.Join();
        }
        await Frames(3);
        long surfaces = PrimLayer.SurfacesAdded - surfaces0;
        _result["mode"] = "disposestress";
        _result["disposestress"] = new
        {
            seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), rounds, per_round = perRound, surface_calls = surfaceCalls, handovers,
            collections = Interlocked.Read(ref gen0) + Interlocked.Read(ref full), gen0_collections = Interlocked.Read(ref gen0), full_collections = Interlocked.Read(ref full),
            collection_destroys = surfaceCalls + handovers, survived = true,   // one dictionary use per call on both (the kept one since none destroyed; the name kept for the reports)
        };
        _result["process_memory"] = StressMemory();
        if (rounds == 0) _failures.Add("no round ran");
        if (surfaces != surfaceCalls + handovers) _failures.Add($"{surfaces} surfaces added, expected {surfaceCalls + handovers}");
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Every site's Godot collections are kept and reused, and
    /// cleared after each engine call. (1) The Variant disposal guard: GodotSharp is 4.7.2 and the Variant's disposer member
    /// is found (else the viewer falls back to holding pattern, and this check fails). (2) 200 main-thread mesh
    /// builds (<see cref="PrimLayer.SurfaceMesh(IReadOnlyList{MeshFace})"/>) and 200 handovers
    /// (<see cref="PrimLayer.HandOver"/>), 4- and 7-vertex surfaces alternated: the same wrapper every time, empty after
    /// every call, and each mesh has exactly its own vertex count (nothing carried from the call before). (3) The synthetic
    /// terrain with water, the prim zoo and the sculpt zoo presented twice on each terrain view (server, nodes), with a
    /// region reset between: every site's set made, the same wrappers after the second load, all empty. Negative controls:
    /// =fresh (a new set at every use) and =noclear (no clearing after the call) must fail.
    /// (0) all six sets exist before any site's first use, made at startup at frame 0, and
    /// none is made later; (3) each load runs frame by frame first (<see cref="WorldPresenter.Frame"/>: the uploads within
    /// the budget, then the bounded drain) until nothing is dirty and the queue has emptied by its per-frame drains alone,
    /// then settles; the queue is empty after settling and after the region reset; (4) no Variant was disposed inside a
    /// site's call, every value queued was disposed exactly once (queued = disposed, none waiting, so none left to a
    /// finalizer), no drain failed, no value started after its frame's slice was spent, no frame's disposals took over
    /// 1 ms, none was forced past its bound, and the per-frame drain ran. On the earlier version (0) and (4)'s first check
    /// fail. Drain controls: =slowdrain (some disposals slowed) and =noslice (the slice ignored) must fail the drain's
    /// bound; =stall (the thread stalled between values) must pass it.
    /// Every surface is handed over through the one kept set left, the handover's
    /// dictionary (the five array sets of the MeshAddSurfaceFromArrays sites had no shipping site left). So: (0) that one
    /// set exists at startup (frame 0) and none is made later; (2) the main-thread build (now encode and hand over), the
    /// prepared LOD's handover and the single-surface handover the terrain and water use (the water plane, 4 vertices)
    /// each get the same wrapper, empty after the call, their own vertex count; (3) on every load the handover is the only
    /// site, the same wrapper, empty, and prims, terrain chunks (server view) and the water were all drawn through it.
    /// </summary>
    private async Task<int> KeptCollectionsCheck(string spec)
    {
        KeptCollections.FreshForTest = spec == "=fresh";
        KeptCollections.SkipClearForTest = spec == "=noclear";
        // every site's set made at startup, before the first frame, so no timed unit pays it
        int setsAtStart = KeptCollections.Made().Count();
        long? preparedAt = KeptCollections.PreparedAtFrame;
        bool controlRun = spec is "=fresh" or "=noclear";
        // the drain's controls run every check: a slowed drain (every 20th disposal 1.2 ms longer) and a drain that
        // ignores its slice must fail the drain's bound; a stalled thread (2 ms after every 50th value, between values)
        // must pass it (only the wall time, reported, shows the stall)
        string? drainControl = spec is "=slowdrain" or "=noslice" or "=stall" ? spec[1..] : null;
        if (drainControl is not null)
        {
            if (VariantDisposal.Queue is not { } dq) _failures.Add($"no disposal queue for the {drainControl} control");
            else
            {
                int n = 0;
                if (drainControl == "slowdrain") dq.InsideValueForTest = () => { if (n++ % 20 == 0) SpinMs(1.2); };
                if (drainControl == "noslice") dq.IgnoreSliceForTest = true;
                if (drainControl == "stall") dq.AfterValueForTest = () => { if (n++ % 50 == 0) SpinMs(2.0); };
            }
        }
        if (!controlRun)
        {
            if (preparedAt != 0) _failures.Add($"kept sets not made at startup before the first frame (made at frame {(preparedAt?.ToString() ?? "never")})");
            if (setsAtStart != 1) _failures.Add($"{setsAtStart} kept sets existed before any site's first use (the handover's alone expected)");
        }
        var rig = MakeRig();
        await Frames(3);
        // (1) the guard
        if (VariantDisposal.Unavailable is { } why) _failures.Add($"Variant disposal unavailable: {why}");
        string godotSharp = typeof(GodotObject).Assembly.GetName().Version?.ToString() ?? "?";
        if (!godotSharp.StartsWith("4.7.2", StringComparison.Ordinal)) _failures.Add($"GodotSharp {godotSharp}, not 4.7.2");
        // (2) the static sites
        var a = SyntheticFace(4, 51);
        var b = SyntheticFace(7, 52);
        var pa = MeshSurfaceData.Prepare([a])!;
        var pb = MeshSurfaceData.Prepare([b])!;
        var water = EnvironmentLayer.WaterSurface();
        object? handover = null;
        int notSame = 0, notEmpty = 0, wrongCount = 0;
        for (int i = 0; i < 200; i++)
        {
            var f = i % 2 == 0 ? a : b;
            // the main-thread build encodes and hands over through the same kept dictionary
            Rid m = PrimLayer.SurfaceMesh([f]);
            var ks = KeptCollections.Handover;
            handover ??= ks;
            if (!ReferenceEquals(handover, ks)) notSame++;
            if (ks.Surface.Count != 0) notEmpty++;
            if (SurfaceVertexCount(m) != f.Vertices) wrongCount++;
            Rids.Free(m);
            // the single-surface handover (terrain chunks and the water plane), alternated with the others
            Rid w = Rids.MeshCreate("test mesh");
            PrimLayer.HandOver(w, water);
            var kw = KeptCollections.Handover;
            if (!ReferenceEquals(handover, kw)) notSame++;
            if (kw.Surface.Count != 0) notEmpty++;
            if (SurfaceVertexCount(w) != 4) wrongCount++;
            Rids.Free(w);
            var p = i % 2 == 0 ? pa : pb;
            Rid h = PrimLayer.HandOver(p, out _);
            var kh = KeptCollections.Handover;
            handover ??= kh;
            if (!ReferenceEquals(handover, kh)) notSame++;
            if (kh.Surface.Count != 0) notEmpty++;
            if (SurfaceVertexCount(h) != (i % 2 == 0 ? a : b).Vertices) wrongCount++;
            Rids.Free(h);
        }
        // (3) every site, twice per terrain view
        var scenario = rig.Scenario;
        var zooPrims = PrimZoo.AsPrimData(PrimZoo.Make()).ToList();
        var sculptPrims = _texDir is null ? [] : PrimZoo.AsPrimData(SculptZoo.Make()).ToList();
        string? sculptDir = _texDir is null ? null : Path.GetFullPath(Path.Combine(_texDir, "..", "sculpts"));
        string? envLlsd = _envFile is null ? null : File.ReadAllText(_envFile);
        if (envLlsd is null) _failures.Add("no environment (--tr-environment): the water site is not exercised");
        var loads = new List<object>();
        int framesRun = 0;
        foreach (var viewKind in new[] { "server", "nodes" })
        {
            var view = MakeView(viewKind, rig);
            Dictionary<string, object>? first = null;
            for (int load = 0; load < 2; load++)
            {
                var terrain = SyntheticTerrain.Make("kept", 256, 256);
                var loaded = FromTerrain(terrain, "kept collections", "generated");
                if (envLlsd is not null)
                {
                    loaded.World.BeginRegion(FixtureSim, loaded.World.Region! with { WaterHeight = 18f, EnvironmentLlsd = envLlsd });
                    foreach (var (x, y, hgt) in terrain.Patches())
                        loaded.World.ApplyPatch(FixtureSim, x, y, terrain.PatchSize, hgt);
                }
                foreach (var p in zooPrims.Concat(sculptPrims))
                    loaded.World.ApplyObject(FixtureSim, p);
                using var textures = sculptDir is null ? null : TextureSourceFor(sculptDir, SculptZoo.Images);
                var prims = new PrimLayer(scenario, loaded.World, null, textures, _budget) { SimulatedFps = 60 };
                prims.Camera = new Vector3(SculptZoo.Origin.X, SculptZoo.Origin.Z + 3, -SculptZoo.Origin.Y + 7);
                var envLayer = envLlsd is null ? null : new EnvironmentLayer(scenario, rig.Environment, rig.Sun);
                var presenter = new WorldPresenter(loaded.World, view, prims: prims, environment: envLayer) { DayFractionOverride = 0.3 };
                // first as the viewer runs it, frame by frame (the uploads within the 4 ms budget, then the
                // bounded drain), until nothing is dirty and the queue has emptied by its per-frame drains alone
                int fr = 0;
                for (; fr < 900 && (presenter.Coordinator.Backlog > 0 || presenter.DirtyChunks > 0 || prims.Dirty > 0 || VariantDisposal.Queued > 0); fr++)
                {
                    presenter.Frame();
                    await Frames(1);
                }
                framesRun += fr;
                if (!controlRun && VariantDisposal.Queued > 0) _failures.Add($"{viewKind} load {load}: {VariantDisposal.Queued} values still queued after {fr} frames of bounded drains");
                await SettleMeshes(presenter, prims, null, textures);
                if (!controlRun && VariantDisposal.Queued > 0) _failures.Add($"{viewKind} load {load}: {VariantDisposal.Queued} values queued after settling");
                var made = KeptCollections.Made().ToDictionary(k => k.Site, k => k.Wrapper);
                int emptyNot = KeptCollections.Made().Count(k => (k.Arrays?.Count ?? 0) != 0 || (k.Dictionary?.Count ?? 0) != 0);
                notEmpty += emptyNot;
                if (first is null) first = made;
                else notSame += made.Count(kv => !first.TryGetValue(kv.Key, out var w) || !ReferenceEquals(w, kv.Value));
                loads.Add(new { view = viewKind, load, sites = made.Keys.OrderBy(s => s).ToArray(), not_empty = emptyNot, instances = prims.Instances, terrain_chunks = view.ChunkCount, water = envLayer is { HasWater: true } });
                // every kind drawn, all through the handover (the node view's terrain is the harness's own packing)
                if (prims.Instances == 0) _failures.Add($"{viewKind} load {load}: no prim or sculpt drawn");
                if (view.ChunkCount == 0) _failures.Add($"{viewKind} load {load}: no terrain chunk drawn");
                if (envLlsd is not null && envLayer is not { HasWater: true }) _failures.Add($"{viewKind} load {load}: no water drawn");
                presenter.Apply(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(loaded.World.Epoch + 1, 0)));
                if (!controlRun && VariantDisposal.Queued > 0) _failures.Add($"{viewKind} load {load}: {VariantDisposal.Queued} values queued after the region reset");
                presenter.Dispose();
                loaded.Dispose();
            }
            view.Clear();
        }
        var sites = KeptCollections.Made().Select(k => k.Site).OrderBy(s => s).ToArray();
        // the handover's is the only kept set on the shipping paths
        if (!sites.Contains("handover")) _failures.Add("the handover site made no kept set");
        foreach (var other in sites.Where(s => s != "handover")) _failures.Add($"a kept set other than the handover's exists: {other}");
        if (notSame > 0) _failures.Add($"{notSame} uses got a different wrapper (not reused)");
        if (notEmpty > 0) _failures.Add($"{notEmpty} times a kept collection was not empty after its call (not cleared)");
        if (wrongCount > 0) _failures.Add($"{wrongCount} meshes did not have their own vertex count (data carried between calls)");
        if (!controlRun)
        {
            if (KeptCollections.MadeLate > 0) _failures.Add($"{KeptCollections.MadeLate} kept sets made after startup, inside a site's call");
            if (VariantDisposal.DisposalsOutsideDrain > 0) _failures.Add($"{VariantDisposal.DisposalsOutsideDrain} Variant disposals ran inside a site's call (not deferred to the drain after the upload budget)");
            // the queue: exercised by the frame loop, every value queued disposed exactly once (none left to a
            // finalizer), no value started after its frame's slice was spent, each frame's disposals at most 1 ms,
            // never forced past its bound. A frame's wall time also holds stalls between values (preemption, a
            // collection) that the drain cannot bound: reported, not gated
            VariantDisposal.DrainAll("check end");
            if (VariantDisposal.Queue is not { } q) _failures.Add("no disposal queue");
            else
            {
                if (q.DrainFrames == 0) _failures.Add("the per-frame drain never ran");
                if (q.Count != 0 || q.Queued != q.Disposed) _failures.Add($"{q.Queued} values queued, {q.Disposed} disposed, {q.Count} still waiting");
                if (q.Errors > 0) _failures.Add($"{q.Errors} disposals failed: {q.LastError}");
                if (q.LateStarts > 0) _failures.Add($"{q.LateStarts} values started after their frame's drain slice was spent");
                if (q.MaxFrameValueMs > 1.0) _failures.Add($"a frame's disposals took {q.MaxFrameValueMs:F3} ms (over 1 ms)");
                if (q.ForcedDrains > 0) _failures.Add($"{q.ForcedDrains} drains forced past the bound (over {q.HardCapBytes >> 20} MiB held)");
            }
        }
        KeptCollections.FreshForTest = KeptCollections.SkipClearForTest = false;
        if (VariantDisposal.Queue is { } dqEnd) { dqEnd.InsideValueForTest = dqEnd.AfterValueForTest = null; dqEnd.IgnoreSliceForTest = false; }
        await Frames(3);
        _result["mode"] = "keptcollections";
        _result["keptcollections"] = new { control = spec, godotsharp = godotSharp, variant_disposal = VariantDisposal.Unavailable ?? "pinned", sites, not_same = notSame, not_empty = notEmpty, wrong_vertex_count = wrongCount, loads,
            prepared_at_frame = preparedAt, sets_at_start = setsAtStart, made_late = KeptCollections.MadeLate, setup_ms = Math.Round(KeptCollections.SetupMs, 3),
            disposals_outside_drain = VariantDisposal.DisposalsOutsideDrain, frames_run = framesRun, queue = VariantDisposal.Report() };
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>Keeps the calling thread busy for <paramref name="ms"/> (the drain controls' slowed disposal and stall).</summary>
    private static void SpinMs(double ms)
    {
        long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000.0);
        while (Stopwatch.GetTimestamp() < until) { }
    }

    /// <summary>The first surface's vertex count, read back from the engine (test only; the read-back dictionary is the
    /// engine's, held here and left to its finalizer, as in --tr-meshprep).</summary>
    private static int SurfaceVertexCount(Rid mesh)
    {
        var s = RenderingServer.MeshGetSurface(mesh, 0);
        int n = s.TryGetValue("vertex_count", out var v) ? v.AsInt32() : -1;
        GC.KeepAlive(s);
        return n;
    }

    /// <summary>
    /// The engine packing faces from arrays, exactly as the viewer's
    /// main-thread path did (engine axes: SL x, z, -y; every triangle reversed), every argument held through
    /// the native call.
    /// </summary>
    internal static Rid EnginePacked(IReadOnlyList<MeshFace> faces)
    {
        Rid mesh = Rids.MeshCreate("test mesh");
        foreach (var f in faces)
        {
            int n = f.Vertices;
            var v = new Vector3[n];
            var nr = new Vector3[n];
            var uv = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                v[i] = new Vector3(f.Positions[i * 3], f.Positions[i * 3 + 2], -f.Positions[i * 3 + 1]);
                nr[i] = new Vector3(f.Normals[i * 3], f.Normals[i * 3 + 2], -f.Normals[i * 3 + 1]);
                uv[i] = new Vector2(f.Uvs[i * 2], f.Uvs[i * 2 + 1]);
            }
            var winding = new int[f.Indices.Length];
            for (int t = 0; t + 2 < winding.Length; t += 3) { winding[t] = f.Indices[t]; winding[t + 1] = f.Indices[t + 2]; winding[t + 2] = f.Indices[t + 1]; }
            using var arrays = new global::Godot.Collections.Array();
            using var held = new HeldValues();   // the packed arrays held through the sets and the call
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = held.Add(v);
            arrays[(int)Mesh.ArrayType.Normal] = held.Add(nr);
            arrays[(int)Mesh.ArrayType.TexUV] = held.Add(uv);
            arrays[(int)Mesh.ArrayType.Index] = held.Add(winding);
            AddPacked(mesh, arrays);
            GC.KeepAlive(held);
        }
        return mesh;
    }

    /// <summary>A body part as the engine packs it from arrays (engine axes and winding, UVs, four bones and four weights a
    /// vertex: the part's two bones and weights, then bone 0 at weight 0): the reference the avatar layer's prepared skinned
    /// surface is checked against (--tr-avatarprep).</summary>
    internal static Rid EnginePackedSkinned(Viewer.Core.Avatars.BodyPart part)
    {
        int n = part.Mesh.VertexCount;
        var v = new Vector3[n];
        var nr = new Vector3[n];
        var uv = new Vector2[n];
        var bones = new int[n * 4];
        var weights = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            var p = part.Positions[i];
            var q = part.Normals[i];
            v[i] = new Vector3(p.X, p.Z, -p.Y);
            nr[i] = new Vector3(q.X, q.Z, -q.Y);
            uv[i] = new Vector2(part.Mesh.Uvs[i].X, part.Mesh.Uvs[i].Y);
            bones[i * 4] = part.BoneA[i]; bones[i * 4 + 1] = part.BoneB[i];
            weights[i * 4] = 1f - part.WeightB[i]; weights[i * 4 + 1] = part.WeightB[i];
        }
        var src = part.Mesh.Indices;
        var winding = new int[src.Length];
        for (int t = 0; t + 2 < winding.Length; t += 3) { winding[t] = src[t]; winding[t + 1] = src[t + 2]; winding[t + 2] = src[t + 1]; }
        return EnginePacked(held =>
        {
            var arrays = new global::Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = held.Add(v);
            arrays[(int)Mesh.ArrayType.Normal] = held.Add(nr);
            arrays[(int)Mesh.ArrayType.TexUV] = held.Add(uv);
            arrays[(int)Mesh.ArrayType.Bones] = held.Add(bones);
            arrays[(int)Mesh.ArrayType.Weights] = held.Add(weights);
            arrays[(int)Mesh.ArrayType.Index] = held.Add(winding);
            return arrays;
        });
    }

    /// <summary>One surface the engine packs from the arrays <paramref name="make"/> makes (disposed
    /// here), their packed arrays held through the call.</summary>
    private static Rid EnginePacked(Func<HeldValues, global::Godot.Collections.Array> make)
    {
        using var held = new HeldValues();
        using var arrays = make(held);
        Rid mesh = Rids.MeshCreate("test mesh");
        AddPacked(mesh, arrays);
        GC.KeepAlive(held);
        return mesh;
    }

    private static void AddPacked(Rid mesh, global::Godot.Collections.Array arrays)
    {
        using var blendShapes = new global::Godot.Collections.Array();   // held: the binding's own would not be
        using var lods = new global::Godot.Collections.Dictionary();
        RenderingServer.MeshAddSurfaceFromArrays(mesh, RenderingServer.PrimitiveType.Triangles, arrays, blendShapes, lods);
    }

    /// <summary>The water plane's arrays as EnvironmentLayer made them.</summary>
    private static global::Godot.Collections.Array WaterArrays(HeldValues held)
    {
        float h = EnvironmentLayer.WaterHalfSize;
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = held.Add(new Vector3[] { new(-h, 0, -h), new(h, 0, -h), new(h, 0, h), new(-h, 0, h) });
        arrays[(int)Mesh.ArrayType.Normal] = held.Add(new Vector3[] { Vector3.Up, Vector3.Up, Vector3.Up, Vector3.Up });
        arrays[(int)Mesh.ArrayType.Index] = held.Add(new[] { 0, 1, 2, 0, 2, 3 });
        return arrays;
    }

    /// <summary>A synthetic face of <paramref name="vertices"/> vertices (a strip of triangles on a wavy sheet, normals
    /// pointing every way including below the horizon, UVs across 0..1 and beyond), from a seed.</summary>
    private static MeshFace SyntheticFace(int vertices, int seed)
    {
        var rnd = new Random(seed);
        var pos = new float[vertices * 3];
        var nor = new float[vertices * 3];
        var uv = new float[vertices * 2];
        for (int i = 0; i < vertices; i++)
        {
            pos[i * 3] = (float)(rnd.NextDouble() - 0.5); pos[i * 3 + 1] = (float)(rnd.NextDouble() - 0.5); pos[i * 3 + 2] = (float)(rnd.NextDouble() - 0.5);
            var n = new System.Numerics.Vector3((float)(rnd.NextDouble() * 2 - 1), (float)(rnd.NextDouble() * 2 - 1), (float)(rnd.NextDouble() * 2 - 1));
            n = n.LengthSquared() < 1e-6f ? System.Numerics.Vector3.UnitZ : System.Numerics.Vector3.Normalize(n);
            nor[i * 3] = n.X; nor[i * 3 + 1] = n.Y; nor[i * 3 + 2] = n.Z;
            uv[i * 2] = (float)(rnd.NextDouble() * 2 - 0.5); uv[i * 2 + 1] = (float)(rnd.NextDouble() * 2 - 0.5);
        }
        var idx = new int[(vertices - 2) * 3];
        for (int t = 0; t < vertices - 2; t++) { idx[t * 3] = t; idx[t * 3 + 1] = t + 1; idx[t * 3 + 2] = t + 2; }
        return new MeshFace(pos, nor, uv, idx);
    }

    private bool _meshMainThread, _prepAlter;

    /// <summary>
    /// Through the layer, with the mesh zoo; the preparations wait on a gate the test holds, so every in-flight state
    /// is certain. (1) First builds held: no mesh object is drawn half-made (they appear once their data is ready), and a
    /// region change leaves nothing in flight or held; released afterwards, the old preparations are thrown away and
    /// nothing is built. (2) Near, settled: every LOD build was a handover only (prepared off the main thread, no main-thread
    /// preparation). (3) Far with the preparations held: every mesh object still drawn, on its previous LOD, while its new
    /// one is prepared. (4) Back near before they are ready, then released: the far data nobody wants is let go, nothing is
    /// built, the image is the first near image. (5) Far, released: the objects switch. (6) A region reset frees everything.
    /// --tr-mesh-main-thread (today's path: every build on the main thread when first drawn) must fail (1)-(4).
    /// </summary>
    private async Task<object> MeshPrepLifecycle(Loaded loaded)
    {
        var rig = MakeRig();
        var view = MakeView("server", rig);
        var scenario = rig.Scenario;
        int meshObjects = loaded.World.Snapshot().Objects.Values.Count(o => o.Shape?.SculptType == PrimShape.SculptTypeMesh && o.AttachmentPoint is null);
        var steps = new List<object>();
        TaskCompletionSource? hold = null;
        Task Gate(CancellationToken t) => hold is { } h ? h.Task.WaitAsync(t) : Task.CompletedTask;
        async Task Settled(MeshLodPreparer p)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while ((p.InFlight > 0 || p.Running > 0) && DateTime.UtcNow < until) await Task.Delay(5);
        }

        // (1) first builds held, then a region change
        var first = new PrimLayer(scenario, loaded.World, loaded.Meshes, null, _budget) { SimulatedFps = 60, MeshPrepareOnMainThread = _meshMainThread };
        var p1 = first.MeshPreparer!;
        p1.Gate = Gate;
        hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Stage.MeshCamera(rig.Camera, "near");
        first.Camera = rig.Camera.GlobalPosition;
        var until1 = DateTime.UtcNow.AddSeconds(10);
        do { first.MarkAll(); while (first.Step()) { } await Task.Delay(5); } while ((loaded.Meshes!.Pending > 0 || first.MeshesWaiting == 0) && first.MeshLodsHeld == 0 && DateTime.UtcNow < until1);
        await Frames(2);
        steps.Add(new { at = "first builds held", drawn = first.MeshObjects, lods_held = first.MeshLodsHeld, in_flight = p1.InFlight, waiting = first.MeshesWaiting });
        if (p1.InFlight == 0) _failures.Add($"first builds: nothing was being prepared off the main thread ({first.MeshLodsHeld} LODs built on it)");
        if (first.MeshObjects != 0) _failures.Add($"first builds: {first.MeshObjects} mesh objects drawn before their data was ready");
        long discarded0 = p1.Discarded;
        first.Clear();   // what a region change does
        int inFlightAfter = p1.InFlight, heldAfter = p1.Held;
        hold.SetResult();
        hold = null;
        await Settled(p1);
        while (first.Step()) { }
        steps.Add(new { at = "region change, then released", in_flight = inFlightAfter, held = heldAfter, running = p1.Running, discarded = p1.Discarded - discarded0, held_bytes = p1.HeldBytes, lods_held = first.MeshLodsHeld, live = first.LiveResources });
        if (inFlightAfter != 0 || heldAfter != 0) _failures.Add($"region change: {inFlightAfter} preparations in flight, {heldAfter} held");
        if (!_meshMainThread && p1.Discarded - discarded0 == 0) _failures.Add("region change: the preparations running were not thrown away");
        if (p1.Running != 0 || p1.Held != 0 || p1.HeldBytes != 0 || first.MeshLodsHeld != 0 || first.LiveResources != 0)
            _failures.Add($"after the region change: {p1.Running} running, {p1.Held} held ({p1.HeldBytes} bytes), {first.MeshLodsHeld} LODs, {first.LiveResources} resources");

        // (2) near, settled, through the presenter
        var prims = new PrimLayer(scenario, loaded.World, loaded.Meshes, null, _budget) { SimulatedFps = 60, MeshPrepareOnMainThread = _meshMainThread };
        var prep = prims.MeshPreparer!;
        prep.Gate = Gate;
        var presenter = new WorldPresenter(loaded.World, view, prims: prims, environment: new EnvironmentLayer(scenario, rig.Environment, rig.Sun)) { DayFractionOverride = 0.3 };
        void Cam(string preset) { Stage.MeshCamera(rig.Camera, preset); prims.Camera = rig.Camera.GlobalPosition; prims.UpdateLods(); }
        Image Shot() { var img = rig.Texture.GetImage(); img.Convert(Image.Format.Rgba8); return img; }
        // each frame as the viewer's: the coordinator runs at least one unit of work even with nothing dirty (arrivals)
        async Task Run(int frames) { for (int i = 0; i < frames; i++) { prims.UpdateLods(); presenter.Flush(); while (prims.Step()) { } if (i % 10 == 0) await Frames(1); } await Frames(2); }
        Cam("near");
        await SettleMeshes(presenter, prims, loaded.Meshes);
        await Run(4);
        var near = Shot();
        var nearLods = prims.MeshLods.ToDictionary(kv => kv.Key, kv => kv.Value);
        var builds = prims.MeshBuilds.ToList();
        steps.Add(new { at = "near", drawn = prims.MeshObjects, lods_held = prims.MeshLodsHeld, builds = builds.Count, prepared_off_thread = builds.Count(b => b.Prepared), main_thread_prepare_ms = Math.Round(builds.Sum(b => b.PrepareMs), 3), longest_ms = builds.Count == 0 ? 0 : Math.Round(builds.Max(b => b.Ms), 3) });
        if (prims.MeshObjects != meshObjects) _failures.Add($"near: {prims.MeshObjects} of {meshObjects} mesh objects drawn");
        if (builds.Count == 0 || builds.Any(b => !b.Prepared || b.PrepareMs > 0))
            _failures.Add($"near: {builds.Count(b => !b.Prepared)} of {builds.Count} mesh LOD builds prepared their data on the main thread ({builds.Sum(b => b.PrepareMs):F3} ms): not only the handover");

        // (3) far, the preparations held
        hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Cam("far");
        await Run(30);
        bool lodsUnchanged = prims.MeshLods.Count == nearLods.Count && prims.MeshLods.All(kv => nearLods.TryGetValue(kv.Key, out var l) && l == kv.Value);
        steps.Add(new { at = "far, preparations held", drawn = prims.MeshObjects, on_previous_lod = prims.MeshObjectsOnPreviousLod, in_flight = prep.InFlight, lods_unchanged = lodsUnchanged, lods_held = prims.MeshLodsHeld });
        if (prep.InFlight == 0) _failures.Add("far: no LOD was being prepared off the main thread (the switch was built on it)");
        if (prims.MeshObjectsOnPreviousLod == 0) _failures.Add("far: no object drew its previous LOD while its new one was prepared");
        if (prims.MeshObjects != meshObjects) _failures.Add($"far while preparing: {prims.MeshObjects} of {meshObjects} mesh objects drawn (the others drew nothing)");
        if (!lodsUnchanged) _failures.Add("far while preparing: an object changed LOD before its data was ready");

        // (4) back near before the data is ready, then released
        long dropped0 = prep.Dropped;
        int held0 = prims.MeshLodsHeld;
        Cam("near");
        await Run(4);
        hold.SetResult();
        hold = null;
        await Settled(prep);
        await Run(10);
        var again = Shot();
        bool same = near.GetData().AsSpan().SequenceEqual(again.GetData());
        steps.Add(new { at = "near again, released", dropped = prep.Dropped - dropped0, held = prep.Held, held_bytes = prep.HeldBytes, lods_held = prims.MeshLodsHeld, same_image = same, waiting = prims.MeshesWaiting });
        if (prep.Dropped - dropped0 == 0) _failures.Add("near again: the far LODs' data nobody wanted was not let go");
        if (prep.Held != 0 || prep.HeldBytes != 0) _failures.Add($"near again: {prep.Held} prepared LODs still held ({prep.HeldBytes} bytes)");
        if (prims.MeshLodsHeld != held0) _failures.Add($"near again: {prims.MeshLodsHeld - held0} LODs built that nobody wanted");
        if (!same) _failures.Add("near again: the image differs from the first near image");
        if (prims.MeshesWaiting != 0) _failures.Add($"near again: {prims.MeshesWaiting} objects still waiting");

        // (5) far, released: the objects switch once their data is ready
        Cam("far");
        await SettleMeshes(presenter, prims, loaded.Meshes);
        await Run(4);
        bool switched = prims.MeshLods.Any(kv => nearLods.TryGetValue(kv.Key, out var l) && l != kv.Value);
        steps.Add(new { at = "far, released", drawn = prims.MeshObjects, switched, held = prep.Held, lods_held = prims.MeshLodsHeld });
        if (!switched || prims.MeshObjects != meshObjects || prep.Held != 0) _failures.Add($"far: switched {switched}, {prims.MeshObjects} of {meshObjects} drawn, {prep.Held} held");

        // (6) a region reset frees everything
        presenter.Apply(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(loaded.World.Epoch + 1, 0)));
        await Settled(prep);
        steps.Add(new { at = "region reset", live = prims.LiveResources, lods_held = prims.MeshLodsHeld, in_flight = prep.InFlight, held = prep.Held, running = prep.Running });
        if (prims.LiveResources != 0 || prims.MeshLodsHeld != 0 || prep.InFlight != 0 || prep.Held != 0 || prep.Running != 0)
            _failures.Add($"region reset: {prims.LiveResources} resources, {prims.MeshLodsHeld} LODs, {prep.InFlight} in flight, {prep.Held} held, {prep.Running} running");
        presenter.Dispose();
        view.Clear();
        return new { mesh_objects = meshObjects, steps, dropped = prep.Dropped, discarded = p1.Discarded + prep.Discarded, failed = p1.Failed + prep.Failed, peak_held_bytes = prep.PeakHeldBytes };
    }

    /// <summary>A face whose every other normal is zero (a degenerate normal in a decoded mesh).</summary>
    private static MeshFace ZeroNormalsFace()
    {
        var f = SyntheticFace(12, 5);
        for (int i = 0; i < 12; i += 2) { f.Normals[i * 3] = 0; f.Normals[i * 3 + 1] = 0; f.Normals[i * 3 + 2] = 0; }
        return f;
    }

    /// <summary>A nearly flat face (spread 3e-6 on one engine axis, flat on another): the box's least size.</summary>
    private static MeshFace TinySpreadFace()
    {
        var f = SyntheticFace(30, 4);
        for (int i = 0; i < 30; i++) f.Positions[i * 3 + 2] = 0.25f + (i % 3) * 1.5e-6f;   // SL z (engine y) within 3e-6
        for (int i = 0; i < 30; i++) f.Positions[i * 3 + 1] = 0.125f;                        // SL y (engine z) flat
        return f;
    }

    /// <summary>Normals exactly on each axis, both ways, and on the octahedron's edges and corners.</summary>
    private static MeshFace AxisNormalsFace()
    {
        float h = MathF.Sqrt(0.5f), c = 1f / MathF.Sqrt(3f);
        float[][] ns =
        [
            [1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1],
            [h, h, 0], [-h, h, 0], [h, -h, 0], [-h, -h, 0], [h, 0, h], [h, 0, -h], [-h, 0, -h], [0, h, -h], [0, -h, -h],
            [c, c, c], [-c, -c, -c], [c, -c, -c], [-c, c, -c], [-c, -c, c],
        ];
        int n = ns.Length;
        var pos = new float[n * 3];
        var nor = new float[n * 3];
        var uv = new float[n * 2];
        for (int i = 0; i < n; i++)
        {
            pos[i * 3] = i * 0.01f; pos[i * 3 + 1] = -i * 0.02f; pos[i * 3 + 2] = (i % 3) * 0.1f;
            nor[i * 3] = ns[i][0]; nor[i * 3 + 1] = ns[i][1]; nor[i * 3 + 2] = ns[i][2];
            uv[i * 2] = i / (float)n; uv[i * 2 + 1] = 1 - i / (float)n;
        }
        var idx = new int[(n - 2) * 3];
        for (int t = 0; t < n - 2; t++) { idx[t * 3] = t; idx[t * 3 + 1] = t + 1; idx[t * 3 + 2] = t + 2; }
        return new MeshFace(pos, nor, uv, idx);
    }

    /// <summary>
    /// A texture let go from memory comes back from the content
    /// store, not the network. The zoo's textures are served by the loopback texture server and fetched in part
    /// (PartialTextureFetcher, as live) with an encrypted content store in the run's output folder, a shared asset store
    /// bounded at 256 bytes and a partial-fetcher hold of 256 bytes (every zoo file is larger, so neither keeps a copy).
    /// Deterministic (the layer's clock advances 1/60 s per frame). Near image; then the texture budget drops to 1 byte, so
    /// the residency sends every texture to its placeholder (fully evicted, its decoded copies released: not a step down);
    /// then the budget comes back and the view settles again. Pass: every texture was evicted and reloaded, content-store
    /// reads &gt; 0, network requests after the eviction 0, and the image identical to the first. --tr-readback=nostore is the
    /// negative control: the same check without the content store must see network requests (it fails, exit 1).
    /// Meshes are not covered: the mesh source keeps its decoded geometry for the session and never asks again.
    /// </summary>
    private async Task<int> ReadbackCheck(string fixture, bool contentStore)
    {
        if (!fixture.EndsWith(".texzoo.json", StringComparison.OrdinalIgnoreCase) || !File.Exists(fixture))
            throw new ArgumentException("--tr-readback needs the texture zoo (--tr-fixture=...texzoo.json)");
        var (zooPrims, images, meshFiles) = TextureZoo.Parse(File.ReadAllText(fixture));
        string dir = Path.GetDirectoryName(fixture)!;
        var z = FromZoo(zooPrims, "texture zoo " + Path.GetFileName(fixture), "readback");
        var meshes = MeshSourceFor(dir, meshFiles);
        const long Small = 256;
        var server = new LoopbackTextureServer(id => images.TryGetValue(id, out var f) ? File.ReadAllBytes(Path.Combine(dir, f)) : null);
        var http = new Viewer.Core.Net.TextureHttp(new Viewer.Core.Net.OutboundPolicy(new Viewer.Core.Net.OutboundOptions()), new CoreLog(new DiscardSink(), "test-render"));
        int cap = Viewer.Core.Imaging.J2kLimits.Default.MaxBytes;
        long wholeReads = 0;
        ContentView? store = null;
        Func<Guid, CancellationToken, Task<AssetFetchResult>> whole = (id, ct) => http.WholeAsync(server.UriFor(id), cap, ct);
        if (contentStore)
        {
            var root = Path.Combine(_out, _tag + "-content-" + Guid.NewGuid().ToString("N"));   // the run's own output folder (ADR-019)
            var view0 = store = new ContentStore(new ContentStoreOptions { Root = root }).View("texture-server", "tr");
            var fromNetwork = whole;
            whole = async (id, ct) =>
            {
                if (view0.TryGet("texture", id, out var hit)) { Interlocked.Increment(ref wholeReads); return AssetFetchResult.Ok(hit); }
                var r = await fromNetwork(id, ct).ConfigureAwait(false);
                if (r.Status == AssetFetchStatus.Ok) view0.Put("texture", id, r.Bytes.Span);
                return r;
            };
        }
        var fetcher = new PartialTextureFetcher((id, from, to, c, ct) => http.RangeAsync(server.UriFor(id), from, to, c, ct), whole, store, maxHeldBytes: Small);
        var shared = new SharedAssetStore(Small);
        var textures = new TextureSource(new SessionAssets(shared, _ => true, whole, fetchLevel: fetcher.FetchLevelAsync));
        var rig = MakeRig();
        var view = MakeView("server", rig);
        var prims = new PrimLayer(rig.Scenario, z.World, meshes, textures, _budget) { SimulatedFps = 60 };
        Stage.ZooCamera(rig.Camera);
        prims.Camera = rig.Camera.GlobalPosition;
        prims.ResidencyView = PrimLayer.ViewOf(rig.Camera, Width, Height);
        var presenter = new WorldPresenter(z.World, view, prims: prims, terrainTextures: textures,
            environment: new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun)) { DayFractionOverride = 0.3 };
        Image Shot() { var img = rig.Texture.GetImage(); img.Convert(Image.Format.Rgba8); return img; }
        long Reads() => Interlocked.Read(ref wholeReads) + fetcher.Stats.FromStore;
        var res = prims.Residency!;

        await SettleMeshes(presenter, prims, meshes, textures);
        await Frames(8);
        var before = Shot();
        int texturesUsed = res.Textures;
        long requests0 = server.Requests, reads0 = Reads(), evictions0 = res.Evictions, reloads0 = res.Reloads;
        var first = new { textures = texturesUsed, resident = res.Resident, gpu_textures = prims.GpuTextures, server_requests = requests0, content_store_reads = reads0, fetcher = fetcher.Stats };
        if (texturesUsed == 0 || res.Resident != texturesUsed) _failures.Add($"first view: {res.Resident} of {texturesUsed} textures resident");

        // every texture fully evicted: the budget drops to 1 byte (steps down first, then to the placeholder)
        long budget = res.BudgetBytes;
        res.SetBudget(1);
        for (int i = 0; i < 120 && (res.Resident > 0 || res.Loading > 0); i++) { presenter.Flush(); if (i % 15 == 0) await Frames(1); }
        await Frames(2);
        int evicted = res.All.Count(id => res.StateOf(id) == Viewer.Core.Residency.ResidencyState.Evicted);
        var gone = new { evicted, resident = res.Resident, loading = res.Loading, evictions = res.Evictions - evictions0, fetcher_held_bytes = fetcher.HeldBytes, shared_store = shared.Stats };
        if (evicted != texturesUsed) _failures.Add($"eviction: {evicted} of {texturesUsed} textures on their placeholder ({res.Resident} resident, {res.Loading} loading)");

        // back: the budget returns and the view settles; what was let go is read back
        res.SetBudget(budget);
        await SettleMeshes(presenter, prims, meshes, textures);
        Stage.ZooCamera(rig.Camera);
        await Frames(8);
        var after = Shot();
        long network = server.Requests - requests0, reads = Reads() - reads0, reloads = res.Reloads - reloads0;
        bool same = before.GetData().AsSpan().SequenceEqual(after.GetData());
        Directory.CreateDirectory(_out);
        before.SavePng(Path.Combine(_out, _tag + "-before.png"));
        after.SavePng(Path.Combine(_out, _tag + "-after.png"));
        if (res.Resident != texturesUsed) _failures.Add($"back: {res.Resident} of {texturesUsed} textures resident");
        if (reloads < texturesUsed) _failures.Add($"back: {reloads} reloads for {texturesUsed} textures evicted");
        if (!same) _failures.Add("back: the image differs from the first image");
        if (network > 0) _failures.Add($"back: {network} network requests after the eviction (a texture let go was fetched again from the network)");
        if (contentStore && reads == 0) _failures.Add("back: no content-store read (nothing was read back)");
        var backReport = new
        {
            resident = res.Resident, reloads, content_store_reads = reads, whole_reads = Interlocked.Read(ref wholeReads), partial_fetcher_reads = fetcher.Stats.FromStore,
            network_requests = network, fetcher = fetcher.Stats, shared_store = shared.Stats,
        };
        presenter.Dispose();
        view.Clear();
        textures.Dispose();
        meshes.Dispose();
        z.Dispose();
        http.Dispose();
        server.Dispose();

        _result["mode"] = "readback";
        _result["readback"] = new
        {
            content_store = contentStore, shared_store_bound_bytes = Small, fetcher_hold_bytes = Small, first, evicted = gone, back = backReport,
            images_identical = same,
        };
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// The main-thread cost of one upload unit, by size and stage, for three
    /// paths on this backend: "mips_on_main" (mipmaps generated on the main thread), "image_on_main" (the mip chain comes
    /// with the decode; the engine image and the average colour are made on the main thread) and "now" (image and average
    /// made on the decode's background task; the main thread creates the GPU texture and swaps it in). "now_first" is a
    /// first upload (ImageTexture.CreateFromImage, a new slot) from a prepared image. Stages: image (Image.CreateFromData),
    /// mipmaps (Image.GenerateMipmaps), average (PlaceholderColour.Average), create (RenderingServer.Texture2DCreate or
    /// ImageTexture.CreateFromImage), replace (RenderingServer.TextureReplace), dispose (the image). frame_after: the next
    /// frame's wall time (work the engine defers to the frame shows there), against idle frames with no upload. The
    /// background-task costs of "now" (mip chain, image, average) are reported separately. Seeded pixels; p50/p95/max.
    /// </summary>
    private async Task<int> UploadBench(int repeats)
    {
        var rig = MakeRig();
        await Frames(4);
        var sizes = new[] { (256, 256), (512, 512), (1024, 1024), (2048, 2048), (2048, 1024) };
        var rnd = new Random(12);
        var idle = new List<double>();
        var frameClock = new System.Diagnostics.Stopwatch();
        for (int i = 0; i < repeats; i++) { frameClock.Restart(); await Frames(1); idle.Add(frameClock.Elapsed.TotalMilliseconds); }
        var outSizes = new List<object>();
        double worst1024 = 0;
        var clock = new System.Diagnostics.Stopwatch();
        foreach (var (w, h) in sizes)
        {
            var rgba = new byte[w * h * 4];
            rnd.NextBytes(rgba);
            var mips = Viewer.Core.Imaging.MipChain.Build(w, h, rgba);
            var slotImg = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            var slot = RenderingServer.Texture2DCreate(slotImg);
            slotImg.Dispose();
            var paths = new Dictionary<string, object>();
            foreach (var path in new[] { "mips_on_main", "image_on_main", "now", "now_first" })
            {
                var st = new Dictionary<string, List<double>> { ["image"] = new(), ["mipmaps"] = new(), ["average"] = new(), ["create"] = new(), ["replace"] = new(), ["dispose"] = new(), ["unit"] = new(), ["frame_after"] = new() };
                var bg = new Dictionary<string, List<double>> { ["mip_chain"] = new(), ["image"] = new(), ["average"] = new() };
                for (int k = 0; k < repeats; k++)
                {
                    Image img;
                    PreparedUpload? prepared = null;
                    double tImage = 0, tMips = 0, tAvg = 0;
                    if (path.StartsWith("now"))
                    {
                        // the background task's share, measured on a worker thread (as TextureSource runs it)
                        var cost = await Task.Run(() =>
                        {
                            var c = System.Diagnostics.Stopwatch.StartNew();
                            var chain = Viewer.Core.Imaging.MipChain.Build(w, h, rgba);
                            double chainMs = c.Elapsed.TotalMilliseconds;
                            c.Restart();
                            var im = Image.CreateFromData(w, h, true, Image.Format.Rgba8, chain);
                            double imMs = c.Elapsed.TotalMilliseconds;
                            c.Restart();
                            Viewer.Core.Residency.PlaceholderColour.Average(rgba);
                            double avgMs = c.Elapsed.TotalMilliseconds;
                            im.Dispose();
                            return (chainMs, imMs, avgMs);
                        });
                        bg["mip_chain"].Add(cost.chainMs); bg["image"].Add(cost.imMs); bg["average"].Add(cost.avgMs);
                        var decoded = new Viewer.Core.Imaging.DecodedTexture(w, h, rgba, Viewer.Core.Imaging.AlphaClass.Opaque, 4) { Mips = mips };
                        prepared = await Task.Run(() => PreparedUpload.Make(decoded));
                    }
                    clock.Restart();
                    if (path == "mips_on_main")
                    {
                        img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba); tImage = clock.Elapsed.TotalMilliseconds; clock.Restart();
                        img.GenerateMipmaps(); tMips = clock.Elapsed.TotalMilliseconds; clock.Restart();
                        Viewer.Core.Residency.PlaceholderColour.Average(rgba); tAvg = clock.Elapsed.TotalMilliseconds; clock.Restart();
                    }
                    else if (path == "part1")
                    {
                        img = Image.CreateFromData(w, h, true, Image.Format.Rgba8, mips); tImage = clock.Elapsed.TotalMilliseconds; clock.Restart();
                        Viewer.Core.Residency.PlaceholderColour.Average(rgba); tAvg = clock.Elapsed.TotalMilliseconds; clock.Restart();
                    }
                    else img = prepared!.Image;
                    double tCreate, tReplace = 0, tDispose;
                    if (path == "now_first")
                    {
                        var tex = ImageTexture.CreateFromImage(img); tCreate = clock.Elapsed.TotalMilliseconds; clock.Restart();
                        prepared!.Dispose(); tDispose = clock.Elapsed.TotalMilliseconds;
                        st["create"].Add(tCreate); st["dispose"].Add(tDispose); st["unit"].Add(tCreate + tDispose);
                        frameClock.Restart(); await Frames(1); st["frame_after"].Add(frameClock.Elapsed.TotalMilliseconds);
                        tex.Dispose();
                        continue;
                    }
                    var full = RenderingServer.Texture2DCreate(img); tCreate = clock.Elapsed.TotalMilliseconds; clock.Restart();
                    RenderingServer.TextureReplace(slot, full); tReplace = clock.Elapsed.TotalMilliseconds; clock.Restart();
                    if (prepared is not null) prepared.Dispose(); else img.Dispose();
                    tDispose = clock.Elapsed.TotalMilliseconds;
                    st["image"].Add(tImage); st["mipmaps"].Add(tMips); st["average"].Add(tAvg); st["create"].Add(tCreate); st["replace"].Add(tReplace); st["dispose"].Add(tDispose);
                    st["unit"].Add(tImage + tMips + tAvg + tCreate + tReplace + tDispose);
                    frameClock.Restart(); await Frames(1); st["frame_after"].Add(frameClock.Elapsed.TotalMilliseconds);
                }
                if (path == "now" && w == 1024 && h == 1024) worst1024 = st["unit"].Max();
                paths[path] = new
                {
                    main_thread = st.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => Dist(kv.Value)),
                    background = bg["image"].Count > 0 ? bg.ToDictionary(kv => kv.Key, kv => Dist(kv.Value)) : null,
                };
            }
            Rids.Free(slot);
            outSizes.Add(new { w, h, paths });
        }
        _result["mode"] = "uploadbench";
        _result["uploadbench"] = new { repeats, idle_frame_ms = Dist(idle), sizes = outSizes, now_1024_worst_unit_ms = Math.Round(worst1024, 3), aim_ms = 4.0 };
        rig.Viewport.QueueFree();
        return _failures.Count == 0 ? 0 : 1;
    }

    // ----------------: end-to-end texture orientation ----------------

    private async Task<int> Orientation(string fixture)
    {
        var (zoo, images, meshAssets) = OrientationZoo.Parse(File.ReadAllText(fixture));
        string dir = Path.GetDirectoryName(fixture)!;
        // the sculpt's map comes from the sculpt zoo's lossless maps
        var allImages = images.Concat([KeyValuePair.Create(SculptZoo.SphereMap, "../sculpts/" + SculptZoo.Images[SculptZoo.SphereMap])]).ToDictionary(kv => kv.Key, kv => kv.Value);
        var z = FromZoo(zoo, "orientation zoo " + Path.GetFileName(fixture), TerrainFixture.Sha256(File.ReadAllBytes(fixture)));
        using var meshes = MeshSourceFor(dir, meshAssets);
        using var textures = TextureSourceFor(dir, allImages);
        var rig = MakeRig();
        var view = MakeView("server", rig);
        var prims = new PrimLayer(rig.Scenario, z.World, meshes, textures, _budget) { SimulatedFps = 60 };   // deterministic decisions for goldens and lifecycle checks
        var o = OrientationZoo.Origin;
        Vector3 E(System.Numerics.Vector3 sl) => new(sl.X, sl.Z, -sl.Y);
        prims.Camera = E(o) + new Vector3(0, 5, 0);
        var presenter = new WorldPresenter(z.World, view, prims: prims);
        await SettleMeshes(presenter, prims, meshes, textures);
        var cam = rig.Camera;
        cam.Projection = Camera3D.ProjectionType.Orthogonal;
        cam.Size = 11.25f;
        cam.Near = 0.1f;
        cam.Far = 100f;
        var results = new List<object>();
        var positions = z.World.Snapshot().Objects.Values.ToDictionary(x => x.Id.LocalId, x => x.Position);
        foreach (var (name, pos, up) in new[] { ("south", new System.Numerics.Vector3(0, -20, 0), new Vector3(0, 1, 0)), ("above", new System.Numerics.Vector3(0, 0, 20), new Vector3(0, 0, -1)), ("below", new System.Numerics.Vector3(0, 0, -20), new Vector3(0, 0, 1)) })
        {
            cam.Position = E(o + pos);
            cam.LookAt(E(o), up);
            await Frames(4);
            var img = rig.Texture.GetImage();
            img.Convert(Image.Format.Rgba8);
            Directory.CreateDirectory(_out);
            img.SavePng(Path.Combine(_out, $"{_tag}-{name}.png"));
            var px = img.GetData();
            foreach (var c in OrientationZoo.Checks().Where(c => c.View == name))
            {
                // the object's screen footprint (0.4 m around its centre), every surface pixel voting (majority 70 %)
                var sp = cam.UnprojectPosition(E(positions[c.LocalId]));
                int half = (int)(0.4f * Height / cam.Size);
                var f = OrientationZoo.Field(px, Width, Height, (int)MathF.Round(sp.X), (int)MathF.Round(sp.Y), half);
                bool ok = f.Samples >= 20 && f.UnmirroredShare >= 0.7 && (!c.URight || f.URightShare >= 0.7) && (!c.VUp || f.VUpShare >= 0.7);
                string found = $"unmirrored {f.UnmirroredShare:P0}, u right {f.URightShare:P0}, v up {f.VUpShare:P0} of {f.Samples} px";
                results.Add(new { view = name, check = c.Name, expected = (c.URight ? "u right, " : "") + (c.VUp ? "v up, " : "") + "not mirrored", found, pass = ok });
                if (!ok) _failures.Add($"orientation {name}: {c.Name}: expected {(c.URight ? "u right, " : "")}{(c.VUp ? "v up, " : "")}not mirrored; found {found}");
            }
            if (name == "above")
            {
                // lighting: the lit sphere's side towards the sun is brighter than the side away (catches a winding regression)
                var sun = Stage.Sun();
                var toward = -(-sun.Basis.Z) with { Y = 0 };
                sun.Free();
                toward = toward.Normalized();
                var centre = E(positions[7214]);
                float Lum(Vector3 p) { var s = cam.UnprojectPosition(p); int i = ((int)s.Y * Width + (int)s.X) * 4; return (px[i] + px[i + 1] + px[i + 2]) / 3f; }
                float lit = Lum(centre + toward * 0.5f), dark = Lum(centre - toward * 0.5f);
                results.Add(new { view = name, check = "lighting: lit sphere, sun side vs far side", sun_side = lit, far_side = dark, pass = lit > dark + 10 });
                if (!(lit > dark + 10)) _failures.Add($"lighting: sun side {lit:0} not brighter than the far side {dark:0}");
            }
        }
        _result["mode"] = "orientation";
        _result["orientation"] = results;
        _result["prims"] = new Dictionary<string, object?> { ["instances"] = prims.Instances, ["objects"] = zoo.Count, ["mesh_objects"] = prims.MeshObjects, ["sculpt_objects"] = prims.SculptObjects, ["textures_waiting"] = prims.TexturesWaiting };
        if (prims.Instances != zoo.Count) _failures.Add($"orientation zoo: {prims.Instances} of {zoo.Count} objects presented");
        presenter.Dispose();
        view.Clear();
        z.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }

    // ---------------- lifecycle (L3: no leaked RIDs across region changes) ----------------

    /// <summary>One region with the lifecycle's avatars, drawn and freed (no textures): what the engine keeps for the process
    /// after drawing skinned bodies and name labels, made before the lifecycle's reference sample.</summary>
    private async Task AvatarOneTimeState(Rig rig, ITerrainView view)
    {
        var b = Viewer.Core.Avatars.AvatarBody.Load(_lifecycleCharacter!);
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("one-time", 1000, 1000, 256, 256, true));
        using var animations = AnimationSourceFor(_lifecycleAssets);
        var avatars = new AvatarPresenter(rig.Scenario, world, LiveSelf,
            Task.FromResult<AvatarPresenter.LoadedBody?>(new(b, AvatarLayer.Prepare(b))), animations, null, null, () => 0);
        var presenter = new WorldPresenter(world, view, avatars: avatars);
        await AvatarsArriveAndOneLeaves(world, presenter, avatars, animations, null, "one-time state");
        await Frames(3);
        presenter.Dispose();
        world.Dispose();
        await Frames(3);
    }

    /// <summary>Name labels anywhere under <paramref name="root"/>.</summary>
    private static int LabelsUnder(Node root)
    {
        int n = 0;
        foreach (var c in root.GetChildren())
        {
            if (c is Label3D) n++;
            n += LabelsUnder(c);
        }
        return n;
    }

    /// <summary>The lifecycle's avatars: three arrive (the user's own and two others; bakes from the texture zoo's images
    /// when there is a texture source, stand and walk), are drawn, and one leaves; what it had is freed at once.</summary>
    private async Task<int> AvatarsArriveAndOneLeaves(WorldState world, WorldPresenter presenter, AvatarPresenter avatars, AnimationSource animations,
        TextureSource? textures, string where, PrimLayer? prims = null)
    {
        var size = new System.Numerics.Vector3(0.45f, 0.6f, 1.9f);
        var others = new[] { Guid.Parse("07e407e4-0000-4000-8000-000000000003"), Guid.Parse("07e407e4-0000-4000-8000-000000000004") };
        var zoo = SculptZoo.Images.Keys.Concat(TextureZoo.Images.Keys).Distinct().Take(4).ToArray();   // the images the lifecycle's texture source serves
        world.ApplyAvatar(FixtureSim, new(800, LiveSelf, "Test User", new(MeshZoo.Origin.X, MeshZoo.Origin.Y - 4, 25), System.Numerics.Quaternion.Identity, size));
        for (int k = 0; k < others.Length; k++)
            world.ApplyAvatar(FixtureSim, new(901 + (uint)k, others[k], $"Example Avatar {k + 1}", new(MeshZoo.Origin.X + 2 * k, MeshZoo.Origin.Y - 6, 25), System.Numerics.Quaternion.Identity, size, new(0, 1, 0)));
        foreach (var agent in others.Prepend(LiveSelf))
        {
            if (textures is not null && zoo.Length == 4)
                world.ApplyAppearance(FixtureSim, Appearance(agent, new Dictionary<string, Guid> { ["head"] = zoo[0], ["upper"] = zoo[1], ["lower"] = zoo[2], ["eyes"] = zoo[3] }), LiveSelf);
            world.ApplyAnimations(FixtureSim, agent, [new(agent == LiveSelf ? StandId : WalkId, 1)]);
        }
        // each avatar wears two boxes (8101..8106, two to an avatar): they are drawn with their wearers, one is detached, and the
        // boxes of the avatar that leaves go with it
        uint[] wearers = [800, 901, 902];
        uint[] boxes = [8101, 8102, 8103, 8104, 8105, 8106];
        for (int k = 0; k < boxes.Length; k++)
            world.ApplyObject(FixtureSim, Box(boxes[k], wearers[k / 2], k % 2 == 0 ? 2 : 5, new(0, 0, 0.1f), System.Numerics.Quaternion.Identity, 0.1f, new(0.8f, 0.2f, 0.2f, 1)), eventIsAttachment: true);
        int wantBakes = textures is not null && zoo.Length == 4 ? 4 * 3 : 0;
        var sw = Stopwatch.StartNew();
        bool Ready() => avatars.Drawn == 3 && animations.Waiting == 0 && animations.InFlight == 0 && avatars.BakeTextures >= wantBakes
            && (prims is null || boxes.All(prims.Draws));
        while (!Ready() && sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            presenter.Frame();
            await Frames(1);
        }
        if (!Ready()) _failures.Add($"{where}: avatars drawn {avatars.Drawn}/3, bake textures {avatars.BakeTextures}/{wantBakes}, animations waiting {animations.Waiting}"
            + (prims is null ? "" : $", worn boxes drawn {boxes.Count(prims.Draws)}/{boxes.Length}"));
        // a detach: the simulator removes the object
        world.Kill(FixtureSim, [8102]);
        // --tr-alter=slowdetach: the frame after the detach has no time for more than one unit of work, as a frame that hitches does
        double budget = presenter.Coordinator.BudgetMs;
        if (_lifecycleAlter == "slowdetach") presenter.Coordinator.BudgetMs = 0;
        presenter.Frame();
        presenter.Coordinator.BudgetMs = budget;
        await PresentPending(presenter, prims);   // a removal is presented by the frames after it, as many as the frame budget needs
        if (prims is not null && (prims.Draws(8102) || boxes.Count(prims.Draws) != boxes.Length - 1))
        {
            var last = presenter.Coordinator.Last;
            _failures.Add($"{where}: after a detach {boxes.Count(prims.Draws)} of {boxes.Length - 1} worn boxes drawn, the detached one {(prims.Draws(8102) ? "still" : "no longer")} drawn"
                + $" (frame: {last.Applied} changes applied, {last.Steps} steps, {last.Backlog} changes queued, {last.SpentMs:0.00} ms of {budget:0.0}; {prims.Dirty} objects waiting)");
        }
        int before = avatars.LiveResources;
        world.Kill(FixtureSim, [902]);
        presenter.Frame();
        await PresentPending(presenter, prims);
        int per = before / 3;
        if (avatars.Drawn != 2 || avatars.LiveResources != before - per)
            _failures.Add($"{where}: after one left, drawn {avatars.Drawn}/2, resources {before} -> {avatars.LiveResources} (expected {before - per})");
        if (prims is not null)
        {
            _lifecycleWornDrawn = boxes.Count(prims.Draws);
            if (prims.Draws(8105) || prims.Draws(8106) || _lifecycleWornDrawn != 3)
                _failures.Add($"{where}: after the avatar left {_lifecycleWornDrawn} worn boxes drawn, expected 3 (its two went with it)");
        }
        return 1;   // one departure
    }

    /// <summary>Frames until every queued change is applied and no prim is waiting to be presented (at most 20 s): the presentation does its
    /// work within a per-frame time budget, so one frame after a change may not have reached it.</summary>
    private async Task PresentPending(WorldPresenter presenter, PrimLayer? prims)
    {
        var sw = Stopwatch.StartNew();
        while ((presenter.Coordinator.Backlog > 0 || (prims?.Dirty ?? 0) > 0) && sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            presenter.Frame();
            await Frames(1);
        }
    }

    /// <summary>The worn boxes the lifecycle's avatars still wear (and the prim layer draws) once one has left and one was detached.</summary>
    private int _lifecycleWornDrawn;

    private async Task<int> Lifecycle(int cycles, string viewKind)
    {
        _beat?.SetPhase("lifecycle");
        Mark("rig");
        var rig = MakeRig();
        await Frames(3);
        var baseline = Resources();
        var samples = new List<object> { new { at = "baseline", r = baseline } };
        Mark("view");
        var view = MakeView(viewKind, rig);
        long errors0 = Main.EngineLog?.Errors ?? 0;
        // with avatars, the engine's one-time state for them is made before the reference sample: their region drawn once
        // and freed (the skinned body, the name labels' font glyphs and text materials, which the engine keeps for the
        // process, as the viewer's start-up warm-up makes them); every region after must then add nothing
        if (_lifecycleCharacter is not null) await AvatarOneTimeState(rig, view);
        var viewMade = Resources();   // the view's own objects (the server view's material) are made here, before any region
        samples.Add(new { at = "view made", r = viewMade });
        var cycleSamples = new List<Res>();
        // the collector's counts when each engine-object sample was taken (not compared with anything): the engine counts an object until
        // the collector has finalized the managed wrapper of one that was let go of, so a sample moves with when the collector ran;
        // with these in the result a failing run says whether a collection fell between its samples
        var cycleGc = new List<int[]>();
        var viewMadeGc = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var zooPrims = PrimZoo.AsPrimData(PrimZoo.Make()).ToList();
        var meshPrims = _meshDir is null ? [] : PrimZoo.AsPrimData(MeshZoo.Make()).ToList();   // meshes too
        var texPrims = _texDir is null ? [] : PrimZoo.AsPrimData(TextureZoo.Make()).Where(p => p.Shape!.SculptType == 0).ToList();   // textured prims
        // the sculpt zoo too (its maps and the texture zoo's images through one texture source, from fixtures\sculpts)
        var sculptPrims = _texDir is null ? [] : PrimZoo.AsPrimData(SculptZoo.Make()).ToList();
        string? sculptDir = _texDir is null ? null : Path.GetFullPath(Path.Combine(_texDir, "..", "sculpts"));
        var lifecycleImages = SculptZoo.Images.Concat(TextureZoo.Images.Where(kv => !SculptZoo.Images.ContainsKey(kv.Key)).Select(kv => KeyValuePair.Create(kv.Key, "../textures/" + kv.Value)))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        int peakLive = 0, peakPrims = 0, peakMeshes = 0, peakEnv = 0, resets = 0, switches = 0, evictionCycles = 0;
        // with --tr-character every region also has three avatars (the user's own and two others, wearing bakes from the
        // texture zoo, playing the grid's stand and walk from --tr-assets=); one leaves mid-region. --tr-alter=leakavatar
        // (negative control): the departed avatar's label is kept, and the run must fail
        AvatarPresenter.LoadedBody? lifeBody = null;
        if (_lifecycleCharacter is not null)
        {
            var b = Viewer.Core.Avatars.AvatarBody.Load(_lifecycleCharacter);
            lifeBody = new(b, AvatarLayer.Prepare(b));
        }
        bool leakAvatar = _lifecycleAlter == "leakavatar";
        int avatarRegions = 0, avatarDepartures = 0, peakAvatarResources = 0;
        long evictedDrop = 0, reloadsTotal = 0, levelDrop = 0;
        int levelCycles = 0;
        // the water case. Every region carries a water height and an environment; the cycles alternate a
        // region RESET (the same presenter gets a RegionReset) and a region SWITCH (the presenter is disposed and a new one
        // made), and each must free the water mesh, instance, material and the sky resources.
        string? envLlsd = _envFile is null ? null : File.ReadAllText(_envFile);
        // keepwater-release: the same leak, and the leaked resources are freed after the checks, before the process quits
        bool leakWater = _lifecycleAlter is "keepwater" or "keepwater-release";
        bool leakTexture = _lifecycleAlter == "leaktexture";
        // what the region made and what outlives its change, by kind. "collect" also forces a full collection and the
        // pending finalizers after each change (what is still alive then is held by something reachable); "hold" builds
        // each region with collections held off (a no-GC region), so nothing the region made can have been let go by a
        // finalizer before the check: what outlives the change then is everything a finalizer would otherwise release
        bool collect = _lifecycleGc == "collect", hold = _lifecycleGc == "hold", details = _lifecycleDetails;
        var changes = new List<object>();
        var changeMs = new List<double>();
        int holdsBroken = 0;
        void HoldOff()
        {
            if (!hold) return;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            try { if (!GC.TryStartNoGCRegion(HoldBytes)) holdsBroken++; }
            catch (ArgumentOutOfRangeException) { holdsBroken++; }   // more than this runtime can hold off for
        }
        bool HoldEnd()
        {
            if (!hold) return true;
            bool held = System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion;
            if (held) GC.EndNoGCRegion();
            else holdsBroken++;
            return held;
        }
        // each object is watched through an engine weak reference, read by its variant's type only: asking by instance id
        // (GodotObject.IsInstanceIdValid) makes a managed wrapper for a live object, which fails fatally once the object's
        // first wrapper was disposed and collected
        static List<(string Kind, WeakRef Ref)> Watch(IEnumerable<(string Kind, GodotObject Object)> objects) => objects.Select(o => (o.Kind, GodotObject.WeakRef(o.Object)!)).ToList();
        static List<(string Kind, WeakRef Ref)> Alive(List<(string Kind, WeakRef Ref)> made)
        {
            var alive = new List<(string Kind, WeakRef Ref)>();
            foreach (var m in made)
            {
                var v = m.Ref.GetRef();
                if (v.VariantType == Variant.Type.Nil) continue;
                alive.Add(m);
                VariantDisposal.Defer(v, 0);   // the reference just taken goes with the drain below
            }
            VariantDisposal.DrainAll("flush");
            return alive;
        }
        void Outlived(int cycle, int size, string how, List<(string Kind, WeakRef Ref)> made, double ms, bool held)
        {
            var outlived = Alive(made);
            List<(string Kind, WeakRef Ref)>? afterCollect = null;
            if (collect)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                afterCollect = Alive(made);
            }
            foreach (var m in made) m.Ref.Dispose();
            static Dictionary<string, int> ByKind(IEnumerable<(string Kind, WeakRef Ref)> l) => l.GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Count());
            var r = Resources();
            changes.Add(new { cycle, size, how, made = ByKind(made), outlived = ByKind(outlived), outlived_after_collection = afterCollect is null ? null : ByKind(afterCollect),
                objects = r.Objects, video_mem = r.VideoMem, change_ms = Math.Round(ms, 3), held });
            changeMs.Add(ms);
            if (outlived.Count > 0)
                _failures.Add($"cycle {cycle} size {size} ({how}): {string.Join(", ", ByKind(outlived).Select(kv => $"{kv.Value} {kv.Key}"))} of the region's own outlived its change");
        }
        // a direct RID check. After a region goes, nothing of it may still be drawn: the objects drawn in a frame must
        // be back to the empty stage's count (a leaked water plane is 4 vertices, far below the memory tolerances, but it is
        // still an instance in the scenario and still drawn).
        long emptyDrawn = DrawnObjects();
        async Task CheckNothingDrawn(string when)
        {
            await Frames(2);
            long drawn = DrawnObjects();
            if (drawn != emptyDrawn) _failures.Add($"{when}: {drawn} objects still drawn after the region went (empty stage {emptyDrawn})");
        }
        for (int i = 0; i < cycles; i++)
        {
            foreach (int size in new[] { 1024, 256 })
            {
                // A region change: the old region's presenter and every chunk go, the new region is built from its store.
                _beat?.SetCycle(i, size);
                Mark("build");
                var terrain = SyntheticTerrain.Make("lifecycle", size, size);
                var loaded = FromTerrain(terrain, "lifecycle", "generated");
                // with --tr-lifecycle-details every region has the synthetic terrain composition, so its detail textures and
                // material come and go too (they are pinned in the residency, so not with the eviction cycles)
                var region = loaded.World.Region! with { Terrain = details ? TerrainDetails.SyntheticTerrain : null };
                if (envLlsd is not null) region = region with { WaterHeight = 18f + (size == 1024 ? 2f : 0f), EnvironmentLlsd = envLlsd };
                loaded.World.BeginRegion(FixtureSim, region);
                foreach (var (x, y, h) in terrain.Patches())
                    loaded.World.ApplyPatch(FixtureSim, x, y, terrain.PatchSize, h);
                foreach (var p in zooPrims.Concat(meshPrims).Concat(texPrims).Concat(sculptPrims))   // prims too; meshes; textures; sculpts
                    loaded.World.ApplyObject(FixtureSim, p);
                using var meshes = _meshDir is null ? null : MeshSourceFor(_meshDir, MeshZoo.Assets);
                using var textures = _texDir is null ? null : TextureSourceFor(sculptDir!, lifecycleImages);
                HoldOff();
                var prims = new PrimLayer(rig.Scenario, loaded.World, meshes, textures, _budget) { SimulatedFps = 60, LeakTextureForNegativeControl = leakTexture };   // deterministic decisions for goldens and lifecycle checks
                prims.Camera = new Vector3(MeshZoo.Origin.X, MeshZoo.Origin.Z + 3, -MeshZoo.Origin.Y + (size == 1024 ? 7 : 40));   // near, then far: both LOD paths
                var envLayer = envLlsd is null ? null : new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun) { LeakWaterForNegativeControl = leakWater };
                using var lifeAnimations = lifeBody is null ? null : AnimationSourceFor(_lifecycleAssets);
                var avatarLayer = lifeBody is null ? null : new AvatarPresenter(rig.Scenario, loaded.World, LiveSelf,
                    Task.FromResult<AvatarPresenter.LoadedBody?>(lifeBody), lifeAnimations, textures, prims.Residency, () => 0) { LeakBakeForNegativeControl = leakAvatar };
                var presenter = new WorldPresenter(loaded.World, view, prims: prims, environment: envLayer, avatars: avatarLayer) { DayFractionOverride = 0.3 };
                _lifecycleWornDrawn = 0;
                if (avatarLayer is not null) avatarDepartures += await AvatarsArriveAndOneLeaves(loaded.World, presenter, avatarLayer, lifeAnimations!, textures, $"cycle {i} size {size}", prims);
                if (avatarLayer is not null) { avatarRegions++; peakAvatarResources = Math.Max(peakAvatarResources, avatarLayer.LiveResources); _result["avatars_last_region"] = avatarLayer.Describe(); }
                Mark("settle");
                await SettleMeshes(presenter, prims, meshes, textures);
                if (envLayer is not null)
                {
                    if (!envLayer.HasWater || envLayer.Frame is null || presenter.EnvironmentSource != "region")
                        _failures.Add($"cycle {i} size {size}: water {envLayer.HasWater}, sky {envLayer.Frame is not null}, environment {presenter.EnvironmentSource}");
                    peakEnv = Math.Max(peakEnv, envLayer.LiveResources);
                    if (size == 256 && i % 2 == 1)
                    {
                        // a region SWITCH: no reset; the presenter goes, and must take the water and sky with it
                        Mark("switch");
                        var madeSwitch = Watch(presenter.EngineObjects());
                        long s0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        presenter.Dispose();
                        double switchMs = System.Diagnostics.Stopwatch.GetElapsedTime(s0).TotalMilliseconds;
                        Outlived(i, size, "switch", madeSwitch, switchMs, HoldEnd());
                        switches++;
                        if (envLayer.LiveResources != 0 || Stage.EnvironmentOf(rig.Environment).Sky is not null)
                            _failures.Add($"cycle {i} size {size}: {envLayer.LiveResources} water/sky resources still live after a region switch");
                        await CheckNothingDrawn($"cycle {i} size {size} (switch)");
                        loaded.Dispose();
                        continue;
                    }
                }
                if (textures is not null && prims.GpuTextures != TextureZoo.Images.Count)
                    _failures.Add($"cycle {i} size {size}: {prims.GpuTextures} of {TextureZoo.Images.Count} textures on the GPU");
                peakMeshes = Math.Max(peakMeshes, prims.MeshAssetResources);
                if (prims.MeshObjects != meshPrims.Count)
                    _failures.Add($"cycle {i} size {size}: {prims.MeshObjects} of {meshPrims.Count} meshes presented");
                Mark("frames");
                await Frames(2);
                peakLive = Math.Max(peakLive, view.LiveResources);
                peakPrims = Math.Max(peakPrims, prims.LiveResources);
                if (prims.SculptObjects != sculptPrims.Count)
                    _failures.Add($"cycle {i} size {size}: {prims.SculptObjects} of {sculptPrims.Count} sculpts presented");
                if (prims.Instances != zooPrims.Count + meshPrims.Count + texPrims.Count + sculptPrims.Count + _lifecycleWornDrawn)
                    _failures.Add($"cycle {i} size {size}: {prims.Instances} of {zooPrims.Count + meshPrims.Count + texPrims.Count + sculptPrims.Count + _lifecycleWornDrawn} prims presented");
                if (_evictCycles > 0 && prims.Residency is { } res)
                {
                    // evict every texture (budget 1 byte: the visible set alone exceeds it) and reload it
                    // (budget back), repeatedly; GPU texture memory must drop on eviction and return to the same level
                    int slots = prims.GpuTextures;
                    // the level to come back to is the settled one: a texture still refining to a finer level when this was read made the
                    // reload look 70 KiB larger (the same difference in each of the cycle's reloads, none added by the next: not a leak)
                    await SettleMeshes(presenter, prims, meshes, textures);
                    await Frames(2);
                    long full = TexMem();
                    for (int e = 0; e < _evictCycles; e++)
                    {
                        Mark("evict");
                        res.SetBudget(1);
                        prims.UpdateResidency();
                        await Frames(6);   // GPU memory is freed once the frames in flight that used it are done
                        long evicted = TexMem();
                        if (res.Resident != 0 || evicted >= full) _failures.Add($"cycle {i} size {size} eviction {e}: {res.Resident} still resident, texture memory {full} -> {evicted}");
                        res.SetBudget(_budget);
                        await SettleMeshes(presenter, prims, meshes, textures);
                        await Frames(2);
                        if (res.Resident != slots || prims.GpuTextures != slots) _failures.Add($"cycle {i} size {size} reload {e}: {res.Resident} of {slots} resident, {prims.GpuTextures} slots");
                        if (TexMem() > full + 64 * 1024) _failures.Add($"cycle {i} size {size} reload {e}: texture memory {full} -> {TexMem()}");
                        // down through the levels and back. A narrow view on the zoo first (its 64 px textures
                        // then want full resolution); a budget that holds every texture only at its coarsest level (the
                        // low-water mark just above their sum) steps each one down in place (same slots, no eviction);
                        // the budget back steps them up again, to the same memory
                        Mark("levels");
                        var eye = new System.Numerics.Vector3(prims.Camera.X, -prims.Camera.Z, prims.Camera.Y);
                        prims.ResidencyView = new Viewer.Core.Residency.ResidencyCamera(eye, System.Numerics.Vector3.Normalize(PrimZoo.Origin - eye),
                            System.Numerics.Vector3.UnitZ, 0.15f, 16f / 9, 0.1f, 4096f, 360);
                        await SettleMeshes(presenter, prims, meshes, textures);
                        await Frames(6);
                        long fine = TexMem();
                        var inView = res.All.Where(id => res.IsVisible(id) && res.LevelsOf(id) > 1).ToList();
                        if (inView.Count == 0 || inView.Any(id => res.LevelOf(id) != 0))
                            _failures.Add($"cycle {i} size {size} levels {e}: the narrow view brought {inView.Count(id => res.LevelOf(id) == 0)} of {inView.Count} in-view textures to level 0");
                        long coarse = res.All.Where(id => res.StateOf(id) == Viewer.Core.Residency.ResidencyState.Resident && res.LevelsOf(id) > 1)
                            .Sum(id => res.BytesAtLevel(id, res.LevelsOf(id) - 1));
                        long fixedBytes = res.All.Where(id => res.StateOf(id) == Viewer.Core.Residency.ResidencyState.Resident && res.LevelsOf(id) <= 1).Sum(res.BytesOf);
                        long evictionsBefore = res.Evictions, downBefore = res.StepsDown;
                        res.SetBudget((long)((coarse + fixedBytes) / 0.9) + 64);
                        await SettleMeshes(presenter, prims, meshes, textures);
                        await Frames(6);
                        long stepped = TexMem();
                        if (res.Evictions != evictionsBefore || res.StepsDown == downBefore || prims.GpuTextures != slots
                            || res.All.Any(id => res.LevelsOf(id) > 1 && res.LevelOf(id) != res.LevelsOf(id) - 1) || stepped >= fine)
                            _failures.Add($"cycle {i} size {size} levels {e}: evictions {res.Evictions - evictionsBefore}, steps down {res.StepsDown - downBefore}, slots {prims.GpuTextures}/{slots}, texture memory {fine} -> {stepped}");
                        res.SetBudget(_budget);
                        await SettleMeshes(presenter, prims, meshes, textures);
                        await Frames(6);
                        if (prims.GpuTextures != slots || res.All.Any(id => !res.AtDesired(id)) || TexMem() > fine + 64 * 1024)
                            _failures.Add($"cycle {i} size {size} levels back {e}: slots {prims.GpuTextures}/{slots}, at desired {res.All.Count(res.AtDesired)}/{res.Textures}, texture memory {fine} -> {TexMem()}");
                        levelDrop = Math.Max(levelDrop, fine - stepped);
                        prims.ResidencyView = null;   // back to the lifecycle's own view
                        await SettleMeshes(presenter, prims, meshes, textures);
                        levelCycles++;
                        evictionCycles++;
                        evictedDrop = Math.Max(evictedDrop, full - evicted);
                    }
                    reloadsTotal += res.Reloads;
                }
                Mark("reset");
                var made = Watch(presenter.EngineObjects());
                long r0 = System.Diagnostics.Stopwatch.GetTimestamp();
                presenter.Apply(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(loaded.World.Epoch + 1, 0)));
                double resetMs = System.Diagnostics.Stopwatch.GetElapsedTime(r0).TotalMilliseconds;
                resets++;
                if (view.LiveResources != 0 || prims.LiveResources != 0 || prims.Materials != 0)
                    _failures.Add($"cycle {i} size {size}: {view.LiveResources} terrain + {prims.LiveResources} prim resources, {prims.Materials} materials still live after the region reset");
                if (avatarLayer is not null && (avatarLayer.LiveResources != 0 || avatarLayer.Scene.Count != 0))
                    _failures.Add($"cycle {i} size {size}: {avatarLayer.LiveResources} avatar resources, {avatarLayer.Scene.Count} avatars still live after the region reset");
                if (avatarLayer is not null && LabelsUnder(rig.Root) is int labelsLeft and > 0)
                    _failures.Add($"cycle {i} size {size}: {labelsLeft} name label(s) still in the scene after the region reset");
                if (envLayer is not null && (envLayer.LiveResources != 0 || Stage.EnvironmentOf(rig.Environment).Sky is not null))
                    _failures.Add($"cycle {i} size {size}: {envLayer.LiveResources} water/sky resources still live after the region reset");
                Mark("dispose");
                presenter.Dispose();
                Mark("outlived");
                Outlived(i, size, "reset", made, resetMs, HoldEnd());
                if (envLlsd is not null)
                {
                    Mark("drawn");
                    await CheckNothingDrawn($"cycle {i} size {size} (reset)");
                }
                Mark("region disposed");
                loaded.Dispose();
            }
            Mark("cycle end");
            await Frames(3);
            cycleSamples.Add(Resources());
            cycleGc.Add([GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)]);
            if (i == Math.Min(1, cycles - 1) || i == cycles - 1)
                samples.Add(new { at = $"after cycle {i}", r = cycleSamples[^1] });
        }
        Mark("clear");
        view.Clear();
        if (view is Node n) { n.GetParent().RemoveChild(n); n.Free(); }
        await Frames(5);
        var end = Resources();
        samples.Add(new { at = "end", r = end });
        // Every chunk's mesh and instance RID freed, and nothing a region made outlives its change (checked per object
        // above). Video memory is compared exactly with the sample after cycle 0: the engine allocates its one-time
        // state while drawing the first regions (measured: 128 B on vulkan, one 64 KiB block on d3d12), and every later
        // cycle must match it. Engine objects are compared with the sample made with the view (the view's own objects
        // are made before it): the regions add none, except that on d3d12 the engine now and then makes one object of its
        // own once, at any cycle, and keeps it for the run (measured: 2 of 17 runs, at cycle 0 and at cycle 4, video
        // memory unchanged, nothing of a region's own alive). So every cycle may be at most one object above that sample,
        // and the count never goes down: growth across region changes (a leaked texture is two objects a region) and
        // objects freed late by a finalizer both fail.
        var reference = cycleSamples[0];
        long engineErrors = (Main.EngineLog?.Errors ?? 0) - errors0;
        _result["engine_errors"] = engineErrors;
        // the engine reports what the viewer did wrong (a mesh left on a freed material, a freed RID used) as errors,
        // and each one also hands the logger objects that only a finalizer releases: a region change makes none
        if (engineErrors != 0) _failures.Add($"{engineErrors} engine errors during the region changes (the log names them)");
        _result["one_time_engine_objects"] = cycleSamples[^1].Objects - viewMade.Objects;
        _result["one_time_video_mem"] = reference.VideoMem - viewMade.VideoMem;

        // the engine's one-time video memory: one 64 KiB block, and with a sky its radiance maps (1 MiB on d3d12,
        // 960 KiB on vulkan, measured). The water reads the scene behind it through the screen texture, and the engine makes
        // its copy of the finished colour (with its blur chain) the first time a material asks for it and keeps it for the
        // render target: measured 16.1 bytes a pixel on vulkan and 14.8 on d3d12, the same in every later cycle (with the
        // water shader's screen read taken out the first cycle adds the 1 MiB above and nothing more, on both). The
        // allowance is 20 bytes a pixel of the view, so it grows with the size the copy is made at, and only with a sky and water;
        // growth in a later cycle is still compared exactly below.
        long viewPixels = (long)rig.Viewport.Size.X * rig.Viewport.Size.Y;
        long oneTimeVideo = 64 * 1024 + (envLlsd is not null ? 1024 * 1024 + 20 * viewPixels : 0);
        _result["one_time_video_mem_allowed"] = oneTimeVideo;
        if (reference.VideoMem - viewMade.VideoMem > oneTimeVideo)
            _failures.Add($"video memory after the first cycle {reference.VideoMem}, with the view made {viewMade.VideoMem}: more than the engine's one-time {oneTimeVideo} B");
        for (int k = 0; k < cycleSamples.Count; k++)
        {
            long extra = cycleSamples[k].Objects - viewMade.Objects;
            if (extra < 0 || extra > 1 || (k > 0 && cycleSamples[k].Objects < cycleSamples[k - 1].Objects))
                _failures.Add($"after cycle {k}: objects {cycleSamples[k].Objects}, with the view made {viewMade.Objects}{(k > 0 ? $", after cycle {k - 1} {cycleSamples[k - 1].Objects}" : "")} (collections since the previous sample, generations 0/1/2: {string.Join("/", cycleGc[k].Select((g, n) => g - (k > 0 ? cycleGc[k - 1][n] : viewMadeGc[n])))})");
            if (k > 0 && cycleSamples[k].VideoMem != reference.VideoMem)
                _failures.Add($"after cycle {k}: video memory {reference.VideoMem} -> {cycleSamples[k].VideoMem} (after cycle 0)");
        }
        // the view goes last: its objects with it, nothing else
        if (end.Objects > cycleSamples[^1].Objects) _failures.Add($"objects after the view went {end.Objects}, after the last cycle {cycleSamples[^1].Objects}");
        if (end.Nodes != baseline.Nodes) _failures.Add($"nodes {baseline.Nodes} -> {end.Nodes}");
        if (end.BufferMem > baseline.BufferMem + 64 * 1024) _failures.Add($"render buffer memory {baseline.BufferMem} -> {end.BufferMem}");
        if (end.VideoMem > reference.VideoMem) _failures.Add($"video memory after the view went {end.VideoMem}, after cycle 0 {reference.VideoMem}");
        if (envLlsd is not null)
            _result["water"] = new { resets, switches, peak_environment_resources = peakEnv, negative_control = leakWater };
        if (_evictCycles > 0)
            _result["eviction"] = new { cycles = evictionCycles, reloads = reloadsTotal, max_texture_mem_freed = evictedDrop, level_cycles = levelCycles, max_texture_mem_freed_by_levels = levelDrop, texture_mem_end = TexMem() };
        _result["cycle_samples"] = cycleSamples.Select((r, k) => new { cycle = k, objects = r.Objects, video_mem = r.VideoMem, collections = cycleGc[k] }).ToArray();
        _result["region_changes"] = new { gc = _lifecycleGc ?? "as it runs", holds_broken = holdsBroken, change_ms = DistOrNull(changeMs), each = changes };
        if (hold && holdsBroken > 0) _failures.Add($"{holdsBroken} region(s) could not be built with collections held off: inconclusive");
        if (lifeBody is not null)
            _result["avatars"] = new { regions = avatarRegions, departures = avatarDepartures, peak_resources = peakAvatarResources, negative_control = leakAvatar };
        _result["mode"] = "lifecycle";
        _result["view"] = viewKind;
        _result["cycles"] = cycles;
        _result["peak_live_resources"] = peakLive;
        _result["peak_prim_resources"] = peakPrims;
        _result["peak_mesh_resources"] = peakMeshes;
        _result["samples"] = samples;
        if (_lifecycleAlter == "keepwater-release") _result["leaked_water_released"] = EnvironmentLayer.ReleaseLeakedWaterForNegativeControl();
        return _failures.Count == 0 ? 0 : 1;
    }

    private sealed class DiscardSink : ILogSink { public void Write(LogEntry entry) { } }

    private sealed record Res(long Objects, long Resources, long Nodes, long BufferMem, long VideoMem);

    private static long TexMem() => (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed);

    /// <summary>The lifecycle's collection mode (--tr-lifecycle-gc=collect|hold; absent: as it runs).</summary>
    private string? _lifecycleGc;
    /// <summary>Every lifecycle region gets terrain detail textures (--tr-lifecycle-details).</summary>
    private bool _lifecycleDetails;

    /// <summary>What a region may allocate while collections are held off (--tr-lifecycle-gc=hold).</summary>
    private const long HoldBytes = 1024L * 1024 * 1024;

    /// <summary>Eviction / reload cycles per region in the lifecycle check (--tr-evict-cycles=N).</summary>
    private int _evictCycles;

    private static long DrawnObjects() => (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame);

    /// <summary>The counts with the garbage collected first. The engine counts an object until the collector has finalized the managed wrapper of
    /// any it has let go of (the warm-up's and each scene's texture, world and environment handles), so a count taken without this moves by
    /// the garbage the collector happened to have reached: the warm-up check's counts differed by up to seven objects between identical runs.
    /// Collected, the same run gives the same counts each time and they are the objects something still holds.</summary>
    private Res CountedResources()
    {
        VariantDisposal.DrainAll("count");
        for (int i = 0; i < 2; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
        }
        return Resources();
    }

    private Res Resources() => new(
        (long)Performance.GetMonitor(Performance.Monitor.ObjectCount),
        (long)Performance.GetMonitor(Performance.Monitor.ObjectResourceCount),
        (long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
        (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed),
        (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed));

    // ---------------- helpers ----------------

    /// <summary>
    /// Takes the display's vertical sync and the engine's frame-rate limit off, so the frame times a run reports are what the
    /// scene costs and not how long a present waited, and records what the engine says it ended up with: the refresh rate of
    /// the screen the window is on (0 when the engine cannot tell), the vsync mode read back, and the frame-rate limit.
    /// A driver or compositor limit outside the engine is not visible here; <see cref="FramePacingGuard"/> catches it from the frames.
    /// </summary>
    private void FreeRunning()
    {
        // --tr-vsync=on is the control run: the mode stays on, so the pacing guard has a known-paced run to be checked against
        var userArgs = OS.GetCmdlineUserArgs();
        bool vsyncControl = userArgs.Any(a => a == "--tr-vsync=on");
        DisplayServer.WindowSetVsyncMode(vsyncControl ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 0;
        // --tr-power-opt-out: the process tells Windows not to throttle it, so a run in an unfocused window is not slowed by the system
        bool optOut = userArgs.Any(a => a == "--tr-power-opt-out");
        bool optOutAccepted = optOut && PresentDiagnostics.OptOutOfThrottling();
        var throttle = PresentDiagnostics.ThrottlingState();
        int screen = DisplayServer.WindowGetCurrentScreen();
        _result["display"] = new Dictionary<string, object?>
        {
            ["refresh_hz"] = DisplayRefreshHz(),
            ["screen"] = screen,
            ["vsync_mode"] = DisplayServer.WindowGetVsyncMode().ToString(),
            ["max_fps"] = Engine.MaxFps,
            ["window_mode"] = DisplayServer.WindowGetMode().ToString(),
            ["rendering_driver"] = RenderingServer.GetCurrentRenderingDriverName(),
            ["tearing_supported"] = PresentDiagnostics.TearingSupported(),
            ["power_throttling"] = throttle.Mode,
            ["power_throttling_control_mask"] = throttle.ControlMask,
            ["power_throttling_state_mask"] = throttle.StateMask,
            ["power_opt_out_requested"] = optOut,
            ["power_opt_out_accepted"] = optOutAccepted,
            ["vsync_control_run"] = vsyncControl,
        };
    }

    /// <summary>What the engine reports it presents with right now: the vsync mode read back, the frame-rate limit, and Windows' throttling of the process.</summary>
    private static PresentSettings CurrentPresentSettings() =>
        new(DisplayServer.WindowGetVsyncMode().ToString(), Engine.MaxFps, PresentDiagnostics.ThrottlingState().Mode);

    private static double DisplayRefreshHz()
    {
        float hz = DisplayServer.ScreenGetRefreshRate(DisplayServer.WindowGetCurrentScreen());
        return hz > 0 ? Math.Round(hz, 2) : 0;
    }

    private static object? DistOrNull(List<double> v) => v.Count == 0 ? null : Dist(v);

    /// <summary>The presenter's settle gate.</summary>
    private static object TerrainSettleReport(TerrainSettle s) => new
    {
        window_ms = s.WindowMs, cap_ms = s.CapMs, started = s.Started, started_by_cap = s.StartedByCap,
        settle_delay_ms = DistOrNull(s.SettleDelayMs.ToList()), patch_changes = s.Changes.Count,
        changes = s.Changes.Select(c => new[] { Math.Round(c.Ms, 2), c.PatchX, c.PatchY }).ToList(),
        all_dirty = s.AllDirtyMarks.Select(a => new { ms = Math.Round(a.Ms, 2), cause = a.Cause }).ToList(),
    };

    private static object Dist(List<double> v) => new
    {
        n = v.Count,
        p50 = Math.Round(Percentile(v, 50), 3),
        p95 = Math.Round(Percentile(v, 95), 3),
        p99 = Math.Round(Percentile(v, 99), 3),
        max = Math.Round(v.Max(), 3),
    };

    private static double Percentile(List<double> v, double p)
    {
        var s = v.OrderBy(x => x).ToList();
        return s[Math.Clamp((int)Math.Ceiling(p / 100.0 * s.Count) - 1, 0, s.Count - 1)];
    }

    private static float[] V(Vector3 v) => [MathF.Round(v.X, 2), MathF.Round(v.Y, 2), MathF.Round(v.Z, 2)];

    /// <summary>A step of the run starts: the heartbeat names it before its work (and the hang test stops here when
    /// asked to).</summary>
    private void Mark(string step)
    {
        if (_watch is not null) _beat?.SetWindow(WindowState());   // at a step, not every frame: the window query allocates
        _beat?.StartStep(step);
        if (_hangAt is { } h && !_hung && (h == step || h == $"{step}:{_beat?.Cycle}"))
        {
            _hung = true;
            FreezeForHangTest(step);
        }
    }

    /// <summary>The hang test's stop: this thread sleeps for good inside this call, which a dump of the process shows
    /// on the main thread's stack.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void FreezeForHangTest(string step)
    {
        Console.Error.WriteLine($"TEST-RENDER hang test: the main thread stops at step '{step}'");
        Thread.Sleep(Timeout.Infinite);
    }

    private static string? Arg(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length);
}
