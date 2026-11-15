// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using NvTools;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Where a test run's window goes (tools\TestWindowPlacement.cs, the file tools\godot.ps1 compiles): half the width and half
/// the height of a monitor's work area (a quarter of its area), in its bottom-right corner, on the monitor that holds the
/// foreground window.
/// </summary>
public class TestWindowPlacementTests
{
    private static PixelRect Area(int x, int y, int w, int h) => new(x, y, w, h);

    [Fact]
    public void The_window_is_half_the_monitor_each_way_which_is_a_quarter_of_its_area()
    {
        var area = Area(0, 0, 1920, 1040);
        var w = TestWindowPlacement.Place(new[] { area }, 0);
        Assert.Equal(960, w.Width);
        Assert.Equal(520, w.Height);
        Assert.Equal((long)area.Width * area.Height, 4L * w.Width * w.Height);
    }

    [Fact]
    public void It_scales_with_the_monitor_it_is_placed_on()
    {
        var small = Area(0, 0, 1366, 728);
        var large = Area(1366, 0, 3840, 2100);
        var onLarge = TestWindowPlacement.Place(new[] { small, large }, 1);
        Assert.Equal(1920, onLarge.Width);
        Assert.Equal(1050, onLarge.Height);
        Assert.True(large.Contains(onLarge));
        Assert.Equal(683, TestWindowPlacement.Place(new[] { small, large }, 0).Width);
    }

    [Fact]
    public void It_sits_in_the_bottom_right_corner_of_the_work_area_not_the_screen()
    {
        // 1080p with a 40 px taskbar along the bottom: the work area ends at 1040
        var area = Area(0, 0, 1920, 1040);
        var w = TestWindowPlacement.Place(new[] { area }, 0);
        Assert.Equal(1920, w.Right);
        Assert.Equal(1040, w.Bottom);
        Assert.True(area.Contains(w));
    }

