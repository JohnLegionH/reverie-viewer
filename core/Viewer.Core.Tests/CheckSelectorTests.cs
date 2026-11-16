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

    // The stock smoke runs for the session, the protocol, inventory, the map, login and the automation, and for code that sends or
    // reads grid messages (object and build updates included); not for every placed file.
    [Theory]
    [InlineData("core/Viewer.Core/Protocol/Messages.cs")]
    [InlineData("core/Viewer.Core/Session/SessionState.cs")]
    [InlineData("core/Viewer.Core/Inventory/InventoryStore.cs")]
    [InlineData("core/Viewer.Core/Map/MapBlocks.cs")]
    [InlineData("host/Viewer.Godot/LoginScreen.cs")]
    [InlineData("host/Viewer.Shell/Grids/GridList.cs")]
    [InlineData("host/Viewer.Automation/Commands.cs")]
    [InlineData("host/Viewer.Godot/Automation/EditCommand.cs")]
    [InlineData("host/Viewer.Shell/Build/EditController.cs")]
    [InlineData("host/Viewer.Shell/Build/EditController.Faces.cs")]
    [InlineData("core/Viewer.Core/Building/TextureUpload.cs")]
    [InlineData("core/Viewer.Core/Appearance/WearableKinds.cs")]
    public void The_areas_v10_names_and_grid_message_code_run_the_stock_smoke(string file)
    {
        var s = Select(file);
        Assert.Empty(s.Unplaced);
        Assert.True(s.Smoke, file);
    }

    [Theory]
    [InlineData("host/Viewer.Godot/Reverie/TopBar.cs")]
    [InlineData("host/Viewer.Shell/TerrainPatch.cs")]
    [InlineData("core/Viewer.Core/Meshing/Extrude.cs")]
    [InlineData("host/Viewer.Shell/Build/Manipulator.cs")]
    [InlineData("host/Viewer.Godot/Main.Handles.cs")]
    [InlineData("fixtures/environment/golden-shared-environment-dusk-vulkan.png")]
    [InlineData("fixtures/avatars/manifest.json")]
    public void Other_placed_files_do_not_run_the_stock_smoke(string file)
    {
        var s = Select(file);
        Assert.Empty(s.Unplaced);
        Assert.DoesNotContain("none", s.FileAreas[file]);
        Assert.NotNull(s.Cases);   // they still select their own render cases
        Assert.False(s.Smoke, file);
    }

    // The grid manager is part of login: its list, look-up and saved names, and its panels on the login screen.
    [Theory]
    [InlineData("host/Viewer.Shell/Grids/GridList.cs")]
    [InlineData("host/Viewer.Shell/Grids/SavedNames.cs")]
    [InlineData("host/Viewer.Godot/LoginGridPanels.cs")]
    public void The_grid_manager_files_are_login_files(string file)
    {
        var s = Select(file);
        Assert.Empty(s.Unplaced);
        Assert.False(s.Full);
        Assert.Equal(["login"], s.FileAreas[file]);
    }

    [Fact]
    public void A_none_file_beside_a_smoke_file_still_runs_the_smoke_and_none_alone_does_not()
    {
        Assert.True(Select("README.md", "core/Viewer.Core/Protocol/Messages.cs").Smoke);
        Assert.False(Select("README.md", "Docs/ARCHITECTURE.md").Smoke);
    }

    // Main.cs alone selects one render case per area, on Vulkan, instead of the full set; the next gate order runs the full set.
    [Fact]
    public void Main_cs_alone_selects_one_render_case_per_area_on_vulkan()
    {
        var s = Select("host/Viewer.Godot/Main.cs");
        Assert.False(s.Full);
        Assert.Empty(s.Unplaced);
        foreach (string c in new[] { "loginscreen_vulkan", "select_vulkan", "clouds_midday_vulkan", "avatars_attach_vulkan", "golden_vulkan", "mesh_near_vulkan", "ui_inworld_classic_vulkan" })
            Assert.True(CheckSelector.Picks(s, c), c);
        Assert.True(CheckSelector.Picks(s, "proposal-golden-vulkan"));
        foreach (string c in new[] { "golden_far_vulkan", "sky_midday_vulkan", "mesh_far_vulkan", "avatars_baked_vulkan", "loginscreen_d3d12", "select_d3d12", "ui_login_classic_vulkan" })
            Assert.False(CheckSelector.Picks(s, c), c);
        Assert.Equal(new[] { "vulkan" }, s.CasesByBackend.Keys.ToArray());
        Assert.Single(CheckSelector.RenderLines(s));
    }

    [Theory]
    [InlineData("host/Viewer.Godot/TestRender.cs")]
    [InlineData("tools/render.ps1")]
    [InlineData("host/Viewer.Godot/Viewer.Godot.csproj")]
    [InlineData("core/Viewer.Core/World/Scene.cs")]
    public void Main_cs_beside_a_full_set_file_still_selects_the_full_set(string other)
    {
        var s = Select("host/Viewer.Godot/Main.cs", other);
        Assert.True(s.Full, other);
        Assert.True(s.Smoke);
    }

    // The lines the selector prints are run as printed: render.ps1 renders nothing without a section, so a narrow
    // selection is the full set filtered by its cases (-All -OnlyCases), one backend per line.
    [Fact]
    public void The_render_lines_name_a_section_so_they_run_as_printed()
    {
        var narrow = Select("host/Viewer.Godot/Reverie/TopBar.cs");
        var lines = CheckSelector.RenderLines(narrow);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Matches(@"^render\.ps1 -All -OnlyCases '[^']+' -Backends (vulkan|d3d12)$", l));
        Assert.Contains(lines, l => l.EndsWith("-Backends vulkan"));
        Assert.Contains(lines, l => l.EndsWith("-Backends d3d12"));
        Assert.Equal(new[] { "render.ps1 -All" }, CheckSelector.RenderLines(Select("tools/render.ps1")));
        Assert.Equal(new[] { "none" }, CheckSelector.RenderLines(Select("README.md")));
    }

    [Fact]
    public void The_script_prints_the_selectors_own_render_lines()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "select-checks.ps1"));
        Assert.Contains("[NvTools.CheckSelector]::RenderLines($s)", script);
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
    [InlineData("host/Viewer.Godot/TestRender.cs")]
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
        Assert.False(s.Smoke);   // the interface sends and reads no grid messages
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
    public void An_environment_fixture_selects_the_sky_cases_on_vulkan_without_the_stock_smoke()
    {
        var s = Select("fixtures/environment/golden-shared-environment-dusk-vulkan.png");
        Assert.False(s.Full);
        Assert.False(s.Smoke);
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
        Assert.False(s.Smoke);   // neither file sends or reads grid messages
        Assert.Equal(new[] { "ui" }, s.FileAreas["host/Viewer.Godot/Reverie/Dock.cs"]);
        Assert.True(CheckSelector.Picks(s, "avatars_baked_vulkan"));
        Assert.True(CheckSelector.Picks(s, "ui_people_classic_d3d12"));
        Assert.False(CheckSelector.Picks(s, "avatars_baked_d3d12"));   // avatar cases run on Vulkan only
    }

    [Theory]
    [InlineData("shared-env-vulkan")]
    [InlineData("proposal-shared-env-vulkan")]
    [InlineData("sky-midday-vulkan")]
    [InlineData("glow-light-sun-vulkan")]
    public void The_cases_are_found_by_their_picture_tags_too_which_render_ps1_filters_on(string tag)
    {
        // render.ps1 -OnlyCases matches the picture's tag (hyphens, and proposal- before a golden exists), not the case's name
        var s = Select("fixtures/environment/shared-dusk-thick.llsd.xml");
        Assert.True(CheckSelector.Picks(s, tag), tag);
        Assert.Matches(s.CasesByBackend["vulkan"], tag);
        Assert.False(CheckSelector.Picks(s, "ui-inworld-classic-vulkan"));
        Assert.True(CheckSelector.Picks(Select("host/Viewer.Godot/Reverie/TopBar.cs"), "uichrome-100-1152x648-d3d12"));
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
