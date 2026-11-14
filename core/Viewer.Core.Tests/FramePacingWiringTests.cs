// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The engine host cannot run in the unit tests, so the wiring that keeps measuring runs free-running and honest is
/// checked in the sources: every run that measures frame times takes vsync and the frame-rate limit off through one helper
/// that also records the display's refresh rate and the vsync mode read back from the engine, a measuring run's path
/// frames go through the pacing guard, and the script that starts a measuring run turns the guard on.
/// </summary>
public class FramePacingWiringTests
{
    private static string Host(string name) => File.ReadAllText(Path.Combine(RepoRoot(), "host", "Viewer.Godot", name));

    [Fact]
    public void No_scene_sets_vsync_or_the_frame_limit_on_its_own()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(RepoRoot(), "host", "Viewer.Godot"), "TestRender*.cs"))
        {
            string text = File.ReadAllText(file);
            if (Path.GetFileName(file) == "TestRender.cs")
            {
                // the helper itself is the one place that sets them
                Assert.Equal(1, Regex.Matches(text, @"WindowSetVsyncMode\(").Count);
                Assert.Equal(1, Regex.Matches(text, @"Engine\.MaxFps\s*=").Count);
            }
            else
            {
                Assert.DoesNotContain("WindowSetVsyncMode(", text);
                Assert.DoesNotContain("Engine.MaxFps", text);
            }
        }
    }

    [Fact]
    public void The_helper_records_the_refresh_rate_and_the_vsync_mode_it_reads_back()
    {
        string text = Host("TestRender.cs");
        Assert.Contains("ScreenGetRefreshRate(", text);
        Assert.Contains("WindowGetVsyncMode(", text);
        Assert.Contains("_result[\"display\"]", text);
    }

    [Fact]
    public void The_path_run_judges_its_frames_with_the_guard_and_fails_a_capped_run_when_asked_to()
    {
        string text = Host("TestRender.cs");
        Assert.Contains("JudgeFrames(frameMs,", text);
        Assert.Contains("--tr-pacing-guard", text);
        string judge = Host("TestRender.FrameFigures.cs");
        Assert.Contains("FrameFigures.Judge(driver, frames,", judge);   // which calls FramePacingGuard.Evaluate on the real frame times
        Assert.Contains("_result[\"frame_pacing\"]", judge);
        Assert.Contains("judgement.Fails && _pacingGuard", judge);
    }

    [Fact]
    public void The_script_that_starts_a_measuring_run_turns_the_guard_on()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "godot.ps1"));
        Assert.Contains("--tr-pacing-guard", script);
        Assert.Matches(@"\$measuring[^\n]*\n?[^\n]*--tr-pacing-guard|--tr-pacing-guard[^\n]*\$measuring", script);
    }

    [Fact]
    public void A_measuring_run_starts_the_engine_with_vsync_off_because_switching_it_off_after_start_does_not_take_effect_everywhere()
    {
        // measured: with the swap chain made with vsync on and the mode switched off at run time, frames stayed paced
        // (about 620 frames a second, 95th percentile about 7 ms); started with the engine's --disable-vsync the same run
        // drew about 1500 frames a second at about 1.2 ms
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "godot.ps1"));
        Assert.Matches(@"\$measuring[^
]*--test-render[^
]*--disable-vsync", script);
    }

    [Fact]
    public void Both_path_and_busy_runs_give_the_guard_the_settings_the_engine_reports()
    {
        foreach (string file in new[] { "TestRender.cs", "TestRender.Busy.cs" })
            Assert.Contains("JudgeFrames(frameMs,", Host(file));
        Assert.Contains("FrameFigures.Judge(driver, frames, cpu, gpu, DisplayRefreshHz(), CurrentPresentSettings())", Host("TestRender.FrameFigures.cs"));
        string helper = Host("TestRender.cs");
        Assert.Contains("PresentSettings CurrentPresentSettings()", helper);
        Assert.Contains("WindowGetVsyncMode()", helper);
        Assert.Contains("ThrottlingState()", helper);
    }

    [Fact]
    public void The_display_record_carries_the_present_state_and_the_verdict_names_its_kind()
    {
        string text = Host("TestRender.cs");
        foreach (string key in new[] { "tearing_supported", "power_throttling", "window_mode", "rendering_driver", "vsync_control_run" })
            Assert.Contains($"[\"{key}\"]", text);
        Assert.Contains("kind = pacing.Kind.ToString()", Host("TestRender.FrameFigures.cs"));
    }

    [Fact]
    public void The_result_names_the_basis_of_each_figure_the_cost_parts_and_the_frames_set_aside()
    {
        string judge = Host("TestRender.FrameFigures.cs");
        foreach (string key in new[] { "basis", "frame_figures", "frame_cost", "frame_time", "render_cpu_ms", "render_gpu_ms", "main_thread_ms", "cost_agreement", "marked", "flagged" })
            Assert.Contains(key + " =", judge);
        Assert.Contains("p50 = name, p95 = name, p99 = name, max = name", judge);   // every figure's basis, by name
        Assert.Contains("_exitOverride = NotDrawingExit", judge);
    }

    [Fact]
    public void Both_measuring_loops_record_the_cost_the_main_threads_work_and_the_frames_to_set_aside()
    {
        string path = Host("TestRender.cs"), busy = Host("TestRender.Busy.cs");
        Assert.Contains("ViewportGetMeasuredRenderTimeCpu(rig.Viewport.GetViewportRid())", path);
        Assert.Contains("ViewportGetMeasuredRenderTimeGpu(rig.Viewport.GetViewportRid())", path);
        Assert.Contains("ViewportGetMeasuredRenderTimeCpu(vpRid)", busy);
        Assert.Contains("ViewportGetMeasuredRenderTimeGpu(vpRid)", busy);
        foreach (string text in new[] { path, busy })
        {
            Assert.Contains("_keeper?.TakeMark()", text);
            Assert.Contains("mainMs.Add(", text);
        }
        Assert.Contains("DisplayServer.WindowSetMode(DisplayServer.WindowMode.Minimized)", busy);   // the proof run minimises the window once
    }

    [Fact]
    public void The_busy_run_keeps_its_frame_series_so_a_run_can_be_judged_again()
    {
        Assert.Contains("frame_ms_series", Host("TestRender.Busy.cs"));
    }

    [Fact]
    public void A_vsync_control_run_is_started_with_the_engines_vsync_on()
    {
        // the control must have a swap chain made with vsync on, so the script leaves out --disable-vsync for it
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "godot.ps1"));
        Assert.Matches(@"--disable-vsync'[^
]*--tr-vsync=on", script);
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Viewer.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir ?? throw new InvalidOperationException("repository root not found");
    }
}