    [Fact]
    public void A_work_area_that_does_not_start_at_the_origin_is_respected()
    {
        // taskbar on the left: the work area starts at x = 60
        var area = Area(60, 0, 1860, 1080);
        var w = TestWindowPlacement.Place(new[] { area }, 0);
        Assert.Equal(1920, w.Right);
        Assert.Equal(1080, w.Bottom);
        Assert.True(area.Contains(w));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    public void A_tiny_monitor_still_gets_a_window_of_at_least_one_pixel_inside_it(int width, int height)
    {
        var area = Area(0, 0, width, height);
        var w = TestWindowPlacement.Place(new[] { area }, 0);
        Assert.True(w.Width >= 1 && w.Height >= 1);
        Assert.True(area.Contains(w), $"{w} must be inside {area}");
        Assert.Equal(area.Right, w.Right);
        Assert.Equal(area.Bottom, w.Bottom);
    }

    [Fact]
    public void A_second_monitor_to_the_right_puts_the_window_in_that_monitors_corner()
    {
        var primary = Area(0, 0, 1920, 1040);
        var second = Area(1920, 0, 2560, 1400);
        var w = TestWindowPlacement.Place(new[] { primary, second }, 1);
        Assert.Equal(1920 + 2560, w.Right);
        Assert.Equal(1400, w.Bottom);
        Assert.True(second.Contains(w));
        Assert.False(primary.Contains(w));
    }

    [Fact]
    public void A_second_monitor_to_the_left_or_above_has_negative_coordinates_and_the_window_stays_on_the_chosen_one()
    {
        var primary = Area(0, 0, 1920, 1040);
        var left = Area(-1280, -200, 1280, 984);   // left of the primary and higher up
        var w = TestWindowPlacement.Place(new[] { primary, left }, 1);
        Assert.Equal(0, w.Right);
        Assert.Equal(784, w.Bottom);
        Assert.True(left.Contains(w));
        Assert.True(w.X < 0);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    public void A_monitor_index_outside_the_list_uses_the_first_monitor(int index)
    {
        var first = Area(0, 0, 1920, 1040);
        var second = Area(1920, 0, 1920, 1040);
        var w = TestWindowPlacement.Place(new[] { first, second }, index);
        Assert.True(first.Contains(w));
    }

    [Fact]
    public void Bad_input_is_refused_with_a_reason()
    {
        Assert.Throws<ArgumentException>(() => TestWindowPlacement.Place(Array.Empty<PixelRect>(), 0));
        Assert.Throws<ArgumentException>(() => TestWindowPlacement.Place(new[] { Area(0, 0, 0, 100) }, 0));
        Assert.Throws<ArgumentException>(() => TestWindowPlacement.MonitorOf(Array.Empty<PixelRect>(), Area(0, 0, 10, 10), 0));
    }

    // Which monitor: the one the person at the machine is using, which holds the foreground window.
    private static readonly PixelRect[] ThreeMonitors = { Area(0, 0, 3840, 2160), Area(-3840, 0, 3840, 2160), Area(3840, 360, 2560, 1440) };

    [Theory]
    [InlineData(100, 100, 800, 600, 0)]        // inside the primary
    [InlineData(-3000, 200, 800, 600, 1)]      // inside the left monitor
    [InlineData(4000, 500, 800, 600, 2)]       // inside the right one
    public void The_monitor_is_the_one_holding_the_foreground_window(int x, int y, int w, int h, int expected)
    {
        Assert.Equal(expected, TestWindowPlacement.MonitorOf(ThreeMonitors, Area(x, y, w, h), 0));
    }

    [Fact]
    public void A_foreground_window_across_two_monitors_goes_to_the_one_it_covers_more_of()
    {
        // 1000 wide, 700 px of it on the primary and 300 on the right monitor
        Assert.Equal(0, TestWindowPlacement.MonitorOf(ThreeMonitors, Area(3140, 500, 1000, 600), 2));
        // 300 px on the primary, 700 on the right monitor
        Assert.Equal(2, TestWindowPlacement.MonitorOf(ThreeMonitors, Area(3540, 500, 1000, 600), 0));
    }

    [Fact]
    public void A_maximised_window_with_a_border_off_screen_still_names_its_monitor()
    {
        // a maximised window extends a few pixels past every edge of the monitor it is on
        Assert.Equal(1, TestWindowPlacement.MonitorOf(ThreeMonitors, Area(-3848, -8, 3856, 2176), 0));
    }

    [Fact]
    public void No_foreground_window_or_one_nowhere_on_a_monitor_uses_the_fallback_monitor()
    {
        Assert.Equal(2, TestWindowPlacement.MonitorOf(ThreeMonitors, null!, 2));
        Assert.Equal(0, TestWindowPlacement.MonitorOf(ThreeMonitors, Area(0, 0, 0, 0), 0));
        Assert.Equal(1, TestWindowPlacement.MonitorOf(ThreeMonitors, Area(-32000, -32000, 160, 28), 1));   // a minimised window's place
        Assert.Equal(0, TestWindowPlacement.MonitorOf(ThreeMonitors, Area(0, 0, 10, 10), 99));            // a fallback outside the list
    }

    [Fact]
    public void The_window_placed_for_a_foreground_window_is_a_quarter_of_that_monitor_in_its_bottom_corner()
    {
        int monitor = TestWindowPlacement.MonitorOf(ThreeMonitors, Area(4100, 600, 900, 700), 0);
        var w = TestWindowPlacement.Place(ThreeMonitors, monitor);
        Assert.Equal(1280, w.Width);
        Assert.Equal(720, w.Height);
        Assert.Equal(6400, w.Right);
        Assert.Equal(1800, w.Bottom);
    }

    [Fact]
    public void The_engine_arguments_carry_the_size_and_a_position_counted_from_the_top_left_of_all_monitors()
    {
        // one monitor: the engine's position is the desktop position
        var only = new[] { Area(0, 0, 1920, 1080) };
        Assert.Equal("--resolution 576x324 --position 1344,716", TestWindowPlacement.EngineArguments(new PixelRect(1344, 716, 576, 324), only));

        // a second monitor to the left makes the desktop's x negative; the engine counts from the leftmost edge
        var both = new[] { Area(0, 0, 3840, 2160), Area(-3840, 0, 3840, 2160) };
        Assert.Equal("--resolution 576x324 --position 7104,1776", TestWindowPlacement.EngineArguments(new PixelRect(3264, 1776, 576, 324), both));
        Assert.Equal("--resolution 576x324 --position 3264,1776", TestWindowPlacement.EngineArguments(new PixelRect(-576, 1776, 576, 324), both));
    }

    [Fact]
    public void The_origin_is_the_top_left_of_the_monitors_together_including_one_above_the_primary()
    {
        var origin = TestWindowPlacement.Origin(new[] { Area(0, 0, 1920, 1080), Area(-1280, -300, 1280, 1024), Area(1920, 200, 1600, 900) });
        Assert.Equal(-1280, origin.X);
        Assert.Equal(-300, origin.Y);
        Assert.Throws<ArgumentException>(() => TestWindowPlacement.Origin(Array.Empty<PixelRect>()));
    }

    [Fact]
    public void The_base_size_is_the_window_a_plain_start_opens()
    {
        // the project sets no window size, so the engine's default (1152x648) is what a normal start shows; test runs draw at it
        string project = File.ReadAllText(Path.Combine(RepoRoot(), "host", "Viewer.Godot", "project.godot"));
        Assert.DoesNotContain("window/size/viewport_width", project);
        Assert.DoesNotContain("window/size/window_width_override", project);
        Assert.Equal(1152, TestWindowPlacement.BaseWidth);
        Assert.Equal(648, TestWindowPlacement.BaseHeight);
    }

    [Fact]
    public void The_launcher_decides_the_window_through_KindFor_MonitorOf_and_Place_and_keeps_the_switches()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "godot.ps1"));
        Assert.Contains("[switch]$FullSizeWindow", script);
        Assert.Contains("[switch]$MeasureRun", script);
        Assert.Contains("NV_TEST_WINDOW_FULL", script);
        Assert.Contains("[switch]$PlaceWindow", script);   // a plain start the harness places like a test window (tools/login-proof.ps1)
        Assert.Contains("[NvTools.TestWindowPlacement]::KindFor($(if ($PlaceWindow) { \"$ArgLine --s1-window\" } else { $ArgLine }), [bool]$fullWindow, [bool]$exportedBuild, [bool]$onScreen)", script);
        Assert.Contains("[NvTools.TestWindowPlacement]::MonitorOf($bounds,", script);
        Assert.Contains("[NvTools.TestWindowPlacement]::Place($areas, $monitor)", script);
        Assert.Contains("GetForegroundWindow", script);
        // the run's own arguments are what the script tests, and --headless is checked before the placement code is reached
        Assert.Contains("--test-render|--s1-", script);
        Assert.Contains("-notmatch '--headless'", script);
    }

    [Fact]
    public void The_launcher_sends_the_runs_window_to_the_bottom_without_activating_it_and_touches_only_its_own_processes()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "godot.ps1"));
        Assert.Contains("[NvZOrder]::SendToBottom($ids.ToArray())", script);
        Assert.Contains("(IntPtr)1", script);                       // HWND_BOTTOM
        Assert.Contains("SwpNoActivate = 0x10", script);
        Assert.DoesNotContain("SetForegroundWindow", script);       // nothing in the launcher brings a window to the front
        Assert.DoesNotContain("SW_RESTORE", script);
        // the process ids are the run's own tree: the process it started and what descends from it
        Assert.Contains("$ids.Add($p.Id)", script);
        Assert.Contains("[int]$c.ParentProcessId -eq $ids[$i]", script);
    }

    [Fact]
    public void The_viewer_keeps_its_test_window_at_the_bottom_and_restores_a_minimised_one_without_activating_it()
    {
        string code = File.ReadAllText(Path.Combine(RepoRoot(), "host", "Viewer.Godot", "TestWindow.cs"));
        Assert.Contains("SetWindowPos", code);
        Assert.Contains("HwndBottom", code);
        Assert.Contains("SwpNoActivate", code);
        Assert.Contains("SwMostRecentNoActivate = 4", code);   // SW_SHOWNOACTIVATE: restores a minimised window, does not activate it
        Assert.Contains("ShowWindow(h, SwMostRecentNoActivate)", code);
        Assert.DoesNotContain("SetForegroundWindow", code);
        Assert.DoesNotContain("SwRestore", code);   // SW_RESTORE activates the window
        Assert.DoesNotContain("WindowSetMode(DisplayServer.WindowMode.Windowed", code);   // the engine's restore activates it too
    }

    // Which runs get a placed window: the table of KindFor.
    [Theory]
    [InlineData("--rendering-driver vulkan -- --test-render --tr-tag=golden", false, TestWindowKind.OffScreen)]            // a render check
    [InlineData("--rendering-driver vulkan -- --s1-smoke", false, TestWindowKind.OffScreen)]                              // an S-1 run
    [InlineData("--rendering-driver d3d12 -- --test-render --tr-path-seconds=30 --tr-return", false, TestWindowKind.OffScreen)]   // a measuring run: the same window
    [InlineData("--rendering-driver vulkan -- --test-render --tr-tag=golden", true, TestWindowKind.Untouched)]          // the full-size switch: the usual window
    [InlineData("--headless --editor --path X --build-solutions --quit", false, TestWindowKind.Untouched)]              // no window is opened
    [InlineData("--headless -- --test-render --tr-tag=x", false, TestWindowKind.Untouched)]
    [InlineData("--rendering-driver vulkan", false, TestWindowKind.Untouched)]                                          // a normally started viewer
    [InlineData("", false, TestWindowKind.Untouched)]
    public void Each_kind_of_run_gets_its_own_window(string argLine, bool fullSize, TestWindowKind expected)
    {
        Assert.Equal(expected, TestWindowPlacement.KindFor(argLine, fullSize));
    }

    // Every window a check opens is off every monitor: test runs, and starts of the exported viewer that open a window
    // (the automation runs of the live checks and the stock smoke). The corner window is kept only when asked for.
    [Theory]
    [InlineData("--rendering-driver vulkan -- --test-render --tr-tag=golden", false, false, false, TestWindowKind.OffScreen)]
    [InlineData("--rendering-driver d3d12 -- --s1-smoke", false, true, false, TestWindowKind.OffScreen)]
    [InlineData("--rendering-driver vulkan -- --automation", false, true, false, TestWindowKind.OffScreen)]        // a live check's or the stock smoke's viewer
    [InlineData("--rendering-driver vulkan -- --automation", false, false, false, TestWindowKind.Untouched)]       // the editor's own program: not a viewer window
    [InlineData("--rendering-driver vulkan -- --test-render --tr-tag=golden", false, true, true, TestWindowKind.Corner)]   // NV_TEST_WINDOW_ONSCREEN=1
    [InlineData("--rendering-driver vulkan -- --automation", false, true, true, TestWindowKind.Corner)]
    [InlineData("--rendering-driver vulkan -- --automation", true, true, false, TestWindowKind.Untouched)]        // the full-size switch
    [InlineData("--headless -- --s1-smoke", false, true, false, TestWindowKind.Untouched)]                        // no window is opened
    [InlineData("--headless --editor --path X --build-solutions --quit", false, false, false, TestWindowKind.Untouched)]
    public void Every_window_a_check_opens_is_off_screen_unless_asked_otherwise(string argLine, bool fullSize, bool exportedViewer, bool onScreen, TestWindowKind expected)
    {
        Assert.Equal(expected, TestWindowPlacement.KindFor(argLine, fullSize, exportedViewer, onScreen));
    }

    public static IEnumerable<object[]> Layouts()
    {
        yield return new object[] { new[] { Area(0, 0, 1920, 1080) } };
        yield return new object[] { new[] { Area(0, 0, 3840, 2160), Area(-3840, 0, 3840, 2160), Area(3840, 360, 2560, 1440) } };
        yield return new object[] { new[] { Area(0, 0, 1920, 1080), Area(-1280, -300, 1280, 1024), Area(1920, 200, 1600, 900) } };
        yield return new object[] { new[] { Area(0, 0, 2560, 1440), Area(0, -1080, 1920, 1080) } };   // one above the primary
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void The_off_screen_window_has_the_drawing_size_and_meets_no_monitor(PixelRect[] screens)
    {
        var w = TestWindowPlacement.OffScreen(screens, TestWindowPlacement.BaseWidth, TestWindowPlacement.BaseHeight);
        Assert.Equal(1152, w.Width);
        Assert.Equal(648, w.Height);
        foreach (var s in screens) Assert.Equal(0L, s.OverlapArea(w));
        int rightmost = screens.Max(s => s.Right);
        Assert.True(w.X >= rightmost, $"{w} starts at or beyond the right edge of every monitor ({rightmost})");
    }

    [Fact]
    public void The_off_screen_window_is_given_to_the_engine_counted_from_the_monitors_origin()
    {
        var screens = new[] { Area(0, 0, 3840, 2160), Area(-3840, 0, 3840, 2160) };
        var w = TestWindowPlacement.OffScreen(screens, 1152, 648);
        Assert.Matches(@"^--resolution 1152x648 --position \d+,\d+$", TestWindowPlacement.EngineArguments(w, screens));
        Assert.True(w.X - TestWindowPlacement.Origin(screens).X >= 7680);
    }

    [Fact]
    public void The_launcher_opens_the_window_off_screen_and_moves_it_off_any_monitor_the_engine_put_it_on()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "godot.ps1"));
        Assert.Contains("[NvTools.TestWindowPlacement]::OffScreen($bounds, [NvTools.TestWindowPlacement]::BaseWidth, [NvTools.TestWindowPlacement]::BaseHeight)", script);
        Assert.Contains("NV_TEST_WINDOW_ONSCREEN", script);
        // the engine keeps a new window partly on a monitor whatever position it is given, so the launcher moves it off once it is up
        Assert.Contains("[NvZOrder]::MoveOff($ids.ToArray(), $offX, $offY", script);
        Assert.Contains("SwpNoActivate = 0x10", script);
    }

    [Fact]
    public void A_missing_argument_line_is_an_unplaced_window()
    {
        Assert.Equal(TestWindowKind.Untouched, TestWindowPlacement.KindFor(null!, false));
    }

    [Fact]
    public void The_measurement_sections_of_render_ps1_pass_MeasureRun_and_the_checks_do_not()
    {
        string render = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "render.ps1"));
        Assert.Contains("$script:Measuring = $false   # true while", render);
        Assert.Contains("if ($script:Measuring) { @(\"-MeasureRun\") }", render);
        // the test cases end where the measurement sections begin; the hang check is a test and sits outside them
        int testsEnd = render.IndexOf("end of the test cases skipped by -MeasureOnly", StringComparison.Ordinal);
        int on = render.IndexOf("$script:Measuring = $true", StringComparison.Ordinal);
        int hangOff = render.IndexOf("$script:Measuring = $false   # the hang check is a test", StringComparison.Ordinal);
        int hang = render.IndexOf("if ($HangCheck)", StringComparison.Ordinal);
        int measureOn = render.IndexOf("if ($Measure)", StringComparison.Ordinal);
        Assert.True(testsEnd > 0 && on > testsEnd, "measuring starts after the test cases");
        Assert.True(hangOff > on && hangOff < hang, "the hang check is switched back to a test run");
        Assert.True(render.IndexOf("$script:Measuring = $true", hangOff, StringComparison.Ordinal) is var again && again > hang && again < measureOn,
            "the measurement section after the hang check is marked again");
        // none of the measurement sections is above the switch-on line
        foreach (string section in new[] { "if ($Residency)", "if ($Resolutions)", "if ($Memory)", "if ($Budget)", "if ($TextureServer)", "if ($UploadBench)" })
            Assert.True(render.IndexOf(section, StringComparison.Ordinal) > on, section + " runs as a measurement");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Viewer.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
