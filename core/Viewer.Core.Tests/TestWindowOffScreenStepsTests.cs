// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Viewer.Core.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>No render run may put its window on a monitor: the harness never maximizes or fills a screen, and no render
/// section asks the launcher for the usual on-monitor window.</summary>
public class TestWindowOffScreenStepsTests
{
    [Fact]
    public void Maximized_stands_in_as_the_work_area_less_the_title_bar() =>
        Assert.Equal((3840, 2066), ScreenStandIn.Size("maximized", (3840, 2160), (3840, 2100), 34));

    [Fact]
    public void Full_screen_stands_in_as_the_whole_monitor() =>
        Assert.Equal((3840, 2160), ScreenStandIn.Size("fullscreen", (3840, 2160), (3840, 2100), 34));

    [Fact]
    public void The_render_harness_never_maximizes_or_fills_a_screen()
    {
        var offenders = Directory.GetFiles(Path.Combine(RepoRoot(), "host", "Viewer.Godot"), "TestRender*.cs")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\.Mode\s*=\s*Window\.ModeEnum\.(Maximized|Fullscreen|ExclusiveFullscreen)|WindowSetMode\(\s*DisplayServer\.WindowMode\.(Maximized|Fullscreen|ExclusiveFullscreen)"))
            .Select(Path.GetFileName);
        Assert.Empty(offenders);
    }

    [Fact]
    public void No_render_section_asks_for_the_on_monitor_window()
    {
        string render = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "render.ps1"));
        Assert.DoesNotMatch(@"\$env:NV_TEST_WINDOW_FULL\s*=\s*""1""", render);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Viewer.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new System.InvalidOperationException("repo root not found");
    }
}
