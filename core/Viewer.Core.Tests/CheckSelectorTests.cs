// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using NvTools;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The case selector (tools\CheckSelector.cs with the committed tools\check-map.tsv and tools\check-areas.tsv): from the files an
/// order changed, the render cases to run, on which backends, and whether the stock smoke runs. A file it cannot place selects
/// the full set.
/// </summary>
public class CheckSelectorTests
{
    private static string Map => File.ReadAllText(Path.Combine(RepoRoot(), "tools", "check-map.tsv"));
    private static string Areas => File.ReadAllText(Path.Combine(RepoRoot(), "tools", "check-areas.tsv"));
    private static CheckSelection Select(params string[] files) => CheckSelector.Select(files, Map, Areas);

    [Fact]
    public void Docs_and_tests_alone_select_no_render_case_and_no_smoke()
    {
        var s = Select("Docs/ARCHITECTURE.md", "core/Viewer.Core.Tests/LoginTests.cs", "README.md", "host/Viewer.Godot/Main.cs.uid");
        Assert.False(s.Full);
        Assert.False(s.Smoke);
        Assert.Null(s.Cases);
        Assert.Empty(s.Unplaced);
        Assert.Equal(4, s.FileAreas.Count);
        Assert.False(CheckSelector.Picks(s, "ui_build_classic_vulkan"));
    }

    [Fact]
    public void A_file_it_cannot_place_selects_the_full_set_and_is_named()
    {
        var s = Select("host/Viewer.Godot/SomethingNew.cs");
        Assert.True(s.Full);
        Assert.True(s.Smoke);
        Assert.Equal(new[] { "host/Viewer.Godot/SomethingNew.cs" }, s.Unplaced);
        Assert.True(CheckSelector.Picks(s, "shared_env_vulkan"));
        Assert.True(CheckSelector.Picks(s, "ui_login_classic_d3d12"));
    }

    [Theory]
    [InlineData("host/Viewer.Godot/Shaders/face.gdshader")]
    [InlineData("tools/render.ps1")]
    [InlineData("tools/godot.ps1")]
    [InlineData("host/Viewer.Godot/Viewer.Godot.csproj")]
    [InlineData("host/Viewer.Godot/Presentation/WorldPresenter.cs")]
    [InlineData("core/Viewer.Core/World/Scene.cs")]
    public void Shared_drawing_code_and_the_check_scripts_select_the_full_set(string file)
    {
        var s = Select(file);
        Assert.True(s.Full, file);
        Assert.Empty(s.Unplaced);
    }

    [Fact]
    public void The_top_bar_selects_every_interface_case_on_both_backends_and_nothing_else()
    {
        var s = Select("host/Viewer.Godot/Reverie/TopBar.cs");
        Assert.False(s.Full);
        Assert.False(s.Smoke);
        Assert.True(CheckSelector.Picks(s, "ui_reverie-topbar-100_reverie-dark_vulkan"));
        Assert.True(CheckSelector.Picks(s, "ui_reverie-topbar-100_reverie-dark_d3d12"));
        Assert.True(CheckSelector.Picks(s, "loginscreen_d3d12"));
        Assert.False(CheckSelector.Picks(s, "handles_move_vulkan"));
        Assert.False(CheckSelector.Picks(s, "sky_midday_vulkan"));
    }

    [Fact]
    public void The_handles_select_the_handle_and_build_window_cases()
    {
        var s = Select("host/Viewer.Godot/Main.Handles.cs", "host/Viewer.Shell/Build/Manipulator.cs");
        Assert.False(s.Full);
        Assert.True(CheckSelector.Picks(s, "handles_grid_vulkan"));
        Assert.True(CheckSelector.Picks(s, "ui_build_classic_d3d12"));
        Assert.False(CheckSelector.Picks(s, "avatars_vulkan"));
    }

    [Fact]
    public void An_environment_fixture_selects_the_sky_cases_and_the_stock_smoke_on_vulkan()
    {
        var s = Select("fixtures/environment/golden-shared-environment-dusk-vulkan.png");
        Assert.False(s.Full);
        Assert.True(s.Smoke);
        Assert.True(CheckSelector.Picks(s, "shared_env_vulkan"));
        Assert.True(CheckSelector.Picks(s, "clouds_midday_vulkan"));
        Assert.False(CheckSelector.Picks(s, "ui_inworld_classic_vulkan"));
    }

    [Fact]
    public void The_protocol_selects_the_stock_smoke_and_the_login_pictures()
    {
        var s = Select("core/Viewer.Core/Protocol/Messages.cs");
        Assert.False(s.Full);
        Assert.True(s.Smoke);
        Assert.True(CheckSelector.Picks(s, "loginscreen_vulkan"));
        Assert.False(CheckSelector.Picks(s, "water_vulkan"));
    }

    [Fact]
    public void Areas_add_up_and_each_file_lists_its_areas()
    {
        var s = Select("host/Viewer.Godot/Reverie/Dock.cs", "fixtures/avatars/manifest.json");
        Assert.Contains("ui", s.Areas);
        Assert.Contains("avatars", s.Areas);
        Assert.True(s.Smoke);   // avatars run the smoke
        Assert.Equal(new[] { "ui" }, s.FileAreas["host/Viewer.Godot/Reverie/Dock.cs"]);
        Assert.True(CheckSelector.Picks(s, "avatars_baked_vulkan"));
        Assert.True(CheckSelector.Picks(s, "ui_people_classic_d3d12"));
        Assert.False(CheckSelector.Picks(s, "avatars_baked_d3d12"));   // avatar cases run on Vulkan only
    }

    [Fact]
    public void Every_area_the_map_names_exists_and_every_pattern_compiles()
    {
        var names = new HashSet<string>(Areas.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.StartsWith("#")).Select(l => l.Split('\t')[0]));
        Assert.Contains("full", names);
        Assert.Contains("none", names);
        foreach (string line in Map.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.StartsWith("#")))
        {
            var cols = line.Split('\t');
            Assert.Equal(2, cols.Length);
            _ = new Regex(cols[0]);
            foreach (string a in cols[1].Split(',')) Assert.True(names.Contains(a), $"area '{a}' in '{line}'");
        }
        // the selection is not empty for a placed file, so the areas file is really read
        Assert.Contains("build", Select("fixtures/build/golden-handles-move-vulkan.png").Areas);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Viewer.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
