// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// tools\aim-check.ps1 judges a live picture from the report the aim command saved with it: the camera was aimed and the pose applied,
/// the target's box fills enough of the frame and sits in the middle (worked out again from the box, not taken from the report's own
/// verdict), and the picture file is there and has the frame's size. These tests run it as a child process on made-up reports and
/// small picture files. Every test uses its own temp folder and child process, so they run in parallel.
/// </summary>
public sealed class AimCheckScriptTests
{
    private readonly ITestOutputHelper _out;
    public AimCheckScriptTests(ITestOutputHelper output) { _out = output; }

    private static string Script => Path.Combine(TestPaths.RepoRoot, "tools", "aim-check.ps1");

    private static string Report(string root, double left, double top, double right, double bottom, bool aimed = true, bool applied = true, double minFill = 0.5,
        double maxOffset = 0.1, bool recordedPass = true, int frameW = 1920, int frameH = 1080, double[]? engineCentre = null, bool noEngineCentre = false, double[]? mainArea = null)
    {
        var report = new
        {
            aimed,
            frame = new[] { frameW, frameH },
            box = new { left, top, right, bottom, in_front = true },
            check = new { pass = recordedPass, min_fill = minFill, max_offset = maxOffset },
            engine = new { applied, centre = noEngineCentre ? null : engineCentre ?? [(left + right) / 2, (top + bottom) / 2] },
            main_area = mainArea is null ? null : new { left = mainArea[0], top = mainArea[1], right = mainArea[2], bottom = mainArea[3] },
        };
        string path = Path.Combine(root, "shot.aim.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report));
        return path;
    }

    /// <summary>A file with a PNG signature and a header chunk of the given size: all the script reads.</summary>
    private static string Picture(string root, int width, int height)
    {
        string path = Path.Combine(root, "shot.png");
        using var w = new BinaryWriter(File.Create(path));
        w.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        w.Write(new byte[] { 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52 });
        w.Write(new byte[] { (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width });
        w.Write(new byte[] { (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height });
        w.Write(new byte[] { 8, 2, 0, 0, 0, 0, 0, 0, 0 });
        return path;
    }

    private (int Exit, string Output) Run(string report, string? picture)
    {
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Report", report };
        if (picture is not null) { args.Add("-Picture"); args.Add(picture); }
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2));
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        return r;
    }

    [Fact]
    public void A_body_that_fills_the_frame_in_the_middle_with_a_picture_of_the_frame_s_size_passes()
    {
        string root = TestTemp.NewFolder("aim-check");
        var r = Run(Report(root, 780, 80, 1140, 1000), Picture(root, 1920, 1080));
        Assert.Equal(0, r.Exit);
        Assert.Contains("AIM CHECK PASS", r.Output);
    }

    [Fact]
    public void A_tiny_figure_fails_as_too_small_whatever_the_report_recorded()
    {
        string root = TestTemp.NewFolder("aim-check");
        var r = Run(Report(root, 930, 500, 990, 580, recordedPass: true), Picture(root, 1920, 1080));
        Assert.Equal(1, r.Exit);
        Assert.Contains("AIM CHECK FAIL", r.Output);
        Assert.Contains("too_small", r.Output);
    }

    [Fact]
    public void A_figure_at_the_edge_fails_as_off_centre()
    {
        string root = TestTemp.NewFolder("aim-check");
        var r = Run(Report(root, 20, 80, 380, 1000), Picture(root, 1920, 1080));
        Assert.Equal(1, r.Exit);
        Assert.Contains("off_centre", r.Output);
    }

    [Fact]
    public void A_figure_cut_off_by_the_frame_fails_as_cut_off()
    {
        string root = TestTemp.NewFolder("aim-check");
        var r = Run(Report(root, 600, -400, 1300, 1500, maxOffset: 0.5), Picture(root, 1920, 1080));
        Assert.Equal(1, r.Exit);
        Assert.Contains("cut_off", r.Output);
    }

    [Fact]
    public void A_picture_of_another_size_than_the_frame_fails()
    {
        string root = TestTemp.NewFolder("aim-check");
        var r = Run(Report(root, 780, 80, 1140, 1000), Picture(root, 1280, 720));
        Assert.Equal(1, r.Exit);
        Assert.Contains("picture_size", r.Output);
    }

    [Fact]
    public void A_picture_that_is_not_there_fails()
    {
        string root = TestTemp.NewFolder("aim-check");
        var r = Run(Report(root, 780, 80, 1140, 1000), Path.Combine(root, "missing.png"));
        Assert.Equal(1, r.Exit);
        Assert.Contains("picture_missing", r.Output);
    }

    [Fact]
    public void A_pose_the_engine_had_not_applied_fails()
    {
        string root = TestTemp.NewFolder("aim-check");
        var r = Run(Report(root, 780, 80, 1140, 1000, applied: false), Picture(root, 1920, 1080));
        Assert.Equal(1, r.Exit);
        Assert.Contains("pose_not_applied", r.Output);
    }

    [Fact]
    public void A_report_of_a_camera_that_was_not_aimed_fails()
    {
        string root = TestTemp.NewFolder("aim-check");
        var r = Run(Report(root, 780, 80, 1140, 1000, aimed: false), Picture(root, 1920, 1080));
        Assert.Equal(1, r.Exit);
        Assert.Contains("not_aimed", r.Output);
    }

    [Fact]
    public void A_target_the_engine_puts_behind_the_camera_fails_whatever_the_arithmetic_says()
    {
        string root = TestTemp.NewFolder("aim-check");
        var r = Run(Report(root, 780, 80, 1140, 1000, noEngineCentre: true), Picture(root, 1920, 1080));
        Assert.Equal(1, r.Exit);
        Assert.Contains("engine_disagrees", r.Output);
    }

    [Fact]
    public void A_target_the_engine_projects_elsewhere_than_the_arithmetic_fails()
    {
        string root = TestTemp.NewFolder("aim-check");
        var r = Run(Report(root, 780, 80, 1140, 1000, engineCentre: [200, 300]), Picture(root, 1920, 1080));
        Assert.Equal(1, r.Exit);
        Assert.Contains("engine_disagrees", r.Output);
    }

    // The interface bars (the menu and location rows on top, the chat row and the toolbar at the bottom) cover the frame; a report that gives the
    // area they leave (main_area, in picture pixels) has anything under a bar counted as off the screen.
    private static readonly double[] BelowBars = [0, 86, 1920, 940];

    [Fact]
    public void A_box_under_the_top_bar_fails_as_cut_off()
    {
        string root = TestTemp.NewFolder("aim-check");
        var r = Run(Report(root, 780, 20, 1140, 500, minFill: 0.3, maxOffset: 0.5, mainArea: BelowBars), Picture(root, 1920, 1080));
        Assert.Equal(1, r.Exit);
        Assert.Contains("cut_off", r.Output);
    }

    [Fact]
    public void The_same_box_clear_of_the_top_bar_passes()
    {
        string root = TestTemp.NewFolder("aim-check");
        var r = Run(Report(root, 780, 100, 1140, 580, minFill: 0.3, maxOffset: 0.5, mainArea: BelowBars), Picture(root, 1920, 1080));
        Assert.Equal(0, r.Exit);
        Assert.Contains("AIM CHECK PASS", r.Output);
    }

    [Fact]
    public void A_box_under_the_bottom_bar_fails_as_cut_off()
    {
        string root = TestTemp.NewFolder("aim-check");
        var r = Run(Report(root, 780, 500, 1140, 1000, minFill: 0.3, maxOffset: 0.5, mainArea: BelowBars), Picture(root, 1920, 1080));
        Assert.Equal(1, r.Exit);
        Assert.Contains("cut_off", r.Output);
    }

    [Fact]
    public void A_report_that_is_not_there_is_a_usage_error()
    {
        string root = TestTemp.NewFolder("aim-check");
        var r = Run(Path.Combine(root, "nothing.json"), null);
        Assert.Equal(2, r.Exit);
        Assert.Contains("report", r.Output);
    }
}
